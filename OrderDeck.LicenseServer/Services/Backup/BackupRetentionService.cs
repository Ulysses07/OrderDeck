using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a: enforces retention policy after each backup insert.
/// - First backup of any calendar month is marked IsMonthlyMilestone=true.
/// - Non-milestones trimmed to 5 most recent (older deleted from DB + filesystem).
/// - Aylık yedek alındığı andan <see cref="BackupOptions.MonthlyRetentionMonths"/> ay (varsayılan 12) sonra silinir; müşterinin
///   en yeni yedeği hariç (karar 7, 23.10.2026). Her silme kişisel veri içermeyen imha kaydını (<see cref="DataDestruction"/>)
///   satırlarla aynı SaveChanges'te yazar (KVKK Ö-13). Son-5 kırpması bugünkü gibi kayıtsız. Yükleme yapmayan müşteri için
///   aynı süre kuralını <see cref="ExpireMonthliesAsync"/> (günlük <see cref="BackupMonthlyRetentionJob"/>) uygular.
/// Per-customer SemaphoreSlim serializes concurrent uploads (ve süpürmeyi yüklemelerle).
/// </summary>
public sealed class BackupRetentionService
{
    private const int MaxNonMilestones = 5;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> _customerLocks = new();

    /// <summary>İmha kaydının hedefi; yayıncı silmesinin yedek satırı da bu adla yazılır (aynı tablonun imhası tek hedefte
    /// gruplanır).</summary>
    public const string DestructionTarget = "CustomerBackups";

    private readonly LicenseDbContext _db;
    private readonly BackupStorageService _storage;
    private readonly BackupOptions _opt;
    private readonly ILogger<BackupRetentionService> _log;
    private readonly TimeProvider _clock;

    public BackupRetentionService(LicenseDbContext db, BackupStorageService storage, IOptions<BackupOptions> opt,
        ILogger<BackupRetentionService> log, TimeProvider? clock = null)
    {
        _db = db;
        _storage = storage;
        _opt = opt.Value;
        _log = log;
        _clock = clock ?? TimeProvider.System;
    }

    public Task EnforceAfterInsertAsync(Guid customerId, Guid newBackupId, CancellationToken ct = default)
        => UnderCustomerLockAsync(customerId, () => EnforceCoreAsync(customerId, newBackupId, ct), ct);

    /// <summary>
    /// Süresi dolan aylık yedekleri tüm müşterilerde siler (<see cref="ExpiredMonthlies"/>; en yeni yedek kalır). Yükleme sonrası
    /// tur yalnız yükleyen müşteride koşar: yüklemeyi bırakan (ör. ayrılan) müşterinin aylık yedekleri bu olmadan süresiz kalırdı.
    /// Yalnız süre kuralı: son-5 kırpması ve ay işareti onarımı yüklemeye bağlı kalır. Müşteri başına yükleme turuyla aynı kilit;
    /// her müşteri kendi SaveChanges'inde (satırlar + imha kaydı), sonra dosyaları. Silinen satır sayısı döner. Süre 0 ya da
    /// eksiyse hiçbir şey yapmaz.
    /// </summary>
    public async Task<int> ExpireMonthliesAsync(CancellationToken ct = default)
    {
        if (_opt.MonthlyRetentionMonths <= 0) return 0;
        var cutoff = _clock.GetUtcNow().AddMonths(-_opt.MonthlyRetentionMonths);
        var customerIds = await _db.CustomerBackups
            .Where(b => b.IsMonthlyMilestone && b.CreatedAt < cutoff)
            .Select(b => b.CustomerId)
            .Distinct()
            .ToListAsync(ct);

        var total = 0;
        foreach (var customerId in customerIds)
        {
            ct.ThrowIfCancellationRequested();
            await UnderCustomerLockAsync(customerId, async () =>
            {
                // Kilidin içinde baştan okunur: arada bir yükleme turu satırları değiştirmiş olabilir.
                var backups = await _db.CustomerBackups
                    .Where(b => b.CustomerId == customerId)
                    .OrderByDescending(b => b.CreatedAt)
                    .ToListAsync(ct);
                var expired = ExpiredMonthlies(backups);
                if (expired.Count == 0) return;
                await DeleteAsync(customerId, expired, expired.Count, ct);
                total += expired.Count;
            }, ct);
            // Müşteriler arasında izleyici boşaltılır: tüm tablo tek izleyicide birikmesin.
            _db.ChangeTracker.Clear();
        }

        if (total > 0)
            _log.LogInformation("Monthly backup retention expired {Count} backups (cutoff {Cutoff})", total, cutoff);
        return total;
    }

    private static async Task UnderCustomerLockAsync(Guid customerId, Func<Task> action, CancellationToken ct)
    {
        var sem = _customerLocks.GetOrAdd(customerId, _ => new SemaphoreSlim(1, 1));
        await sem.WaitAsync(ct);
        try
        {
            await action();
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
        var trimmed = nonMilestones.Skip(MaxNonMilestones).ToList();

        // Step 3: süresi dolan aylık yedekler (karar 7). Son-5 kuralı yalnız aylık
        // olmayanları sayar ve değişmedi: süresi dolan aylık yedek aylık olmayana
        // dönüşüp o beşten yer kapmaz, doğrudan gider. Tracked (onarılmış) bayrakla
        // seçilir, kırpmayla aynı atomik SaveChanges'e girer.
        var expired = ExpiredMonthlies(customerBackups);

        if (trimmed.Count + expired.Count > 0)
        {
            await DeleteAsync(customerId, trimmed.Concat(expired).ToList(), expired.Count, ct);
            _log.LogInformation(
                "Retention trimmed {Count} backups and expired {Expired} monthly backups for customer {CustomerId}",
                trimmed.Count, expired.Count, customerId);
        }
        else if (milestoneChanged)
        {
            await _db.SaveChangesAsync(ct);
        }
    }

    /// <summary>Alındığı andan (<see cref="CustomerBackup.CreatedAt"/>) <see cref="BackupOptions.MonthlyRetentionMonths"/> aydan
    /// ESKİ aylık yedekler; tam o sınırda alınan kalır. Müşterinin en yeni yedeği (<paramref name="newestFirst"/>'in ilki) aylık
    /// ve süresi dolmuş olsa da seçilmez: süre kuralı müşteriyi yedeksiz bırakmasın. Süre 0 ya da eksiyse boş (süresiz).</summary>
    private List<CustomerBackup> ExpiredMonthlies(List<CustomerBackup> newestFirst)
    {
        var months = _opt.MonthlyRetentionMonths;
        if (months <= 0 || newestFirst.Count == 0) return [];
        var cutoff = _clock.GetUtcNow().AddMonths(-months);
        var newestId = newestFirst[0].Id;
        return newestFirst
            .Where(b => b.IsMonthlyMilestone && b.CreatedAt < cutoff && b.Id != newestId)
            .ToList();
    }

    /// <summary>Satırları ve dosyalarını siler. <paramref name="rows"/> içindeki süresi dolan aylık yedekler
    /// (<paramref name="expiredCount"/> adet) için imha kaydı (hedef <see cref="DestructionTarget"/>, konu müşteri) aynı
    /// SaveChanges'e girer; son-5 kırpmasının satırları kayıtsız (bugünkü davranış).</summary>
    private async Task DeleteAsync(Guid customerId, List<CustomerBackup> rows, int expiredCount, CancellationToken ct)
    {
        // ÖNCE satırlar, SONRA dosyalar. Ters sırada SaveChanges patlarsa
        // dosyaları silinmiş ama satırları duran bir yığın "hayalet yedek"
        // kalırdı — müşteri listede görür, geri yükleyemez. Bu sırada en
        // kötü ihtimalle yetim dosya kalır; BackupOrphanCleanupJob toplar.
        var blobPaths = rows.Select(b => b.BlobPath).ToList();
        _db.CustomerBackups.RemoveRange(rows);
        // Ölçüt metninde ';' yok: dış kopyanın (CSV) ayracı.
        DataDestruction.Record(_db, null, DestructionTarget, DataDestruction.Actions.Deleted, expiredCount,
            $"CreatedAt+{_opt.MonthlyRetentionMonths}ay, aylık yedek", DataDestruction.Triggers.Retention,
            DataDestruction.SystemActor, subjectId: customerId);
        await _db.SaveChangesAsync(ct);
        foreach (var path in blobPaths) _storage.DeleteBlob(path);
    }
}
