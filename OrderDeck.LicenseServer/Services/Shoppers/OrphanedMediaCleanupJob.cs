using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Privacy;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.WhatsApp;

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
    private readonly IWhatsAppMediaStore _whatsApp;
    private readonly IBroadcastMediaStorage _broadcast;
    private readonly ILogger<OrphanedMediaCleanupJob> _log;

    public OrphanedMediaCleanupJob(
        LicenseDbContext db,
        IShopperPaymentStorage storage,
        IWhatsAppMediaStore whatsApp,
        IBroadcastMediaStorage broadcast,
        ILogger<OrphanedMediaCleanupJob> log)
    {
        _db = db;
        _storage = storage;
        _whatsApp = whatsApp;
        _broadcast = broadcast;
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
                // Yayıncı silmesi WhatsApp medyasını, paylaşım medyasını ve ürün fotoğrafını da kuyruğa bırakabilir: anahtar kendi
                // deposundan silinir.
                if (LicenseMediaPurge.IsWhatsAppKey(row.ObjectKey)) await _whatsApp.DeleteAsync(row.ObjectKey, ct);
                else if (LicenseMediaPurge.IsBroadcastMediaKey(row.ObjectKey)) await DeleteBroadcastAsync(row.ObjectKey, ct);
                else await _storage.DeleteAsync(row.ObjectKey, ct);
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
        foreach (var g in deletedRows.Where(r => r.ShopperId is not null).GroupBy(r => (r.ShopperId,
                     License: r.PaymentId is { } id && paymentLicenses.TryGetValue(id, out var l) ? l : (Guid?)null)))
            DataDestruction.Record(_db, g.Key.License, ShopperPurgeService.MediaTarget, DataDestruction.Actions.Deleted,
                g.Count(), ShopperPurgeService.PurgeCriterion, DataDestruction.Triggers.ShopperPurge, DataDestruction.SystemActor,
                subjectId: g.Key.ShopperId);
        // ShopperId'siz satır yayıncı silmesinden (LicenseMediaPurge): kaydı yayıncı silmesinin tetikleyicisi ve ölçütüyle, lisans
        // ve hedef (dekont / WhatsApp / paylaşım medyası / ürün fotoğrafı) anahtarın önekinden. Yayıncı hesabının kimliği kuyruk
        // satırında yok: konu boş kalır.
        foreach (var g in deletedRows.Where(r => r.ShopperId is null).GroupBy(r =>
                     (License: LicenseMediaPurge.LicenseOf(r.ObjectKey), Target: LicenseMediaPurge.TargetOf(r.ObjectKey))))
            DataDestruction.Record(_db, g.Key.License, g.Key.Target, DataDestruction.Actions.Deleted, g.Count(),
                LicenseMediaPurge.CustomerPurgeCriterion, DataDestruction.Triggers.CustomerPurge, DataDestruction.SystemActor);

        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "[OrphanedMediaCleanup] {Deleted}/{Total} yetim nesne silindi",
            deletedRows.Count, pending.Count);
        return deletedRows.Count;
    }

    /// <summary>Yayın kovasının tek silmesi (<see cref="IBroadcastMediaStorage.DeleteAsync"/>) R2'de hatayı yutar (yayın uçlarının
    /// sözleşmesi); onunla silinseydi reddedilen nesne silinmiş sayılıp kuyruktan düşer, imha kaydına yazılırdı. Hata döndüren
    /// toplu silme kullanılır, hata yukarıdaki yeniden deneme yoluna fırlatılır.</summary>
    private async Task DeleteBroadcastAsync(string key, CancellationToken ct)
    {
        var failures = await _broadcast.DeleteObjectsAsync([key], ct);
        if (failures.Count > 0) throw new IOException(failures[0].Error);
    }
}
