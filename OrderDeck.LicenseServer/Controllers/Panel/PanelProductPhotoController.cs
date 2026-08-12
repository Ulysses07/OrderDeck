using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün fotoğraf galerisi (Faz 1a). İki adımlı presigned yükleme: baytlar
/// panelden doğrudan R2'ye gider, sunucu yalnız anahtarı doğrular ve
/// <see cref="ProductPhoto"/> satırı yazar.
///
/// Kapak = en küçük <see cref="ProductPhoto.SortOrder"/>. Ayrı IsCover alanı
/// bilerek yok: iki doğruluk kaynağı er geç ayrışır.
///
/// Sunucu baytları görmediği için <b>küçültme yapamaz</b>; panel yüklemeden
/// önce küçültür, sunucu sınırı uygular.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/photos")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelProductPhotoController : ControllerBase
{
    private const long MaxSizeBytes = 5 * 1024 * 1024;

    private static readonly string[] AllowedContentTypes =
        ["image/jpeg", "image/png", "image/webp"];

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;

    public PanelProductPhotoController(LicenseDbContext db, IBroadcastMediaStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public sealed record UploadUrlRequest(string ContentType, long SizeBytes);
    public sealed record UploadUrlDto(string ObjectKey, string UploadUrl);

    // Anahtarı sunucu üretiyor (~111 karakter) ama YAZILAN, istemcinin geri
    // gönderdiği anahtar; önek kontrolünden sonrası serbest uzunlukta. Kolon
    // 512 olduğu için sınır burada kapatılıyor.
    //
    // Attribute PARAMETREYE yazılır, [property:] hedefiyle değil — MVC record'un
    // birincil kurucusunu okuyor.
    //
    // ContentType istemciden değil R2'nin HeadAsync yanıtından geliyor ve zaten
    // izin listesindeki üç MIME türünden biri olmak zorunda.
    public sealed record AttachRequest(
        [MaxLength(CatalogLimits.PhotoObjectKey)] string ObjectKey,
        int? Width,
        int? Height);

    public sealed record ReorderRequest(List<Guid> Ids);

    public sealed record PhotoDto(
        Guid Id, string ObjectKey, string ContentType, long SizeBytes,
        int? Width, int? Height, int SortOrder, string Url);

    [AllowStockStaff]
    [HttpPost("upload-url")]
    public async Task<IActionResult> CreateUploadUrl(
        Guid productId, [FromBody] UploadUrlRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        if (!IsAllowed(req.ContentType)) return UnsupportedType();
        if (req.SizeBytes <= 0 || req.SizeBytes > MaxSizeBytes)
            return Problem(title: "file-too-large",
                detail: "Fotoğraf en çok 5 MB olabilir.", statusCode: 400);

        var count = await _db.ProductPhotos.CountAsync(p => p.ProductId == productId, ct);
        if (count >= CatalogLimits.MaxProductPhotos)
            return Problem(title: "photo-limit-reached",
                detail: $"Bir ürüne en çok {CatalogLimits.MaxProductPhotos} fotoğraf "
                      + "eklenebilir.", statusCode: 409);

        var objectKey = Prefix(licenseId.Value, productId) + Guid.NewGuid().ToString("N") + ".img";
        var url = await _storage.CreateUploadUrlAsync(objectKey, req.ContentType, req.SizeBytes, ct);

        return Ok(new UploadUrlDto(objectKey, url));
    }

    [AllowStockStaff]
    [HttpPost]
    public async Task<IActionResult> Attach(
        Guid productId, [FromBody] AttachRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var key = (req.ObjectKey ?? string.Empty).Trim();
        if (!key.StartsWith(Prefix(licenseId.Value, productId), StringComparison.Ordinal))
            return Problem(title: "invalid-object-key",
                detail: "Anahtar bu ürüne ait değil.", statusCode: 400);

        var info = await _storage.HeadAsync(key, ct);
        if (info is null)
            return Problem(title: "object-not-found",
                detail: "Yüklenen dosya depoda bulunamadı.", statusCode: 400);

        if (!IsAllowed(info.ContentType)) return UnsupportedType();
        if (info.SizeBytes <= 0 || info.SizeBytes > MaxSizeBytes)
            return Problem(title: "file-too-large",
                detail: "Fotoğraf en çok 5 MB olabilir.", statusCode: 400);

        var existing = await _db.ProductPhotos
            .Where(p => p.ProductId == productId)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        if (existing.Count >= CatalogLimits.MaxProductPhotos)
        {
            // Yükleme URL'i alındıktan sonra başka bir sekme sınırı doldurmuş:
            // yeni nesne artık yetim, hemen sil. Beklemek de olurdu (temizlik
            // işi 24 saat sonra alırdı) ama sebebi burada kesin biliyoruz.
            await _storage.DeleteAsync(key, ct);
            return Problem(title: "photo-limit-reached",
                detail: $"Bir ürüne en çok {CatalogLimits.MaxProductPhotos} fotoğraf "
                      + "eklenebilir.", statusCode: 409);
        }

        if (existing.Exists(p => string.Equals(p.ObjectKey, key, StringComparison.Ordinal)))
            return Problem(title: "photo-already-attached",
                detail: "Bu fotoğraf zaten ekli.", statusCode: 409);

        var photo = new ProductPhoto
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = productId,
            ObjectKey = key,
            ContentType = info.ContentType,
            SizeBytes = info.SizeBytes,
            Width = req.Width,
            Height = req.Height,
            SortOrder = existing.Count,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        _db.ProductPhotos.Add(photo);
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Created(
            $"/api/panel/products/{productId}/photos/{photo.Id}",
            await ToDtoAsync(photo, ct));
    }

    [AllowStockStaff]
    [HttpGet]
    public async Task<IActionResult> List(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var photos = await _db.ProductPhotos.AsNoTracking()
            .Where(p => p.ProductId == productId)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        var dtos = new List<PhotoDto>(photos.Count);
        foreach (var photo in photos) dtos.Add(await ToDtoAsync(photo, ct));
        return Ok(dtos);
    }

    /// <summary>
    /// Sıralamayı baştan yazar; <b>ilk id kapak olur</b>. Gelen liste ürünün
    /// bütün fotoğraflarını tam olarak bir kez içermek zorunda — eksik liste
    /// kabul edilseydi listede olmayan fotoğrafın sırası belirsiz kalır ve kapak
    /// kullanıcının görmediği bir kurala göre değişirdi.
    /// </summary>
    [AllowStockStaff]
    [HttpPut("order")]
    public async Task<IActionResult> Reorder(
        Guid productId, [FromBody] ReorderRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var photos = await _db.ProductPhotos
            .Where(p => p.ProductId == productId)
            .ToListAsync(ct);

        var ids = req.Ids ?? [];
        if (ids.Count != photos.Count
            || ids.Distinct().Count() != ids.Count
            || ids.Exists(id => photos.TrueForAll(p => p.Id != id)))
            return Problem(title: "photo-order-mismatch",
                detail: "Sıralama listesi ürünün fotoğraflarıyla birebir eşleşmeli.",
                statusCode: 400);

        for (var i = 0; i < ids.Count; i++)
            photos.First(p => p.Id == ids[i]).SortOrder = i;

        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var dtos = new List<PhotoDto>(photos.Count);
        foreach (var photo in photos.OrderBy(p => p.SortOrder))
            dtos.Add(await ToDtoAsync(photo, ct));
        return Ok(dtos);
    }

    [AllowStockStaff]
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid productId, Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var photos = await _db.ProductPhotos
            .Where(p => p.ProductId == productId)
            .OrderBy(p => p.SortOrder)
            .ToListAsync(ct);

        var photo = photos.FirstOrDefault(p => p.Id == id);
        if (photo is null) return NotFound();

        _db.ProductPhotos.Remove(photo);
        photos.Remove(photo);

        // Boşluk kapatılıyor: 0,2,3 gibi bir dizi kapak kuralını bozmaz ama
        // sonraki eklemenin SortOrder'ı (= kalan sayı) mevcut bir satırla
        // çakışırdı.
        for (var i = 0; i < photos.Count; i++) photos[i].SortOrder = i;

        product.UpdatedAt = DateTimeOffset.UtcNow;

        // DB önce, R2 sonra: ters sırada olsa ve commit düşse, kayıt var olmayan
        // bir nesneye işaret ederdi. Bu sırada en kötü ihtimal yetim nesne, onu
        // da ProductPhotoOrphanCleanupJob topluyor.
        await _db.SaveChangesAsync(ct);
        await _storage.DeleteAsync(photo.ObjectKey, ct);

        return NoContent();
    }

    private async Task<PhotoDto> ToDtoAsync(ProductPhoto p, CancellationToken ct)
        => new(p.Id, p.ObjectKey, p.ContentType, p.SizeBytes, p.Width, p.Height,
            p.SortOrder, await _storage.CreateDownloadUrlAsync(p.ObjectKey, ct));

    private static string Prefix(Guid licenseId, Guid productId)
        => $"{licenseId:N}/products/{productId:N}/";

    private static bool IsAllowed(string? contentType)
        => contentType is not null
           && AllowedContentTypes.Contains(contentType.Trim().ToLowerInvariant());

    private IActionResult UnsupportedType()
        => Problem(title: "unsupported-media-type",
            detail: "Yalnız JPEG, PNG ve WebP kabul ediliyor.", statusCode: 400);

    private Task<Product?> FindAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products.FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        return _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
