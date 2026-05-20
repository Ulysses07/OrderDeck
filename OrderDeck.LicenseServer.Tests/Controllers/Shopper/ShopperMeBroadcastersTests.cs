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

public class ShopperMeBroadcastersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperMeBroadcastersTests(ApiFactory factory) => _factory = factory;

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

    private sealed record BroadcasterSummaryDto(Guid LicenseId, string DisplayName, string Platform, string Username);
    private sealed record BroadcastersResponse(BroadcasterSummaryDto[] Broadcasters);

    private sealed record JoinRequest(string BroadcasterCode, string Platform, string Username);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string shopperCode, string name)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"me-bc-{Guid.NewGuid():N}@x.test",
            Name = "MeBC-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "mebc-" + Guid.NewGuid().ToString("N")[..8];
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
        var req = new RegisterRequest(broadcasterCode, "MeBC User", phone, "MeBCPass1!", "Ankara", "youtube", "mebcuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── T1: 2 active links → 200, both returned ───────────────────────────────

    [Fact]
    public async Task GetBroadcasters_two_active_links_returns_both()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, nameA) = await SeedLicenseAsync();
        var (licenseIdB, codeB, nameB) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        // Join broadcaster B
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var joinResp = await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "twitch", "mebcuser2"));
        joinResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var resp = await client.GetAsync("/api/v1/shopper/me/broadcasters");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BroadcastersResponse>();
        body!.Broadcasters.Should().HaveCount(2);
        body.Broadcasters.Should().Contain(b => b.LicenseId == licenseIdA);
        body.Broadcasters.Should().Contain(b => b.LicenseId == licenseIdB);
    }

    // ── T2: 1 active + 1 left → only active returned ─────────────────────────

    [Fact]
    public async Task GetBroadcasters_one_active_one_left_returns_only_active()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();
        var (licenseIdB, codeB, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Join broadcaster B
        await client.PostAsJsonAsync(
            "/api/v1/shopper/broadcasters/join",
            new JoinRequest(codeB, "twitch", "mebcuser3"));

        // Leave broadcaster B
        await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseIdB}");

        var resp = await client.GetAsync("/api/v1/shopper/me/broadcasters");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BroadcastersResponse>();
        body!.Broadcasters.Should().HaveCount(1);
        body.Broadcasters[0].LicenseId.Should().Be(licenseIdA);
    }

    // ── T3: 0 active links → 200, empty array ────────────────────────────────

    [Fact]
    public async Task GetBroadcasters_no_active_links_returns_empty_array()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Leave broadcaster A
        await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseIdA}");

        var resp = await client.GetAsync("/api/v1/shopper/me/broadcasters");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<BroadcastersResponse>();
        body!.Broadcasters.Should().BeEmpty();
    }

    // ── T4: Soft-deleted shopper → 401 ───────────────────────────────────────

    [Fact]
    public async Task GetBroadcasters_deleted_shopper_returns_401()
    {
        var client = _factory.CreateClient();
        var (_, code, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Delete the shopper account
        var deleteResp = await client.DeleteAsync("/api/v1/shopper/me");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Now try to get broadcasters — should 401 (deleted)
        var resp = await client.GetAsync("/api/v1/shopper/me/broadcasters");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T5: No auth → 401 ────────────────────────────────────────────────────

    [Fact]
    public async Task GetBroadcasters_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/shopper/me/broadcasters");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
