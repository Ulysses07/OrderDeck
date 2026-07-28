using Hangfire;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.WhatsApp;

/// <summary>
/// Hangfire recurring job — eski WhatsApp gönderim rezervasyonlarını siler.
///
/// <para><b>Neden var:</b> <see cref="Domain.WaSendAttempt"/> her gönderimde bir
/// satır yazıyor ve hiç silinmiyordu. Satırın tek işlevi kısa ömürlü: istemcinin
/// yeniden-denemesini tanımak. Dayanıklılık katmanının denemeleri saniyeler
/// içinde biter, iki dakika sonra satır zaten "bayat" sayılıyor
/// (<c>LicensesWhatsAppSendController.StalePendingAfter</c>). Yani günlerce
/// saklamanın idempotency açısından bir değeri yok; sakladığımız süre tamamen
/// teşhis içindir.</para>
///
/// <para><b>Neden CompletedAt değil StartedAt'e bakıyoruz:</b> damgalanamamış
/// satırlar <c>pending</c> kalıp <c>CompletedAt = null</c> taşıyor. Ölçüt
/// <c>CompletedAt</c> olsaydı tam da temizlenmesi gereken artıklar sonsuza dek
/// kalırdı. <c>StartedAt</c> her satırda dolu.</para>
///
/// <para><see cref="Auth.PasswordResetCodeCleanupJob"/> ile aynı batched-delete
/// deseni.</para>
/// </summary>
public sealed class WaSendAttemptCleanupJob
{
    private readonly LicenseDbContext _db;
    private readonly int _retentionDays;
    private readonly int _batchSize;
    private readonly ILogger<WaSendAttemptCleanupJob> _log;

    public WaSendAttemptCleanupJob(
        LicenseDbContext db, IConfiguration config, ILogger<WaSendAttemptCleanupJob> log)
    {
        _db = db;
        _retentionDays = config.GetValue("OrderDeck:WhatsApp:SendAttemptRetentionDays", 30);
        _batchSize = 1000;
        _log = log;
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task PruneAsync(CancellationToken ct)
    {
        if (_retentionDays <= 0)
        {
            _log.LogInformation("[WaSendAttemptCleanup] disabled (SendAttemptRetentionDays=0)");
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
        var totalDeleted = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = await _db.WaSendAttempts
                .Where(a => a.StartedAt < cutoff)
                .OrderBy(a => a.StartedAt)
                .Take(_batchSize)
                .Select(a => a.Id)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            if (_db.Database.IsRelational())
            {
                await _db.WaSendAttempts
                    .Where(a => batch.Contains(a.Id))
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                var entries = await _db.WaSendAttempts
                    .Where(a => batch.Contains(a.Id))
                    .ToListAsync(ct);
                _db.WaSendAttempts.RemoveRange(entries);
                await _db.SaveChangesAsync(ct);
            }

            totalDeleted += batch.Count;
            if (batch.Count < _batchSize) break;
        }

        if (totalDeleted > 0)
            _log.LogInformation(
                "[WaSendAttemptCleanup] pruned {Count} send-attempt rows older than {Cutoff}",
                totalDeleted, cutoff);
    }
}
