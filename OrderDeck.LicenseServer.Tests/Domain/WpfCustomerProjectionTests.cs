using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class WpfCustomerProjectionTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wpfproj-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_nullable_identity_fields()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = id,
            LicenseId = licenseId,
            Platform = "tiktok",
            Username = "@tt_user",
            FullName = null,
            Phone = null,
            Address = null,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.WpfCustomerProjections.SingleAsync(c => c.Id == id);
        loaded.Platform.Should().Be("tiktok");
        loaded.FullName.Should().BeNull();
    }

    [Fact]
    public void LicenseId_Platform_Username_combo_indexed_for_match()
    {
        using var db = NewDb();
        var entityType = db.Model.FindEntityType(typeof(WpfCustomerProjection))!;
        entityType.GetIndexes().Should().Contain(i =>
            i.Properties.Count == 3
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.LicenseId))
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.Platform))
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.Username)),
            "sipariş eşleşmesi için (LicenseId, Platform, Username) sorgulanacak");
    }

    [Fact]
    public void IdentityKey_Username_setter_ile_otomatik_turetilir()
    {
        var p = new WpfCustomerProjection { Username = "  Ayse.Kaya " };
        p.IdentityKey.Should().Be("ayse.kaya");
    }

    [Theory]
    // Kırpma + küçük harf (temel durum).
    [InlineData("  Ayse.Kaya ", "ayse.kaya")]
    // Türkçe büyük 'İ' (U+0130): .NET ToLowerInvariant bunu DEĞİŞTİRMEZ,
    // Replace('İ','i') ile SQL LOWER'ın verdiği sonuca (her collation'da 'i')
    // eşitleniyor — asıl prod bulgusu bu.
    [InlineData("MELİKE", "melike")]
    // ASCII 'I' invariant kültürde düz 'i'ye döner (tr-TR'nin 'ı'sına DEĞİL) —
    // bu metot bilerek invariant, kültüre bağlı belirsizlik istemiyor.
    [InlineData("ISIK", "isik")]
    // Zaten küçük harf noktasız 'ı' (U+0131) DEĞİŞMEDEN kalır — yalnız İ
    // düzeltiliyor, dotless ı'ya dokunulmuyor.
    [InlineData("ışık", "ışık")]
    // 'Ş' invariant altında da doğru küçülüyor, özel işlem gerekmiyor.
    [InlineData("ŞULE", "şule")]
    public void IdentityKeyOf_kirpar_kucultur_ve_Turkce_I_noktasini_esitler(string input, string expected)
        => WpfCustomerProjection.IdentityKeyOf(input).Should().Be(expected);

    [Fact]
    public void ScrubPersonal_kisisel_alanlari_bosaltir_is_notunu_ve_kara_listeyi_birakir()
    {
        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var blacklistedAt = DateTimeOffset.UtcNow.AddDays(-3);
        var p = new WpfCustomerProjection
        {
            Platform = "tiktok", Username = "ayse",
            FullName = "Ayşe", DisplayName = "ayşe🌸", Phone = phone,
            Address = "Adres", City = "İzmir", District = "Bornova",
            Email = "a@example.test", TcknProtected = "x", Notes = "not",
            WhatsAppConsent = true, SmsConsent = true,
            IsBlacklisted = true, BlacklistReason = "sebep", BlacklistedAt = blacklistedAt,
        };

        p.ScrubPersonal();

        p.FullName.Should().BeNull(); p.DisplayName.Should().BeNull();
        p.Phone.Should().BeNull(); p.Address.Should().BeNull();
        p.City.Should().BeNull(); p.District.Should().BeNull();
        p.Email.Should().BeNull(); p.TcknProtected.Should().BeNull();
        p.WhatsAppConsent.Should().BeFalse(); p.SmsConsent.Should().BeFalse();

        // KVKK politikası (CustomerRepository.ScrubAssignments'in belgelediği
        // aynı karar): işletme notu ve sahtekârlık koruması BİLEREK kalır,
        // tombstone ScrubPersonal'ın işi değil (bkz. MarkPurged).
        p.Notes.Should().Be("not");
        p.IsBlacklisted.Should().BeTrue();
        p.BlacklistReason.Should().Be("sebep");
        p.BlacklistedAt.Should().Be(blacklistedAt);
        p.PurgedAt.Should().BeNull();
        p.Username.Should().Be("ayse");
    }

    [Fact]
    public void MarkPurged_damgayi_vurur_ikinci_cagri_ilk_PurgedAt_tarihini_korur()
    {
        var first = DateTimeOffset.UtcNow;
        var p = new WpfCustomerProjection
        {
            Platform = "tiktok", Username = "ayse", FullName = "Ayşe",
        };

        p.MarkPurged(first);
        p.PurgedAt.Should().Be(first);
        p.UpdatedAt.Should().Be(first);
        p.FullName.Should().BeNull("MarkPurged ScrubPersonal'ı da çağırır");

        var second = first.AddMinutes(5);
        p.MarkPurged(second);
        p.PurgedAt.Should().Be(first, "tarih adli kayıt — tekrarlanan silme talebi İLK tarihi korur");
        p.UpdatedAt.Should().Be(second, "UpdatedAt PurgedAt'tan farklı olarak her çağrıda ilerler");
    }
}
