using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

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

    [Fact]
    public async Task Get_my_licenses_returns_only_active_licenses()
    {
        var (client, email) = await CreateLoggedInClientAsync();

        // Seed: aynı customer'a 1 aktif + 1 revoke + 1 expired lisans
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);

            db.Licenses.AddRange(
                new License
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "LDK-ACTIVE-" + Guid.NewGuid().ToString("N"),
                    CustomerId = customer.Id,
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
                },
                new License
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "LDK-REVOKED-" + Guid.NewGuid().ToString("N"),
                    CustomerId = customer.Id,
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow.AddDays(-100),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                    RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    RevokeReason = "test"
                },
                new License
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "LDK-EXPIRED-" + Guid.NewGuid().ToString("N"),
                    CustomerId = customer.Id,
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow.AddDays(-400),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
                });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/v1/me/licenses");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<List<LicenseSummaryBody>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(1);
        body[0].licenseKey.Should().StartWith("LDK-ACTIVE-");
        body[0].skuCode.Should().Be("STD");
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record MeBody(Guid id, string email, string name, DateTimeOffset? emailConfirmedAt);
    private sealed record LicenseSummaryBody(string licenseKey, string skuCode, DateTimeOffset expiresAt, DateTimeOffset? revokedAt);
}
