using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST sales/commands</c> — spec 2026-10-13 §4.6, §7.2, §10.2 (InMemory): zarf ve kapılar (mod Off, istemci
/// sürümü), görünürlük, beklenen sürüm, yeniden oynatma, günlük ve <c>cancel</c>/<c>uncancel</c>. Eşzamanlılık (jeton,
/// eşzamanlı aynı komut, push ile yarış) ve uygulama kilidi gerçek SQL Server'da
/// (<see cref="SalesCommandsRelationalTests"/>).
/// </summary>
public sealed class SalesCommandsTests : IClassFixture<SalesSyncConfiguredApiFactory>, IClassFixture<ApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;
    private readonly ApiFactory _noMinVersion;

    public SalesCommandsTests(SalesSyncConfiguredApiFactory factory, ApiFactory noMinVersion)
    {
        _factory = factory;
        _noMinVersion = noMinVersion;
    }

    // ── Kapılar ───────────────────────────────────────────────────────────────

    /// <summary>Mod Off (varsayılan; v0.9.10 davranışı): 409 <c>mode-off</c>, günlük satırı yok, sipariş değişmez.</summary>
    [Fact]
    public async Task Mod_Off_409_mode_off_gunluk_ve_siparis_degismez()
    {
        var kit = await Kit.CreateAsync(_factory, SalesSyncMode.Off);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));

        var resp = await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", order));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("mode-off");
        (await kit.LogsAsync()).Should().BeEmpty();
        var saved = await kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().BeNull();
        saved.CancelSetAt.Should().BeNull();
        saved.SyncVersion.Should().Be(1);
    }

    /// <summary>Alt sürüm tanımsız (Ws yayımlanmadı): kapı kapalı-hata, 409 <c>client-update-required</c>.</summary>
    [Fact]
    public async Task Alt_surum_tanimsizken_409_client_update_required()
    {
        var kit = await Kit.CreateAsync(_noMinVersion);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));

        var resp = await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", order));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("client-update-required");
        (await kit.LogsAsync()).Should().BeEmpty();
        (await kit.OrderAsync(order.Id)).CancelledAt.Should().BeNull();
    }

    /// <summary>İstemci sürümü yok, ayrıştırılamıyor ya da alt sınırdan eski: 409 <c>client-update-required</c>.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("OrderDeck-WPF/0.9.10")]
    [InlineData("OrderDeck-WPF/0.9.11-rc1")]
    [InlineData("Mozilla/5.0")]
    public async Task UA_yok_ya_da_eski_surum_409_client_update_required(string? userAgent)
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));

        var resp = await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", order), userAgent);

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("client-update-required");
        (await kit.LogsAsync()).Should().BeEmpty();
        (await kit.OrderAsync(order.Id)).CancelledAt.Should().BeNull();
    }

    /// <summary>Komutlar yalnız v2 konuşur: zarfsız ya da yarım zarflı istek 400 <c>invalid-envelope</c>.</summary>
    [Fact]
    public async Task Zarfsiz_istek_400_invalid_envelope()
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var payload = new { items = new[] { order.Id }, reason = "customer" };
        var expected = new[] { new { orderId = order.Id, version = 1 } };

        var bodies = new object[]
        {
            new { commandId = Guid.NewGuid(), kind = "cancel", payload, expected },
            new { commandId = Guid.NewGuid(), replicaId = kit.ReplicaId, kind = "cancel", payload, expected },
            new { commandId = Guid.NewGuid(), replicaId = kit.ReplicaId, protocol = 1, seenMode = 1, kind = "cancel", payload, expected },
        };
        foreach (var body in bodies)
        {
            var resp = await kit.SendAsync(body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-envelope");
        }
        (await kit.LogsAsync()).Should().BeEmpty();
        (await kit.OrderAsync(order.Id)).CancelledAt.Should().BeNull();
    }

    [Fact]
    public async Task Baskasinin_lisansina_404()
    {
        var foreign = await Kit.CreateAsync(_factory);
        var order = await foreign.SeedOrderAsync(await foreign.SeedSessionAsync(Kit.Phase.Released));
        var kit = await Kit.CreateAsync(_factory);

        var resp = await kit.SendToAsync(foreign.Url, kit.Cancel(Guid.NewGuid(), "customer", order));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await foreign.OrderAsync(order.Id)).CancelledAt.Should().BeNull();
    }

    /// <summary>Kayıtlı işleyicisi olmayan tür (henüz gelmemiş türler dahil) 400 <c>unknown-kind</c>.</summary>
    [Fact]
    public async Task Bilinmeyen_tur_400_unknown_kind()
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));

        var resp = await kit.SendAsync(kit.Body(
            Guid.NewGuid(), "yok-boyle-bir-tur", new { items = new[] { order.Id } }, [(order.Id, 1)]));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("unknown-kind");
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>Yük ve beklenen sürüm doğrulaması: boş öğe listesi, eksik neden, öğeleri tam kapsamayan ya da bir
    /// siparişi iki kez taşıyan <c>expected</c>, nesne olmayan yük — 400, hiçbir şey yazılmaz.</summary>
    [Fact]
    public async Task Gecersiz_yuk_ve_beklenen_surum_400()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var a = await kit.SeedOrderAsync(session);
        var b = await kit.SeedOrderAsync(session);

        var cases = new (object Body, string Code)[]
        {
            (kit.Body(Guid.NewGuid(), "cancel", new { items = Array.Empty<Guid>(), reason = "customer" }, []), "invalid-payload"),
            (kit.Body(Guid.NewGuid(), "cancel", new { items = new[] { a.Id } }, [(a.Id, 1)]), "invalid-payload"),
            (kit.Body(Guid.NewGuid(), "cancel", new { items = new[] { a.Id, b.Id }, reason = "customer" }, [(a.Id, 1)]), "invalid-payload"),
            (kit.Body(Guid.NewGuid(), "uncancel", new { items = new[] { a.Id } }, [(a.Id, 1), (b.Id, 1)]), "invalid-payload"),
            (kit.Body(Guid.NewGuid(), "uncancel", new { items = new[] { a.Id } }, [(a.Id, 1), (a.Id, 1)]), "invalid-expected"),
            (kit.Body(Guid.NewGuid(), "cancel", new[] { a.Id }, [(a.Id, 1)]), "invalid-payload"),
            (kit.Body(Guid.Empty, "cancel", new { items = new[] { a.Id }, reason = "customer" }, [(a.Id, 1)]), "invalid-command"),
        };
        foreach (var (body, code) in cases)
        {
            var resp = await kit.SendAsync(body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, code);
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(code);
        }
        (await kit.LogsAsync()).Should().BeEmpty();
        (await kit.OrderAsync(a.Id)).CancelledAt.Should().BeNull();
    }

    /// <summary>Bu lisansta olmayan sipariş (başka lisansınki dahil) 400 <c>unknown-order</c>.</summary>
    [Fact]
    public async Task Lisansta_olmayan_siparis_400_unknown_order()
    {
        var kit = await Kit.CreateAsync(_factory);
        var other = await kit.SecondLicenseAsync();
        var foreignOrder = await other.SeedOrderAsync(await other.SeedSessionAsync(Kit.Phase.Released));
        var missing = Guid.NewGuid();

        var resp = await kit.SendAsync(kit.Body(
            Guid.NewGuid(), "cancel", new { items = new[] { foreignOrder.Id, missing }, reason = "customer" },
            [(foreignOrder.Id, 1), (missing, 1)]));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("unknown-order");
        problem.GetProperty("orderIds").EnumerateArray().Select(e => e.GetGuid())
            .Should().BeEquivalentTo([foreignOrder.Id, missing]);
        (await other.OrderAsync(foreignOrder.Id)).CancelledAt.Should().BeNull();
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    // ── Görünürlük ve beklenen sürüm ─────────────────────────────────────────

    /// <summary>
    /// §4.6 adım 3, Y1, Y8: canlı, devrediliyor ya da yayınsız siparişin iptal birimine dokunulamaz — 409
    /// <c>session-live</c>, sahip bilgisayarın adı ve son kalp atışıyla. Aynı komuttaki devredilmiş siparişe de hiçbir
    /// şey yazılmaz.
    /// </summary>
    [Theory]
    [InlineData(Kit.Phase.Live)]
    [InlineData(Kit.Phase.HandingOver)]
    [InlineData(null)]
    public async Task Devredilmemis_ya_da_yayinsiz_siparis_409_session_live(Kit.Phase? phase)
    {
        var kit = await Kit.CreateAsync(_factory);
        var ownerLastSeenAt = DateTimeOffset.UtcNow.AddMinutes(-3);
        await kit.SeedReplicaAsync(kit.OwnerReplicaId, ownerLastSeenAt);
        var released = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        Guid? sessionId = phase is { } p ? await kit.SeedSessionAsync(p) : null;
        var hidden = await kit.SeedOrderAsync(sessionId);

        var resp = await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", released, hidden));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("session-live");
        if (phase is null)
        {
            problem.GetProperty("machine").ValueKind.Should().Be(JsonValueKind.Null);
            problem.GetProperty("ownerLastSeenAt").ValueKind.Should().Be(JsonValueKind.Null);
        }
        else
        {
            problem.GetProperty("machine").GetString().Should().Be("PC-A");
            problem.GetProperty("ownerLastSeenAt").GetDateTimeOffset().Should().Be(ownerLastSeenAt);
            problem.GetProperty("sessionId").GetGuid().Should().Be(sessionId!.Value);
        }
        (await kit.OrderAsync(released.Id)).CancelledAt.Should().BeNull("ret hiçbir şey yazmaz");
        (await kit.OrderAsync(hidden.Id)).CancelledAt.Should().BeNull();
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>§4.6 adım 4: istemcinin gördüğü sürüm güncel değilse 409 <c>stale</c>, güncel satırlar (akış biçiminde)
    /// ve hiçbir yazım.</summary>
    [Fact]
    public async Task Beklenen_surum_uyusmazsa_409_stale_guncel_satirlar_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var fresh = await kit.SeedOrderAsync(session);
        var moved = await kit.SeedOrderAsync(session, o => { o.SyncVersion = 4; o.Price = 90m; });

        var resp = await kit.SendAsync(kit.Body(
            Guid.NewGuid(), "cancel", new { items = new[] { fresh.Id, moved.Id }, reason = "customer" },
            [(fresh.Id, 1), (moved.Id, 3)]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("stale");
        var current = problem.GetProperty("current").Deserialize<List<SalesOrderItem>>(
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        current.Select(o => (o.Id, o.Version, o.Price))
            .Should().BeEquivalentTo([(fresh.Id, 1, 100m), (moved.Id, 4, 90m)]);
        (await kit.OrderAsync(fresh.Id)).CancelledAt.Should().BeNull("hiçbir şey yazılmaz");
        (await kit.OrderAsync(moved.Id)).SyncVersion.Should().Be(4);
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    // ── cancel ve uncancel ───────────────────────────────────────────────────

    /// <summary>
    /// <c>cancel</c> (§4.6, §7.2 Y5): <c>CancelledAt</c> sunucu saati, neden ve <c>CancelSetAt</c> basılır, sürüm bir
    /// artar; kargo bağı kalır. Açık türetilmiş dosyanın tutarı siparişin fiyatı kadar düşer; Shipped dosya donmuş,
    /// kabul edilmiş (türetilmemiş) dosya değişmez. Defter ürünü bilinen siparişe telafi yazar, ürünsüz siparişe hiçbir
    /// şey. Zaten iptal edilmiş sipariş atlanır, sürümü değişmez. Yanıt akış biçiminde güncel satırları ve sonucu taşır.
    /// </summary>
    [Fact]
    public async Task Cancel_sunucu_saatiyle_damgalar_bagi_korur_dosyayi_turetir_defteri_yazar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var open = await kit.SeedShipmentAsync(customer, ShipmentStatus.Pending, 150m, derived: true, version: 3);
        var shipped = await kit.SeedShipmentAsync(customer, ShipmentStatus.Shipped, 80m, derived: true, version: 2);
        var accepted = await kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 70m, derived: false, version: 1);
        var inOpen = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = open.Id; });
        var stays = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = open.Id; o.Price = 50m; });
        var inShipped = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = shipped.Id; o.Price = 80m; });
        var inAccepted = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = accepted.Id; o.Price = 70m; });
        var productId = await kit.SeedProductAsync();
        var withProduct = await kit.SeedOrderAsync(session, o => o.ProductId = productId);
        await kit.SeedSaleMovementAsync(withProduct);
        var cancelledBefore = StartedAt.AddHours(5);
        var already = await kit.SeedOrderAsync(session, o =>
        {
            o.CancelledAt = cancelledBefore;
            o.CancelReason = "customer";
            o.SyncVersion = 4;
        });
        var reason = $"custom:{Guid.NewGuid():N}";
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.Cancel(
            Guid.NewGuid(), reason, inOpen, inShipped, inAccepted, withProduct, already));

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("applied");

        foreach (var original in new[] { inOpen, inShipped, inAccepted, withProduct })
        {
            var saved = await kit.OrderAsync(original.Id);
            saved.CancelledAt.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(after, "sunucu saati");
            saved.CancelSetAt.Should().Be(saved.CancelledAt, "birim damgası aynı sunucu anı");
            saved.CancelReason.Should().Be(reason);
            saved.SyncVersion.Should().Be(original.SyncVersion + 1);
            saved.UpdatedAt.Should().Be(saved.CancelledAt!.Value);
            saved.ShipmentId.Should().Be(original.ShipmentId, "iptal bağı çözmez (Y5)");
        }
        var skipped = await kit.OrderAsync(already.Id);
        skipped.CancelledAt.Should().Be(cancelledBefore, "zaten iptal: atlanır");
        skipped.CancelReason.Should().Be("customer");
        skipped.CancelSetAt.Should().BeNull();
        skipped.SyncVersion.Should().Be(4, "atlanan siparişin sürümü ilerlemez");
        (await kit.OrderAsync(stays.Id)).SyncVersion.Should().Be(1);

        var openAfter = await kit.ShipmentAsync(open.Id);
        openAfter.CumulativeAmount.Should().Be(50m, "açık türetilmiş dosyanın tutarı iptal edilen fiyat kadar düşer");
        openAfter.Version.Should().Be(4);
        (await kit.ShipmentAsync(shipped.Id)).Should().BeEquivalentTo(new { CumulativeAmount = 80m, Version = 2 },
            "Shipped dosyanın tutarı donmuştur");
        (await kit.ShipmentAsync(accepted.Id)).Should().BeEquivalentTo(new { CumulativeAmount = 70m, Version = 1 },
            "kabul edilmiş dosya türetilmez");

        var movements = await kit.MovementsAsync(withProduct.Id);
        movements.Select(m => (m.Quantity, m.Reason))
            .Should().BeEquivalentTo([(-1, StockMovementReason.Sale), (1, StockMovementReason.CancelReturn)]);
        (await kit.MovementsAsync(inOpen.Id)).Should().BeEmpty("ürünü bilinmeyen siparişin defterde satırı yok");

        body.Orders.Select(o => o.Id).Should().BeEquivalentTo(
            [inOpen.Id, inShipped.Id, inAccepted.Id, withProduct.Id, already.Id]);
        body.Orders.Single(o => o.Id == inOpen.Id).Should().BeEquivalentTo(new
        {
            Version = 2, ShipmentId = (Guid?)open.Id, CancelReason = reason,
        });
        body.Shipments.Single(s => s.Id == open.Id).Should().BeEquivalentTo(new { CumulativeAmount = 50m, Version = 4 });
        body.Shipments.Select(s => s.Id).Should().BeEquivalentTo([open.Id, shipped.Id, accepted.Id]);
        body.Sessions.Should().ContainSingle().Which.Id.Should().Be(session);

        var result = body.Result;
        Guids(result, "changed").Should().BeEquivalentTo([inOpen.Id, inShipped.Id, inAccepted.Id, withProduct.Id]);
        Guids(result, "skipped").Should().Equal(already.Id);
        result.GetProperty("orders").EnumerateArray()
            .Select(o => (o.GetProperty("id").GetGuid(), o.GetProperty("version").GetInt32()))
            .Should().BeEquivalentTo([(inOpen.Id, 2), (inShipped.Id, 2), (inAccepted.Id, 2), (withProduct.Id, 2)]);
        result.GetProperty("shipments").EnumerateArray()
            .Select(s => (s.GetProperty("id").GetGuid(), s.GetProperty("version").GetInt32(), s.GetProperty("amount").GetDecimal()))
            .Should().Equal((open.Id, 4, 50m));
    }

    /// <summary>
    /// <c>uncancel</c> (§7.2 Y5): iptal alanları boşalır, <c>CancelSetAt</c> yeni (daha geç, boş değil) bir anla basılır;
    /// kargo bağı ve <c>PaidPrice</c> kalır, açık dosyanın tutarı eski hâline döner, defter satışı yeniden düşer. İptal
    /// edilmemiş sipariş atlanır.
    /// </summary>
    [Fact]
    public async Task Uncancel_tutari_geri_getirir_bag_ve_odenen_fiyat_kalir_damga_yenilenir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var productId = await kit.SeedProductAsync();
        var open = await kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 150m, derived: true, version: 1);
        var paid = await kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.ShipmentId = open.Id;
            o.PaidPrice = 120m;
            o.ProductId = productId;
        });
        await kit.SeedSaleMovementAsync(paid);
        var other = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = open.Id; o.Price = 50m; });

        var cancel = await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", paid));
        cancel.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
        var cancelled = await kit.OrderAsync(paid.Id);
        (await kit.ShipmentAsync(open.Id)).CumulativeAmount.Should().Be(50m, "ön koşul: iptal tutardan düştü");

        var resp = await kit.SendAsync(kit.Uncancel(Guid.NewGuid(), cancelled, other));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("applied");
        var saved = await kit.OrderAsync(paid.Id);
        saved.CancelledAt.Should().BeNull();
        saved.CancelReason.Should().BeNull();
        saved.CancelSetAt.Should().NotBeNull().And.BeAfter(cancelled.CancelSetAt!.Value, "damga yenilenir, silinmez");
        saved.ShipmentId.Should().Be(open.Id, "bağ kalır: etiket aynı dosyaya döner");
        saved.PaidPrice.Should().Be(120m, "ödenmiş bilgisi kalır");
        saved.SyncVersion.Should().Be(cancelled.SyncVersion + 1);
        (await kit.OrderAsync(other.Id)).SyncVersion.Should().Be(1, "iptal edilmemiş sipariş atlanır");

        var shipment = await kit.ShipmentAsync(open.Id);
        shipment.CumulativeAmount.Should().Be(150m, "tutar eski hâline döner");
        (await kit.MovementsAsync(paid.Id)).Select(m => m.Quantity).Should().Equal(-1, 1, -1);

        Guids(body.Result, "changed").Should().Equal(paid.Id);
        Guids(body.Result, "skipped").Should().Equal(other.Id);
        body.Orders.Single(o => o.Id == paid.Id).Should().BeEquivalentTo(new
        {
            CancelledAt = (DateTimeOffset?)null, ShipmentId = (Guid?)open.Id, PaidPrice = (decimal?)120m,
        });
    }

    // ── Yeniden oynatma ──────────────────────────────────────────────────────

    /// <summary>
    /// §4.6 adım 2: aynı <c>CommandId</c> ve aynı içerik yeniden uygulanmaz — aynı durum ve sonuç, satırların GÜNCEL
    /// hâliyle; ikinci yazım ve ikinci günlük satırı yok. İçeriğin yazımı (öğe sırası, yinelenen öğe) özeti değiştirmez.
    /// </summary>
    [Fact]
    public async Task Ayni_komut_yeniden_oynatilir_guncel_satirlar_doner_ikinci_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var a = await kit.SeedOrderAsync(session);
        var b = await kit.SeedOrderAsync(session);
        var commandId = Guid.NewGuid();
        var first = await kit.SendAsync(kit.Cancel(commandId, "customer", a, b));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var firstBody = await Kit.ReadAsync(first);
        // Komuttan sonra satır başka bir yoldan değişti: yeniden oynatma onu güncel hâliyle döner.
        await kit.WithDbAsync(async db =>
        {
            var row = await db.Orders.SingleAsync(o => o.Id == a.Id);
            row.Price = 90m;
            row.SyncVersion++;
        });

        var replay = await kit.SendAsync(kit.Body(
            commandId, "cancel", new { items = new[] { b.Id, a.Id, b.Id }, reason = "customer" },
            [(b.Id, 1), (a.Id, 1)]));

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        var replayBody = await Kit.ReadAsync(replay);
        replayBody.Status.Should().Be(firstBody.Status);
        replayBody.Result.GetRawText().Should().Be(firstBody.Result.GetRawText());
        replayBody.Orders.Single(o => o.Id == a.Id).Should().BeEquivalentTo(new { Price = 90m, Version = 3 });
        (await kit.OrderAsync(a.Id)).SyncVersion.Should().Be(3, "yeniden oynatma yazmaz");
        (await kit.OrderAsync(b.Id)).SyncVersion.Should().Be(2);
        (await kit.LogsAsync()).Should().ContainSingle();
    }

    /// <summary>Aynı <c>CommandId</c>'ye farklı içerik (neden, tür, kopya) istemci hatasıdır: 409
    /// <c>content-conflict</c>, hiçbir şey yazılmaz.</summary>
    [Fact]
    public async Task Ayni_CommandId_farkli_icerik_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var commandId = Guid.NewGuid();
        (await kit.SendAsync(kit.Cancel(commandId, "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await kit.OrderAsync(order.Id);

        var conflicts = new[]
        {
            kit.Cancel(commandId, "duplicate", order),
            kit.Uncancel(commandId, cancelled),
            kit.Body(commandId, "cancel", new { items = new[] { order.Id }, reason = "customer" }, [(order.Id, 1)],
                replicaId: Guid.NewGuid()),
        };
        foreach (var body in conflicts)
        {
            var resp = await kit.SendAsync(body);
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        }

        var saved = await kit.OrderAsync(order.Id);
        saved.CancelReason.Should().Be("customer");
        saved.CancelledAt.Should().NotBeNull();
        saved.SyncVersion.Should().Be(cancelled.SyncVersion);
        (await kit.LogsAsync()).Should().ContainSingle();
    }

    /// <summary>Aynı <c>CommandId</c> başka bir lisansta (aynı yayıncının bile) 409 <c>content-conflict</c>.</summary>
    [Fact]
    public async Task Ayni_CommandId_baska_lisansta_409_content_conflict()
    {
        var kit = await Kit.CreateAsync(_factory);
        var other = await kit.SecondLicenseAsync();
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var otherOrder = await other.SeedOrderAsync(await other.SeedSessionAsync(Kit.Phase.Released));
        var commandId = Guid.NewGuid();
        (await kit.SendAsync(kit.Cancel(commandId, "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);

        var sameBody = await other.SendToAsync(other.Url, kit.Cancel(commandId, "customer", order));
        var ownOrder = await other.SendAsync(other.Cancel(commandId, "customer", otherOrder));

        foreach (var resp in new[] { sameBody, ownOrder })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("content-conflict");
        }
        (await other.OrderAsync(otherOrder.Id)).CancelledAt.Should().BeNull();
        (await other.LogsAsync()).Should().BeEmpty();
    }

    // ── Günlük, yanıt, bildirim, kalp atışı ──────────────────────────────────

    /// <summary>Günlük satırı türü, kopyayı, özeti ve sonucu taşır; sonuç yalnız Id, sürüm ve tutardır — görünen ad,
    /// yorum metni ve (serbest metin) iptal nedeni yazılmaz (§9).</summary>
    [Fact]
    public async Task Gunluk_satiri_tur_kopya_ve_ozet_tasir_kisisel_metin_tasimaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var reason = $"custom:gizli-{Guid.NewGuid():N}";
        var commandId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        (await kit.SendAsync(kit.Cancel(commandId, reason, order))).StatusCode.Should().Be(HttpStatusCode.OK);

        var log = (await kit.LogsAsync()).Should().ContainSingle().Subject;
        log.CommandId.Should().Be(commandId);
        log.LicenseId.Should().Be(kit.LicenseId);
        log.ReplicaId.Should().Be(kit.ReplicaId);
        log.Kind.Should().Be("cancel");
        log.Status.Should().Be("applied");
        log.RequestHash.Should().HaveCount(32).And.Contain(b => b != 0);
        log.CreatedAt.Should().BeOnOrAfter(before);
        log.ResultJson.Should().Contain(order.Id.ToString());
        log.ResultJson.Should().NotContain(order.DisplayName!).And.NotContain(order.MessageText)
            .And.NotContain(reason).And.NotContain(order.Username);
    }

    /// <summary>Yanıt akış biçimindedir: sipariş asıl kaydın Id'siyle ve KVKK okuma maskesiyle gider — asıl kaydı
    /// silinmiş müşterinin görünen adı ve yorumu yanıtta yok (satır değişmez).</summary>
    [Fact]
    public async Task Yanit_silinmis_musterinin_metnini_maskeler()
    {
        var kit = await Kit.CreateAsync(_factory);
        var canonical = Guid.NewGuid();
        var alias = Guid.NewGuid();
        await kit.WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = canonical, LicenseId = kit.LicenseId, Platform = "youtube",
                Username = $"silinen-{Guid.NewGuid():N}", UpdatedAt = now, PurgedAt = now.AddMinutes(-5),
            });
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = alias, LicenseId = kit.LicenseId, Platform = "youtube",
                Username = $"kopya-{Guid.NewGuid():N}", UpdatedAt = now, MergedIntoId = canonical,
            });
            return Task.CompletedTask;
        });
        var order = await kit.SeedOrderAsync(
            await kit.SeedSessionAsync(Kit.Phase.Released), o => o.CustomerId = alias.ToString("N"));

        var resp = await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", order));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var item = (await Kit.ReadAsync(resp)).Orders.Should().ContainSingle().Subject;
        item.CustomerId.Should().Be(canonical.ToString("N"), "asıl kaydın Id'si");
        item.DisplayName.Should().BeNull();
        item.MessageText.Should().BeEmpty();
        (await kit.OrderAsync(order.Id)).MessageText.Should().Be(order.MessageText, "maske bir yazım değildir");
    }

    /// <summary>§10.2: komutlar bildirim üretmez — yayıncıya ve alıcıya push yok, WhatsApp etiket kuralı uygulanmaz.</summary>
    [Fact]
    public async Task Komutlar_bildirim_uretmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var wpfCustomerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
        var shopperId = Guid.NewGuid();
        await kit.WithDbAsync(db =>
        {
            var now = DateTimeOffset.UtcNow;
            var phone = TestPhone.NewE164();
            db.WpfCustomerProjections.Add(new WpfCustomerProjection
            {
                Id = wpfCustomerId, LicenseId = kit.LicenseId, Platform = "youtube",
                Username = $"u-{Guid.NewGuid():N}", Phone = phone, UpdatedAt = now,
            });
            db.WaConversations.Add(new WaConversation
            {
                Id = conversationId, LicenseId = kit.LicenseId, CustomerPhone = phone[1..], PhoneNumberId = "PNID_1",
                Status = "open", CreatedAt = now,
            });
            var label = new WaLabel
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, Name = "Yeni sipariş", Color = "#eab308", CreatedAt = now,
            };
            db.WaLabels.Add(label);
            foreach (var eventKey in new[] { WaLabelEvent.OrderReceived, WaLabelEvent.ShipmentStatusChanged })
                db.WaLabelRules.Add(new WaLabelRule
                {
                    Id = Guid.NewGuid(), LicenseId = kit.LicenseId, EventKey = eventKey, WaLabelId = label.Id,
                    CreatedAt = now,
                });
            db.Shoppers.Add(new OrderDeck.LicenseServer.Domain.Shopper
            {
                Id = shopperId, FullName = "Alıcı", Phone = TestPhone.NewE164(), PasswordHash = $"hash-{Guid.NewGuid():N}",
                Address = "Adres", CreatedAt = now, UpdatedAt = now, NotificationsEnabledOrders = true,
            });
            db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
            {
                Id = Guid.NewGuid(), ShopperId = shopperId, LicenseId = kit.LicenseId, Platform = "youtube",
                Username = $"u-{Guid.NewGuid():N}", WpfCustomerId = wpfCustomerId, JoinedAt = now,
            });
            return Task.CompletedTask;
        });
        var order = await kit.SeedOrderAsync(
            await kit.SeedSessionAsync(Kit.Phase.Released), o => o.CustomerId = wpfCustomerId.ToString("N"));

        (await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.SendAsync(kit.Uncancel(Guid.NewGuid(), await kit.OrderAsync(order.Id))))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        _factory.Push.Sent.Where(n => n.CustomerId == kit.CustomerId).Should().BeEmpty();
        _factory.Push.SentToShoppers.Where(n => n.ShopperIds.Contains(shopperId)).Should().BeEmpty();
        (await kit.ReadDbAsync(db => db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId)))
            .Should().Be(0, "etiket kuralı uygulanmaz");
    }

    /// <summary>Komut isteği kopyanın kalp atışıdır (§4.0): <c>WpfReplicas.LastSeenAt</c> ilerler.</summary>
    [Fact]
    public async Task Komut_istegi_kalp_atisidir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var old = DateTimeOffset.UtcNow.AddHours(-1);
        await kit.SeedReplicaAsync(kit.ReplicaId, old);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));

        (await kit.SendAsync(kit.Cancel(Guid.NewGuid(), "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);

        var replica = await kit.ReplicaAsync(kit.ReplicaId);
        replica!.LastSeenAt.Should().BeAfter(old);
        replica.SeenMode.Should().Be(SalesSyncMode.On);
    }

    /// <summary>Uygulama kilidi InMemory'de (işlem yok) hiçbir şey yapmaz; etkisi gerçek SQL Server'da sınanır
    /// (<see cref="SalesCommandsRelationalTests"/>).</summary>
    [Fact]
    public async Task Uygulama_kilidi_InMemory_de_etkisiz()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var acquire = () => SalesCommandLock.AcquireAsync(db, $"ship:{Guid.NewGuid():N}", TimeSpan.FromSeconds(1), default);

        await acquire.Should().NotThrowAsync();
    }

    private static readonly DateTimeOffset StartedAt = Kit.StartedAt;

    private static List<Guid> Guids(JsonElement result, string property) =>
        result.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).ToList();
}
