using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;
using V2Result = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>set-code-price</c> — spec 2026-10-13 §4.6, §4.11, DM5, DM7, K2, K5, K6 (InMemory). Planı sunucu kurar: kod
/// eşleşmesi bellekte (<c>LabelRules.SameCode</c>), sınıflama ortak kuraldan (<c>LabelRules.Classify</c>). İki eşzamanlı
/// komutun yayın kilidindeki yarışı ve okuma sırası gerçek SQL Server'da (<see cref="SalesCommandsRelationalTests"/>,
/// §10.1-4).
/// </summary>
public sealed class SalesPriceCommandsTests : IClassFixture<SalesSyncConfiguredApiFactory>
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SalesSyncConfiguredApiFactory _factory;

    public SalesPriceCommandsTests(SalesSyncConfiguredApiFactory factory) => _factory = factory;

    // ── set-code-price: plan ─────────────────────────────────────────────────

    /// <summary>Sorgu kodu, koda eşleşen saklı kodlar ve eşleşmeyen saklı kodlar (LabelRules vektörlerindeki
    /// durumlar).</summary>
    public static TheoryData<string, string[], string[]> CodeCases => new()
    {
        { " Çanta1 ", ["çanta1", "ÇANTA1", "Çanta1"], ["Çanta1 ", " çanta1", "canta1", "Çanta2"] },
        { "ğşöü9", ["ĞŞÖÜ9", "ğşöü9"], ["gsou9", "ğşöü9 "] },
        { "i1", ["i1", "I1"], ["İ1", "ı1", "i1 "] },
        { "A12", ["a12", "A12"], ["A13", "A12 ", " A12"] },
    };

    /// <summary>
    /// §4.11: plan yayının koda eşleşen siparişleridir — eşleşme bellekte, harf duyarsız (Türkçe harfler dahil, noktalı
    /// ve noktasız I ayrı), saklanan kod kırpılmaz (sondaki ya da baştaki boşluklu kod ayrı ürün), sorgu kodu kırpılır.
    /// Başka yayındaki aynı kodlu sipariş plana girmez.
    /// </summary>
    [Theory]
    [MemberData(nameof(CodeCases))]
    public async Task SetCodePrice_plani_bellekte_SameCode_ile_kurar_saklanan_kodu_kirpmaz(
        string code, string[] matching, string[] notMatching)
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var plan = new List<Order>();
        foreach (var stored in matching) plan.Add(await kit.SeedOrderAsync(session, o => o.Code = stored));
        var untouched = new List<Order>();
        foreach (var stored in notMatching) untouched.Add(await kit.SeedOrderAsync(session, o => o.Code = stored));
        var otherSession = await kit.SeedSessionAsync(Kit.Phase.Released);
        untouched.Add(await kit.SeedOrderAsync(otherSession, o => o.Code = matching[0]));

        var resp = await kit.SendAsync(kit.SetCodePrice(Guid.NewGuid(), session, code, 120m, plan));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(resp)).Status.Should().Be("applied");
        foreach (var order in plan)
        {
            var saved = await kit.OrderAsync(order.Id);
            saved.Price.Should().Be(120m, $"'{order.Code}' koda eşleşir");
            saved.PriceSetAt.Should().NotBeNull();
            saved.SyncVersion.Should().Be(2);
        }
        foreach (var order in untouched)
        {
            var saved = await kit.OrderAsync(order.Id);
            saved.Price.Should().Be(100m, $"'{order.Code}' (yayın {order.SessionId}) plana girmez");
            saved.PriceSetAt.Should().BeNull();
            saved.SyncVersion.Should().Be(1);
        }
    }

    /// <summary>
    /// Plan = yayının kod siparişleri ∪ <c>alsoSelectedIds</c> (ör. önceki yayından kuyruğa taşınmış aynı kodlu satır).
    /// Seçilmeyen önceki yayın satırı değişmez. Yanıt istemcinin planını ve seçimini taşır.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_alsoSelected_plana_eklenir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var previous = await kit.SeedSessionAsync(Kit.Phase.Released);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var a = await kit.SeedOrderAsync(session);
        var b = await kit.SeedOrderAsync(session, o => o.Code = "a1");
        var carried = await kit.SeedOrderAsync(previous, o => o.PrintedAt = null);
        var notSelected = await kit.SeedOrderAsync(previous);

        var resp = await kit.SendAsync(kit.SetCodePrice(
            Guid.NewGuid(), session, "A1", 150m, [a, b, carried], alsoSelected: [carried.Id]));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        foreach (var id in new[] { a.Id, b.Id, carried.Id })
            (await kit.OrderAsync(id)).Price.Should().Be(150m);
        (await kit.OrderAsync(notSelected.Id)).Price.Should().Be(100m, "seçilmeyen önceki yayın satırı plana girmez");
        body.Orders.Select(o => o.Id).Should().BeEquivalentTo([a.Id, b.Id, carried.Id]);
        Guids(body.Result, "changed").Should().BeEquivalentTo([a.Id, b.Id, carried.Id]);
        body.Sessions.Select(s => s.Id).Should().BeEquivalentTo([session, previous]);
    }

    /// <summary>Kodsuz seçim (<c>code</c> null): plan yalnız seçilen siparişlerdir; yayının öbür kodsuz siparişleri
    /// değişmez.</summary>
    [Fact]
    public async Task SetCodePrice_kodsuz_secimde_yalniz_secilenler()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var selected = await kit.SeedOrderAsync(session, o => o.Code = null);
        var other = await kit.SeedOrderAsync(session, o => o.Code = null);

        var resp = await kit.SendAsync(kit.SetCodePrice(
            Guid.NewGuid(), session, null, 70m, [selected], alsoSelected: [selected.Id]));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await kit.OrderAsync(selected.Id)).Price.Should().Be(70m);
        (await kit.OrderAsync(other.Id)).Price.Should().Be(100m);
    }

    /// <summary>
    /// <c>Classify</c> (K6): kargoya verilmiş dosyadaki, iptal edilmiş ve kargo ücreti satırları planın dışındadır ve
    /// değişmez; kuyruk ve geçici yedek düzeltilebilirdir. Komuttan sonra yayında kodun düzeltilebilir her etiketi tek
    /// fiyattadır (DM5). Shipped dosyanın tutarı donmuştur.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_kargolanmis_iptal_ve_kargo_ucreti_plan_disinda_kalir_DM5_saglanir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var shipped = await kit.SeedShipmentAsync(customer, ShipmentStatus.Shipped, 100m, derived: true, version: 2);
        var printed = await kit.SeedOrderAsync(session);
        var queued = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var tentative = await kit.SeedOrderAsync(session, o => { o.IsTentativeBackup = true; o.Price = 90m; });
        var inShipped = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = shipped.Id; });
        var cancelled = await kit.SeedOrderAsync(session, o =>
        {
            o.CancelledAt = StartedAt.AddHours(3);
            o.CancelReason = "customer";
        });
        var fee = await kit.SeedOrderAsync(session, o => { o.IsShippingFee = true; o.Price = 60m; });

        var resp = await kit.SendAsync(kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, [printed, queued, tentative]));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        foreach (var order in new[] { printed, queued, tentative })
            (await kit.OrderAsync(order.Id)).Price.Should().Be(120m);
        foreach (var (order, price) in new[] { (inShipped, 100m), (cancelled, 100m), (fee, 60m) })
        {
            var saved = await kit.OrderAsync(order.Id);
            saved.Price.Should().Be(price, "plan dışı satır değişmez");
            saved.PriceSetAt.Should().BeNull();
            saved.SyncVersion.Should().Be(1);
        }
        (await kit.ShipmentAsync(shipped.Id)).Should().BeEquivalentTo(new { CumulativeAmount = 100m, Version = 2 },
            "Shipped dosyanın tutarı donmuştur");
        (await kit.CorrectableCodePricesAsync(session, "A1")).Should().Equal(120m);

        var body = await Kit.ReadAsync(resp);
        body.Orders.Select(o => o.Id).Should().BeEquivalentTo([printed.Id, queued.Id, tentative.Id]);
        Guids(body.Result, "changed").Should().BeEquivalentTo([printed.Id, queued.Id, tentative.Id]);
        Guids(body.Result, "skipped").Should().BeEmpty();
    }

    // ── set-code-price: etkiler ──────────────────────────────────────────────

    /// <summary>
    /// §4.6 etkisi (<c>CorrectPrices</c> + <c>UpdatePriceForCorrection</c> aynası): <c>PriceCorrectedAt</c> yalnız basılmış
    /// satırda; <c>PaidPrice</c> yalnız boşken ve sipariş ödenmişse (dosyaya bağlı ya da <c>paidFromBalanceIds</c>'te)
    /// düzeltmeden önceki fiyat, dolu <c>PaidPrice</c> değişmez; <c>PriceSetAt</c> her yazılan satırda aynı sunucu anı;
    /// fiyatı zaten yeni fiyat olan satır yazılmaz (sürümü ve damgaları değişmez). Açık türetilmiş dosyanın tutarı bağlı,
    /// iptal edilmemiş, geçici yedek olmayan siparişlerin fiyat toplamına eşitlenir (K7, DM7).
    /// </summary>
    [Fact]
    public async Task SetCodePrice_duzeltme_damgasi_odenen_fiyat_ve_dosya_turetmesi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var open = await kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 300m, derived: true, version: 5);
        void Linked(Order o)
        {
            o.CustomerId = customer;
            o.ShipmentId = open.Id;
        }

        var linkedPaid = await kit.SeedOrderAsync(session, Linked);
        var alreadyPaid = await kit.SeedOrderAsync(session, o => { Linked(o); o.PaidPrice = 80m; });
        var balancePaid = await kit.SeedOrderAsync(session);
        var unpaid = await kit.SeedOrderAsync(session);
        var queued = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var same = await kit.SeedOrderAsync(session, o => o.Price = 130m);
        var cancelledLinked = await kit.SeedOrderAsync(session, o =>
        {
            Linked(o);
            o.Code = "B2";
            o.Price = 40m;
            o.CancelledAt = StartedAt.AddHours(3);
            o.CancelReason = "customer";
        });
        await kit.SeedOrderAsync(session, o =>
        {
            Linked(o);
            o.Code = "B2";
            o.Price = 30m;
            o.IsTentativeBackup = true;
        });
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.SetCodePrice(
            Guid.NewGuid(), session, "A1", 130m, [linkedPaid, alreadyPaid, balancePaid, unpaid, queued, same],
            paidFromBalance: [balancePaid.Id, cancelledLinked.Id]));

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = new Dictionary<Guid, Order>();
        foreach (var o in new[] { linkedPaid, alreadyPaid, balancePaid, unpaid, queued, same })
            saved[o.Id] = await kit.OrderAsync(o.Id);

        var stamp = saved[linkedPaid.Id].PriceSetAt;
        stamp.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(after, "sunucu saati");
        foreach (var id in new[] { linkedPaid.Id, alreadyPaid.Id, balancePaid.Id, unpaid.Id, queued.Id })
        {
            saved[id].Price.Should().Be(130m);
            saved[id].PriceSetAt.Should().Be(stamp, "tek komut anı");
            saved[id].SyncVersion.Should().Be(2);
            saved[id].UpdatedAt.Should().Be(stamp!.Value);
        }
        foreach (var id in new[] { linkedPaid.Id, alreadyPaid.Id, balancePaid.Id, unpaid.Id })
            saved[id].PriceCorrectedAt.Should().Be(stamp, "basılmış satır düzeltme işareti alır");
        saved[queued.Id].PriceCorrectedAt.Should().BeNull("kuyruktaki satırın ilk kâğıdı zaten yeni fiyatla çıkar");

        saved[linkedPaid.Id].PaidPrice.Should().Be(100m, "dosyaya bağlı: ödendiği fiyat saklanır");
        saved[alreadyPaid.Id].PaidPrice.Should().Be(80m, "dolu PaidPrice değişmez");
        saved[balancePaid.Id].PaidPrice.Should().Be(100m, "bakiyeden ödenmiş: ödendiği fiyat saklanır");
        saved[unpaid.Id].PaidPrice.Should().BeNull("ödenmemiş");
        saved[queued.Id].PaidPrice.Should().BeNull();

        saved[same.Id].Should().BeEquivalentTo(
            new { Price = 130m, SyncVersion = 1, PriceSetAt = (DateTimeOffset?)null, PriceCorrectedAt = (DateTimeOffset?)null },
            "fiyatı zaten yeni fiyat olan satır yazılmaz");
        (await kit.OrderAsync(cancelledLinked.Id)).PaidPrice.Should().BeNull("plan dışı satır paidFromBalanceIds'te de olsa değişmez");

        var shipment = await kit.ShipmentAsync(open.Id);
        shipment.CumulativeAmount.Should().Be(260m, "bağlı, iptal edilmemiş, geçici yedek olmayan: 130 + 130");
        shipment.Version.Should().Be(6);

        var body = await Kit.ReadAsync(resp);
        Guids(body.Result, "changed").Should().BeEquivalentTo(
            [linkedPaid.Id, alreadyPaid.Id, balancePaid.Id, unpaid.Id, queued.Id]);
        Guids(body.Result, "skipped").Should().Equal(same.Id);
        body.Result.GetProperty("shipments").EnumerateArray()
            .Select(s => (s.GetProperty("id").GetGuid(), s.GetProperty("amount").GetDecimal()))
            .Should().Equal((open.Id, 260m));
    }

    // ── set-code-price: plan-stale, görünürlük, doğrulama ────────────────────

    public enum PlanDrift { NewRow, OldVersion, RowLeftPlan }

    /// <summary>
    /// §4.6, §10.1-4: <c>expected</c> planın kendisidir. Sunucunun planı satırca ya da sürümce farklıysa — istemcinin
    /// görmediği yeni satır, ilerlemiş sürüm, plandan çıkmış (iptal edilmiş) satır — 409 <c>plan-stale</c>: güncel plan
    /// (<c>plan</c>, Id ve sürüm) ve planın, beklenen ve seçilen satırların güncel hâli (<c>current</c>). Hiçbir şey
    /// yazılmaz, günlüğe girmez.
    /// </summary>
    [Theory]
    [InlineData(PlanDrift.NewRow)]
    [InlineData(PlanDrift.OldVersion)]
    [InlineData(PlanDrift.RowLeftPlan)]
    public async Task SetCodePrice_plan_uyusmazsa_409_plan_stale_guncel_plan_yazim_yok(PlanDrift drift)
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var a = await kit.SeedOrderAsync(session);
        var b = await kit.SeedOrderAsync(session, o => o.SyncVersion = drift == PlanDrift.OldVersion ? 3 : 1);
        var left = await kit.SeedOrderAsync(session, o =>
        {
            o.CancelledAt = StartedAt.AddHours(3);
            o.CancelReason = "customer";
            o.SyncVersion = 2;
        });
        (Guid Id, int Version)[] shown = drift switch
        {
            PlanDrift.NewRow => [(a.Id, 1)],
            PlanDrift.OldVersion => [(a.Id, 1), (b.Id, 1)],
            _ => [(a.Id, 1), (b.Id, 1), (left.Id, 1)],
        };

        var resp = await kit.SendAsync(kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, shown));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("plan-stale");
        problem.GetProperty("plan").EnumerateArray()
            .Select(p => (p.GetProperty("orderId").GetGuid(), p.GetProperty("version").GetInt32()))
            .Should().BeEquivalentTo([(a.Id, 1), (b.Id, drift == PlanDrift.OldVersion ? 3 : 1)]);
        var current = problem.GetProperty("current").Deserialize<List<SalesOrderItem>>(Web)!;
        current.Select(o => o.Id).Should().BeEquivalentTo(
            shown.Select(o => o.Id).Union([a.Id, b.Id]), "plan ∪ istemcinin beklediği satırlar");

        foreach (var order in new[] { a, b, left })
        {
            var saved = await kit.OrderAsync(order.Id);
            saved.Price.Should().Be(100m, "hiçbir şey yazılmaz");
            saved.PriceSetAt.Should().BeNull();
            saved.SyncVersion.Should().Be(order.SyncVersion);
        }
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// §4.6 adım 3, Y1, Y8: komutun yayını devredilmemişse (canlı ya da devrediliyor) ya da plana giren seçilmiş bir
    /// sipariş devredilmemiş bir yayındaysa 409 <c>session-live</c>, sahip bilgisayarın adıyla; hiçbir şey yazılmaz.
    /// </summary>
    [Theory]
    [InlineData(Kit.Phase.Live, false)]
    [InlineData(Kit.Phase.HandingOver, false)]
    [InlineData(Kit.Phase.Live, true)]
    [InlineData(Kit.Phase.HandingOver, true)]
    public async Task SetCodePrice_devredilmemis_yayinda_409_session_live(Kit.Phase phase, bool viaSelection)
    {
        var kit = await Kit.CreateAsync(_factory);
        var hidden = await kit.SeedSessionAsync(phase);
        var session = viaSelection ? await kit.SeedSessionAsync(Kit.Phase.Released) : hidden;
        var inSession = await kit.SeedOrderAsync(session);
        var selected = await kit.SeedOrderAsync(hidden);
        Guid[] alsoSelected = viaSelection ? [selected.Id] : [];

        var resp = await kit.SendAsync(kit.SetCodePrice(
            Guid.NewGuid(), session, "A1", 120m, [inSession, selected], alsoSelected: alsoSelected));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("session-live");
        problem.GetProperty("sessionId").GetGuid().Should().Be(hidden);
        problem.GetProperty("machine").GetString().Should().Be("PC-A");
        foreach (var order in new[] { inSession, selected })
            (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new { Price = 100m, SyncVersion = 1 });
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Komutun yayını devredilmemişse plan boş olsa da (kodun sunucuda etiketi yok) 409 <c>session-live</c>: canlı ya da
    /// devrediliyor yayının etiketleri sahipte olabilir, sunucunun boş planı eksik bir plandır (Y1, Y8).
    /// </summary>
    [Theory]
    [InlineData(Kit.Phase.Live)]
    [InlineData(Kit.Phase.HandingOver)]
    public async Task SetCodePrice_kodun_sunucuda_etiketi_olmayan_devredilmemis_yayinda_da_409_session_live(Kit.Phase phase)
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(phase);
        var otherCode = await kit.SeedOrderAsync(session, o => o.Code = "B2");

        var resp = await kit.SendAsync(kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, Array.Empty<Order>()));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("session-live");
        problem.GetProperty("sessionId").GetGuid().Should().Be(session);
        (await kit.OrderAsync(otherCode.Id)).SyncVersion.Should().Be(1);
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Yük doğrulaması (400, hiçbir şey yazılmaz): eksi ya da ikiden çok ondalıklı fiyat, eksik fiyat, eksik yayın, boş
    /// kod, kodsuz seçimde boş seçim; bu lisansta olmayan yayın 400 <c>unknown-session</c>, olmayan seçilmiş sipariş 400
    /// <c>unknown-order</c>.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_gecersiz_yuk_400()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var order = await kit.SeedOrderAsync(session);
        var foreignSession = await (await kit.SecondLicenseAsync()).SeedSessionAsync(Kit.Phase.Released);
        (Guid, int)[] expected = [(order.Id, 1)];

        var cases = new (object Body, string Code)[]
        {
            (kit.SetCodePrice(Guid.NewGuid(), session, "A1", -1m, [order]), "invalid-payload"),
            (kit.SetCodePrice(Guid.NewGuid(), session, "A1", 1.234m, [order]), "invalid-payload"),
            (kit.Body(Guid.NewGuid(), "set-code-price", new { sessionId = session, code = "A1" }, expected), "invalid-payload"),
            (kit.Body(Guid.NewGuid(), "set-code-price", new { code = "A1", newPrice = 120m }, expected), "invalid-payload"),
            (kit.SetCodePrice(Guid.NewGuid(), session, "   ", 120m, [order]), "invalid-payload"),
            (kit.SetCodePrice(Guid.NewGuid(), session, new string('K', 65), 120m, [order]), "invalid-payload"),
            (kit.SetCodePrice(Guid.NewGuid(), session, null, 120m, [order]), "invalid-payload"),
            (kit.SetCodePrice(Guid.NewGuid(), foreignSession, "A1", 120m, [order]), "unknown-session"),
            (kit.SetCodePrice(Guid.NewGuid(), Guid.NewGuid(), "A1", 120m, [order]), "unknown-session"),
            (kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, [order], alsoSelected: [Guid.NewGuid()]), "unknown-order"),
        };
        foreach (var (body, code) in cases)
        {
            var resp = await kit.SendAsync(body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, code);
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be(code);
        }
        (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new { Price = 100m, SyncVersion = 1 });
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Yeniden oynatma: aynı <c>CommandId</c> yeniden uygulanmaz; fiyatın yazımı (<c>120</c> ile <c>120.00</c>) aynı
    /// istektir. Fiyat farklıysa 409 <c>content-conflict</c>.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_yeniden_oynatma_fiyat_yazimindan_bagimsiz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var order = await kit.SeedOrderAsync(session);
        var commandId = Guid.NewGuid();
        var first = await kit.SendAsync(kit.SetCodePrice(commandId, session, "A1", 120m, [order]));
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());

        var replay = await kit.SendAsync(kit.SetCodePrice(commandId, session, "A1", 120.00m, [order]));
        var conflict = await kit.SendAsync(kit.SetCodePrice(commandId, session, "A1", 125m, [order]));

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(replay)).Result.GetRawText()
            .Should().Be((await Kit.ReadAsync(first)).Result.GetRawText());
        conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.ProblemAsync(conflict)).GetProperty("title").GetString().Should().Be("content-conflict");
        (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new { Price = 120m, SyncVersion = 2 });
        (await kit.LogsAsync()).Should().ContainSingle();
    }

    // ── set-code-price: sonraki push ─────────────────────────────────────────

    /// <summary>
    /// Komuttan sonra sahibin bayat push'u fiyatı geri alamaz:
    /// <list type="bullet">
    ///   <item>mod On, olağan devredilmiş yayın: v2 (eski <c>baseVersion</c>) ve v1 push K5 ile reddedilir;</item>
    ///   <item>zorla bitirilmiş yayın: sahibin v2 push'u K2 ile yargılanır — <c>PriceSetAt</c> damgalı fiyat
    ///   <c>ignored</c>;</item>
    ///   <item>mod Off'a dönüşte (acil kapatma): damga kalır, v1 push damgalı fiyatı yazamaz (K2).</item>
    /// </list>
    /// </summary>
    [Fact]
    public async Task SetCodePrice_sonrasi_bayat_push_fiyati_geri_alamaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var released = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var forceEnded = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released, forceEndedBy: "cli"));
        foreach (var order in new[] { released, forceEnded })
        {
            var resp = await kit.SendAsync(kit.SetCodePrice(
                Guid.NewGuid(), order.SessionId!.Value, "A1", 120m, [order]));
            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        }

        var v2 = await OwnerPushV2Async(kit, ItemOf(released), ItemOf(forceEnded));
        v2.Single(r => r.Id == released.Id).Should().BeEquivalentTo(new { Status = "rejected", Reason = "session-released" });
        var judged = v2.Single(r => r.Id == forceEnded.Id);
        judged.Status.Should().Be("ignored");
        judged.IgnoredUnits.Should().Equal("price");
        judged.Current!.Price.Should().Be(120m);

        (await PushV1Async(kit, ItemOf(released))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await kit.OrderAsync(released.Id)).Price.Should().Be(120m, "mod On'da v1 devredilmiş yayında fiyatı yazamaz");

        await kit.WithDbAsync(async db =>
        {
            var license = await db.Licenses.SingleAsync(l => l.Id == kit.LicenseId);
            license.SalesSyncMode = SalesSyncMode.Off;
            license.SalesSyncModeChangedAt = DateTimeOffset.UtcNow;
        });
        (await PushV1Async(kit, ItemOf(released))).StatusCode.Should().Be(HttpStatusCode.OK);

        foreach (var order in new[] { released, forceEnded })
        {
            var saved = await kit.OrderAsync(order.Id);
            saved.Price.Should().Be(120m, "damgalı fiyatı bayat push yazamaz");
            saved.PriceSetAt.Should().NotBeNull();
        }
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static readonly DateTimeOffset StartedAt = Kit.StartedAt;

    private static List<Guid> Guids(JsonElement result, string property) =>
        result.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).ToList();

    /// <summary>Siparişin komuttan ÖNCEKİ hâliyle push öğesi; <c>baseVersion</c> o anki sürüm.</summary>
    private static Item ItemOf(Order o) => new(
        Id: o.Id,
        SessionId: o.SessionId,
        CustomerId: o.CustomerId,
        Platform: o.Platform,
        Username: o.Username,
        DisplayName: o.DisplayName,
        MessageText: o.MessageText,
        Code: o.Code,
        Price: o.Price,
        AddedAt: o.AddedAt,
        PrintedAt: o.PrintedAt,
        CancelledAt: o.CancelledAt,
        CancelReason: o.CancelReason,
        IsShippingFee: o.IsShippingFee,
        IsBackupPromoted: o.IsBackupPromoted,
        IsTentativeBackup: o.IsTentativeBackup,
        BaseVersion: o.SyncVersion);

    /// <summary>Yayın sahibinin (PC-A) v2 push'u.</summary>
    private static async Task<List<V2Result>> OwnerPushV2Async(Kit kit, params Item[] items)
    {
        var resp = await kit.SendToAsync(OrdersUrl(kit), new
        {
            orders = items, format = 2, replicaId = kit.OwnerReplicaId, protocol = 2, seenMode = 1,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return (await resp.Content.ReadFromJsonAsync<List<V2Result>>())!;
    }

    /// <summary>Protokolsüz (≤0.9.10) push.</summary>
    private static Task<HttpResponseMessage> PushV1Async(Kit kit, params Item[] items) =>
        kit.SendToAsync(OrdersUrl(kit), new { orders = items }, userAgent: null);

    private static string OrdersUrl(Kit kit) => $"/api/v1/licenses/{kit.LicenseId}/orders/sync";
}
