using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelCustomersControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public PanelCustomersControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record CustomerSummaryDto(
        string CustomerId,
        string? DisplayName,
        string Username,
        string Platform,
        int OrderCount,
        decimal TotalRevenue,
        int ActiveShipmentCount,
        DateTimeOffset FirstOrderAt,
        DateTimeOffset LastOrderAt,
        List<RecentOrderDto> RecentOrders,
        List<ShipmentDto> ActiveShipments);

    private sealed record RecentOrderDto(
        Guid Id, Guid? SessionId, string? SessionTitle,
        string MessageText, string? Code, decimal Price,
        DateTimeOffset AddedAt, DateTimeOffset? PrintedAt,
        DateTimeOffset? CancelledAt, bool IsShippingFee);

    private sealed record ShipmentDto(
        Guid Id, string Status, decimal CumulativeAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? HeldAt);

    private async Task<(HttpClient client, Guid licenseId, string customerWpfId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        var customerWpfId = Guid.NewGuid().ToString("N");
        Guid licenseId;

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-CUST-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        licenseId = license.Id;

        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Test yayını",
            StartedAt = DateTimeOffset.UtcNow.AddHours(-2),
            EndedAt = DateTimeOffset.UtcNow.AddHours(-1),
            Platforms = "youtube",
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.StreamSessions.Add(session);

        var now = DateTimeOffset.UtcNow;

        // 3 basılmış order, 1 iptal, 1 kargo ücreti
        db.Orders.AddRange(
            MkOrder(licenseId, session.Id, customerWpfId, "x #1", 100m, addedAt: now.AddMinutes(-10), printedAt: now.AddMinutes(-9)),
            MkOrder(licenseId, session.Id, customerWpfId, "x #2", 200m, addedAt: now.AddMinutes(-8), printedAt: now.AddMinutes(-7)),
            MkOrder(licenseId, session.Id, customerWpfId, "x #3", 150m, addedAt: now.AddMinutes(-6), printedAt: now.AddMinutes(-5)),
            MkOrder(licenseId, session.Id, customerWpfId, "iptal", 999m, addedAt: now.AddMinutes(-4), printedAt: now.AddMinutes(-3), cancelledAt: now.AddMinutes(-2), cancelReason: "iade"),
            MkOrder(licenseId, session.Id, customerWpfId, "kargo", 30m, addedAt: now.AddMinutes(-1), printedAt: now, isShippingFee: true)
        );

        db.Shipments.Add(new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = customerWpfId,
            Status = ShipmentStatus.Held,
            CumulativeAmount = 450m,
            CreatedAt = now.AddDays(-1),
            HeldAt = now.AddHours(-2),
            UpdatedAt = now
        });

        await db.SaveChangesAsync();
        return (client, licenseId, customerWpfId);
    }

    private static Order MkOrder(
        Guid licenseId, Guid sessionId, string customerId,
        string text, decimal price,
        DateTimeOffset addedAt, DateTimeOffset? printedAt = null,
        DateTimeOffset? cancelledAt = null, string? cancelReason = null,
        bool isShippingFee = false, bool isTentative = false)
    {
        return new Order
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            SessionId = sessionId,
            CustomerId = customerId,
            Platform = "youtube",
            Username = "ali_veli",
            DisplayName = "Ali Veli",
            MessageText = text,
            Code = null,
            Price = price,
            AddedAt = addedAt,
            PrintedAt = printedAt,
            CancelledAt = cancelledAt,
            CancelReason = cancelReason,
            IsShippingFee = isShippingFee,
            IsBackupPromoted = false,
            IsTentativeBackup = isTentative,
            UpdatedAt = addedAt
        };
    }

    [Fact]
    public async Task Get_returns_customer_aggregate_with_orders_and_shipments()
    {
        var (client, _, customerWpfId) = await SeedAsync();

        var resp = await client.GetAsync($"/api/panel/customers/{customerWpfId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<CustomerSummaryDto>();
        body.Should().NotBeNull();
        body!.CustomerId.Should().Be(customerWpfId);
        body.DisplayName.Should().Be("Ali Veli");
        body.Username.Should().Be("ali_veli");
        body.Platform.Should().Be("youtube");

        // 3 basılmış real sale (iptal ve kargo dahil değil).
        body.OrderCount.Should().Be(3);
        body.TotalRevenue.Should().Be(450m);

        body.ActiveShipmentCount.Should().Be(1);
        body.ActiveShipments.Should().HaveCount(1);
        body.ActiveShipments[0].Status.Should().Be("held");
        body.ActiveShipments[0].CumulativeAmount.Should().Be(450m);

        // RecentOrders 5 satır içermeli (tüm satırlar — iptal/kargo dahil).
        body.RecentOrders.Should().HaveCount(5);
    }

    [Fact]
    public async Task Get_404_when_customer_has_no_orders_under_this_license()
    {
        var (client, _, _) = await SeedAsync();
        var resp = await client.GetAsync($"/api/panel/customers/{Guid.NewGuid():N}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Get_isolates_tenants_other_license_orders_invisible()
    {
        // İlk customer + müşteri seed
        var (client1, _, customer1WpfId) = await SeedAsync();

        // İkinci ayrı customer + ayrı license + aynı WPF customerId — diğer
        // tenant'ın görmemesi gerek.
        var (client2, customer2AuthId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-CUST2-" + Guid.NewGuid().ToString("N"),
                CustomerId = customer2AuthId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
            await db.SaveChangesAsync();
        }

        // client1 görmeli
        (await client1.GetAsync($"/api/panel/customers/{customer1WpfId}"))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        // client2 görememeli (kendi license'ında bu WPF customerId yok)
        (await client2.GetAsync($"/api/panel/customers/{customer1WpfId}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private async Task<(HttpClient client, Guid licenseId)> SeedListAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-MUS-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static Order MakeListOrder(Guid licenseId, string customerId, string platform,
        string username, string? displayName, decimal price,
        DateTimeOffset addedAt, bool printed = true, bool cancelled = false,
        bool isShippingFee = false, bool isTentativeBackup = false)
    {
        return new Order
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            CustomerId = customerId, Platform = platform,
            Username = username, DisplayName = displayName,
            MessageText = "test", Price = price,
            AddedAt = addedAt,
            PrintedAt = printed ? addedAt : null,
            CancelledAt = cancelled ? addedAt : null,
            IsShippingFee = isShippingFee,
            IsTentativeBackup = isTentativeBackup,
            UpdatedAt = addedAt
        };
    }

    [Fact]
    public async Task List_returns_empty_when_no_orders()
    {
        var (client, _) = await SeedListAsync();
        var resp = await client.GetAsync("/api/panel/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("\"customers\":[]").And.Contain("\"nextCursor\":null");
    }

    [Fact]
    public async Task List_returns_customers_with_aggregate_counts()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "alice-ig", "instagram", "@alice", "Alice", 100m, now.AddDays(-1)),
                MakeListOrder(licenseId, "alice-ig", "instagram", "@alice", "Alice", 50m,  now.AddDays(-2)),
                MakeListOrder(licenseId, "bob-tt",   "tiktok",    "@bob",   "Bob",   200m, now.AddDays(-3)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();

        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(2);

        var first = customers[0];
        first.GetProperty("id").GetString().Should().Be("alice-ig");
        first.GetProperty("totalSpent").GetDecimal().Should().Be(150m);
        first.GetProperty("orderCount").GetInt32().Should().Be(2);
        first.GetProperty("isActive").GetBoolean().Should().BeTrue();

        var second = customers[1];
        second.GetProperty("id").GetString().Should().Be("bob-tt");
        second.GetProperty("orderCount").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task List_sort_by_lastOrder_desc_default()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "carol-ig", "instagram", "@carol", "Carol", 10m, now.AddDays(-5)),
                MakeListOrder(licenseId, "dave-ig",  "instagram", "@dave",  "Dave",  10m, now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers[0].GetProperty("id").GetString().Should().Be("dave-ig");
    }

    [Fact]
    public async Task List_excludes_customers_with_only_cancelled_or_tentative_orders()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            // ghost: only cancelled orders → should NOT appear
            db.Orders.Add(MakeListOrder(licenseId, "ghost-ig", "instagram", "@g", "Ghost",
                100m, now.AddDays(-1), printed: true, cancelled: true));
            // tentative: only tentative-backup → should NOT appear
            db.Orders.Add(MakeListOrder(licenseId, "tent-ig", "instagram", "@t", "Tentative",
                100m, now.AddDays(-1), isTentativeBackup: true));
            // real: one real sale + one cancelled → should appear with OrderCount=1
            db.Orders.AddRange(
                MakeListOrder(licenseId, "real-ig", "instagram", "@r", "Real",
                    50m, now.AddDays(-2)),
                MakeListOrder(licenseId, "real-ig", "instagram", "@r", "Real",
                    999m, now.AddDays(-1), cancelled: true));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers");
        resp.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(1);
        customers[0].GetProperty("id").GetString().Should().Be("real-ig");
        customers[0].GetProperty("orderCount").GetInt32().Should().Be(1);
        customers[0].GetProperty("totalSpent").GetDecimal().Should().Be(50m);
    }

    [Fact]
    public async Task List_sort_by_totalSpent_desc()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "low-ig",  "instagram", "@low",  "Low",  100m, now.AddDays(-1)),
                MakeListOrder(licenseId, "high-ig", "instagram", "@high", "High", 999m, now.AddDays(-5)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?sort=totalSpent");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers[0].GetProperty("id").GetString().Should().Be("high-ig");
        customers[1].GetProperty("id").GetString().Should().Be("low-ig");
    }

    [Fact]
    public async Task List_sort_by_orderCount_desc()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "frequent-ig", "instagram", "@f", "Frequent", 10m, now.AddDays(-5)),
                MakeListOrder(licenseId, "frequent-ig", "instagram", "@f", "Frequent", 10m, now.AddDays(-4)),
                MakeListOrder(licenseId, "frequent-ig", "instagram", "@f", "Frequent", 10m, now.AddDays(-3)),
                MakeListOrder(licenseId, "rare-ig",     "instagram", "@r", "Rare",     10m, now.AddDays(-1)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?sort=orderCount");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers[0].GetProperty("id").GetString().Should().Be("frequent-ig");
        customers[0].GetProperty("orderCount").GetInt32().Should().Be(3);
    }

    [Fact]
    public async Task List_sort_by_name_asc()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "zoe-ig",   "instagram", "@zoe",   "Zoe",    10m, now.AddDays(-1)),
                MakeListOrder(licenseId, "anna-ig",  "instagram", "@anna",  "Anna",   10m, now.AddDays(-2)),
                MakeListOrder(licenseId, "mike-ig",  "instagram", "@mike",  "Mike",   10m, now.AddDays(-3)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?sort=name");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers[0].GetProperty("displayName").GetString().Should().Be("Anna");
        customers[1].GetProperty("displayName").GetString().Should().Be("Mike");
        customers[2].GetProperty("displayName").GetString().Should().Be("Zoe");
    }

    [Fact]
    public async Task List_filter_active_within_30_days()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "active-ig",   "instagram", "@a", "Active",   10m, now.AddDays(-5)),
                MakeListOrder(licenseId, "inactive-ig", "instagram", "@i", "Inactive", 10m, now.AddDays(-60)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?activeWithinDays=30");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(1);
        customers[0].GetProperty("id").GetString().Should().Be("active-ig");
        customers[0].GetProperty("isActive").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task List_filter_platform_multi()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "ig-cust", "instagram", "@i", "IG", 10m, now.AddDays(-1)),
                MakeListOrder(licenseId, "tt-cust", "tiktok",    "@t", "TT", 10m, now.AddDays(-2)),
                MakeListOrder(licenseId, "fb-cust", "facebook",  "@f", "FB", 10m, now.AddDays(-3)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?platforms=instagram,tiktok");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(2);
        var ids = new[]
        {
            customers[0].GetProperty("id").GetString(),
            customers[1].GetProperty("id").GetString()
        };
        ids.Should().Contain("ig-cust").And.Contain("tt-cust");
        ids.Should().NotContain("fb-cust");
    }

    [Fact]
    public async Task List_filter_spent_range()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeListOrder(licenseId, "small-ig",  "instagram", "@s",  "S",  50m,    now.AddDays(-1)),
                MakeListOrder(licenseId, "medium-ig", "instagram", "@m",  "M",  500m,   now.AddDays(-2)),
                MakeListOrder(licenseId, "huge-ig",   "instagram", "@h",  "H",  50000m, now.AddDays(-3)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?minSpent=100&maxSpent=1000");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(1);
        customers[0].GetProperty("id").GetString().Should().Be("medium-ig");
    }

    [Fact]
    public async Task List_filter_order_count_range()
    {
        var (client, licenseId) = await SeedListAsync();
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            // single: 1 order, loyal: 5 orders
            db.Orders.Add(MakeListOrder(licenseId, "single-ig", "instagram", "@s", "Single", 10m, now.AddDays(-1)));
            for (int i = 0; i < 5; i++)
                db.Orders.Add(MakeListOrder(licenseId, "loyal-ig", "instagram", "@l", "Loyal", 10m, now.AddDays(-i - 2)));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/customers?minOrders=2");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var customers = doc.RootElement.GetProperty("customers");
        customers.GetArrayLength().Should().Be(1);
        customers[0].GetProperty("id").GetString().Should().Be("loyal-ig");
        customers[0].GetProperty("orderCount").GetInt32().Should().Be(5);
    }
}
