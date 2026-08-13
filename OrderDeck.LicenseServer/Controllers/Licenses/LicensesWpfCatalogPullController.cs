using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF'in yerel katalog kopyasını beslediği uç. <b>Tam anlık görüntü</b>,
/// artımlı değil.
///
/// Neden artımlı değil: panelden ürün ve varyant silinebiliyor; <c>since</c>
/// imleci silmeleri hiç göremez ve WPF'te hayalet satır bırakır — o satır da
/// yayında yanlış ürüne eşleşir. Katalog lisans başına yüzler mertebesinde
/// olduğu için tam sayfalı çekme hem ucuz hem kendini onarıcı.
///
/// Sayfalama <b>Id üstünde keyset</b>: <c>OrderBy(Id).Where(Id > after)</c>.
/// Offset kullanılmıyor — sayfalar arasında araya giren bir kayıt satır
/// atlatırdı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/catalog")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfCatalogPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesWpfCatalogPullController(LicenseDbContext db) => _db = db;

    public sealed record CatalogVariantDto(
        Guid Id,
        string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode,
        bool IsActive);

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
        List<CatalogVariantDto> Variants);

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
                p.Variants
                    .OrderBy(v => v.VariantCode)
                    .Select(v => new CatalogVariantDto(
                        v.Id, v.Axis1Value, v.Axis1Code,
                        v.Axis2Value, v.Axis2Code,
                        v.VariantCode, v.Barcode, v.IsActive))
                    .ToList()))
            .ToListAsync(ct);

        return Ok(rows);
    }
}
