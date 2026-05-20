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

public class ShopperMeDeleteTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperMeDeleteTests(ApiFactory factory) => _factory = factory;

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

    private sealed record LoginRequest(string Phone, string Password);

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private static string UniqueCode() =>
        ("medelete" + Guid.NewGuid().ToString("N"))[..16];

    private async Task<(string accessToken, Guid shopperId, string phone, string password)> RegisterShopperAsync(
        HttpClient client)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "MeDel-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = UniqueCode();
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

        var phone = UniquePhone();
        const string password = "DelPass1!";
        var req = new RegisterRequest(code, "Delete User", phone, password, "Izmir", "youtube", "deleteuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId, phone, password);
    }

    // ── T14.1: Happy path → 204; DeletedAt set; tokens revoked; links left ────

    [Fact]
    public async Task DeleteMe_happy_path_returns_204_and_soft_deletes()
    {
        var client = _factory.CreateClient();
        var (token, shopperId, _, _) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.DeleteAsync("/api/v1/shopper/me");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        // Verify DeletedAt is set
        var shopper = await db.Shoppers.FindAsync(shopperId);
        shopper!.DeletedAt.Should().NotBeNull("shopper should be soft-deleted");

        // Verify all refresh tokens are revoked
        var unrevokedTokens = await db.ShopperRefreshTokens
            .Where(t => t.ShopperId == shopperId && t.RevokedAt == null)
            .CountAsync();
        unrevokedTokens.Should().Be(0, "all refresh tokens should be revoked");

        // Verify all broadcaster links have LeftAt set
        var activeLinks = await db.ShopperBroadcasterLinks
            .Where(l => l.ShopperId == shopperId && l.LeftAt == null)
            .CountAsync();
        activeLinks.Should().Be(0, "all broadcaster links should have LeftAt set");
    }

    // ── T14.2: Subsequent login attempt → 401 (deleted) ──────────────────────

    [Fact]
    public async Task DeleteMe_subsequent_login_returns_401()
    {
        var client = _factory.CreateClient();
        var (token, _, phone, password) = await RegisterShopperAsync(client);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var deleteResp = await client.DeleteAsync("/api/v1/shopper/me");
        deleteResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Try logging in with same phone+password → should fail
        var loginClient = _factory.CreateClient();
        var loginResp = await loginClient.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, password));
        loginResp.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "deleted shopper should not be able to log in");
    }

    // ── T14.3: No auth → 401 ─────────────────────────────────────────────────

    [Fact]
    public async Task DeleteMe_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.DeleteAsync("/api/v1/shopper/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
