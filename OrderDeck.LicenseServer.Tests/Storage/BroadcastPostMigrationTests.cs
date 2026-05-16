using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Storage;

public class BroadcastPostMigrationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BroadcastPostMigrationTests(ApiFactory f) => _factory = f;

    [Fact]
    public async Task BroadcastPosts_table_can_round_trip_entity()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"bp-{Guid.NewGuid():N}@test.com",
            Name = "X", PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id,
            LicenseKey = "LDK-BP-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var post = new BroadcastPost
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Type = BroadcastPostType.Text, TextBody = "hello",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            IsPinned = false
        };
        db.BroadcastPosts.Add(post);
        await db.SaveChangesAsync();

        var fetched = await db.BroadcastPosts.FirstAsync(p => p.Id == post.Id);
        fetched.TextBody.Should().Be("hello");
        fetched.Type.Should().Be(BroadcastPostType.Text);
    }
}
