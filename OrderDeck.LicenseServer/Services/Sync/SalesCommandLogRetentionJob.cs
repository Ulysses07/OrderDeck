using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sync;

/// <summary>
/// <c>sales-command-log-retention</c> — spec 2026-10-13 §3.1 (<c>SalesCommandLogs</c>: 90 gün), §4.9. Günde bir (04:54
/// UTC, <see cref="Program.ScheduleSalesSyncJobs"/>) <see cref="Retention"/>'dan eski komut günlüğü satırlarını siler.
/// Günlük idempotency ve denetim içindir: istemci bir komutu en çok birkaç gün yeniden dener (bekleyen komut, §5.5);
/// 90 günden eski bir <c>CommandId</c>'nin yeniden gelmesi beklenmez.
///
/// <para><b>Partiler.</b> İlişkisel sağlayıcıda <see cref="BatchSize"/>'lık <c>ExecuteDelete</c> turları: ilk koşu
/// birikmiş satırları uzun tek bir işlemde silip komut ucunun yazımlarını bekletmesin. InMemory'de (testler) aynı kural
/// izlenen varlıklarla (<c>SecurityDataRetentionJob</c> deseni).</para>
///
/// <para>İdempotent; yalnız sayılar günlüğe yazılır (§10.5).</para>
/// </summary>
[DisableConcurrentExecution(timeoutInSeconds: 300)]
[AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
public sealed class SalesCommandLogRetentionJob
{
    /// <summary>Hangfire işinin adı (recurring Id).</summary>
    public const string JobId = "sales-command-log-retention";

    /// <summary>04:54 UTC: yayın penceresi (TR 20:00–01:00) dışında; 04:50 onarım işinden sonra, 04:52 İYS ve 04:57
    /// banka saklama işlerinin arasında, 5 dakikalık ızgaranın dışında.</summary>
    public const string Cron = "54 4 * * *";

    /// <summary>Saklama süresi (§3.1).</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(90);

    private readonly LicenseDbContext _db;
    private readonly ILogger<SalesCommandLogRetentionJob> _log;

    public SalesCommandLogRetentionJob(LicenseDbContext db, ILogger<SalesCommandLogRetentionJob> log)
    {
        _db = db;
        _log = log;
    }

    /// <summary>Bir silme turunda en çok bu kadar satır. Testler küçültüp parti döngüsünü sınar.</summary>
    public int BatchSize { get; set; } = 5_000;

    /// <returns>Silinen satır sayısı.</returns>
    public async Task<int> RunAsync(CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - Retention;
        var relational = _db.Database.IsRelational();
        var total = 0;
        var batches = 0;

        while (!ct.IsCancellationRequested)
        {
            var expired = _db.SalesCommandLogs.Where(c => c.CreatedAt < cutoff).Take(BatchSize);
            int deleted;
            if (relational)
            {
                deleted = await expired.ExecuteDeleteAsync(ct);
            }
            else
            {
                var rows = await expired.ToListAsync(ct);
                _db.SalesCommandLogs.RemoveRange(rows);
                await _db.SaveChangesAsync(ct);
                deleted = rows.Count;
            }

            total += deleted;
            if (deleted > 0) batches++;
            if (deleted < BatchSize) break;
        }

        if (total > 0)
            _log.LogInformation(
                "sales-command-log-retention: {Cutoff} öncesi {Deleted} komut günlüğü satırı silindi ({Batches} parti)",
                cutoff, total, batches);
        else
            _log.LogDebug("sales-command-log-retention: {Cutoff} öncesi silinecek satır yok", cutoff);

        return total;
    }
}
