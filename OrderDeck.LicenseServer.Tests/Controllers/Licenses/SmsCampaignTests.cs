using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class SmsCampaignTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SmsCampaignTests(ApiFactory factory) => _factory = factory;

    // consenting = SMS izinli + bağlı + telefonlu shopper (alıcı);
    // nonConsenting = bağlı ama SmsConsent=false shopper (elenmeli).
    private async Task<(HttpClient client, Guid licenseId)> SetupAsync(
        int consenting = 0, int nonConsenting = 0, int credits = 0)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-CMP-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        licenseId = license.Id;

        void AddShopperLink(bool consent)
        {
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "S " + Guid.NewGuid().ToString("N")[..6],
                Phone = "+90500" + Guid.NewGuid().ToString("N")[..7],
                PasswordHash = "hash",
                Address = "addr",
                SmsConsent = consent,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            db.Shoppers.Add(shopper);
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopper.Id,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "u-" + Guid.NewGuid().ToString("N")[..8],
                JoinedAt = DateTimeOffset.UtcNow,
            });
        }

        for (var i = 0; i < consenting; i++) AddShopperLink(true);
        for (var i = 0; i < nonConsenting; i++) AddShopperLink(false);

        if (credits > 0)
        {
            db.LicenseSmsBalances.Add(new LicenseSmsBalance
            {
                Id = Guid.NewGuid(), LicenseId = licenseId,
                CreditsRemaining = credits, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, Amount = credits,
                Kind = "purchase", CreatedAt = DateTimeOffset.UtcNow,
            });
        }
        await db.SaveChangesAsync();
        return (client, licenseId);
    }

    private sealed record PreviewResponse(
        int RecipientCount, int SegmentsPerMessage, int TotalCredits,
        int CreditsRemaining, bool Sufficient);
    private sealed record CreateResponse(Guid CampaignId, int RecipientCount, int TotalCredits);
    private sealed record StatusResponse(
        Guid CampaignId, string Status, int RecipientCount,
        int Sent, int Failed, int Skipped, int CreditsRefunded,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    [Fact]
    public async Task Preview_counts_only_consenting_linked_shoppers()
    {
        var (client, licenseId) = await SetupAsync(
            consenting: 3, nonConsenting: 6, credits: 100);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", new { messageBody = "Merhaba" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PreviewResponse>();
        body!.RecipientCount.Should().Be(3);          // sadece izinli + bağlı shopper
        body.SegmentsPerMessage.Should().Be(1);
        body.TotalCredits.Should().Be(3);
        body.CreditsRemaining.Should().Be(100);
        body.Sufficient.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_turkish_message_uses_ucs2_segments()
    {
        var (client, licenseId) = await SetupAsync(consenting: 2, credits: 100);
        // 71 Türkçe karakter → 2 segment (UCS-2)
        var msg = new string('ş', 71);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", new { messageBody = msg });
        var body = await resp.Content.ReadFromJsonAsync<PreviewResponse>();
        body!.SegmentsPerMessage.Should().Be(2);
        body.TotalCredits.Should().Be(4);             // 2 alıcı × 2 segment
    }

    [Fact]
    public async Task Create_reserves_credits_and_snapshots_recipients()
    {
        var (client, licenseId) = await SetupAsync(consenting: 5, nonConsenting: 2, credits: 100);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Kampanya" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<CreateResponse>();
        body!.RecipientCount.Should().Be(5);
        body.TotalCredits.Should().Be(5);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Kredi rezerve edildi: 100 - 5 = 95
        (await db.LicenseSmsBalances.FirstAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(95);
        // Alıcı snapshot'ı
        (await db.SmsCampaignRecipients.CountAsync(r => r.CampaignId == body.CampaignId))
            .Should().Be(5);
        var campaign = await db.SmsCampaigns.FirstAsync(c => c.Id == body.CampaignId);
        campaign.Status.Should().Be("pending");
        campaign.ReservedCredits.Should().Be(5);
    }

    [Fact]
    public async Task Create_insufficient_credits_returns_409()
    {
        var (client, licenseId) = await SetupAsync(consenting: 10, credits: 3);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Selam" });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Bakiye dokunulmadı
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.LicenseSmsBalances.FirstAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(3);
    }

    [Fact]
    public async Task Create_no_recipients_returns_409()
    {
        var (client, licenseId) = await SetupAsync(nonConsenting: 8, credits: 100);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Selam" });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_empty_message_returns_400()
    {
        var (client, licenseId) = await SetupAsync(consenting: 2, credits: 100);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "   " });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Job_sends_to_all_recipients_on_success()
    {
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;
        var (client, licenseId) = await SetupAsync(consenting: 4, credits: 100);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Indirim!" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        // Job'ı doğrudan çalıştır (testte Hangfire server yok)
        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
        }

        _factory.Sms.Sent.Should().HaveCount(4);
        _factory.Sms.Sent.Should().OnlyContain(m => m.Text == "Indirim!");

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create.CampaignId}");
        status!.Status.Should().Be("completed");
        status.Sent.Should().Be(4);
        status.Failed.Should().Be(0);
        status.CreditsRefunded.Should().Be(0);

        // Başarıda iade yok: 100 - 4 = 96
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.LicenseSmsBalances.FirstAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(96);
    }

    [Fact]
    public async Task Job_refunds_credits_when_all_sends_fail()
    {
        _factory.Sms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 4, credits: 100);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Selam" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        _factory.Sms.ThrowOnSend = true;   // tüm gönderimler patlar
        try
        {
            using var scope = _factory.Services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
        }
        finally { _factory.Sms.ThrowOnSend = false; }

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create!.CampaignId}");
        status!.Status.Should().Be("completed");
        status.Failed.Should().Be(4);
        status.Sent.Should().Be(0);
        status.CreditsRefunded.Should().Be(4);

        // Tüm başarısız → tam iade: 100 - 4 (rezerve) + 4 (iade) = 100
        using var verify = _factory.Services.CreateScope();
        var db = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.LicenseSmsBalances.FirstAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(100);
    }

    [Fact]
    public async Task Job_is_idempotent_after_completion()
    {
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;
        var (client, licenseId) = await SetupAsync(consenting: 2, credits: 100);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Tek sefer" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
            await job.RunAsync(create.CampaignId, default);   // ikinci çağrı no-op olmalı
        }

        _factory.Sms.Sent.Should().HaveCount(2, "ikinci job çağrısı tekrar göndermemeli");
    }

    [Fact]
    public async Task Preview_other_license_returns_404()
    {
        var (clientA, _) = await SetupAsync(consenting: 1, credits: 100);
        var (_, licenseB) = await SetupAsync(consenting: 1, credits: 100);

        var resp = await clientA.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseB}/sms-campaigns/preview", new { messageBody = "x" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record ListItem(
        Guid CampaignId, string Status, string MessagePreview, int RecipientCount,
        int Sent, int Failed, int Skipped, int CreditsRefunded,
        DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

    [Fact]
    public async Task List_returns_campaigns_newest_first_with_counts()
    {
        _factory.Sms.Clear();
        _factory.Sms.ThrowOnSend = false;
        var (client, licenseId) = await SetupAsync(consenting: 3, credits: 100);

        // Eski kampanya (gönderilmiş → sent sayıları dolu)
        var first = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Eski kampanya" }))
            .Content.ReadFromJsonAsync<CreateResponse>();
        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(first!.CampaignId, default);
        }

        // Yeni kampanya (pending)
        var second = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Yeni kampanya" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        var list = await client.GetFromJsonAsync<List<ListItem>>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns?take=20");

        list.Should().HaveCount(2);
        list![0].CampaignId.Should().Be(second!.CampaignId);   // en yeni önce
        list[0].MessagePreview.Should().Be("Yeni kampanya");
        list[1].CampaignId.Should().Be(first.CampaignId);
        list[1].Sent.Should().Be(3);
        list[1].Status.Should().Be("completed");
    }

    [Fact]
    public async Task List_other_license_returns_404()
    {
        var (clientA, _) = await SetupAsync(consenting: 1, credits: 100);
        var (_, licenseB) = await SetupAsync(consenting: 1, credits: 100);

        var resp = await clientA.GetAsync($"/api/v1/licenses/{licenseB}/sms-campaigns");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
