using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class WpfCustomerProjectionTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wpfproj-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_nullable_identity_fields()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = id,
            LicenseId = licenseId,
            Platform = "tiktok",
            Username = "@tt_user",
            FullName = null,
            Phone = null,
            Address = null,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.WpfCustomerProjections.SingleAsync(c => c.Id == id);
        loaded.Platform.Should().Be("tiktok");
        loaded.FullName.Should().BeNull();
    }

    [Fact]
    public void LicenseId_Platform_Username_combo_indexed_for_match()
    {
        using var db = NewDb();
        var entityType = db.Model.FindEntityType(typeof(WpfCustomerProjection))!;
        entityType.GetIndexes().Should().Contain(i =>
            i.Properties.Count == 3
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.LicenseId))
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.Platform))
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.Username)),
            "sipariş eşleşmesi için (LicenseId, Platform, Username) sorgulanacak");
    }
}
