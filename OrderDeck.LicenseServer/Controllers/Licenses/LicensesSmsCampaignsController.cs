using Hangfire;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Sms;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// Yayıncı toplu SMS kampanyaları. Alıcılar server-side çözülür (lisansın
/// SMS izinli + telefonu olan müşterileri). Kredi = alıcı × mesaj segment.
/// Oluşturmada kredi rezerve edilir, gönderim Hangfire job'ında arka planda
/// yapılır; başarısız alıcılar iade edilir.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/sms-campaigns")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesSmsCampaignsController : ControllerBase
{
    private const int MaxMessageLength = 2000;

    private readonly LicenseDbContext _db;
    private readonly LicenseSmsBalanceService _balance;
    private readonly IBackgroundJobClient _jobs;

    public LicensesSmsCampaignsController(
        LicenseDbContext db, LicenseSmsBalanceService balance, IBackgroundJobClient jobs)
    {
        _db = db;
        _balance = balance;
        _jobs = jobs;
    }

    private async Task<bool> OwnsLicenseAsync(Guid licenseId, CancellationToken ct)
    {
        var callerId = User.GetTenantCustomerId();
        return await _db.Licenses.AnyAsync(l => l.Id == licenseId && l.CustomerId == callerId, ct);
    }

    private sealed record RecipientRow(Guid? WpfCustomerId, string Phone);

    // Alıcılar = bu yayıncıya aktif bağlı + SMS izinli + telefonu olan shopper'lar.
    // Consent kaynağı Shopper'dır (kayıtta otomatik true, app profilinden iptal);
    // WPF müşterisi (WpfCustomerProjection) consent taşımaz.
    private IQueryable<RecipientRow> ConsentedRecipients(Guid licenseId) =>
        from link in _db.ShopperBroadcasterLinks
        where link.LicenseId == licenseId && link.LeftAt == null
        join shopper in _db.Shoppers on link.ShopperId equals shopper.Id
        where shopper.SmsConsent && shopper.DeletedAt == null && shopper.Phone != ""
        select new RecipientRow(link.WpfCustomerId, shopper.Phone);

    public sealed record PreviewRequest(string MessageBody);
    public sealed record PreviewResponse(
        int RecipientCount, int SegmentsPerMessage, int TotalCredits,
        int CreditsRemaining, bool Sufficient);

    [HttpPost("preview")]
    public async Task<IActionResult> Preview(
        Guid licenseId, [FromBody] PreviewRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.MessageBody) || req.MessageBody.Length > MaxMessageLength)
            return Problem(title: "invalid-message", statusCode: 400);
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var recipientCount = await ConsentedRecipients(licenseId)
            .Select(r => r.Phone).Distinct().CountAsync(ct);
        var segments = SmsSegmentCalculator.Segments(req.MessageBody);
        var totalCredits = recipientCount * segments;
        var balance = await _balance.GetAsync(licenseId, ct);

        return Ok(new PreviewResponse(
            recipientCount, segments, totalCredits,
            balance.CreditsRemaining, balance.CreditsRemaining >= totalCredits));
    }

    public sealed record CreateRequest(string MessageBody);
    public sealed record CreateResponse(Guid CampaignId, int RecipientCount, int TotalCredits);

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid licenseId, [FromBody] CreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.MessageBody) || req.MessageBody.Length > MaxMessageLength)
            return Problem(title: "invalid-message", statusCode: 400);
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var rawRecipients = await ConsentedRecipients(licenseId).ToListAsync(ct);
        // Aynı telefonu tek alıcıya indir (defansif — telefon shopper'da unique).
        var recipients = rawRecipients
            .GroupBy(r => r.Phone)
            .Select(g => g.First())
            .ToList();
        if (recipients.Count == 0)
            return Problem(title: "no-recipients", statusCode: 409,
                detail: "Bu lisansta SMS izinli, bağlı ve telefonu olan müşteri yok.");

        var segments = SmsSegmentCalculator.Segments(req.MessageBody);
        var totalCredits = recipients.Count * segments;

        var balance = await _balance.GetAsync(licenseId, ct);
        if (balance.CreditsRemaining < totalCredits)
            return Problem(title: "insufficient-credits", statusCode: 409,
                detail: $"Gerekli {totalCredits} kredi, mevcut {balance.CreditsRemaining}.");

        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        var campaignId = Guid.NewGuid();

        var campaign = new SmsCampaign
        {
            Id = campaignId,
            LicenseId = licenseId,
            MessageBody = req.MessageBody,
            SegmentsPerMessage = segments,
            RecipientCount = recipients.Count,
            ReservedCredits = totalCredits,
            Status = "pending",
            CreatedByCustomerId = customerId,
            CreatedAt = now,
        };
        _db.SmsCampaigns.Add(campaign);

        foreach (var r in recipients)
        {
            _db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                WpfCustomerId = r.WpfCustomerId,
                Phone = r.Phone,
                Status = "pending",
            });
        }

        // Krediyi rezerve et (aynı transaction'da atomik).
        await _balance.ApplyAsync(
            licenseId, -totalCredits, "send-reserve",
            reason: $"campaign:{campaignId}", createdByCustomerId: customerId, ct);

        await _db.SaveChangesAsync(ct);

        _jobs.Enqueue<SmsCampaignSendJob>(j => j.RunAsync(campaignId, CancellationToken.None));

        return Ok(new CreateResponse(campaignId, recipients.Count, totalCredits));
    }

    public sealed record StatusResponse(
        Guid CampaignId, string Status, int RecipientCount,
        int Sent, int Failed, int Skipped, int CreditsRefunded,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    [HttpGet("{campaignId:guid}")]
    public async Task<IActionResult> Status(
        Guid licenseId, Guid campaignId, CancellationToken ct)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        var campaign = await _db.SmsCampaigns
            .FirstOrDefaultAsync(c => c.Id == campaignId && c.LicenseId == licenseId, ct);
        if (campaign is null) return NotFound();

        var counts = await _db.SmsCampaignRecipients
            .Where(r => r.CampaignId == campaignId)
            .GroupBy(r => r.Status)
            .Select(g => new { Status = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        int Count(string s) => counts.FirstOrDefault(c => c.Status == s)?.Count ?? 0;
        var failed = Count("failed");

        return Ok(new StatusResponse(
            campaign.Id, campaign.Status, campaign.RecipientCount,
            Sent: Count("sent"), Failed: failed, Skipped: Count("skipped"),
            CreditsRefunded: failed * campaign.SegmentsPerMessage,
            campaign.CreatedAt, campaign.CompletedAt));
    }

    public sealed record CampaignListItem(
        Guid CampaignId, string Status, string MessagePreview, int RecipientCount,
        int Sent, int Failed, int Skipped, int CreditsRefunded,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    /// <summary>Yayıncının kampanya geçmişi (en yeni önce). WPF "Toplu SMS"
    /// ekranındaki geçmiş listesi. Alıcı durum sayıları tek grouped query ile
    /// çözülür (N+1 yok).</summary>
    [HttpGet]
    public async Task<IActionResult> List(
        Guid licenseId, [FromQuery] int take = 20, CancellationToken ct = default)
    {
        if (!await OwnsLicenseAsync(licenseId, ct)) return NotFound();

        take = Math.Clamp(take, 1, 100);

        var campaigns = await _db.SmsCampaigns
            .Where(c => c.LicenseId == licenseId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(take)
            .ToListAsync(ct);

        if (campaigns.Count == 0)
            return Ok(Array.Empty<CampaignListItem>());

        var ids = campaigns.Select(c => c.Id).ToList();
        var counts = await _db.SmsCampaignRecipients
            .Where(r => ids.Contains(r.CampaignId))
            .GroupBy(r => new { r.CampaignId, r.Status })
            .Select(g => new { g.Key.CampaignId, g.Key.Status, Count = g.Count() })
            .ToListAsync(ct);

        var byCampaign = counts
            .GroupBy(c => c.CampaignId)
            .ToDictionary(g => g.Key, g => g.ToDictionary(x => x.Status, x => x.Count));

        var items = campaigns.Select(c =>
        {
            var map = byCampaign.TryGetValue(c.Id, out var m) ? m : null;
            int Count(string s) => map is not null && map.TryGetValue(s, out var n) ? n : 0;
            var failed = Count("failed");
            return new CampaignListItem(
                c.Id, c.Status,
                MessagePreview: c.MessageBody.Length > 60 ? c.MessageBody[..60] : c.MessageBody,
                c.RecipientCount,
                Sent: Count("sent"), Failed: failed, Skipped: Count("skipped"),
                CreditsRefunded: failed * c.SegmentsPerMessage,
                c.CreatedAt, c.CompletedAt);
        }).ToList();

        return Ok(items);
    }
}
