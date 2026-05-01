using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Audit;
using OrderDeck.LicenseServer.Services.Backup;

namespace OrderDeck.LicenseServer.Controllers.Backups;

[ApiController]
[Route("api/v1/me/backups")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class MeBackupsController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly BackupRetentionService _retention;
    private readonly IAuditService _audit;
    private readonly IS3BackupSink _s3;
    private readonly Microsoft.Extensions.Options.IOptions<BackupOptions> _opt;
    private readonly ILogger<MeBackupsController> _log;

    public MeBackupsController(
        LicenseDbContext db,
        BackupStorageService storage,
        BackupRetentionService retention,
        IAuditService audit,
        IS3BackupSink s3,
        Microsoft.Extensions.Options.IOptions<BackupOptions> opt,
        ILogger<MeBackupsController> log)
    {
        _db = db;
        _storage = storage;
        _retention = retention;
        _audit = audit;
        _s3 = s3;
        _opt = opt;
        _log = log;
    }

    private Guid CustomerId =>
        Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? User.FindFirstValue("sub")
                ?? throw new InvalidOperationException("Missing sub claim"));

    [HttpPost]
    [EnableRateLimiting("backup-upload")]
    public async Task<IActionResult> Upload(CancellationToken ct)
    {
        var sha = Request.Headers["X-Backup-Sha256"].ToString();
        if (string.IsNullOrWhiteSpace(sha) || sha.Length != 64)
            return BadRequest(new { error = "X-Backup-Sha256 header required (64 hex chars)" });

        var maxBytes = _opt.Value.MaxBlobSizeMb * 1024L * 1024L;
        using var ms = new MemoryStream();
        await Request.Body.CopyToAsync(ms, ct);
        if (ms.Length > maxBytes)
            return StatusCode(StatusCodes.Status413PayloadTooLarge,
                new { error = $"Backup exceeds {_opt.Value.MaxBlobSizeMb} MB limit" });

        var bytes = ms.ToArray();
        var actualSha = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        if (!string.Equals(actualSha, sha, StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "SHA256 mismatch — body integrity check failed" });

        // Per-customer storage quota. Retention prunes oldest non-milestones to 5,
        // but a customer accumulating monthly milestones for years could still drift
        // past a sane budget. Reject up-front instead of after the encrypted blob
        // is on disk so we don't waste IO writing something we'd just delete.
        var quotaMb = _opt.Value.PerCustomerQuotaMb;
        if (quotaMb > 0)
        {
            var existingBytes = await _db.CustomerBackups
                .Where(b => b.CustomerId == CustomerId)
                .SumAsync(b => (long?)b.SizeBytes, ct) ?? 0L;
            var quotaBytes = quotaMb * 1024L * 1024L;
            // ms.Length is plaintext; encrypted is ms.Length + 28 (nonce+tag). Use
            // ms.Length as a close-enough estimate — over-counting is fine, we'd
            // rather reject borderline cases than blow the cap.
            if (existingBytes + ms.Length > quotaBytes)
            {
                return StatusCode(StatusCodes.Status507InsufficientStorage,
                    new { error = $"Per-customer backup quota exceeded ({quotaMb} MB). Delete older backups via /api/v1/me/backups/{{id}}." });
            }
        }

        var (encrypted, keyVersion) = _storage.Encrypt(bytes);
        var blobPath = await _storage.WriteBlobAsync(CustomerId, encrypted, ct);

        var backup = new CustomerBackup
        {
            Id = Guid.NewGuid(),
            CustomerId = CustomerId,
            BlobPath = blobPath,
            SizeBytes = encrypted.Length,
            ChecksumSha256 = actualSha,
            CreatedAt = DateTimeOffset.UtcNow,
            IsMonthlyMilestone = false,
            UserAgent = Request.Headers["User-Agent"].ToString(),
            MachineName = Request.Headers["X-Machine-Name"].ToString(),
            KeyVersion = keyVersion
        };
        _db.CustomerBackups.Add(backup);
        await _db.SaveChangesAsync(ct);

        await _retention.EnforceAfterInsertAsync(CustomerId, backup.Id, ct);

        // Re-load to capture milestone flag (retention may have set it)
        var saved = await _db.CustomerBackups.FindAsync(new object[] { backup.Id }, ct);

        // Off-host replication (Phase 5b). Fire-and-forget when BestEffort=true
        // so the customer's POST doesn't wait on cross-region S3 latency. Sink
        // is a no-op when Backup:S3:Enabled=false.
        if (_s3.IsEnabled)
        {
            var customerIdCopy = CustomerId;
            var blobPathCopy = blobPath;
            _ = Task.Run(async () =>
            {
                try { await _s3.UploadAsync(blobPathCopy, customerIdCopy); }
                catch (Exception ex) { _log.LogError(ex, "S3 replication failed for {Path}", blobPathCopy); }
            });
        }

        await _audit.LogAsync(BackupAuditEvents.BackupCreated,
            BackupAuditEvents.TargetType,
            backup.Id.ToString(),
            new { sizeBytes = encrypted.Length, isMonthlyMilestone = saved!.IsMonthlyMilestone },
            ct);

        return StatusCode(StatusCodes.Status201Created, new
        {
            id = saved.Id,
            sizeBytes = saved.SizeBytes,
            createdAt = saved.CreatedAt,
            isMonthlyMilestone = saved.IsMonthlyMilestone
        });
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var list = await _db.CustomerBackups
            .Where(b => b.CustomerId == CustomerId)
            .OrderByDescending(b => b.CreatedAt)
            .Select(b => new
            {
                id = b.Id,
                sizeBytes = b.SizeBytes,
                createdAt = b.CreatedAt,
                isMonthlyMilestone = b.IsMonthlyMilestone,
                machineName = b.MachineName
            })
            .ToListAsync(ct);
        return Ok(list);
    }

    [HttpGet("{id:guid}/download")]
    public async Task<IActionResult> Download(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        var encrypted = await _storage.ReadBlobAsync(b.BlobPath, ct);
        var plaintext = _storage.Decrypt(encrypted, b.KeyVersion);
        return File(plaintext, "application/octet-stream", $"orderdeck-backup-{b.CreatedAt:yyyyMMdd-HHmmss}.zip");
    }

    [HttpDelete("{id:guid}")]
    [EnableRateLimiting("backup-delete")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var b = await _db.CustomerBackups
            .FirstOrDefaultAsync(x => x.Id == id && x.CustomerId == CustomerId, ct);
        if (b is null) return NotFound();

        _storage.DeleteBlob(b.BlobPath);
        _db.CustomerBackups.Remove(b);
        await _db.SaveChangesAsync(ct);

        await _audit.LogAsync(BackupAuditEvents.BackupDeleted,
            BackupAuditEvents.TargetType, id.ToString(),
            new { reason = "manual" }, ct);

        return NoContent();
    }
}
