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
    // nonConsenting = bağlı ama SmsConsent=false shopper (elenmeli);
    // verifiedAccount = lisansın doğrulanmış NetgsmAccount'u var mı (kampanya
    // kapısı — kredi sistemi emekli, kapı artık kurulum durumu).
    private async Task<(HttpClient client, Guid licenseId)> SetupAsync(
        int consenting = 0, int nonConsenting = 0, bool verifiedAccount = true)
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

        var accounts = scope.ServiceProvider.GetRequiredService<NetgsmAccountService>();
        var brandCode = Random.Shared.Next(100000, 999999).ToString();
        if (verifiedAccount)
        {
            db.NetgsmAccounts.Add(new NetgsmAccount
            {
                Id = Guid.NewGuid(),
                LicenseId = licenseId,
                UserCode = $"user-{Guid.NewGuid():N}",
                PasswordProtected = accounts.ProtectPassword($"pw-{Guid.NewGuid():N}"),
                Header = "ORDERDECK",
                BrandCode = brandCode,
                Status = NetgsmAccountStatus.Verified,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
        }

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

            if (!consent) return;

            // Gönderim kapısı (kural 7) gönderim ANINDA İYS satırını okur:
            // yerel onay TEK BAŞINA yetmez, İYS'nin de ONAY demiş olması
            // gerekir. Bu testler gönderimin gerçekleştiğini ölçüyor, o yüzden
            // izinli shopper'ın doğrulanmış kaydı da kurulmalı. BrandCode
            // yukarıda bu lisans için üretilen marka — kapı artık kampanyanın
            // lisansından markayı çözüyor.
            var now = DateTimeOffset.UtcNow;
            db.IysConsents.Add(new IysConsent
            {
                Id = Guid.NewGuid(),
                BrandCode = brandCode,
                ChannelType = "MESAJ",
                RecipientType = "BIREYSEL",
                Recipient = shopper.Phone,
                Status = IysConsentStatus.Onay,
                LastVerifiedStatus = IysConsentStatus.Onay,
                LastVerifiedAt = now,
                PushState = IysPushState.Confirmed,
                ConsentDate = now,
                LastLocalEventAt = now,
                CreatedAt = now,
                UpdatedAt = now,
            });
        }

        for (var i = 0; i < consenting; i++) AddShopperLink(true);
        for (var i = 0; i < nonConsenting; i++) AddShopperLink(false);

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
        var (client, licenseId) = await SetupAsync(consenting: 3, nonConsenting: 6);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", new { messageBody = "Merhaba" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PreviewResponse>();
        body!.RecipientCount.Should().Be(3);          // sadece izinli + bağlı shopper
        body.SegmentsPerMessage.Should().Be(1);
        body.TotalCredits.Should().Be(3);
        // Kredi emekli: alan eski WPF istemcisi için JSON'da sabit 0 yaşar.
        body.CreditsRemaining.Should().Be(0);
        // Sufficient artık "kurulum hazır mı": doğrulanmış NetgsmAccount var.
        body.Sufficient.Should().BeTrue();
    }

    [Fact]
    public async Task Preview_without_verified_account_reports_insufficient()
    {
        // Eski WPF'in Gönder düğmesi Sufficient'a bağlı: doğrulanmamış
        // kurulumda alan false dönmeli ki eski istemci de doğru bloklansın.
        var (client, licenseId) = await SetupAsync(consenting: 3, verifiedAccount: false);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", new { messageBody = "Merhaba" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PreviewResponse>();
        body!.RecipientCount.Should().Be(3);
        body.Sufficient.Should().BeFalse("doğrulanmış NetgsmAccount yok — kapı Preview'dan da görünmeli");
        body.CreditsRemaining.Should().Be(0);
    }

    [Fact]
    public async Task Preview_turkish_message_uses_ucs2_segments()
    {
        var (client, licenseId) = await SetupAsync(consenting: 2);
        // 71 Türkçe karakter → 2 segment (UCS-2)
        var msg = new string('ş', 71);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/preview", new { messageBody = msg });
        var body = await resp.Content.ReadFromJsonAsync<PreviewResponse>();
        body!.SegmentsPerMessage.Should().Be(2);
        body.TotalCredits.Should().Be(4);             // 2 alıcı × 2 segment
    }

    [Fact]
    public async Task Create_writes_campaign_and_recipients_without_credit_service()
    {
        // Sözleşme 17: kampanya + alıcı satırları kredi servisi OLMADAN tek
        // SaveChanges ile yazılır, gönderim job'ı sonra kuyruğa atılır.
        var (client, licenseId) = await SetupAsync(consenting: 5, nonConsenting: 2);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Kampanya" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<CreateResponse>();
        body!.RecipientCount.Should().Be(5);
        body.TotalCredits.Should().Be(5);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Alıcı snapshot'ı
        (await db.SmsCampaignRecipients.CountAsync(r => r.CampaignId == body.CampaignId))
            .Should().Be(5);
        var campaign = await db.SmsCampaigns.FirstAsync(c => c.Id == body.CampaignId);
        campaign.Status.Should().Be("pending");
        campaign.ReservedCredits.Should().Be(0, "kredi emekli — rezervasyon yazılmamalı");

        // Hangfire enqueue çağrıldı: testte server koşmadığı için job
        // "enqueued" durumda bekler ve memory storage'dan okunabilir.
        var monitoring = _factory.Services
            .GetRequiredService<Hangfire.JobStorage>().GetMonitoringApi();
        monitoring.EnqueuedJobs("default", 0, 1000).Should().Contain(j =>
            j.Value.Job.Type == typeof(SmsCampaignSendJob) &&
            j.Value.Job.Args.Contains((object)body.CampaignId));
    }

    [Fact]
    public async Task Create_without_verified_account_returns_409_and_writes_nothing()
    {
        // §3.2 kapısı Create'te: doğrulanmamış kurulumda kampanyayı açıp hemen
        // duraklatmak yerine hiç açmamak — yayıncı hatayı anında görür.
        var (client, licenseId) = await SetupAsync(consenting: 3, verifiedAccount: false);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Selam" });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        problem!.Title.Should().Be("netgsm-account-missing");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.SmsCampaigns.CountAsync(c => c.LicenseId == licenseId))
            .Should().Be(0, "kapıya takılan istek kampanya satırı bırakmamalı");
    }

    private sealed record ProblemBody(string Title, string? Detail);

    [Fact]
    public async Task Create_no_recipients_returns_409()
    {
        var (client, licenseId) = await SetupAsync(nonConsenting: 8);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Selam" });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Create_empty_message_returns_400()
    {
        var (client, licenseId) = await SetupAsync(consenting: 2);
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "   " });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Job_sends_to_all_recipients_on_success()
    {
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 4);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Indirim!" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        // Job'ı doğrudan çalıştır (testte Hangfire server yok)
        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
        }

        _factory.TenantSms.Sent.Should().HaveCount(4);
        _factory.TenantSms.Sent.Should().OnlyContain(m => m.Text == "Indirim!");

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create.CampaignId}");
        status!.Status.Should().Be("completed");
        status.Sent.Should().Be(4);
        status.Failed.Should().Be(0);
        status.CreditsRefunded.Should().Be(0);
    }

    [Fact]
    public async Task Job_all_sends_fail_marks_failed_without_refund()
    {
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 4);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Selam" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        // Belirsiz (ağ) hata: SMS gitmiş OLABİLİR → failed, döngü sürer (§3.4).
        _factory.TenantSms.FailAllWith = new HttpRequestException("baglanti koptu");
        try
        {
            using var scope = _factory.Services.CreateScope();
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
        }
        finally { _factory.TenantSms.FailAllWith = null; }

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create!.CampaignId}");
        status!.Status.Should().Be("completed");
        status.Failed.Should().Be(4);
        status.Sent.Should().Be(0);
        status.CreditsRefunded.Should().Be(0,
            "kredi sistemi emekli (Plan 3) — job iade yazmaz, alan eski istemci için sabit");
    }

    [Fact]
    public async Task Job_is_idempotent_after_completion()
    {
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 2);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Tek sefer" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
            await job.RunAsync(create.CampaignId, default);   // ikinci çağrı no-op olmalı
        }

        _factory.TenantSms.Sent.Should().HaveCount(2, "ikinci job çağrısı tekrar göndermemeli");
    }

    // ── F09 (denetim 2026-09-09): Create idempotency ─────────────────────────
    //
    // WPF'in HttpClient'ı AddStandardResilienceHandler kullanıyor: 5xx/ağ
    // hatasında AYNI gövdeyi yeniden gönderir. Anahtar olmadan kaybolan her
    // yanıt = ikinci kampanya + ikinci kredi rezervi demekti. Aynı
    // ClientRequestId'li tekrar, var olan kampanyanın yanıtını döndürmeli.
    // (Eşzamanlı yarış tarafı SmsCampaignIdempotencyConcurrencyTests'te —
    // unique index InMemory'de uygulanmadığı için gerçek SQL Server'da.)

    [Fact]
    public async Task Create_same_client_request_id_returns_existing_campaign()
    {
        var (client, licenseId) = await SetupAsync(consenting: 3);
        var key = Guid.NewGuid();

        var first = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns",
            new { messageBody = "Ayni eylem", clientRequestId = key }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        var retryResp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns",
            new { messageBody = "Ayni eylem", clientRequestId = key });
        retryResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var retry = await retryResp.Content.ReadFromJsonAsync<CreateResponse>();

        retry!.CampaignId.Should().Be(first!.CampaignId,
            "aynı anahtarın tekrarı var olan kampanyayı döndürmeli");
        // ReservedCredits artık yazılmıyor; tekrar yanıtındaki TotalCredits
        // alıcı × segment'ten hesaplanmalı (eski istemci alanı bilgi amaçlı okur).
        retry.TotalCredits.Should().Be(retry.RecipientCount * 1,
            "TotalCredits = RecipientCount × SegmentsPerMessage olmalı");
        retry.TotalCredits.Should().Be(first.TotalCredits);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.SmsCampaigns.CountAsync(c => c.LicenseId == licenseId))
            .Should().Be(1, "retry ikinci kampanya açmamalı");
    }

    // ── F08 (denetim 2026-09-09): "sending"de takılma + kaldığı yerden devam ──
    //
    // Eski davranış: job yalnız "pending" kabul ediyor, sonuçları tek toplu
    // SaveChanges ile yazıyordu. Süreç ölürse kampanya sonsuza dek "sending"
    // kalıyor, krediler rezervede kilitleniyordu; elle "pending"e çekmek ise
    // gönderilmiş SMS'leri TEKRAR gönderirdi. Yeni sözleşme: bayat claim'li
    // "sending" devralınır, yalnız "pending" alıcılar gönderilir, iade DB'deki
    // failed sayısından hesaplanır.

    [Fact]
    public async Task Status_gonderim_surerken_iadeyi_gerceklesmis_gibi_raporlamaz()
    {
        // N05 (2026-09-10 denetimi): CreditsRefunded "failed × segment" HESABI
        // değil, gerçekleşen iade olmalı. İade yalnız kampanya tamamlanırken
        // yapılır; "sending" sırasında failed'lar birikmişken eski kod daha
        // yapılmamış iadeyi "iade edildi" diye gösteriyordu.
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 4);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Rapor" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        // Gönderim ortası anını kur: sending + 2 failed, iade henüz YOK.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.FirstAsync(c => c.Id == create!.CampaignId);
            campaign.Status = "sending";
            campaign.ClaimedAt = DateTimeOffset.UtcNow;
            var recipients = await db.SmsCampaignRecipients
                .Where(r => r.CampaignId == create!.CampaignId).ToListAsync();
            recipients[0].Status = "failed"; recipients[0].Error = "boom";
            recipients[1].Status = "failed"; recipients[1].Error = "boom";
            await db.SaveChangesAsync();
        }

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create!.CampaignId}");
        status!.Failed.Should().Be(2);
        status.CreditsRefunded.Should().Be(0,
            "iade kampanya tamamlanırken yapılır; sürerken 'iade edildi' raporlanamaz");

        var list = await client.GetFromJsonAsync<List<ListItem>>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns");
        list!.Single(c => c.CampaignId == create.CampaignId).CreditsRefunded
            .Should().Be(0, "liste de gerçekleşen iadeyi göstermeli");
    }

    [Fact]
    public async Task Job_resumes_stale_sending_campaign_without_resending()
    {
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 4);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Devam" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        // Önceki koşu 2 alıcıya göndermiş, 1'i başarısız olmuş, 1'i sıradayken
        // süreç ölmüş gibi kur: status=sending + bayat ClaimedAt.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.FirstAsync(c => c.Id == create!.CampaignId);
            campaign.Status = "sending";
            campaign.ClaimedAt = DateTimeOffset.UtcNow - SmsCampaignSendJob.ClaimLease - TimeSpan.FromMinutes(1);
            var recipients = await db.SmsCampaignRecipients
                .Where(r => r.CampaignId == create!.CampaignId).ToListAsync();
            recipients[0].Status = "sent"; recipients[0].SentAt = DateTimeOffset.UtcNow;
            recipients[1].Status = "sent"; recipients[1].SentAt = DateTimeOffset.UtcNow;
            recipients[2].Status = "failed"; recipients[2].Error = "boom";
            await db.SaveChangesAsync();
        }

        _factory.TenantSms.Clear();
        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
        }

        // Yalnız sıradaki 1 alıcıya gönderildi — gönderilmişler tekrarlanmadı.
        _factory.TenantSms.Sent.Should().HaveCount(1,
            "devralınan koşu yalnız pending alıcıları göndermeli");

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create.CampaignId}");
        status!.Status.Should().Be("completed");
        status.Sent.Should().Be(3);
        status.Failed.Should().Be(1);
        status.CreditsRefunded.Should().Be(0,
            "kredi sistemi emekli (Plan 3) — job iade yazmaz");
    }

    [Fact]
    public async Task Job_does_not_steal_fresh_sending_claim()
    {
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 2);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Canli" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        // Başka bir işçi kampanyayı AZ ÖNCE üstlenmiş: claim taze.
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.FirstAsync(c => c.Id == create!.CampaignId);
            campaign.Status = "sending";
            campaign.ClaimedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        _factory.TenantSms.Clear();
        using (var scope = _factory.Services.CreateScope())
        {
            var job = scope.ServiceProvider.GetRequiredService<SmsCampaignSendJob>();
            await job.RunAsync(create!.CampaignId, default);
        }

        _factory.TenantSms.Sent.Should().BeEmpty("taze claim'li kampanya çalınmamalı");
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.SmsCampaigns.FirstAsync(c => c.Id == create!.CampaignId))
            .Status.Should().Be("sending", "kampanya sahibi işçide kalmalı");
    }

    [Fact]
    public async Task Preview_other_license_returns_404()
    {
        var (clientA, _) = await SetupAsync(consenting: 1);
        var (_, licenseB) = await SetupAsync(consenting: 1);

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
        _factory.TenantSms.Clear();
        var (client, licenseId) = await SetupAsync(consenting: 3);

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
        var (clientA, _) = await SetupAsync(consenting: 1);
        var (_, licenseB) = await SetupAsync(consenting: 1);

        var resp = await clientA.GetAsync($"/api/v1/licenses/{licenseB}/sms-campaigns");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Status_and_list_report_credits_refunded_as_zero()
    {
        // Eski WPF istemcisi CreditsRefunded alanını parse ediyor; kredi
        // emekli — DB'de geçiş döneminden kalma bir iade değeri olsa bile
        // JSON'da sabit 0 dönmeli.
        var (client, licenseId) = await SetupAsync(consenting: 2);

        var create = await (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/sms-campaigns", new { messageBody = "Sabit alan" }))
            .Content.ReadFromJsonAsync<CreateResponse>();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var campaign = await db.SmsCampaigns.FirstAsync(c => c.Id == create!.CampaignId);
            campaign.RefundedCredits = 7;   // eski sistemden kalmış olabilir
            await db.SaveChangesAsync();
        }

        var status = await client.GetFromJsonAsync<StatusResponse>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns/{create!.CampaignId}");
        status!.CreditsRefunded.Should().Be(0, "alan eski istemci için sabit 0");

        var list = await client.GetFromJsonAsync<List<ListItem>>(
            $"/api/v1/licenses/{licenseId}/sms-campaigns");
        list!.Single(c => c.CampaignId == create.CampaignId).CreditsRefunded
            .Should().Be(0, "liste de sabit 0 döndürmeli");
    }
}
