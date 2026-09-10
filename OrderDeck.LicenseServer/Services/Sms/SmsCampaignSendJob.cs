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
/// F08 (denetim 2026-09-09) — kesintiye dayanıklılık:
/// - Kampanya CAS ile üstlenilir (ClaimedAt concurrency token). Yarışı
///   kaybeden job iz bırakmadan çıkar → aynı kampanyayı iki işçi işleyemez.
/// - Her alıcının sonucu ANINDA kaydedilir (tek toplu SaveChanges değil) ve
///   ClaimedAt tazelenir (lease kalp atışı). Süreç ölürse en fazla 1 alıcı
///   belirsiz kalır; kalanı "pending" durur.
/// - Job "sending"de takılı kalmış kampanyayı da kabul eder — lease
///   (<see cref="ClaimLease"/>) bayatladıysa devralır ve yalnız "pending"
///   alıcıları gönderir: gönderilmiş SMS tekrarlanmaz.
/// - İade tutarı bellekteki sayaçtan değil DB'deki failed sayısından
///   hesaplanır → devralınan koşuda da doğru.
/// </summary>
public sealed class SmsCampaignSendJob
{
    /// <summary>
    /// Bir claim'in bayat sayılması için geçmesi gereken süre. Kalp atışı
    /// alıcı başına attığı için canlı bir job'ın damgası bundan çok daha
    /// tazedir; 15 dk yalnız ölü süreçleri yakalar.
    /// </summary>
    public static readonly TimeSpan ClaimLease = TimeSpan.FromMinutes(15);

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

        var now = DateTimeOffset.UtcNow;
        var staleSending = campaign.Status == "sending"
            && (campaign.ClaimedAt is null || now - campaign.ClaimedAt >= ClaimLease);
        if (campaign.Status != "pending" && !staleSending)
        {
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} status={Status} claimedAt={ClaimedAt}, skipping",
                campaignId, campaign.Status, campaign.ClaimedAt);
            return;
        }

        var resumed = campaign.Status == "sending";
        campaign.Status = "sending";
        campaign.ClaimedAt = now;
        try
        {
            // ClaimedAt concurrency token → bu SaveChanges bir CAS: aynı anda
            // ikinci bir job da claim'liyorsa yalnız biri geçer.
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            _log.LogInformation(
                "SmsCampaignSendJob: campaign {Id} claimed by another worker, skipping", campaignId);
            return;
        }

        if (resumed)
            _log.LogWarning(
                "SmsCampaignSendJob: campaign {Id} resumed from stale 'sending' state", campaignId);

        // Yalnız henüz sonuçlanmamış alıcılar — devralınan koşuda gönderilmiş
        // SMS tekrarlanmaz.
        var recipients = await _db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId && r.Status == "pending")
            .ToListAsync(ct);

        foreach (var r in recipients)
        {
            try
            {
                // Kampanya = ticari ileti → İYS filtresi "11" (Commercial).
                await _sms.SendAsync(r.Phone, campaign.MessageBody, SmsKind.Commercial, ct);
                r.Status = "sent";
                r.SentAt = DateTimeOffset.UtcNow;
                r.Error = null;
            }
            catch (Exception ex)
            {
                r.Status = "failed";
                r.Error = ex.Message.Length > 500 ? ex.Message[..500] : ex.Message;
                _log.LogWarning(ex, "SmsCampaignSendJob: send failed for campaign {Id} recipient {RecipientId}",
                    campaignId, r.Id);
            }

            // Alıcı sonucu ANINDA diske iner; ClaimedAt tazelenir (kalp atışı).
            // Süreç burada ölürse kalan alıcılar "pending" kalır ve recovery
            // job'ı kaldığı yerden devam ettirir.
            campaign.ClaimedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }

        // İade, bu koşunun sayacından değil DB'deki toplam failed sayısından:
        // devralınan koşuda önceki koşunun failed'ları da iade edilmeli
        // (önceki koşu tamamlanamadığı için hiç iade yapmamıştı).
        var failedCount = await _db.SmsCampaignRecipients
            .CountAsync(r => r.CampaignId == campaignId && r.Status == "failed", ct);

        campaign.Status = "completed";
        campaign.CompletedAt = DateTimeOffset.UtcNow;

        // Başarısız alıcılar için kredi iadesi — yalnızca kabul edilen gönderim
        // ücretlenir. Kampanya sonucu + iade tek SaveChanges'te yazılır:
        // status güncellemesi ApplyAndSaveAsync'in kaydına biner.
        if (failedCount > 0)
        {
            var refund = failedCount * campaign.SegmentsPerMessage;
            await _balance.ApplyAndSaveAsync(
                campaign.LicenseId, refund, "send-refund",
                reason: $"campaign:{campaignId} failed={failedCount}",
                createdByCustomerId: null, disallowNegative: false, ct);
        }
        else
        {
            await _db.SaveChangesAsync(ct);
        }

        _log.LogInformation(
            "SmsCampaignSendJob: campaign {Id} completed — {Sent} sent this run, {Failed} failed total",
            campaignId, recipients.Count(r => r.Status == "sent"), failedCount);
    }
}
