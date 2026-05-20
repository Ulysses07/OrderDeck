using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.ShopperCode;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Müşteri (shopper) app davet kodu yönetimi. Yayıncı mobile panel'den veya
/// WPF'ten ayarlayabilir. Multi-license tenant'larda en son IssuedAt'lı License
/// kullanılır (MVP — multi-license edge case ileri faz).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[Route("api/panel/shopper-code")]
public sealed class PanelShopperCodeController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly IShopperCodeValidator _validator;

    public PanelShopperCodeController(LicenseDbContext db, IShopperCodeValidator validator)
    {
        _db = db; _validator = validator;
    }

    public sealed record ShopperCodeResponse(
        string? Code,
        DateTimeOffset? UpdatedAt,
        DateTimeOffset? CanChangeAt,
        Guid LicenseId);

    public sealed record SetShopperCodeRequest(string Code);

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var license = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .OrderByDescending(l => l.IssuedAt)
            .Select(l => new { l.Id, l.ShopperCode, l.ShopperCodeUpdatedAt })
            .FirstOrDefaultAsync(ct);

        if (license is null)
            return Problem(title: "no-license", statusCode: 404);

        return Ok(BuildResponse(license.Id, license.ShopperCode, license.ShopperCodeUpdatedAt));
    }

    [HttpPut]
    public async Task<IActionResult> Set([FromBody] SetShopperCodeRequest req, CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var license = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .OrderByDescending(l => l.IssuedAt)
            .FirstOrDefaultAsync(ct);

        if (license is null)
            return Problem(title: "no-license", statusCode: 404);

        var result = await _validator.ValidateAsync(req.Code, license.Id, license.ShopperCodeUpdatedAt, ct);
        if (!result.IsValid)
            return Problem(title: result.ErrorCode!, statusCode: 400);

        license.ShopperCode = req.Code.Trim().ToLowerInvariant();
        license.ShopperCodeUpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(BuildResponse(license.Id, license.ShopperCode, license.ShopperCodeUpdatedAt));
    }

    private static ShopperCodeResponse BuildResponse(Guid licenseId, string? code, DateTimeOffset? updatedAt)
    {
        var canChangeAt = updatedAt?.AddDays(7);
        return new ShopperCodeResponse(code, updatedAt, canChangeAt, licenseId);
    }
}
