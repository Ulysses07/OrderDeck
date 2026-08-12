using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Hangfire günlük işi: R2'de kalmış <b>yetim</b> ürün fotoğraflarını süpürür.
///
/// Neden var — yetim üreten dört yol var ve hiçbiri tek bir çağrı yerine yama
/// koyarak kapanmıyor:
/// <list type="number">
/// <item>Presigned URL alınıp baytlar yüklenir, sonra Attach <b>hiç</b> çağrılmaz
/// (kullanıcı dialogu iptal eder). Anahtar veritabanına HİÇ yazılmadığı için
/// DB'yi okuyarak bulunması imkânsızdır — tek yol kovayı listelemektir.</item>
/// <item>Galeriden fotoğraf çıkarılır ya da ürün silinir. Uçlar artık nesneyi
/// de siliyor ama...</item>
/// <item>...lisans cascade'iyle giden ürünler o uçtan geçmez.</item>
/// <item>Commit ile silme arasında süreç ölürse nesne yetim kalır.</item>
/// </list>
/// Bu iş, kaynağı ne olursa olsun hepsini tek noktadan toplar.
///
/// <b>YIKICI iş.</b> Yanlış kurgulanırsa canlı fotoğrafları siler. Bu yüzden
/// silme ölçütü iki koşulun BİRLİKTE sağlanmasıdır: anahtar DB'de yok
/// <i>ve</i> nesne ödemsiz süreden eski.
/// </summary>
public sealed class ProductPhotoOrphanCleanupJob
{
    /// <summary>
    /// Ödemsiz süre — zorunlu emniyet mandalı. Presigned yükleme URL'si 10 dakika
    /// yaşıyor; şu anda yüklenmiş ama henüz Attach edilmemiş bir anahtar DB'de
    /// görünmez. Süre olmasaydı iş, kullanıcı kaydet'e basmadan fotoğrafını
    /// gözünün önünde silerdi. 24 saat, en yavaş kullanıcı akışına bile fazlasıyla
    /// yeter.
    /// </summary>
    private static readonly TimeSpan GracePeriod = TimeSpan.FromHours(24);

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;
    private readonly ILogger<ProductPhotoOrphanCleanupJob> _log;

    public ProductPhotoOrphanCleanupJob(
        LicenseDbContext db, IBroadcastMediaStorage storage,
        ILogger<ProductPhotoOrphanCleanupJob> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var cutoff = DateTimeOffset.UtcNow - GracePeriod;
        var deleted = 0;

        // Kova geneli tek listeleme YAPILMIYOR, lisans lisans dolaşılıyor: aynı
        // kovada yayın gönderisi medyası da duruyor. Yalnız
        // "{licenseId:N}/products/" önekinin altına bakarak, başka hiçbir şeye
        // dokunmadığımızı sorgu mantığıyla değil YAPI gereği garantiliyoruz.
        var licenseIds = await _db.Licenses.Select(l => l.Id).ToListAsync(ct);

        foreach (var licenseId in licenseIds)
        {
            // Tek bir lisansın hatası (R2 kesintisi, yetki sorunu) işi düşürmesin;
            // kalan lisanslar temizlenmeye devam etsin.
            try
            {
                deleted += await CleanLicenseAsync(licenseId, cutoff, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Ürün fotoğrafı mutabakatı: {LicenseId} lisansı atlandı", licenseId);
            }
        }

        if (deleted == 0)
            _log.LogDebug("Ürün fotoğrafı mutabakatı: yetim nesne bulunmadı");
        else
            _log.LogInformation(
                "Ürün fotoğrafı mutabakatı: {Count} yetim nesne silindi", deleted);
    }

    private async Task<int> CleanLicenseAsync(
        Guid licenseId, DateTimeOffset cutoff, CancellationToken ct)
    {
        var prefix = $"{licenseId:N}/products/";
        var listed = await _storage.ListAsync(prefix, ct);
        if (listed.Count == 0) return 0;

        var referenced = (await _db.ProductPhotos
                .Where(p => p.LicenseId == licenseId)
                .Select(p => p.ObjectKey)
                .ToListAsync(ct))
            .ToHashSet(StringComparer.Ordinal);

        var deleted = 0;
        foreach (var obj in listed)
        {
            // İki koşul BİRLİKTE: DB'de yok VE ödemsiz süreden eski.
            if (referenced.Contains(obj.Key)) continue;
            if (obj.LastModified >= cutoff) continue;

            await _storage.DeleteAsync(obj.Key, ct);
            deleted++;
        }

        return deleted;
    }
}
