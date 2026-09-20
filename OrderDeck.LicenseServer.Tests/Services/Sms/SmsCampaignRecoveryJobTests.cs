using FluentAssertions;
using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sms;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Sms;

/// <summary>
/// F08/F09 güvenlik ağı: <see cref="SmsCampaignRecoveryJob"/> takılı
/// kampanyaları (bayat "sending" + kayıp-enqueue "pending") yeniden kuyruğa
/// almalı; sağlıklı olanlara dokunmamalı.
/// </summary>
public class SmsCampaignRecoveryJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public SmsCampaignRecoveryJobTests(ApiFactory factory) => _factory = factory;

    /// <summary>Enqueue çağrılarını kaydeden IBackgroundJobClient — gerçek
    /// Hangfire storage'a yazmadan hangi kampanyaların kuyruğa alındığını
    /// doğrulamak için.</summary>
    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Created { get; } = new();

        public string Create(Job job, IState state)
        {
            Created.Add(job);
            return Guid.NewGuid().ToString("N");
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }

    private async Task<Guid> SeedCampaignAsync(
        LicenseDbContext db, string status, DateTimeOffset? claimedAt, DateTimeOffset createdAt)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"rec-{Guid.NewGuid():N}@example.com",
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-REC-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            MessageBody = "m",
            SegmentsPerMessage = 1,
            RecipientCount = 1,
            ReservedCredits = 1,
            Status = status,
            ClaimedAt = claimedAt,
            CreatedByCustomerId = customer.Id,
            CreatedAt = createdAt,
        };
        db.SmsCampaigns.Add(campaign);
        await db.SaveChangesAsync();
        return campaign.Id;
    }

    /// <summary>
    /// Görev 15 — "paused" bir kampanya + istenen durumlarda alıcılar tohumlar.
    /// Bakiye + ledger satırı da açılır: iadenin gerçekten para hareket ettirip
    /// ettirmediği ancak oradan ölçülebilir.
    /// </summary>
    private static async Task<(Guid CampaignId, Guid LicenseId)> SeedPausedAsync(
        LicenseDbContext db,
        string[] recipientStatuses,
        int segmentsPerMessage,
        int refundedCredits)
    {
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"str-{Guid.NewGuid():N}@example.com",
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-STR-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);

        db.LicenseSmsBalances.Add(new LicenseSmsBalance
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CreditsRemaining = 50,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.LicenseSmsTransactions.Add(new LicenseSmsTransaction
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Amount = 50,
            Kind = "purchase",
            CreatedAt = DateTimeOffset.UtcNow,
        });

        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            MessageBody = "m",
            SegmentsPerMessage = segmentsPerMessage,
            RecipientCount = recipientStatuses.Length,
            ReservedCredits = recipientStatuses.Length * segmentsPerMessage,
            RefundedCredits = refundedCredits,
            Status = "paused",
            // Duraklatma damgayı ilerletir; kurtarma onu GERİ almamalı.
            ClaimedAt = DateTimeOffset.UtcNow,
            CreatedByCustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-30),
        };
        db.SmsCampaigns.Add(campaign);

        foreach (var status in recipientStatuses)
        {
            db.SmsCampaignRecipients.Add(new SmsCampaignRecipient
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                Phone = $"+90555{Random.Shared.Next(1_000_000, 9_999_999)}",
                Status = status,
                Error = status == "failed" ? "provider-rejected" : null,
                SentAt = status == "sent" ? DateTimeOffset.UtcNow : null,
            });
        }

        await db.SaveChangesAsync();
        return (campaign.Id, license.Id);
    }

    private static SmsCampaignRecoveryJob NewRecovery(
        LicenseDbContext db, IBackgroundJobClient jobs)
        => new(
            db,
            jobs,
            new LicenseSmsBalanceService(db),
            NullLogger<SmsCampaignRecoveryJob>.Instance);

    /// <summary>
    /// Görev 15 — tamamlanma anında kapatılan kampanya asılı kalmasın.
    /// Gönderim işi son alıcıyı yazdıktan sonra tamamlama bloğuna girdi, tam o
    /// anda admin kurulumu kapattı: tamamlama yazımı <c>ClaimedAt</c> CAS'ından
    /// düştü, yeniden deneme durum kapısında (paused) çekildi. Kampanya
    /// "duraklatıldı" görünür ama devam edecek hiçbir şeyi yoktur ve
    /// başarısızların kredisi kimseye iade edilmemiştir.
    /// </summary>
    [Fact]
    public async Task Bekleyen_alicisi_olmayan_paused_kampanya_tamamlanir_ve_iade_edilir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, licenseId) = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2, refundedCredits: 0);

        var before = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;
        var previousClaim = (await db.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId)).ClaimedAt!.Value;

        await NewRecovery(db, new RecordingJobClient()).RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("completed");
        after.CompletedAt.Should().NotBeNull();
        after.RefundedCredits.Should().Be(2, "1 failed × 2 segment");
        after.ClaimedAt!.Value.Should().BeAfter(previousClaim,
            "damga ilerlemezse duraklatmadan önce kampanyayı okumuş bayat bir "
            + "işçi sahipliği geri kazanır");

        (await vdb.LicenseSmsBalances.AsNoTracking().SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(before + 2);

        var refunds = await vdb.LicenseSmsTransactions.AsNoTracking()
            .Where(t => t.LicenseId == licenseId && t.Kind == "send-refund").ToListAsync();
        refunds.Should().ContainSingle().Which.Amount.Should().Be(2);
    }

    /// <summary>
    /// Görevin kalbi: bekleyen alıcısı OLAN duraklatılmış kampanya gerçekten
    /// duraklatılmıştır. Onu tamamlamak gitmemiş SMS'leri gitmiş saymak,
    /// rezervasyonu da sahibine geri vermek olurdu — devam ettirildiğinde aynı
    /// kredi ikinci kez harcanır.
    /// </summary>
    [Fact]
    public async Task Bekleyen_alicisi_olan_paused_kampanyaya_dokunulmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, licenseId) = await SeedPausedAsync(
            db, ["failed", "pending"], segmentsPerMessage: 2, refundedCredits: 0);

        var before = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;

        var jobs = new RecordingJobClient();
        await NewRecovery(db, jobs).RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("paused", "kapatma kararı kutsal");
        after.CompletedAt.Should().BeNull();
        after.RefundedCredits.Should().Be(0);

        jobs.Created.Select(j => (Guid)j.Args[0]!).Should().NotContain(campaignId,
            "kurtarma bu kampanyayı kuyruğa da almamalı");

        (await vdb.LicenseSmsBalances.AsNoTracking().SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(before);
        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0);
    }

    /// <summary>
    /// İade İDEMPOTENT: borcu zaten ödenmiş bir kampanya süpürmede ikinci kez
    /// ödenmemeli. Kurulum: gönderim işi iadeyi yazmayı BAŞARDI ama tamamlama
    /// yazımı çakıştı — <c>RefundedCredits</c> dolu, durum hâlâ "paused".
    /// Süpürmeyi iki kez koşuyoruz; ikincisi kampanyayı artık "completed"
    /// gördüğü için sorguya hiç girmemeli.
    /// </summary>
    [Fact]
    public async Task Ikinci_supurme_ikinci_iade_yazmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var (campaignId, licenseId) = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2, refundedCredits: 2);

        var before = (await db.LicenseSmsBalances.AsNoTracking()
            .SingleAsync(b => b.LicenseId == licenseId)).CreditsRemaining;

        await NewRecovery(db, new RecordingJobClient()).RunAsync();
        await NewRecovery(db, new RecordingJobClient()).RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("completed");
        after.RefundedCredits.Should().Be(2, "borç zaten ödenmişti, artmamalı");

        (await vdb.LicenseSmsBalances.AsNoTracking().SingleAsync(b => b.LicenseId == licenseId))
            .CreditsRemaining.Should().Be(before,
                "hak edilen iade zaten ödenmişti; tekrar ödemek krediyi yoktan var eder");
        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseId && t.Kind == "send-refund"))
            .Should().Be(0);
    }

    /// <summary>
    /// Süpürme aynı turda İKİ asılı kampanya bulduğunda, birincinin CAS
    /// çakışması ikincinin kurtarılmasını ENGELLEMEMELİ. Kurulum: A'nın
    /// izleyicideki kopyası bayat (rakip işçi damgayı ilerletti), B sağlam.
    ///
    /// <para>Düzeltmeden önce A'nın düşen iadesi izleyicide asılı kalıyordu
    /// (tx <c>Added</c>, bakiye <c>Modified</c>) ve B'nin <c>SaveChanges</c>'ine
    /// biniyordu; tek çakışma bütün süpürmeyi düşürüyordu.</para>
    ///
    /// <para><b>Para tarafı burada ölçülemez:</b> InMemory transactional
    /// değil — A'nın düşen yazımının bir kısmı store'a işlenmiş olabiliyor.
    /// "Düşen karar kuruş oynatmaz" sözleşmesi gerçek SQL Server'da
    /// doğrulanıyor: <c>SmsBalanceConcurrencyTests.Dusen_karar_ayni_contextin_
    /// sonraki_yazimina_binmez</c>.</para>
    /// </summary>
    [Fact]
    public async Task Bir_kampanyanin_cakismasi_digerinin_kurtarilmasini_engellemez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var (campaignA, _) = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2, refundedCredits: 0);
        var (campaignB, licenseB) = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2, refundedCredits: 0);

        // Rakip işçi A'yı yazar: `db`'de izlenen kopyanın jetonu artık bayat.
        using (var rival = _factory.Services.CreateScope())
        {
            var rdb = rival.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var row = await rdb.SmsCampaigns.SingleAsync(c => c.Id == campaignA);
            row.ClaimedAt = row.ClaimedAt!.Value.AddSeconds(5);
            await rdb.SaveChangesAsync();
        }

        await NewRecovery(db, new RecordingJobClient()).RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        (await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignA))
            .Status.Should().Be("paused", "çakışan karar düşmeliydi");

        var afterB = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignB);
        afterB.Status.Should().Be("completed", "sağlam kampanya çakışmadan etkilenmemeli");
        afterB.RefundedCredits.Should().Be(2);
        (await vdb.LicenseSmsTransactions.AsNoTracking()
            .CountAsync(t => t.LicenseId == licenseB && t.Kind == "send-refund"))
            .Should().Be(1, "B'nin iadesi tam olarak bir kez yazılmalı");
    }

    [Fact]
    public async Task Requeues_stale_sending_and_orphan_pending_only()
    {
        var now = DateTimeOffset.UtcNow;
        var staleClaim = now - SmsCampaignSendJob.ClaimLease - TimeSpan.FromMinutes(1);
        var oldCreate = now - SmsCampaignRecoveryJob.PendingGrace - TimeSpan.FromMinutes(1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Takılı: süreç ölümüyle yarıda kalmış gönderim (bayat claim).
        var staleSending = await SeedCampaignAsync(db, "sending", staleClaim, oldCreate);
        // Takılı: claim'siz "sending" (token'sız eski satır) — o da kurtarılmalı.
        var nullClaimSending = await SeedCampaignAsync(db, "sending", null, oldCreate);
        // Takılı: Create ile Enqueue arasında çökme — hiç kuyruğa girmemiş (F09 boşluğu).
        var orphanPending = await SeedCampaignAsync(db, "pending", null, oldCreate);

        // Sağlıklı: canlı işçinin elindeki kampanya (taze claim).
        var liveSending = await SeedCampaignAsync(db, "sending", now, oldCreate);
        // Sağlıklı: az önce oluşturuldu, Enqueue normal yoldan gerçekleşecek.
        var freshPending = await SeedCampaignAsync(db, "pending", null, now);
        // Sağlıklı: bitmiş kampanya.
        var completed = await SeedCampaignAsync(db, "completed", staleClaim, oldCreate);

        var jobs = new RecordingJobClient();
        await NewRecovery(db, jobs).RunAsync();

        var enqueued = jobs.Created
            .Select(j => (Guid)j.Args[0]!)
            .ToList();

        enqueued.Should().BeEquivalentTo(
            new[] { staleSending, nullClaimSending, orphanPending },
            "yalnız bayat sending + yaşlı pending kurtarılmalı");
        enqueued.Should().NotContain(new[] { liveSending, freshPending, completed });
        jobs.Created.Should().OnlyContain(j => j.Type == typeof(SmsCampaignSendJob));
    }
}
