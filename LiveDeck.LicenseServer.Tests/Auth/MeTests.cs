using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class MeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MeTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string email)> CreateLoggedInClientAsync()
    {
        var email = $"me-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Me Test", password = "secret-password"
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;
        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "secret-password"
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, email);
    }

    [Fact]
    public async Task Get_me_returns_authenticated_customer()
    {
        var (client, email) = await CreateLoggedInClientAsync();
        var resp = await client.GetAsync("/api/v1/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeBody>();
        body!.email.Should().Be(email);
        body.name.Should().Be("Me Test");
        body.emailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Get_me_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record MeBody(Guid id, string email, string name, DateTimeOffset? emailConfirmedAt);
}
