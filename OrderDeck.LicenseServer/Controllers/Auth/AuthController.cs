using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailConfirmationService _confirm;
    private readonly JwtTokenService _jwt;
    private readonly PasswordResetService _resetService;

    public AuthController(LicenseDbContext db, PasswordHasher hasher,
        EmailConfirmationService confirm, JwtTokenService jwt, PasswordResetService resetService)
    {
        _db = db;
        _hasher = hasher;
        _confirm = confirm;
        _jwt = jwt;
        _resetService = resetService;
    }

    public sealed record RegisterRequest(string Email, string Name, string Password);

    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) ||
            string.IsNullOrWhiteSpace(req.Name) ||
            string.IsNullOrWhiteSpace(req.Password))
            return Problem(title: "missing-fields", statusCode: 400);

        if (req.Password.Length < 8)
            return Problem(title: "password-too-short", detail: "En az 8 karakter olmalı.", statusCode: 400);

        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (existing is not null) return StatusCode(202);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            Name = req.Name,
            PasswordHash = _hasher.Hash(req.Password),
            EmailConfirmedAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(201);
    }

    [HttpGet("confirm-email/{token:guid}")]
    public async Task<IActionResult> ConfirmEmail(Guid token, CancellationToken ct)
    {
        var ok = await _confirm.ConsumeAsync(token, ct);
        if (!ok) return Problem(title: "token-invalid", statusCode: 400);
        return Ok(new { ok = true });
    }

    public sealed record ResendRequest(string Email);

    [HttpPost("resend-confirmation")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email)) return StatusCode(202);

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (customer is null) return StatusCode(202);
        if (customer.EmailConfirmedAt is not null) return StatusCode(202);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(202);
    }

    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (customer is null || !_hasher.Verify(customer.PasswordHash, req.Password))
            return Problem(title: "invalid-credentials", statusCode: 401);

        if (customer.EmailConfirmedAt is null)
            return Problem(title: "email-not-confirmed", statusCode: 403);

        var (token, expiresAt) = _jwt.IssueCustomerToken(customer.Id, customer.Email);
        return Ok(new LoginResponse(token, expiresAt));
    }

    public sealed record PasswordResetRequestBody(string Email);

    [HttpPost("password-reset-request")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> PasswordResetRequest([FromBody] PasswordResetRequestBody req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email)) return StatusCode(202);
        await _resetService.RequestResetAsync(req.Email, ct);
        return StatusCode(202);
    }

    public sealed record PasswordResetCompleteRequest(Guid Token, string NewPassword);

    [HttpPost("password-reset")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> PasswordResetComplete([FromBody] PasswordResetCompleteRequest req, CancellationToken ct)
    {
        var result = await _resetService.CompleteResetAsync(req.Token, req.NewPassword, ct);
        return result switch
        {
            PasswordResetResult.Success => NoContent(),
            PasswordResetResult.PasswordTooShort => Problem(title: "password-too-short", detail: "En az 8 karakter olmalı.", statusCode: 400),
            _ => Problem(title: "token-invalid", statusCode: 400)
        };
    }
}
