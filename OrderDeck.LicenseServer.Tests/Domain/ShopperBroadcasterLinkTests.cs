using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperBroadcasterLinkTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"link-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_optional_wpf_customer_id_null()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = id,
            ShopperId = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),
            Platform = "instagram",
            Username = "@ali_veli",
            WpfCustomerId = null,
            JoinedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperBroadcasterLinks.SingleAsync(l => l.Id == id);
        loaded.Platform.Should().Be("instagram");
        loaded.Username.Should().Be("@ali_veli");
        loaded.WpfCustomerId.Should().BeNull();
        loaded.LeftAt.Should().BeNull();
    }

    [Fact]
    public void ShopperId_LicenseId_pair_is_unique()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(ShopperBroadcasterLink))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 2
                && i.Properties.Any(p => p.Name == nameof(ShopperBroadcasterLink.ShopperId))
                && i.Properties.Any(p => p.Name == nameof(ShopperBroadcasterLink.LicenseId)));
        index.Should().NotBeNull("aynı (Shopper, License) çifti iki kez eklenememeli");
        index!.IsUnique.Should().BeTrue();
    }
}
