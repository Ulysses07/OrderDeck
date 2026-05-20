using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperMeGetTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperMeGetTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ──────────────────────────────────────────────────────────────────

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

    private sealed record NotificationPrefsDto(bool Broadcast, bool Orders, bool Payments);

    private sealed record BroadcasterSummaryDto(Guid LicenseId, string DisplayName, string Platform, string Username);

    private sealed record MeResponse(
        Guid Id,
        string FullName,
        string Phone,
        string Address,
        string? Email,
        string? Tc,
        NotificationPrefsDto NotificationPrefs,
        BroadcasterSummaryDto[] Broadcasters);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private static string UniqueCode() =>
        ("meget" + Guid.NewGuid().ToString("N"))[..16];

    private async Task<(string accessToken, Guid shopperId, Guid licenseId)> RegisterShopperAsync(
        HttpClient client, string? code = null, string? phone = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "MeGet-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var shopperCode = code ?? UniqueCode();
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = shopperCode,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        var actualPhone = phone ?? UniquePhone();
        var req = new RegisterRequest(shopperCode, "Me Get User", actualPhone, "Pass1234!", "Istanbul", "youtube", "megetuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId, licenseId);
    }

    // ── T12.1: Happy path → 200 with full profile + broadcasters ─────────────

    [Fact]
    public async Task GetMe_happy_path_returns_200_with_profile_and_broadcasters()
    {
        var client = _factory.CreateClient();
        var (token, shopperId, licenseId) = await RegisterShopperAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/api/v1/shopper/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body.Should().NotBeNull();
        body!.Id.Should().Be(shopperId);
        body.FullName.Should().Be("Me Get User");
        body.Address.Should().Be("Istanbul");
        body.NotificationPrefs.Should().NotBeNull();
        body.Broadcasters.Should().HaveCount(1);
        body.Broadcasters[0].LicenseId.Should().Be(licenseId);
    }

    // ── T12.2: No Authorization header → 401 ─────────────────────────────────

    [Fact]
    public async Task GetMe_no_auth_header_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/shopper/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T12.3: Soft-deleted shopper → 401 ─────────────────────────────────────

    [Fact]
    public async Task GetMe_soft_deleted_shopper_returns_401()
    {
        var client = _factory.CreateClient();
        var (token, shopperId, _) = await RegisterShopperAsync(client);

        // Soft-delete the shopper directly in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopper = await db.Shoppers.FindAsync(shopperId);
        shopper!.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/api/v1/shopper/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T12.4: Shopper with 0 active broadcasters → 200 with empty array ──────

    [Fact]
    public async Task GetMe_shopper_with_no_active_broadcasters_returns_200_empty_array()
    {
        var client = _factory.CreateClient();
        var (token, shopperId, _) = await RegisterShopperAsync(client);

        // Leave all links
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var links = await db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId)
            .ToListAsync();
        foreach (var link in links)
            link.LeftAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/api/v1/shopper/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.Broadcasters.Should().BeEmpty();
    }

    // ── T12.5: Shopper with 1 left link + 1 active → 200, only active ─────────

    [Fact]
    public async Task GetMe_only_active_links_returned_left_excluded()
    {
        var client = _factory.CreateClient();
        var phone = UniquePhone();
        var (token, shopperId, _) = await RegisterShopperAsync(client, phone: phone);

        // Register a second broadcaster and link the shopper
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer2 = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust2-{Guid.NewGuid():N}@x.test",
            Name = "MeGet2-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer2);
        var code2 = UniqueCode();
        var licenseId2 = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId2,
            CustomerId = customer2.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code2,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();

        // Register shopper to second broadcaster (uses same phone+password → reuse)
        var registerResp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register",
            new RegisterRequest(code2, "Me Get User", phone, "Pass1234!", "Istanbul", "youtube", "megetuser2"));
        registerResp.StatusCode.Should().Be(HttpStatusCode.Created);

        // Now leave the second link
        var link2 = await db.ShopperBroadcasterLinks
            .FirstOrDefaultAsync(l => l.ShopperId == shopperId && l.LicenseId == licenseId2);
        link2.Should().NotBeNull();
        link2!.LeftAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.GetAsync("/api/v1/shopper/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeResponse>();
        body!.Broadcasters.Should().HaveCount(1, "only active link should appear");
        body.Broadcasters[0].LicenseId.Should().NotBe(licenseId2);
    }
}
