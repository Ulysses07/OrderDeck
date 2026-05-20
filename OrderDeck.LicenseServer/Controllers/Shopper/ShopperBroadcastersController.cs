using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Shopper-facing broadcaster (yayıncı) endpoint'leri. CodeLookup anonim;
/// diğerleri (join/leave) Bearer-Shopper gerektirir.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/broadcasters")]
public sealed class ShopperBroadcastersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperBroadcastersController(LicenseDbContext db) => _db = db;

    public sealed record CodeLookupResponse(Guid LicenseId, string DisplayName);

    public sealed record JoinRequest(string BroadcasterCode, string Platform, string Username);
    public sealed record JoinResponse(BroadcasterSummary[] Broadcasters);

    private Guid? GetShopperId()
    {
        var sub = User.FindFirst("sub")?.Value ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        return Guid.TryParse(sub, out var id) ? id : null;
    }

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

    // ── POST /api/v1/shopper/broadcasters/join ────────────────────────────────

    [HttpPost("join")]
    public async Task<IActionResult> Join([FromBody] JoinRequest req, CancellationToken ct)
    {
        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        // 2. Load shopper
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 3. Validate fields
        if (string.IsNullOrWhiteSpace(req.BroadcasterCode) ||
            string.IsNullOrWhiteSpace(req.Platform) ||
            string.IsNullOrWhiteSpace(req.Username))
            return Problem(title: "invalid-input", statusCode: 400);

        // 4. Lookup license by ShopperCode
        var normalizedCode = req.BroadcasterCode.Trim().ToLowerInvariant();
        var license = await _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.ShopperCode == normalizedCode)
            .FirstOrDefaultAsync(ct);
        if (license is null)
            return Problem(title: "invalid-code", statusCode: 404);

        // 5. Check active link for (ShopperId, LicenseId)
        var existingActiveLink = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == license.Id && l.LeftAt == null, ct);
        if (existingActiveLink is not null)
            return Problem(title: "already-linked", statusCode: 409);

        // 6. Match WpfCustomerProjection
        var platformNorm = req.Platform.Trim().ToLowerInvariant();
        var usernameNorm = req.Username.Trim();
        var wpfMatch = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == license.Id &&
                        p.Platform == platformNorm &&
                        p.Username == usernameNorm)
            .FirstOrDefaultAsync(ct);

        // 7. Insert new link
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId.Value,
            LicenseId = license.Id,
            Platform = platformNorm,
            Username = usernameNorm,
            WpfCustomerId = wpfMatch?.Id,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        _db.ShopperBroadcasterLinks.Add(link);

        // 8. SaveChanges
        await _db.SaveChangesAsync(ct);

        // 9. Load all active links → BroadcasterSummary array
        var broadcasters = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Select(l => new BroadcasterSummary(l.LicenseId, l.License.Customer.Name, l.Platform, l.Username))
            .ToArrayAsync(ct);

        // 10. Return 200
        return Ok(new JoinResponse(broadcasters));
    }

    // ── DELETE /api/v1/shopper/broadcasters/{licenseId} ───────────────────────

    [HttpDelete("{licenseId:guid}")]
    public async Task<IActionResult> Leave(Guid licenseId, CancellationToken ct)
    {
        // 1. Parse shopperId from claims
        var shopperId = GetShopperId();
        if (shopperId is null) return Unauthorized();

        // 2. Load shopper
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        // 3. Find active link
        var link = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId && l.LeftAt == null, ct);
        if (link is null)
            return Problem(title: "not-linked", statusCode: 404);

        // 4. Soft leave
        link.LeftAt = DateTimeOffset.UtcNow;

        // 5. SaveChanges
        await _db.SaveChangesAsync(ct);

        // 6. Return 204
        return NoContent();
    }
}
