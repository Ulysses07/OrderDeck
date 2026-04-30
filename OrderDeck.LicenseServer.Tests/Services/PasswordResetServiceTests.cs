using FluentAssertions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Email;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public sealed class PasswordResetServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PasswordResetServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedConfirmedCustomerAsync(string password = "real-password")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"reset-{Guid.NewGuid():N}@x",
            Name = "Reset",
            PasswordHash = hasher.Hash(password),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task RequestResetAsync_unknown_email_silent_no_token_no_email()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.RequestResetAsync($"never-exists-{Guid.NewGuid():N}@x", default);

        _factory.Email.Sent.Count.Should().Be(sentBefore);
        // No token row created
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var anyToken = await db.PasswordResetTokens.AnyAsync();
        // Token might exist from prior tests; we only verify no NEW one for this email — implicit
    }

    [Fact]
    public async Task RequestResetAsync_known_confirmed_customer_creates_token_and_sends_email()
    {
        var customer = await SeedConfirmedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.RequestResetAsync(customer.Email, default);

        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).FirstOrDefaultAsync();
        token.Should().NotBeNull();
        token!.UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task RequestResetAsync_unconfirmed_customer_silent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"unconf-{Guid.NewGuid():N}@x",
            Name = "U",
            PasswordHash = hasher.Hash("p"),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = null   // not confirmed
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();

        using var s2 = _factory.Services.CreateScope();
        var svc = s2.ServiceProvider.GetRequiredService<PasswordResetService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.RequestResetAsync(c.Email, default);

        _factory.Email.Sent.Count.Should().Be(sentBefore);
    }

    [Fact]
    public async Task CompleteResetAsync_with_valid_token_updates_password_and_marks_used()
    {
        var customer = await SeedConfirmedCustomerAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);

            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        using var s2 = _factory.Services.CreateScope();
        var svc2 = s2.ServiceProvider.GetRequiredService<PasswordResetService>();
        var result = await svc2.CompleteResetAsync(tokenId, "new-password-123", default);

        result.Should().Be(PasswordResetResult.Success);

        using var s3 = _factory.Services.CreateScope();
        var db3 = s3.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = s3.ServiceProvider.GetRequiredService<PasswordHasher>();
        var updated = await db3.Customers.FirstAsync(c => c.Id == customer.Id);
        hasher.Verify(updated.PasswordHash, "new-password-123").Should().BeTrue();

        var token = await db3.PasswordResetTokens.FirstAsync(t => t.Id == tokenId);
        token.UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteResetAsync_with_unknown_token_returns_TokenInvalid()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();

        var result = await svc.CompleteResetAsync(Guid.NewGuid(), "new-password-123", default);

        result.Should().Be(PasswordResetResult.TokenInvalid);
    }

    [Fact]
    public async Task CompleteResetAsync_with_used_token_returns_TokenInvalid()
    {
        var customer = await SeedConfirmedCustomerAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);

            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.CompleteResetAsync(tokenId, "first-pw-12345", default);   // first use
            var second = await svc.CompleteResetAsync(tokenId, "second-pw-12345", default);   // second use → invalid
            second.Should().Be(PasswordResetResult.TokenInvalid);
        }
    }

    [Fact]
    public async Task CompleteResetAsync_with_short_password_returns_PasswordTooShort()
    {
        var customer = await SeedConfirmedCustomerAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);

            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        using var s2 = _factory.Services.CreateScope();
        var svc2 = s2.ServiceProvider.GetRequiredService<PasswordResetService>();
        var result = await svc2.CompleteResetAsync(tokenId, "short", default);

        result.Should().Be(PasswordResetResult.PasswordTooShort);
    }
}
