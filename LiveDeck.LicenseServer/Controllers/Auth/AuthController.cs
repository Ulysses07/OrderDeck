using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailConfirmationService _confirm;

    public AuthController(LicenseDbContext db, PasswordHasher hasher, EmailConfirmationService confirm)
    {
        _db = db;
        _hasher = hasher;
        _confirm = confirm;
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

        // Enumeration koruması: zaten varsa sessizce 202 dön (yeni email yollanmaz)
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
        // Enumeration koruması: kullanıcı yoksa veya zaten confirmed ise sessiz 202
        if (customer is null) return StatusCode(202);
        if (customer.EmailConfirmedAt is not null) return StatusCode(202);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(202);
    }
}
