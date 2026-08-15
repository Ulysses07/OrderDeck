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

    /// <summary>
    /// Tek turlu kapı (bkz. <see cref="SyncOnceAsync"/>). Servis singleton ve
    /// <c>SyncOnceAsync</c> public olduğu için "aynı iş parçacığında, sırayla"
    /// sözü tek başına bir YORUM; mekanizması bu.
    /// </summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Yalnız kapının içinde okunup yazılıyor (SyncOnceAsync → SyncCoreAsync).
    // Kilit olmasaydı iki tur bu ikiliyi yarı yazılmış hâlde görebilirdi:
    // yeni Key + eski Id, yani BAŞKA bir lisansın katalogu. SemaphoreSlim'in
    // al/bırak çifti aynı zamanda gereken bellek engelini de kuruyor.
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

    /// <summary>
    /// Yazılan ürün sayısı; senkron yapılamadıysa 0.
    ///
    /// <b>Tek turlu kapı.</b> Bir tur sürerken gelen ikinci çağrı kuyruğa
    /// ALINMAZ, anında 0 döner. İki neden:
    /// <list type="number">
    /// <item>Eşzamanlı istek gereksiz iş: süren tur zaten AYNI tam anlık
    /// görüntüyü çekiyor. Kuyruğa almak iki tam çekmeyi arka arkaya koştururdu
    /// — sunucuya iki kat yük, sonuç aynı.</item>
    /// <item><see cref="CatalogPhotoCache"/> iş parçacığı güvenli değil:
    /// <c>Prune</c>'un koruma kümesinde yalnız <c>{özet}.img</c> adları var,
    /// süren bir <c>Save</c>'in <c>.tmp</c> dosyası yok. Paralel iki tur,
    /// temizliği indirmenin üstüne sürer ve <c>File.Move</c>'u
    /// <see cref="System.IO.FileNotFoundException"/> ile düşürür.</item>
    /// </list>
    /// </summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct))
        {
            _log.LogDebug("Katalog senkronu zaten sürüyor; bu çağrı atlandı");
            return 0;
        }

        try { return await SyncCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<int> SyncCoreAsync(CancellationToken ct)
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
                //
                // Kayıt iki AYRI arızayı ayırt edilebilir yapmalı, çünkü çareleri
                // zıt: (a) katalog gerçekten tavanı aştı → MaxPages artırılmalı,
                // (b) sunucu imleci ilerletmiyor (aynı sayfayı tekrar döndürüyor)
                // → sunucu tarafı hata, MaxPages'i artırmak yalnız arızayı büyütür.
                // Ayrımı çekilen satır sayısı veriyor; son imleç de sunucu
                // tarafında sorguyu birebir tekrarlamayı sağlıyor.
                _log.LogWarning(
                    "Katalog senkronu {MaxPages} sayfada bitmedi ({PageSize} satır/sayfa); "
                  + "{Rows} satır çekildi, son imleç {Cursor}. Yarım liste YAZILMADI. "
                  + "Satır sayısı tavana (MaxPages×PageSize) yakınsa katalog gerçekten büyümüştür; "
                  + "çok daha küçükse sunucu imleci ilerletmiyor demektir.",
                    MaxPages, PageSize, pulled.Count, after);
                return 0;
            }

            var categories = await _api.GetCatalogCategoriesAsync(licenseId.Value, ct);

            // Buraya geldiysek tam anlık görüntü elimizde: tek transaction'da yaz.
            // TODO(Task 3): broadcastCodes parametresi eklenecek.
            _repo.Replace(
                pulled.Select(ToProduct).ToList(),
                pulled.SelectMany(ToVariants).ToList(),
                categories.Select(ToCategory).ToList(),
                []);

            // Save ve Prune AYNI iş parçacığında, sırayla — ve turlar arası
            // örtüşme SyncOnceAsync'teki kapıyla engelleniyor: CatalogPhotoCache
            // iş parçacığı güvenli değil, paralel koşarlarsa temizlik sürmekte
            // olan indirmenin .tmp dosyasını siler.
            await DownloadMissingPhotosAsync(pulled, ct);
            _photos.Prune(_repo.CoverPhotoKeys());

            _log.LogInformation(
                "Katalog senkronu: {Products} ürün, {Categories} kategori",
                pulled.Count, categories.Count);
            return pulled.Count;
        }
        // 2xx SÜZGECİ şart: LicenseApiUnknownException iki ayrı şeyi taşıyor —
        // (a) eşlenmemiş bir HTTP durumu (5xx gibi), (b) BAŞARILI bir yanıtın
        // çözümlenemeyen gövdesi. Yalnız (b) yükseltiliyor; (a)'yı da Error
        // yapmak, sunucu birkaç dakika 500 verdiğinde günlüğü boşuna kırmızıya
        // boyardı — o durum bir sonraki turda kendini onarır.
        catch (LicenseApiUnknownException ex)
            when (!ct.IsCancellationRequested && ex.StatusCode is >= 200 and < 300)
        {
            // Sunucu "tamam" dedi ama gövdeyi anlamadık: LicenseApiClient bunu
            // bilerek gürültülü yapıyor ("bu 'katalog boş' demek DEĞİLDİR").
            // 401 ya da ağ kopması bir sonraki turda kendini onarır, bu onarmaz
            // — sunucu şeması ya da araya giren bir katman (proxy hata sayfası,
            // WAF) bozulmuştur ve replika sessizce bayatlar. Warning yığınında
            // kaybolmasın diye Error.
            _log.LogError(ex,
                "Katalog senkronu BOZUK GÖVDE nedeniyle düştü; replika bayat kalıyor "
              + "(bu 'katalog boş' demek değildir)");
            return 0;
        }
        // Filtre "iptal değilse" DEĞİL, "token iptal edilmediyse": HttpClient'ın
        // zaman aşımı TaskCanceledException olarak çıkıyor ve o da bir
        // OperationCanceledException. Tür bakan bir filtre onu dışarı kaçırır
        // ve zaman aşımı SESSİZ bir başarısızlığa dönüşürdü:
        // CatalogSyncHostedService kaçan OperationCanceledException'ı kayıt
        // düşmeden yutuyor (RunRoundAsync: "catch (OperationCanceledException)
        // { return false; }"). Döngü ölmez, ama aşağıdaki Warning hiç yazılmaz
        // ve tur "yazmadı" sayılır: replika henüz dolmadıysa 30 saniyelik
        // açılış ritmi 5 dakikaya hiç oturmaz, üstelik arıza günlükte görünmez.
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            _log.LogWarning(ex, "Katalog senkronu başarısız; replika olduğu gibi bırakıldı");
            return 0;
        }
    }

    private async Task DownloadMissingPhotosAsync(
        IReadOnlyList<CatalogProductPullItem> pulled, CancellationToken ct)
    {
        // İmzalı adres 5 dakika geçerli, ama indirmeler BURADA başlıyor: bütün
        // ürün sayfaları, kategori isteği ve Replace işleminden SONRA, üstelik
        // sırayla. Soğuk önbellekte büyük bir katalogda ilk sayfaların imzası
        // sırası gelmeden dolabilir; R2 403 döner ve aşağıdaki catch onu
        // LogDebug'a yutar.
        //
        // Bilerek düzeltmiyoruz, çünkü durum kendi kendini onarıyor: bir sonraki
        // turda önbellekte olan anahtarlar Has ile atlandığı için kuyruk her
        // turda kısalıyor, kuyruğun sonu her turda daha erken başlıyor ve
        // önbellek birkaç tur içinde yakınsıyor. Paralel ya da sayfa-başına
        // indirmeye geçmek YASAK — CatalogPhotoCache iş parçacığı güvenli değil.
        //
        // Kimliksiz istemci ŞART: LicenseApiClient'ın istemcisi her isteğe
        // Authorization ekliyor ve presigned bir R2 adresine fazladan başlık
        // göndermek isteği bozar.
        var http = _httpFactory.CreateClient(PhotoClientName);

        foreach (var p in pulled)
        {
            // Boşluktan ibaret anahtar Save'i fırlatır (ArgumentException) ve
            // tek bir bozuk satır bütün indirme turunu düşürürdü. "Ölümsüz
            // yetim" gerekçesi ARTIK GEÇERLİ DEĞİL: Task 6'dan beri Prune boş
            // anahtarları kendi eliyor (CatalogPhotoCache.Prune) ve
            // CoverPhotoKeys() zaten SQL'de <> '' süzüyor. Geriye kalan tek
            // sebep Save'in fırlatması — o yüzden Length değil,
            // Save ile aynı ölçüt: IsNullOrWhiteSpace.
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
            // "İptal değilse" DEĞİL, "token iptal edilmediyse": HttpClient'ın
            // zaman aşımı TaskCanceledException, yani bir
            // OperationCanceledException. Türe bakan filtre onu yakalamaz;
            // istisna fotoğraf döngüsünden kaçar ve TEK bir fotoğrafın zaman
            // aşımı bütün turu başarısız yapar: kalan fotoğraflar indirilmez,
            // Prune atlanır (katalogdan düşen ürünlerin dosyaları bir tur daha
            // diskte kalır), başarı kaydı yazılmaz ve SyncOnceAsync — replika
            // az önce yazılmış olmasına rağmen — 0 döner. Sonuncusu görünür bir
            // bedel: CatalogSyncHostedService "yazan tur" görene kadar 30
            // saniyelik açılış ritminde kalır, 5 dakikaya oturmaz. Aynı tuzağı
            // LicenseApiClient de kapatıyor (GetExpectingJsonAsync:
            // TaskCanceledException → LicenseApiNetworkException).
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                // Tek fotoğrafın düşmesi katalogu düşürmez: kart placeholder
                // gösterir, bir sonraki turda taze bir imzayla yeniden denenir.
                _log.LogDebug(ex, "Kapak fotoğrafı indirilemedi: {Key}", key);
            }
        }
    }

    // NOT: p.NameSearch tel modelinde geliyor ama replikada karşılığı YOK ve
    // bilerek düşürülüyor — yerel eşleştirme ürün ADI üzerinden değil KOD
    // üzerinden yapılıyor (CodeNormalized), sohbete yazılan şey ürün kodu.
    // Ada göre arama istenirse iş göç + kolon işidir (bkz. 025_catalog_replica.sql),
    // burada sessizce eklenecek bir alan değil.
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
        // Sıra sunucunun kararı; DTO'da SortOrder alanı yok, sıralamanın
        // kendisi dizideki konum. TODO(Task 3): tam implementasyon.
        => p.Variants.Select((v, i) => new CatalogVariant(
            v.Id.ToString("N"), p.Id.ToString("N"),
            v.Axis1Value, v.Axis2Value, v.Barcode, v.IsActive, i));

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
        // Buradaki filtre bilerek TÜRE bakıyor (yukarıdaki ikisi token'a):
        // tek çağrı LicenseApiClient'tan geçiyor ve orası zaman aşımını zaten
        // LicenseApiNetworkException'a çeviriyor (LicenseApiClient:
        // "catch (TaskCanceledException) when (!ct.IsCancellationRequested)"),
        // yani buraya ulaşan bir OperationCanceledException ancak GERÇEK iptal
        // olabilir. Yine de yeni bir çağrı eklenirse önce bu satır gözden
        // geçirilmeli.
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Katalog senkronu için lisans çözümlenemedi");
            return null;
        }
    }
}
