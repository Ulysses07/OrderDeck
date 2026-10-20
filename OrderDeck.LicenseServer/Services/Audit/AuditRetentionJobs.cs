using System.Linq.Expressions;
using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Services.Privacy;

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
///
/// Budama kişisel veri siler (denetim ayrıntıları, <c>customer.purged</c>'ın e-postası): her parti kişisel veri içermeyen
/// kalıcı imha kaydını (<see cref="DataDestruction"/>) silmeyle AYNI işlemde yazar, başarılı koşu sonda bir koşu kaydı
/// bırakır, iş düşerse yöneticiye e-posta gider (<see cref="BankJobAlerts"/>) ve istisna Hangfire'a çıkar (iç imha prosedürü
/// §4.2-3).
/// </summary>
public sealed class AuditRetentionJobs
{
    public const string JobId = "audit-retention";
    public const string FailureSubject = "[OrderDeck] Denetim kaydı budama işi düştü";

    private readonly LicenseDbContext _db;
    private readonly AuditRetentionOptions _opt;
    private readonly BankJobAlerts _alerts;
    private readonly ILogger<AuditRetentionJobs> _log;

    public AuditRetentionJobs(LicenseDbContext db, IOptions<AuditRetentionOptions> opt, BankJobAlerts alerts,
        ILogger<AuditRetentionJobs> log)
    {
        _db = db;
        _opt = opt.Value;
        _alerts = alerts;
        _log = log;
    }

    /// <summary>Recurring entry point. Hangfire invokes this; idempotent and
    /// safe to call manually for ops/test. Düşerse yöneticiye e-posta ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task PruneAsync(CancellationToken ct)
    {
        if (_opt.RetentionMonths <= 0)
        {
            _log.LogInformation("[AuditRetentionJobs] retention disabled (RetentionMonths=0)");
            return;
        }

        try
        {
            await PruneCoreAsync(ct);
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "[AuditRetentionJobs] budama düştü");
            await _alerts.SendAsync(FailureSubject,
                $"audit-retention düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, " +
                "o güne kadar denetim kayıtlarının saklama süresi aşılmış olur.", CancellationToken.None);
            throw;
        }
    }

    private async Task PruneCoreAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var cutoff = now.AddMonths(-_opt.RetentionMonths);
        // İmha olayları kendi, daha uzun süresiyle budanır (Silme Yönetmeliği md. 7/3).
        var destructionMonths = Math.Max(_opt.DestructionRetentionMonths, _opt.RetentionMonths);
        var destructionCutoff = now.AddMonths(-destructionMonths);
        // Dizi (HashSet değil): SQL Server'da parametre listesi olarak çevrilir.
        var exempt = AuditEvents.DestructionEvents.ToArray();

        var totalDeleted = await PruneWhereAsync(a => a.OccurredAt < cutoff && !exempt.Contains(a.EventType),
                $"OccurredAt+{_opt.RetentionMonths}ay", ct)
            + await PruneWhereAsync(a => a.OccurredAt < destructionCutoff && exempt.Contains(a.EventType),
                $"OccurredAt+{destructionMonths}ay;imha olayı", ct);

        // Kesilen koşu (Hangfire kapanışı) PruneWhereAsync'in döngüsünden sessizce çıkar: başarılı koşu kaydı yazılmasın.
        ct.ThrowIfCancellationRequested();
        DataDestruction.RecordRun(_db, JobId, $"OccurredAt+{_opt.RetentionMonths}ay; imha olayı +{destructionMonths}ay");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        if (totalDeleted > 0)
            _log.LogInformation("[AuditRetentionJobs] pruned {Count} audit rows (cutoff {Cutoff}, destruction cutoff {DestructionCutoff})",
                totalDeleted, cutoff, destructionCutoff);
        else
            _log.LogDebug("[AuditRetentionJobs] no rows older than {Cutoff} — nothing to prune", cutoff);
    }

    /// <summary>Koşula uyan satırları <see cref="AuditRetentionOptions.BatchSize"/>'lık partilerle siler: her işlem küçük
    /// kalır, bir parti <see cref="AuditRetentionOptions.BatchSize"/>'tan az dönünce biter. Her parti imha kaydını silmeyle
    /// AYNI işlemde yazar (biri olup öbürü olmasın). Relational'da ExecuteDelete (tek gidiş-dönüş, <c>WHERE Id IN …</c>) ve
    /// kayıt açık bir işlemde; InMemory (testler) ExecuteDelete ve işlem bilmez, orada yükle+sil ve kayıt tek SaveChanges'te.</summary>
    private async Task<int> PruneWhereAsync(Expression<Func<AuditLogEntry, bool>> predicate, string criterion,
        CancellationToken ct)
    {
        void Record(int count) => DataDestruction.Record(_db, null, "AuditLogs", DataDestruction.Actions.Deleted, count, criterion,
            DataDestruction.Triggers.Retention, DataDestruction.SystemActor);

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

            int deleted;
            if (_db.Database.IsRelational())
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                deleted = await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ExecuteDeleteAsync(ct);
                Record(deleted);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                var entries = await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ToListAsync(ct);
                _db.AuditLogs.RemoveRange(entries);
                deleted = entries.Count;
                Record(deleted);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();

            total += deleted;
            if (batch.Count < _opt.BatchSize) break;  // son, eksik parti — bitti
        }
        return total;
    }
}
