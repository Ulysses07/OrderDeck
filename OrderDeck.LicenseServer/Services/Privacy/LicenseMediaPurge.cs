using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.ShopperPayments;
using OrderDeck.LicenseServer.Services.Shoppers;
using OrderDeck.LicenseServer.Services.WhatsApp;

namespace OrderDeck.LicenseServer.Services.Privacy;

/// <summary>
/// Yayıncı silmesinde (KVKK, <c>AdminCustomersController.Purge</c>) lisansların kova nesneleri: müşterilerin o yayıncıya
/// yüklediği dekontlar (<see cref="ShopperPaymentKeys.LicensePrefix"/>, karar 5) ve WhatsApp medyası
/// (<see cref="WhatsAppMediaKeys.LicensePrefix"/>, karar 6; 23.10.2026). Satırları (Payment, WaMessage) lisansla kaskad gidiyor,
/// nesneler kovada süresiz kalıyordu. Yayın paylaşımı medyası ve ürün fotoğrafları bu silmenin kapsamında DEĞİL.
///
/// <para>İki adım, ikisinin arasında satırların silinmesi: anahtarlar satırlar silinmeden ÖNCE listelenir
/// (<see cref="TryListAsync"/>) — lisans satırı gidince öneki bir daha bulunamaz, liste alınamıyorsa silme hiç başlamaz.
/// Nesneler satırlar silindikten SONRA silinir (<see cref="DeleteAsync"/>) — satırlar dururken dosya silinmiş olmaz (R12-S01).
/// Silinemeyen her anahtar <see cref="OrphanedMediaObject"/> kuyruğuna yazılır, <see cref="OrphanedMediaCleanupJob"/> yeniden
/// dener; kuyruk satırında ShopperId boş: konu yayıncı, lisans ve tür anahtarın önekinde.</para>
///
/// <para>Listeleme ile silme arasında yazılan nesne (yayıncı silinirken yüklenen dekont, o sırada indirilen WhatsApp medyası)
/// listede yoktur ve kalır; pencere bir isteğin süresi kadar.</para>
/// </summary>
public sealed class LicenseMediaPurge
{
    /// <summary>Silinen WhatsApp medya nesnesinin imha kaydı hedefi. Dekontun hedefi müşteri silmesininkiyle aynı
    /// (<see cref="ShopperPurgeService.MediaTarget"/>): aynı tür nesnenin imhası tek hedef adıyla gruplansın.</summary>
    public const string WhatsAppMediaTarget = "WhatsAppMedia";

    /// <summary>Yayıncı silmesinin imha kaydı ölçütü; geciken silme (<see cref="OrphanedMediaCleanupJob"/>) da bunu yazar.</summary>
    public const string CustomerPurgeCriterion = "yayıncı silme talebi";

    public sealed record LicenseObject(Guid LicenseId, string Key);

    /// <summary>Satırlar silinmeden önce alınan anahtar listesi, depo başına.</summary>
    public sealed record Listing(IReadOnlyList<LicenseObject> Receipts, IReadOnlyList<LicenseObject> WhatsApp);

    private readonly LicenseDbContext _db;
    private readonly IShopperPaymentStorage _receipts;
    private readonly IWhatsAppMediaStore _whatsApp;
    private readonly ILogger<LicenseMediaPurge> _log;

    public LicenseMediaPurge(LicenseDbContext db, IShopperPaymentStorage receipts, IWhatsAppMediaStore whatsApp,
        ILogger<LicenseMediaPurge> log)
    {
        _db = db;
        _receipts = receipts;
        _whatsApp = whatsApp;
        _log = log;
    }

    public static bool IsWhatsAppKey(string key) => key.StartsWith(WhatsAppMediaKeys.Root, StringComparison.Ordinal);

    /// <summary>Kuyruktaki anahtarın imha kaydı hedefi.</summary>
    public static string TargetOf(string key) => IsWhatsAppKey(key) ? WhatsAppMediaTarget : ShopperPurgeService.MediaTarget;

    /// <summary>Anahtarın lisansı: iki önek de <c>{kök}/{lisans:N}/…</c>. Biçim dışı anahtarda null.</summary>
    public static Guid? LicenseOf(string key)
    {
        var parts = key.Split('/', 3);
        return parts.Length == 3 && Guid.TryParseExact(parts[1], "N", out var id) ? id : null;
    }

    /// <summary>Lisansların dekont ve WhatsApp anahtarları. Depo listelenemezse null (günlüğe yazılır): çağıran silmeyi
    /// başlatmaz.</summary>
    public async Task<Listing?> TryListAsync(IReadOnlyCollection<Guid> licenseIds, CancellationToken ct)
    {
        var receipts = new List<LicenseObject>();
        var whatsApp = new List<LicenseObject>();
        try
        {
            foreach (var licenseId in licenseIds)
            {
                foreach (var key in await _receipts.ListLicenseObjectsAsync(licenseId, ct))
                    receipts.Add(new LicenseObject(licenseId, key));
                foreach (var key in await _whatsApp.ListLicenseObjectsAsync(licenseId, ct))
                    whatsApp.Add(new LicenseObject(licenseId, key));
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogError(ex, "[CustomerPurge] Lisans nesneleri listelenemedi; silme başlatılmadı");
            return null;
        }
        return new Listing(receipts, whatsApp);
    }

    /// <summary>
    /// Listelenen nesneleri siler; silinemeyenleri kuyruğa yazar, imha kaydını lisans ve tür başına yazar ve kaydeder.
    /// Satırlar zaten silinmiş olduğundan isteğin iptaline bağlı değil: yarıda kesilse anahtarlar hiçbir yerde kalmazdı.
    /// </summary>
    /// <returns>Silinen ve kuyrukta bekleyen nesne sayısı.</returns>
    public async Task<(int Deleted, int Pending)> DeleteAsync(Listing listing, string actor, Guid subjectId)
    {
        if (listing.Receipts.Count + listing.WhatsApp.Count == 0) return (0, 0);
        var ct = CancellationToken.None;

        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in await _receipts.DeleteObjectsAsync(listing.Receipts.Select(o => o.Key).ToList(), ct))
            failures[f.Key] = f.Error;
        foreach (var f in await _whatsApp.DeleteObjectsAsync(listing.WhatsApp.Select(o => o.Key).ToList(), ct))
            failures[f.Key] = f.Error;

        // Kuyruk anahtarı tekil: müşteri silmesinden (ShopperPurgeService) bekleyen satır varsa ikinci satır açılmaz, o güncellenir;
        // bu silmede silinen nesnenin bekleyen satırı kapanır (yoksa temizlik işi nesneyi bir daha "silip" ikinci kez kayda yazardı).
        var queued = new Dictionary<string, OrphanedMediaObject>(StringComparer.Ordinal);
        foreach (var licenseId in listing.Receipts.Concat(listing.WhatsApp).Select(o => o.LicenseId).Distinct())
            foreach (var prefix in new[] { ShopperPaymentKeys.LicensePrefix(licenseId), WhatsAppMediaKeys.LicensePrefix(licenseId) })
                foreach (var row in await _db.OrphanedMediaObjects.Where(o => o.ObjectKey.StartsWith(prefix)).ToListAsync(ct))
                    queued[row.ObjectKey] = row;

        var now = DateTimeOffset.UtcNow;
        var deleted = 0;
        void Settle(IReadOnlyList<LicenseObject> objects, string target)
        {
            foreach (var license in objects.GroupBy(o => o.LicenseId))
            {
                var count = 0;
                foreach (var (_, key) in license)
                {
                    queued.TryGetValue(key, out var row);
                    if (failures.TryGetValue(key, out var error))
                    {
                        if (row is null)
                        {
                            row = new OrphanedMediaObject { Id = Guid.NewGuid(), ObjectKey = key, CreatedAt = now };
                            _db.OrphanedMediaObjects.Add(row);
                        }
                        row.AttemptCount++;
                        row.LastAttemptAt = now;
                        row.LastError = OrphanedMedia.TruncateError(error);
                        row.DeletedAt = null;
                        continue;
                    }
                    count++;
                    if (row is { DeletedAt: null })
                    {
                        row.AttemptCount++;
                        row.LastAttemptAt = now;
                        row.LastError = null;
                        row.DeletedAt = now;   // gerçek silme onayı
                    }
                }
                // KVKK Ö-13: yalnız gerçekten silinen sayılır; kuyruktaki, silindiğinde temizlik işinin kaydına girer.
                DataDestruction.Record(_db, license.Key, target, DataDestruction.Actions.Deleted, count, CustomerPurgeCriterion,
                    DataDestruction.Triggers.CustomerPurge, actor, subjectId: subjectId);
                deleted += count;
            }
        }
        Settle(listing.Receipts, ShopperPurgeService.MediaTarget);
        Settle(listing.WhatsApp, WhatsAppMediaTarget);

        // Kesintide binlerce anahtar düşebilir: anahtar başına değil, tek satır (anahtarlar ve hataları kuyrukta).
        if (failures.Count > 0)
            _log.LogError("[CustomerPurge] {Pending} nesne silinemedi, kuyruğa alındı (ilk: {Key} — {Error})",
                failures.Count, failures.First().Key, failures.First().Value);

        await _db.SaveChangesAsync(ct);
        return (deleted, failures.Count);
    }
}
