using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class LoginTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LoginTests(ApiFactory factory) => _factory = factory;

    private async Task<string> RegisterAndConfirmAsync(string email, string password)
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Test", password
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;
        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");
        return email;
    }

    [Fact]
    public async Task Login_with_valid_credentials_returns_token()
    {
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await RegisterAndConfirmAsync(email, "secret-password");

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "secret-password"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrEmpty();
        body.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var email = $"wrong-{Guid.NewGuid():N}@example.com";
        await RegisterAndConfirmAsync(email, "secret-password");

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "wrong"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_for_unknown_email_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = $"never-{Guid.NewGuid():N}@example.com",
            password = "anything"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_before_email_confirmed_returns_403()
    {
        var email = $"unconf-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Unconf", password = "secret-password"
        });
        // intentionally no confirm

        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "secret-password"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
}
