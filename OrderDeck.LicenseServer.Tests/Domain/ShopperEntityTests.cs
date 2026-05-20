using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperEntityTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shopper-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_persists_all_required_fields()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Shoppers.Add(new Shopper
        {
            Id = id,
            FullName = "Ali Veli",
            Phone = "+905551112233",
            PasswordHash = "bcrypt-hash",
            Address = "Bağdat Cd. 1",
            Email = "ali@example.com",
            Tc = "12345678901",
            NotificationsEnabledBroadcast = true,
            NotificationsEnabledOrders = true,
            NotificationsEnabledPayments = false,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Shoppers.SingleAsync(s => s.Id == id);
        loaded.FullName.Should().Be("Ali Veli");
        loaded.Phone.Should().Be("+905551112233");
        loaded.PasswordHash.Should().Be("bcrypt-hash");
        loaded.Email.Should().Be("ali@example.com");
        loaded.Tc.Should().Be("12345678901");
        loaded.NotificationsEnabledPayments.Should().BeFalse();
    }

    [Fact]
    public async Task Phone_is_unique()
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new Shopper
        {
            Id = Guid.NewGuid(), FullName = "A", Phone = "+905551112233",
            PasswordHash = "h", Address = "x", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        db.Shoppers.Add(new Shopper
        {
            Id = Guid.NewGuid(), FullName = "B", Phone = "+905551112233",
            PasswordHash = "h2", Address = "y", CreatedAt = now, UpdatedAt = now,
        });

        var phoneIndex = db.Model.FindEntityType(typeof(Shopper))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Shopper.Phone));
        phoneIndex.IsUnique.Should().BeTrue();
    }
}
