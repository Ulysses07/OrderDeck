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
using OrderItem = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using OrderResult = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Spec 2026-10-13 §10.1-21 gerçek SQL Server'da: mod On'da açılış penceresindeki kopyanın (§4.0) <c>shipments/sync</c>
/// kararı alınır ve tutar bağlardan türetilir; <c>CommandedAt</c>'li dosyaya dokunulmaz. Dosya bağlarından önce gelirse
/// (kargo gönderimi etiket gönderiminden sık koşar) sonra gelen <c>orders/sync</c> K4 bağı dosyayı yeniden türetir (K7).
/// <c>PreviousReplicaId</c>'li ya da modu bir kez görmüş kopyanın <c>SeenMode = 0</c>'ı On kurallarıyla yargılanır
/// (§4.0, §8.7). Kural tablosunun InMemory ayrıntısı <see cref="ShipmentSyncModeOnTests"/>'te.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class ShipmentSyncOpeningWindowRelationalTests : IAsyncLifetime
{
    private static readonly DateTimeOffset CreatedAt = new(2026, 10, 1, 19, 0, 0, TimeSpan.Zero);

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;
    private HttpClient _client = null!;
    private Guid _customerId;

    public ShipmentSyncOpeningWindowRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

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

    /// <summary>
    /// Pencerenin kararı alınır, tutar push'tan (9999) değil bağlı, iptal edilmemiş siparişlerden türetilir; aynı
    /// gönderimdeki komutlu (<c>CommandedAt</c>) dosyanın kararı ve tutarı değişmez.
    /// </summary>
    [Fact]
    public async Task Pencere_karari_alinir_tutar_turetilir_komutlu_dosyaya_dokunulmaz()
    {
        var (licenseId, window, _) = await SetupAsync();
        var derived = await SeedShipmentAsync(licenseId, s => s.CumulativeAmount = 0m);
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = derived.Id; o.Price = 120m; });
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = derived.Id; o.Price = 30m; o.CancelledAt = CreatedAt.AddHours(2); });
        var commanded = await SeedShipmentAsync(licenseId, s =>
        {
            s.Status = ShipmentStatus.Held; s.HeldAt = CreatedAt.AddHours(1); s.CommandedAt = CreatedAt.AddHours(1);
            s.CumulativeAmount = 400m;
        });
        var heldAt = CreatedAt.AddDays(1);

        var rows = await PushShipmentsAsync(licenseId, window,
            Item(derived) with { Status = "held", HeldAt = heldAt, CumulativeAmount = 9999m },
            Item(commanded) with { Status = "shipped", ShippedAt = CreatedAt.AddDays(2), CumulativeAmount = 9999m });

        var saved = await ShipmentAsync(derived.Id);
        saved.Status.Should().Be(ShipmentStatus.Held);
        saved.HeldAt.Should().Be(heldAt);
        saved.CumulativeAmount.Should().Be(120m);
        saved.Version.Should().Be(derived.Version + 1);
        var untouched = await ShipmentAsync(commanded.Id);
        untouched.Should().BeEquivalentTo(new
        {
            commanded.Status, commanded.HeldAt, commanded.ShippedAt, commanded.CumulativeAmount, commanded.Version,
        });
        rows.Select(r => (r.Id, r.Status, r.CumulativeAmount, r.Version)).Should().Equal(
            (derived.Id, "held", 120m, saved.Version), (commanded.Id, "held", 400m, commanded.Version));
    }

    /// <summary>
    /// K7: dosya bağlarından önce gelir (yeni dosya 0 tutarla, türetilmiş doğar); sonra aynı kopyanın
    /// <c>orders/sync</c> gönderimi bağları K4 istisnasıyla kurar (biri ekleme, biri var olan siparişte boş bağ) ve dosya
    /// aynı kayıtta doğru tutara türetilir.
    /// </summary>
    [Fact]
    public async Task Baglarindan_once_gelen_dosya_sonraki_K4_bagiyla_yeniden_turetilir()
    {
        var (licenseId, window, _) = await SetupAsync();
        var fileId = Guid.NewGuid();
        var customer = Guid.NewGuid().ToString("N");
        var fileItem = new ShipmentItem(fileId, customer, "held", 9999m, CreatedAt, CreatedAt.AddDays(1), null);

        var first = (await PushShipmentsAsync(licenseId, window, fileItem)).Single();
        first.CumulativeAmount.Should().Be(0m, "bağlar henüz gelmedi");
        first.AmountDerivedAt.Should().NotBeNull();
        first.OriginReplicaId.Should().Be(window);

        var existing = await SeedOrderAsync(licenseId, o => { o.CustomerId = customer; o.Price = 45m; });
        var insert = OrderSyncV2Tests.NewItem(null) with { CustomerId = customer, Price = 80m, ShipmentId = fileId };
        var results = await PushOrdersAsync(licenseId, window, insert, OrderItemOf(existing) with { ShipmentId = fileId });

        results.Select(r => r.Status).Should().Equal("applied", "applied");
        var saved = await ShipmentAsync(fileId);
        saved.CumulativeAmount.Should().Be(125m);
        saved.Status.Should().Be(ShipmentStatus.Held);
        saved.Version.Should().Be(first.Version + 1, "K7 türetmesi aynı kayıtta");
    }

    /// <summary>
    /// §7.1, §10.1-21, kargoya geçiş bağlarından önce: pencerede kargoya verilen dosya — yeni ya da kopyanın var olan
    /// türetilmiş dosyası — bağları sunucuya gelmeden kargolanır ve eksik bağlarla türetilip donar (yeni dosya 0). Karar
    /// yerelde o bağlarla verilmişti: aynı kopyanın sonraki <c>orders/sync</c> gönderimi bağları K4 istisnasıyla kurunca
    /// dosya (Shipped, türetilmiş, komutsuz, kaynağı bu kopya) o bağlarla bir kez daha türetilir. Komutla kargolanmış
    /// dosyanın donmuş tutarına gelen bağ dokunmaz.
    /// </summary>
    [Fact]
    public async Task Pencerede_kargolanan_dosya_baglarindan_once_gelirse_gec_K4_bagi_tutarini_kurar()
    {
        var (licenseId, window, _) = await SetupAsync();
        var customer = Guid.NewGuid().ToString("N");
        var shippedAt = CreatedAt.AddDays(1);
        var newFile = new ShipmentItem(Guid.NewGuid(), customer, "shipped", 9999m, CreatedAt, null, shippedAt);
        var held = await SeedShipmentAsync(licenseId, s =>
        {
            s.CustomerId = customer; s.Status = ShipmentStatus.Held; s.HeldAt = CreatedAt.AddHours(1);
            s.CumulativeAmount = 60m; s.OriginReplicaId = window;
        });
        await SeedOrderAsync(licenseId, o => { o.CustomerId = customer; o.ShipmentId = held.Id; o.Price = 60m; });
        var commanded = await SeedShipmentAsync(licenseId, s =>
        {
            s.CustomerId = customer; s.Status = ShipmentStatus.Shipped; s.ShippedAt = CreatedAt.AddHours(2);
            s.CommandedAt = CreatedAt.AddHours(2); s.CumulativeAmount = 40m; s.OriginReplicaId = window;
        });

        var rows = await PushShipmentsAsync(licenseId, window,
            newFile, Item(held) with { Status = "shipped", ShippedAt = shippedAt, CumulativeAmount = 9999m });

        rows.Select(r => (r.Id, r.Status, r.CumulativeAmount)).Should().Equal(
            [(newFile.Id, "shipped", 0m), (held.Id, "shipped", 60m)], "bağlar henüz gelmedi");
        var existing = await SeedOrderAsync(licenseId, o => { o.CustomerId = customer; o.Price = 45m; });
        var lateToHeld = await SeedOrderAsync(licenseId, o => { o.CustomerId = customer; o.Price = 25m; });
        var toCommanded = await SeedOrderAsync(licenseId, o => { o.CustomerId = customer; o.Price = 15m; });
        var insert = OrderSyncV2Tests.NewItem(null) with { CustomerId = customer, Price = 80m, ShipmentId = newFile.Id };

        var results = await PushOrdersAsync(licenseId, window,
            insert,
            OrderItemOf(existing) with { ShipmentId = newFile.Id },
            OrderItemOf(lateToHeld) with { ShipmentId = held.Id },
            OrderItemOf(toCommanded) with { ShipmentId = commanded.Id });

        results.Select(r => r.Status).Should().AllBe("applied");
        var savedNew = await ShipmentAsync(newFile.Id);
        savedNew.Should().BeEquivalentTo(new { Status = ShipmentStatus.Shipped, CumulativeAmount = 125m },
            "kararın dayandığı bağlar: 80 + 45");
        savedNew.Version.Should().Be(rows[0].Version + 1);
        (await ShipmentAsync(held.Id)).Should().BeEquivalentTo(
            new { Status = ShipmentStatus.Shipped, CumulativeAmount = 85m }, "60 + geç gelen 25");
        (await ShipmentAsync(commanded.Id)).Should().BeEquivalentTo(
            new { CumulativeAmount = 40m, commanded.Version }, "komutla kargolanmış dosyanın tutarı donmuştur");
    }

    public static TheoryData<string> OutsideWindow => new() { "geri-yuklenmis", "modu-gormus" };

    /// <summary>
    /// <c>PreviousReplicaId</c>'li (yedekten geri yüklenmiş, §8.7) ya da modu bu açılıştan beri görmüş kopyanın
    /// <c>SeenMode = 0</c>'ı On kurallarıyla yargılanır (§4.0, M21): kararı alınmaz, yeni dosya eklenmez; etiket
    /// gönderiminde K4 istisnası da yok — boş bağ doldurulmaz.
    /// </summary>
    [Theory]
    [MemberData(nameof(OutsideWindow))]
    public async Task Pencere_disi_kopyanin_sifiri_On_kurallariyla_yargilanir(string kind)
    {
        var (licenseId, _, modeChangedAt) = await SetupAsync();
        var replica = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, replica, modeChangedAt.AddDays(-2), r =>
        {
            if (kind == "geri-yuklenmis") r.PreviousReplicaId = Guid.NewGuid();
            else r.SeenModeOnAt = modeChangedAt.AddMinutes(5);
        });
        var file = await SeedShipmentAsync(licenseId);
        var newFile = new ShipmentItem(Guid.NewGuid(), file.CustomerId, "pending", 0m, CreatedAt, null, null);
        var order = await SeedOrderAsync(licenseId, o => o.CustomerId = file.CustomerId);

        var rows = await PushShipmentsAsync(licenseId, replica,
            Item(file) with { Status = "held", HeldAt = CreatedAt.AddDays(1) }, newFile);
        var link = (await PushOrdersAsync(licenseId, replica, OrderItemOf(order) with { ShipmentId = file.Id })).Single();

        rows.Should().ContainSingle().Which.Status.Should().Be("pending");
        var saved = await ShipmentAsync(file.Id);
        saved.Status.Should().Be(ShipmentStatus.Pending);
        saved.Version.Should().Be(file.Version);
        (await ShipmentOrNullAsync(newFile.Id)).Should().BeNull();
        link.Reason.Should().Be("shipment-server-owned");
        (await OrderAsync(order.Id)).ShipmentId.Should().BeNull();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private sealed record ShipmentItem(
        Guid Id, string CustomerId, string Status, decimal CumulativeAmount, DateTimeOffset CreatedAt,
        DateTimeOffset? HeldAt, DateTimeOffset? ShippedAt);

    private static ShipmentItem Item(Shipment s) => new(
        s.Id, s.CustomerId, s.Status.ToString().ToLowerInvariant(), s.CumulativeAmount, s.CreatedAt, s.HeldAt,
        s.ShippedAt);

    private static OrderItem OrderItemOf(Order o) => OrderSyncV2Tests.NewItem(o.SessionId) with
    {
        Id = o.Id, CustomerId = o.CustomerId, Platform = o.Platform, Username = o.Username,
        DisplayName = o.DisplayName, MessageText = o.MessageText, Code = o.Code, Price = o.Price, AddedAt = o.AddedAt,
        PrintedAt = o.PrintedAt, CancelledAt = o.CancelledAt, CancelReason = o.CancelReason,
        BaseVersion = o.SyncVersion,
    };

    private async Task<List<SalesShipmentItem>> PushShipmentsAsync(Guid licenseId, Guid replicaId, params ShipmentItem[] items)
    {
        var resp = await _client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/shipments/sync",
            new { shipments = items, replicaId, protocol = 2, seenMode = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<SalesShipmentItem>>())!;
    }

    private async Task<List<OrderResult>> PushOrdersAsync(Guid licenseId, Guid replicaId, params OrderItem[] items)
    {
        var resp = await _client.PostAsJsonAsync($"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = items, format = 2, replicaId, protocol = 2, seenMode = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<OrderResult>>())!;
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
            AmountDerivedAt = CreatedAt,
        };
        change?.Invoke(shipment);
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private async Task<Order> SeedOrderAsync(Guid licenseId, Action<Order>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var order = new Order
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Platform = "youtube",
            Username = $"u-{Guid.NewGuid():N}",
            MessageText = "A1",
            Code = "A1",
            Price = 100m,
            AddedAt = CreatedAt,
            PrintedAt = CreatedAt.AddMinutes(1),
            UpdatedAt = CreatedAt,
        };
        change?.Invoke(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task SeedReplicaAsync(
        Guid licenseId, Guid replicaId, DateTimeOffset firstSeenAt, Action<WpfReplica>? change = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var replica = new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = firstSeenAt,
        };
        change?.Invoke(replica);
        db.WpfReplicas.Add(replica);
        await db.SaveChangesAsync();
    }

    private async Task<Shipment> ShipmentAsync(Guid id) =>
        (await ShipmentOrNullAsync(id)) ?? throw new InvalidOperationException("Dosya yok.");

    private async Task<Shipment?> ShipmentOrNullAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id);
    }

    private async Task<Order> OrderAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    /// <summary>Mod On lisansı (bir saat önce açıldı) ve açılış penceresindeki bir kopya: mod açılmadan önce görülmüş,
    /// geri yüklenmemiş, klon izi yok, modu görmemiş (§4.0).</summary>
    private async Task<(Guid LicenseId, Guid Window, DateTimeOffset ModeChangedAt)> SetupAsync()
    {
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var license = new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "key-" + Guid.NewGuid().ToString("N"),
                CustomerId = _customerId,
                SkuCode = "STD",
                ActivationSlots = 2,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
                SalesSyncMode = SalesSyncMode.On,
                SalesSyncModeChangedAt = modeChangedAt,
            };
            db.Licenses.Add(license);
            await db.SaveChangesAsync();
            var window = Guid.NewGuid();
            await SeedReplicaAsync(license.Id, window, modeChangedAt.AddDays(-2));
            return (license.Id, window, modeChangedAt);
        }
    }
}
