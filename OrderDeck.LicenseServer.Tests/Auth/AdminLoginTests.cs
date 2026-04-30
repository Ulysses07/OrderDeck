using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class AdminLoginTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminLoginTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AdminLogin_with_valid_credentials_returns_token()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AdminLogin_with_wrong_password_returns_401()
    {
        var username = $"a-{Guid.NewGuid():N}";
        // Seed via factory but login with wrong pw
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        db.AdminUsers.Add(new AdminUser
        {
            Id = Guid.NewGuid(), Username = username,
            PasswordHash = hasher.Hash("real-pw"), CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new
        {
            username, password = "wrong"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
