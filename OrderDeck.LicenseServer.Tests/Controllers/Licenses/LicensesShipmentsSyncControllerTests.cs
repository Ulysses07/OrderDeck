using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// PR-D (2026-05-13): WPF Shipment outbox push + reverse sync endpoint
/// integration tests. Pattern: <see cref="LicensesPaymentsSyncControllerTests"/>.
/// </summary>
public class LicensesShipmentsSyncControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicensesShipmentsSyncControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId, Guid licenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        Guid licenseId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-SHIP-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            licenseId = license.Id;
        }
        return (client, customerId, licenseId);
    }

    private sealed record SyncedShipmentDto(
        Guid Id, string CustomerId, string Status,
        decimal CumulativeAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt,
        DateTimeOffset UpdatedAt);

    [Fact]
    public async Task Sync_inserts_new_shipment_and_echoes()
    {
        var (client, _, licenseId) = await SetupAsync();
        var shipmentId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow.AddHours(-1);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/shipments/sync",
            new
            {
                shipments = new[]
                {
                    new
                    {
                        id = shipmentId,
                        customerId = "c1hex",
                        status = "pending",
                        cumulativeAmount = 1200.50m,
                        createdAt,
                        heldAt = (DateTimeOffset?)null,
                        shippedAt = (DateTimeOffset?)null
                    }
                }
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedShipmentDto>>();
        body.Should().HaveCount(1);
        body![0].Id.Should().Be(shipmentId);
        body[0].CustomerId.Should().Be("c1hex");
        body[0].Status.Should().Be("pending");
        body[0].CumulativeAmount.Should().Be(1200.50m);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Shipments.FirstAsync(s => s.Id == shipmentId);
        stored.LicenseId.Should().Be(licenseId);
        stored.Status.Should().Be(ShipmentStatus.Pending);
    }

    [Fact]
    public async Task Sync_updates_existing_shipment_state_transition()
    {
        var (client, _, licenseId) = await SetupAsync();
        var shipmentId = Guid.NewGuid();
        var heldAt = DateTimeOffset.UtcNow.AddMinutes(-30);

        // İlk push: Pending
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/shipments/sync",
            new { shipments = new[] {
                new { id = shipmentId, customerId = "c1", status = "pending",
                      cumulativeAmount = 2000m, createdAt = DateTimeOffset.UtcNow.AddHours(-2),
                      heldAt = (DateTimeOffset?)null, shippedAt = (DateTimeOffset?)null }
            }});

        // İkinci push: Held'e geçiş + cumulative artmış
        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/shipments/sync",
            new { shipments = new[] {
                new { id = shipmentId, customerId = "c1", status = "held",
                      cumulativeAmount = 3500m, createdAt = DateTimeOffset.UtcNow.AddHours(-2),
                      heldAt = (DateTimeOffset?)heldAt, shippedAt = (DateTimeOffset?)null }
            }});

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Shipments.FirstAsync(s => s.Id == shipmentId);
        stored.Status.Should().Be(ShipmentStatus.Held);
        stored.CumulativeAmount.Should().Be(3500m);
        stored.HeldAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Sync_rejects_when_license_not_owned_by_caller()
    {
        var (client, _, _) = await SetupAsync();
        var otherLicenseId = Guid.NewGuid();   // farklı, bizim olmayan lisans

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{otherLicenseId}/shipments/sync",
            new { shipments = new[] {
                new { id = Guid.NewGuid(), customerId = "x", status = "pending",
                      cumulativeAmount = 100m, createdAt = DateTimeOffset.UtcNow,
                      heldAt = (DateTimeOffset?)null, shippedAt = (DateTimeOffset?)null }
            }});

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Sync_empty_batch_returns_empty_echo()
    {
        var (client, _, licenseId) = await SetupAsync();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/shipments/sync",
            new { shipments = Array.Empty<object>() });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedShipmentDto>>();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task Since_returns_shipments_after_cursor()
    {
        var (client, _, licenseId) = await SetupAsync();
        var s1 = Guid.NewGuid();
        var s2 = Guid.NewGuid();

        // İki shipment push'la
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/shipments/sync",
            new { shipments = new[] {
                new { id = s1, customerId = "c1", status = "pending", cumulativeAmount = 100m,
                      createdAt = DateTimeOffset.UtcNow.AddHours(-2),
                      heldAt = (DateTimeOffset?)null, shippedAt = (DateTimeOffset?)null }
            }});
        await Task.Delay(10);
        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/shipments/sync",
            new { shipments = new[] {
                new { id = s2, customerId = "c2", status = "pending", cumulativeAmount = 200m,
                      createdAt = DateTimeOffset.UtcNow.AddHours(-1),
                      heldAt = (DateTimeOffset?)null, shippedAt = (DateTimeOffset?)null }
            }});

        // MinValue cursor → ikisi de gelir
        var resp = await client.GetAsync(
            $"/api/v1/licenses/{licenseId}/shipments/since?since={Uri.EscapeDataString(DateTimeOffset.MinValue.ToString("O"))}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<List<SyncedShipmentDto>>();
        body.Should().HaveCount(2);
        body!.Select(s => s.Id).Should().BeEquivalentTo(new[] { s1, s2 });
    }
}
