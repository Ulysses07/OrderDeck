using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperAuthRefreshTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthRefreshTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record RefreshRequest(string RefreshToken);

    private sealed record RefreshResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt);

    // ── Register request (reuse to get a valid refresh token) ────────────────

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

    /// <summary>Registers a shopper and returns the initial refresh token + shopperId.</summary>
    private async Task<(string refreshToken, Guid shopperId)> RegisterAndGetTokensAsync()
    {
        // Seed a license
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new OrderDeck.LicenseServer.Domain.Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "RefreshTestBC",
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var licenseId = Guid.NewGuid();
        var code = "ref-" + Guid.NewGuid().ToString("N")[..8];
        db.Licenses.Add(new OrderDeck.LicenseServer.Domain.License
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
        var req = new RegisterRequest(code, "Refresh Tester", phone, "Password1!", "İstanbul", "youtube", "reftester");
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.RefreshToken, body.ShopperId);
    }

    // ── T9.1: Happy path → 200, new tokens, old token revoked ────────────────

    [Fact]
    public async Task Refresh_happy_path_returns_200_with_new_tokens()
    {
        var (oldRefreshToken, shopperId) = await RegisterAndGetTokensAsync();

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest(oldRefreshToken));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<RefreshResponse>();
        body.Should().NotBeNull();
        body!.AccessToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBe(oldRefreshToken, "should be a new token");
        body.AccessTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
        body.RefreshTokenExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    // ── T9.2: Old token now has RevokedAt + ReplacedByTokenHash in DB ─────────

    [Fact]
    public async Task Refresh_old_token_is_revoked_in_db_after_rotation()
    {
        var (oldRefreshToken, _) = await RegisterAndGetTokensAsync();

        await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest(oldRefreshToken));

        // Verify old token is revoked
        var oldHash = ShopperRefreshTokenService.HashForTest(oldRefreshToken);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var oldRow = await db.ShopperRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == oldHash);
        oldRow.Should().NotBeNull();
        oldRow!.RevokedAt.Should().NotBeNull("old token should be revoked after rotation");
        oldRow.ReplacedByTokenHash.Should().NotBeNullOrEmpty("should track the replacement");
    }

    // ── T9.3: Unknown token → 401 ─────────────────────────────────────────────

    [Fact]
    public async Task Refresh_unknown_token_returns_401()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest("deadbeefdeadbeefdeadbeefdeadbeef"));
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T9.4: Reused (already rotated) token → 401 ────────────────────────────

    [Fact]
    public async Task Refresh_reused_already_rotated_token_returns_401()
    {
        var (oldRefreshToken, _) = await RegisterAndGetTokensAsync();

        // First rotation — succeeds
        var resp1 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest(oldRefreshToken));
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);

        // Second rotation with same old token — must fail
        var resp2 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest(oldRefreshToken));
        resp2.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── T9.5: New token from rotation can itself be used ──────────────────────

    [Fact]
    public async Task Refresh_new_token_from_rotation_is_usable()
    {
        var (firstRefreshToken, _) = await RegisterAndGetTokensAsync();

        var resp1 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest(firstRefreshToken));
        var body1 = await resp1.Content.ReadFromJsonAsync<RefreshResponse>();
        var secondRefreshToken = body1!.RefreshToken;

        // Use the new token
        var resp2 = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/refresh",
                new RefreshRequest(secondRefreshToken));
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var body2 = await resp2.Content.ReadFromJsonAsync<RefreshResponse>();
        body2!.AccessToken.Should().NotBeNullOrEmpty();
        body2.RefreshToken.Should().NotBe(secondRefreshToken);
    }
}
