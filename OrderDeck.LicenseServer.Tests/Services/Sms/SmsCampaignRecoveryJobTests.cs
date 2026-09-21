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
/// almalı; sağlıklı olanlara ve devam ettirilmiş (taze damgalı) olanlara
/// dokunmamalı. Kredi sistemi emekli (Plan 3): kurtarma artık iade YAZMAZ.
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
    /// </summary>
    private static async Task<Guid> SeedPausedAsync(
        LicenseDbContext db,
        string[] recipientStatuses,
        int segmentsPerMessage)
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

        var campaign = new SmsCampaign
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            MessageBody = "m",
            SegmentsPerMessage = segmentsPerMessage,
            RecipientCount = recipientStatuses.Length,
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
        return campaign.Id;
    }

    private static SmsCampaignRecoveryJob NewRecovery(
        LicenseDbContext db, IBackgroundJobClient jobs)
        => new(db, jobs, NullLogger<SmsCampaignRecoveryJob>.Instance);

    /// <summary>
    /// Görev 15 — tamamlanma anında kapatılan kampanya asılı kalmasın.
    /// Gönderim işi son alıcıyı yazdıktan sonra tamamlama bloğuna girdi, tam o
    /// anda admin kurulumu kapattı: tamamlama yazımı <c>ClaimedAt</c> CAS'ından
    /// düştü, yeniden deneme durum kapısında (paused) çekildi. Kampanya
    /// "duraklatıldı" görünür ama devam edecek hiçbir şeyi yoktur.
    /// Plan 3: kredi sistemi emekli — tamamlama İADESİZ olmalı.
    /// </summary>
    [Fact]
    public async Task Bekleyen_alicisi_olmayan_paused_kampanya_iadesiz_tamamlanir()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaignId = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2);

        var previousClaim = (await db.SmsCampaigns.AsNoTracking()
            .SingleAsync(c => c.Id == campaignId)).ClaimedAt!.Value;

        await NewRecovery(db, new RecordingJobClient()).RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("completed");
        after.CompletedAt.Should().NotBeNull();
        after.ClaimedAt!.Value.Should().BeAfter(previousClaim,
            "damga ilerlemezse duraklatmadan önce kampanyayı okumuş bayat bir "
            + "işçi sahipliği geri kazanır");
    }

    /// <summary>
    /// Sözleşme 6'nın kalbi: bekleyen alıcısı OLAN duraklatılmış kampanya
    /// gerçekten duraklatılmıştır. Onu tamamlamak gitmemiş SMS'leri gitmiş
    /// saymak olurdu; kuyruğa almak da admin'in kapatma kararını sessizce
    /// geri almak olurdu.
    /// </summary>
    [Fact]
    public async Task Bekleyen_alicisi_olan_paused_kampanyaya_dokunulmaz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var campaignId = await SeedPausedAsync(
            db, ["failed", "pending"], segmentsPerMessage: 2);

        var jobs = new RecordingJobClient();
        await NewRecovery(db, jobs).RunAsync();

        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var after = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignId);
        after.Status.Should().Be("paused", "kapatma kararı kutsal");
        after.CompletedAt.Should().BeNull();

        jobs.Created.Select(j => (Guid)j.Args[0]!).Should().NotContain(campaignId,
            "kurtarma bu kampanyayı kuyruğa da almamalı");
    }

    /// <summary>
    /// §3.4 düzeltme 2: devam ettirilen (paused→pending) kampanyanın damgası
    /// TAZE olur — <c>StageResumePausedCampaignsAsync</c> damgayı ilerletir.
    /// Damga filtresi olmadan böyle bir kampanya, <c>CreatedAt</c>'i eski
    /// olduğu için HER süpürmede yeniden kuyruklanırdı. Taze damgalı pending
    /// atlanmalı; damgası bayat ya da hiç olmayan pending kurtarılmalı.
    /// </summary>
    [Fact]
    public async Task Devam_ettirilen_taze_damgali_pending_yeniden_kuyruklanmaz()
    {
        var now = DateTimeOffset.UtcNow;
        var staleClaim = now - SmsCampaignSendJob.ClaimLease - TimeSpan.FromMinutes(1);
        var oldCreate = now - SmsCampaignRecoveryJob.PendingGrace - TimeSpan.FromMinutes(1);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Devam ettirilmiş: CreatedAt eski ama damga taze — lease sahibi canlı
        // sayılır; ilk enqueue lease bayatlayınca (≤15 dk) gelir.
        var resumedPending = await SeedCampaignAsync(db, "pending", now, oldCreate);
        // Damgası bayatlamış pending — kayıp sayılır, kurtarılmalı.
        var stalePending = await SeedCampaignAsync(db, "pending", staleClaim, oldCreate);
        // Hiç damgasız yaşlı pending (F09 kayıp-enqueue) — kurtarılmalı.
        var orphanPending = await SeedCampaignAsync(db, "pending", null, oldCreate);

        var jobs = new RecordingJobClient();
        await NewRecovery(db, jobs).RunAsync();

        var enqueued = jobs.Created.Select(j => (Guid)j.Args[0]!).ToList();
        enqueued.Should().NotContain(resumedPending,
            "taze damgalı pending'i her süpürmede kuyruklamak devam ettirme "
            + "akışını enqueue fırtınasına çevirir (§3.4 düzeltme 2)");
        enqueued.Should().Contain(stalePending);
        enqueued.Should().Contain(orphanPending);
    }

    /// <summary>
    /// Süpürme aynı turda İKİ asılı kampanya bulduğunda, birincinin CAS
    /// çakışması ikincinin kurtarılmasını ENGELLEMEMELİ. Kurulum: A'nın
    /// izleyicideki kopyası bayat (rakip işçi damgayı ilerletti), B sağlam.
    /// Detach şart — kirli kopya bir sonraki kampanyanın SaveChanges'ine
    /// binerse tek çakışma bütün süpürmeyi sürekli düşürür.
    /// </summary>
    [Fact]
    public async Task Bir_kampanyanin_cakismasi_digerinin_kurtarilmasini_engellemez()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var campaignA = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2);
        var campaignB = await SeedPausedAsync(
            db, ["sent", "failed"], segmentsPerMessage: 2);

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

        var afterA = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignA);
        afterA.Status.Should().Be("paused", "çakışan karar düşmeliydi");

        var afterB = await vdb.SmsCampaigns.AsNoTracking().SingleAsync(c => c.Id == campaignB);
        afterB.Status.Should().Be("completed", "sağlam kampanya çakışmadan etkilenmemeli");
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

        // Store sınıf genelinde paylaşılıyor: diğer testlerin bıraktığı takılı
        // kampanyalar da süpürmeye girer. İddia bu testin tohumlarıyla sınırlı.
        var seeded = new[]
        {
            staleSending, nullClaimSending, orphanPending,
            liveSending, freshPending, completed,
        };
        var enqueued = jobs.Created
            .Select(j => (Guid)j.Args[0]!)
            .Where(seeded.Contains)
            .ToList();

        enqueued.Should().BeEquivalentTo(
            new[] { staleSending, nullClaimSending, orphanPending },
            "yalnız bayat sending + yaşlı pending kurtarılmalı");
        enqueued.Should().NotContain(new[] { liveSending, freshPending, completed });
        jobs.Created.Should().OnlyContain(j => j.Type == typeof(SmsCampaignSendJob));
    }
}
