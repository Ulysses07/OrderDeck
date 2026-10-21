using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;
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
        var deletedRows = new List<OrphanedMediaObject>();
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
                deletedRows.Add(row);
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

        var payments = deletedPaymentIds.Count == 0
            ? new List<Payment>()
            : await _db.Payments
                .Where(p => deletedPaymentIds.Contains(p.Id))
                .ToListAsync(ct);
        foreach (var p in payments)
        {
            p.PdfPurgedAt ??= now;
            p.UpdatedAt = now;
        }

        // KVKK Ö-13 (Silme Yönetmeliği md. 7/3): geciken silme purge'ün 1. adımının devamıdır ve onunla aynı kaydı yazar,
        // aynı SaveChanges'te (DeletedAt ile birlikte): hedef, ölçüt ve tetikleyici purge'ünkü, konu = ShopperId, lisans
        // ödemeden (ödemesi bilinmeyen kuyruk satırında yok), yapan sistem. Talebin kimliği kuyruk satırında yok; kayıt talebe
        // SubjectId → ShopperDeletionRequests.ShopperId ile bağlanır.
        var paymentLicenses = payments.ToDictionary(p => p.Id, p => p.LicenseId);
        foreach (var g in deletedRows.GroupBy(r => (r.ShopperId,
                     License: r.PaymentId is { } id && paymentLicenses.TryGetValue(id, out var l) ? l : (Guid?)null)))
            DataDestruction.Record(_db, g.Key.License, ShopperPurgeService.MediaTarget, DataDestruction.Actions.Deleted,
                g.Count(), ShopperPurgeService.PurgeCriterion, DataDestruction.Triggers.ShopperPurge, DataDestruction.SystemActor,
                subjectId: g.Key.ShopperId);

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "[OrphanedMediaCleanup] {Deleted}/{Total} yetim nesne silindi",
            deletedRows.Count, pending.Count);
        return deletedRows.Count;
    }
}
