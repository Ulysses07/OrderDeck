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
/// Kargo dosyasının sürüm jetonu gerçek SQL Server'da — spec 2026-10-13 §3.1 (<c>Shipments.Version</c> jeton), K7.
/// İki yazıcı aynı dosyaya yazar: <c>shipments/sync</c> (WPF'in son-gelen-kazanır'ı) ve <c>orders/sync</c>'in K7
/// türetmesi. Jeton olmadan, türetme okuduktan sonra kaydedilen bir yazım sessizce ezilirdi; jetonla kaybeden 409
/// alır ve işlemi tümüyle geri alınır. Sıra kancayla kurulur (zamanlamayla değil): isteğin kaydı diske inmeden
/// başka bir bağlam dosyayı yazar. InMemory jetonu uygulamaz; orada yalnız 409 eşlemesi sınanır
/// (<see cref="ShipmentSyncV2Tests"/>).
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class ShipmentVersionTokenRelationalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;
    private HookedFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _licenseId;

    public ShipmentVersionTokenRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _factory = new HookedFactory(await _sql.CreateDatabaseAsync());
        var (_, customerId, jwt) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        _client = _factory.CreateClient();
        _client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", jwt);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = "key-" + Guid.NewGuid().ToString("N"), CustomerId = customerId,
            SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            SalesSyncMode = SalesSyncMode.On, SalesSyncModeChangedAt = DateTimeOffset.UtcNow.AddDays(-1),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        _licenseId = license.Id;
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Reset();
        _client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    /// <summary><c>shipments/sync</c> okuduktan sonra dosyayı başka bir yazıcı (komut, türetme) yazdı: istek 409
    /// <c>sync-conflict</c> alır, öbür yazım kalır.</summary>
    [Fact]
    public async Task Shipments_sync_eszamanli_yazimla_cakisirsa_409_ve_yazmaz()
    {
        var shipment = await SeedShipmentAsync(derived: false);
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;
            await WriteElsewhereAsync(shipment.Id, 777m);
        };

        var resp = await _client.PostAsJsonAsync($"/api/v1/licenses/{_licenseId}/shipments/sync", new
        {
            shipments = new[]
            {
                new
                {
                    id = shipment.Id, customerId = shipment.CustomerId, status = "held", cumulativeAmount = 150m,
                    createdAt = CreatedAt, heldAt = (DateTimeOffset?)CreatedAt.AddDays(1),
                    shippedAt = (DateTimeOffset?)null,
                },
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        (await resp.Content.ReadAsStringAsync()).Should().Contain("sync-conflict");
        var stored = await ShipmentAsync(shipment.Id);
        stored.CumulativeAmount.Should().Be(777m, "öbür yazım ezilmez");
        stored.Status.Should().Be(ShipmentStatus.Pending);
    }

    /// <summary>
    /// K7: siparişin iptali türetilmiş dosyayı aynı kayıtta türetir; türetme okuduktan sonra dosyaya başka bir yazım
    /// inerse kayıt jetona çarpar — iptal de yazılmaz (tek işlem), 409. Tutar bayat okumadan hesaplanıp öbür yazımı
    /// ezemez.
    /// </summary>
    [Fact]
    public async Task K7_turetme_eszamanli_dosya_yazimiyla_cakisirsa_409_ve_iptal_yazilmaz()
    {
        var shipment = await SeedShipmentAsync(derived: true);
        var orderId = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Orders.Add(new Order
            {
                Id = orderId, LicenseId = _licenseId, CustomerId = shipment.CustomerId, Platform = "youtube",
                Username = $"u-{Guid.NewGuid():N}", MessageText = "A1", Code = "A1", Price = 100m,
                AddedAt = CreatedAt, UpdatedAt = CreatedAt, ShipmentId = shipment.Id,
            });
            await db.SaveChangesAsync();
        }
        _factory.Hook.BeforeSave = async () =>
        {
            _factory.Hook.BeforeSave = null;
            await WriteElsewhereAsync(shipment.Id, 777m);
        };

        var resp = await _client.PostAsJsonAsync($"/api/v1/licenses/{_licenseId}/orders/sync", new
        {
            orders = new[]
            {
                new
                {
                    id = orderId, sessionId = (Guid?)null, customerId = shipment.CustomerId, platform = "youtube",
                    username = "izleyici", displayName = (string?)null, messageText = "A1", code = "A1", price = 100m,
                    addedAt = CreatedAt, printedAt = (DateTimeOffset?)null,
                    cancelledAt = (DateTimeOffset?)CreatedAt.AddDays(1), cancelReason = "iade",
                    isShippingFee = false, isBackupPromoted = false, isTentativeBackup = false,
                },
            },
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        using var verify = _factory.Services.CreateScope();
        var vdb = verify.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await vdb.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId)).CancelledAt
            .Should().BeNull("sipariş ve dosya tek kayıtta: biri düşerse ikisi de");
        (await ShipmentAsync(shipment.Id)).CumulativeAmount.Should().Be(777m);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>Başka bir bağlamdan dosyaya yazar (komut ya da başka bir isteğin türetmesi gibi): sürüm ilerler.</summary>
    private async Task WriteElsewhereAsync(Guid shipmentId, decimal amount)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var row = await db.Shipments.SingleAsync(s => s.Id == shipmentId);
        row.CumulativeAmount = amount;
        row.Version++;
        await db.SaveChangesAsync();
    }

    private async Task<Shipment> SeedShipmentAsync(bool derived)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(), LicenseId = _licenseId, CustomerId = Guid.NewGuid().ToString("N"),
            Status = ShipmentStatus.Pending, CumulativeAmount = 100m, CreatedAt = CreatedAt, UpdatedAt = CreatedAt,
            AmountDerivedAt = derived ? CreatedAt : null,
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private async Task<Shipment> ShipmentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private sealed class HookedFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public SaveHookInterceptor Hook { get; } = new();

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) => opt.AddInterceptors(Hook);
    }
}
