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
/// Ürün fotoğrafı (Faz 1a). İki adımlı presigned yükleme: baytlar panelden
/// doğrudan R2'ye gider, sunucu yalnız anahtarı doğrular ve kaydeder.
///
/// Sunucu baytları görmediği için <b>küçültme yapamaz</b>; panel yüklemeden
/// önce küçültür, sunucu sınırı uygular.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/photo")]
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
    // PhotoContentType'a karşılık gelen bir DTO alanı yok: o değer istemciden
    // değil R2'nin HeadAsync yanıtından geliyor ve zaten izin listesindeki üç
    // MIME türünden biri olmak zorunda.
    public sealed record AttachRequest(
        [MaxLength(CatalogLimits.PhotoObjectKey)] string ObjectKey,
        int? Width,
        int? Height);
    public sealed record PhotoDto(
        string ObjectKey, string ContentType, long SizeBytes, int? Width, int? Height);
    public sealed record PhotoUrlDto(string Url);

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

        var objectKey = Prefix(licenseId.Value, productId) + Guid.NewGuid().ToString("N") + ".img";
        var url = await _storage.CreateUploadUrlAsync(objectKey, req.ContentType, req.SizeBytes, ct);

        return Ok(new UploadUrlDto(objectKey, url));
    }

    [AllowStockStaff]
    [HttpPut]
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

        var previousKey = product.PhotoObjectKey;

        product.PhotoObjectKey = key;
        product.PhotoContentType = info.ContentType;
        product.PhotoSizeBytes = info.SizeBytes;
        product.PhotoWidth = req.Width;
        product.PhotoHeight = req.Height;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (previousKey is not null && previousKey != key)
            await _storage.DeleteAsync(previousKey, ct);

        return Ok(new PhotoDto(key, info.ContentType, info.SizeBytes, req.Width, req.Height));
    }

    [AllowStockStaff]
    [HttpGet("url")]
    public async Task<IActionResult> GetUrl(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product?.PhotoObjectKey is null) return NotFound();

        var url = await _storage.CreateDownloadUrlAsync(product.PhotoObjectKey, ct);
        return Ok(new PhotoUrlDto(url));
    }

    [AllowStockStaff]
    [HttpDelete]
    public async Task<IActionResult> Delete(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var key = product.PhotoObjectKey;
        if (key is null) return NoContent();

        product.PhotoObjectKey = null;
        product.PhotoContentType = null;
        product.PhotoSizeBytes = null;
        product.PhotoWidth = null;
        product.PhotoHeight = null;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _storage.DeleteAsync(key, ct);
        return NoContent();
    }

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
