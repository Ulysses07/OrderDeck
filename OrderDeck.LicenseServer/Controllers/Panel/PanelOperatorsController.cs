using System.Security.Claims;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayıncı ekibinin staff hesaplarını yönetir. Owner Customer kendi License'ına
/// staff ekleyebilir, listeleyebilir ve silebilir.
///
/// Faz 1 (2026-05-14): sadece entity + CRUD. Staff hesapları henüz login
/// akışında kullanılmaz — Faz 2'de auth refactor ile aktive olacak.
/// </summary>
[ApiController]
[Route("api/panel/operators")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelOperatorsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public PanelOperatorsController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    public sealed record InviteRequest(string Email, string Name, string Password);
    public sealed record OperatorDto(
        Guid Id,
        Guid LicenseId,
        string Email,
        string Name,
        string Role,
        DateTimeOffset CreatedAt,
        DateTimeOffset? LastLoginAt,
        DateTimeOffset? RevokedAt);

    [HttpPost]
    public async Task<IActionResult> Invite([FromBody] InviteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) ||
            string.IsNullOrWhiteSpace(req.Name) ||
            string.IsNullOrWhiteSpace(req.Password))
            return Problem(title: "missing-fields", statusCode: 400);

        if (req.Password.Length < 8)
            return Problem(title: "weak-password",
                detail: "Şifre en az 8 karakter olmalı.", statusCode: 400);

        var customerId = GetCustomerId();
        var licenseId = await ResolveLicenseAsync(customerId, ct);
        if (licenseId is null)
            return Problem(title: "no-license",
                detail: "Önce lisans aktivasyonu yapmalısın.", statusCode: 400);

        // Aynı license + email tekrarına izin verme.
        var duplicate = await _db.OperatorUsers
            .AnyAsync(o => o.LicenseId == licenseId.Value && o.Email == req.Email, ct);
        if (duplicate)
            return Problem(title: "email-exists", statusCode: 409);

        var op = new OperatorUser
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            Email = req.Email.Trim().ToLowerInvariant(),
            Name = req.Name.Trim(),
            PasswordHash = _hasher.Hash(req.Password),
            Role = "staff",
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.OperatorUsers.Add(op);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/panel/operators/{op.Id}", ToDto(op));
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var licenseIds = await _db.Licenses
            .Where(l => l.CustomerId == customerId)
            .Select(l => l.Id)
            .ToListAsync(ct);

        var rows = await _db.OperatorUsers
            .Where(o => licenseIds.Contains(o.LicenseId))
            .OrderByDescending(o => o.CreatedAt)
            .Select(o => new OperatorDto(
                o.Id, o.LicenseId, o.Email, o.Name, o.Role,
                o.CreatedAt, o.LastLoginAt, o.RevokedAt))
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var op = await _db.OperatorUsers
            .Include(o => o.License)
            .FirstOrDefaultAsync(o => o.Id == id, ct);
        if (op is null) return NotFound();
        if (op.License.CustomerId != customerId) return NotFound();   // hide existence

        _db.OperatorUsers.Remove(op);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private static OperatorDto ToDto(OperatorUser o) =>
        new(o.Id, o.LicenseId, o.Email, o.Name, o.Role,
            o.CreatedAt, o.LastLoginAt, o.RevokedAt);

    private async Task<Guid?> ResolveLicenseAsync(Guid customerId, CancellationToken ct)
    {
        // Şu an her customer'ın tipik olarak tek lisansı var; tipik kullanım
        // bunu varsayabilir. Birden fazla varsa ilk aktif olanı seçeriz.
        var now = DateTimeOffset.UtcNow;
        return await _db.Licenses
            .Where(l => l.CustomerId == customerId
                && l.RevokedAt == null
                && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
