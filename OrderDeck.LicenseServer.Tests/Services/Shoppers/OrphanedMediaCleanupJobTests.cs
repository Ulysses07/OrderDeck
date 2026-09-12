using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;

namespace OrderDeck.LicenseServer.Tests.Services.Shoppers;

/// <summary>
/// R4-05: temizlik kuyruğu tek başına yeterli değil. Satır yalnızca aynı
/// müşteri için ELLE ikinci bir purge tetiklenirse okunurdu; pratikte bu hiç
/// olmaz. Kişisel veri "bir gün biri tekrar basarsa" değil, silinene kadar
/// denenerek silinir — bu işin varlık sebebi o.
/// </summary>
public sealed class OrphanedMediaCleanupJobTests
{
    private static LicenseDbContext NewDb()
        => new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"orphan-{Guid.NewGuid():N}")
            .Options);

    private sealed class FlakyStorage : IShopperPaymentStorage
    {
        private readonly StubShopperPaymentStorage _inner = new();

        public bool FailDeletes { get; set; }

        public Task<string> UploadAsync(string objectKey, byte[] bytes, string contentType, CancellationToken ct = default)
            => _inner.UploadAsync(objectKey, bytes, contentType, ct);

        public Task<string> CreateDownloadUrlAsync(string objectKey, TimeSpan? expiry = null, CancellationToken ct = default)
            => _inner.CreateDownloadUrlAsync(objectKey, expiry, ct);

        public Task DeleteAsync(string objectKey, CancellationToken ct = default)
        {
            if (FailDeletes) throw new IOException("depo geçici olarak erişilemez");
            return _inner.DeleteAsync(objectKey, ct);
        }

        public bool Contains(string objectKey) => _inner.Contains(objectKey);
    }

    private static OrphanedMediaCleanupJob Build(LicenseDbContext db, IShopperPaymentStorage storage)
        => new(db, storage, NullLogger<OrphanedMediaCleanupJob>.Instance);

    private static (Guid PaymentId, string Key) Seed(LicenseDbContext db)
    {
        const string key = "payments/abc/def.pdf";
        var paymentId = Guid.NewGuid();
        db.Payments.Add(new Payment
        {
            Id = paymentId,
            LicenseId = Guid.NewGuid(),
            PayerName = "",
            ReferansNo = "REF-1",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.OrphanedMediaObjects.Add(new OrphanedMediaObject
        {
            Id = Guid.NewGuid(),
            ObjectKey = key,
            PaymentId = paymentId,
            ShopperId = Guid.NewGuid(),
            AttemptCount = 1,
            LastError = "depo geçici olarak erişilemez",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1),
            LastAttemptAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        return (paymentId, key);
    }

    [Fact]
    public async Task Silinebilen_nesneyi_siler_ve_odemeyi_damgalar()
    {
        using var db = NewDb();
        var (paymentId, key) = Seed(db);
        await db.SaveChangesAsync();

        var storage = new FlakyStorage();
        await storage.UploadAsync(key, new byte[] { 1 }, "application/pdf");

        var deleted = await Build(db, storage).RunAsync(default);

        deleted.Should().Be(1);
        storage.Contains(key).Should().BeFalse();

        var row = await db.OrphanedMediaObjects.SingleAsync();
        row.DeletedAt.Should().NotBeNull();
        row.LastError.Should().BeNull();
        row.AttemptCount.Should().Be(2);

        // Damganın tek doğru anı gerçek silme onayı; iş onu burada vuruyor.
        (await db.Payments.FirstAsync(p => p.Id == paymentId))
            .PdfPurgedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Hala_silemiyorsa_damga_vurmaz_ve_satiri_kuyrukta_tutar()
    {
        using var db = NewDb();
        var (paymentId, _) = Seed(db);
        await db.SaveChangesAsync();

        var deleted = await Build(db, new FlakyStorage { FailDeletes = true }).RunAsync(default);

        deleted.Should().Be(0);

        var row = await db.OrphanedMediaObjects.SingleAsync();
        row.DeletedAt.Should().BeNull();
        row.AttemptCount.Should().Be(2);
        row.LastError.Should().NotBeNullOrEmpty("hata metni yönetim ekranında görünmeli");

        (await db.Payments.FirstAsync(p => p.Id == paymentId))
            .PdfPurgedAt.Should().BeNull();
    }

    [Fact]
    public async Task Kapanmis_satirlari_yeniden_denemez()
    {
        using var db = NewDb();
        var (_, key) = Seed(db);
        var row = db.OrphanedMediaObjects.Local.Single();
        row.DeletedAt = DateTimeOffset.UtcNow.AddHours(-1);
        await db.SaveChangesAsync();

        var storage = new FlakyStorage { FailDeletes = true };
        await storage.UploadAsync(key, new byte[] { 1 }, "application/pdf");

        (await Build(db, storage).RunAsync(default)).Should().Be(0);
        (await db.OrphanedMediaObjects.SingleAsync()).AttemptCount.Should().Be(1);
    }
}
