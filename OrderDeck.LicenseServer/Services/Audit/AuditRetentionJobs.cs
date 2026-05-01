using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Audit;

public sealed class AuditRetentionOptions
{
    /// <summary>How many months of AuditLog history to keep before pruning.
    /// 0 disables pruning entirely. Default: 24 (KVKK / typical SaaS retention).</summary>
    public int RetentionMonths { get; set; } = 24;

    /// <summary>How many rows to delete per batch when pruning. Bounded so the
    /// job never holds a giant transaction across SQL Server.</summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Hangfire recurring job that prunes audit log rows older than the configured
/// retention window. Closes audit High-priority gap "AuditLog büyümesi sınırsız".
///
/// Rationale: AuditLog grows at ~5 events/customer/day. At 100 customers and
/// year 2 that's ~360K rows / 60 MB. Manageable, but on SQL Express's 10 GB
/// hard cap it eventually crowds out the operational tables. 24-month rolling
/// window gives enough history for support / forensics without unbounded growth.
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

        var cutoff = DateTimeOffset.UtcNow.AddMonths(-_opt.RetentionMonths);
        var totalDeleted = 0;

        // Batched delete keeps each transaction small. Loop until a batch
        // returns < BatchSize, meaning we've drained everything older than cutoff.
        while (!ct.IsCancellationRequested)
        {
            // Pull just the IDs to delete; avoids loading full entity into change tracker.
            var batch = await _db.AuditLogs
                .Where(a => a.OccurredAt < cutoff)
                .OrderBy(a => a.OccurredAt)
                .Take(_opt.BatchSize)
                .Select(a => a.Id)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            // ExecuteDeleteAsync is the perf-optimal path on relational providers
            // (single round-trip with WHERE Id IN ...). InMemory provider
            // (used by tests) doesn't implement it, so fall back to a load+remove
            // round-trip there.
            if (_db.Database.IsRelational())
            {
                await _db.AuditLogs
                    .Where(a => batch.Contains(a.Id))
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                var entries = await _db.AuditLogs
                    .Where(a => batch.Contains(a.Id))
                    .ToListAsync(ct);
                _db.AuditLogs.RemoveRange(entries);
                await _db.SaveChangesAsync(ct);
            }

            totalDeleted += batch.Count;

            if (batch.Count < _opt.BatchSize) break;  // last partial batch — done
        }

        if (totalDeleted > 0)
            _log.LogInformation("[AuditRetentionJobs] pruned {Count} audit rows older than {Cutoff}",
                totalDeleted, cutoff);
        else
            _log.LogDebug("[AuditRetentionJobs] no rows older than {Cutoff} — nothing to prune", cutoff);
    }
}
