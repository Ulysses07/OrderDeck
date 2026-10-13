using System.Net;
using System.Net.Http.Headers;
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
/// Spec 2026-10-13 §10.1-24 (v1 ve her kip): <c>shipments/sync</c> sunucuda kargoya verilmiş (Shipped) dosyayı
/// Held'e, Pending'e ya da başka bir tutara çeviremez (§7.1, M19). Bugün uç bütün alanları koşulsuz eziyordu; bayat
/// bir yerel ayna kargolanmış dosyayı yeniden açıp yeni etiketi ona bağlatabilirdi. Yerelde de Shipped son durumdur,
/// meşru bir geri dönüş yolu yok. Kural kipten bağımsızdır: mod Off ve On ile ayrı ayrı koşar.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class ShipmentShippedOneWayRelationalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset ShippedAt = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);
    private const decimal ShippedAmount = 300m;

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _customerId;

    public ShipmentShippedOneWayRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _customerId = customerId;
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    public static IEnumerable<object[]> StalePushes()
    {
        foreach (var mode in new[] { SalesSyncMode.Off, SalesSyncMode.On })
        {
            yield return new object[] { mode, "held", ShippedAmount, (DateTimeOffset?)CreatedAt.AddDays(1), null! };
            yield return new object[] { mode, "pending", ShippedAmount, null!, null! };
            yield return new object[] { mode, "shipped", ShippedAmount + 150m, null!, (DateTimeOffset?)ShippedAt.AddDays(1) };
        }
    }

    [Theory]
    [MemberData(nameof(StalePushes))]
    public async Task Kargolanmis_dosya_v1_gonderimiyle_geri_donmez(
        SalesSyncMode mode, string status, decimal amount, DateTimeOffset? heldAt, DateTimeOffset? shippedAt)
    {
        var licenseId = await SeedLicenseAsync(mode);
        var shipmentId = Guid.NewGuid();
        var customerHex = Guid.NewGuid().ToString("N");
        (await PushAsync(licenseId, shipmentId, customerHex, "shipped", ShippedAmount, null, ShippedAt))
            .StatusCode.Should().Be(HttpStatusCode.OK, "ön koşul: dosya kargoya verildi");

        var resp = await PushAsync(licenseId, shipmentId, customerHex, status, amount, heldAt, shippedAt);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var echo = (await resp.Content.ReadFromJsonAsync<List<SyncedShipmentDto>>())!.Single();
        echo.Status.Should().Be("shipped", "yanıt sunucunun satırını döner");
        echo.CumulativeAmount.Should().Be(ShippedAmount);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId);
        stored.Status.Should().Be(ShipmentStatus.Shipped);
        stored.CumulativeAmount.Should().Be(ShippedAmount, "tutar kargoya verildiği anda donar");
        stored.ShippedAt.Should().Be(ShippedAt);
        stored.HeldAt.Should().BeNull();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record SyncedShipmentDto(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt, DateTimeOffset UpdatedAt);

    private Task<HttpResponseMessage> PushAsync(
        Guid licenseId, Guid shipmentId, string customerHex, string status, decimal amount,
        DateTimeOffset? heldAt, DateTimeOffset? shippedAt)
        => _client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/shipments/sync", new
        {
            shipments = new[]
            {
                new
                {
                    id = shipmentId,
                    customerId = customerHex,
                    status,
                    cumulativeAmount = amount,
                    createdAt = CreatedAt,
                    heldAt,
                    shippedAt,
                },
            },
        });

    private async Task<Guid> SeedLicenseAsync(SalesSyncMode mode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
            CustomerId = _customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? DateTimeOffset.UtcNow : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }
}
