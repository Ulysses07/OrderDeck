using System.IO.Compression;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Audit;

namespace OrderDeck.LicenseServer.Controllers.Compliance;

/// <summary>
/// KVKK / GDPR right-to-data-portability. Authenticated customer downloads a
/// ZIP of every data item we hold about them: profile, licenses, activations,
/// audit log entries, email logs, backup metadata. Encrypted backup blobs
/// themselves are NOT bundled — that's already a separate flow (/me/backups
/// download per row); we'd just inflate the ZIP unnecessarily.
/// </summary>
[ApiController]
[Route("api/v1/me/export")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class MeDataExportController : ControllerBase
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly LicenseDbContext _db;
    private readonly IAuditService _audit;

    public MeDataExportController(LicenseDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    private Guid CustomerId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? throw new InvalidOperationException("Missing sub claim"));

    [HttpGet]
    public async Task<IActionResult> Export(CancellationToken ct)
    {
        var customer = await _db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == CustomerId, ct);
        if (customer is null) return NotFound();

        // Pull everything into memory first — exports are tiny (single customer's
        // history) and ZipArchive needs random access.
        var licenses = await _db.Licenses.AsNoTracking()
            .Where(l => l.CustomerId == customer.Id).ToListAsync(ct);
        var licenseIds = licenses.Select(l => l.Id).ToList();
        var activations = await _db.Activations.AsNoTracking()
            .Where(a => licenseIds.Contains(a.LicenseId)).ToListAsync(ct);
        var emailLogs = await _db.EmailLogs.AsNoTracking()
            .Where(e => e.CustomerId == customer.Id).ToListAsync(ct);
        var backups = await _db.CustomerBackups.AsNoTracking()
            .Where(b => b.CustomerId == customer.Id).ToListAsync(ct);
        var auditEntries = await _db.AuditLogs.AsNoTracking()
            .Where(a => a.TargetType == "customer" && a.TargetId == customer.Id.ToString())
            .ToListAsync(ct);

        // Reduce to projections so internal columns (PasswordHash, ConfirmationTokenHash)
        // never leak. BlobPath also stays internal — exposing the on-disk path adds
        // nothing for the customer.
        var profileExport = new
        {
            customer.Id, customer.Email, customer.Name, customer.CreatedAt,
            customer.EmailConfirmedAt, customer.Unsubscribed
        };
        var licensesExport = licenses.Select(l => new
        {
            l.LicenseKey, l.SkuCode, l.IssuedAt, l.ExpiresAt,
            l.RevokedAt, l.RevokeReason, l.ActivationSlots
        });
        var activationsExport = activations.Select(a => new
        {
            a.Id, a.LicenseId, a.MachineName, a.ActivatedAt, a.LastSeenAt, a.DeactivatedAt
            // HardwareFingerprint deliberately omitted — it's a hash of customer
            // PII (machine guid + SID) and the customer doesn't need their own
            // fingerprint to identify their devices. MachineName covers the UX side.
        });
        var emailLogsExport = emailLogs.Select(e => new
        {
            e.TemplateKey, e.ContextKey, e.SentAt, e.Error
        });
        var backupsExport = backups.Select(b => new
        {
            b.Id, b.CreatedAt, b.SizeBytes, b.ChecksumSha256, b.IsMonthlyMilestone, b.MachineName
        });
        var auditExport = auditEntries.Select(a => new
        {
            a.Id, a.OccurredAt, a.EventType, a.AdminUsername, a.Details
        });

        // Build the ZIP in a MemoryStream so we can set Content-Length on the response.
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            await WriteJsonEntryAsync(zip, "customer.json", profileExport);
            await WriteJsonEntryAsync(zip, "licenses.json", licensesExport);
            await WriteJsonEntryAsync(zip, "activations.json", activationsExport);
            await WriteJsonEntryAsync(zip, "email-logs.json", emailLogsExport);
            await WriteJsonEntryAsync(zip, "backups.json", backupsExport);
            await WriteJsonEntryAsync(zip, "audit-log.json", auditExport);
            await WriteJsonEntryAsync(zip, "_export-info.json", new
            {
                exportedAt = DateTimeOffset.UtcNow,
                schemaVersion = 1,
                note = "KVKK / GDPR data portability export. Encrypted backup blobs " +
                       "are NOT included — download them individually via /api/v1/me/backups/{id}/download."
            });
        }

        await _audit.LogCustomerEventAsync(customer.Id, customer.Email,
            AuditEvents.CustomerDataExported, AuditTargets.Customer, customer.Id.ToString(),
            details: null, ipAddress: HttpContext.Connection.RemoteIpAddress?.ToString(), ct: ct);

        var bytes = ms.ToArray();
        return File(bytes, "application/zip",
            $"orderdeck-data-{customer.Id:N}-{DateTimeOffset.UtcNow:yyyyMMdd}.zip");
    }

    private static async Task WriteJsonEntryAsync(ZipArchive zip, string entryName, object payload)
    {
        var entry = zip.CreateEntry(entryName, CompressionLevel.Optimal);
        await using var stream = entry.Open();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload, JsonOpts));
        await stream.WriteAsync(bytes);
    }
}
