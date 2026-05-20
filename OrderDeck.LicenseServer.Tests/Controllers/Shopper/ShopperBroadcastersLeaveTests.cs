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

public class ShopperBroadcastersLeaveTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperBroadcastersLeaveTests(ApiFactory factory) => _factory = factory;

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

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string shopperCode)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"leave-{Guid.NewGuid():N}@x.test",
            Name = "Leave-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "leave-" + Guid.NewGuid().ToString("N")[..8];
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

    private async Task<(string accessToken, Guid shopperId)> RegisterShopperAsync(
        HttpClient client, string broadcasterCode)
    {
        var phone = UniquePhone();
        var req = new RegisterRequest(broadcasterCode, "Leave User", phone, "LeavePass1!", "Ankara", "youtube", "leaveuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    // ── T1: Happy path → 204, LeftAt set in DB ────────────────────────────────

    [Fact]
    public async Task Leave_happy_path_returns_204_and_sets_left_at()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, shopperId) = await RegisterShopperAsync(client, code);

        var before = DateTimeOffset.UtcNow;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseId}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify LeftAt is set in DB
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var link = await db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LicenseId == licenseId)
            .FirstOrDefaultAsync();
        link.Should().NotBeNull();
        link!.LeftAt.Should().NotBeNull();
        link.LeftAt.Should().BeOnOrAfter(before);
    }

    // ── T2: Already left link → 404 ──────────────────────────────────────────

    [Fact]
    public async Task Leave_already_left_returns_404()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, code);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // First leave
        var firstResp = await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseId}");
        firstResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Second leave — should 404
        var secondResp = await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseId}");
        secondResp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T3: Not linked at all → 404 ──────────────────────────────────────────

    [Fact]
    public async Task Leave_not_linked_returns_404()
    {
        var client = _factory.CreateClient();
        var (_, codeA) = await SeedLicenseAsync();
        var (licenseIdB, _) = await SeedLicenseAsync();
        var (token, _) = await RegisterShopperAsync(client, codeA);

        // Try to leave broadcaster B (never joined)
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseIdB}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── T4: No auth → 401 ────────────────────────────────────────────────────

    [Fact]
    public async Task Leave_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var (licenseId, _) = await SeedLicenseAsync();
        var resp = await client.DeleteAsync($"/api/v1/shopper/broadcasters/{licenseId}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
