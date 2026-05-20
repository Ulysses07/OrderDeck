using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Shopper-facing broadcaster (yayıncı) endpoint'leri. CodeLookup anonim;
/// diğerleri (join/leave) Bearer-Shopper gerektirir (Faz 0b-2'de eklenir).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/broadcasters")]
public sealed class ShopperBroadcastersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperBroadcastersController(LicenseDbContext db) => _db = db;

    public sealed record CodeLookupResponse(Guid LicenseId, string DisplayName);

    [AllowAnonymous]
    [HttpGet("code-lookup")]
    public async Task<IActionResult> CodeLookup([FromQuery] string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Problem(title: "empty-code", statusCode: 400);

        var normalized = code.Trim().ToLowerInvariant();
        var license = await _db.Licenses
            .Where(l => l.ShopperCode == normalized)
            .Select(l => new { l.Id, CustomerName = l.Customer.Name })
            .FirstOrDefaultAsync(ct);
        if (license is null) return NotFound();

        return Ok(new CodeLookupResponse(license.Id, license.CustomerName));
    }
}
