using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperRefreshTokenEntityTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoprt-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_refresh_token_with_rotation_chain()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = id,
            ShopperId = Guid.NewGuid(),
            TokenHash = new string('a', 64),
            CreatedAt = now,
            ExpiresAt = now.AddDays(90),
            CreatedByIp = "1.2.3.4",
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperRefreshTokens.SingleAsync(t => t.Id == id);
        loaded.TokenHash.Should().HaveLength(64);
        loaded.RevokedAt.Should().BeNull();
        loaded.ReplacedByTokenHash.Should().BeNull();
    }

    [Fact]
    public void TokenHash_is_indexed_for_lookup()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(ShopperRefreshToken))!
            .GetIndexes()
            .SingleOrDefault(i => i.Properties.Count == 1
                && i.Properties[0].Name == nameof(ShopperRefreshToken.TokenHash));
        index.Should().NotBeNull("TokenHash refresh akışında her istekte sorgulanacak");
    }

    [Fact]
    public async Task Support_request_roundtrip()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperSupportRequests.Add(new ShopperSupportRequest
        {
            Id = id,
            ShopperId = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),
            Kind = "forgot-password",
            CreatedAt = now,
            ResolvedAt = null,
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperSupportRequests.SingleAsync(r => r.Id == id);
        loaded.Kind.Should().Be("forgot-password");
        loaded.ResolvedAt.Should().BeNull();
    }
}
