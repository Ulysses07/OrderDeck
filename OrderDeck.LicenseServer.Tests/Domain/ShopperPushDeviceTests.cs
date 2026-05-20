using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperPushDeviceTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoppush-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_unique_per_shopper_device()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var shopperId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperPushDevices.Add(new ShopperPushDevice
        {
            Id = id,
            ShopperId = shopperId,
            DeviceId = "ios-uuid-1",
            Platform = "ios",
            PushToken = "apns-token",
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperPushDevices.SingleAsync(d => d.Id == id);
        loaded.DeviceId.Should().Be("ios-uuid-1");
        loaded.PushToken.Should().Be("apns-token");
    }

    [Fact]
    public void ShopperId_DeviceId_pair_is_unique_so_reregister_upserts()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(ShopperPushDevice))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 2
                && i.Properties.Any(p => p.Name == nameof(ShopperPushDevice.ShopperId))
                && i.Properties.Any(p => p.Name == nameof(ShopperPushDevice.DeviceId)));
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }
}
