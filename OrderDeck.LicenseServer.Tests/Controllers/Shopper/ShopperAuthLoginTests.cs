using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperAuthLoginTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthLoginTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ────────────────────────────────────────────────────────────────
    private sealed record LoginRequest(string Phone, string Password);

    private sealed record BroadcasterSummary(Guid LicenseId, string DisplayName, string Platform, string Username);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        BroadcasterSummary[] Broadcasters);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    /// <summary>Seeds a Shopper with a known password. Returns (phone, password, shopperId).</summary>
    private async Task<(string phone, string password, Guid shopperId)> SeedShopperAsync(
        string? phone = null, string? password = null, bool deleted = false)
    {
        phone ??= UniquePhone();
        password ??= "LoginPass1!";

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = id,
            FullName = "Login Test User",
            Phone = phone,
            PasswordHash = hasher.Hash(password),
            Address = "Test Address",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deleted ? now : null,
        });
        await db.SaveChangesAsync();
        return (phone, password, id);
    }

    // ── T8.1: Happy path → 200 + tokens ──────────────────────────────────────

    [Fact]
    public async Task Login_happy_path_returns_200_with_tokens()
    {
        var (phone, password, shopperId) = await SeedShopperAsync();
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/login", new LoginRequest(phone, password));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.ShopperId.Should().Be(shopperId);
        body.AccessTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        body.RefreshTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    // ── T8.2: Unknown phone → 401 ─────────────────────────────────────────────

    [Fact]
    public async Task Login_unknown_phone_returns_401()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/login",
                new LoginRequest("+905512345678", "AnyPassword1!"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T8.3: Wrong password → 401 ────────────────────────────────────────────

    [Fact]
    public async Task Login_wrong_password_returns_401()
    {
        var (phone, _, _) = await SeedShopperAsync();
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/login",
                new LoginRequest(phone, "WrongPassword99!"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T8.4: Deleted shopper → 401 ──────────────────────────────────────────

    [Fact]
    public async Task Login_deleted_shopper_returns_401()
    {
        var (phone, password, _) = await SeedShopperAsync(deleted: true);
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/login",
                new LoginRequest(phone, password));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T8.5: Invalid phone format → 400 ──────────────────────────────────────

    [Fact]
    public async Task Login_invalid_phone_format_returns_400()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/login",
                new LoginRequest("notaphone", "Password1!"));
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    // ── T8.6: Response includes broadcasters array ────────────────────────────

    [Fact]
    public async Task Login_response_includes_broadcasters_array()
    {
        var (phone, password, shopperId) = await SeedShopperAsync();

        // Seed a license and link for this shopper
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"cust-{Guid.NewGuid():N}@x.test",
                Name = "Broadcaster",
                PasswordHash = "ph",
                CreatedAt = DateTimeOffset.UtcNow,
            };
            db.Customers.Add(customer);

            var licenseId = Guid.NewGuid();
            db.Licenses.Add(new License
            {
                Id = licenseId,
                CustomerId = customer.Id,
                SkuCode = "STD",
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
                LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
                ShopperCode = "login-bc-" + Guid.NewGuid().ToString("N")[..6],
                ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
            });
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(),
                ShopperId = shopperId,
                LicenseId = licenseId,
                Platform = "youtube",
                Username = "testloginuser",
                JoinedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/login", new LoginRequest(phone, password));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        body!.Broadcasters.Should().HaveCount(1);
        body.Broadcasters[0].Platform.Should().Be("youtube");
        body.Broadcasters[0].Username.Should().Be("testloginuser");
    }
}
