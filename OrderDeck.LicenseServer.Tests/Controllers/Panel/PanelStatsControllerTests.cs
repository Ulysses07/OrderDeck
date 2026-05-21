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

    /// <summary>
    /// "Today TR" penceresinde kalmasını garantileyen <c>count</c> farklı
    /// timestamp döner — şu an'dan eşit aralıklarla geriye, ama TR günbaşından
    /// önceye sarkmadan. Server "today" filtresi [TR-00:00, TR-now] aralığı;
    /// önceki tests <c>trNow.AddHours(-N)</c> kullanıyordu, gece 00-04 TR'de
    /// dün'e kayıp CI/lokal'i flaky yapıyordu. <c>count</c> kaç sipariş eklenecekse
    /// o kadar.
    /// </summary>
    private static List<DateTimeOffset> TodayTimestamps(int count)
    {
        var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
        var todayStart = new DateTimeOffset(
            trNow.Year, trNow.Month, trNow.Day, 0, 0, 0, trNow.Offset);
        // 1 dakika buffer — UTC clock skew için.
        var availableMin = Math.Max(1, (int)(trNow - todayStart).TotalMinutes - 1);
        var step = Math.Max(1, availableMin / (count + 1));
        return Enumerable.Range(1, count)
            .Select(i => trNow.AddMinutes(-i * step))
            .ToList();
    }

    [Fact]
    public async Task Stats_today_revenue_orderCount_correct()
    {
        var (client, licenseId) = await SeedAsync();
        var todayTs = TodayTimestamps(3);
        var trYesterday = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3)).AddDays(-2);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeOrder(licenseId, 100m, todayTs[0].UtcDateTime),
                MakeOrder(licenseId, 250m, todayTs[1].UtcDateTime),
                MakeOrder(licenseId, 150m, todayTs[2].UtcDateTime),
                MakeOrder(licenseId, 999m, trYesterday.UtcDateTime));
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
        var todayTs = TodayTimestamps(1);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(MakeOrder(licenseA, 500m, todayTs[0].UtcDateTime));
            await db.SaveChangesAsync();
        }

        var resp = await clientB.GetAsync("/api/panel/stats?range=today");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);
        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(0m);
        doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(0);
    }

    [Fact]
    public async Task Stats_today_excludes_cancelled_and_shipping_fees()
    {
        var (client, licenseId) = await SeedAsync();
        var todayTs = TodayTimestamps(4);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.AddRange(
                MakeOrder(licenseId, 100m, todayTs[0].UtcDateTime),
                MakeOrder(licenseId, 500m, todayTs[1].UtcDateTime, cancelled: true),
                MakeOrder(licenseId, 50m,  todayTs[2].UtcDateTime, shippingFee: true),
                MakeOrder(licenseId, 999m, todayTs[3].UtcDateTime, tentative: true));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/stats?range=today");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(100m);
        doc.RootElement.GetProperty("orderCount").GetInt32().Should().Be(1);
        doc.RootElement.GetProperty("averageOrderValue").GetDecimal().Should().Be(100m);
    }

    [Fact]
    public async Task Stats_cancelRate_calculation()
    {
        var (client, licenseId) = await SeedAsync();
        var todayTs = TodayTimestamps(10); // 8 normal + 2 cancelled, all today TR
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            for (int i = 0; i < 8; i++)
                db.Orders.Add(MakeOrder(licenseId, 100m, todayTs[i].UtcDateTime));
            for (int i = 0; i < 2; i++)
                db.Orders.Add(MakeOrder(licenseId, 100m, todayTs[8 + i].UtcDateTime, cancelled: true));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/stats?range=today");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        doc.RootElement.GetProperty("cancelRate").GetDecimal().Should().Be(0.2m);
    }

    [Fact]
    public async Task Stats_month_includes_orders_since_first_of_month()
    {
        var (client, licenseId) = await SeedAsync();
        var trNow = DateTimeOffset.UtcNow.ToOffset(TimeSpan.FromHours(3));
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var firstOfMonthTr = new DateTimeOffset(trNow.Year, trNow.Month, 1, 12, 0, 0, trNow.Offset);
            db.Orders.Add(MakeOrder(licenseId, 100m, firstOfMonthTr.UtcDateTime));
            var prevMonth = firstOfMonthTr.AddMonths(-1);
            db.Orders.Add(MakeOrder(licenseId, 999m, prevMonth.UtcDateTime));
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/panel/stats?range=month");
        var body = await resp.Content.ReadAsStringAsync();
        using var doc = System.Text.Json.JsonDocument.Parse(body);

        doc.RootElement.GetProperty("revenue").GetDecimal().Should().Be(100m);
        doc.RootElement.GetProperty("range").GetString().Should().Be("month");
    }
}
