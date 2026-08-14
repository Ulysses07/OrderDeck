using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF'in yerel katalog kopyasını beslediği uç. <b>Tam anlık görüntü</b>,
/// artımlı değil.
///
/// Neden artımlı değil: panelden ürün ve varyant silinebiliyor; <c>since</c>
/// imleci silmeleri hiç göremez ve WPF'te hayalet satır bırakır — o satır da
/// yayında yanlış ürüne eşleşir. Katalog lisans başına yüzler mertebesinde
/// olduğu için tam sayfalı çekme hem ucuz hem kendini onarıcı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/catalog")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfCatalogPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;
    private readonly ILogger<LicensesWpfCatalogPullController> _log;

    public LicensesWpfCatalogPullController(
        LicenseDbContext db,
        IBroadcastMediaStorage storage,
        ILogger<LicensesWpfCatalogPullController> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public sealed record CatalogVariantDto(
        Guid Id,
        string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode,
        bool IsActive);

    public sealed record CatalogCategoryDto(
        Guid Id,
        Guid? ParentCategoryId,
        string Name,
        string Path,
        int SortOrder,
        bool IsActive);

    /// <param name="Id">Ürün birincil anahtarı.</param>
    /// <param name="CategoryId">Bağlı kategori; kategorisiz ürünlerde null.</param>
    /// <param name="Code">Ürün kodu.</param>
    /// <param name="Name">Görünen ad.</param>
    /// <param name="NameSearch">Arama için normalleştirilmiş ad (büyük harf, Türkçe karakterler ASCII'ye dönüştürülmüş).</param>
    /// <param name="DefaultPrice">Varsayılan satış fiyatı.</param>
    /// <param name="ShelfLocation">Raf konumu; girilmemişse null.</param>
    /// <param name="Axis1Name">Birinci boyut adı (örn. "Beden").</param>
    /// <param name="Axis1Role">Birinci boyut rolü; yoksa null.</param>
    /// <param name="Axis2Name">İkinci boyut adı; yoksa null.</param>
    /// <param name="Axis2Role">İkinci boyut rolü; yoksa null.</param>
    /// <param name="UpdatedAt">Son değişiklik zaman damgası.</param>
    /// <param name="CoverPhotoKey">
    /// Kapak fotoğrafının R2 nesne anahtarı (en küçük <c>SortOrder</c>);
    /// fotoğraf yoksa null. <b>Önbellek anahtarı budur</b> — URL değil.
    /// </param>
    /// <param name="CoverPhotoUrl">
    /// Aynı nesne için <b>5 dakika</b> geçerli imzalı indirme adresi.
    /// Saklanmamalı; çekme döngüsünün hemen ardından indirilip
    /// <c>CoverPhotoKey</c> altına önbelleklenmeli.
    /// </param>
    /// <param name="Variants">Ürüne ait varyantlar; varyantsız ürün yoktur.</param>
    public sealed record CatalogProductDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        string NameSearch,
        decimal DefaultPrice,
        string? ShelfLocation,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        DateTimeOffset UpdatedAt,
        string? CoverPhotoKey,
        string? CoverPhotoUrl,
        List<CatalogVariantDto> Variants);

    /// <summary>
    /// Ürün kataloğunun sayfalı anlık görüntüsü. Sayfalama <b>Id üstünde keyset</b>:
    /// <c>OrderBy(Id).Where(Id &gt; after)</c>. Offset kullanılmıyor — sayfalar
    /// arasında araya giren bir kayıt satır atlatırdı.
    /// </summary>
    /// <param name="licenseId">Katalogu çekilecek lisans.</param>
    /// <param name="after">Son alınan ürünün Id'si; ilk sayfada verilmez.</param>
    /// <param name="take">Varsayılan 200, üst sınır 500.</param>
    /// <param name="ct">İstek iptal jetonu.</param>
    [HttpGet("products")]
    public async Task<IActionResult> Products(
        Guid licenseId,
        [FromQuery] Guid? after,
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);

        var q = _db.Products
            .Where(p => p.LicenseId == licenseId && !p.IsArchived);
        if (after is not null)
            q = q.Where(p => p.Id.CompareTo(after.Value) > 0);

        // Maliyet (Cost) bilerek dışarda: WPF'in eşleştirme ve kart gösterimi
        // için gerekmiyor, kâr hesabı panelde yapılıyor.
        var rows = await q
            .OrderBy(p => p.Id)
            .Take(take)
            .Select(p => new CatalogProductDto(
                p.Id, p.CategoryId, p.Code, p.Name, p.NameSearch,
                p.DefaultPrice, p.ShelfLocation,
                p.Axis1Name, p.Axis1Role == null ? null : (int?)p.Axis1Role,
                p.Axis2Name, p.Axis2Role == null ? null : (int?)p.Axis2Role,
                p.UpdatedAt,
                // Kapak = en küçük SortOrder (ayrı IsCover bayrağı bilerek yok).
                p.Photos.OrderBy(x => x.SortOrder)
                        .Select(x => x.ObjectKey).FirstOrDefault(),
                null,  // CoverPhotoUrl — ikinci aşamada, materyalizasyondan sonra imzalanarak doldurulur.
                // GEÇİCİ UYUM KALKANI — plan 2/3'te kaldırılacak.
                //
                // Sunucudaki VariantCode/Axis*Code kolonları kalktı ama WPF
                // replikasında VariantCode hâlâ NOT NULL ve tel modelinde
                // nullable değil. Alanı ürünün stok koduyla dolduruyoruz:
                // WPF bu değeri YALNIZ iki eksen değeri de boşken gösteriyor
                // (CatalogVariantViewModel: Display = label ?? VariantCode) ve
                // o satırlar zaten BuildAutoVariant ile product.Code taşıyordu
                // — davranış birebir aynı.
                //
                // Axis1Code/Axis2Code artık gönderilmiyor; iki tarafta da
                // nullable olduğu için JSON'da yoklukları sorunsuz.
                p.Variants
                    .OrderBy(v => v.Axis1ValueNorm)
                    .ThenBy(v => v.Axis2ValueNorm)
                    .Select(v => new CatalogVariantDto(
                        v.Id, v.Axis1Value, null,
                        v.Axis2Value, null,
                        p.Code, v.Barcode, v.IsActive))
                    .ToList()))
            .ToListAsync(ct);

        // İmzalama YEREL bir HMAC, ağ çağrısı değil — sayfa başına en çok 500
        // imza mikrosaniyeler sürer. Yine de fotoğrafsız ürünler atlanıyor.
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].CoverPhotoKey is not { Length: > 0 } key) continue;

            // İmzalama hatası sayfayı düşürmesin: CoverPhotoUrl null kalır, döngü
            // devam eder, uç yine 200 döner. Hata yutulmuyor — ürün verisi ve
            // CoverPhotoKey akar; istemci elindeki önbelleği korur. Alternatifi
            // R2 kimlik bilgisi eksik bir sunucuda bütün kataloğun kurulamamasıdır.
            try
            {
                rows[i] = rows[i] with
                {
                    CoverPhotoUrl = await _storage.CreateDownloadUrlAsync(key, ct)
                };
            }
            // Yalnız GERÇEK iptal fırlatılır. Koşulsuz bir OperationCanceledException
            // dalı, depolama bir gün ağ çağrısı yapmaya başlarsa (R2 zaman aşımı →
            // ct iptal edilmemişken TaskCanceledException) sayfayı yine düşürürdü.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Kapak fotoğrafı imzalanamadı, CoverPhotoUrl null bırakıldı. Key={Key}", key);
            }
        }

        return Ok(rows);
    }

    /// <summary>
    /// Kategori ağacının tamamı. <b>Sayfalama yok</b>: derinlik
    /// <c>CatalogLimits.CategoryMaxDepth</c> ile sınırlı ve ağaç lisans başına
    /// onlar mertebesinde — sayfalamak, çözdüğünden çok karmaşıklık getirirdi.
    ///
    /// Pasif kategoriler de dönüyor: ürün pasif bir kategoriye bağlı kalmış
    /// olabilir ve WPF'te adının kaybolması "kategori yok" gibi görünürdü.
    /// Sıralama <c>Path</c> üstünde — ata her zaman çocuğundan önce gelir.
    /// </summary>
    [HttpGet("categories")]
    public async Task<IActionResult> Categories(Guid licenseId, CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var rows = await _db.Categories
            .Where(c => c.LicenseId == licenseId)
            .OrderBy(c => c.Path)
            .Select(c => new CatalogCategoryDto(
                c.Id, c.ParentCategoryId, c.Name, c.Path, c.SortOrder, c.IsActive))
            .ToListAsync(ct);

        return Ok(rows);
    }
}
