using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.CustomerSync;
using OrderDeck.LicenseServer.Services.ShopperLinking;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperLinking;

/// <summary>
/// ShopperCustomerResolver (S2b, tasarım §2.5): bağlantıdan müşteriye giden dört yolun (bağdaştırma, shopper silmesi, gönderim,
/// yeniden yazım) ortak çözücüsü; bakiye ucu onu kullanmaz (ayrı iş). Birleştirilmiş geçici kopyadan geçen bağlantı yalnız
/// telefon kanıtıyla asıl kayda çözülür, değilse müşteri bilinmez. InMemory yeterli: yalnız okur. Değerler üretilir (telefon
/// <see cref="TestPhone"/>, Id'ler Guid.NewGuid()).
/// </summary>
public sealed class ShopperCustomerResolverTests
{
    public enum Proof { Matches, OtherPhone, NotVerified, CanonicalProvisional }

    private static LicenseDbContext NewDb() => new(new DbContextOptionsBuilder<LicenseDbContext>()
        .UseInMemoryDatabase($"shopper-customer-{Guid.NewGuid():N}").Options);

    private static WpfCustomerProjection Row(
        LicenseDbContext db, Guid licenseId, Guid? mergedInto = null, string? phone = null, bool provisional = false)
    {
        var row = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Platform = "tiktok",
            Username = $"u-{Guid.NewGuid():N}",
            MergedIntoId = mergedInto,
            Phone = phone,
            CreatedByShopper = provisional,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.WpfCustomerProjections.Add(row);
        db.SaveChanges();
        return row;
    }

    private static Shopper NewShopper(LicenseDbContext db, string phone, DateTimeOffset? phoneVerifiedAt)
    {
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            Phone = phone,
            PhoneVerifiedAt = phoneVerifiedAt,
            PasswordHash = $"h-{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);
        db.SaveChanges();
        return shopper;
    }

    private static ShopperBroadcasterLink Link(
        LicenseDbContext db, Guid shopperId, Guid licenseId, Guid? customerId, DateTimeOffset joinedAt, bool left = false)
    {
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "tiktok",
            Username = $"u-{Guid.NewGuid():N}",
            WpfCustomerId = customerId,
            JoinedAt = joinedAt,
            LeftAt = left ? joinedAt.AddHours(1) : null,
        };
        db.ShopperBroadcasterLinks.Add(link);
        db.SaveChanges();
        return link;
    }

    [Theory, InlineData(Proof.Matches, true), InlineData(Proof.OtherPhone, false), InlineData(Proof.NotVerified, false),
     InlineData(Proof.CanonicalProvisional, false)]
    public async Task Gecici_kopyadan_kanitsiz_cikilmaz(Proof proof, bool resolves)
    {
        // Kp (geçici kopya) → P; bağlantı Kp'de. Kanıt asıl kaydın telefonundan: kopyanın telefonu kaydolanın kendi
        // beyanıdır, kanıt sayılmaz (OtherPhone'da da shopper'ınki).
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var shopperPhone = TestPhone.NewE164();
        var p = Row(db, lic, phone: proof == Proof.OtherPhone ? TestPhone.NewE164() : shopperPhone,
            provisional: proof == Proof.CanonicalProvisional);
        var kp = Row(db, lic, p.Id, phone: shopperPhone, provisional: true);
        DateTimeOffset? verifiedAt = proof == Proof.NotVerified ? null : DateTimeOffset.UtcNow;
        var shopper = NewShopper(db, shopperPhone, verifiedAt);
        Link(db, shopper.Id, lic, kp.Id, DateTimeOffset.UtcNow);
        Guid? expected = resolves ? p.Id : null;

        (await ShopperCustomerResolver.ResolveLinkAsync(db, lic, kp.Id, shopperPhone, verifiedAt, CancellationToken.None))
            .Should().Be(expected);
        (await ShopperCustomerResolver.PaymentCustomerAsync(db, lic, shopper.Id, CancellationToken.None))
            .Should().Be(expected);
    }

    [Fact]
    public async Task Gecici_kopyadan_gecen_zincir_lisansin_asil_kaydina_varmazsa_kanit_aranmaz()
    {
        // Kanıt yalnız lisansın asıl kaydında aranır; telefon iki durumda da kanıtlıyor. (1) Kp → X, X başka lisansta: zincir
        // X'te durur (aynı lisans süzgeci), X müşteri değildir. (2) Kp → Z3 → Z2 → Z1 → W: zincir adım sınırında Z1'de (kopya)
        // durur; sonuç kopya Id'si olmaz.
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var shopperPhone = TestPhone.NewE164();
        var verifiedAt = DateTimeOffset.UtcNow;
        var x = Row(db, Guid.NewGuid(), phone: shopperPhone);
        var kpToOtherLicense = Row(db, lic, x.Id, phone: shopperPhone, provisional: true);
        var w = Row(db, lic);
        var z1 = Row(db, lic, w.Id, phone: shopperPhone);
        var z2 = Row(db, lic, z1.Id);
        var z3 = Row(db, lic, z2.Id);
        var kpBeyondHops = Row(db, lic, z3.Id, phone: shopperPhone, provisional: true);

        (await ShopperCustomerResolver.ResolveLinkAsync(db, lic, kpToOtherLicense.Id, shopperPhone, verifiedAt,
            CancellationToken.None)).Should().BeNull("varılan satır başka lisansın");
        (await ShopperCustomerResolver.ResolveLinkAsync(db, lic, kpBeyondHops.Id, shopperPhone, verifiedAt,
            CancellationToken.None)).Should().BeNull("adım sınırında varılan satır kopya");
    }

    [Fact]
    public async Task Gecici_olmayan_kopya_asil_kayda_cozulur_gecici_asil_kayit_kendisidir()
    {
        // K→P (geçici değil): telefon aranmadan P; S (geçici asıl): S; 4 adımlı zincir: CustomerIdResolver'ın vardığı Id.
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var p = Row(db, lic, phone: TestPhone.NewE164());
        var k = Row(db, lic, p.Id);
        var s = Row(db, lic, phone: TestPhone.NewE164(), provisional: true);
        var w = Row(db, lic);
        var z1 = Row(db, lic, w.Id);
        var z2 = Row(db, lic, z1.Id);
        var z3 = Row(db, lic, z2.Id);
        var z4 = Row(db, lic, z3.Id);

        (await ShopperCustomerResolver.ResolveLinkAsync(db, lic, k.Id, null, null, CancellationToken.None))
            .Should().Be(p.Id);
        (await ShopperCustomerResolver.ResolveLinkAsync(db, lic, s.Id, null, null, CancellationToken.None))
            .Should().Be(s.Id, "geçici asıl kayıt zincir değildir, kendisidir");
        (await ShopperCustomerResolver.ResolveLinkAsync(db, lic, z4.Id, null, null, CancellationToken.None))
            .Should().Be(await new CustomerIdResolver(db).CanonicalOfAsync(lic, z4.Id, CancellationToken.None));
    }

    [Fact]
    public async Task Dekontun_musterisi_en_yeni_musterili_baglantidan_turetilir()
    {
        // Etkin bağlantı müşterisiz + eski ayrılmış bağlantı Q'da ⇒ Q; daha yeni ayrılmış bağlantı P'de (K kopyası
        // üzerinden) ⇒ P; bağlantı yalnız başka lisansta ⇒ null; shopperId null ⇒ null. JoinedAt eşitliği tohumlanmaz
        // (Guid sırası sağlayıcıya göre).
        using var db = NewDb();
        var lic = Guid.NewGuid();
        var otherLic = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow.AddDays(-30);
        var q = Row(db, lic);
        var p = Row(db, lic);
        var k = Row(db, lic, p.Id);
        var shopper = NewShopper(db, TestPhone.NewE164(), DateTimeOffset.UtcNow);
        var oldLeft = Link(db, shopper.Id, lic, q.Id, t0, left: true);
        Link(db, shopper.Id, lic, null, t0.AddDays(2));
        Link(db, shopper.Id, otherLic, Row(db, otherLic).Id, t0.AddDays(3));

        (await ShopperCustomerResolver.DeriveAsync(db, lic, shopper.Id, CancellationToken.None))
            .Should().Be(new DerivedCustomer(q.Id, oldLeft.Id, q.Id));

        var newerLeft = Link(db, shopper.Id, lic, k.Id, t0.AddDays(1), left: true);

        (await ShopperCustomerResolver.DeriveAsync(db, lic, shopper.Id, CancellationToken.None))
            .Should().Be(new DerivedCustomer(p.Id, newerLeft.Id, k.Id), "okunan müşteri kopya Id'sidir, çözülen asıl kayıt");
        (await ShopperCustomerResolver.PaymentCustomerAsync(db, lic, shopper.Id, CancellationToken.None))
            .Should().Be(p.Id);
        var thirdLic = Guid.NewGuid();
        (await ShopperCustomerResolver.DeriveAsync(db, thirdLic, shopper.Id, CancellationToken.None))
            .Should().BeNull("bağlantıları yalnız başka lisanslarda");
        (await ShopperCustomerResolver.PaymentCustomerAsync(db, thirdLic, shopper.Id, CancellationToken.None))
            .Should().BeNull();
        (await ShopperCustomerResolver.DeriveAsync(db, lic, null, CancellationToken.None)).Should().BeNull();
        (await ShopperCustomerResolver.PaymentCustomerAsync(db, lic, null, CancellationToken.None)).Should().BeNull();
    }
}
