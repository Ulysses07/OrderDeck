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
/// yüklediği dekontlar (<see cref="ShopperPaymentKeys.LicensePrefix"/>, karar 5), WhatsApp medyası
/// (<see cref="WhatsAppMediaKeys.LicensePrefix"/>, karar 6), yayın paylaşımı medyası (<see cref="BroadcastMediaKeys.PostPrefix"/>)
/// ve ürün fotoğrafları (<see cref="BroadcastMediaKeys.ProductPhotoPrefix(Guid)"/>; kararların hepsi 23.10.2026). Satırları
/// (Payment, WaMessage, BroadcastPost, ProductPhoto) lisansla kaskad gidiyor, nesneler kovada süresiz kalıyordu.
///
/// <para><b>Niyet kalıcı.</b> Lisans satırlarını silen SaveChanges'e lisans başına bir <see cref="LicenseMediaPurgeTask"/> girer
/// (<see cref="Schedule"/>). İlk tur istekte, kayıttan hemen sonra koşar; sonrakileri <see cref="LicenseMediaPurgeJob"/>. İstek
/// iptal edilse, konteyner yeniden başlasa (master'a merge = otomatik deploy) ya da tur yarıda kalsa görev açık kalır, iş
/// tamamlar. Yeniden deneme idempotent: silinmiş nesne listede yoktur.</para>
///
/// <para><b>Bir tur</b> (<see cref="RunAsync"/>): lisansın dört öneki kovadan listelenir, satırlardan değil (satırı hiç yazılmamış
/// nesne de gider: ürüne bağlanmadan bırakılan fotoğraf, silinmesi yutulmuş eski paylaşım medyası). Bulunanlar silinir; gerçekten
/// silinenler imha kaydına lisans ve tür başına, görevin yeni durumuyla aynı SaveChanges'te yazılır. Silinemeyen nesne ya da
/// listelenemeyen depo görevi açık tutar (<see cref="RetryDelay"/> sonra yeniden).</para>
///
/// <para><b>Kapanış:</b> görev ancak <see cref="LateWriteWindow"/> geçtikten sonraki bir tur dört öneki de BOŞ görünce kapanır.
/// Silme sırasında alınmış imzalı URL'yle sonradan yüklenen nesne de böylece gider.</para>
///
/// <para>Kalan pencere: R2 silmesiyle turun SaveChanges'i arasında süreç ölürse nesne silinmiş ama imha kaydına sayılmamış olur;
/// sonraki tur öneki boş görüp görevi kapatır. Silme niyetinin ve kapanışının kaydı görev satırında kalır.</para>
/// </summary>
public sealed class LicenseMediaPurge
{
    /// <summary>Silinen WhatsApp medya nesnesinin imha kaydı hedefi. Dekontun hedefi müşteri silmesininkiyle aynı
    /// (<see cref="ShopperPurgeService.MediaTarget"/>): aynı tür nesnenin imhası tek hedef adıyla gruplansın.</summary>
    public const string WhatsAppMediaTarget = "WhatsAppMedia";

    /// <summary>Silinen yayın paylaşımı medya nesnesinin imha kaydı hedefi.</summary>
    public const string BroadcastPostMediaTarget = "BroadcastPostMedia";

    /// <summary>Silinen ürün fotoğrafı nesnesinin imha kaydı hedefi.</summary>
    public const string ProductPhotoMediaTarget = "ProductPhotoMedia";

    /// <summary>Yayıncı silmesinin imha kaydı ölçütü; işin sonraki turları da bunu yazar.</summary>
    public const string CustomerPurgeCriterion = "yayıncı silme talebi";

    /// <summary>Lisans satırları silindikten sonra lisansın önekine hâlâ yazılabilecek süre: imzalı yükleme URL'si 10 dk yaşar
    /// (<see cref="R2BroadcastMediaStorage.CreateUploadUrlAsync"/>) ve süresi dolmadan başlamış büyük bir yükleme sonra biter;
    /// dekont ve WhatsApp medyası istek içinde yazılır. Görev bu süre geçmeden kapanmaz.</summary>
    public static readonly TimeSpan LateWriteWindow = TimeSpan.FromMinutes(30);

    /// <summary>Silinemeyen nesne ya da listelenemeyen depo: görev bu kadar sonra yeniden ele alınır (iş saatte bir koşar).</summary>
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(45);

    /// <summary>Bir turun sonucu: silinen ve silinemeyen nesne sayısı, listelenemeyen lisans sayısı.</summary>
    public sealed record RoundResult(int Deleted, int Failed, int Unlisted);

    /// <summary>Bir lisansın dört önekinde bulunan anahtarlar; paylaşım medyası ve ürün fotoğrafı aynı depoda
    /// (<see cref="IBroadcastMediaStorage"/>), imha kaydında ayrı hedef.</summary>
    private sealed record Listing(IReadOnlyList<string> Receipts, IReadOnlyList<string> WhatsApp,
        IReadOnlyList<string> BroadcastPosts, IReadOnlyList<string> ProductPhotos)
    {
        public int Count => Receipts.Count + WhatsApp.Count + BroadcastPosts.Count + ProductPhotos.Count;

        public IEnumerable<string> All => Receipts.Concat(WhatsApp).Concat(BroadcastPosts).Concat(ProductPhotos);
    }

    private readonly LicenseDbContext _db;
    private readonly IShopperPaymentStorage _receipts;
    private readonly IWhatsAppMediaStore _whatsApp;
    private readonly IBroadcastMediaStorage _broadcast;
    private readonly ILogger<LicenseMediaPurge> _log;
    private readonly TimeProvider _clock;

    public LicenseMediaPurge(LicenseDbContext db, IShopperPaymentStorage receipts, IWhatsAppMediaStore whatsApp,
        IBroadcastMediaStorage broadcast, ILogger<LicenseMediaPurge> log, TimeProvider? clock = null)
    {
        _db = db;
        _receipts = receipts;
        _whatsApp = whatsApp;
        _broadcast = broadcast;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>Lisans başına görev satırını izleyiciye EKLER, kaydetmez: lisans satırlarını silen SaveChanges'te yazılsın (ikisi
    /// birlikte olur ya da hiçbiri). İlk turu istek, kayıttan hemen sonra, elindeki görevle koşar; işe görev ancak
    /// <see cref="LateWriteWindow"/> sonra açılır. Şimdiye açık olsaydı saatlik iş kayıtla istekteki turun bitişi arasına denk
    /// gelince aynı görevi aynı anda ele alır, iki tur da imha kaydı yazardı (sayılar ikiye katlanır). İstek ilk turu koşamadan
    /// ölürse iş pencere sonunda devralır; görev zaten bundan önce kapanamaz. Tur sonunda yazılan değer de aynı.</summary>
    public static List<LicenseMediaPurgeTask> Schedule(LicenseDbContext db, IEnumerable<Guid> licenseIds, Guid subjectId,
        DateTimeOffset now)
    {
        var tasks = licenseIds.Select(id => new LicenseMediaPurgeTask
        {
            Id = Guid.NewGuid(), LicenseId = id, SubjectId = subjectId, CreatedAt = now, NextAttemptAt = now + LateWriteWindow,
        }).ToList();
        db.LicenseMediaPurgeTasks.AddRange(tasks);
        return tasks;
    }

    /// <summary>Görevlerin birer turu; her görev kendi SaveChanges'iyle kaydedilir. İptal yalnız görevler ARASINDA dinlenir: başlamış
    /// tur, sildiği nesnenin kaydını yazmadan bırakılmaz. <paramref name="actor"/> imha kaydının yapanı (istekte yönetici, işte
    /// sistem).</summary>
    public async Task<RoundResult> RunAsync(IReadOnlyList<LicenseMediaPurgeTask> tasks, string actor, CancellationToken ct)
    {
        int deleted = 0, failed = 0, unlisted = 0;
        foreach (var task in tasks)
        {
            ct.ThrowIfCancellationRequested();
            var (d, f, listed) = await RunOneAsync(task, actor);
            deleted += d;
            failed += f;
            if (!listed) unlisted++;
        }
        return new RoundResult(deleted, failed, unlisted);
    }

    private async Task<(int Deleted, int Failed, bool Listed)> RunOneAsync(LicenseMediaPurgeTask task, string actor)
    {
        var ct = CancellationToken.None;
        var now = _clock.GetUtcNow();
        task.AttemptCount++;
        task.LastAttemptAt = now;

        Listing listing;
        try
        {
            listing = await ListAsync(task.LicenseId, ct);
        }
        catch (Exception ex)
        {
            // Eksik listeyle tur başlamaz: "nesne yok" sanılıp görev kapanmasın.
            _log.LogError(ex, "[LicenseMediaPurge] {LicenseId} lisansının nesneleri listelenemedi; görev açık", task.LicenseId);
            task.LastError = OrphanedMedia.TruncateError($"listelenemedi: {ex.Message}");
            task.NextAttemptAt = now + RetryDelay;
            await _db.SaveChangesAsync(ct);
            return (0, 0, false);
        }

        var windowEnd = task.CreatedAt + LateWriteWindow;
        if (listing.Count == 0)
        {
            task.LastError = null;
            if (now >= windowEnd) task.CompletedAt = now;
            else task.NextAttemptAt = windowEnd;
            await _db.SaveChangesAsync(ct);
            return (0, 0, true);
        }

        var failures = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in await _receipts.DeleteObjectsAsync(listing.Receipts, ct)) failures[f.Key] = f.Error;
        foreach (var f in await _whatsApp.DeleteObjectsAsync(listing.WhatsApp, ct)) failures[f.Key] = f.Error;
        // Tek silme (DeleteAsync) R2'de hatayı yutar: silinemeyen nesne silinmiş sayılmasın diye hata döndüren toplu silme.
        foreach (var f in await _broadcast.DeleteObjectsAsync([.. listing.BroadcastPosts, .. listing.ProductPhotos], ct))
            failures[f.Key] = f.Error;

        await CloseQueuedAsync(task.LicenseId, listing, failures, now, ct);

        // KVKK Ö-13: yalnız gerçekten silinen sayılır; silinemeyen, sildiği turun kaydına girer.
        var deleted = 0;
        void Record(IReadOnlyList<string> keys, string target)
        {
            var count = keys.Count(k => !failures.ContainsKey(k));
            DataDestruction.Record(_db, task.LicenseId, target, DataDestruction.Actions.Deleted, count, CustomerPurgeCriterion,
                DataDestruction.Triggers.CustomerPurge, actor, subjectId: task.SubjectId);
            deleted += count;
        }
        Record(listing.Receipts, ShopperPurgeService.MediaTarget);
        Record(listing.WhatsApp, WhatsAppMediaTarget);
        Record(listing.BroadcastPosts, BroadcastPostMediaTarget);
        Record(listing.ProductPhotos, ProductPhotoMediaTarget);

        if (failures.Count > 0)
        {
            // Kesintide binlerce anahtar düşebilir: anahtar başına değil, tek satır.
            var (key, error) = failures.First();
            task.LastError = OrphanedMedia.TruncateError($"{failures.Count} nesne silinemedi (ilk: {key} — {error})");
            task.NextAttemptAt = now + RetryDelay;
            _log.LogError("[LicenseMediaPurge] {LicenseId}: {Failed} nesne silinemedi, görev açık (ilk: {Key} — {Error})",
                task.LicenseId, failures.Count, key, error);
        }
        else
        {
            // Bulunan her şey silindi; kapanış, pencereden sonra öneki boş gören turda.
            task.LastError = null;
            task.NextAttemptAt = now > windowEnd ? now : windowEnd;
        }
        await _db.SaveChangesAsync(ct);
        return (deleted, failures.Count, true);
    }

    private async Task<Listing> ListAsync(Guid licenseId, CancellationToken ct)
        => new(
            await _receipts.ListLicenseObjectsAsync(licenseId, ct),
            await _whatsApp.ListLicenseObjectsAsync(licenseId, ct),
            // ListAsync R2 hatasını yutmaz (eksik liste "nesne yok" görünmesin).
            (await _broadcast.ListAsync(BroadcastMediaKeys.PostPrefix(licenseId), ct)).Select(o => o.Key).ToList(),
            (await _broadcast.ListAsync(BroadcastMediaKeys.ProductPhotoPrefix(licenseId), ct)).Select(o => o.Key).ToList());

    /// <summary>Müşteri silmesinden (<see cref="ShopperPurgeService"/>) kuyrukta bekleyen anahtarı bu tur sildiyse satırı kapanır:
    /// temizlik işi nesneyi bir daha "silip" imha kaydına ikinci kez yazmasın. Silinemeyenin satırına dokunulmaz: kuyruk müşteri
    /// silmesinin, bu görev zaten yeniden dener.</summary>
    private async Task CloseQueuedAsync(Guid licenseId, Listing listing, IReadOnlyDictionary<string, string> failures,
        DateTimeOffset now, CancellationToken ct)
    {
        var deletedKeys = listing.All.Where(k => !failures.ContainsKey(k)).ToHashSet(StringComparer.Ordinal);
        foreach (var prefix in new[]
                 {
                     ShopperPaymentKeys.LicensePrefix(licenseId), WhatsAppMediaKeys.LicensePrefix(licenseId),
                     BroadcastMediaKeys.PostPrefix(licenseId), BroadcastMediaKeys.ProductPhotoPrefix(licenseId),
                 })
            foreach (var row in await _db.OrphanedMediaObjects
                         .Where(o => o.DeletedAt == null && o.ObjectKey.StartsWith(prefix)).ToListAsync(ct))
            {
                if (!deletedKeys.Contains(row.ObjectKey)) continue;
                row.AttemptCount++;
                row.LastAttemptAt = now;
                row.LastError = null;
                row.DeletedAt = now;   // gerçek silme onayı
            }
    }
}
