using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün varyantları (Faz 1a). Varyant kodu <c>ÜRÜNKODU-EKSEN1[-EKSEN2]</c>
/// biçiminde ve yalnız ASCII harf/rakam taşır — Faz 1c'nin barkot alfabesi
/// Code128 ve Code128 Türkçe harf kabul etmiyor.
///
/// Kodu tek bir yer kurar: <see cref="VariantCodeBuilder"/>. Kod türetilmiştir ve
/// ürün kodu değişince yenilenir; Faz 1c'de barkot yükü basım anında
/// <c>ProductVariant.Barcode</c>'a kopyalanıp dondurulur, okutma oradan
/// çözümlenir — yoksa yeniden adlandırma basılmış etiketleri geçersiz kılar.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/variants")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[AllowStockStaff]
public sealed class PanelProductVariantsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelProductVariantsController(LicenseDbContext db) => _db = db;

    // DİKKAT — positional record'da doğrulama attribute'u PARAMETREYE yazılır,
    // [property:] hedefiyle DEĞİL. MVC record'un birincil kurucusunu okuyor;
    // metadata property'ye taşınırsa çalışma zamanında istisna atıyor.
    //
    // Kod parçalarını ayrıca AxisCodeDeriver 4 karaktere kısaltıyor; buradaki
    // sınır kolonun kendisi (8). VariantCode istemciden GELMEZ, bu üçünden
    // türetilir ve yapı gereği 64'e sığar (bkz. CatalogLimits.VariantCode).
    public sealed record VariantRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? Axis1Value,
        [MaxLength(CatalogLimits.AxisCode)] string? Axis1Code,
        [MaxLength(CatalogLimits.AxisValue)] string? Axis2Value,
        [MaxLength(CatalogLimits.AxisCode)] string? Axis2Code,
        bool IsActive);

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid productId, [FromBody] VariantRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var built = BuildSegments(product, req, out var error);
        if (error is not null) return error;

        if (product.Variants.Any(v => v.VariantCode == built.VariantCode))
            return Duplicate(built.VariantCode);

        var now = DateTimeOffset.UtcNow;
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = built.Axis1Value,
            Axis1Code = built.Axis1Code,
            Axis2Value = built.Axis2Value,
            Axis2Code = built.Axis2Code,
            VariantCode = built.VariantCode,
            IsActive = req.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ProductVariants.Add(variant);
        product.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Created(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}", ToDto(variant));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid productId, Guid id, [FromBody] VariantRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var variant = product.Variants.FirstOrDefault(v => v.Id == id);
        if (variant is null) return NotFound();

        var built = BuildSegments(product, req, out var error);
        if (error is not null) return error;

        if (product.Variants.Any(v => v.Id != id && v.VariantCode == built.VariantCode))
            return Duplicate(built.VariantCode);

        var now = DateTimeOffset.UtcNow;
        variant.Axis1Value = built.Axis1Value;
        variant.Axis1Code = built.Axis1Code;
        variant.Axis2Value = built.Axis2Value;
        variant.Axis2Code = built.Axis2Code;
        variant.VariantCode = built.VariantCode;
        variant.IsActive = req.IsActive;
        variant.UpdatedAt = now;
        product.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(variant));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid productId, Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var variant = product.Variants.FirstOrDefault(v => v.Id == id);
        if (variant is null) return NotFound();

        _db.ProductVariants.Remove(variant);
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private readonly record struct Segments(
        string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode);

    /// <summary>
    /// Eksen değerlerini doğrular, kod parçalarını türetir ve varyant kodunu kurar.
    /// Hata varsa <paramref name="error"/> dolar; dönen değer o durumda anlamsızdır.
    /// </summary>
    private Segments BuildSegments(Product product, VariantRequest req, out IActionResult? error)
    {
        error = null;

        if (product.Axis1Name is null)
        {
            error = Problem(title: "product-has-no-axis",
                detail: "Eksensiz üründe varyant satırı elle eklenemez.", statusCode: 400);
            return default;
        }

        var axis1Value = Trim(req.Axis1Value);
        var axis2Value = Trim(req.Axis2Value);

        if (axis1Value is null || (product.Axis2Name is not null && axis2Value is null))
        {
            error = Problem(title: "missing-axis-value",
                detail: "Her eksen için bir değer girmelisin.", statusCode: 400);
            return default;
        }

        if (product.Axis2Name is null && axis2Value is not null)
        {
            error = Problem(title: "unexpected-axis-value",
                detail: "Bu ürünün ikinci ekseni yok.", statusCode: 400);
            return default;
        }

        var axis1Code = ResolveCode(req.Axis1Code, axis1Value);
        var axis2Code = axis2Value is null ? null : ResolveCode(req.Axis2Code, axis2Value);

        if (axis1Code.Length == 0 || axis2Code?.Length == 0)
        {
            error = Problem(title: "invalid-axis-code",
                detail: "Değerden ASCII kod türetilemedi; kodu elle gir.", statusCode: 400);
            return default;
        }

        var variantCode = VariantCodeBuilder.Build(product.Code, axis1Code, axis2Code);

        return new Segments(axis1Value, axis1Code, axis2Value, axis2Code, variantCode);
    }

    private IActionResult Duplicate(string variantCode)
        => Problem(title: "duplicate-variant",
            detail: $"'{variantCode}' varyantı bu üründe zaten var.", statusCode: 409);

    private static string ResolveCode(string? supplied, string displayValue)
    {
        var manual = AxisCodeDeriver.Derive(supplied);
        return manual.Length > 0 ? manual : AxisCodeDeriver.Derive(displayValue);
    }

    private Task<Product?> LoadProductAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PanelProductsController.VariantDto ToDto(ProductVariant v) => new(
        v.Id, v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
        v.VariantCode, v.Barcode, v.IsActive);

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
