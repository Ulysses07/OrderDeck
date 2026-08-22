using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/admin/auth")]
public sealed class AdminAuthController : ControllerBase
{
    private readonly AdminLoginService _login;
    private readonly JwtTokenService _jwt;

    public AdminAuthController(AdminLoginService login, JwtTokenService jwt)
    {
        _login = login;
        _jwt = jwt;
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        // Kilit Razor sayfasıyla ORTAK servisten geliyor: iki kapı aynı hesaba
        // açılıyor, birini korumak kilidi anlamsız kılardı.
        var result = await _login.AuthenticateAsync(req.Username, req.Password, ct);
        if (result.Outcome == AdminLoginService.Outcome.LockedOut)
            return Problem(title: "account-locked", statusCode: 429);
        if (!result.IsSuccess)
            return Problem(title: "invalid-credentials", statusCode: 401);

        var admin = result.Admin!;
        var (token, expiresAt) = _jwt.IssueAdminToken(admin.Id, admin.Username);
        return Ok(new LoginResponse(token, expiresAt));
    }
}
