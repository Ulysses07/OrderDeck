using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;

namespace OrderDeck.LicenseServer.Tests.Services.Shoppers;

public sealed class ShopperPurgeServiceTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"purge-{Guid.NewGuid():N}")
            .Options);

    private static Shopper SeedShopper(LicenseDbContext db, string phone = "+905001112233")
    {
        var shopper = new Shopper
        {
            Id = Guid.NewGuid(),
            FullName = "Ayşe Yılmaz",
            Phone = phone,
            PasswordHash = "argon2-hash",
            Address = "Örnek Mah. 1. Sok. No:2 Kadıköy/İstanbul",
            Email = "ayse@example.com",
            Tc = "12345678901",
            SmsConsent = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Shoppers.Add(shopper);
        return shopper;
    }

    private static License SeedLicense(LicenseDbContext db)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"{Guid.NewGuid():N}@test.com",
            Name = "Yayıncı",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = Guid.NewGuid().ToString("N")[..20].ToUpperInvariant(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
        };
        db.Licenses.Add(license);
        return license;
    }

    private static (ShopperPurgeService Service, StubShopperPaymentStorage Storage) Build(LicenseDbContext db)
    {
        var storage = new StubShopperPaymentStorage();
        return (new ShopperPurgeService(db, storage, NullLogger<ShopperPurgeService>.Instance), storage);
    }

    [Fact]
    public async Task Kisisel_alanlari_temizler_ve_hesabi_kilitler()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db);
        await db.SaveChangesAsync();

        var (service, _) = Build(db);
        var result = await service.PurgeAsync(shopper.Id, default);

        result.Should().NotBeNull();
        var purged = await db.Shoppers.FirstAsync(s => s.Id == shopper.Id);
        purged.FullName.Should().Be("[Silindi]");
        purged.Address.Should().BeEmpty();
        purged.Email.Should().BeNull();
        purged.Tc.Should().BeNull();
        purged.SmsConsent.Should().BeFalse();
        purged.DeletedAt.Should().NotBeNull();
        // Hiçbir doğrulamanın eşleyemeyeceği değer: hesaba bir daha girilemez.
        purged.PasswordHash.Should().Be("PURGED");
    }

    /// <summary>
    /// Telefon kimliğin en güçlü tekil alanı; silinmezse kişi hâlâ
    /// tanımlanabilir kalır. Boşalması ayrıca aynı numarayla yeniden kayıt
    /// olmayı mümkün kılıyor (filtreli unique index sayesinde).
    /// </summary>
    [Fact]
    public async Task Telefonu_bosaltir()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db, "+905551234567");
        await db.SaveChangesAsync();

        var (service, _) = Build(db);
        await service.PurgeAsync(shopper.Id, default);

        (await db.Shoppers.FirstAsync(s => s.Id == shopper.Id)).Phone.Should().BeEmpty();
    }

    /// <summary>
    /// Silinenler ile kalanların ayrımı bu testin bütün konusu. Tutar, tarih,
    /// referans no ve hash'ler mali kayıt + sahtekârlık koruması olarak
    /// kalmak zorunda; ad ve IBAN gitmek zorunda.
    /// </summary>
    [Fact]
    public async Task Odemede_PII_gider_mali_alanlar_ve_hashler_kalir()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db);
        var license = SeedLicense(db);
        var paidAt = DateTimeOffset.UtcNow.AddDays(-3);

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ShopperId = shopper.Id,
            PayerName = "Ayşe Yılmaz",
            Amount = 1500m,
            PaidAt = paidAt,
            ReferansNo = "REF-42",
            PdfHash = "sha256-abc",
            MetadataHash = "meta-def",
            RecipientIban = "TR330006100519786457841326",
            RecipientName = "Yayıncı Ltd",
            MediaObjectKey = "payments/x/y.pdf",
            MediaContentType = "application/pdf",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (service, _) = Build(db);
        var result = await service.PurgeAsync(shopper.Id, default);

        var payment = await db.Payments.FirstAsync();
        payment.PayerName.Should().BeEmpty();
        payment.RecipientIban.Should().BeNull();
        payment.RecipientName.Should().BeNull();
        payment.MediaObjectKey.Should().BeNull();
        payment.PdfPurgedAt.Should().NotBeNull();

        payment.Amount.Should().Be(1500m);
        payment.PaidAt.Should().Be(paidAt);
        payment.ReferansNo.Should().Be("REF-42");
        payment.PdfHash.Should().Be("sha256-abc");
        payment.MetadataHash.Should().Be("meta-def");

        result!.PaymentsScrubbed.Should().Be(1);
    }

    [Fact]
    public async Task Dekont_pdfini_depodan_siler()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db);
        var license = SeedLicense(db);
        const string key = "payments/abc/def.pdf";

        db.Payments.Add(new Payment
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ShopperId = shopper.Id,
            PayerName = "Ayşe Yılmaz",
            ReferansNo = "REF-1",
            MediaObjectKey = key,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (service, storage) = Build(db);
        await storage.UploadAsync(key, new byte[] { 1, 2, 3 }, "application/pdf");
        storage.Contains(key).Should().BeTrue();

        var result = await service.PurgeAsync(shopper.Id, default);

        storage.Contains(key).Should().BeFalse();
        result!.PdfsDeleted.Should().Be(1);
    }

    /// <summary>
    /// Silmenin üçüncü katmanı: yayıncının kendi bilgisayarındaki kopya.
    /// Sunucu projeksiyonu temizlenip UpdatedAt ileri alınmazsa WPF senkronu
    /// ("UpdatedAt > since") değişikliği hiç görmez ve adres sahada durmaya
    /// devam eder.
    /// </summary>
    [Fact]
    public async Task Yayinci_kopyasini_temizler_ve_senkron_icin_damgalar()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db, "+905551110000");
        var license = SeedLicense(db);
        var stale = DateTimeOffset.UtcNow.AddDays(-10);

        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            LicenseId = license.Id,
            Platform = "instagram",
            Username = "ayse_y",
            JoinedAt = DateTimeOffset.UtcNow,
        });
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Platform = "instagram",
            Username = "ayse_y",
            FullName = "Ayşe Yılmaz",
            Phone = "+905551110000",
            Address = "Örnek Mah.",
            UpdatedAt = stale,
        });
        await db.SaveChangesAsync();

        var (service, _) = Build(db);
        var result = await service.PurgeAsync(shopper.Id, default);

        var projection = await db.WpfCustomerProjections.FirstAsync();
        projection.FullName.Should().BeNull();
        projection.Phone.Should().BeNull();
        projection.Address.Should().BeNull();
        projection.UpdatedAt.Should().BeAfter(stale);
        // Damga olmadan temizlik kalıcı değil: yayıncının bilgisayarındaki
        // kopya silinmediği için bir sonraki WPF push'u bu satıra kişisel
        // veriyi geri yazardı.
        projection.PurgedAt.Should().NotBeNull();
        result!.ProjectionsScrubbed.Should().Be(1);
    }

    [Fact]
    public async Task Bagli_satirlari_siler()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db);
        var license = SeedLicense(db);

        db.ShopperPushDevices.Add(new ShopperPushDevice
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            DeviceId = "device-1",
            Platform = "android",
            PushToken = "token-1",
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            TokenHash = "hash-1",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(90),
        });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            LicenseId = license.Id,
            Platform = "instagram",
            Username = "ayse_y",
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (service, _) = Build(db);
        var result = await service.PurgeAsync(shopper.Id, default);

        db.ShopperPushDevices.Should().BeEmpty();
        db.ShopperRefreshTokens.Should().BeEmpty();
        db.ShopperBroadcasterLinks.Should().BeEmpty();
        result!.DependentRowsDeleted.Should().Be(3);
    }

    /// <summary>
    /// Denetim satırı mali izin parçası olduğu için silinmiyor; yalnız
    /// istemci parmak izi gidiyor. SecurityDataRetentionJob'ın 90 günde
    /// yaptığının aynısı.
    /// </summary>
    [Fact]
    public async Task Odeme_denetiminde_yalniz_parmak_izini_siler()
    {
        using var db = NewDb();
        var shopper = SeedShopper(db);
        var license = SeedLicense(db);
        var paymentId = Guid.NewGuid();

        db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            PaymentId = paymentId,
            LicenseId = license.Id,
            IpAddress = "203.0.113.7",
            UserAgent = "OrderDeck/1.0 Android",
            FraudFlags = "",
            ParserConfidence = "High",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var (service, _) = Build(db);
        await service.PurgeAsync(shopper.Id, default);

        var audit = await db.PaymentSubmissionAudits.FirstAsync();
        audit.IpAddress.Should().BeEmpty();
        audit.UserAgent.Should().BeEmpty();
        audit.PaymentId.Should().Be(paymentId);
    }

    [Fact]
    public async Task Bilinmeyen_shopper_icin_null_doner()
    {
        using var db = NewDb();
        var (service, _) = Build(db);

        (await service.PurgeAsync(Guid.NewGuid(), default)).Should().BeNull();
    }
}
