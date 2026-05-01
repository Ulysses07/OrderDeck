using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Backup;
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
    private readonly BackupStorageService _backups;
    private readonly IAuditService _audit;

    public AdminCustomersController(LicenseDbContext db, PasswordHasher hasher,
        BackupStorageService backups, IAuditService audit)
    {
        _db = db;
        _hasher = hasher;
        _backups = backups;
        _audit = audit;
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

    public sealed record PurgeRequest(string ConfirmEmail);

    /// <summary>
    /// KVKK / GDPR right-to-be-forgotten. Deletes the customer's operational
    /// data (licenses, activations, refresh tokens, intake form submissions,
    /// email logs, backup blobs + metadata) and anonymises the Customer row
    /// itself. The Customer row stays — its Id is referenced from AuditLog
    /// rows that we MUST retain for the configured retention window. Email
    /// + Name are blanked, PasswordHash is set to a sentinel that no hash
    /// can ever match, EmailConfirmedAt is cleared.
    ///
    /// Caller must echo the customer's current email in the request body to
    /// guard against fat-finger purges. Audit row is written with the
    /// pre-purge email so the action remains traceable.
    /// </summary>
    [HttpPost("{id:guid}/purge")]
    public async Task<IActionResult> Purge(Guid id, [FromBody] PurgeRequest req, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return NotFound();
        if (!string.Equals(customer.Email, req.ConfirmEmail, StringComparison.OrdinalIgnoreCase))
            return Problem(title: "email-mismatch",
                detail: "Body confirmEmail must match the customer's current email.",
                statusCode: 400);

        var preEmail = customer.Email;

        // 1. Delete encrypted backup blobs from the filesystem before nuking
        //    the rows that point to them — otherwise we'd lose the path.
        var backupRows = await _db.CustomerBackups
            .Where(b => b.CustomerId == id).ToListAsync(ct);
        foreach (var b in backupRows) _backups.DeleteBlob(b.BlobPath);

        // 2. Hard-delete dependent rows. FK cascade would handle Activations
        //    via Licenses, but doing it explicitly keeps the operation
        //    self-documenting and survives any future cascade changes.
        var licenseIds = await _db.Licenses.Where(l => l.CustomerId == id)
            .Select(l => l.Id).ToListAsync(ct);
        if (licenseIds.Count > 0)
        {
            _db.Activations.RemoveRange(_db.Activations.Where(a => licenseIds.Contains(a.LicenseId)));
            _db.Licenses.RemoveRange(_db.Licenses.Where(l => l.CustomerId == id));
        }
        _db.CustomerBackups.RemoveRange(backupRows);
        _db.EmailLogs.RemoveRange(_db.EmailLogs.Where(e => e.CustomerId == id));
        _db.RefreshTokens.RemoveRange(_db.RefreshTokens.Where(r => r.CustomerId == id));
        _db.PasswordResetTokens.RemoveRange(_db.PasswordResetTokens.Where(p => p.CustomerId == id));
        _db.EmailConfirmationTokens.RemoveRange(_db.EmailConfirmationTokens.Where(t => t.CustomerId == id));
        // Intake form submissions reference IntakeFormConfig (not Customer directly).
        // Drop the customer's config + all submissions that fed into it.
        var configIds = await _db.IntakeFormConfigs
            .Where(c => c.CustomerId == id)
            .Select(c => c.Id)
            .ToListAsync(ct);
        if (configIds.Count > 0)
        {
            _db.IntakeFormSubmissions.RemoveRange(
                _db.IntakeFormSubmissions.Where(s => configIds.Contains(s.IntakeFormConfigId)));
            _db.IntakeFormConfigs.RemoveRange(_db.IntakeFormConfigs.Where(c => c.CustomerId == id));
        }

        // 3. Anonymise the Customer row. Email is moved to a deterministic
        //    placeholder so AuditLog rows still pivot off the same Id but
        //    PII is gone. PasswordHash is set to a value no Argon2 hash can
        //    match (length is invalid for the encoding) so the account can
        //    never be logged into again.
        customer.Email = $"purged-{customer.Id:N}@deleted.invalid";
        customer.Name = "[Deleted]";
        customer.PasswordHash = "PURGED";
        customer.EmailConfirmedAt = null;
        customer.Unsubscribed = true;
        customer.Notes = null;

        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(AuditEvents.CustomerPurged, AuditTargets.Customer, id.ToString(),
            details: new
            {
                preEmail,
                deletedLicenses = licenseIds.Count,
                deletedBackups = backupRows.Count
            }, ct: ct);

        return NoContent();
    }
}
