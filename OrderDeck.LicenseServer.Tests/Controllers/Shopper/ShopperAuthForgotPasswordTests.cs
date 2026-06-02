using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// forgot-password artık self-service SMS OTP yollar (support request/push YOK);
/// eski manuel davranış forgot-password/escalate'e taşındı. Bu suite her iki
/// endpoint'i kapsar.
/// </summary>
public class ShopperAuthForgotPasswordTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperAuthForgotPasswordTests(ApiFactory factory) => _factory = factory;

    private sealed record ForgotPasswordRequest(string Phone);

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(string phone, Guid shopperId)> SeedShopperAsync(bool deleted = false)
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

    // ── forgot-password: SMS OTP ───────────────────────────────────────────────

    [Fact]
    public async Task ForgotPassword_unknown_phone_returns_202_no_sms()
    {
        var phone = UniquePhone();
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Sms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    [Fact]
    public async Task ForgotPassword_invalid_phone_format_returns_202_no_sms()
    {
        var sentBefore = _factory.Sms.Sent.Count;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest("notaphone"));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Sms.Sent.Count.Should().Be(sentBefore, "invalid phone sends no SMS");
    }

    [Fact]
    public async Task ForgotPassword_deleted_shopper_returns_202_no_sms()
    {
        var (phone, _) = await SeedShopperAsync(deleted: true);

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Sms.Sent.Should().NotContain(m => m.Phone == phone);
    }

    [Fact]
    public async Task ForgotPassword_known_shopper_sends_one_otp_sms_no_support_rows()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var before = DateTimeOffset.UtcNow;

        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var sent = _factory.Sms.Sent.Where(m => m.Phone == phone).ToList();
        sent.Should().HaveCount(1);
        Regex.IsMatch(sent[0].Text, @"\d{6}").Should().BeTrue("OTP message contains a 6-digit code");

        // forgot-password artık support request OLUŞTURMAZ (escalate'e taşındı).
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.ShopperSupportRequests
            .CountAsync(r => r.ShopperId == shopperId && r.CreatedAt >= before);
        count.Should().Be(0);
    }

    // ── forgot-password/escalate: manuel fallback (eski davranış) ───────────────

    [Fact]
    public async Task Escalate_known_shopper_two_active_links_creates_two_support_rows()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var licenseId1 = await SeedLicenseAsync();
        var licenseId2 = await SeedLicenseAsync();
        await SeedActiveLinkAsync(shopperId, licenseId1);
        await SeedActiveLinkAsync(shopperId, licenseId2);

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password/escalate",
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

    [Fact]
    public async Task Escalate_one_left_one_active_creates_one_support_row()
    {
        var (phone, shopperId) = await SeedShopperAsync();
        var licenseIdLeft = await SeedLicenseAsync();
        var licenseIdActive = await SeedLicenseAsync();
        await SeedLeftLinkAsync(shopperId, licenseIdLeft);
        await SeedActiveLinkAsync(shopperId, licenseIdActive);

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password/escalate",
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

    [Fact]
    public async Task Escalate_known_shopper_no_active_links_creates_no_rows()
    {
        var (phone, shopperId) = await SeedShopperAsync();

        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password/escalate",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var count = await db.ShopperSupportRequests
            .CountAsync(r => r.ShopperId == shopperId && r.CreatedAt >= before);
        count.Should().Be(0);
    }

    [Fact]
    public async Task Escalate_unknown_phone_returns_202_no_rows()
    {
        var phone = UniquePhone();
        var before = DateTimeOffset.UtcNow;
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync("/api/v1/shopper/auth/forgot-password/escalate",
                new ForgotPasswordRequest(phone));

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shopperExists = await db.Shoppers.AnyAsync(s => s.Phone == phone);
        shopperExists.Should().BeFalse();
        var anyNew = await db.ShopperSupportRequests
            .Where(r => r.CreatedAt >= before)
            .Join(db.Shoppers.Where(s => s.Phone == phone),
                r => r.ShopperId, s => s.Id, (r, s) => r)
            .AnyAsync();
        anyNew.Should().BeFalse();
    }
}
