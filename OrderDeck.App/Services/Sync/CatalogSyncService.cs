using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Shared.Text;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Sunucu kataloğunu yerel replikaya çeker.
///
/// <b>Ya hep ya hiç:</b> sayfalar tamamlanmadan replikaya hiçbir şey yazılmaz.
/// Sunucu tam anlık görüntü döndürdüğü için yazma bir <c>DELETE + INSERT</c>;
/// yarım listeyle yazsaydık, ağ ikinci sayfada koptuğunda silinmemiş yüzlerce
/// ürün "panelden silinmiş" muamelesi görürdü — ardından yayında operatörün
/// yazdığı kod yanlış ürüne eşleşirdi.
/// </summary>
public sealed class CatalogSyncService
{
    private const int PageSize = 200;

    /// <summary>
    /// 40.000 ürünlük tavan. Sunucu imleci ilerletmezse (beklenmedik bir hata)
    /// döngü sonsuza kadar dönmesin; katalogun gerçek büyüklüğü yüzler mertebesi.
    /// Tavana çarpmak <b>başarı değil</b>: elde yarım liste kalır ve o liste
    /// yazılmaz (bkz. <see cref="SyncOnceAsync"/>).
    /// </summary>
    private const int MaxPages = 200;

    /// <summary>Fotoğraf indirmede kullanılacak KİMLİKSİZ istemci adı.</summary>
    public const string PhotoClientName = "catalog-photos";

    private readonly LicenseApiClient _api;
    private readonly CatalogReplicaRepository _repo;
    private readonly CatalogPhotoCache _photos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<CatalogSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public CatalogSyncService(
        LicenseApiClient api,
        CatalogReplicaRepository repo,
        CatalogPhotoCache photos,
        IHttpClientFactory httpFactory,
        ICurrentLicenseProvider licenseProvider,
        ILogger<CatalogSyncService> log)
    {
        _api = api;
        _repo = repo;
        _photos = photos;
        _httpFactory = httpFactory;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    /// <summary>Yazılan ürün sayısı; senkron yapılamadıysa 0.</summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrEmpty(licenseKey)) return 0;

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct);
        if (licenseId is null) return 0;

        try
        {
            var pulled = new List<CatalogProductPullItem>();
            Guid? after = null;
            var complete = false;

            for (var page = 0; page < MaxPages; page++)
            {
                var batch = await _api.GetCatalogProductsAsync(licenseId.Value, after, PageSize, ct);

                // Tek güvenilir bitiş işareti BOŞ sayfa — GetCatalogProductsAsync'in
                // XML dokümanı bunu koyu yazıyor. "Kısa sayfa = son sayfa" kuralı
                // bir istek tasarruf ederdi ama PageSize sunucunun kırpma sınırını
                // (500) aştığı gün katalogu SESSİZCE kırpardı: eksik ürünler
                // yayında hiç eşleşmez ve hiçbir yerde hata görünmez.
                if (batch.Count == 0)
                {
                    complete = true;
                    break;
                }

                pulled.AddRange(batch);

                // İmleç birebir son satırın Id'si. Sayfayı yerelde YENİDEN SIRALAMA:
                // sunucu sırası SQL Server'ın uniqueidentifier karşılaştırmasından
                // geliyor, .NET'in Guid.CompareTo sırası farklı düşer ve satır atlatır.
                after = batch[^1].Id;
            }

            if (!complete)
            {
                // Tavana çarptık: elimizdeki liste yarım. Yazmak, tavandan sonraki
                // bütün ürünleri silinmiş saymak olurdu — ağın yarıda kopmasından
                // farkı yok, aynı şekilde davranıyoruz.
                _log.LogWarning(
                    "Katalog senkronu {MaxPages} sayfada bitmedi; yarım liste yazılmadı", MaxPages);
                return 0;
            }

            var categories = await _api.GetCatalogCategoriesAsync(licenseId.Value, ct);

            // Buraya geldiysek tam anlık görüntü elimizde: tek transaction'da yaz.
            _repo.Replace(
                pulled.Select(ToProduct).ToList(),
                pulled.SelectMany(ToVariants).ToList(),
                categories.Select(ToCategory).ToList());

            // Save ve Prune AYNI iş parçacığında, sırayla: CatalogPhotoCache
            // iş parçacığı güvenli değil, paralel koşarlarsa temizlik sürmekte
            // olan indirmenin .tmp dosyasını siler.
            await DownloadMissingPhotosAsync(pulled, ct);
            _photos.Prune(_repo.CoverPhotoKeys());

            _log.LogInformation(
                "Katalog senkronu: {Products} ürün, {Categories} kategori",
                pulled.Count, categories.Count);
            return pulled.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Katalog senkronu başarısız; replika olduğu gibi bırakıldı");
            return 0;
        }
    }

    private async Task DownloadMissingPhotosAsync(
        IReadOnlyList<CatalogProductPullItem> pulled, CancellationToken ct)
    {
        // İmzalı adres 5 dakika geçerli — indirme çekmenin hemen ardında.
        // Kimliksiz istemci ŞART: LicenseApiClient'ın istemcisi her isteğe
        // Authorization ekliyor ve presigned bir R2 adresine fazladan başlık
        // göndermek isteği bozar.
        var http = _httpFactory.CreateClient(PhotoClientName);

        foreach (var p in pulled)
        {
            // Boşluktan ibaret anahtar Save'i fırlatır (Has onu asla göremez,
            // Prune canlı sayar → her turda yeniden indirilen ölümsüz yetim).
            // Bu yüzden Length kontrolü değil IsNullOrWhiteSpace.
            if (string.IsNullOrWhiteSpace(p.CoverPhotoKey)) continue;

            // URL'in null gelmesi MEŞRU: sunucu R2 imzalama hatasını yutup
            // sayfayı yine 200 döndürüyor. Böyle bir turda indirmeyi atla ve
            // önbellekteki dosyayı KORU — "fotoğraf silinmiş" sayma.
            if (string.IsNullOrWhiteSpace(p.CoverPhotoUrl)) continue;

            var key = p.CoverPhotoKey;
            if (_photos.Has(key)) continue;

            try
            {
                _photos.Save(key, await http.GetByteArrayAsync(p.CoverPhotoUrl, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tek fotoğrafın düşmesi katalogu düşürmez: kart placeholder
                // gösterir, bir sonraki turda taze bir imzayla yeniden denenir.
                _log.LogDebug(ex, "Kapak fotoğrafı indirilemedi: {Key}", key);
            }
        }
    }

    private static CatalogProduct ToProduct(CatalogProductPullItem p) => new(
        p.Id.ToString("N"),
        p.CategoryId?.ToString("N"),
        p.Code,
        // Aranan iğne de aynı fonksiyondan geçiyor: sohbete "güzel elbise"
        // yazan da "GÜZEL ELBİSE" yazan da aynı ürüne düşer.
        SearchNormalizer.Normalize(p.Code),
        p.Name,
        p.DefaultPrice,
        p.ShelfLocation,
        p.Axis1Name, p.Axis1Role,
        p.Axis2Name, p.Axis2Role,
        p.CoverPhotoKey,
        // ToUnixTimeSeconds — .DateTime/.LocalDateTime DEĞİL: yerel saate
        // çevirmek tr-TR makinede sessiz veri hatası üretir.
        p.UpdatedAt.ToUnixTimeSeconds());

    private static IEnumerable<CatalogVariant> ToVariants(CatalogProductPullItem p)
        // Sıra sunucunun kararı (VariantCode'a göre, SQL Server collation'ında);
        // DTO'da SortOrder alanı yok, sıralamanın kendisi dizideki konum.
        // Yerelde VariantCode'a göre yeniden sıralamak SQLite'ın ordinal
        // karşılaştırmasıyla farklı düşerdi, o yüzden indeksi taşıyoruz.
        => p.Variants.Select((v, i) => new CatalogVariant(
            v.Id.ToString("N"), p.Id.ToString("N"),
            v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
            v.VariantCode, v.Barcode, v.IsActive, i));

    private static CatalogCategory ToCategory(CatalogCategoryPullItem c) => new(
        c.Id.ToString("N"), c.ParentCategoryId?.ToString("N"),
        c.Name, c.Path, c.SortOrder, c.IsActive);

    // ─── Lisans kimliği çözümlemesi ───────────────────────────────────────────
    // ShopperRegistrationIngestService ile birebir aynı kalıp — bilerek. İki
    // servis ayrışırsa lisans çözümleme davranışı iki farklı yerde yaşamaya başlar.

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;

            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Katalog senkronu için lisans çözümlenemedi");
            return null;
        }
    }
}
