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

public class ShopperAuthChangePasswordTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthChangePasswordTests(ApiFactory factory) => _factory = factory;

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

    private sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
    private sealed record LoginRequest(string Phone, string Password);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    /// <summary>
    /// Seeds a broadcaster license and registers a shopper against it.
    /// Returns (accessToken, shopperId, phone, password).
    /// </summary>
    private async Task<(string accessToken, Guid shopperId, string phone, string password)> RegisterShopperAsync(
        HttpClient client)
    {
        // Seed a license
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "CPW-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "cpw-" + Guid.NewGuid().ToString("N")[..8];
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
        const string password = "OldPass1!";
        var req = new RegisterRequest(code, "CPW User", phone, password, "Ankara", "youtube", "cpwuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId, phone, password);
    }

    // ── T11.1: Happy path → 204, password updated ────────────────────────────

    [Fact]
    public async Task ChangePassword_happy_path_returns_204_and_updates_password()
    {
        var client = _factory.CreateClient();
        var (token, _, phone, oldPassword) = await RegisterShopperAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/change-password",
            new ChangePasswordRequest(oldPassword, "NewPass99!"));

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify by logging in with the new password
        var loginClient = _factory.CreateClient();
        var loginResp = await loginClient.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, "NewPass99!"));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK, "new password should work for login");

        // Old password should no longer work
        var loginRespOld = await loginClient.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, oldPassword));
        loginRespOld.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "old password should be rejected");
    }

    // ── T11.2: Wrong currentPassword → 401, password unchanged ───────────────

    [Fact]
    public async Task ChangePassword_wrong_current_password_returns_401()
    {
        var client = _factory.CreateClient();
        var (token, _, phone, oldPassword) = await RegisterShopperAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/change-password",
            new ChangePasswordRequest("WrongCurrent99!", "NewPass99!"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // Original password should still work
        var loginClient = _factory.CreateClient();
        var loginResp = await loginClient.PostAsJsonAsync("/api/v1/shopper/auth/login",
            new LoginRequest(phone, oldPassword));
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK, "original password should still work");
    }

    // ── T11.3: Weak new password (<8 chars) → 400 ───────────────────────────

    [Fact]
    public async Task ChangePassword_weak_new_password_returns_400()
    {
        var client = _factory.CreateClient();
        var (token, _, _, oldPassword) = await RegisterShopperAsync(client);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/change-password",
            new ChangePasswordRequest(oldPassword, "short"));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── T11.4: No Authorization header → 401 ────────────────────────────────

    [Fact]
    public async Task ChangePassword_no_auth_header_returns_401()
    {
        var client = _factory.CreateClient();
        // No Authorization header set
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/change-password",
            new ChangePasswordRequest("OldPass1!", "NewPass99!"));

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
