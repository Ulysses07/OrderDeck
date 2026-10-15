using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>shipments/sync</c> v2 (spec 2026-10-13 §7.1, §3.1): yanıt dosya başına sunucunun güncel satırı (akış öğesi
/// biçiminde), dosya kaynağı (<c>OriginReplicaId</c>: mod Off'ta dosyanın tek yazıcısı) yalnız v2'de ve boşsa dolar;
/// sunucunun her yazımı dosyanın sürümünü ilerletir ve sürüm eşzamanlılık jetonudur — çakışan yazım 409
/// <c>sync-conflict</c> alır (sipariş partisinin deseni). Mod On'da <c>shipments/sync</c>'in yok sayılması ve açılış
/// penceresi istisnası <see cref="ShipmentSyncModeOnTests"/>'te.
/// </summary>
public sealed class ShipmentSyncV2Tests : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;

    public ShipmentSyncV2Tests(ApiFactory factory) => _factory = factory;

    /// <summary>v2 yanıtı istek sırasıyla dosya başına güncel satırdır (sürüm, türetme ve kaynak dahil). Ekleme ve
    /// güncelleme boş kaynağı çağıranla doldurur; dolu kaynak değişmez.</summary>
    [Fact]
    public async Task V2_yaniti_guncel_satir_kaynak_bossa_cagiranla_dolar()
    {
        var (client, licenseId) = await SetupAsync();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        var inserted = NewItem();
        var legacy = await SeedShipmentAsync(licenseId);
        var owned = await SeedShipmentAsync(licenseId, s => s.OriginReplicaId = first);

        var rows = await PushV2Async(client, licenseId, second,
            inserted, ItemOf(legacy) with { Status = "held", HeldAt = CreatedAt.AddDays(1) }, ItemOf(owned));

        rows.Select(r => r.Id).Should().Equal(inserted.Id, legacy.Id, owned.Id);
        rows[0].OriginReplicaId.Should().Be(second);
        rows[0].Version.Should().Be(0, "ekleme sürüm 0 ile doğar (eski imajın DEFAULT'u gibi)");
        rows[1].OriginReplicaId.Should().Be(second, "boş kaynak dolar");
        rows[1].Status.Should().Be("held");
        rows[1].Version.Should().Be(legacy.Version + 1);
        rows[2].OriginReplicaId.Should().Be(first, "dolu kaynak değişmez");
        rows[2].Version.Should().Be(owned.Version + 1, "sunucunun her yazımı sürümü ilerletir");
        (await ShipmentAsync(legacy.Id)).OriginReplicaId.Should().Be(second);
    }

    /// <summary>Bugünkü istemcinin (v1) yanıtı değişmez ve kaynağı doldurmaz: o istemci etiket–dosya bağını
    /// göndermiyor, dosyanın kaynağı bilinmez (§4.10-6).</summary>
    [Fact]
    public async Task V1_yaniti_eski_DTO_kaynak_dolmaz_surum_ilerler()
    {
        var (client, licenseId) = await SetupAsync();
        var existing = await SeedShipmentAsync(licenseId);
        var inserted = NewItem();
        string[] expected = ["id", "customerId", "status", "cumulativeAmount", "createdAt", "heldAt", "shippedAt", "updatedAt"];

        var resp = await client.PostAsJsonAsync(Url(licenseId),
            new { shipments = new[] { inserted, ItemOf(existing) with { CumulativeAmount = 250m } } });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.EnumerateArray())
            row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(expected);
        (await ShipmentAsync(inserted.Id)).OriginReplicaId.Should().BeNull();
        var updated = await ShipmentAsync(existing.Id);
        updated.OriginReplicaId.Should().BeNull();
        updated.CumulativeAmount.Should().Be(250m);
        updated.Version.Should().Be(existing.Version + 1);
    }

    /// <summary>Kargolanmış dosyaya v2 gönderimi: karar ve tutar donmuştur (§7.1); yanıt güncel Shipped satırı.
    /// Yalnız kaynak doldurma ve CustomerId kanonikleştirmesi yazar; değişmeyen satırın sürümü ilerlemez.</summary>
    [Fact]
    public async Task Kargolanmis_dosyaya_v2_gonderimi_guncel_satiri_doner()
    {
        var (client, licenseId) = await SetupAsync();
        var replica = Guid.NewGuid();
        var shipped = await SeedShipmentAsync(licenseId, s =>
        {
            s.Status = ShipmentStatus.Shipped;
            s.ShippedAt = CreatedAt.AddDays(2);
            s.CumulativeAmount = 300m;
        });
        var stale = ItemOf(shipped) with { Status = "held", CumulativeAmount = 450m, ShippedAt = null };

        var first = (await PushV2Async(client, licenseId, replica, stale)).Single();
        first.Status.Should().Be("shipped");
        first.CumulativeAmount.Should().Be(300m);
        first.OriginReplicaId.Should().Be(replica);
        first.Version.Should().Be(shipped.Version + 1, "kaynak doldurma bir yazımdır");

        var again = (await PushV2Async(client, licenseId, Guid.NewGuid(), stale)).Single();
        again.Version.Should().Be(first.Version, "yazım yoksa sürüm ilerlemez");
        again.OriginReplicaId.Should().Be(replica);
    }

    /// <summary>
    /// Sürüm jetonu (§3.1, notlar #6): dosyaya yazan istek kaydederken başka bir yazımla çakışırsa (jeton) işlem
    /// tümüyle geri alınır ve 409 <c>sync-conflict</c> döner — 500 değil; istemci partiyi yeniden gönderir. Gerçek
    /// jetonun SQL Server kanıtı <see cref="ShipmentVersionTokenRelationalTests"/>'te; burada yalnız eşleme.
    /// <c>shipments/sync</c> mod Off'ta yazar (mod On'da yalnız açılış penceresindeki kopya yazar, onun eşlemesi
    /// <see cref="ShipmentSyncModeOnTests"/>'te); K7 türetmesi mod On ister — lisans iki gönderim arasında açılır.
    /// </summary>
    [Fact]
    public async Task Dosya_yazim_cakismasi_409_sync_conflict()
    {
        using var factory = new ShipmentConflictFactory();
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        var licenseId = Guid.NewGuid();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License
            {
                Id = licenseId, LicenseKey = $"cakisma-{Guid.NewGuid():N}", CustomerId = customerId, SkuCode = "STD",
                ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            });
            var shipment = new Shipment
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = Guid.NewGuid().ToString("N"),
                CumulativeAmount = 100m, CreatedAt = CreatedAt, UpdatedAt = CreatedAt,
                AmountDerivedAt = CreatedAt,
            };
            db.Shipments.Add(shipment);
            db.Orders.Add(new Order
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, CustomerId = shipment.CustomerId, Platform = "youtube",
                Username = $"u-{Guid.NewGuid():N}", MessageText = "A1", Code = "A1", Price = 100m,
                AddedAt = CreatedAt, UpdatedAt = CreatedAt, ShipmentId = shipment.Id,
            });
            await db.SaveChangesAsync();
        }
        Shipment seeded;
        Order linked;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            seeded = await db.Shipments.AsNoTracking().SingleAsync(s => s.LicenseId == licenseId);
            linked = await db.Orders.AsNoTracking().SingleAsync(o => o.LicenseId == licenseId);
        }
        factory.Armed = true;

        var shipmentPush = await client.PostAsJsonAsync(Url(licenseId),
            new { shipments = new[] { ItemOf(seeded) with { Status = "held" } } });
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = await db.Licenses.SingleAsync(l => l.Id == licenseId);
            license.SalesSyncMode = SalesSyncMode.On;
            license.SalesSyncModeChangedAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var orderPush = await client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/orders/sync", new
        {
            orders = new[]
            {
                new
                {
                    id = linked.Id, sessionId = (Guid?)null, customerId = linked.CustomerId, platform = "youtube",
                    username = linked.Username, displayName = (string?)null, messageText = "A1", code = "A1",
                    price = 100m, addedAt = CreatedAt, printedAt = (DateTimeOffset?)null,
                    cancelledAt = (DateTimeOffset?)CreatedAt.AddDays(1), cancelReason = "iade",
                    isShippingFee = false, isBackupPromoted = false, isTentativeBackup = false,
                },
            },
        });

        foreach (var resp in new[] { shipmentPush, orderPush })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await resp.Content.ReadAsStringAsync()).Should().Contain("sync-conflict");
        }
        factory.Thrown.Should().Be(2, "shipments/sync ve K7 türetmesi dosyaya yazmayı denedi");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record Item(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt);

    private static Item NewItem() =>
        new(Guid.NewGuid(), Guid.NewGuid().ToString("N"), "pending", 100m, CreatedAt, null, null);

    private static Item ItemOf(Shipment s) => new(
        s.Id, s.CustomerId, s.Status.ToString().ToLowerInvariant(), s.CumulativeAmount, s.CreatedAt, s.HeldAt,
        s.ShippedAt);

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/shipments/sync";

    private static async Task<List<SalesShipmentItem>> PushV2Async(
        HttpClient client, Guid licenseId, Guid replicaId, params Item[] items)
    {
        var resp = await client.PostAsJsonAsync(Url(licenseId),
            new { shipments = items, replicaId, protocol = 2, seenMode = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<SalesShipmentItem>>())!;
    }

    private async Task<Shipment> SeedShipmentAsync(Guid licenseId, Action<Shipment>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Status = ShipmentStatus.Pending,
            CumulativeAmount = 100m,
            CreatedAt = CreatedAt,
            UpdatedAt = CreatedAt,
            Version = 3,
        };
        change?.Invoke(shipment);
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

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-DOSYA-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    /// <summary>Kurulduktan sonra (<see cref="Armed"/>) değişmiş bir <see cref="Shipment"/> taşıyan her kaydetmede
    /// eşzamanlılık çakışması fırlatır — jetonun yakaladığı yarışın uçtaki karşılığı.</summary>
    private sealed class ShipmentConflictFactory : ApiFactory
    {
        private int _thrown;

        public bool Armed { get; set; }

        public int Thrown => Volatile.Read(ref _thrown);

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt)
            => opt.AddInterceptors(new Interceptor(this));

        private sealed class Interceptor(ShipmentConflictFactory owner) : SaveChangesInterceptor
        {
            public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
                DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
            {
                if (owner.Armed && eventData.Context?.ChangeTracker.Entries<Shipment>()
                        .Any(e => e.State == EntityState.Modified) == true)
                {
                    Interlocked.Increment(ref owner._thrown);
                    throw new DbUpdateConcurrencyException("Eşzamanlı dosya yazımı");
                }

                return base.SavingChangesAsync(eventData, result, cancellationToken);
            }
        }
    }
}
