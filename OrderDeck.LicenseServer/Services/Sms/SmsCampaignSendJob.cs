using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Sms;

/// <summary>
/// Hangfire job: bir <see cref="Domain.SmsCampaign"/>'in alıcılarına SMS gönderir.
/// Kampanya oluşturulurken krediler rezerve edilmiştir; burada alıcı başına
/// gönderim yapılır ve başarısız/atlanan alıcılar için kredi iade edilir
/// (yalnızca kabul edilen gönderim ücretlenir).
///
/// Idempotent: yalnızca Status == "pending" kampanyada çalışır (Hangfire retry
/// veya çift enqueue'da tekrar göndermez).
/// </summary>
public sealed class SmsCampaignSendJob
{
    private readonly LicenseDbContext _db;
    private readonly ISmsSender _sms;
    private readonly LicenseSmsBalanceService _balance;
    private readonly ILogger<SmsCampaignSendJob> _log;

    public SmsCampaignSendJob(
        LicenseDbContext db,
        ISmsSender sms,
        LicenseSmsBalanceService balance,
        ILogger<SmsCampaignSendJob> log)
    {
        _db = db;
        _sms = sms;
        _balance = balance;
        _log = log;
    }

    public async Task RunAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await _db.SmsCampaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null)
        {
            _log.LogWarning("SmsCampaignSendJob: campaign {Id} not found", campaignId);
            return;
        }
        if (campaign.Status != "pending")
        {
            _log.LogInformation("SmsCampaignSendJob: campaign {Id} status={Status}, skipping",
                campaignId, campaign.Status);
            return;
        }

        campaign.Status = "sending";
        await _db.SaveChangesAsync(ct);

        var recipients = await _db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId)
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        var failedCount = 0;

        foreach (var r in recipients)
        {
            try
            {
                await _sms.SendAsync(r.Phone, campaign.MessageBody, ct);
                r.Status = "sent";
                r.SentAt = DateTimeOffset.UtcNow;
                r.Error = null;
            }
            catch (Exception ex)
            {
                r.Status = "failed";
                r.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                failedCount++;
                _log.LogWarning(ex, "SmsCampaignSendJob: send failed for campaign {Id} recipient {RecipientId}",
                    campaignId, r.Id);
            }
        }

        // Başarısız alıcılar için kredi iadesi — yalnızca kabul edilen gönderim ücretlenir.
        if (failedCount > 0)
        {
            var refund = failedCount * campaign.SegmentsPerMessage;
            await _balance.ApplyAsync(
                campaign.LicenseId, refund, "send-refund",
                reason: $"campaign:{campaignId} failed={failedCount}",
                createdByCustomerId: null, ct);
        }

        campaign.Status = "completed";
        campaign.CompletedAt = now;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation(
            "SmsCampaignSendJob: campaign {Id} completed — {Sent} sent, {Failed} failed",
            campaignId, recipients.Count - failedCount, failedCount);
    }
}
