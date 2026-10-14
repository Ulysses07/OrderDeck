using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.Shared.Sales;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using V2Result = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>orders/sync</c>'in kargo kuralları — spec 2026-10-13 §4.2 K4 (kargo bağı), K6 (kargoya verilmiş dosya), K7
/// (türetilmiş dosya, DM7) ve birleştirilmiş durumdan beslenen defter. Kuralın kendisi ortak vektörlerde
/// (<c>orderUnitMerge</c>, <c>shipmentAmount</c>); burada ucun onu kipe, açılış penceresine ve dosyalara doğru
/// bağladığı sabitlenir. K5'in gerçek SQL Server kanıtı <see cref="OrderSyncV2RelationalTests"/>'te.
/// </summary>
public sealed class OrderSyncShipmentRuleTests : IClassFixture<ApiFactory>
{
    private static readonly DateTimeOffset AddedAt = new(2026, 10, 12, 18, 10, 0, TimeSpan.Zero);

    private readonly ApiFactory _factory;

    public OrderSyncShipmentRuleTests(ApiFactory factory) => _factory = factory;

    // ── K4, mod Off ───────────────────────────────────────────────────────────

    /// <summary>K4 mod Off: damgasız bağı v2 yazar ve boşaltır (yerel dosya bağı mod Off'ta push'la gider).</summary>
    [Fact]
    public async Task K4_mod_Off_v2_bagi_yazar_ve_bosaltir()
    {
        var (client, licenseId) = await SetupAsync();
        var replica = Guid.NewGuid();
        var order = await SeedOrderAsync(licenseId);
        var shipmentId = Guid.NewGuid();

        var linked = (await PushV2Async(client, licenseId, replica, ItemOf(order) with { ShipmentId = shipmentId }))
            .Single();
        linked.Status.Should().Be("applied");
        linked.ShipmentId.Should().Be(shipmentId);
        (await OrderAsync(order.Id)).ShipmentId.Should().Be(shipmentId);

        var cleared = (await PushV2Async(client, licenseId, replica, ItemOf(order) with { ShipmentId = null }))
            .Single();
        cleared.Status.Should().Be("applied");
        (await OrderAsync(order.Id)).ShipmentId.Should().BeNull("boşaltma da yazılır");
    }

    /// <summary>K4 mod Off: komutun damgaladığı bağı bayat v2 değiştiremez (<c>ignored</c> + <c>current</c>); güncel
    /// sürümü gören v2 yazar, damga kalır (K2'nin istisnasının aynısı, §15-51).</summary>
    [Fact]
    public async Task K4_mod_Off_damgali_bag_bayat_surumde_yok_sayilir_guncel_surumde_yazilir()
    {
        var (client, licenseId) = await SetupAsync();
        var replica = Guid.NewGuid();
        var commanded = Guid.NewGuid();
        var stampedAt = DateTimeOffset.UtcNow;
        var order = await SeedOrderAsync(licenseId, o =>
        {
            o.ShipmentId = commanded;
            o.ShipmentSetAt = stampedAt;
            o.SyncVersion = 3;
        });
        var local = Guid.NewGuid();

        var stale = (await PushV2Async(client, licenseId, replica,
            ItemOf(order) with { ShipmentId = local, BaseVersion = 2 })).Single();
        stale.Status.Should().Be("ignored");
        stale.IgnoredUnits.Should().Equal("shipment");
        stale.Reason.Should().Be("unit-stamped");
        stale.Current!.ShipmentId.Should().Be(commanded);
        stale.ShipmentId.Should().Be(commanded);

        var fresh = (await PushV2Async(client, licenseId, replica,
            ItemOf(order) with { ShipmentId = local, BaseVersion = stale.Version })).Single();
        fresh.Status.Should().Be("applied");
        var saved = await OrderAsync(order.Id);
        saved.ShipmentId.Should().Be(local);
        saved.ShipmentSetAt.Should().Be(stampedAt, "push damgayı silmez");
    }

    /// <summary>K4: v1 bağı taşımaz ve hiç dokunmaz; zarflı ama biçimsiz (v1 biçimi) istek de. Ekleme de aynı: v2
    /// eklemesi bağı yazar, v1 eklemesi boş bırakır.</summary>
    [Fact]
    public async Task K4_v1_bagi_degistirmez_v2_eklemesi_yazar()
    {
        var (client, licenseId) = await SetupAsync();
        var shipmentId = Guid.NewGuid();
        var order = await SeedOrderAsync(licenseId, o => o.ShipmentId = shipmentId);

        (await client.PostAsJsonAsync(Url(licenseId), new { orders = new[] { ItemOf(order) with { ShipmentId = null } } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsJsonAsync(Url(licenseId), new
            {
                orders = new[] { ItemOf(order) with { ShipmentId = null } },
                replicaId = Guid.NewGuid(), protocol = 2, seenMode = 0,
            }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await OrderAsync(order.Id)).ShipmentId.Should().Be(shipmentId);

        var v2Insert = OrderSyncV2Tests.NewItem(null) with { ShipmentId = shipmentId };
        var v1Insert = OrderSyncV2Tests.NewItem(null) with { ShipmentId = shipmentId };
        (await PushV2Async(client, licenseId, Guid.NewGuid(), v2Insert)).Single().Status.Should().Be("applied");
        (await client.PostAsJsonAsync(Url(licenseId), new { orders = new[] { v1Insert } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await OrderAsync(v2Insert.Id)).ShipmentId.Should().Be(shipmentId);
        (await OrderAsync(v1Insert.Id)).ShipmentId.Should().BeNull();
    }

    // ── K4, mod On ────────────────────────────────────────────────────────────

    /// <summary>K4 mod On: bağ her oturum hâlinde sunucunundur; v2 push'u ne bağlar ne boşaltır
    /// (<c>shipment-server-owned</c>), eklemede de bağ yazılmaz.</summary>
    [Fact]
    public async Task K4_mod_On_v2_bag_alinmaz()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var replica = Guid.NewGuid();
        var serverLink = Guid.NewGuid();
        var unlinked = await SeedOrderAsync(licenseId);
        var linked = await SeedOrderAsync(licenseId, o => o.ShipmentId = serverLink);
        var insert = OrderSyncV2Tests.NewItem(null) with { ShipmentId = Guid.NewGuid() };

        var results = await PushV2Async(client, licenseId, replica, seenMode: 1,
            ItemOf(unlinked) with { ShipmentId = Guid.NewGuid() },
            ItemOf(linked) with { ShipmentId = null },
            insert);

        foreach (var r in results)
        {
            r.Status.Should().Be("ignored");
            r.IgnoredUnits.Should().Equal("shipment");
            r.Reason.Should().Be("shipment-server-owned");
            r.Current.Should().NotBeNull();
        }
        (await OrderAsync(unlinked.Id)).ShipmentId.Should().BeNull();
        (await OrderAsync(linked.Id)).ShipmentId.Should().Be(serverLink);
        (await OrderAsync(insert.Id)).ShipmentId.Should().BeNull("eklemenin kendisi kabul, bağı değil");
    }

    /// <summary>
    /// K4 mod On, açılış penceresi (§4.0, §8.4): modu henüz görmemiş kopyanın <c>SeenMode = 0</c> push'u boş bağı
    /// doldurur (eklemede de); dolu bağı değiştirmez, komutla çözülmüş (damgalı) bağı doldurmaz. Modu görmüş kopyanın
    /// 0'ı pencere sayılmaz.
    /// </summary>
    [Fact]
    public async Task K4_mod_On_acilis_penceresi_yalniz_bos_bagi_doldurur()
    {
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On, modeChangedAt);
        var window = Guid.NewGuid();
        var sawMode = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, window, firstSeenAt: modeChangedAt.AddDays(-2));
        await SeedReplicaAsync(licenseId, sawMode, firstSeenAt: modeChangedAt.AddDays(-2),
            seenModeOnAt: modeChangedAt.AddMinutes(5));
        var target = Guid.NewGuid();
        var empty = await SeedOrderAsync(licenseId);
        var full = await SeedOrderAsync(licenseId, o => o.ShipmentId = Guid.NewGuid());
        var detached = await SeedOrderAsync(licenseId, o => o.ShipmentSetAt = DateTimeOffset.UtcNow);
        var insert = OrderSyncV2Tests.NewItem(null) with { ShipmentId = target };
        var notWindow = await SeedOrderAsync(licenseId);

        var results = await PushV2Async(client, licenseId, window, seenMode: 0,
            ItemOf(empty) with { ShipmentId = target },
            ItemOf(full) with { ShipmentId = target },
            ItemOf(detached) with { ShipmentId = target },
            insert);
        var other = (await PushV2Async(client, licenseId, sawMode, seenMode: 0,
            ItemOf(notWindow) with { ShipmentId = target })).Single();

        results.Single(r => r.Id == empty.Id).Status.Should().Be("applied");
        results.Single(r => r.Id == insert.Id).Status.Should().Be("applied");
        (await OrderAsync(empty.Id)).ShipmentId.Should().Be(target);
        (await OrderAsync(insert.Id)).ShipmentId.Should().Be(target);
        results.Single(r => r.Id == full.Id).Reason.Should().Be("shipment-server-owned");
        (await OrderAsync(full.Id)).ShipmentId.Should().Be(full.ShipmentId);
        results.Single(r => r.Id == detached.Id).Reason.Should().Be("shipment-server-owned");
        (await OrderAsync(detached.Id)).ShipmentId.Should().BeNull("komutla çözülmüş bağ doldurulmaz");
        other.Reason.Should().Be("shipment-server-owned", "modu görmüş kopyanın 0'ı On kurallarıyla yargılanır");
        (await OrderAsync(notWindow.Id)).ShipmentId.Should().BeNull();
    }

    // ── K6 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// K6 (mod On): kargoya verilmiş dosyadaki siparişin fiyat değişikliği reddedilir (<c>shipment-shipped</c>,
    /// v1'de yazılmaz); iptal geçer, bağ kalır ve dosyanın donmuş tutarı değişmez (§7.2).
    /// </summary>
    [Fact]
    public async Task K6_kargolanmis_dosyada_fiyat_reddedilir_iptal_gecer()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var shipment = await SeedShipmentAsync(licenseId, ShipmentStatus.Shipped, 100m, derived: true);
        var order = await SeedOrderAsync(licenseId, o => o.ShipmentId = shipment.Id);

        var rejected = (await PushV2Async(client, licenseId, Guid.NewGuid(), seenMode: 1,
            ItemOf(order) with { Price = 80m })).Single();
        rejected.Status.Should().Be("rejected");
        rejected.IgnoredUnits.Should().Equal("price");
        rejected.Reason.Should().Be("shipment-shipped");
        rejected.Current!.Price.Should().Be(100m);
        (await PushV1Async(client, licenseId, ItemOf(order) with { Price = 80m })).StatusCode
            .Should().Be(HttpStatusCode.OK);
        (await OrderAsync(order.Id)).Price.Should().Be(100m, "v1 de yazamaz");

        var cancelledAt = AddedAt.AddDays(1);
        (await PushV1Async(client, licenseId, ItemOf(order) with { CancelledAt = cancelledAt, CancelReason = "iade" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var saved = await OrderAsync(order.Id);
        saved.CancelledAt.Should().Be(cancelledAt);
        saved.ShipmentId.Should().Be(shipment.Id, "iptal bağı çözmez");
        var frozen = await ShipmentAsync(shipment.Id);
        frozen.CumulativeAmount.Should().Be(100m, "kargolanmış dosyanın tutarı donmuştur");
        frozen.Version.Should().Be(shipment.Version);
    }

    // ── K7 ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// K7 (mod On, DM7, §7.2): kabul edilen iptal türetilmiş açık dosyanın tutarını aynı kayıtta düşürür, iptali geri
    /// almak eski hâline döndürür; bağ kalır. Her türetmede dosyanın sürümü artar.
    /// </summary>
    [Fact]
    public async Task K7_iptal_turetilmis_tutari_dusurur_geri_alma_dondurur()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var shipment = await SeedShipmentAsync(licenseId, ShipmentStatus.Held, 150m, derived: true);
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = shipment.Id; o.Price = 100m; });
        var cancelled = await SeedOrderAsync(licenseId, o => { o.ShipmentId = shipment.Id; o.Price = 50m; });

        (await PushV1Async(client, licenseId, ItemOf(cancelled) with { CancelledAt = AddedAt.AddDays(1), CancelReason = "iade" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        var afterCancel = await ShipmentAsync(shipment.Id);
        afterCancel.CumulativeAmount.Should().Be(100m);
        afterCancel.Version.Should().Be(shipment.Version + 1);
        afterCancel.UpdatedAt.Should().BeAfter(shipment.UpdatedAt);
        (await OrderAsync(cancelled.Id)).ShipmentId.Should().Be(shipment.Id, "iptal bağı çözmez");

        (await PushV1Async(client, licenseId, ItemOf(cancelled))).StatusCode.Should().Be(HttpStatusCode.OK);
        var afterUncancel = await ShipmentAsync(shipment.Id);
        afterUncancel.CumulativeAmount.Should().Be(150m);
        afterUncancel.Version.Should().Be(shipment.Version + 2);
    }

    /// <summary>K7 yalnız türetilmiş (AmountDerivedAt dolu) ve Shipped olmayan dosyaya dokunur; mod Off'ta hiç
    /// türetmez (tutarı WPF yazar).</summary>
    [Fact]
    public async Task K7_kargolanmis_turetilmemis_ve_mod_Off_dosyaya_dokunmaz()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var shipped = await SeedShipmentAsync(licenseId, ShipmentStatus.Shipped, 150m, derived: true);
        var accepted = await SeedShipmentAsync(licenseId, ShipmentStatus.Pending, 999m, derived: false);
        var a = await SeedOrderAsync(licenseId, o => o.ShipmentId = shipped.Id);
        var b = await SeedOrderAsync(licenseId, o => o.ShipmentId = accepted.Id);

        (await PushV1Async(client, licenseId,
                ItemOf(a) with { CancelledAt = AddedAt.AddDays(1) },
                ItemOf(b) with { CancelledAt = AddedAt.AddDays(1) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var s in new[] { shipped, accepted })
        {
            var saved = await ShipmentAsync(s.Id);
            saved.CumulativeAmount.Should().Be(s.CumulativeAmount);
            saved.Version.Should().Be(s.Version);
        }

        var (offClient, offLicense) = await SetupAsync(SalesSyncMode.Off);
        var derived = await SeedShipmentAsync(offLicense, ShipmentStatus.Pending, 100m, derived: true);
        var c = await SeedOrderAsync(offLicense, o => o.ShipmentId = derived.Id);
        (await PushV1Async(offClient, offLicense, ItemOf(c) with { CancelledAt = AddedAt.AddDays(1) }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ShipmentAsync(derived.Id)).CumulativeAmount.Should().Be(100m, "mod Off'ta K7 yok");
    }

    /// <summary>K4 istisnası + K7 (§15-49): açılış penceresindeki eklemenin kurduğu bağ türetilmiş dosyayı aynı kayıtta
    /// yeniden türetir — dosya bağlarından önce gelse de tutar eksik kalmaz.</summary>
    [Fact]
    public async Task K7_acilis_penceresi_eklemesinin_bagi_dosyayi_turetir()
    {
        var modeChangedAt = DateTimeOffset.UtcNow.AddHours(-1);
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On, modeChangedAt);
        var window = Guid.NewGuid();
        await SeedReplicaAsync(licenseId, window, firstSeenAt: modeChangedAt.AddDays(-2));
        var shipment = await SeedShipmentAsync(licenseId, ShipmentStatus.Pending, 0m, derived: true);
        var insert = OrderSyncV2Tests.NewItem(null) with { ShipmentId = shipment.Id, Price = 120m };

        (await PushV2Async(client, licenseId, window, seenMode: 0, insert)).Single().Status.Should().Be("applied");

        var saved = await ShipmentAsync(shipment.Id);
        saved.CumulativeAmount.Should().Be(120m);
        saved.Version.Should().Be(shipment.Version + 1);
    }

    /// <summary>
    /// K7 bağ değişikliği (§4.2: komutlar ve <c>sales/backfill</c> da aynı türetmeyi kullanır): eski ve yeni dosya
    /// izlenen birleşik durumdan türetilir — veritabanı siparişi hâlâ eski dosyada görse de.
    /// </summary>
    [Fact]
    public async Task K7_bag_degisince_eski_ve_yeni_dosya_izlenen_durumdan_turetilir()
    {
        var (_, licenseId) = await SetupAsync(SalesSyncMode.On);
        var from = await SeedShipmentAsync(licenseId, ShipmentStatus.Pending, 130m, derived: true);
        var to = await SeedShipmentAsync(licenseId, ShipmentStatus.Held, 0m, derived: true);
        var moving = await SeedOrderAsync(licenseId, o => { o.ShipmentId = from.Id; o.Price = 100m; });
        await SeedOrderAsync(licenseId, o => { o.ShipmentId = from.Id; o.Price = 30m; });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var deriver = scope.ServiceProvider.GetRequiredService<ShipmentAmountDeriver>();
        var shipments = await deriver.LoadAsync(licenseId, new[] { from.Id, to.Id }, default);
        var order = await db.Orders.SingleAsync(o => o.Id == moving.Id);
        order.ShipmentId = to.Id;

        var derived = await deriver.DeriveAsync(licenseId, shipments,
            ShipmentAmountDeriver.AffectedBy(from.Id, to.Id, [OrderUnit.Shipment]),
            [order], DateTimeOffset.UtcNow, default);
        await db.SaveChangesAsync();

        derived.Should().BeEquivalentTo(new[] { from.Id, to.Id });
        (await ShipmentAsync(from.Id)).CumulativeAmount.Should().Be(30m);
        (await ShipmentAsync(to.Id)).CumulativeAmount.Should().Be(100m);
        (await ShipmentAsync(from.Id)).Version.Should().Be(from.Version + 1);
        (await ShipmentAsync(to.Id)).Version.Should().Be(to.Version + 1);
    }

    /// <summary>Baskı tutara girmez (DM7 basılmamışı da sayar): yalnız baskı değişen sipariş dosyayı türetmez.</summary>
    [Fact]
    public void K7_etkilenen_dosyalar_yalniz_tutar_birimlerinden()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        ShipmentAmountDeriver.AffectedBy(a, a, [OrderUnit.Print]).Should().BeEmpty();
        ShipmentAmountDeriver.AffectedBy(a, a, [OrderUnit.Cancel]).Should().Equal(a);
        ShipmentAmountDeriver.AffectedBy(a, b, [OrderUnit.Shipment]).Should().Equal(a, b);
        ShipmentAmountDeriver.AffectedBy(null, b, [OrderUnit.Shipment]).Should().Equal(b);
        ShipmentAmountDeriver.AffectedBy(null, null, [OrderUnit.Price]).Should().BeEmpty();
    }

    // ── Defter ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Defter birleştirilmiş durumdan beslenir (§4.2, M4): devredilmiş oturumda reddedilen iptal (K5, mod On) deftere
    /// telafi (+1) yazmaz — satış düşülmüş kalır, satır iptalsiz kalır.
    /// </summary>
    [Fact]
    public async Task Defter_reddedilen_iptali_tasimaz()
    {
        var (client, licenseId) = await SetupAsync(SalesSyncMode.On);
        var productId = await SeedProductAsync(licenseId);
        var sessionId = await SeedSessionAsync(licenseId, releasedAt: DateTimeOffset.UtcNow.AddHours(-1));
        var sold = OrderSyncV2Tests.NewItem(sessionId) with { ProductId = productId, PrintedAt = AddedAt.AddMinutes(1) };
        (await client.PostAsJsonAsync(Url(licenseId), new { orders = new[] { sold }, catalogAware = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await MovementsAsync(sold.Id)).Sum(m => m.Quantity).Should().Be(-1, "ön koşul: ekleme kabul, satış düştü");

        (await client.PostAsJsonAsync(Url(licenseId),
                new { orders = new[] { sold with { CancelledAt = AddedAt.AddDays(1) } }, catalogAware = true }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await OrderAsync(sold.Id)).CancelledAt.Should().BeNull("K5: devredilmiş oturumda push iptal yazamaz");
        var movements = await MovementsAsync(sold.Id);
        movements.Should().ContainSingle("reddedilen iptal deftere telafi yazmamalı");
        movements.Sum(m => m.Quantity).Should().Be(-1);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static string Url(Guid licenseId) => $"/api/v1/licenses/{licenseId}/orders/sync";

    private static Item ItemOf(Order o) => new(
        o.Id, o.SessionId, o.CustomerId, o.Platform, o.Username, o.DisplayName, o.MessageText, o.Code, o.Price,
        o.AddedAt, o.PrintedAt, o.CancelledAt, o.CancelReason, o.IsShippingFee, o.IsBackupPromoted,
        o.IsTentativeBackup, o.ProductId, o.ProductVariantId,
        BaseVersion: o.SyncVersion, ParentOrderId: o.ParentOrderId, PrintBatchId: o.PrintBatchId,
        PrintSeq: o.PrintSeq, PriceCorrectedAt: o.PriceCorrectedAt, PaidPrice: o.PaidPrice, ShipmentId: o.ShipmentId);

    private static Task<HttpResponseMessage> PushV1Async(HttpClient client, Guid licenseId, params Item[] items)
        => client.PostAsJsonAsync(Url(licenseId), new { orders = items });

    private static Task<List<V2Result>> PushV2Async(
        HttpClient client, Guid licenseId, Guid replicaId, params Item[] items)
        => PushV2Async(client, licenseId, replicaId, 0, items);

    private static async Task<List<V2Result>> PushV2Async(
        HttpClient client, Guid licenseId, Guid replicaId, int seenMode, params Item[] items)
    {
        var resp = await client.PostAsJsonAsync(Url(licenseId),
            new { orders = items, format = 2, replicaId, protocol = 2, seenMode });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<V2Result>>())!;
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
            AddedAt = AddedAt,
            PrintedAt = AddedAt.AddMinutes(1),
            UpdatedAt = AddedAt,
        };
        change?.Invoke(order);
        db.Orders.Add(order);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Shipment> SeedShipmentAsync(
        Guid licenseId, ShipmentStatus status, decimal amount, bool derived)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var past = DateTimeOffset.UtcNow.AddDays(-1);
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Status = status,
            CumulativeAmount = amount,
            CreatedAt = past,
            ShippedAt = status == ShipmentStatus.Shipped ? past : null,
            UpdatedAt = past,
            Version = 4,
            AmountDerivedAt = derived ? past : null,
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment;
    }

    private async Task<Guid> SeedSessionAsync(Guid licenseId, DateTimeOffset releasedAt)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Akşam yayını",
            StartedAt = AddedAt.AddHours(-1),
            EndedAt = releasedAt,
            ReleasedAt = releasedAt,
            Platforms = "youtube",
            UpdatedAt = releasedAt,
            OwnerReplicaId = Guid.NewGuid(),
        };
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private async Task<Guid> SeedProductAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Code = "P" + Guid.NewGuid().ToString("N")[..8],
            Name = "Kargo Ürünü",
            NameSearch = "kargo urunu",
            DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private async Task SeedReplicaAsync(
        Guid licenseId, Guid replicaId, DateTimeOffset firstSeenAt, DateTimeOffset? seenModeOnAt = null)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.WpfReplicas.Add(new WpfReplica
        {
            LicenseId = licenseId,
            ReplicaId = replicaId,
            Protocol = 2,
            FirstSeenAt = firstSeenAt,
            LastSeenAt = firstSeenAt,
            SeenModeOnAt = seenModeOnAt,
        });
        await db.SaveChangesAsync();
    }

    private async Task<Order> OrderAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleAsync(o => o.Id == id);
    }

    private async Task<Shipment> ShipmentAsync(Guid id)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().SingleAsync(s => s.Id == id);
    }

    private async Task<List<StockMovement>> MovementsAsync(Guid orderId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StockMovements.AsNoTracking().Where(m => m.OrderId == orderId).ToListAsync();
    }

    private async Task<(HttpClient Client, Guid LicenseId)> SetupAsync(
        SalesSyncMode mode = SalesSyncMode.Off, DateTimeOffset? modeChangedAt = null)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-KARGO-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? modeChangedAt ?? DateTimeOffset.UtcNow.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }
}
