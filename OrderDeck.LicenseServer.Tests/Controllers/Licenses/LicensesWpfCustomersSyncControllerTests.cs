using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesWpfCustomersSyncControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesWpfCustomersSyncControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-WCS-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, customerId, licenseId);
    }

    private sealed record SyncResponse(int Synced, int RetroactiveMatches);

    private static object MakeSyncItem(Guid id, string platform = "youtube", string username = "testuser",
        string? fullName = null, string? phone = null, string? address = null)
        => new
        {
            id,
            platform,
            username,
            fullName,
            phone,
            address,
            updatedAt = DateTimeOffset.UtcNow
        };

    [Fact]
    public async Task Happy_path_inserts_new_projection_rows()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();
        var id3 = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    MakeSyncItem(id1, "youtube", "user1", "Ali Veli", "+905001112233"),
                    MakeSyncItem(id2, "instagram", "user2"),
                    MakeSyncItem(id3, "tiktok", "user3"),
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(3);
        body.RetroactiveMatches.Should().Be(0);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.WpfCustomerProjections.CountAsync(p => p.LicenseId == licenseId);
        count.Should().Be(3);

        var proj1 = await db.WpfCustomerProjections.FirstAsync(p => p.Id == id1);
        proj1.FullName.Should().Be("Ali Veli");
        proj1.Phone.Should().Be("+905001112233");
        proj1.Platform.Should().Be("youtube");
    }

    [Fact]
    public async Task Upsert_updates_existing_rows()
    {
        var (client, _, licenseId) = await SetupAsync();
        var id1 = Guid.NewGuid();
        var id2 = Guid.NewGuid();

        // Initial sync
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    MakeSyncItem(id1, "youtube", "olduser1", "Old Name"),
                    MakeSyncItem(id2, "instagram", "olduser2"),
                }
            });

        // Update sync — same IDs, different data
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = id1, platform = "youtube", username = "newuser1", fullName = "New Name",
                          phone = "+905559998877", address = "New Address", updatedAt = DateTimeOffset.UtcNow },
                    new { id = id2, platform = "instagram", username = "newuser2", fullName = (string?)null,
                          phone = (string?)null, address = (string?)null, updatedAt = DateTimeOffset.UtcNow },
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(2);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var proj1 = await db.WpfCustomerProjections.FirstAsync(p => p.Id == id1);
        proj1.Username.Should().Be("newuser1");
        proj1.FullName.Should().Be("New Name");
        proj1.Phone.Should().Be("+905559998877");

        // No duplicate rows
        var totalCount = await db.WpfCustomerProjections.CountAsync(p => p.LicenseId == licenseId);
        totalCount.Should().Be(2);
    }

    [Fact]
    public async Task Empty_batch_returns_200_with_zero()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(0);
        body.RetroactiveMatches.Should().Be(0);
    }

    [Fact]
    public async Task Batch_too_large_returns_400()
    {
        var (client, _, licenseId) = await SetupAsync();

        var huge = Enumerable.Range(0, 501)
            .Select(i => MakeSyncItem(Guid.NewGuid(), "youtube", $"user{i}"))
            .ToArray();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = huge });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Invalid_platform_returns_400()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = Guid.NewGuid(), platform = "", username = "testuser",
                          fullName = (string?)null, phone = (string?)null, address = (string?)null,
                          updatedAt = DateTimeOffset.UtcNow }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Different_customer_returns_404()
    {
        var (clientA, _, _) = await SetupAsync();
        var (_, _, licenseB) = await SetupAsync();

        var resp = await clientA.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseB}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(Guid.NewGuid()) } });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Retroactive_match_links_existing_ShopperBroadcasterLink()
    {
        var (client, _, licenseId) = await SetupAsync();

        // Pre-seed a Shopper and a ShopperBroadcasterLink with WpfCustomerId = null
        Guid shopperId;
        Guid linkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "Test Shopper",
                Phone = "+90500" + Guid.NewGuid().ToString("N")[..7],
                PasswordHash = "hash",
                Address = "Some address",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Shoppers.Add(shopper);
            shopperId = shopper.Id;

            var link = new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopperId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "matchme",
                WpfCustomerId = null,
                JoinedAt = DateTimeOffset.UtcNow
            };
            db.ShopperBroadcasterLinks.Add(link);
            linkId = link.Id;
            await db.SaveChangesAsync();
        }

        var wpfCustomerId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = wpfCustomerId, platform = "youtube", username = "matchme",
                          fullName = (string?)null, phone = (string?)null, address = (string?)null,
                          updatedAt = DateTimeOffset.UtcNow }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.Synced.Should().Be(1);
        body.RetroactiveMatches.Should().Be(1);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var link2 = await verifyDb.ShopperBroadcasterLinks.FirstAsync(l => l.Id == linkId);
        link2.WpfCustomerId.Should().Be(wpfCustomerId);
    }

    [Fact]
    public async Task Already_matched_link_not_touched()
    {
        var (client, _, licenseId) = await SetupAsync();

        var existingWpfId = Guid.NewGuid();
        Guid linkId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var shopper = new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = Guid.NewGuid(),
                FullName = "Already Matched Shopper",
                Phone = "+90500" + Guid.NewGuid().ToString("N")[..7],
                PasswordHash = "hash",
                Address = "Some address",
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Shoppers.Add(shopper);

            var link = new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopper.Id,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "already-matched",
                WpfCustomerId = existingWpfId,   // already has a value
                JoinedAt = DateTimeOffset.UtcNow
            };
            db.ShopperBroadcasterLinks.Add(link);
            linkId = link.Id;
            await db.SaveChangesAsync();
        }

        var newWpfCustomerId = Guid.NewGuid();
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new
            {
                customers = new[]
                {
                    new { id = newWpfCustomerId, platform = "youtube", username = "already-matched",
                          fullName = (string?)null, phone = (string?)null, address = (string?)null,
                          updatedAt = DateTimeOffset.UtcNow }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<SyncResponse>();
        body!.RetroactiveMatches.Should().Be(0);

        using var verifyScope = _factory.Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var link2 = await verifyDb.ShopperBroadcasterLinks.FirstAsync(l => l.Id == linkId);
        link2.WpfCustomerId.Should().Be(existingWpfId, "pre-existing WpfCustomerId must not be overwritten");
    }

    [Fact]
    public async Task No_auth_returns_401()
    {
        var (_, _, licenseId) = await SetupAsync();
        var anon = _factory.CreateClient();

        var resp = await anon.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/wpf-customers/sync",
            new { customers = new[] { MakeSyncItem(Guid.NewGuid()) } });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
