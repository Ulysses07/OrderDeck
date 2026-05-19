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

public class PanelStatsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelStatsControllerTests(ApiFactory f) => _factory = f;

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-STAT-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static Order MakeOrder(Guid licenseId, decimal price, DateTimeOffset addedAt,
        bool printed = true, bool cancelled = false, bool shippingFee = false, bool tentative = false)
    {
        return new Order
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            CustomerId = "test-cust", Platform = "instagram",
            Username = "@test", DisplayName = "Test",
            MessageText = "x", Price = price,
            AddedAt = addedAt,
            PrintedAt = printed ? addedAt : null,
            CancelledAt = cancelled ? addedAt : null,
            IsShippingFee = shippingFee,
            IsTentativeBackup = tentative,
            UpdatedAt = addedAt
        };
    }

    [Fact]
    public async Task Stats_today_revenue_orderCount_correct()
    {
        var (client, licenseId) = await SeedAsync();
        var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeOrder(licenseId, 100m, trNow.AddHours(-2).UtcDateTime),
                MakeOrder(licenseId, 250m, trNow.AddHours(-3).UtcDateTime),
                MakeOrder(licenseId, 150m, trNow.AddHours(-1).UtcDateTime),
                MakeOrder(licenseId, 999m, trNow.AddDays(-2).UtcDateTime));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/stats?range=today");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(500m);
        doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(3);
        doc.RootElement.GetProperty("range").GetString().Should().Be("today");
    }

    [Fact]
    public async Task Stats_activeStream_null_when_no_live_session()
    {
        var (client, _) = await SeedAsync();
        var resp = await client.GetAsync("/api/panel/stats?range=today");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        var activeId = doc.RootElement.GetProperty("activeStreamId");
        activeId.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Stats_cross_tenant_returns_zero()
    {
        var (clientA, licenseA) = await SeedAsync();
        var (clientB, _) = await SeedAsync();
        var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(MakeOrder(licenseA, 500m, trNow.AddHours(-1).UtcDateTime));
            await db.SaveChangesAsync();
        }

        var resp = await clientB.GetAsync("/api/panel/stats?range=today");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(0m);
        doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(0);
    }
}
