using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Customers;

[ApiController]
[Route("api/v1/admin/customers")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminCustomersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public AdminCustomersController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.Customers
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                id = c.Id,
                email = c.Email,
                name = c.Name,
                emailConfirmedAt = c.EmailConfirmedAt,
                createdAt = c.CreatedAt,
                licenseCount = c.Licenses.Count
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers
            .Include(x => x.Licenses)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            email = c.Email,
            name = c.Name,
            emailConfirmedAt = c.EmailConfirmedAt,
            createdAt = c.CreatedAt,
            notes = c.Notes,
            licenses = c.Licenses.Select(l => new
            {
                id = l.Id,
                licenseKey = l.LicenseKey,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = l.RevokedAt
            })
        });
    }

    public sealed record CreateRequest(string Email, string Name, string? InitialPassword, bool? AutoConfirm);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-fields", statusCode: 400);

        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (existing is not null) return Conflict(new { error = "email-exists" });

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            Name = req.Name,
            PasswordHash = _hasher.Hash(req.InitialPassword ?? Guid.NewGuid().ToString("N")),
            EmailConfirmedAt = (req.AutoConfirm ?? false) ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = customer.Id }, new { id = customer.Id });
    }

    [HttpPost("{id:guid}/confirm-email")]
    public async Task<IActionResult> ConfirmEmail(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.EmailConfirmedAt is null)
        {
            c.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}
