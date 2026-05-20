using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class LicenseShopperFieldsTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"licshopper-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_persists_shopper_code_and_payment_account()
    {
        await using var db = NewDb();

        var customerId = Guid.NewGuid();
        // Sku PK is string Code; no Id/Name/PriceTry — use actual property names.
        db.Customers.Add(new Customer
        {
            Id = customerId, Email = $"u-{customerId:N}@x", Name = "Test",
            PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Skus.Add(new Sku { Code = "S1", DisplayName = "Sku 1", DefaultDurationDays = 365, DefaultActivationSlots = 1 });
        await db.SaveChangesAsync();

        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        // License uses SkuCode (string FK), not SkuId; no Status property on entity.
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customerId,
            SkuCode = "S1",
            IssuedAt = now,
            ExpiresAt = now.AddYears(1),
            LicenseKey = "key-1",
            ShopperCode = "royal",
            ShopperCodeUpdatedAt = now,
            PaymentIban = "TR330006100519786457841326",
            PaymentAccountHolder = "BURAK YILMAZ",
            ShopperAppEnabled = true,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Licenses.SingleAsync(l => l.Id == licenseId);
        loaded.ShopperCode.Should().Be("royal");
        loaded.PaymentIban.Should().Be("TR330006100519786457841326");
        loaded.PaymentAccountHolder.Should().Be("BURAK YILMAZ");
        loaded.ShopperAppEnabled.Should().BeTrue();
    }

    [Fact]
    public void ShopperCode_is_unique_globally()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(License))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 1
                && i.Properties[0].Name == nameof(License.ShopperCode));
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }
}
