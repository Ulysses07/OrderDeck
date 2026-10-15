using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Spec 2026-10-13 §10.1-24 (her kipte, v1 ve v2): <c>shipments/sync</c> sunucuda kargoya verilmiş (Shipped) dosyayı
/// Held'e, Pending'e ya da başka bir tutara çeviremez (§7.1, M19). Bugün uç bütün alanları koşulsuz eziyordu; bayat
/// bir yerel ayna kargolanmış dosyayı yeniden açıp yeni etiketi ona bağlatabilirdi. Yerelde de Shipped son durumdur,
/// meşru bir geri dönüş yolu yok. Kural kipten bağımsızdır: mod Off ve On ile ayrı ayrı koşar. v2 yanıtı güncel
/// satırı döner (Ws onu yerel aynaya yazar); dosya kaynağı (<c>OriginReplicaId</c>) yalnız v2'de dolar.
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
        // Dosya mod Off'ta gönderimle açılır (mod On'da gönderim dosya açmaz, §7.1), sonra lisans kipine geçer.
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var shipmentId = Guid.NewGuid();
        var customerHex = Guid.NewGuid().ToString("N");
        (await PushAsync(licenseId, shipmentId, customerHex, "shipped", ShippedAmount, null, ShippedAt))
            .StatusCode.Should().Be(HttpStatusCode.OK, "ön koşul: dosya kargoya verildi");
        await SetModeAsync(licenseId, mode);

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

    /// <summary>
    /// v2 (§7.1, §3.1): bayat v2 gönderimi de kargolanmış dosyayı geri çeviremez; yanıt dosyanın güncel satırıdır
    /// (Shipped, donmuş tutar). Dosya kaynağı yalnız v2'de ve boşsa dolar: v1 gönderimi dosyayı kaynağı bilinmez
    /// bırakır. Mod On'da açılış penceresi dışındaki v2 gönderimi hiçbir şey yazmaz (kaynak da boş kalır).
    /// </summary>
    [Theory]
    [InlineData(SalesSyncMode.Off)]
    [InlineData(SalesSyncMode.On)]
    public async Task Kargolanmis_dosyaya_v2_gonderimi_guncel_satiri_doner_kaynak_yalniz_v2de_dolar(SalesSyncMode mode)
    {
        // Dosya mod Off'ta gönderimle açılır (mod On'da gönderim dosya açmaz, §7.1), sonra lisans kipine geçer.
        var licenseId = await SeedLicenseAsync(SalesSyncMode.Off);
        var shipmentId = Guid.NewGuid();
        var customerHex = Guid.NewGuid().ToString("N");
        (await PushAsync(licenseId, shipmentId, customerHex, "shipped", ShippedAmount, null, ShippedAt))
            .StatusCode.Should().Be(HttpStatusCode.OK, "ön koşul: dosya v1'le açıldı ve kargoya verildi");
        (await PushAsync(licenseId, shipmentId, customerHex, "held", ShippedAmount, CreatedAt.AddDays(1), null))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await StoredAsync(shipmentId)).OriginReplicaId.Should().BeNull("v1 dosya kaynağı yazmaz");
        await SetModeAsync(licenseId, mode);

        var replicaId = Guid.NewGuid();
        var resp = await PushAsync(licenseId, shipmentId, customerHex, "held", ShippedAmount + 150m,
            CreatedAt.AddDays(1), null, replicaId);

        // Mod On'da kopya mod açıldıktan sonra doğdu: açılış penceresinde değil, gönderim yok sayılır.
        var expectedOrigin = mode == SalesSyncMode.Off ? replicaId : (Guid?)null;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var current = (await resp.Content.ReadFromJsonAsync<List<SalesShipmentItem>>())!.Single();
        current.Id.Should().Be(shipmentId);
        current.Status.Should().Be("shipped");
        current.CumulativeAmount.Should().Be(ShippedAmount);
        current.ShippedAt.Should().Be(ShippedAt);
        current.HeldAt.Should().BeNull();
        current.OriginReplicaId.Should().Be(expectedOrigin);
        var stored = await StoredAsync(shipmentId);
        stored.Status.Should().Be(ShipmentStatus.Shipped);
        stored.CumulativeAmount.Should().Be(ShippedAmount);
        stored.OriginReplicaId.Should().Be(expectedOrigin);
        current.Version.Should().Be(stored.Version);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private async Task SetModeAsync(Guid licenseId, SalesSyncMode mode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = await db.Licenses.SingleAsync(l => l.Id == licenseId);
        license.SalesSyncMode = mode;
        license.SalesSyncModeChangedAt = mode == SalesSyncMode.On ? DateTimeOffset.UtcNow : null;
        await db.SaveChangesAsync();
    }

    private async Task<Shipment> StoredAsync(Guid shipmentId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == shipmentId);
    }

    private sealed record SyncedShipmentDto(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount,
        DateTimeOffset CreatedAt, DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt, DateTimeOffset UpdatedAt);

    /// <param name="replicaId">Verilirse v2 zarfıyla.</param>
    private Task<HttpResponseMessage> PushAsync(
        Guid licenseId, Guid shipmentId, string customerHex, string status, decimal amount,
        DateTimeOffset? heldAt, DateTimeOffset? shippedAt, Guid? replicaId = null)
    {
        var shipments = new[]
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
        };
        object body = replicaId is { } r
            ? new { shipments, replicaId = r, protocol = 2, seenMode = 0 }
            : new { shipments };
        return _client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/shipments/sync", body);
    }

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
