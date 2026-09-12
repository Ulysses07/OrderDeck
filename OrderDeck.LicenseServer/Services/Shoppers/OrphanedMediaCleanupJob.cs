using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;

namespace OrderDeck.LicenseServer.Services.Shoppers;

/// <summary>Kuyruk boşaltma yardımcıları; hem purge hem temizlik işi kullanıyor.</summary>
public static class OrphanedMedia
{
    /// <summary>Hata metni <c>LastError</c> sütununa (1024) sığmalı.</summary>
    public static string TruncateError(string message) =>
        message.Length <= 1024 ? message : message[..1024];
}

/// <summary>
/// KVKK silmesi sırasında depodan silinemeyen nesneleri yeniden silmeyi
/// dener. Kuyruğun var olma sebebi ile bu işin var olma sebebi aynı: silme
/// niyeti kaybolmamalı. Ama kuyruk tek başına yetmiyordu — satırı yalnızca
/// aynı shopper için ELLE ikinci bir purge tetiklenirse okunurdu ve o
/// pratikte hiç olmaz. Kişisel veri "bir gün biri tekrar basarsa" silinmez;
/// silinene kadar denenir.
///
/// <see cref="OrphanedMediaObject.DeletedAt"/> dolduğunda ilgili ödemenin
/// <c>PdfPurgedAt</c> damgası da burada vuruluyor — damganın tek doğru anı
/// gerçek silme onayı.
/// </summary>
public sealed class OrphanedMediaCleanupJob
{
    /// <summary>Bir turda denenecek azami satır; iş uzun sürüp Hangfire'ı tıkamasın.</summary>
    private const int BatchSize = 200;

    private readonly LicenseDbContext _db;
    private readonly IShopperPaymentStorage _storage;
    private readonly ILogger<OrphanedMediaCleanupJob> _log;

    public OrphanedMediaCleanupJob(
        LicenseDbContext db,
        IShopperPaymentStorage storage,
        ILogger<OrphanedMediaCleanupJob> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    /// <summary>Silinebilen nesne sayısını döndürür.</summary>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var pending = await _db.OrphanedMediaObjects
            .Where(o => o.DeletedAt == null)
            .OrderBy(o => o.LastAttemptAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        if (pending.Count == 0) return 0;

        var now = DateTimeOffset.UtcNow;
        var deleted = 0;
        var deletedPaymentIds = new List<Guid>();

        foreach (var row in pending)
        {
            row.AttemptCount++;
            row.LastAttemptAt = now;
            try
            {
                await _storage.DeleteAsync(row.ObjectKey, ct);
                row.DeletedAt = now;
                row.LastError = null;
                deleted++;
                if (row.PaymentId is not null) deletedPaymentIds.Add(row.PaymentId.Value);
            }
            catch (Exception ex)
            {
                row.LastError = OrphanedMedia.TruncateError(ex.Message);
                _log.LogError(ex,
                    "[OrphanedMediaCleanup] {Key} hâlâ silinemiyor ({Attempts}. deneme)",
                    row.ObjectKey, row.AttemptCount);
            }
        }

        if (deletedPaymentIds.Count > 0)
        {
            var payments = await _db.Payments
                .Where(p => deletedPaymentIds.Contains(p.Id))
                .ToListAsync(ct);
            foreach (var p in payments)
            {
                p.PdfPurgedAt ??= now;
                p.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "[OrphanedMediaCleanup] {Deleted}/{Total} yetim nesne silindi",
            deleted, pending.Count);
        return deleted;
    }
}
