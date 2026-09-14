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

        // Step 1: month milestone marker. Satırlar retention başlamadan önce
        // görünür olabilir; bu yüzden "başka satır var mı" diye yeni satıra
        // karar vermek yerine ayın deterministik en eskisini her turda onar.
        var monthStart = new DateTimeOffset(newBackup.CreatedAt.Year, newBackup.CreatedAt.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var monthEnd = monthStart.AddMonths(1);
        var monthBackups = await _db.CustomerBackups
            .Where(b => b.CustomerId == customerId
                     && b.CreatedAt >= monthStart
                     && b.CreatedAt < monthEnd)
            .OrderBy(b => b.CreatedAt)
            .ThenBy(b => b.Id)
            .ToListAsync(ct);

        var oldest = monthBackups[0];
        var milestoneChanged = false;
        foreach (var backup in monthBackups)
        {
            var shouldBeMilestone = backup.Id == oldest.Id;
            if (backup.IsMonthlyMilestone == shouldBeMilestone) continue;
            backup.IsMonthlyMilestone = shouldBeMilestone;
            milestoneChanged = true;
        }
        // Step 2: trim non-milestones to MaxNonMilestones most recent. SQL'de
        // milestone filtresi kullanma: yukarıdaki onarım henüz kaydedilmedi.
        // Tüm satırları yükleyip tracked güncel değerle filtrelemek milestone
        // onarımı ile silmeleri tek atomik SaveChanges içinde tutar.
        var customerBackups = await _db.CustomerBackups
            .Where(b => b.CustomerId == customerId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        var nonMilestones = customerBackups
            .Where(b => !b.IsMonthlyMilestone)
            .ToList();

        if (nonMilestones.Count > MaxNonMilestones)
        {
            var toDelete = nonMilestones.Skip(MaxNonMilestones).ToList();
            // ÖNCE satırlar, SONRA dosyalar. Ters sırada SaveChanges patlarsa
            // dosyaları silinmiş ama satırları duran bir yığın "hayalet yedek"
            // kalırdı — müşteri listede görür, geri yükleyemez. Bu sırada en
            // kötü ihtimalle yetim dosya kalır; BackupOrphanCleanupJob toplar.
            var blobPaths = toDelete.Select(b => b.BlobPath).ToList();
            _db.CustomerBackups.RemoveRange(toDelete);
            await _db.SaveChangesAsync(ct);
            foreach (var path in blobPaths) _storage.DeleteBlob(path);
            _log.LogInformation("Retention trimmed {Count} backups for customer {CustomerId}",
                toDelete.Count, customerId);
        }
        else if (milestoneChanged)
        {
            await _db.SaveChangesAsync(ct);
        }
    }
}
