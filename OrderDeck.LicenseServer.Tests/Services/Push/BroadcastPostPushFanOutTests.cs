using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Push;

/// <summary>
/// Faz 4c-3: yeni broadcast post create → linkli + broadcast bildirimi açık
/// shopper'lara push gönderilir.
/// </summary>
public class BroadcastPostPushFanOutTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public BroadcastPostPushFanOutTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Push.Clear();
    }

    private sealed record CreatePostResp(Guid Id);

    private async Task<(HttpClient client, Guid licenseId, string broadcasterName)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        string customerName;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            customerName = (await db.Customers.FindAsync(customerId))!.Name;

            licenseId = Guid.NewGuid();
            db.Licenses.Add(new License
            {
                Id = licenseId,
                LicenseKey = "LDK-BP-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            await db.SaveChangesAsync();
        }
        return (client, licenseId, customerName);
    }

    private async Task<Guid> SeedShopperAsync(
        Guid licenseId,
        bool broadcastEnabled,
        bool leftLink = false,
        bool deleted = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopperId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new Shopper
        {
            Id = shopperId,
            FullName = "Test Shopper " + shopperId.ToString("N")[..6],
            Phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999),
            PasswordHash = "ph",
            Address = "Addr",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deleted ? now : null,
            NotificationsEnabledBroadcast = broadcastEnabled,
            NotificationsEnabledOrders = true,
            NotificationsEnabledPayments = true,
        });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "u" + shopperId.ToString("N")[..6],
            JoinedAt = now,
            LeftAt = leftLink ? now : null,
        });
        await db.SaveChangesAsync();
        return shopperId;
    }

    [Fact]
    public async Task CreateTextPost_pushes_to_eligible_shoppers()
    {
        var (client, licenseId, broadcasterName) = await SetupAsync();
        var optedIn = await SeedShopperAsync(licenseId, broadcastEnabled: true);
        await SeedShopperAsync(licenseId, broadcastEnabled: false); // opted out
        await SeedShopperAsync(licenseId, broadcastEnabled: true, leftLink: true); // left
        await SeedShopperAsync(licenseId, broadcastEnabled: true, deleted: true); // deleted

        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            "/api/panel/posts",
            new { type = "text", textBody = "Bu akşam canlı yayın!" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        _factory.Push.SentToShoppers.Should().HaveCount(1);
        var n = _factory.Push.SentToShoppers[0];
        n.ShopperIds.Should().BeEquivalentTo(new[] { optedIn });
        n.Title.Should().Be(broadcasterName);
        n.Body.Should().Be("Bu akşam canlı yayın!");
        n.Data!["type"].Should().Be("broadcast-post");
        n.Data["licenseId"].Should().Be(licenseId.ToString());
    }

    [Fact]
    public async Task CreatePost_no_linked_shoppers_no_push()
    {
        var (client, _, _) = await SetupAsync();
        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            "/api/panel/posts",
            new { type = "text", textBody = "Hello" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        _factory.Push.SentToShoppers.Should().BeEmpty();
    }

    [Fact]
    public async Task CreatePost_all_opted_out_no_push()
    {
        var (client, licenseId, _) = await SetupAsync();
        await SeedShopperAsync(licenseId, broadcastEnabled: false);
        await SeedShopperAsync(licenseId, broadcastEnabled: false);

        _factory.Push.Clear();

        var resp = await client.PostAsJsonAsync(
            "/api/panel/posts",
            new { type = "text", textBody = "Hello" });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);

        _factory.Push.SentToShoppers.Should().BeEmpty();
    }
}
