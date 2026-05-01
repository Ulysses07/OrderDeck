using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: enforces retention policy after each backup insert.
/// - First backup of any calendar month is marked IsMonthlyMilestone=true (preserved indefinitely).
/// - Non-milestones trimmed to 5 most recent (older deleted from DB + filesystem).
/// Per-customer SemaphoreSlim serializes concurrent uploads.
/// </summary>
public sealed class BackupRetentionService
{
    private const int MaxNonMilestones = 5;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _customerLocks = new();

    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly ILogger<BackupRetentionService> _log;

    public BackupRetentionService(LicenseDbContext db, BackupStorageService storage, ILogger<BackupRetentionService> log)
    {
        _db = db;
        _storage = storage;
        _log = log;
    }

    public async Task EnforceAfterInsertAsync(Guid customerId, Guid newBackupId, CancellationToken ct = default)
    {
        var sem = _customerLocks.GetOrAdd(customerId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await EnforceCoreAsync(customerId, newBackupId, ct);
        }
        finally
        {
            sem.Release();
        }
    }

    private async Task EnforceCoreAsync(Guid customerId, Guid newBackupId, CancellationToken ct)
    {
        var newBackup = await _db.CustomerBackups.FindAsync(new object[] { newBackupId }, ct);
        if (newBackup is null) return;

        // Step 1: first-of-month milestone marker
        var monthStart = new DateTimeOffset(newBackup.CreatedAt.Year, newBackup.CreatedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEnd = monthStart.AddMonths(1);
        var existingThisMonth = await _db.CustomerBackups
            .Where(b => b.CustomerId == customerId
                     && b.CreatedAt >= monthStart
                     && b.CreatedAt < monthEnd
                     && b.Id != newBackupId)
            .AnyAsync(ct);

        if (!existingThisMonth)
        {
            newBackup.IsMonthlyMilestone = true;
            await _db.SaveChangesAsync(ct);
        }

        // Step 2: trim non-milestones to MaxNonMilestones most recent
        var nonMilestones = await _db.CustomerBackups
            .Where(b => b.CustomerId == customerId && !b.IsMonthlyMilestone)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        if (nonMilestones.Count > MaxNonMilestones)
        {
            var toDelete = nonMilestones.Skip(MaxNonMilestones).ToList();
            foreach (var old in toDelete)
            {
                _storage.DeleteBlob(old.BlobPath);
                _db.CustomerBackups.Remove(old);
            }
            await _db.SaveChangesAsync(ct);
            _log.LogInformation("Retention trimmed {Count} backups for customer {CustomerId}",
                toDelete.Count, customerId);
        }
    }
}
