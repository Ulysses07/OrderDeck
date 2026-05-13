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

/// <summary>
/// PR-D (2026-05-13): Mobile Panel Shipment read endpoint integration tests.
/// Read-only — kararlar WPF tarafında, Panel sadece listeler.
/// </summary>
public class PanelShipmentsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelShipmentsControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record ShipmentDto(
        Guid Id, Guid LicenseId, string CustomerId, string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt);

    private async Task<(HttpClient client, Guid licenseId)> SeedFixtureAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-PANEL-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = now,
                ExpiresAt = now.AddDays(30)
            };
            db.Licenses.Add(license);

            db.Shipments.AddRange(
                new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = "ca",
                    Status = ShipmentStatus.Held, CumulativeAmount = 3000m,
                    CreatedAt = now.AddHours(-2), HeldAt = now.AddHours(-1), UpdatedAt = now.AddMinutes(-30) },
                new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = "cb",
                    Status = ShipmentStatus.RecipientPays, CumulativeAmount = 800m,
                    CreatedAt = now.AddHours(-3), UpdatedAt = now.AddMinutes(-20) },
                new Shipment { Id = Guid.NewGuid(), LicenseId = license.Id, CustomerId = "ca",
                    Status = ShipmentStatus.Shipped, CumulativeAmount = 7500m,
                    CreatedAt = now.AddDays(-1), ShippedAt = now.AddHours(-5), UpdatedAt = now.AddHours(-5) });

            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, licenseId);
    }

    [Fact]
    public async Task List_returns_all_shipments_for_caller()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();
        body.Should().HaveCount(3);
    }

    [Fact]
    public async Task List_filters_by_status_held()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?status=held");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();

        body.Should().HaveCount(1);
        body![0].Status.Should().Be("held");
    }

    [Fact]
    public async Task List_filters_by_status_recipientpays()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?status=recipientpays");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();

        body.Should().HaveCount(1);
        body![0].Status.Should().Be("recipientpays");
    }

    [Fact]
    public async Task List_filters_by_customerId()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?customerId=ca");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();

        body.Should().HaveCount(2);
        body!.Should().OnlyContain(s => s.CustomerId == "ca");
    }

    [Fact]
    public async Task List_invalid_status_returns_400()
    {
        var (client, _) = await SeedFixtureAsync();
        var resp = await client.GetAsync("/api/panel/shipments?status=garbage");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task List_isolates_tenants_other_users_shipments_hidden()
    {
        // Bizim shipment'larımızı seed et
        var (client, myLicense) = await SeedFixtureAsync();

        // Başka bir customer'ın lisansı ve shipment'ı
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var otherCustomer = new Customer
            {
                Id = Guid.NewGuid(),
                Email = $"other-{Guid.NewGuid():N}@example.com",
                EmailConfirmedAt = DateTimeOffset.UtcNow,
                CreatedAt = DateTimeOffset.UtcNow
            };
            var otherLicense = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-OTHER-" + Guid.NewGuid().ToString("N"),
                CustomerId = otherCustomer.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Customers.Add(otherCustomer);
            db.Licenses.Add(otherLicense);
            db.Shipments.Add(new Shipment
            {
                Id = Guid.NewGuid(),
                LicenseId = otherLicense.Id,
                CustomerId = "stranger",
                Status = ShipmentStatus.Held,
                CumulativeAmount = 9999m,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/shipments");
        var body = await resp.Content.ReadFromJsonAsync<List<ShipmentDto>>();
        body!.Should().OnlyContain(s => s.LicenseId == myLicense);
    }
}
