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

public class ShopperOrdersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperOrdersTests(ApiFactory factory) => _factory = factory;

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

    private sealed record OrderItem(
        Guid Id,
        Guid? SessionId,
        string? SessionTitle,
        string Platform,
        string MessageText,
        string? Code,
        decimal Price,
        DateTimeOffset AddedAt,
        DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt,
        bool IsShippingFee);

    private sealed record OrdersResponse(OrderItem[] Items, string? NextCursor);

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
            Email = $"ord-{Guid.NewGuid():N}@x.test",
            Name = "Ord-Broadcaster-" + Guid.NewGuid().ToString("N")[..6],
            PasswordHash = "ph",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);

        var code = "ord-" + Guid.NewGuid().ToString("N")[..8];
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

    /// <summary>
    /// Registers a shopper with a matching WpfCustomerProjection so orders can be linked.
    /// Returns (accessToken, shopperId, wpfCustomerId as N-format string).
    /// </summary>
    private async Task<(string accessToken, Guid shopperId, string wpfCustomerIdString)>
        RegisterShopperWithWpfMatchAsync(HttpClient client, Guid licenseId, string broadcasterCode)
    {
        var wpfId = Guid.NewGuid();
        const string platform = "youtube";
        const string username = "ordersuser";

        // Seed WpfCustomerProjection
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfId,
                LicenseId = licenseId,
                Platform = platform,
                Username = username,
                UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var phone = UniquePhone();
        var req = new RegisterRequest(broadcasterCode, "Orders User", phone, "OrdersPass1!", "Ankara", platform, username);
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created, "registration prerequisite must succeed");
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();
        return (body!.AccessToken, body.ShopperId, wpfId.ToString("N"));
    }

    private async Task<Guid> SeedOrderAsync(
        Guid licenseId,
        string customerId,
        DateTimeOffset? addedAt = null,
        DateTimeOffset? cancelledAt = null,
        bool isShippingFee = false,
        string messageText = "Test Order")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var orderId = Guid.NewGuid();
        db.Orders.Add(new Order
        {
            Id = orderId,
            LicenseId = licenseId,
            CustomerId = customerId,
            Platform = "youtube",
            Username = "ordersuser",
            MessageText = messageText,
            Price = 100m,
            AddedAt = addedAt ?? DateTimeOffset.UtcNow,
            CancelledAt = cancelledAt,
            IsShippingFee = isShippingFee,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return orderId;
    }

    // ── T1: Happy path → 200 with own orders, cross-customer excluded ──────

    [Fact]
    public async Task Orders_happy_path_returns_own_orders_only()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, _, wpfCustomerIdString) =
            await RegisterShopperWithWpfMatchAsync(client, licenseId, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var ownOrderId = await SeedOrderAsync(licenseId, wpfCustomerIdString, messageText: "my order");

        // Another customer's order on the same license
        var otherCustomerId = Guid.NewGuid().ToString("N");
        await SeedOrderAsync(licenseId, otherCustomerId, messageText: "other customer");

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OrdersResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(ownOrderId);
    }

    // ── T2: Not linked → 403 ─────────────────────────────────────────────

    [Fact]
    public async Task Orders_not_linked_returns_403()
    {
        var client = _factory.CreateClient();
        var (licenseIdA, codeA) = await SeedLicenseAsync();
        var (unlinkedLicenseId, _) = await SeedLicenseAsync();

        var (token, _, _) = await RegisterShopperWithWpfMatchAsync(client, licenseIdA, codeA);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{unlinkedLicenseId}/orders");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    // ── T3: WpfCustomerId NULL → 200 with empty array ────────────────────

    [Fact]
    public async Task Orders_wpf_customer_null_returns_200_empty()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        // Register without WpfCustomerProjection match — WpfCustomerId will be null
        var phone = UniquePhone();
        var req = new RegisterRequest(code, "No WPF", phone, "NoWpfPass1!", "Ankara", "tiktok", "nowpfuser");
        var resp = await client.PostAsJsonAsync("/api/v1/shopper/auth/register", req);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<AuthResponse>();

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body!.AccessToken);

        var ordersResp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders");
        ordersResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var ordersBody = await ordersResp.Content.ReadFromJsonAsync<OrdersResponse>();

        ordersBody!.Items.Should().BeEmpty();
        ordersBody.NextCursor.Should().BeNull();
    }

    // ── T4: status=active → cancelled excluded ────────────────────────────

    [Fact]
    public async Task Orders_status_active_excludes_cancelled()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, _, wpfId) = await RegisterShopperWithWpfMatchAsync(client, licenseId, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var activeId = await SeedOrderAsync(licenseId, wpfId, messageText: "active");
        await SeedOrderAsync(licenseId, wpfId, cancelledAt: DateTimeOffset.UtcNow, messageText: "cancelled");

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders?status=active");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OrdersResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(activeId);
    }

    // ── T5: status=cancelled → only cancelled ────────────────────────────

    [Fact]
    public async Task Orders_status_cancelled_returns_only_cancelled()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, _, wpfId) = await RegisterShopperWithWpfMatchAsync(client, licenseId, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        await SeedOrderAsync(licenseId, wpfId, messageText: "active");
        var cancelledId = await SeedOrderAsync(licenseId, wpfId, cancelledAt: DateTimeOffset.UtcNow, messageText: "cancelled");

        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders?status=cancelled");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<OrdersResponse>();

        body!.Items.Should().HaveCount(1);
        body.Items[0].Id.Should().Be(cancelledId);
        body.Items[0].CancelledAt.Should().NotBeNull();
    }

    // ── T6: Cursor pagination → no duplicates ────────────────────────────

    [Fact]
    public async Task Orders_cursor_pagination_returns_no_duplicates()
    {
        var client = _factory.CreateClient();
        var (licenseId, code) = await SeedLicenseAsync();

        var (token, _, wpfId) = await RegisterShopperWithWpfMatchAsync(client, licenseId, code);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var baseTime = DateTimeOffset.UtcNow;
        var id1 = await SeedOrderAsync(licenseId, wpfId, addedAt: baseTime.AddSeconds(-2));
        var id2 = await SeedOrderAsync(licenseId, wpfId, addedAt: baseTime.AddSeconds(-1));
        var id3 = await SeedOrderAsync(licenseId, wpfId, addedAt: baseTime.AddSeconds(0));

        var resp1 = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders?limit=2");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var page1 = await resp1.Content.ReadFromJsonAsync<OrdersResponse>();

        page1!.Items.Should().HaveCount(2);
        page1.NextCursor.Should().NotBeNull();

        var resp2 = await client.GetAsync(
            $"/api/v1/shopper/broadcasters/{licenseId}/orders?limit=2&cursor={Uri.EscapeDataString(page1.NextCursor!)}");
        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        var page2 = await resp2.Content.ReadFromJsonAsync<OrdersResponse>();

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
    public async Task Orders_no_auth_returns_401()
    {
        var client = _factory.CreateClient();
        var (licenseId, _) = await SeedLicenseAsync();
        var resp = await client.GetAsync($"/api/v1/shopper/broadcasters/{licenseId}/orders");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
