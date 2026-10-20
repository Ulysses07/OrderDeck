using System.Linq.Expressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Audit;

public sealed class AuditRetentionOptions
{
    /// <summary>How many months of AuditLog history to keep before pruning.
    /// 0 disables pruning entirely. Default: 24 (KVKK / typical SaaS retention).</summary>
    public int RetentionMonths { get; set; } = 24;

    /// <summary>How many rows to delete per batch when pruning. Bounded so the
    /// job never holds a giant transaction across SQL Server.</summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>İmha olaylarının (<see cref="AuditEvents.DestructionEvents"/>) saklama süresi, ay. Silme Yönetmeliği md. 7/3
    /// en az üç yıl istiyor; <see cref="RetentionMonths"/>'tan kısa verilirse onun değeri kullanılır.</summary>
    public int DestructionRetentionMonths { get; set; } = 36;
}

/// <summary>
/// Hangfire recurring job that prunes audit log rows older than the configured
/// retention window. Closes audit High-priority gap "AuditLog büyümesi sınırsız".
///
/// Rationale: AuditLog grows at ~5 events/customer/day. At 100 customers and
/// year 2 that's ~360K rows / 60 MB. Manageable, but on SQL Express's 10 GB
/// hard cap it eventually crowds out the operational tables. 24-month rolling
/// window gives enough history for support / forensics without unbounded growth.
///
/// İmha olayları (<see cref="AuditEvents.DestructionEvents"/>) kendi, daha uzun süresiyle
/// (<see cref="AuditRetentionOptions.DestructionRetentionMonths"/>, 36 ay) budanır: Silme Yönetmeliği md. 7/3 (KVKK ön
/// inceleme Ö-13).
/// </summary>
public sealed class AuditRetentionJobs
{
    private readonly LicenseDbContext _db;
    private readonly AuditRetentionOptions _opt;
    private readonly ILogger<AuditRetentionJobs> _log;

    public AuditRetentionJobs(LicenseDbContext db, IOptions<AuditRetentionOptions> opt,
        ILogger<AuditRetentionJobs> log)
    {
        _db = db;
        _opt = opt.Value;
        _log = log;
    }

    /// <summary>Recurring entry point. Hangfire invokes this; idempotent and
    /// safe to call manually for ops/test.</summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task PruneAsync(CancellationToken ct)
    {
        if (_opt.RetentionMonths <= 0)
        {
            _log.LogInformation("[AuditRetentionJobs] retention disabled (RetentionMonths=0)");
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMonths(-_opt.RetentionMonths);
        // İmha olayları kendi, daha uzun süresiyle budanır (Silme Yönetmeliği md. 7/3).
        var destructionCutoff = now.AddMonths(-Math.Max(_opt.DestructionRetentionMonths, _opt.RetentionMonths));
        // Dizi (HashSet değil): SQL Server'da parametre listesi olarak çevrilir.
        var exempt = AuditEvents.DestructionEvents.ToArray();

        var totalDeleted = await PruneWhereAsync(a => a.OccurredAt < cutoff && !exempt.Contains(a.EventType), ct)
            + await PruneWhereAsync(a => a.OccurredAt < destructionCutoff && exempt.Contains(a.EventType), ct);

        if (totalDeleted > 0)
            _log.LogInformation("[AuditRetentionJobs] pruned {Count} audit rows (cutoff {Cutoff}, destruction cutoff {DestructionCutoff})",
                totalDeleted, cutoff, destructionCutoff);
        else
            _log.LogDebug("[AuditRetentionJobs] no rows older than {Cutoff} — nothing to prune", cutoff);
    }

    /// <summary>Koşula uyan satırları <see cref="AuditRetentionOptions.BatchSize"/>'lık partilerle siler: her işlem küçük
    /// kalır, bir parti <see cref="AuditRetentionOptions.BatchSize"/>'tan az dönünce biter. Relational'da ExecuteDelete
    /// (tek gidiş-dönüş, <c>WHERE Id IN …</c>); InMemory (testler) ExecuteDelete bilmez, orada yükle+sil.</summary>
    private async Task<int> PruneWhereAsync(Expression<Func<AuditLogEntry, bool>> predicate, CancellationToken ct)
    {
        var total = 0;
        while (!ct.IsCancellationRequested)
        {
            // Yalnız kimlikler çekilir: tam varlık izleyiciye yüklenmez.
            var batch = await _db.AuditLogs
                .Where(predicate)
                .OrderBy(a => a.OccurredAt)
                .Take(_opt.BatchSize)
                .Select(a => a.Id)
                .ToListAsync(ct);
            if (batch.Count == 0) break;

            if (_db.Database.IsRelational())
            {
                await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ExecuteDeleteAsync(ct);
            }
            else
            {
                var entries = await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ToListAsync(ct);
                _db.AuditLogs.RemoveRange(entries);
                await _db.SaveChangesAsync(ct);
            }

            total += batch.Count;
            if (batch.Count < _opt.BatchSize) break;  // son, eksik parti — bitti
        }
        return total;
    }
}
