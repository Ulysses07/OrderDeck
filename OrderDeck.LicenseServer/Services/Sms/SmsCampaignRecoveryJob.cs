using Hangfire;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// F08/F09 güvenlik ağı (denetim 2026-09-09): takılı SMS kampanyalarını
/// yeniden kuyruğa alan periyodik Hangfire job'ı. İki takılma sınıfını yakalar:
///
/// 1. "sending" + bayat ClaimedAt — gönderim job'ı süreç ölümüyle yarıda
///    kaldı. Yeniden kuyruğa alınır; <see cref="SmsCampaignSendJob"/> claim'i
///    devralıp yalnız "pending" alıcıları gönderir.
/// 2. "pending" + oluşturulalı belli süre geçmiş — kampanya yazıldı ama
///    Enqueue hiç gerçekleşmedi (Create ile Enqueue arasında çökme, F09'un
///    kayıp-enqueue boşluğu). Kredi rezerve edilmiş ama tek SMS gitmemiş
///    olurdu; yeniden kuyruğa almak boşluğu kapatır.
///
/// Çifte enqueue zararsızdır: gönderim job'ı claim CAS'ı + yalnız-pending
/// alıcı seçimiyle idempotent.
/// </summary>
public sealed class SmsCampaignRecoveryJob
{
    /// <summary>"pending" kampanyanın kayıp-enqueue sayılması için yaşı.
    /// Normal akışta Enqueue, Create yanıtından önce gerçekleşir; 2 dk
    /// yalnız gerçekten kopmuş olanları yakalar.</summary>
    public static readonly TimeSpan PendingGrace = TimeSpan.FromMinutes(2);

    private readonly LicenseDbContext _db;
    private readonly IBackgroundJobClient _jobs;
    private readonly ILogger<SmsCampaignRecoveryJob> _log;

    public SmsCampaignRecoveryJob(
        LicenseDbContext db, IBackgroundJobClient jobs, ILogger<SmsCampaignRecoveryJob> log)
    {
        _db = db;
        _jobs = jobs;
        _log = log;
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var staleClaim = now - SmsCampaignSendJob.ClaimLease;
        var pendingCutoff = now - PendingGrace;

        var stuck = await _db.SmsCampaigns
            .Where(c =>
                (c.Status == "sending" && (c.ClaimedAt == null || c.ClaimedAt < staleClaim))
                || (c.Status == "pending" && c.CreatedAt < pendingCutoff))
            .Select(c => new { c.Id, c.Status, c.ClaimedAt, c.CreatedAt })
            .ToListAsync(ct);

        foreach (var c in stuck)
        {
            _log.LogWarning(
                "SmsCampaignRecoveryJob: re-enqueueing stuck campaign {Id} (status={Status}, claimedAt={ClaimedAt}, createdAt={CreatedAt})",
                c.Id, c.Status, c.ClaimedAt, c.CreatedAt);
            _jobs.Enqueue<SmsCampaignSendJob>(j => j.RunAsync(c.Id, CancellationToken.None));
        }
    }
}
