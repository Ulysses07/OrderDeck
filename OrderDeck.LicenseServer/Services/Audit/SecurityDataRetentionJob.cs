using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Audit;

public sealed class SecurityDataRetentionOptions
{
    /// <summary>Kaç gün sonra IP / User-Agent alanları anonimleştirilsin.
    /// 0 = kapalı. Varsayılan 90, gizlilik politikasındaki "Güvenlik kayıtları:
    /// 90 gün" taahhüdüyle birebir aynı olmak zorunda — birini değiştiren
    /// diğerini de değiştirmeli.</summary>
    public int RetentionDays { get; set; } = 90;

    /// <summary>Tek turda güncellenecek satır sayısı. Transaction'ı küçük
    /// tutar; iş bitene kadar döngü devam eder.</summary>
    public int BatchSize { get; set; } = 1000;
}

/// <summary>
/// Hangfire recurring job — 90 günden eski güvenlik kayıtlarındaki IP ve
/// User-Agent alanlarını SATIRI SİLMEDEN boşaltır.
///
/// Neden silme değil de anonimleştirme: gizlilik politikası yalnızca IP ve
/// giriş zamanı için 90 gün taahhüt ediyor, oysa aynı satırlar audit izinin
/// (24 ay, <see cref="AuditRetentionJobs"/>) ve mali kayıtların taşıyıcısı.
/// Satırı silmek taahhüdün gerektirmediği bir kaybı doğururdu; sütunu
/// boşaltmak ikisini birden karşılıyor.
///
/// Kapsam dışı bıraktıklarım ve gerekçeleri:
///   • ShopperPasswordResetCode.RequestIp — satırın kendisi 7 günde
///     PasswordResetCodeCleanupJob tarafından siliniyor, zaten uyumlu.
///   • CustomerBackup.MachineName — yedek listesinde "hangi makine"
///     bilgisini gösteren işlevsel alan, güvenlik kaydı değil.
///
/// Job idempotent: WHERE koşulu zaten boşaltılmış satırları dışlıyor, bu
/// yüzden her gece aynı satırları tekrar yazmaz.
/// </summary>
public sealed class SecurityDataRetentionJob
{
    private readonly LicenseDbContext _db;
    private readonly SecurityDataRetentionOptions _opt;
    private readonly ILogger<SecurityDataRetentionJob> _log;

    public SecurityDataRetentionJob(
        LicenseDbContext db,
        IOptions<SecurityDataRetentionOptions> opt,
        ILogger<SecurityDataRetentionJob> log)
    {
        _db = db;
        _opt = opt.Value;
        _log = log;
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task AnonymiseAsync(CancellationToken ct)
    {
        if (_opt.RetentionDays <= 0)
        {
            _log.LogInformation("[SecurityDataRetention] devre dışı (RetentionDays=0)");
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_opt.RetentionDays);
        var relational = _db.Database.IsRelational();

        var audit = await RunAsync(
            "AuditLog.IpAddress",
            () => _db.AuditLogs.Where(a => a.OccurredAt < cutoff && a.IpAddress != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(a => a.IpAddress, (string?)null), ct),
            rows => { foreach (var r in rows) r.IpAddress = null; },
            relational, ct);

        var refreshTokens = await RunAsync(
            "RefreshToken.CreatedByIp",
            () => _db.RefreshTokens.Where(t => t.CreatedAt < cutoff && t.CreatedByIp != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, (string?)null), ct),
            rows => { foreach (var r in rows) r.CreatedByIp = null; },
            relational, ct);

        var shopperTokens = await RunAsync(
            "ShopperRefreshToken.CreatedByIp",
            () => _db.ShopperRefreshTokens.Where(t => t.CreatedAt < cutoff && t.CreatedByIp != null),
            q => q.ExecuteUpdateAsync(s => s.SetProperty(t => t.CreatedByIp, (string?)null), ct),
            rows => { foreach (var r in rows) r.CreatedByIp = null; },
            relational, ct);

        var intake = await RunAsync(
            "IntakeFormSubmission.IpAddress+UserAgent",
            () => _db.IntakeFormSubmissions.Where(
                s => s.SubmittedAt < cutoff && (s.IpAddress != null || s.UserAgent != null)),
            q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpAddress, (string?)null)
                .SetProperty(x => x.UserAgent, (string?)null), ct),
            rows => { foreach (var r in rows) { r.IpAddress = null; r.UserAgent = null; } },
            relational, ct);

        // Mali kayıt: satır ve tutar duruyor, yalnız istemci parmak izi gidiyor.
        // Sütunlar non-nullable olduğu için null yerine boş string yazılıyor.
        var paymentAudit = await RunAsync(
            "PaymentSubmissionAudit.IpAddress+UserAgent",
            () => _db.PaymentSubmissionAudits.Where(
                a => a.CreatedAt < cutoff && (a.IpAddress != "" || a.UserAgent != "")),
            q => q.ExecuteUpdateAsync(s => s
                .SetProperty(x => x.IpAddress, "")
                .SetProperty(x => x.UserAgent, ""), ct),
            rows => { foreach (var r in rows) { r.IpAddress = ""; r.UserAgent = ""; } },
            relational, ct);

        var total = audit + refreshTokens + shopperTokens + intake + paymentAudit;
        if (total > 0)
            _log.LogInformation(
                "[SecurityDataRetention] {Cutoff} öncesi {Total} satır anonimleştirildi "
                + "(audit={Audit}, refresh={Refresh}, shopperRefresh={ShopperRefresh}, "
                + "intake={Intake}, paymentAudit={PaymentAudit})",
                cutoff, total, audit, refreshTokens, shopperTokens, intake, paymentAudit);
        else
            _log.LogDebug("[SecurityDataRetention] {Cutoff} öncesi anonimleştirilecek satır yok", cutoff);
    }

    /// <summary>
    /// Tek tablo için batch'li anonimleştirme. Relational sağlayıcıda tek
    /// round-trip'lik ExecuteUpdate, InMemory'de (testler) load+set fallback —
    /// AuditRetentionJobs'taki aynı ikili desen.
    /// </summary>
    private async Task<int> RunAsync<TEntity>(
        string label,
        Func<IQueryable<TEntity>> query,
        Func<IQueryable<TEntity>, Task<int>> bulkUpdate,
        Action<List<TEntity>> inMemoryUpdate,
        bool relational,
        CancellationToken ct) where TEntity : class
    {
        var total = 0;

        while (!ct.IsCancellationRequested)
        {
            if (relational)
            {
                // ExecuteUpdate WHERE'i zaten boşaltılmışları dışlıyor, yani
                // her tur kalan işi küçültüyor ve döngü kesin sonlanıyor.
                var affected = await bulkUpdate(query().Take(_opt.BatchSize));
                total += affected;
                if (affected < _opt.BatchSize) break;
            }
            else
            {
                var rows = await query().Take(_opt.BatchSize).ToListAsync(ct);
                if (rows.Count == 0) break;
                inMemoryUpdate(rows);
                await _db.SaveChangesAsync(ct);
                total += rows.Count;
                if (rows.Count < _opt.BatchSize) break;
            }
        }

        if (total > 0)
            _log.LogDebug("[SecurityDataRetention] {Label}: {Count} satır", label, total);

        return total;
    }
}
