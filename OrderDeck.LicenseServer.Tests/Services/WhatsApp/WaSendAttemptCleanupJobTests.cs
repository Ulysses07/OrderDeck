using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public class WaSendAttemptCleanupJobTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static WaSendAttemptCleanupJob NewJob(LicenseDbContext db, int days = 30)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["OrderDeck:WhatsApp:SendAttemptRetentionDays"] = days.ToString(),
            })
            .Build();
        return new WaSendAttemptCleanupJob(db, config,
            NullLogger<WaSendAttemptCleanupJob>.Instance);
    }

    private static WaSendAttempt Attempt(
        DateTimeOffset startedAt, string status = "done", DateTimeOffset? completedAt = null) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = Guid.NewGuid(),
        Status = status,
        Ok = status == "done" ? true : null,
        StartedAt = startedAt,
        CompletedAt = completedAt,
    };

    [Fact]
    public async Task Prune_deletes_old_keeps_recent()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.WaSendAttempts.AddRange(
            Attempt(now.AddDays(-40), completedAt: now.AddDays(-40)),  // eski → silinir
            Attempt(now.AddDays(-31), completedAt: now.AddDays(-31)),  // eski → silinir
            Attempt(now.AddHours(-1), completedAt: now.AddHours(-1))); // güncel → kalır
        await db.SaveChangesAsync();

        await NewJob(db, days: 30).PruneAsync(CancellationToken.None);

        var remaining = await db.WaSendAttempts.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].StartedAt.Should().BeAfter(now.AddDays(-1));
    }

    [Fact]
    public async Task Prune_deletes_stale_pending_rows()
    {
        // Damgalanamamış satırlar "pending" kalıp CompletedAt=null taşıyor.
        // Ölçüt CompletedAt olsaydı tam da temizlenmesi gereken artıklar
        // sonsuza dek kalırdı — bu yüzden StartedAt'e bakıyoruz.
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.WaSendAttempts.Add(Attempt(now.AddDays(-40), status: "pending"));
        await db.SaveChangesAsync();

        await NewJob(db, days: 30).PruneAsync(CancellationToken.None);

        (await db.WaSendAttempts.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task Prune_disabled_when_retention_zero()
    {
        using var db = NewDb();
        db.WaSendAttempts.Add(Attempt(DateTimeOffset.UtcNow.AddYears(-1)));
        await db.SaveChangesAsync();

        await NewJob(db, days: 0).PruneAsync(CancellationToken.None);

        (await db.WaSendAttempts.CountAsync()).Should().Be(1);
    }
}
