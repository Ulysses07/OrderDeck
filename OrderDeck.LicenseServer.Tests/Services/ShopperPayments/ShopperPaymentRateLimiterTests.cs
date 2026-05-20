using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.ShopperPayments;

namespace OrderDeck.LicenseServer.Tests.Services.ShopperPayments;

public class ShopperPaymentRateLimiterTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"rl-{Guid.NewGuid():N}")
            .Options);

    private static void SeedAudit(LicenseDbContext db, Guid shopperId, Guid licenseId, DateTimeOffset createdAt)
    {
        db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
        {
            Id = Guid.NewGuid(),
            PaymentId = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            IpAddress = "1.2.3.4",
            UserAgent = "test",
            FraudFlags = "",
            ParserConfidence = "High",
            CreatedAt = createdAt,
        });
    }

    [Fact]
    public async Task Zero_audits_allowed()
    {
        await using var db = NewDb();
        var rl = new ShopperPaymentRateLimiter(db);
        (await rl.CheckAsync(Guid.NewGuid(), Guid.NewGuid(), default)).Should().BeNull();
    }

    [Fact]
    public async Task Four_recent_shopper_audits_allowed()
    {
        await using var db = NewDb();
        var shopperId = Guid.NewGuid(); var licenseId = Guid.NewGuid();
        for (int i = 0; i < 4; i++) SeedAudit(db, shopperId, licenseId, DateTimeOffset.UtcNow.AddMinutes(-i * 5));
        await db.SaveChangesAsync();
        var rl = new ShopperPaymentRateLimiter(db);
        (await rl.CheckAsync(shopperId, licenseId, default)).Should().BeNull();
    }

    [Fact]
    public async Task Five_recent_shopper_audits_blocked()
    {
        await using var db = NewDb();
        var shopperId = Guid.NewGuid(); var licenseId = Guid.NewGuid();
        for (int i = 0; i < 5; i++) SeedAudit(db, shopperId, licenseId, DateTimeOffset.UtcNow.AddMinutes(-i));
        await db.SaveChangesAsync();
        var rl = new ShopperPaymentRateLimiter(db);
        (await rl.CheckAsync(shopperId, licenseId, default)).Should().Be("shopper-hourly-limit");
    }

    [Fact]
    public async Task Old_shopper_audits_outside_window_allowed()
    {
        await using var db = NewDb();
        var shopperId = Guid.NewGuid(); var licenseId = Guid.NewGuid();
        for (int i = 0; i < 10; i++) SeedAudit(db, shopperId, licenseId, DateTimeOffset.UtcNow.AddHours(-2));
        await db.SaveChangesAsync();
        var rl = new ShopperPaymentRateLimiter(db);
        (await rl.CheckAsync(shopperId, licenseId, default)).Should().BeNull();
    }

    [Fact]
    public async Task LicenseHourlyLimit_triggered_by_many_shoppers_same_license()
    {
        await using var db = NewDb();
        var licenseId = Guid.NewGuid();
        for (int i = 0; i < 150; i++) SeedAudit(db, Guid.NewGuid(), licenseId, DateTimeOffset.UtcNow.AddMinutes(-i % 50));
        await db.SaveChangesAsync();
        var rl = new ShopperPaymentRateLimiter(db);
        (await rl.CheckAsync(Guid.NewGuid(), licenseId, default)).Should().Be("license-hourly-limit");
    }
}
