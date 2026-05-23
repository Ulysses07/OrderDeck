using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Shopper kendi profil + bağlı yayıncılar yönetimi. Tüm endpoint'ler
/// Bearer-Shopper gerektirir.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/me")]
public sealed class ShopperMeController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperMeController(LicenseDbContext db) => _db = db;

    public sealed record NotificationPrefs(bool Broadcast, bool Orders, bool Payments);

    public sealed record MeResponse(
        Guid Id,
        string FullName,
        string Phone,
        string Address,
        string? Email,
        string? Tc,
        NotificationPrefs NotificationPrefs,
        BroadcasterSummary[] Broadcasters);

    public sealed record PatchMeRequest(
        string? FullName,
        string? Address,
        string? Email,
        string? Tc,
        NotificationPrefs? NotificationPrefs);

    // ── GET /api/v1/shopper/me ────────────────────────────────────────────────

    [HttpGet]
    public async Task<IActionResult> GetMe(CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        var broadcasters = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Select(l => new BroadcasterSummary(l.LicenseId, l.License.Customer.Name, l.Platform, l.Username))
            .ToArrayAsync(ct);

        return Ok(new MeResponse(
            shopper.Id, shopper.FullName, shopper.Phone, shopper.Address,
            shopper.Email, shopper.Tc,
            new NotificationPrefs(
                shopper.NotificationsEnabledBroadcast,
                shopper.NotificationsEnabledOrders,
                shopper.NotificationsEnabledPayments),
            broadcasters));
    }

    // ── PATCH /api/v1/shopper/me ──────────────────────────────────────────────

    [HttpPatch]
    public async Task<IActionResult> PatchMe([FromBody] PatchMeRequest req, CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        if (!ModelState.IsValid) return ValidationProblem(ModelState);

        if (req.FullName is not null)
        {
            var name = req.FullName.Trim();
            if (name.Length < 1 || name.Length > 200)
                return Problem(title: "invalid-name", statusCode: 400);
            shopper.FullName = name;
        }

        if (req.Address is not null)
        {
            var address = req.Address.Trim();
            if (address.Length < 1 || address.Length > 500)
                return Problem(title: "invalid-address", statusCode: 400);
            shopper.Address = address;
        }

        if (req.Email is not null)
        {
            var email = req.Email.Trim().ToLowerInvariant();
            if (!System.Text.RegularExpressions.Regex.IsMatch(email, @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
                return Problem(title: "invalid-email", statusCode: 400);
            shopper.Email = email;
        }

        if (req.Tc is not null)
        {
            if (!IsValidTckn(req.Tc))
                return Problem(title: "invalid-tc", statusCode: 400);
            shopper.Tc = req.Tc;
        }

        if (req.NotificationPrefs is not null)
        {
            shopper.NotificationsEnabledBroadcast = req.NotificationPrefs.Broadcast;
            shopper.NotificationsEnabledOrders = req.NotificationPrefs.Orders;
            shopper.NotificationsEnabledPayments = req.NotificationPrefs.Payments;
        }

        shopper.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var broadcasters = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Select(l => new BroadcasterSummary(l.LicenseId, l.License.Customer.Name, l.Platform, l.Username))
            .ToArrayAsync(ct);

        return Ok(new MeResponse(
            shopper.Id, shopper.FullName, shopper.Phone, shopper.Address,
            shopper.Email, shopper.Tc,
            new NotificationPrefs(
                shopper.NotificationsEnabledBroadcast,
                shopper.NotificationsEnabledOrders,
                shopper.NotificationsEnabledPayments),
            broadcasters));
    }

    // ── GET /api/v1/shopper/me/broadcasters ──────────────────────────────────

    public sealed record BroadcastersResponse(BroadcasterSummary[] Broadcasters);

    [HttpGet("broadcasters")]
    public async Task<IActionResult> GetBroadcasters(CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        var broadcasters = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Select(l => new BroadcasterSummary(l.LicenseId, l.License.Customer.Name, l.Platform, l.Username))
            .ToArrayAsync(ct);

        return Ok(new BroadcastersResponse(broadcasters));
    }

    // ── DELETE /api/v1/shopper/me ─────────────────────────────────────────────

    [HttpDelete]
    public async Task<IActionResult> DeleteMe(CancellationToken ct)
    {
        var shopperId = User.GetShopperId();
        if (shopperId is null) return Unauthorized();

        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Id == shopperId && s.DeletedAt == null, ct);
        if (shopper is null) return Unauthorized();

        var now = DateTimeOffset.UtcNow;

        shopper.DeletedAt = now;
        shopper.UpdatedAt = now;

        // Revoke all active refresh tokens
        var refreshTokens = await _db.ShopperRefreshTokens
            .Where(t => t.ShopperId == shopperId && t.RevokedAt == null)
            .ToListAsync(ct);
        foreach (var token in refreshTokens)
            token.RevokedAt = now;

        // Leave all active broadcaster links
        var activeLinks = await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .ToListAsync(ct);
        foreach (var link in activeLinks)
            link.LeftAt = now;

        await _db.SaveChangesAsync(ct);

        return NoContent();
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static bool IsValidTckn(string tc)
    {
        if (tc.Length != 11) return false;
        if (!tc.All(char.IsDigit)) return false;
        if (tc[0] == '0') return false;

        var digits = tc.Select(c => c - '0').ToArray();
        // 10th digit check: ((sum of 1,3,5,7,9 * 7) - (sum of 2,4,6,8)) mod 10
        var oddSum = digits[0] + digits[2] + digits[4] + digits[6] + digits[8];
        var evenSum = digits[1] + digits[3] + digits[5] + digits[7];
        var d10 = ((oddSum * 7) - evenSum) % 10;
        if (d10 < 0) d10 += 10;
        if (d10 != digits[9]) return false;
        // 11th digit check: sum of first 10 mod 10
        var sumFirst10 = digits.Take(10).Sum();
        if ((sumFirst10 % 10) != digits[10]) return false;
        return true;
    }
}
