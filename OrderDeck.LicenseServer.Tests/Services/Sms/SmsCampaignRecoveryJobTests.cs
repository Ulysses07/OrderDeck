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
        var recovery = new SmsCampaignRecoveryJob(
            db, jobs, NullLogger<SmsCampaignRecoveryJob>.Instance);
        await recovery.RunAsync();

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
