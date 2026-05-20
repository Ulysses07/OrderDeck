using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class ShopperRefreshTokenServiceTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoprtsvc-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Issue_creates_db_row_and_returns_raw_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopperId = Guid.NewGuid();

        var (raw, expiresAt) = await svc.IssueAsync(shopperId, "1.2.3.4", default);

        raw.Should().NotBeNullOrWhiteSpace().And.HaveLength(64);
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(89));

        var row = await db.ShopperRefreshTokens.SingleAsync();
        row.ShopperId.Should().Be(shopperId);
        row.TokenHash.Should().HaveLength(64);
        row.TokenHash.Should().NotBe(raw);
        row.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_revokes_old_and_returns_new_chain()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopperId = Guid.NewGuid();
        var (oldRaw, _) = await svc.IssueAsync(shopperId, "1.2.3.4", default);

        var rotateResult = await svc.RotateAsync(oldRaw, "5.6.7.8", default);
        rotateResult.Should().NotBeNull();

        var rows = await db.ShopperRefreshTokens.OrderBy(t => t.CreatedAt).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].RevokedAt.Should().NotBeNull();
        rows[0].ReplacedByTokenHash.Should().Be(rows[1].TokenHash);
        rows[1].ShopperId.Should().Be(shopperId);
    }

    [Fact]
    public async Task Rotate_returns_null_for_unknown_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var result = await svc.RotateAsync("notarealtoken", "1.2.3.4", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_returns_null_for_already_revoked_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var (raw, _) = await svc.IssueAsync(Guid.NewGuid(), "1.2.3.4", default);
        await svc.RotateAsync(raw, "1.2.3.4", default);
        var second = await svc.RotateAsync(raw, "1.2.3.4", default);
        second.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_returns_null_for_expired_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var rawSeed = new string('x', 64);
        var hash = ShopperRefreshTokenService.HashForTest(rawSeed);
        db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = Guid.NewGuid(),
            TokenHash = hash,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();

        var result = await svc.RotateAsync(rawSeed, "1.2.3.4", default);
        result.Should().BeNull();
    }
}
