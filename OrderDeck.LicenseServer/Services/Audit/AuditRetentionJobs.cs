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
    /// en az üç yıl istiyor; <see cref="RetentionMonths"/>'tan kısa verilirse onun değeri kullanılır. Satırdaki yöneticinin
    /// kullanıcı adı ve IP'si bu süreyi beklemez, <see cref="RetentionMonths"/>'ta maskelenir.</summary>
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
/// inceleme Ö-13). Muafiyet kişisel veriyi uzatmasın diye bu satırlarda silmeyi yapan yöneticinin kullanıcı adı ve IP'si
/// <see cref="AuditRetentionOptions.RetentionMonths"/>'ta maskelenir (ad kimliğe çevrilir, IP boşaltılır; Ö-14) ve maskeleme de
/// imha kaydına geçer (<see cref="MaskedTarget"/>).
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
    /// <summary>İmha olayının <see cref="AuditRetentionOptions.RetentionMonths"/>'ı geçen satırında maskelenen sütunlar (imha
    /// kaydının hedefi).</summary>
    public const string MaskedTarget = "AuditLogs.AdminUsername+IpAddress";

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
        // Silmeden SONRA: 36 ayı geçip yukarıda silinen satır boşuna maskelenmesin. Ad zaten kimlikse ve IP boşsa satır seçilmez:
        // her koşu aynı satırı yeniden yazıp yeniden saymaz. Koşul ve MaskWhereAsync'in yazdığı değer aynı ifade
        // (AdminId.ToString().ToLower(); SQL'de LOWER(CONVERT(…))): yazılan değer koşulu kesin kapatır, döngü biter.
        var totalMasked = await MaskWhereAsync(a => a.OccurredAt < cutoff && exempt.Contains(a.EventType)
                && (a.IpAddress != null || a.AdminUsername != a.AdminId.ToString().ToLower()),
            $"OccurredAt+{_opt.RetentionMonths}ay, imha olayı", ct);

        // Kesilen koşu (Hangfire kapanışı) partilerin döngüsünden sessizce çıkar: başarılı koşu kaydı yazılmasın.
        ct.ThrowIfCancellationRequested();
        DataDestruction.RecordRun(_db, JobId, $"OccurredAt+{_opt.RetentionMonths}ay; imha olayı +{destructionMonths}ay");
        await _db.SaveChangesAsync(ct);
        _db.ChangeTracker.Clear();

        if (totalDeleted + totalMasked > 0)
            _log.LogInformation("[AuditRetentionJobs] pruned {Count} audit rows, masked {Masked} destruction-event rows " +
                "(cutoff {Cutoff}, destruction cutoff {DestructionCutoff})", totalDeleted, totalMasked, cutoff, destructionCutoff);
        else
            _log.LogDebug("[AuditRetentionJobs] no rows older than {Cutoff} — nothing to prune", cutoff);
    }

    /// <summary>Koşula uyan satırları siler (<see cref="InBatchesAsync"/>).</summary>
    private Task<int> PruneWhereAsync(Expression<Func<AuditLogEntry, bool>> predicate, string criterion, CancellationToken ct)
        => InBatchesAsync(predicate, "AuditLogs", DataDestruction.Actions.Deleted, criterion,
            ids => _db.AuditLogs.Where(a => ids.Contains(a.Id)).ExecuteDeleteAsync(ct),
            rows => _db.AuditLogs.RemoveRange(rows), ct);

    /// <summary>İmha olayının satırında silmeyi yapan yöneticinin kişisel verisini maskeler (<see cref="InBatchesAsync"/>):
    /// kullanıcı adı yöneticinin kimliğine (<see cref="AuditLogEntry.AdminId"/>'nin metni) çevrilir, IP boşaltılır. Satır
    /// <see cref="AuditRetentionOptions.DestructionRetentionMonths"/>'a kadar yalnız takma kimlikler ve sayılarla kalır (KVKK
    /// Ö-14: muafiyet kişisel veriyi <see cref="AuditRetentionOptions.RetentionMonths"/>'tan uzun tutmasın).
    /// <paramref name="predicate"/> maskelenmiş satırı seçmemeli.</summary>
    private Task<int> MaskWhereAsync(Expression<Func<AuditLogEntry, bool>> predicate, string criterion, CancellationToken ct)
        => InBatchesAsync(predicate, MaskedTarget, DataDestruction.Actions.Masked, criterion,
            ids => _db.AuditLogs.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IpAddress, (string?)null)
                .SetProperty(a => a.AdminUsername, a => a.AdminId.ToString().ToLower()), ct),
            rows =>
            {
                foreach (var a in rows)
                {
                    a.IpAddress = null;
                    a.AdminUsername = a.AdminId.ToString(); // .NET "D" biçimi küçük harf: SQL'deki LOWER(CONVERT(…)) ile aynı
                }
            }, ct);

    /// <summary>Koşula uyan satırları <see cref="AuditRetentionOptions.BatchSize"/>'lık partilerle işler: her işlem küçük
    /// kalır, bir parti <see cref="AuditRetentionOptions.BatchSize"/>'tan az dönünce biter. İşlenen satır koşuldan düşmeli (silinir
    /// ya da maskelenir): her parti baştan sorgulanır. Her parti imha kaydını işlemle AYNI işlemde yazar (biri olup öbürü
    /// olmasın). Relational'da <paramref name="bulk"/> (ExecuteDelete/ExecuteUpdate; tek gidiş-dönüş, <c>WHERE Id IN …</c>) ve
    /// kayıt açık bir işlemde; InMemory (testler) ikisini de ve işlemi bilmez, orada yükle + <paramref name="tracked"/> ve kayıt
    /// tek SaveChanges'te.</summary>
    private async Task<int> InBatchesAsync(Expression<Func<AuditLogEntry, bool>> predicate, string target, string action,
        string criterion, Func<List<Guid>, Task<int>> bulk, Action<List<AuditLogEntry>> tracked, CancellationToken ct)
    {
        void Record(int count) => DataDestruction.Record(_db, null, target, action, count, criterion,
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

            int affected;
            if (_db.Database.IsRelational())
            {
                await using var tx = await _db.Database.BeginTransactionAsync(ct);
                affected = await bulk(batch);
                Record(affected);
                await _db.SaveChangesAsync(ct);
                await tx.CommitAsync(ct);
            }
            else
            {
                var entries = await _db.AuditLogs.Where(a => batch.Contains(a.Id)).ToListAsync(ct);
                tracked(entries);
                affected = entries.Count;
                Record(affected);
                await _db.SaveChangesAsync(ct);
            }
            _db.ChangeTracker.Clear();

            total += affected;
            if (batch.Count < _opt.BatchSize) break;  // son, eksik parti — bitti
        }
        return total;
    }
}
