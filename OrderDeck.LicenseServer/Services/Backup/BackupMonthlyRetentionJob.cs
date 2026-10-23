using Hangfire;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Services.Privacy;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Hangfire günlük işi: süresi dolan aylık masaüstü yedeklerini yükleme yapmayan müşterilerde de siler (karar 7, 23.10.2026;
/// kural <see cref="BackupRetentionService.ExpireMonthliesAsync"/>'te). Yükleme sonrası saklama turu yalnız yükleyen müşteride
/// koşar; yüklemeyi bırakan müşterinin aylık yedekleri bu iş olmadan süresiz kalırdı.
///
/// <para>Her silme kişisel veri içermeyen imha kaydını satırlarla aynı SaveChanges'te yazar; başarılı koşu sonda bir koşu kaydı
/// (<see cref="JobId"/>) bırakır; iş düşerse yöneticiye e-posta gider (<see cref="AdminJobAlerts"/>) ve istisna Hangfire'a çıkar
/// (KVKK Ö-13, iç imha prosedürü §4.2-3). Dosyası silinemeyen yedek yetim kalır, <see cref="BackupOrphanCleanupJob"/> toplar.
/// Süre 0 ise (süresiz) koşmaz, koşu kaydı da yazmaz (<c>audit-retention</c> kalıbı).</para>
/// </summary>
public sealed class BackupMonthlyRetentionJob
{
    /// <summary>Hangfire iş kimliği; koşu kaydının hedefi de bu (<see cref="DataDestruction.RecordRun"/>).</summary>
    public const string JobId = "backup-monthly-retention";
    public const string FailureSubject = "[OrderDeck] Aylık yedek saklama işi düştü";

    private readonly LicenseDbContext _db;
    private readonly BackupRetentionService _retention;
    private readonly BackupOptions _opt;
    private readonly AdminJobAlerts _alerts;
    private readonly ILogger<BackupMonthlyRetentionJob> _log;

    public BackupMonthlyRetentionJob(LicenseDbContext db, BackupRetentionService retention, IOptions<BackupOptions> opt,
        AdminJobAlerts alerts, ILogger<BackupMonthlyRetentionJob> log)
    {
        _db = db;
        _retention = retention;
        _opt = opt.Value;
        _alerts = alerts;
        _log = log;
    }

    /// <summary>Silinen yedek sayısını döner. Düşerse yöneticiye e-posta (KVKK Ö-13) ve istisna Hangfire'a çıkar (koşu Failed).</summary>
    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var months = _opt.MonthlyRetentionMonths;
        if (months <= 0)
        {
            _log.LogInformation("[{JobId}] devre dışı (Backup:MonthlyRetentionMonths=0, aylık yedekler süresiz)", JobId);
            return 0;
        }

        try
        {
            var deleted = await _retention.ExpireMonthliesAsync(ct);
            // Kesilen koşu başarılı koşu kaydı yazmasın (audit-retention kalıbı). Ölçüt metninde ';' yok: dış kopyanın ayracı.
            ct.ThrowIfCancellationRequested();
            DataDestruction.RecordRun(_db, JobId, $"CreatedAt+{months}ay, aylık yedek");
            await _db.SaveChangesAsync(ct);
            return deleted;
        }
        catch (Exception ex) when (!(ex is OperationCanceledException && ct.IsCancellationRequested))
        {
            _log.LogError(ex, "[{JobId}] düştü", JobId);
            await _alerts.SendAsync(FailureSubject,
                $"{JobId} düştü: {ex.GetType().Name}. Hangfire panosuna bakın; iş ertesi gün yeniden koşar, o güne kadar " +
                "süresi dolan aylık masaüstü yedekleri sunucuda kalır.", CancellationToken.None);
            throw;
        }
    }
}
