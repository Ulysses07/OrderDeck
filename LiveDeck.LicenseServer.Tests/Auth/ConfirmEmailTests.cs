using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class ConfirmEmailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ConfirmEmailTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Confirm_with_valid_token_marks_email_confirmed()
    {
        var client = _factory.CreateClient();
        var email = $"c-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Confirm Test", password = "secret-password"
        });

        // Extract token from db
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email)
            .OrderByDescending(t => t.CreatedAt)
            .First().Token;

        var resp = await client.GetAsync($"/api/v1/auth/confirm-email/{token}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var customer = await db.Customers.FirstAsync(c => c.Email == email);
        customer.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Confirm_with_invalid_token_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/v1/auth/confirm-email/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_with_already_used_token_returns_400()
    {
        var client = _factory.CreateClient();
        var email = $"reuse-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Reuse", password = "secret-password"
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;

        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");           // first use
        var resp = await client.GetAsync($"/api/v1/auth/confirm-email/{token}");  // second use

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
