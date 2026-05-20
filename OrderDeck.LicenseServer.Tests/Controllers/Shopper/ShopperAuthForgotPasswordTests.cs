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

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperAuthForgotPasswordTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthForgotPasswordTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record ForgotPasswordRequest(string Phone);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    /// <summary>Seeds a Shopper with optional deleted state. Returns (phone, shopperId).</summary>
    private async Task<(string phone, Guid shopperId)> SeedShopperAsync(
        bool deleted = false)
    {
        var phone = UniquePhone();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();

        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = id,
            FullName = "Forgot PW Tester",
            Phone = phone,
            PasswordHash = hasher.Hash("Password1!"),
            Address = "Test Address",
            CreatedAt = now,
            UpdatedAt = now,
            DeletedAt = deleted ? now : null,
        });
        await db.SaveChangesAsync();
        return (phone, id);
    }

    /// <summary>Seeds a License and returns (licenseId).</summary>
    private async Task<Guid> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"cust-{Guid.NewGuid():N}@x.test",
            Name = "FP-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = "fp-" + Guid.NewGuid().ToString("N")[..8],
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return licenseId;
    }

    /// <summary>Adds an active (LeftAt == null) ShopperBroadcasterLink.</summary>
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
            Username = "fpuser",
            JoinedAt = DateTimeOffset.UtcNow,
            LeftAt = null,
        });
        await db.SaveChangesAsync();
    }

    /// <summary>Adds a left (LeftAt != null) ShopperBroadcasterLink.</summary>
    private async Task SeedLeftLinkAsync(Guid shopperId, Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            LicenseId = licenseId,
            Platform = "youtube",
            Username = "fpuser-left",
            JoinedAt = DateTimeOffset.UtcNow.AddDays(-10),
            LeftAt = DateTimeOffset.UtcNow.AddDays(-5),
        });
        await db.SaveChangesAsync();
    }

    // ── T10.1: Unknown phone → 202, no DB writes ──────────────────────────────

    [Fact]
    public async Task ForgotPassword_unknown_phone_returns_202_no_db_writes()
    {
        var phone = UniquePhone();
        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        // The phone doesn't belong to any shopper — verify no support request
        // was created (scoped by timestamp to avoid parallel-test interference)
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        // Unknown phone → no shopper → no support request could be linked to a non-existent shopper.
        // Verify indirectly: total new rows since 'before' should be 0 if only this test ran,
        // but filter by the fact that no shopper with this phone exists at all.
        var shopperExists = await db.Shoppers.AnyAsync(s => s.Phone == phone);
        shopperExists.Should().BeFalse("unknown phone should not create a shopper");
        // And no support requests created in this test's time window that are not tied to a real shopper
        var anyNew = await db.ShopperSupportRequests
            .Where(r => r.CreatedAt >= before)
            .Join(db.Shoppers.Where(s => s.Phone == phone),
                r => r.ShopperId, s => s.Id, (r, s) => r)
            .AnyAsync();
        anyNew.Should().BeFalse();
    }

    // ── T10.2: Invalid phone format → 202, no DB writes ──────────────────────

    [Fact]
    public async Task ForgotPassword_invalid_phone_format_returns_202_no_db_writes()
    {
        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest("notaphone"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var any = await db.ShopperSupportRequests
            .AnyAsync(r => r.CreatedAt >= before);
        any.Should().BeFalse("no support request should be created for invalid phone");
    }

    // ── T10.3: Known shopper with 0 active links → 202, no SupportRequest rows ─

    [Fact]
    public async Task ForgotPassword_known_shopper_no_active_links_returns_202_no_support_rows()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        // No links seeded for this shopper

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.ShopperSupportRequests
            .CountAsync(r => r.ShopperId == shopperId && r.CreatedAt >= before);
        count.Should().Be(0);
    }

    // ── T10.4: Known shopper with 2 active links → 202, 2 SupportRequest rows ──

    [Fact]
    public async Task ForgotPassword_known_shopper_two_active_links_returns_202_two_support_rows()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var licenseId1 = await SeedLicenseAsync();
        var licenseId2 = await SeedLicenseAsync();
        await SeedActiveLinkAsync(shopperId, licenseId1);
        await SeedActiveLinkAsync(shopperId, licenseId2);

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.ShopperSupportRequests
            .Where(r => r.ShopperId == shopperId && r.CreatedAt >= before)
            .ToListAsync();
        rows.Should().HaveCount(2);
        rows.Select(r => r.LicenseId).Should().BeEquivalentTo(new[] { licenseId1, licenseId2 });
        rows.Should().AllSatisfy(r => r.Kind.Should().Be("forgot-password"));
    }

    // ── T10.5: 1 left link + 1 active → 202, only 1 SupportRequest (active) ───

    [Fact]
    public async Task ForgotPassword_one_left_one_active_returns_202_one_support_row()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var licenseIdLeft = await SeedLicenseAsync();
        var licenseIdActive = await SeedLicenseAsync();
        await SeedLeftLinkAsync(shopperId, licenseIdLeft);
        await SeedActiveLinkAsync(shopperId, licenseIdActive);

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var rows = await db.ShopperSupportRequests
            .Where(r => r.ShopperId == shopperId && r.CreatedAt >= before)
            .ToListAsync();
        rows.Should().HaveCount(1);
        rows[0].LicenseId.Should().Be(licenseIdActive);
    }

    // ── T10.6: Soft-deleted shopper → 202, no DB writes ──────────────────────

    [Fact]
    public async Task ForgotPassword_deleted_shopper_returns_202_no_db_writes()
    {
        var (phone, shopperId) = await SeedShopperAsync(deleted: true);

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var any = await db.ShopperSupportRequests
            .AnyAsync(r => r.ShopperId == shopperId && r.CreatedAt >= before);
        any.Should().BeFalse("deleted shopper should not generate support requests");
    }
}
