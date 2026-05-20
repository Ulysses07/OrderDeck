using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperFeedTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperFeedTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email = null,
        string? Tc = null);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        object[] Broadcasters);

    private sealed record FeedItem(
        Guid Id,
        Guid LicenseId,
        string BroadcasterName,
        string Type,
        string? TextBody,
        string? MediaObjectKey,
        string? MediaContentType,
        int? MediaWidth,
        int? MediaHeight,
        bool IsPinned,
        DateTimeOffset CreatedAt,
        DateTimeOffset ExpiresAt);

    private sealed record FeedResponse(FeedItem[] Items, string? NextCursor);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string shopperCode, string customerName)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"feed-{Guid.NewGuid():N}@x.test",
            Name = "Feed-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "feed-" + Guid.NewGuid().ToString("N")[..8];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, code, customer.Name);
    }

    private async Task<(string accessToken, Guid shopperId)> RegisterShopperAsync(
        HttpClient client, string broadcasterCode)
    {
        var phone = UniquePhone();
        var req = new RegisterRequest(broadcasterCode, "Feed User", phone, "FeedPass1!", "Ankara", "youtube", "feeduser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    private async Task JoinLicenseAsync(HttpClient client, string broadcasterCode, string platform = "twitch", string username = "handle")
    {
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/broadcasters/join",
            new { BroadcasterCode = broadcasterCode, Platform = platform, Username = username });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, "join prerequisite must succeed");
    }

    private async Task<Guid> SeedPostAsync(
        Guid licenseId,
        bool isPinned = false,
        DateTimeOffset? createdAt = null,
        DateTimeOffset? expiresAt = null,
        DateTimeOffset? deletedAt = null,
        string type = "text",
        string? textBody = "Hello")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var postId = Guid.NewGuid();
        var postType = type switch
        {
            "photo" => BroadcastPostType.Photo,
            "video" => BroadcastPostType.Video,
            _ => BroadcastPostType.Text
        };
        db.BroadcastPosts.Add(new BroadcastPost
        {
            Id = postId,
            LicenseId = licenseId,
            Type = postType,
            TextBody = textBody,
            IsPinned = isPinned,
            CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            ExpiresAt = expiresAt ?? DateTimeOffset.UtcNow.AddDays(7),
            DeletedAt = deletedAt,
        });
        await db.SaveChangesAsync();
        return postId;
    }

    // ── T1: Happy path — 2 broadcasters → posts from both, pinned first ──────

    [Fact]
    public async Task Feed_happy_path_returns_posts_from_both_broadcasters_pinned_first()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, nameA) = await SeedLicenseAsync();
        var (licenseIdB, codeB, nameB) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await JoinLicenseAsync(client, codeB);

        var pinnedId = await SeedPostAsync(licenseIdA, isPinned: true, createdAt: DateTimeOffset.UtcNow.AddMinutes(-1));
        var normalId = await SeedPostAsync(licenseIdB, isPinned: false, createdAt: DateTimeOffset.UtcNow);

        var resp = await client.GetAsync("/api/v1/shopper/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FeedResponse>();

        body!.Items.Should().HaveCount(2);
        // Pinned first
        body.Items[0].Id.Should().Be(pinnedId);
        body.Items[0].IsPinned.Should().BeTrue();
        body.Items[1].Id.Should().Be(normalId);
        // Both broadcaster names populated
        body.Items.Should().Contain(i => i.BroadcasterName == nameA);
        body.Items.Should().Contain(i => i.BroadcasterName == nameB);
    }

    // ── T2: licenseId filter → only that broadcaster's posts ──────────────

    [Fact]
    public async Task Feed_licenseId_filter_returns_only_that_broadcasters_posts()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await JoinLicenseAsync(client, codeB, platform: "instagram", username: "filtertest");

        await SeedPostAsync(licenseIdA);
        var postBId = await SeedPostAsync(licenseIdB, textBody: "B-only post");

        var resp = await client.GetAsync($"/api/v1/shopper/feed?licenseId={licenseIdB}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FeedResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(postBId);
    }

    // ── T3: Unauthorized licenseId (not linked) → 403 ─────────────────────

    [Fact]
    public async Task Feed_unauthorized_licenseId_returns_403()
    {
        var client = _factory.CreateClient();
        var (_, codeA, _) = await SeedLicenseAsync();
        var (unlinkedLicenseId, _, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/api/v1/shopper/feed?licenseId={unlinkedLicenseId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── T4: Cursor pagination → second page excludes first page items ──────

    [Fact]
    public async Task Feed_cursor_pagination_returns_no_duplicates()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Seed 3 posts
        var base64 = DateTimeOffset.UtcNow;
        var post1Id = await SeedPostAsync(licenseIdA, createdAt: base64.AddSeconds(-2));
        var post2Id = await SeedPostAsync(licenseIdA, createdAt: base64.AddSeconds(-1));
        var post3Id = await SeedPostAsync(licenseIdA, createdAt: base64.AddSeconds(0));

        // First page (limit=2)
        var resp1 = await client.GetAsync("/api/v1/shopper/feed?limit=2");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await resp1.Content.ReadFromJsonAsync<FeedResponse>();

        page1!.Items.Should().HaveCount(2);
        page1.NextCursor.Should().NotBeNull();

        // Second page using cursor
        var resp2 = await client.GetAsync($"/api/v1/shopper/feed?limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await resp2.Content.ReadFromJsonAsync<FeedResponse>();

        page2!.Items.Should().HaveCount(1);
        page2.NextCursor.Should().BeNull();

        // No duplicates between pages
        var allIds = page1.Items.Select(i => i.Id).Concat(page2.Items.Select(i => i.Id)).ToList();
        allIds.Should().OnlyHaveUniqueItems();
        allIds.Should().Contain(post1Id);
        allIds.Should().Contain(post2Id);
        allIds.Should().Contain(post3Id);
    }

    // ── T5: Expired posts → excluded ──────────────────────────────────────

    [Fact]
    public async Task Feed_expired_posts_are_excluded()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var activeId = await SeedPostAsync(licenseIdA, expiresAt: DateTimeOffset.UtcNow.AddDays(1), textBody: "active");
        await SeedPostAsync(licenseIdA, expiresAt: DateTimeOffset.UtcNow.AddSeconds(-1), textBody: "expired");

        var resp = await client.GetAsync("/api/v1/shopper/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FeedResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(activeId);
    }

    // ── T6: Deleted posts → excluded ──────────────────────────────────────

    [Fact]
    public async Task Feed_deleted_posts_are_excluded()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var activeId = await SeedPostAsync(licenseIdA, textBody: "visible");
        await SeedPostAsync(licenseIdA, deletedAt: DateTimeOffset.UtcNow.AddMinutes(-5), textBody: "deleted");

        var resp = await client.GetAsync("/api/v1/shopper/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FeedResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(activeId);
    }

    // ── T7: No auth → 401 ─────────────────────────────────────────────────

    [Fact]
    public async Task Feed_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/shopper/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T8: Empty feed (no links) → 200 with empty array ─────────────────

    [Fact]
    public async Task Feed_no_links_returns_200_with_empty_array()
    {
        // Register a shopper on broadcaster A, then leave — so no active links
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Leave the only broadcaster
        await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseIdA}");

        await SeedPostAsync(licenseIdA);

        var resp = await client.GetAsync("/api/v1/shopper/feed");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<FeedResponse>();

        body!.Items.Should().BeEmpty();
        body.NextCursor.Should().BeNull();
    }
}
