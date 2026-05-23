using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Push;

/// <summary>
/// Shopper "Parolamı unuttum" → server ShopperSupportRequest oluşturduktan
/// sonra bağlı her yayıncıya (Customer) push gönderir. Mevcut
/// ShopperSupportRequest DB davranışı ShopperAuthForgotPasswordTests'te
/// kapsamlı test edilmiş; bu suite sadece push fan-out'unu doğrular.
/// </summary>
public class ForgotPasswordSupportRequestPushTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ForgotPasswordSupportRequestPushTests(ApiFactory factory)
    {
        _factory = factory;
        _factory.Push.Clear();
    }

    private sealed record ForgotPasswordRequest(string Phone);

    private async Task<(string phone, Guid shopperId)> SeedShopperAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();

        var phone = "+9055" + Random.Shared.Next(10_000_000, 99_999_999);
        var shopperId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new Shopper
        {
            Id = shopperId,
            FullName = "Push Test Shopper",
            Phone = phone,
            PasswordHash = hasher.Hash("Pass1234!"),
            Address = "Addr",
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();
        return (phone, shopperId);
    }

    private async Task<(Guid licenseId, Guid customerId)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customerId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId,
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "Push-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customerId,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = "spt-" + Guid.NewGuid().ToString("N")[..8],
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, customerId);
    }

    private async Task SeedActiveLinkAsync(Guid shopperId, Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "u" + Guid.NewGuid().ToString("N")[..6],
            JoinedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task ForgotPassword_one_linked_broadcaster_pushes_once()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var (licenseId, customerId) = await SeedLicenseAsync();
        await SeedActiveLinkAsync(shopperId, licenseId);
        _factory.Push.Clear();

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        _factory.Push.Sent.Should().HaveCount(1);
        var n = _factory.Push.Sent[0];
        n.CustomerId.Should().Be(customerId);
        n.Title.Should().Be("Destek talebi");
        n.Body.Should().Contain("Push Test Shopper");
        n.Data!["type"].Should().Be("support-request");
        n.Data["kind"].Should().Be("forgot-password");
    }

    [Fact]
    public async Task ForgotPassword_two_linked_broadcasters_pushes_each_once()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var (license1, cust1) = await SeedLicenseAsync();
        var (license2, cust2) = await SeedLicenseAsync();
        await SeedActiveLinkAsync(shopperId, license1);
        await SeedActiveLinkAsync(shopperId, license2);
        _factory.Push.Clear();

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        _factory.Push.Sent.Should().HaveCount(2);
        _factory.Push.Sent.Select(s => s.CustomerId).Should().BeEquivalentTo(
            new[] { cust1, cust2 });
    }

    [Fact]
    public async Task ForgotPassword_no_active_links_does_not_push()
    {
        var (phone, _) = await SeedShopperAsync();
        _factory.Push.Clear();

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        _factory.Push.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ForgotPassword_unknown_phone_does_not_push()
    {
        _factory.Push.Clear();
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest("+905555555555"));
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        _factory.Push.Sent.Should().BeEmpty();
    }
}
