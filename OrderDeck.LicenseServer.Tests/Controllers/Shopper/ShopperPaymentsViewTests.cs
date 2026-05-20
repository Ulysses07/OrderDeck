using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperPaymentsViewTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperPaymentsViewTests(ApiFactory factory) => _factory = factory;

    // ── DTOs ─────────────────────────────────────────────────────────────────

    private sealed record RegisterRequest(
        string BroadcasterCode,
        string FullName,
        string Phone,
        string Password,
        string Address,
        string Platform,
        string Username,
        string? Email = null,
        string? Tc = null);

    private sealed record AuthResponse(
        string AccessToken,
        DateTimeOffset AccessTokenExpiresAt,
        string RefreshToken,
        DateTimeOffset RefreshTokenExpiresAt,
        Guid ShopperId,
        object[] Broadcasters);

    private sealed record PaymentItem(
        Guid Id,
        string PayerName,
        decimal Amount,
        DateTimeOffset PaidAt,
        string? ReferansNo,
        string Status,
        string? RejectReason,
        DateTimeOffset CreatedAt,
        DateTimeOffset? ApprovedAt,
        DateTimeOffset? RejectedAt);

    private sealed record PaymentsResponse(PaymentItem[] Items, string? NextCursor);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string UniquePhone() =>
        "+9055" + Random.Shared.Next(10_000_000, 99_999_999).ToString();

    private async Task<(Guid licenseId, string shopperCode)> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"pay-{Guid.NewGuid():N}@x.test",
            Name = "Pay-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "pay-" + Guid.NewGuid().ToString("N")[..8];
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = "STD",
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            ShopperCode = code,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, code);
    }

    private async Task<(string accessToken, Guid shopperId)> RegisterShopperAsync(
        HttpClient client, string broadcasterCode, string platform = "youtube", string username = "payuser")
    {
        var phone = UniquePhone();
        var req = new RegisterRequest(broadcasterCode, "Pay User", phone, "PayPass1!", "Ankara", platform, username);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId);
    }

    private async Task<Guid> SeedPaymentAsync(
        Guid licenseId,
        Guid? shopperId,
        PaymentStatus status = PaymentStatus.Pending,
        DateTimeOffset? createdAt = null,
        string payerName = "Ali Veli")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var paymentId = Guid.NewGuid();
        var now = createdAt ?? DateTimeOffset.UtcNow;
        db.Payments.Add(new Payment
        {
            Id = paymentId,
            LicenseId = licenseId,
            ShopperId = shopperId,
            PayerName = payerName,
            Amount = 500m,
            PaidAt = now.AddMinutes(-10),
            ReferansNo = Guid.NewGuid().ToString("N")[..12],
            Status = status,
            CreatedAt = now,
            UpdatedAt = now,
            ApprovedAt = status == PaymentStatus.Approved ? now : null,
            RejectedAt = status == PaymentStatus.Rejected ? now : null,
        });
        await db.SaveChangesAsync();
        return paymentId;
    }

    // ── T1: Happy path → 200 with own payments only ────────────────────────

    [Fact]
    public async Task Payments_happy_path_returns_own_payments()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, shopperId) = await RegisterShopperAsync(client, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ownPaymentId = await SeedPaymentAsync(licenseId, shopperId, payerName: "My Payment");

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PaymentsResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(ownPaymentId);
        body.Items[0].PayerName.Should().Be("My Payment");
        body.Items[0].Status.Should().Be("pending");
    }

    // ── T2: Not linked → 403 ──────────────────────────────────────────────

    [Fact]
    public async Task Payments_not_linked_returns_403()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA) = await SeedLicenseAsync();
        var (unlinkedLicenseId, _) = await SeedLicenseAsync();

        var (token, _) = await RegisterShopperAsync(client, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{unlinkedLicenseId}/payments");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── T3: Legacy payments (ShopperId null) → excluded ───────────────────

    [Fact]
    public async Task Payments_legacy_whatsapp_payments_excluded()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, shopperId) = await RegisterShopperAsync(client, code, platform: "instagram", username: "legacytest");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        // Own payment
        var ownPaymentId = await SeedPaymentAsync(licenseId, shopperId, payerName: "mine");
        // Legacy (ShopperId = null)
        await SeedPaymentAsync(licenseId, shopperId: null, payerName: "legacy");

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PaymentsResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(ownPaymentId);
    }

    // ── T4: status=pending → only pending ─────────────────────────────────

    [Fact]
    public async Task Payments_status_pending_returns_only_pending()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, shopperId) = await RegisterShopperAsync(client, code, platform: "twitch", username: "pendingtest");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var pendingId = await SeedPaymentAsync(licenseId, shopperId, PaymentStatus.Pending);
        await SeedPaymentAsync(licenseId, shopperId, PaymentStatus.Approved);
        await SeedPaymentAsync(licenseId, shopperId, PaymentStatus.Rejected);

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments?status=pending");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PaymentsResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(pendingId);
        body.Items[0].Status.Should().Be("pending");
    }

    // ── T5: Different shopper's payments → excluded ────────────────────────

    [Fact]
    public async Task Payments_different_shopper_payments_excluded()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        // Register shopper 1
        var (token1, shopperId1) = await RegisterShopperAsync(client, code, platform: "youtube", username: "shopper1diff");

        // Register shopper 2 by joining (different platform to avoid conflict)
        var (token2, shopperId2) = await RegisterShopperAsync(_factory.CreateClient(), code, platform: "instagram", username: "shopper2diff");

        // Seed payment for shopper1
        var shopper1PaymentId = await SeedPaymentAsync(licenseId, shopperId1, payerName: "shopper1");
        // Seed payment for shopper2
        await SeedPaymentAsync(licenseId, shopperId2, payerName: "shopper2");

        // Authenticate as shopper1
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token1);

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<PaymentsResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(shopper1PaymentId);
    }

    // ── T6: Cursor pagination → no duplicates ─────────────────────────────

    [Fact]
    public async Task Payments_cursor_pagination_returns_no_duplicates()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, shopperId) = await RegisterShopperAsync(client, code, platform: "tiktok", username: "cursorpaytest");
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var baseTime = DateTimeOffset.UtcNow;
        var id1 = await SeedPaymentAsync(licenseId, shopperId, createdAt: baseTime.AddSeconds(-2));
        var id2 = await SeedPaymentAsync(licenseId, shopperId, createdAt: baseTime.AddSeconds(-1));
        var id3 = await SeedPaymentAsync(licenseId, shopperId, createdAt: baseTime.AddSeconds(0));

        var resp1 = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments?limit=2");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await resp1.Content.ReadFromJsonAsync<PaymentsResponse>();

        page1!.Items.Should().HaveCount(2);
        page1.NextCursor.Should().NotBeNull();

        var resp2 = await client.GetAsync(
            $"/api/v1/shopper/broadcasters/{licenseId}/payments?limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await resp2.Content.ReadFromJsonAsync<PaymentsResponse>();

        page2!.Items.Should().HaveCount(1);
        page2.NextCursor.Should().BeNull();

        var allIds = page1.Items.Select(i => i.Id).Concat(page2.Items.Select(i => i.Id)).ToList();
        allIds.Should().OnlyHaveUniqueItems();
        allIds.Should().Contain(id1);
        allIds.Should().Contain(id2);
        allIds.Should().Contain(id3);
    }

    // ── T7: No auth → 401 ─────────────────────────────────────────────────

    [Fact]
    public async Task Payments_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var (licenseId, _) = await SeedLicenseAsync();
        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/payments");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
