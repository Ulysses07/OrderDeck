using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST sales/backfill</c> — spec 2026-10-13 §4.3, §8.2-3 (InMemory). Ws'nin ilk açılışında geçmişi tamamlar: yalnız
/// boşu doldurur, hiçbir bildirim üretmez (§10.2), moddan bağımsız ve idempotenttir. Birim ayrışmasının gerçek SQL
/// Server'daki sözleşmesi <see cref="SalesBackfillRelationalTests"/>'te.
/// </summary>
public sealed class SalesBackfillTests
    : IClassFixture<ApiFactory>, IClassFixture<SalesSyncConfiguredApiFactory>
{
    private static readonly DateTimeOffset StartedAt = new(2026, 10, 12, 18, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset EndedAt = StartedAt.AddHours(2);

    private readonly ApiFactory _factory;
    private readonly SalesSyncConfiguredApiFactory _scrubbing;

    public SalesBackfillTests(ApiFactory factory, SalesSyncConfiguredApiFactory scrubbing)
    {
        _factory = factory;
        _scrubbing = scrubbing;
    }

    // ── §10.2: bildirim bastırma ─────────────────────────────────────────────

    /// <summary>
    /// Geri doldurmayla eklenen basılmış, iptal edilmemiş sipariş yayıncıya push, WhatsApp <c>OrderReceived</c> etiket
    /// kuralı ve shopper push üretmez; eklenen canlı oturum "Yayın başladı" göndermez (D1'in Replay deseni). Kontrol:
    /// aynı kişiye <c>orders/sync</c> ve <c>sessions/sync</c> ile gelen yeni satırlar üçünü de üretir — ölçüm
    /// gerçekten bildirimi görebiliyor.
    /// </summary>
    [Fact]
    public async Task Eklemeler_hicbir_bildirim_uretmez()
    {
        var ctx = await SetupAsync(_factory);
        var (conversationId, wpfCustomerId, shopperId) = await SeedNotificationTargetsAsync(ctx.LicenseId);
        var replicaId = Guid.NewGuid();
        var live = NewSession(endedAt: null);
        var order = OrderSyncV2Tests.NewItem(live.Id) with
        {
            CustomerId = wpfCustomerId.ToString("N"),
            PrintedAt = StartedAt.AddMinutes(20),
        };

        var result = await BackfillAsync(ctx, replicaId, sessions: [live], orders: [order]);

        result.Orders.Single().Status.Should().Be("inserted");
        (await OrderAsync(_factory, order.Id)).Should().NotBeNull();
        (await SessionAsync(_factory, live.Id)).Should().NotBeNull();
        CustomerPushes(ctx).Should().BeEmpty("geri doldurma yayıncıya push üretmez (yeni sipariş de yayın başladı da)");
        ShopperPushes(shopperId).Should().BeEmpty();
        (await LabelCountAsync(conversationId)).Should().Be(0, "OrderReceived etiket kuralı uygulanmaz");

        // Kontrol: olağan uçlar aynı kişi için üçünü de üretir.
        var control = OrderSyncV2Tests.NewItem(null) with
        {
            CustomerId = wpfCustomerId.ToString("N"),
            PrintedAt = StartedAt.AddMinutes(30),
        };
        (await ctx.Client.PostAsJsonAsync(Url(ctx, "orders/sync"), new { orders = new[] { control } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await ctx.Client.PostAsJsonAsync(Url(ctx, "sessions/sync"), new { sessions = new[] { NewSession(endedAt: null) } }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        CustomerPushes(ctx).Select(p => p.Title).Should().BeEquivalentTo("Yeni sipariş", "Yayın başladı");
        ShopperPushes(shopperId).Should().ContainSingle();
        (await LabelCountAsync(conversationId)).Should().Be(1);
    }

    // ── Yalnız boşu doldurur ──────────────────────────────────────────────────

    /// <summary>
    /// Mevcut siparişte yalnız boş yeni alanlar dolar (ebeveyn, baskı grubu, düzeltme damgası, ödenen fiyat, kaynak
    /// kopya, kargo bağı); dolu alan ezilmez. Satır değiştiyse sürüm bir kez artar. Aynı çağrı ikinci kez hiçbir alanı
    /// ve sürümü değiştirmez.
    /// </summary>
    [Fact]
    public async Task Mevcut_sipariste_yalniz_bos_alanlar_dolar_ikinci_cagri_etkisiz()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var serverParent = Guid.NewGuid();
        var printedAt = StartedAt.AddMinutes(20);
        var item = OrderSyncV2Tests.NewItem(null) with { PrintedAt = printedAt };
        await PushV1Async(ctx, item);
        await SeedOrderAsync(_factory, item.Id, o => o.ParentOrderId = serverParent);
        var before = await OrderAsync(_factory, item.Id);
        var shipmentId = Guid.NewGuid();
        var local = item with
        {
            ParentOrderId = Guid.NewGuid(),
            PrintBatchId = "b1",
            PrintSeq = 3,
            PriceCorrectedAt = printedAt.AddMinutes(5),
            PaidPrice = 120m,
            ShipmentId = shipmentId,
        };

        var first = await BackfillAsync(ctx, replicaId, orders: [local]);

        var filled = await OrderAsync(_factory, item.Id);
        first.Orders.Single().Should().BeEquivalentTo(new { Id = item.Id, Status = "filled", Version = filled!.SyncVersion });
        first.Diverged.Should().BeEmpty();
        filled.ParentOrderId.Should().Be(serverParent, "dolu alan ezilmez");
        filled.PrintBatchId.Should().Be("b1");
        filled.PrintSeq.Should().Be(3);
        filled.PriceCorrectedAt.Should().Be(local.PriceCorrectedAt);
        filled.PaidPrice.Should().Be(120m);
        filled.ShipmentId.Should().Be(shipmentId);
        filled.OriginReplicaId.Should().Be(replicaId);
        filled.SyncVersion.Should().Be(before!.SyncVersion + 1);
        filled.Price.Should().Be(100m);
        filled.PrintedAt.Should().Be(printedAt);

        var second = await BackfillAsync(ctx, replicaId, orders: [local]);

        second.Orders.Single().Status.Should().Be("unchanged");
        var again = await OrderAsync(_factory, item.Id);
        again.Should().BeEquivalentTo(filled, "ikinci çağrı hiçbir alanı ve sürümü değiştirmez");
    }

    /// <summary>Kaynak kopya yalnız boşsa dolar: başka kopyanın gönderdiği satırın kaynağı değişmez; değişmeyen satırın
    /// sürümü artmaz.</summary>
    [Fact]
    public async Task Kaynak_kopya_yalniz_bossa_dolar()
    {
        var ctx = await SetupAsync(_factory);
        var other = Guid.NewGuid();
        var item = OrderSyncV2Tests.NewItem(null);
        await PushV1Async(ctx, item);
        await SeedOrderAsync(_factory, item.Id, o => o.OriginReplicaId = other);
        var before = await OrderAsync(_factory, item.Id);

        var result = await BackfillAsync(ctx, Guid.NewGuid(), orders: [item]);

        result.Orders.Single().Status.Should().Be("unchanged");
        var after = await OrderAsync(_factory, item.Id);
        after!.OriginReplicaId.Should().Be(other);
        after.SyncVersion.Should().Be(before!.SyncVersion);
    }

    /// <summary>Damgalı birim hiçbir alanıyla yazılmaz (§4.3): komutun dokunduğu fiyatın düzeltme damgası ve ödenen
    /// fiyatı doldurulmaz; komutun dokunduğu kargo bağı (<c>ShipmentSetAt</c>) boş olsa da doldurulmaz.</summary>
    [Fact]
    public async Task Damgali_birim_ve_damgali_bag_doldurulmaz()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var item = OrderSyncV2Tests.NewItem(null);
        await PushV1Async(ctx, item);
        var commandAt = DateTimeOffset.UtcNow;
        await SeedOrderAsync(_factory, item.Id, o => { o.PriceSetAt = commandAt; o.ShipmentSetAt = commandAt; });

        await BackfillAsync(ctx, replicaId,
            orders: [item with { PriceCorrectedAt = StartedAt.AddHours(3), PaidPrice = 90m, ShipmentId = Guid.NewGuid() }]);

        var after = await OrderAsync(_factory, item.Id);
        after!.PriceCorrectedAt.Should().BeNull();
        after.PaidPrice.Should().BeNull();
        after.ShipmentId.Should().BeNull("komutun çözdüğü bağ geri doldurmayla geri gelmez");
        after.OriginReplicaId.Should().Be(replicaId, "damgasız alan yine dolar");
    }

    // ── Oturumlar ─────────────────────────────────────────────────────────────

    /// <summary>
    /// §4.3 oturum: sahip boşsa çağıran yazılır; sunucuda <c>EndedAt</c> boş, yerelde doluysa yazılır (kaçmış kapanış);
    /// devir yalnız öğe <c>Release = true</c> taşıyorsa ve çağıran sahipse olur, yayının siparişleri aynı anla
    /// damgalanır. Dolu <c>EndedAt</c> değişmez. Başka kopyanın oturumuna dokunulmaz (Y1: yayını kapatmanın başka yolu
    /// force-end).
    /// </summary>
    [Fact]
    public async Task Oturum_sahip_ve_kapanis_dolar_devir_yalniz_bayrak_ve_sahiple()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var missed = await SeedSessionAsync(_factory, ctx.LicenseId, owner: null, endedAt: null);
        var missedOrder = OrderSyncV2Tests.NewItem(missed);
        await PushV1Async(ctx, missedOrder);
        var waiting = await SeedSessionAsync(_factory, ctx.LicenseId, owner: null, endedAt: null);
        var foreign = await SeedSessionAsync(_factory, ctx.LicenseId, owner: other, endedAt: null);
        var ownEnded = await SeedSessionAsync(_factory, ctx.LicenseId, owner: replicaId, endedAt: EndedAt);

        var result = await BackfillAsync(ctx, replicaId, sessions:
        [
            NewSession(EndedAt, release: true) with { Id = missed },
            NewSession(EndedAt, release: false) with { Id = waiting },
            NewSession(EndedAt, release: true) with { Id = foreign },
            NewSession(EndedAt.AddHours(5), release: false) with { Id = ownEnded },
        ]);

        var m = await SessionAsync(_factory, missed);
        m!.OwnerReplicaId.Should().Be(replicaId);
        m.OwnerMachineName.Should().Be("PC-A");
        m.EndedAt.Should().Be(EndedAt);
        m.ReleasedAt.Should().NotBeNull("kaçmış kapanış devredilir");
        (await OrderAsync(_factory, missedOrder.Id))!.SessionReleasedAt.Should().Be(m.ReleasedAt);
        result.Sessions.Single(s => s.Id == missed).ReleasedAt.Should().Be(m.ReleasedAt);

        var w = await SessionAsync(_factory, waiting);
        w!.OwnerReplicaId.Should().Be(replicaId);
        w.EndedAt.Should().Be(EndedAt);
        w.ReleasedAt.Should().BeNull("Release = false: etiket kuyruğu sahipte bekliyor (M2)");

        var f = await SessionAsync(_factory, foreign);
        f!.OwnerReplicaId.Should().Be(other);
        f.EndedAt.Should().BeNull("başka kopyanın canlı yayını kapatılamaz");
        f.ReleasedAt.Should().BeNull();

        (await SessionAsync(_factory, ownEnded))!.EndedAt.Should().Be(EndedAt, "dolu EndedAt değişmez");
    }

    /// <summary>Eksik oturum eklenir (sahip çağıran); aynı istekteki siparişleri ona bağlanabilir ve oturum devredilirse
    /// onlar da damgalanır. Devredilmiş oturuma eklenen sipariş devir anını kopyalar (DM3).</summary>
    [Fact]
    public async Task Eksik_oturum_eklenir_siparisleri_devirle_damgalanir()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var fresh = NewSession(EndedAt, release: true);
        var freshOrder = OrderSyncV2Tests.NewItem(fresh.Id);
        var releasedAt = EndedAt.AddMinutes(1);
        var released = await SeedSessionAsync(_factory, ctx.LicenseId, owner: null, endedAt: EndedAt, releasedAt: releasedAt);
        var lateOrder = OrderSyncV2Tests.NewItem(released);

        var result = await BackfillAsync(ctx, replicaId, sessions: [fresh], orders: [freshOrder, lateOrder]);

        var s = await SessionAsync(_factory, fresh.Id);
        s!.OwnerReplicaId.Should().Be(replicaId);
        s.ReleasedAt.Should().NotBeNull();
        (await OrderAsync(_factory, freshOrder.Id))!.SessionReleasedAt.Should().Be(s.ReleasedAt);
        (await OrderAsync(_factory, lateOrder.Id))!.SessionReleasedAt.Should().Be(releasedAt);
        result.Orders.Select(o => o.Status).Should().AllBe("inserted");
    }

    // ── Siparişler ────────────────────────────────────────────────────────────

    /// <summary>Eklemede v2 alanları, kaynak kopya ve kargo bağı yazılır; kopyanın Id'si asıl kayda çevrilir (A5b).
    /// Eklenen satır sürüm 0 ile doğar.</summary>
    [Fact]
    public async Task Ekleme_yeni_alanlari_kaynak_kopyayi_ve_asil_kaydi_yazar()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var (canonical, alias) = await SeedMergedCustomerAsync(_factory, ctx.LicenseId);
        var parent = Guid.NewGuid();
        var shipmentId = Guid.NewGuid();
        var item = OrderSyncV2Tests.NewItem(null) with
        {
            CustomerId = alias,
            ParentOrderId = parent,
            PrintedAt = StartedAt.AddMinutes(5),
            PrintBatchId = "b9",
            PrintSeq = 7,
            PriceCorrectedAt = StartedAt.AddMinutes(6),
            PaidPrice = 130m,
            ShipmentId = shipmentId,
        };

        var result = await BackfillAsync(ctx, replicaId, orders: [item]);

        result.Orders.Single().Should().BeEquivalentTo(new { Id = item.Id, Status = "inserted", Version = 0 });
        var saved = await OrderAsync(_factory, item.Id);
        saved!.CustomerId.Should().Be(canonical);
        saved.ParentOrderId.Should().Be(parent);
        saved.PrintBatchId.Should().Be("b9");
        saved.PrintSeq.Should().Be(7);
        saved.PriceCorrectedAt.Should().Be(item.PriceCorrectedAt);
        saved.PaidPrice.Should().Be(130m);
        saved.ShipmentId.Should().Be(shipmentId);
        saved.OriginReplicaId.Should().Be(replicaId);
        saved.MessageText.Should().Be(item.MessageText);
    }

    /// <summary>Stok defteri eklemede bugünkü gibi çalışır (§4.3): katalog bilen istek hareketi yazar, bilmeyen
    /// deftere dokunmaz.</summary>
    [Fact]
    public async Task Defter_eklemede_katalog_bilen_istekte_yazilir()
    {
        var ctx = await SetupAsync(_factory);
        var productId = await SeedProductAsync(_factory, ctx.LicenseId);
        var aware = OrderSyncV2Tests.NewItem(null) with { ProductId = productId };
        var unaware = OrderSyncV2Tests.NewItem(null) with { ProductId = productId };

        await BackfillAsync(ctx, Guid.NewGuid(), orders: [aware], catalogAware: true);
        await BackfillAsync(ctx, Guid.NewGuid(), orders: [unaware], catalogAware: false);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.StockMovements.Where(m => m.OrderId == aware.Id).SumAsync(m => m.Quantity)).Should().Be(-1);
        (await db.StockMovements.AnyAsync(m => m.OrderId == unaware.Id)).Should().BeFalse();
    }

    /// <summary>KVKK (§4.3 = §4.2 ekleme dalı): ayar açıkken asıl projeksiyonu silinmiş müşteriye geri doldurmayla gelen
    /// ekleme metni boş yazar; kullanıcı adı, kod ve fiyat kalır.</summary>
    [Fact]
    public async Task Ayar_acikken_silinmis_musteriye_ekleme_metni_bos_yazar()
    {
        var ctx = await SetupAsync(_scrubbing);
        var (canonical, alias) = await SeedMergedCustomerAsync(_scrubbing, ctx.LicenseId, purged: true);
        var item = OrderSyncV2Tests.NewItem(null) with { CustomerId = alias };
        var other = OrderSyncV2Tests.NewItem(null);

        await BackfillAsync(ctx, Guid.NewGuid(), orders: [item, other]);

        var saved = await OrderAsync(_scrubbing, item.Id);
        saved!.CustomerId.Should().Be(canonical);
        saved.DisplayName.Should().BeNull();
        saved.MessageText.Should().BeEmpty();
        saved.TextScrubbedAt.Should().NotBeNull();
        saved.Username.Should().Be(item.Username);
        saved.Code.Should().Be("A1");
        saved.Price.Should().Be(100m);
        (await OrderAsync(_scrubbing, other.Id))!.MessageText.Should().Be(other.MessageText);
    }

    /// <summary>§4.2 ekleme dalı ile aynı (Y1): başka kopyanın CANLI yayınına ekleme satır başına reddedilir; satır
    /// yazılmaz, paketin öbür satırları uygulanır.</summary>
    [Fact]
    public async Task Baska_kopyanin_canli_oturumuna_ekleme_reddedilir()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var othersLive = await SeedSessionAsync(_factory, ctx.LicenseId, owner: Guid.NewGuid(), endedAt: null);
        var rejected = OrderSyncV2Tests.NewItem(othersLive);
        var accepted = OrderSyncV2Tests.NewItem(null);

        var result = await BackfillAsync(ctx, replicaId, orders: [rejected, accepted]);

        result.Orders.Single(o => o.Id == rejected.Id).Should()
            .BeEquivalentTo(new { Status = "rejected", Reason = "session-owned-elsewhere", Version = (int?)null });
        (await OrderAsync(_factory, rejected.Id)).Should().BeNull();
        result.Orders.Single(o => o.Id == accepted.Id).Status.Should().Be("inserted");
    }

    // ── Kargo dosyası bağları ─────────────────────────────────────────────────

    /// <summary>
    /// §4.3 dosya bağları: <c>shipments[].labelIds</c> siparişe yalnız <c>ShipmentId</c> ve <c>ShipmentSetAt</c> boşsa
    /// yazılır; başka dosyaya bağlı ya da bağı komutla değişmiş sipariş değişmez, sunucuda olmayan etiket atlanır.
    /// Dosyanın boş kaynağı çağıranla dolar, dolu kaynak değişmez. Mod Off'ta tutar türetilmez.
    /// </summary>
    [Fact]
    public async Task Dosya_baglari_yalniz_bos_ve_damgasiz_siparise_yazilir()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var other = Guid.NewGuid();
        var free = OrderSyncV2Tests.NewItem(null);
        var linked = OrderSyncV2Tests.NewItem(null);
        var commanded = OrderSyncV2Tests.NewItem(null);
        await PushV1Async(ctx, free, linked, commanded);
        var elsewhere = Guid.NewGuid();
        await SeedOrderAsync(_factory, linked.Id, o => o.ShipmentId = elsewhere);
        await SeedOrderAsync(_factory, commanded.Id, o => o.ShipmentSetAt = DateTimeOffset.UtcNow);
        var target = await SeedShipmentAsync(_factory, ctx.LicenseId, origin: null, amount: 40m);
        var foreignOrigin = await SeedShipmentAsync(_factory, ctx.LicenseId, origin: other, amount: 0m);
        var freeBefore = (await OrderAsync(_factory, free.Id))!.SyncVersion;
        var unknownLabel = Guid.NewGuid();

        await BackfillAsync(ctx, replicaId, shipments:
        [
            new(target, [free.Id, linked.Id, commanded.Id, unknownLabel]),
            new(foreignOrigin, []),
        ]);

        var f = await OrderAsync(_factory, free.Id);
        f!.ShipmentId.Should().Be(target);
        f.SyncVersion.Should().Be(freeBefore + 1);
        (await OrderAsync(_factory, linked.Id))!.ShipmentId.Should().Be(elsewhere);
        (await OrderAsync(_factory, commanded.Id))!.ShipmentId.Should().BeNull();
        (await OrderAsync(_factory, unknownLabel)).Should().BeNull();
        var t = await ShipmentAsync(_factory, target);
        t!.OriginReplicaId.Should().Be(replicaId);
        t.CumulativeAmount.Should().Be(40m, "mod Off'ta tutar türetilmez");
        (await ShipmentAsync(_factory, foreignOrigin))!.OriginReplicaId.Should().Be(other);
    }

    /// <summary>K7 (§4.2, §4.3): mod On'da geri doldurmanın yazdığı bağ türetilmiş dosyanın tutarını aynı yazımda yeniden
    /// türetir — bağlanan mevcut sipariş ve bağıyla eklenen sipariş, iptal edilmiş olan sayılmaz.</summary>
    [Fact]
    public async Task Mod_Onda_yazilan_bag_turetilmis_dosyayi_turetir()
    {
        var ctx = await SetupAsync(_factory, SalesSyncMode.On);
        var shipmentId = await SeedShipmentAsync(_factory, ctx.LicenseId, origin: null, amount: 0m, derived: true);
        var existing = OrderSyncV2Tests.NewItem(null) with { Price = 100m };
        var cancelled = OrderSyncV2Tests.NewItem(null) with { Price = 70m, CancelledAt = StartedAt.AddHours(1) };
        await PushV1Async(ctx, existing, cancelled);
        var inserted = OrderSyncV2Tests.NewItem(null) with { Price = 30m, ShipmentId = shipmentId };

        await BackfillAsync(ctx, Guid.NewGuid(), orders: [inserted],
            shipments: [new(shipmentId, [existing.Id, cancelled.Id])]);

        var s = await ShipmentAsync(_factory, shipmentId);
        s!.CumulativeAmount.Should().Be(130m);
    }

    // ── Birim ayrışması (InMemory yolu; gerçek SQL Server sözleşmesi ilişkisel testte) ─────────

    /// <summary>Mevcut satırda damgasız birim farkı yazılmaz, <c>diverged</c> döner; özgün kopyada mod Off'ta yerel
    /// kazanır ve sayı kopya satırına eklenir.</summary>
    [Fact]
    public async Task Ayrisma_yazilmaz_doner_ve_sayilir()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        await SeedReplicaAsync(_factory, ctx.LicenseId, replicaId, previousReplicaId: null);
        var item = OrderSyncV2Tests.NewItem(null);
        await PushV1Async(ctx, item);

        var result = await BackfillAsync(ctx, replicaId,
            orders: [item with { CancelledAt = StartedAt.AddHours(1), CancelReason = "vazgeçti" }]);

        result.Diverged.Should().ContainSingle().Which.Should()
            .BeEquivalentTo(new { Id = item.Id, Units = new[] { "cancel" }, LocalWins = true });
        (await OrderAsync(_factory, item.Id))!.CancelledAt.Should().BeNull("ayrışma yazılmaz");
        var replica = await ReplicaAsync(_factory, ctx.LicenseId, replicaId);
        replica!.BackfillDivergedLocal.Should().Be(1);
        replica.BackfillDivergedServer.Should().Be(0);

        // Kopya satırı bu istekle doğduysa (hello'suz ilk istek) geri yüklenmediği kanıtlanamaz: sunucu kazanır.
        var unknown = Guid.NewGuid();
        var fromUnknown = await BackfillAsync(ctx, unknown,
            orders: [item with { CancelledAt = StartedAt.AddHours(1), CancelReason = "vazgeçti" }]);
        fromUnknown.Diverged.Single().LocalWins.Should().BeFalse();
        (await ReplicaAsync(_factory, ctx.LicenseId, unknown))!.BackfillDivergedServer.Should().Be(1);
    }

    // ── Doğrulama ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Parti_siniri_her_listede_200()
    {
        var ctx = await SetupAsync(_factory);
        var replicaId = Guid.NewGuid();
        var sessions = Enumerable.Range(0, 201).Select(_ => NewSession(EndedAt)).ToArray();
        var orders = Enumerable.Range(0, 201).Select(_ => OrderSyncV2Tests.NewItem(null)).ToArray();
        var shipments = Enumerable.Range(0, 201).Select(_ => new LinkItem(Guid.NewGuid(), [])).ToArray();

        foreach (var body in new[]
                 {
                     Body(replicaId, sessions, [], []),
                     Body(replicaId, [], orders, []),
                     Body(replicaId, [], [], shipments),
                 })
        {
            var resp = await ctx.Client.PostAsJsonAsync(Url(ctx, "sales/backfill"), body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await resp.Content.ReadAsStringAsync()).Should().Contain("batch-too-large");
        }

        (await OrderAsync(_factory, orders[0].Id)).Should().BeNull();
        (await SessionAsync(_factory, sessions[0].Id)).Should().BeNull();
    }

    /// <summary>orders/sync'in oturum denetimi (F01): hiçbir lisansta olmayan oturuma bağlı sipariş paketin tamamını
    /// 400 <c>unknown-session</c> ile düşürür; aynı istekteki oturum bilinen sayılır.</summary>
    [Fact]
    public async Task Hicbir_lisansta_olmayan_oturum_400_hicbir_sey_yazilmaz()
    {
        var ctx = await SetupAsync(_factory);
        var session = NewSession(EndedAt);
        var good = OrderSyncV2Tests.NewItem(session.Id);
        var bad = OrderSyncV2Tests.NewItem(Guid.NewGuid());

        var resp = await ctx.Client.PostAsJsonAsync(Url(ctx, "sales/backfill"),
            Body(Guid.NewGuid(), [session], [good, bad], []));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("unknown-session");
        (await OrderAsync(_factory, good.Id)).Should().BeNull();
        (await OrderAsync(_factory, bad.Id)).Should().BeNull();
        (await SessionAsync(_factory, session.Id)).Should().BeNull();
    }

    /// <summary>
    /// Lisans değiştiren PC (§3.1, §4.3): yerel geçmişte eski lisansın oturum ve siparişleri durur ve Ws ilk açılışta
    /// hepsini yeni lisansa gönderir. Başka lisansta var olan oturum ya da sipariş Id'si satır başına
    /// <c>rejected: foreign-license</c> alır; böyle bir oturuma bağlı yeni siparişler de (oturum istekte olsun ya da
    /// olmasın). Parti 200 döner, öbür satırlar yazılır, öbür lisansın satırları değişmez.
    /// </summary>
    [Fact]
    public async Task Baska_lisansta_var_olan_Idler_satir_basina_reddedilir()
    {
        var ctx = await SetupAsync(_factory);
        var previous = await SetupAsync(_factory);
        var oldSession = await SeedSessionAsync(_factory, previous.LicenseId, owner: null, endedAt: EndedAt);
        var oldOrder = OrderSyncV2Tests.NewItem(oldSession);
        await PushV1Async(previous, oldOrder);
        var replicaId = Guid.NewGuid();
        var oldSessionItem = NewSession(EndedAt, release: true) with { Id = oldSession };
        var newSession = NewSession(EndedAt);
        var newOrderInOldSession = OrderSyncV2Tests.NewItem(oldSession);
        var oldOrderInNewSession = oldOrder with { SessionId = newSession.Id };
        var good = OrderSyncV2Tests.NewItem(newSession.Id);

        var result = await BackfillAsync(ctx, replicaId,
            sessions: [oldSessionItem, newSession],
            orders: [newOrderInOldSession, oldOrderInNewSession, good]);

        result.Sessions.Single(s => s.Id == oldSession).Should()
            .BeEquivalentTo(new { Reason = "foreign-license", OwnerReplicaId = (Guid?)null });
        result.Sessions.Single(s => s.Id == newSession.Id).Should()
            .BeEquivalentTo(new { Reason = (string?)null, OwnerReplicaId = (Guid?)replicaId });
        foreach (var id in new[] { newOrderInOldSession.Id, oldOrder.Id })
            result.Orders.Single(o => o.Id == id).Should()
                .BeEquivalentTo(new { Status = "rejected", Reason = "foreign-license", Version = (int?)null });
        result.Orders.Single(o => o.Id == good.Id).Status.Should().Be("inserted");
        (await OrderAsync(_factory, newOrderInOldSession.Id)).Should().BeNull();

        var stillOld = await SessionAsync(_factory, oldSession);
        stillOld!.LicenseId.Should().Be(previous.LicenseId);
        stillOld.OwnerReplicaId.Should().BeNull();
        stillOld.ReleasedAt.Should().BeNull("başka lisansın oturumu devredilmez");
        var stillOldOrder = await OrderAsync(_factory, oldOrder.Id);
        stillOldOrder!.LicenseId.Should().Be(previous.LicenseId);
        stillOldOrder.SessionId.Should().Be(oldSession);
        stillOldOrder.OriginReplicaId.Should().BeNull();

        // Sonraki sipariş partisi oturumları taşımaz: eski lisansın oturumu "bilinmeyen" sayılıp partiyi düşürmez.
        var later = await BackfillAsync(ctx, replicaId, orders: [oldOrder, OrderSyncV2Tests.NewItem(oldSession)]);
        later.Orders.Should().OnlyContain(o => o.Status == "rejected" && o.Reason == "foreign-license");
    }

    /// <summary>Başka yayıncının lisansı 404 (sahiplik zarftan önce); geri doldurma çağıranı bilmek zorunda olduğu için
    /// protokolsüz istek 400 <c>invalid-envelope</c>.</summary>
    [Fact]
    public async Task Yabanci_lisans_404_protokolsuz_istek_400()
    {
        var ctx = await SetupAsync(_factory);
        var stranger = await SetupAsync(_factory);
        var item = OrderSyncV2Tests.NewItem(null);

        (await ctx.Client.PostAsJsonAsync(Url(stranger, "sales/backfill"), Body(Guid.NewGuid(), [], [item], [])))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        var legacy = await ctx.Client.PostAsJsonAsync(Url(ctx, "sales/backfill"), new { orders = new[] { item } });
        legacy.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await legacy.Content.ReadAsStringAsync()).Should().Contain("invalid-envelope");
        (await OrderAsync(_factory, item.Id)).Should().BeNull();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    internal sealed record SessionItem(
        Guid Id,
        string? Title,
        DateTimeOffset StartedAt,
        DateTimeOffset? EndedAt,
        string Platforms,
        string? Notes,
        bool Release = false,
        string? MachineName = null,
        int? UnsentLabelCount = null);

    internal sealed record LinkItem(Guid Id, Guid[] LabelIds);

    internal sealed record SessionResult(
        Guid Id, Guid? OwnerReplicaId, DateTimeOffset? EndedAt, DateTimeOffset? ReleasedAt, string? Reason = null);

    internal sealed record OrderResult(Guid Id, int? Version, string Status, string? Reason);

    internal sealed record DivergedResult(Guid Id, List<string> Units, bool LocalWins);

    internal sealed record BackfillResult(
        List<SessionResult> Sessions, List<OrderResult> Orders, List<DivergedResult> Diverged);

    private sealed record Ctx(HttpClient Client, Guid CustomerId, Guid LicenseId);

    internal static SessionItem NewSession(DateTimeOffset? endedAt, bool release = false) =>
        new(Guid.NewGuid(), "Akşam yayını", StartedAt, endedAt, "youtube", null, release, "PC-A");

    internal static object Body(
        Guid replicaId, SessionItem[] sessions, Item[] orders, LinkItem[] shipments,
        bool catalogAware = false, int seenMode = 0) =>
        new { sessions, orders, shipments, catalogAware, replicaId, protocol = 2, seenMode };

    private static string Url(Ctx ctx, string path) => $"/api/v1/licenses/{ctx.LicenseId}/{path}";

    private static async Task<BackfillResult> BackfillAsync(
        Ctx ctx, Guid replicaId,
        SessionItem[]? sessions = null, Item[]? orders = null, LinkItem[]? shipments = null,
        bool catalogAware = false)
    {
        var resp = await ctx.Client.PostAsJsonAsync(Url(ctx, "sales/backfill"),
            Body(replicaId, sessions ?? [], orders ?? [], shipments ?? [], catalogAware));
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<BackfillResult>())!;
    }

    private static async Task PushV1Async(Ctx ctx, params Item[] items)
    {
        var resp = await ctx.Client.PostAsJsonAsync(Url(ctx, "orders/sync"), new { orders = items });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
    }

    private IEnumerable<RecordingNotificationSender.Notification> CustomerPushes(Ctx ctx) =>
        _factory.Push.Sent.Where(n => n.CustomerId == ctx.CustomerId);

    private IEnumerable<RecordingNotificationSender.ShopperNotification> ShopperPushes(Guid shopperId) =>
        _factory.Push.SentToShoppers.Where(n => n.ShopperIds.Contains(shopperId));

    private static async Task<Ctx> SetupAsync(ApiFactory factory, SalesSyncMode mode = SalesSyncMode.Off)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(factory);
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-BF-" + Guid.NewGuid().ToString("N"),
            CustomerId = customerId,
            SkuCode = "STD",
            ActivationSlots = 2,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
            SalesSyncMode = mode,
            SalesSyncModeChangedAt = mode == SalesSyncMode.On ? now.AddDays(-1) : null,
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return new Ctx(client, customerId, license.Id);
    }

    /// <summary>Telefonlu müşteri + WhatsApp sohbeti + <c>OrderReceived</c> kuralı (OrderSyncLabelTests deseni) ve aynı
    /// müşteriye bağlı, sipariş bildirimi açık bir shopper.</summary>
    private async Task<(Guid ConversationId, Guid WpfCustomerId, Guid ShopperId)> SeedNotificationTargetsAsync(
        Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var wpfCustomerId = Guid.NewGuid();
        var phone = TestPhone.NewE164();
        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = wpfCustomerId, LicenseId = licenseId, Platform = "youtube", Username = $"u-{Guid.NewGuid():N}",
            Phone = phone, UpdatedAt = now,
        });
        var conversation = new WaConversation
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, CustomerPhone = phone[1..], PhoneNumberId = "PNID_1",
            Status = "open", CreatedAt = now,
        };
        db.WaConversations.Add(conversation);
        var label = new WaLabel { Id = Guid.NewGuid(), LicenseId = licenseId, Name = "Yeni sipariş", Color = "#eab308", CreatedAt = now };
        db.WaLabels.Add(label);
        db.WaLabelRules.Add(new WaLabelRule
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, EventKey = WaLabelEvent.OrderReceived, WaLabelId = label.Id,
            CreatedAt = now,
        });
        var shopperId = Guid.NewGuid();
        db.Shoppers.Add(new OrderDeck.LicenseServer.Domain.Shopper
        {
            Id = shopperId,
            FullName = "Alıcı",
            Phone = TestPhone.NewE164(),
            PasswordHash = $"hash-{Guid.NewGuid():N}",
            Address = "Adres",
            CreatedAt = now,
            UpdatedAt = now,
            NotificationsEnabledOrders = true,
        });
        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = licenseId, Platform = "youtube",
            Username = $"u-{Guid.NewGuid():N}", WpfCustomerId = wpfCustomerId, JoinedAt = now,
        });
        await db.SaveChangesAsync();
        return (conversation.Id, wpfCustomerId, shopperId);
    }

    private async Task<int> LabelCountAsync(Guid conversationId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId);
    }

    /// <summary>Asıl kayıt ve ona birleştirilmiş kopya (hex Id'ler); <paramref name="purged"/> asıl kaydı KVKK ile
    /// silinmiş yapar.</summary>
    private static async Task<(string Canonical, string Alias)> SeedMergedCustomerAsync(
        ApiFactory factory, Guid licenseId, bool purged = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var canonical = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "youtube", Username = $"asil-{Guid.NewGuid():N}",
            UpdatedAt = now, PurgedAt = purged ? now.AddMinutes(-5) : null,
        };
        var alias = new WpfCustomerProjection
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Platform = "youtube", Username = $"kopya-{Guid.NewGuid():N}",
            UpdatedAt = now, MergedIntoId = canonical.Id,
        };
        db.WpfCustomerProjections.AddRange(canonical, alias);
        await db.SaveChangesAsync();
        return (canonical.Id.ToString("N"), alias.Id.ToString("N"));
    }

    private static async Task<Guid> SeedProductAsync(ApiFactory factory, Guid licenseId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Code = "A1", Name = "Tişört", DefaultPrice = 100m,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static async Task<Guid> SeedSessionAsync(
        ApiFactory factory, Guid licenseId, Guid? owner, DateTimeOffset? endedAt, DateTimeOffset? releasedAt = null)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var session = new StreamSession
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            Title = "Akşam yayını",
            StartedAt = StartedAt,
            EndedAt = endedAt,
            Platforms = "youtube",
            UpdatedAt = StartedAt,
            OwnerReplicaId = owner,
            OwnerMachineName = owner is null ? null : "PC-B",
            ReleasedAt = releasedAt,
        };
        db.StreamSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    private static async Task<Guid> SeedShipmentAsync(
        ApiFactory factory, Guid licenseId, Guid? origin, decimal amount, bool derived = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var shipment = new Shipment
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            CustomerId = Guid.NewGuid().ToString("N"),
            Status = ShipmentStatus.Pending,
            CumulativeAmount = amount,
            CreatedAt = now,
            UpdatedAt = now,
            OriginReplicaId = origin,
            AmountDerivedAt = derived ? now : null,
        };
        db.Shipments.Add(shipment);
        await db.SaveChangesAsync();
        return shipment.Id;
    }

    private static async Task SeedReplicaAsync(ApiFactory factory, Guid licenseId, Guid replicaId, Guid? previousReplicaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var seen = DateTimeOffset.UtcNow.AddHours(-1);
        db.WpfReplicas.Add(new WpfReplica
        {
            LicenseId = licenseId, ReplicaId = replicaId, Protocol = 2, FirstSeenAt = seen, LastSeenAt = seen,
            PreviousReplicaId = previousReplicaId,
        });
        await db.SaveChangesAsync();
    }

    private static async Task SeedOrderAsync(ApiFactory factory, Guid orderId, Action<Order> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        change(await db.Orders.SingleAsync(o => o.Id == orderId));
        await db.SaveChangesAsync();
    }

    private static async Task<Order?> OrderAsync(ApiFactory factory, Guid orderId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Orders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == orderId);
    }

    private static async Task<StreamSession?> SessionAsync(ApiFactory factory, Guid sessionId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StreamSessions.AsNoTracking().SingleOrDefaultAsync(s => s.Id == sessionId);
    }

    private static async Task<Shipment?> ShipmentAsync(ApiFactory factory, Guid shipmentId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.Shipments.AsNoTracking().SingleOrDefaultAsync(s => s.Id == shipmentId);
    }

    private static async Task<WpfReplica?> ReplicaAsync(ApiFactory factory, Guid licenseId, Guid replicaId)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.WpfReplicas.AsNoTracking()
            .SingleOrDefaultAsync(r => r.LicenseId == licenseId && r.ReplicaId == replicaId);
    }
}
