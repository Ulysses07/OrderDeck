using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

public sealed class RefreshEndpointTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RefreshEndpointTests(ApiFactory factory) => _factory = factory;

    private async Task<LoginBody> RegisterConfirmLoginAsync(string suffix)
    {
        var email = $"refresh-{suffix}-{Guid.NewGuid():N}@example.com";
        var password = "secret-password-123";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new { email, name = "Refresh Test", password });

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var token = db.EmailConfirmationTokens.Where(t => t.Customer.Email == email).First().Token;
            await client.GetAsync($"/api/v1/auth/confirm-email/{token}");
        }

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new { email, password });
        loginResp.EnsureSuccessStatusCode();
        var body = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        return body!;
    }

    [Fact]
    public async Task Login_returns_access_and_refresh_tokens()
    {
        var body = await RegisterConfirmLoginAsync("login-pair");

        body.Token.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().NotBeNullOrEmpty();
        body.RefreshToken.Should().HaveLength(64);
        body.RefreshExpiresAt.Should().BeAfter(body.ExpiresAt);
        body.CustomerId.Should().NotBeEmpty();
        body.Email.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Refresh_with_valid_token_returns_new_pair()
    {
        var body = await RegisterConfirmLoginAsync("rotate");
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = body.RefreshToken });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var rotated = await resp.Content.ReadFromJsonAsync<LoginBody>();
        rotated!.Token.Should().NotBeNullOrEmpty();
        // Refresh tokens MUST rotate; access tokens may be byte-identical when both are
        // issued in the same second with identical claims (no jti claim) — that's fine,
        // the security property is the rotating refresh token.
        rotated.RefreshToken.Should().NotBeNullOrEmpty().And.NotBe(body.RefreshToken);
    }

    [Fact]
    public async Task Refresh_with_already_rotated_token_returns_401()
    {
        var body = await RegisterConfirmLoginAsync("reuse");
        var client = _factory.CreateClient();
        var first = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = body.RefreshToken });
        first.EnsureSuccessStatusCode();

        var replay = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = body.RefreshToken });
        replay.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_with_garbage_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = "not-a-real-token" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Old_access_token_remains_valid_until_natural_expiry()
    {
        // We do not maintain a denylist for access tokens; rotating refresh tokens
        // does not invalidate already-issued access JWTs. They expire naturally.
        var body = await RegisterConfirmLoginAsync("access-keepalive");
        var client = _factory.CreateClient();

        // Rotate refresh
        var rot = await client.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = body.RefreshToken });
        rot.EnsureSuccessStatusCode();

        // Old access token should still authenticate against a Bearer-Customer endpoint.
        // Use logout (which requires Bearer-Customer) with a bogus refresh — auth check must pass first.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);
        var resp = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "anything" });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent, "old access token must still authenticate until natural expiry");
    }

    [Fact]
    public async Task Logout_revokes_refresh_token_and_subsequent_refresh_returns_401()
    {
        var body = await RegisterConfirmLoginAsync("logout");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = body.RefreshToken });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var unauthClient = _factory.CreateClient();
        var refresh = await unauthClient.PostAsJsonAsync("/api/v1/auth/refresh", new { refreshToken = body.RefreshToken });
        refresh.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_with_unknown_token_is_idempotent_204()
    {
        var body = await RegisterConfirmLoginAsync("logout-idem");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);

        var logout = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "not-real" });
        logout.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Logout_without_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/logout", new { refreshToken = "anything" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record LoginBody(
        string Token,
        DateTimeOffset ExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshExpiresAt,
        Guid CustomerId,
        string Email);
}
