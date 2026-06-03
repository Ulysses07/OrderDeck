using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Hangfire recurring job — eski parola sıfırlama OTP satırlarını siler.
/// Kodlar 10 dakikada expire olduğu için bir günden eski satırlar zaten ölü;
/// tablo sınırsız büyümesin diye <c>Sms:OtpRetentionDays</c> (default 7) günden
/// eski tüm satırlar (tüketilmiş veya değil) temizlenir. AuditRetentionJobs ile
/// aynı batched-delete deseni.
/// </summary>
public sealed class PasswordResetCodeCleanupJob
{
    private readonly LicenseDbContext _db;
    private readonly int _retentionDays;
    private readonly int _batchSize;
    private readonly ILogger<PasswordResetCodeCleanupJob> _log;

    public PasswordResetCodeCleanupJob(
        LicenseDbContext db, IConfiguration config, ILogger<PasswordResetCodeCleanupJob> log)
    {
        _db = db;
        _retentionDays = config.GetValue("Sms:OtpRetentionDays", 7);
        _batchSize = 1000;
        _log = log;
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task PruneAsync(CancellationToken ct)
    {
        if (_retentionDays <= 0)
        {
            _log.LogInformation("[PasswordResetCodeCleanup] disabled (OtpRetentionDays=0)");
            return;
        }

        var cutoff = DateTimeOffset.UtcNow.AddDays(-_retentionDays);
        var totalDeleted = 0;

        while (!ct.IsCancellationRequested)
        {
            var batch = await _db.ShopperPasswordResetCodes
                .Where(c => c.CreatedAt < cutoff)
                .OrderBy(c => c.CreatedAt)
                .Take(_batchSize)
                .Select(c => c.Id)
                .ToListAsync(ct);

            if (batch.Count == 0) break;

            if (_db.Database.IsRelational())
            {
                await _db.ShopperPasswordResetCodes
                    .Where(c => batch.Contains(c.Id))
                    .ExecuteDeleteAsync(ct);
            }
            else
            {
                var entries = await _db.ShopperPasswordResetCodes
                    .Where(c => batch.Contains(c.Id))
                    .ToListAsync(ct);
                _db.ShopperPasswordResetCodes.RemoveRange(entries);
                await _db.SaveChangesAsync(ct);
            }

            totalDeleted += batch.Count;
            if (batch.Count < _batchSize) break;
        }

        if (totalDeleted > 0)
            _log.LogInformation("[PasswordResetCodeCleanup] pruned {Count} OTP rows older than {Cutoff}",
                totalDeleted, cutoff);
    }
}
