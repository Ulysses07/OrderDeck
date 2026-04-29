using System.Security.Claims;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/me")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class MeController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public MeController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var id = GetCustomerId();
        var c = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            email = c.Email,
            name = c.Name,
            emailConfirmedAt = c.EmailConfirmedAt,
            createdAt = c.CreatedAt
        });
    }

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        if (req.NewPassword.Length < 8)
            return Problem(title: "password-too-short", statusCode: 400);

        var id = GetCustomerId();
        var c = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (c is null) return NotFound();

        if (!_hasher.Verify(c.PasswordHash, req.CurrentPassword))
            return Problem(title: "wrong-current-password", statusCode: 400);

        c.PasswordHash = _hasher.Hash(req.NewPassword);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    [HttpGet("licenses")]
    public async Task<IActionResult> GetMyLicenses(CancellationToken ct)
    {
        var id = GetCustomerId();
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.Licenses
            .Where(l => l.CustomerId == id && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderByDescending(l => l.IssuedAt)
            .Select(l => new
            {
                licenseKey = l.LicenseKey,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = (DateTimeOffset?)null
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
