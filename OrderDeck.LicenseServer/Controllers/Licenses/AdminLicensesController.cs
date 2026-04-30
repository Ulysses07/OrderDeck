using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

[ApiController]
[Route("api/v1/admin/licenses")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminLicensesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly LicenseIssuer _issuer;
    private readonly AdminActionEmailService _adminEmail;

    public AdminLicensesController(LicenseDbContext db, LicenseIssuer issuer, AdminActionEmailService adminEmail)
    {
        _db = db;
        _issuer = issuer;
        _adminEmail = adminEmail;
    }

    public sealed record IssueRequest(string CustomerEmail, string SkuCode,
        int? DurationDaysOverride, int? SlotsOverride);

    [HttpPost]
    public async Task<IActionResult> Issue([FromBody] IssueRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _issuer.IssueAsync(
                new(req.CustomerEmail, req.SkuCode, req.DurationDaysOverride, req.SlotsOverride), ct);
            var customerId = await _db.Customers.Where(c => c.Email == req.CustomerEmail).Select(c => c.Id).FirstAsync(ct);
            await _adminEmail.NotifyLicenseIssuedAsync(customerId, result.LicenseKey, req.SkuCode, result.ExpiresAt, ct);
            return CreatedAtAction(nameof(Get), new { key = result.LicenseKey },
                new { licenseKey = result.LicenseKey, expiresAt = result.ExpiresAt });
        }
        catch (LicenseIssuer.IssueException ex)
        {
            return Problem(title: ex.Code, detail: ex.Message, statusCode: 400);
        }
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        var l = await _db.Licenses
            .Include(x => x.Customer)
            .Include(x => x.Activations)
            .FirstOrDefaultAsync(x => x.LicenseKey == key, ct);
        if (l is null) return NotFound();

        return Ok(new
        {
            id = l.Id,
            licenseKey = l.LicenseKey,
            customerEmail = l.Customer.Email,
            skuCode = l.SkuCode,
            activationSlots = l.ActivationSlots,
            issuedAt = l.IssuedAt,
            expiresAt = l.ExpiresAt,
            revokedAt = l.RevokedAt,
            revokeReason = l.RevokeReason,
            activations = l.Activations.Select(a => new
            {
                id = a.Id,
                hardwareFingerprint = a.HardwareFingerprint,
                machineName = a.MachineName,
                activatedAt = a.ActivatedAt,
                lastSeenAt = a.LastSeenAt,
                deactivatedAt = a.DeactivatedAt
            })
        });
    }

    public sealed record RevokeRequest(string Reason);

    [HttpPost("{key}/revoke")]
    public async Task<IActionResult> Revoke(string key, [FromBody] RevokeRequest req, CancellationToken ct)
    {
        var l = await _db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, ct);
        if (l is null) return NotFound();
        l.RevokedAt = DateTimeOffset.UtcNow;
        l.RevokeReason = req.Reason;
        await _db.SaveChangesAsync(ct);
        await _adminEmail.NotifyLicenseRevokedAsync(l.CustomerId, l.LicenseKey, req.Reason, ct);
        return NoContent();
    }

    public sealed record ExtendRequest(int AdditionalDays);

    [HttpPost("{key}/extend")]
    public async Task<IActionResult> Extend(string key, [FromBody] ExtendRequest req, CancellationToken ct)
    {
        if (req.AdditionalDays <= 0) return Problem(title: "invalid-days", statusCode: 400);
        var l = await _db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, ct);
        if (l is null) return NotFound();
        l.ExpiresAt = l.ExpiresAt.AddDays(req.AdditionalDays);
        await _db.SaveChangesAsync(ct);
        await _adminEmail.NotifyLicenseExtendedAsync(l.CustomerId, l.LicenseKey, l.ExpiresAt, req.AdditionalDays, ct);
        return Ok(new { newExpiresAt = l.ExpiresAt });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? customer, [FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.Licenses.Include(l => l.Customer).AsQueryable();
        if (!string.IsNullOrEmpty(customer))
            q = q.Where(l => l.Customer.Email.Contains(customer));
        if (status == "revoked") q = q.Where(l => l.RevokedAt != null);
        else if (status == "expired") q = q.Where(l => l.RevokedAt == null && l.ExpiresAt < DateTimeOffset.UtcNow);
        else if (status == "active") q = q.Where(l => l.RevokedAt == null && l.ExpiresAt >= DateTimeOffset.UtcNow);

        var rows = await q
            .OrderByDescending(l => l.IssuedAt)
            .Select(l => new
            {
                licenseKey = l.LicenseKey,
                customerEmail = l.Customer.Email,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = l.RevokedAt
            })
            .ToListAsync(ct);
        return Ok(rows);
    }
}
