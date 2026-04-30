using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/admin/auth")]
public sealed class AdminAuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly JwtTokenService _jwt;

    public AdminAuthController(LicenseDbContext db, PasswordHasher hasher, JwtTokenService jwt)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var admin = await _db.AdminUsers.FirstOrDefaultAsync(a => a.Username == req.Username, ct);
        if (admin is null || !_hasher.Verify(admin.PasswordHash, req.Password))
            return Problem(title: "invalid-credentials", statusCode: 401);

        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var (token, expiresAt) = _jwt.IssueAdminToken(admin.Id, admin.Username);
        return Ok(new LoginResponse(token, expiresAt));
    }
}
