using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.WhatsApp;

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

        public Task<IReadOnlyList<string>> ListLicenseObjectsAsync(Guid licenseId, CancellationToken ct = default)
            => _inner.ListLicenseObjectsAsync(licenseId, ct);

        public Task<IReadOnlyList<MediaDeleteFailure>> DeleteObjectsAsync(IReadOnlyList<string> keys,
            CancellationToken ct = default)
            => _inner.DeleteObjectsAsync(keys, ct);

        public bool Contains(string objectKey) => _inner.Contains(objectKey);
    }

    private static OrphanedMediaCleanupJob Build(LicenseDbContext db, IShopperPaymentStorage storage,
        IWhatsAppMediaStore? whatsApp = null)
        => new(db, storage, whatsApp ?? new InMemoryWhatsAppMediaStore(), NullLogger<OrphanedMediaCleanupJob>.Instance);

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
        db.DataDestructionRecords.Should().BeEmpty("silinmeyen nesne imha kaydına girmez");
    }

    [Fact]
    public async Task Gec_silinen_dekont_silmenin_imha_kaydina_girer()
    {
        // KVKK Ö-13 (Silme Yönetmeliği md. 7/3): purge'de depodan silinemeyen dekont burada silinir; bu, purge'ün 1. adımının
        // devamıdır ve onunla aynı kayıtla yazılır (hedef, tetikleyici, konu = ShopperId, lisans ödemeden). Yoksa gecikmeli
        // silme veritabanı dışındaki özete hiç girmez.
        using var db = NewDb();
        var (paymentId, key) = Seed(db);
        var queuedWithoutPayment = new OrphanedMediaObject
        {
            Id = Guid.NewGuid(), ObjectKey = $"payments/{Guid.NewGuid():N}.pdf", ShopperId = Guid.NewGuid(), AttemptCount = 1,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), LastAttemptAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.OrphanedMediaObjects.Add(queuedWithoutPayment);
        await db.SaveChangesAsync();
        var row = await db.OrphanedMediaObjects.AsNoTracking().SingleAsync(o => o.ObjectKey == key);
        var licenseId = (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == paymentId)).LicenseId;

        (await Build(db, new FlakyStorage()).RunAsync(default)).Should().Be(2);

        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Should().HaveCount(2).And.OnlyContain(r => r.Target == ShopperPurgeService.MediaTarget
            && r.Action == DataDestruction.Actions.Deleted && r.RowCount == 1 && r.Trigger == DataDestruction.Triggers.ShopperPurge
            && r.Criterion == ShopperPurgeService.PurgeCriterion && r.Actor == DataDestruction.SystemActor);
        records.Should().ContainSingle(r => r.SubjectId == row.ShopperId && r.LicenseId == licenseId);
        records.Should().ContainSingle(r => r.SubjectId == queuedWithoutPayment.ShopperId && r.LicenseId == null,
            "ödemesi bilinmeyen kuyruk satırının lisansı yok");
    }

    [Fact]
    public async Task Yayinci_silmesinden_kalan_nesneyi_kendi_deposundan_siler_ve_yayinci_silmesi_kaydini_yazar()
    {
        // Yayıncı silmesinin (AdminCustomersController.Purge) kuyruğa bıraktığı satırda ShopperId yok: lisans anahtarın önekinde,
        // tür (dekont / WhatsApp) de öyle. WhatsApp nesnesi WhatsApp deposundan silinir; geciken silme, yayıncı silmesinin
        // kaydıyla (tetikleyici, ölçüt, hedef) yazılır — müşteri silmesinin kaydıyla değil.
        using var db = NewDb();
        var license = Guid.NewGuid();
        var waKey = $"{WhatsAppMediaKeys.LicensePrefix(license)}2026/10/m1.jpg";
        var receiptKey = $"{ShopperPaymentKeys.LicensePrefix(license)}r1.pdf";
        foreach (var key in new[] { waKey, receiptKey })
            db.OrphanedMediaObjects.Add(new OrphanedMediaObject
            {
                Id = Guid.NewGuid(), ObjectKey = key, AttemptCount = 1, LastError = "depo geçici olarak erişilemez",
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-1), LastAttemptAt = DateTimeOffset.UtcNow.AddDays(-1),
            });
        await db.SaveChangesAsync();
        var storage = new FlakyStorage();
        await storage.UploadAsync(receiptKey, new byte[] { 1 }, "application/pdf");
        var whatsApp = new InMemoryWhatsAppMediaStore();
        await whatsApp.SaveAsync(waKey, new byte[] { 1 }, "image/jpeg");

        (await Build(db, storage, whatsApp).RunAsync(default)).Should().Be(2);

        whatsApp.TryGet(waKey, out _).Should().BeFalse();
        storage.Contains(receiptKey).Should().BeFalse();
        var records = await db.DataDestructionRecords.AsNoTracking().ToListAsync();
        records.Select(r => r.Target).Should().BeEquivalentTo(
            new[] { LicenseMediaPurge.WhatsAppMediaTarget, ShopperPurgeService.MediaTarget });
        records.Should().OnlyContain(r => r.LicenseId == license && r.SubjectId == null && r.RowCount == 1
            && r.Action == DataDestruction.Actions.Deleted && r.Trigger == DataDestruction.Triggers.CustomerPurge
            && r.Criterion == LicenseMediaPurge.CustomerPurgeCriterion && r.Actor == DataDestruction.SystemActor);
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
