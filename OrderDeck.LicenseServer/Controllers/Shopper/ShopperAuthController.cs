using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

// ── DTOs ──────────────────────────────────────────────────────────────────────

public sealed record BroadcasterSummary(Guid LicenseId, string DisplayName, string Platform, string Username);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid ShopperId,
    BroadcasterSummary[] Broadcasters);

public sealed record RefreshResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt);

// ── Controller ────────────────────────────────────────────────────────────────

/// <summary>
/// Shopper (müşteri app) kimlik doğrulama endpointleri.
/// Register + Login anonim (AllowAnonymous); Refresh de anonim çünkü
/// access token gerekmez.
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/auth")]
public sealed class ShopperAuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _passwordHasher;
    private readonly JwtTokenService _jwt;
    private readonly ShopperRefreshTokenService _refresh;

    public ShopperAuthController(
        LicenseDbContext db,
        PasswordHasher passwordHasher,
        JwtTokenService jwt,
        ShopperRefreshTokenService refresh)
    {
        _db = db;
        _passwordHasher = passwordHasher;
        _jwt = jwt;
        _refresh = refresh;
    }

    // ── Register ──────────────────────────────────────────────────────────────

    public sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email,
        string? Tc);

    [AllowAnonymous]
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        // 1. Basic field validation
        if (string.IsNullOrWhiteSpace(req.BroadcasterCode) ||
            string.IsNullOrWhiteSpace(req.FullName) ||
            string.IsNullOrWhiteSpace(req.Phone) ||
            string.IsNullOrWhiteSpace(req.Password) ||
            string.IsNullOrWhiteSpace(req.Address) ||
            string.IsNullOrWhiteSpace(req.Platform) ||
            string.IsNullOrWhiteSpace(req.Username))
            return Problem(title: "missing-fields", statusCode: 400);

        // 2. Phone normalization
        if (!PhoneNormalizer.TryNormalize(req.Phone, out var phone))
            return Problem(title: "invalid-phone", statusCode: 400);

        // 3. Password strength
        if (req.Password.Length < 8)
            return Problem(title: "weak-password", statusCode: 400);

        // 4. Lookup license by broadcaster code (lowercase)
        var normalizedCode = req.BroadcasterCode.Trim().ToLowerInvariant();
        var license = await _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.ShopperCode == normalizedCode)
            .FirstOrDefaultAsync(ct);
        if (license is null)
            return Problem(title: "invalid-code", statusCode: 404);

        // 5. Find or create Shopper by phone
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Phone == phone, ct);

        if (shopper is not null)
        {
            // Phone already exists — verify password
            if (!_passwordHasher.Verify(shopper.PasswordHash, req.Password))
                return Problem(title: "phone-already-used", statusCode: 401);
            // Correct password → reuse shopper
        }
        else
        {
            // Create new Shopper
            var now = DateTimeOffset.UtcNow;
            shopper = new Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = req.FullName,
                Phone = phone!,
                PasswordHash = _passwordHasher.Hash(req.Password),
                Address = req.Address,
                Email = req.Email,
                Tc = req.Tc,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _db.Shoppers.Add(shopper);
        }

        // 6. Check if ShopperBroadcasterLink already exists
        var existingLink = await _db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopper.Id && l.LicenseId == license.Id, ct);
        if (existingLink is not null)
            return Problem(title: "already-linked", statusCode: 409);

        // 7. Match WpfCustomerProjection by (LicenseId, Platform, Username)
        var platformNorm = req.Platform.Trim().ToLowerInvariant();
        var usernameNorm = req.Username.Trim();
        var wpfMatch = await _db.WpfCustomerProjections
            .Where(p => p.LicenseId == license.Id &&
                        p.Platform == platformNorm &&
                        p.Username == usernameNorm)
            .FirstOrDefaultAsync(ct);

        // 8. Insert link
        var link = new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopper.Id,
            LicenseId = license.Id,
            Platform = platformNorm,
            Username = usernameNorm,
            WpfCustomerId = wpfMatch?.Id,
            JoinedAt = DateTimeOffset.UtcNow,
        };
        _db.ShopperBroadcasterLinks.Add(link);
        await _db.SaveChangesAsync(ct);

        // 9. & 10. Issue tokens
        var (accessToken, accessExpiresAt) = _jwt.IssueShopperToken(shopper.Id, phone!);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (refreshRaw, refreshExpiresAt) = await _refresh.IssueAsync(shopper.Id, ip, ct);

        // 11. Load all active links for the shopper
        var broadcasters = await BuildBroadcastersAsync(shopper.Id, ct);

        // 12. Return 201
        return StatusCode(201, new AuthResponse(
            accessToken, accessExpiresAt,
            refreshRaw, refreshExpiresAt,
            shopper.Id,
            broadcasters));
    }

    // ── Login ─────────────────────────────────────────────────────────────────

    public sealed record LoginRequest(string Phone, string Password);

    [AllowAnonymous]
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        // 1. Phone normalization
        if (!PhoneNormalizer.TryNormalize(req.Phone, out var phone))
            return Problem(title: "invalid-phone", statusCode: 400);

        // 2. Find shopper
        var shopper = await _db.Shoppers
            .FirstOrDefaultAsync(s => s.Phone == phone, ct);
        if (shopper is null || shopper.DeletedAt is not null)
            return Problem(title: "invalid-credentials", statusCode: 401);

        // 3. Verify password
        if (!_passwordHasher.Verify(shopper.PasswordHash, req.Password))
            return Problem(title: "invalid-credentials", statusCode: 401);

        // 4. Issue tokens
        var (accessToken, accessExpiresAt) = _jwt.IssueShopperToken(shopper.Id, phone!);
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        var (refreshRaw, refreshExpiresAt) = await _refresh.IssueAsync(shopper.Id, ip, ct);

        // 5. Load active links
        var broadcasters = await BuildBroadcastersAsync(shopper.Id, ct);

        // 6. Return 200
        return Ok(new AuthResponse(
            accessToken, accessExpiresAt,
            refreshRaw, refreshExpiresAt,
            shopper.Id,
            broadcasters));
    }

    // ── Refresh ───────────────────────────────────────────────────────────────

    public sealed record RefreshRequest(string RefreshToken);

    [AllowAnonymous]
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();

        // 1. Rotate token
        var rotated = await _refresh.RotateAsync(req.RefreshToken ?? string.Empty, ip, ct);
        if (rotated is null)
            return Problem(title: "invalid-refresh-token", statusCode: 401);

        var (shopperId, newRefreshRaw, newRefreshExpiresAt) = rotated.Value;

        // 2. Get shopper (verify not deleted)
        var shopper = await _db.Shoppers.FirstOrDefaultAsync(s => s.Id == shopperId, ct);
        if (shopper is null || shopper.DeletedAt is not null)
            return Problem(title: "invalid-refresh-token", statusCode: 401);

        // 3. Issue new access token
        var (newAccessToken, newAccessExpiresAt) = _jwt.IssueShopperToken(shopper.Id, shopper.Phone);

        // 4. Return 200 with RefreshResponse
        return Ok(new RefreshResponse(
            newAccessToken, newAccessExpiresAt,
            newRefreshRaw, newRefreshExpiresAt));
    }

    // ── Shared helper ─────────────────────────────────────────────────────────

    private async Task<BroadcasterSummary[]> BuildBroadcastersAsync(Guid shopperId, CancellationToken ct)
    {
        return await _db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .Include(l => l.License)
            .ThenInclude(lic => lic.Customer)
            .Select(l => new BroadcasterSummary(
                l.LicenseId,
                l.License.Customer.Name,
                l.Platform,
                l.Username))
            .ToArrayAsync(ct);
    }
}
