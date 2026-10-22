using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

/// <summary>KVKK K-3: yayıncının veri sorumlusu bilgileri — zorunlu alanlar, e-posta biçimi, kırpma; içerik değişince yeni
/// sürüm (m. 10/1-a ispatı); silinmiş yayıncıya yazılmaz. Eşzamanlı yazım gerçek SQL Server'da:
/// <see cref="DataControllerProfilesRelationalTests"/>.</summary>
public sealed class DataControllerProfilesTests
{
    [Fact]
    public async Task Icerik_degisince_yeni_surum_yazilir_ayni_icerik_surum_artirmaz()
    {
        // KVKK m. 10/1-a ispat yükü: formda gösterilen kimlik sonradan ispatlanabilsin (IntakeFormSubmission.DataControllerVersion).
        using var db = NewDb();
        var customerId = SeedCustomer(db);
        DataControllerProfiles.Validate(Valid(), out var first);
        DataControllerProfiles.Validate(Valid() with { PostalAddress = "Yeni Mah. 3. Sok. No:4 İstanbul" }, out var moved);

        (await DataControllerProfiles.UpsertAsync(db, customerId, first, "panel", CancellationToken.None)).Version.Should().Be(1);
        (await DataControllerProfiles.UpsertAsync(db, customerId, first, "panel", CancellationToken.None)).Version.Should().Be(1);
        (await DataControllerProfiles.UpsertAsync(db, customerId, moved, "admin:yonetici-1", CancellationToken.None))
            .Version.Should().Be(2);

        var versions = await db.DataControllerProfileVersions.AsNoTracking().OrderBy(v => v.Version).ToListAsync();
        versions.Select(v => (v.Version, v.PostalAddress, v.CreatedBy)).Should().Equal(
            (1, first.PostalAddress!, "panel"), (2, "Yeni Mah. 3. Sok. No:4 İstanbul", "admin:yonetici-1"));
    }

    internal static DataControllerInput Valid() => new("Örnek Ticaret Ltd. Şti.", "Örnek Mah. 1. Sok. No:2 Kadıköy/İstanbul",
        "kvkk@example.test", null);

    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"dc-{Guid.NewGuid():N}").Options);

    private static Guid SeedCustomer(LicenseDbContext db, bool purged = false)
    {
        var id = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = id, Email = $"dc-{Guid.NewGuid():N}@example.test", Name = "Dc",
            PasswordHash = purged ? Customer.PurgedSentinel : $"h-{Guid.NewGuid():N}", CreatedAt = DateTimeOffset.UtcNow });
        db.SaveChanges();
        db.ChangeTracker.Clear();
        return id;
    }

    [Fact]
    public async Task Silinmis_yayicida_bilgi_yazilmaz()
    {
        // KVKK silmesi müşteri satırını anonimleştirip bırakır (FK geçerli kalır): yazım ona yeni kişisel veri bırakmamalı.
        using var db = NewDb();
        var customerId = SeedCustomer(db, purged: true);
        DataControllerProfiles.Validate(Valid(), out var valid);

        var act = () => DataControllerProfiles.UpsertAsync(db, customerId, valid, "panel", CancellationToken.None);

        await act.Should().ThrowAsync<DataControllerCustomerPurgedException>().WithMessage(DataControllerProfiles.PurgedMessage);
        (await db.DataControllerProfiles.AnyAsync()).Should().BeFalse();
        (await db.DataControllerProfileVersions.AnyAsync()).Should().BeFalse();
    }

    [Fact]
    public void Gecerli_girdi_kirpilarak_kabul_edilir_bos_KEP_null_olur()
    {
        var error = DataControllerProfiles.Validate(
            new DataControllerInput("  Örnek Ticaret  ", " Adres 1 ", " kvkk@example.test ", "   "), out var valid);

        error.Should().BeNull();
        valid.Should().Be(new DataControllerInput("Örnek Ticaret", "Adres 1", "kvkk@example.test", null));
    }

    [Fact]
    public void Eksik_ya_da_bozuk_alan_reddedilir()
    {
        DataControllerProfiles.Validate(Valid() with { LegalName = " " }, out _).Should().Contain("unvan");
        DataControllerProfiles.Validate(Valid() with { LegalName = new string('a', DataControllerProfiles.LegalNameMax + 1) }, out _)
            .Should().Contain("unvan");
        DataControllerProfiles.Validate(Valid() with { PostalAddress = "" }, out _).Should().Contain("adres");
        DataControllerProfiles.Validate(Valid() with { KvkkEmail = "kvkk" }, out _).Should().Contain("e-posta");
        DataControllerProfiles.Validate(Valid() with { KvkkEmail = "Ad <kvkk@example.test>" }, out _).Should().Contain("e-posta");
        DataControllerProfiles.Validate(Valid() with { KepAddress = "kep" }, out _).Should().Contain("KEP");
        DataControllerProfiles.Validate(Valid(), out _).Should().BeNull();
    }
}
