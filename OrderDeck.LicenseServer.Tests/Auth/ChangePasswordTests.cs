using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class ChangePasswordTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ChangePasswordTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string email)> RegisterConfirmLoginAsync(string password)
    {
        var email = $"pw-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "PW Test", password
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;
        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, email);
    }

    [Fact]
    public async Task ChangePassword_with_correct_current_returns_204_and_new_password_works()
    {
        var (client, email) = await RegisterConfirmLoginAsync("old-password");
        var resp = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "old-password",
            newPassword = "new-password"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify new password works
        var freshClient = _factory.CreateClient();
        var loginResp = await freshClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "new-password"
        });
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_with_wrong_current_returns_400()
    {
        var (client, _) = await RegisterConfirmLoginAsync("real-password");
        var resp = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "wrong",
            newPassword = "new-password"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_with_short_new_returns_400()
    {
        var (client, _) = await RegisterConfirmLoginAsync("real-password");
        var resp = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "real-password",
            newPassword = "short"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
}
