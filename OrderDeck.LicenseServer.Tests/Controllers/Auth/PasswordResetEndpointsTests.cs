using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Auth;

public sealed class PasswordResetEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PasswordResetEndpointsTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedConfirmedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"ep-{Guid.NewGuid():N}@x",
            Name = "Ep",
            PasswordHash = hasher.Hash("old-password-12345"),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task PasswordResetRequest_returns_202_for_known_email()
    {
        var customer = await SeedConfirmedAsync();
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset-request",
            new { email = customer.Email });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PasswordResetRequest_returns_202_silently_for_unknown_email()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset-request",
            new { email = $"never-{Guid.NewGuid():N}@x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PasswordResetComplete_with_valid_token_returns_204()
    {
        var customer = await SeedConfirmedAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = tokenId, newPassword = "new-password-12345" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task PasswordResetComplete_with_unknown_token_returns_400_token_invalid()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = Guid.NewGuid(), newPassword = "new-password-12345" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        body!.title.Should().Be("token-invalid");
    }

    [Fact]
    public async Task PasswordResetComplete_with_short_password_returns_400_password_too_short()
    {
        var customer = await SeedConfirmedAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = tokenId, newPassword = "short" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        body!.title.Should().Be("password-too-short");
    }

    private sealed record ProblemBody(string title, string? detail, int? status);
}
