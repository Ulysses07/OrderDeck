using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class PasswordResetCodeCleanupJobTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static PasswordResetCodeCleanupJob NewJob(LicenseDbContext db, int days = 7)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Sms:OtpRetentionDays"] = days.ToString(),
            })
            .Build();
        return new PasswordResetCodeCleanupJob(db, config,
            NullLogger<PasswordResetCodeCleanupJob>.Instance);
    }

    private static ShopperPasswordResetCode Code(DateTimeOffset createdAt) => new()
    {
        Id = Guid.NewGuid(),
        ShopperId = Guid.NewGuid(),
        CodeHash = "h",
        CreatedAt = createdAt,
        ExpiresAt = createdAt.AddMinutes(10),
    };

    [Fact]
    public async Task Prune_deletes_old_keeps_recent()
    {
        using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.ShopperPasswordResetCodes.AddRange(
            Code(now.AddDays(-10)),  // eski → silinir
            Code(now.AddDays(-8)),   // eski → silinir
            Code(now.AddHours(-1))); // güncel → kalır
        await db.SaveChangesAsync();

        await NewJob(db, days: 7).PruneAsync(CancellationToken.None);

        var remaining = await db.ShopperPasswordResetCodes.ToListAsync();
        remaining.Should().HaveCount(1);
        remaining[0].CreatedAt.Should().BeAfter(now.AddDays(-1));
    }

    [Fact]
    public async Task Prune_disabled_when_retention_zero()
    {
        using var db = NewDb();
        db.ShopperPasswordResetCodes.Add(Code(DateTimeOffset.UtcNow.AddDays(-30)));
        await db.SaveChangesAsync();

        await NewJob(db, days: 0).PruneAsync(CancellationToken.None);

        (await db.ShopperPasswordResetCodes.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task Prune_noop_when_all_recent()
    {
        using var db = NewDb();
        db.ShopperPasswordResetCodes.Add(Code(DateTimeOffset.UtcNow.AddHours(-2)));
        await db.SaveChangesAsync();

        await NewJob(db, days: 7).PruneAsync(CancellationToken.None);

        (await db.ShopperPasswordResetCodes.CountAsync()).Should().Be(1);
    }
}
