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

/// <summary>
/// GET /api/v1/shopper/feed/{postId}/media-url
///
/// Shopper-tenancy: shopper sadece aktif bağlı olduğu yayıncıların media
/// URL'lerini alabilir; başka license/silinmiş post/bağlı olmadığı yayıncı
/// → 404 (existence leak yok).
/// </summary>
public class ShopperFeedMediaUrlTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperFeedMediaUrlTests(ApiFactory factory) => _factory = factory;

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

    private sealed record MediaUrlResponse(string Url, DateTimeOffset ExpiresAt);

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999);

    private async Task<(Guid licenseId, string code)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"media-{Guid.NewGuid():N}@x.test",
            Name = "MediaBC-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var code = "media-" + Guid.NewGuid().ToString("N")[..6];
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
        return (licenseId, code);
    }

    private async Task<HttpClient> RegisterAndAuthClientAsync(string broadcasterCode)
    {
        var client = _factory.CreateClient();
        var req = new RegisterRequest(
            broadcasterCode, "Media User", UniquePhone(),
            "MediaPass1!", "Ankara", "youtube", "mediauser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", body!.AccessToken);
        return client;
    }

    private async Task<Guid> SeedPostAsync(Guid licenseId,
        string type = "photo",
        string? mediaObjectKey = "license/post/media.bin",
        bool deleted = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var postId = Guid.NewGuid();
        db.BroadcastPosts.Add(new BroadcastPost
        {
            Id = postId,
            LicenseId = licenseId,
            Type = type == "video" ? BroadcastPostType.Video : BroadcastPostType.Photo,
            MediaObjectKey = mediaObjectKey,
            MediaContentType = "image/jpeg",
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
            DeletedAt = deleted ? DateTimeOffset.UtcNow : null,
        });
        await db.SaveChangesAsync();
        return postId;
    }

    [Fact]
    public async Task GetMediaUrl_linked_shopper_returns_url()
    {
        var (licenseId, code) = await SeedLicenseAsync();
        var client = await RegisterAndAuthClientAsync(code);
        var postId = await SeedPostAsync(licenseId);

        var resp = await client.GetAsync($"/api/v1/shopper/feed/{postId}/media-url");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MediaUrlResponse>();
        body!.Url.Should().NotBeNullOrEmpty();
        body.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task GetMediaUrl_unlinked_shopper_returns_404()
    {
        // Shopper farklı bir yayıncıya bağlı; başka yayıncının post'una erişemez.
        var (_, codeA) = await SeedLicenseAsync();
        var client = await RegisterAndAuthClientAsync(codeA);
        var (licenseB, _) = await SeedLicenseAsync();
        var postIdB = await SeedPostAsync(licenseB);

        var resp = await client.GetAsync($"/api/v1/shopper/feed/{postIdB}/media-url");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMediaUrl_deleted_post_returns_404()
    {
        var (licenseId, code) = await SeedLicenseAsync();
        var client = await RegisterAndAuthClientAsync(code);
        var postId = await SeedPostAsync(licenseId, deleted: true);

        var resp = await client.GetAsync($"/api/v1/shopper/feed/{postId}/media-url");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMediaUrl_post_without_media_returns_400()
    {
        var (licenseId, code) = await SeedLicenseAsync();
        var client = await RegisterAndAuthClientAsync(code);
        var postId = await SeedPostAsync(licenseId, mediaObjectKey: null);

        var resp = await client.GetAsync($"/api/v1/shopper/feed/{postId}/media-url");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetMediaUrl_no_auth_returns_401()
    {
        var (licenseId, _) = await SeedLicenseAsync();
        var postId = await SeedPostAsync(licenseId);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/api/v1/shopper/feed/{postId}/media-url");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
