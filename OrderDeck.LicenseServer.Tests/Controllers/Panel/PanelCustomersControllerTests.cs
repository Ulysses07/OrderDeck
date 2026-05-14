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
}
