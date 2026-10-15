using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync.Commands;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;
using V2Result = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.V2Result;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>POST sales/commands</c>'ın eşzamanlılık sözleşmesi gerçek SQL Server'da — spec 2026-10-13 §4.6 adım 5–6, §10.1-4,
/// §10.1-5, §10.1-13. Komut siparişi <c>SyncVersion</c> jetonu altında ve tek işlemde yazar, planı kuran komut yayın
/// kilidini alır; InMemory jetonu, işlemi ve kilidi uygulamadığı için yarışın hakemi ancak burada kanıtlanır.
///
/// <para>Yarış sırayla kurulur (zamanlamayla değil): bir isteğin asıl kaydetmesi diske inmeden hemen önce kanca öbür
/// isteği baştan sona koşturur (<see cref="ShippingSettingsRelationalTests"/> deseni). Kanca yalnız seçilen kaydetmede
/// tetiklenir; isteklerin kalp atışı (kopya satırı) ondan önce ayrı kaydedilir.</para>
/// Canlı yayın sırasında ÇALIŞTIRILMAZ.
/// </summary>
[Collection(SqlServerCollection.Name)]
[Trait("Category", "Testcontainers")]
public sealed class SalesCommandsRelationalTests : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _sql;
    private string _connectionString = null!;
    private CommandHookFactory _factory = null!;
    private Kit _kit = null!;

    public SalesCommandsRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
    {
        _connectionString = await _sql.CreateDatabaseAsync();
        _factory = new CommandHookFactory(_connectionString);
        _kit = await Kit.CreateAsync(_factory);
    }

    public Task DisposeAsync()
    {
        _factory.Hook.Before = null;
        _factory.Barrier.Disarm();
        _factory.Queries.Disarm();
        _kit.Client.Dispose();
        _factory.Dispose();
        return Task.CompletedTask;
    }

    // ── §10.1-5: komut ile sahibin geç push'u ────────────────────────────────

    /// <summary>
    /// §10.1-5 (a): komutun yazımı diske inmeden hemen önce sahibin geç push'u (zorla bitirilmiş yayın, K5 istisnası i)
    /// aynı satırı yazar. Push kazanır; komut jetona çarpar, işlem tümüyle geri alınır ve 409 <c>stale</c> güncel satırı
    /// taşır. Yalnız biri 409; komutun hiçbir yazımı (iptal, damga, günlük) kalmaz.
    /// </summary>
    [Fact]
    public async Task Push_komutun_yazimindan_once_gelirse_komut_409_stale_push_kazanir()
    {
        var order = await SeedForceEndedOrderAsync();
        HttpResponseMessage? push = null;
        _factory.Hook.When = db => db.ChangeTracker.Entries<SalesCommandLog>().Any();
        _factory.Hook.Before = async () => push = await OwnerPushAsync(ItemOf(order) with { Price = 90m });

        var command = await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order));

        push.Should().NotBeNull("kanca push'u komutun yazımından önce koşturmalı");
        push!.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        command.StatusCode.Should().Be(HttpStatusCode.Conflict, await command.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(command);
        problem.GetProperty("title").GetString().Should().Be("stale");
        var current = problem.GetProperty("current").EnumerateArray().Should().ContainSingle().Subject;
        current.GetProperty("price").GetDecimal().Should().Be(90m, "taze satır push'un yazımını gösterir");
        current.GetProperty("version").GetInt32().Should().Be(2);

        var saved = await _kit.OrderAsync(order.Id);
        saved.Price.Should().Be(90m);
        saved.CancelledAt.Should().BeNull("komutun yazımı geri alındı");
        saved.CancelSetAt.Should().BeNull();
        saved.SyncVersion.Should().Be(2);
        (await _kit.LogsAsync()).Should().BeEmpty("reddedilen komut günlüğe girmez");
    }

    /// <summary>
    /// §10.1-5 (b): push'un yazımı diske inmeden hemen önce komut baştan sona koşar. Komut kazanır; push jetona çarpar ve
    /// 409 <c>sync-conflict</c> alır (istemci yeniden gönderir). Yalnız biri 409.
    /// </summary>
    [Fact]
    public async Task Komut_pushun_yaziminden_once_gelirse_push_409_komut_kazanir()
    {
        var order = await SeedForceEndedOrderAsync();
        HttpResponseMessage? command = null;
        _factory.Hook.When = db => db.ChangeTracker.Entries<Order>().Any(e => e.State == EntityState.Modified)
            && !db.ChangeTracker.Entries<SalesCommandLog>().Any();
        _factory.Hook.Before = async () => command = await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order));

        var push = await OwnerPushAsync(ItemOf(order) with { Price = 90m });

        command.Should().NotBeNull("kanca komutu push'un yazımından önce koşturmalı");
        command!.StatusCode.Should().Be(HttpStatusCode.OK, await command.Content.ReadAsStringAsync());
        push.StatusCode.Should().Be(HttpStatusCode.Conflict, await push.Content.ReadAsStringAsync());
        (await Kit.ProblemAsync(push)).GetProperty("title").GetString().Should().Be("sync-conflict");

        var saved = await _kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().NotBeNull();
        saved.CancelSetAt.Should().NotBeNull();
        saved.Price.Should().Be(100m, "push'un yazımı geri alındı");
        saved.SyncVersion.Should().Be(2);
        (await _kit.LogsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// §10.1-5, K2, K5 istisnası (i): komuttan sonra zorla bitirilmiş yayının sahibi bayat <c>baseVersion</c>'lı v2
    /// push'uyla damgalı iptali geri alamaz — <c>ignored</c> ve güncel satır; damgasız fiyat değişikliği push'tan
    /// alınır (push toptan reddedilmez).
    /// </summary>
    [Fact]
    public async Task Komuttan_sonra_sahibin_bayat_pushu_damgali_iptali_degistiremez()
    {
        var order = await SeedForceEndedOrderAsync();
        (await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await _kit.OrderAsync(order.Id);

        var push = await OwnerPushAsync(ItemOf(order) with { Price = 95m, BaseVersion = order.SyncVersion });

        push.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        var result = (await push.Content.ReadFromJsonAsync<List<V2Result>>())!.Single();
        result.Status.Should().Be("ignored");
        result.IgnoredUnits.Should().Equal("cancel");
        result.Current!.CancelledAt.Should().Be(cancelled.CancelledAt);
        result.Current.CancelSetAt.Should().Be(cancelled.CancelSetAt);

        var saved = await _kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().Be(cancelled.CancelledAt, "damgalı birimi bayat push değiştiremez");
        saved.CancelReason.Should().Be("customer");
        saved.Price.Should().Be(95m, "damgasız birim sahibin push'undan alınır");
    }

    // ── §10.1-13: mod On'da protokolsüz push ─────────────────────────────────

    /// <summary>§10.1-13: mod On'da protokolsüz (≤0.9.10) push komutun iptalini geri alamaz; lisansın eski sürüm izi
    /// (<c>SalesLegacyTraces</c>) yazılır.</summary>
    [Fact]
    public async Task Mod_On_da_protokolsuz_push_komut_iptalini_geri_alamaz_iz_yazilir()
    {
        var order = await _kit.SeedOrderAsync(await _kit.SeedSessionAsync(Kit.Phase.Released));
        (await _kit.SendAsync(_kit.Cancel(Guid.NewGuid(), "customer", order))).StatusCode.Should().Be(HttpStatusCode.OK);
        var cancelled = await _kit.OrderAsync(order.Id);
        (await LegacyTraceAsync()).Should().BeNull("ön koşul: protokolsüz istek yok");

        var push = await _kit.SendToAsync(OrdersUrl, new { orders = new[] { ItemOf(order) } }, userAgent: null);

        push.StatusCode.Should().Be(HttpStatusCode.OK, await push.Content.ReadAsStringAsync());
        var saved = await _kit.OrderAsync(order.Id);
        saved.CancelledAt.Should().Be(cancelled.CancelledAt, "v1 damgalı birimi yazamaz");
        saved.CancelSetAt.Should().Be(cancelled.CancelSetAt);
        (await LegacyTraceAsync()).Should().NotBeNull("protokolsüz istek izi iki kipte yazılır");
    }

    // ── Aynı komutun eşzamanlı ikinci kopyası ────────────────────────────────

    /// <summary>
    /// Aynı <c>CommandId</c> eşzamanlı iki kez: ikincisi birincinin yazımından önce baştan sona koşar ve kaydeder. Birinci
    /// sipariş jetonuna (yazacak satır varsa) ya da günlüğün birincil anahtarına (yoksa) çarpar, günlüğü yeniden okur ve
    /// kayıtlı sonucu oynatır: komut bir kez uygulanır, iki istek de 200 ve aynı sonucu alır.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ayni_komut_eszamanli_iki_kez_bir_kez_uygulanir_ikisi_de_ayni_sonucu_alir(bool alreadyCancelled)
    {
        var order = await _kit.SeedOrderAsync(await _kit.SeedSessionAsync(Kit.Phase.Released), o =>
        {
            if (alreadyCancelled)
            {
                o.CancelledAt = Kit.StartedAt.AddHours(3);
                o.CancelReason = "customer";
            }
        });
        var body = _kit.Cancel(Guid.NewGuid(), "customer", order);
        HttpResponseMessage? rival = null;
        _factory.Hook.When = db => db.ChangeTracker.Entries<SalesCommandLog>().Any();
        _factory.Hook.Before = async () => rival = await _kit.SendAsync(body);

        var first = await _kit.SendAsync(body);

        rival.Should().NotBeNull("kanca ikinci kopyayı birincinin yazımından önce koşturmalı");
        rival!.StatusCode.Should().Be(HttpStatusCode.OK, await rival.Content.ReadAsStringAsync());
        first.StatusCode.Should().Be(HttpStatusCode.OK, await first.Content.ReadAsStringAsync());
        var rivalBody = await Kit.ReadAsync(rival);
        var firstBody = await Kit.ReadAsync(first);
        firstBody.Status.Should().Be(rivalBody.Status);
        firstBody.Result.GetRawText().Should().Be(rivalBody.Result.GetRawText());
        firstBody.Status.Should().Be(alreadyCancelled ? "unchanged" : "applied");

        (await _kit.LogsAsync()).Should().ContainSingle();
        (await _kit.OrderAsync(order.Id)).SyncVersion.Should().Be(alreadyCancelled ? 1 : 2, "komut bir kez uygulanır");
    }

    // ── §10.1-4: aynı yayın×koda eşzamanlı iki set-code-price ────────────────

    /// <summary>
    /// §10.1-4, DM5: aynı yayın×koda iki kopyadan, farklı fiyatlarla, aynı planı görmüş eşzamanlı iki
    /// <c>set-code-price</c>. Engel: komutun kaydetmesi öbür istek de kaydetmeye gelene kadar (en çok 1,5 sn — kilit
    /// bekleme sınırının altında) bekler; iki istek de planını öbürünün yazımından önce kurabilseydi ikisi de engele gelir.
    /// Yayın kilidi bunu önler: ikinci istek kilitte bekler, planı birincinin yazımından SONRA kurar ve 409
    /// <c>plan-stale</c> (güncel plan, güncel satırlar) alır — bayat planla jetona çarpıp <c>stale</c> almaz. Tam olarak
    /// biri 200; veritabanında kodun düzeltilebilir her etiketi kazananın fiyatındadır.
    /// </summary>
    [Fact]
    public async Task Ayni_yayin_koduna_eszamanli_iki_set_code_price_biri_200_obur_409_plan_stale_DM5_korunur()
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var plan = new List<Order>();
        foreach (var code in new[] { "A1", "a1", "A1" })
            plan.Add(await _kit.SeedOrderAsync(session, o => o.Code = code));
        var other = await _kit.SeedOrderAsync(session, o => o.Code = "B2");
        _factory.Barrier.Arm(db => db.ChangeTracker.Entries<SalesCommandLog>().Any(), TimeSpan.FromSeconds(1.5));

        var responses = await Task.WhenAll(
            _kit.SendAsync(_kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, plan, replicaId: Guid.NewGuid())),
            _kit.SendAsync(_kit.SetCodePrice(Guid.NewGuid(), session, "a1", 130m, plan, replicaId: Guid.NewGuid())));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict], string.Join(" | ", bodies));
        var winnerPrice = responses[0].StatusCode == HttpStatusCode.OK ? 120m : 130m;
        var loser = responses.Single(r => r.StatusCode == HttpStatusCode.Conflict);
        var problem = await Kit.ProblemAsync(loser);
        problem.GetProperty("title").GetString().Should().Be("plan-stale", "kilit planı yazımdan sonra kurdurur");
        problem.GetProperty("plan").EnumerateArray()
            .Select(p => (p.GetProperty("orderId").GetGuid(), p.GetProperty("version").GetInt32()))
            .Should().BeEquivalentTo(plan.Select(o => (o.Id, 2)));
        problem.GetProperty("current").EnumerateArray().Select(o => o.GetProperty("price").GetDecimal())
            .Should().AllSatisfy(p => p.Should().Be(winnerPrice));

        (await _kit.CorrectableCodePricesAsync(session, "A1")).Should().Equal(winnerPrice);
        foreach (var order in plan)
            (await _kit.OrderAsync(order.Id)).SyncVersion.Should().Be(2, "yalnız kazanan yazdı");
        (await _kit.OrderAsync(other.Id)).Should().BeEquivalentTo(new { Price = 100m, SyncVersion = 1 });
        (await _kit.LogsAsync()).Should().ContainSingle();
    }

    /// <summary>
    /// §4.11 gerçek SQL Server'da: kod eşleşmesi sunucu harmanına bırakılmaz. Harf duyarsız harman sondaki boşluğu yok
    /// sayar (<c>'A1 ' = 'A1'</c>, <c>LEN</c> da saymaz) ve Türkçe I'ları ortak koddan farklı katlar; plan yine
    /// <c>LabelRules.SameCode</c>'un seçtiği satırlardır — boşluklu kod ve noktalı/noktasız I ayrı üründür.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_kod_eslesmesi_SQL_Server_harmanina_birakilmaz()
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var cases = new (string Query, string[] Matching, string[] NotMatching)[]
        {
            (" Çanta1 ", ["çanta1", "ÇANTA1"], ["Çanta1 ", "Çanta1  ", " Çanta1", "canta1"]),
            ("i1", ["i1", "I1"], ["İ1", "ı1", "i1 "]),
        };
        var untouched = new List<Order>();
        foreach (var (query, matching, notMatching) in cases)
        {
            var plan = new List<Order>();
            foreach (var stored in matching) plan.Add(await _kit.SeedOrderAsync(session, o => o.Code = stored));
            foreach (var stored in notMatching) untouched.Add(await _kit.SeedOrderAsync(session, o => o.Code = stored));

            var resp = await _kit.SendAsync(_kit.SetCodePrice(Guid.NewGuid(), session, query, 120m, plan));

            resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
            foreach (var order in plan)
                (await _kit.OrderAsync(order.Id)).Price.Should().Be(120m, $"'{order.Code}' '{query}' koduna eşleşir");
        }
        foreach (var order in untouched)
            (await _kit.OrderAsync(order.Id)).Should().BeEquivalentTo(
                new { Price = 100m, SyncVersion = 1 }, $"'{order.Code}' ayrı üründür");
    }

    /// <summary>
    /// §4.6 adım 6, DM7: <c>set-code-price</c> planındaki, fiyatı zaten yeni fiyat olan (yazmadığı) bir sipariş, komut
    /// siparişleri okuduktan sonra eşzamanlı bir <c>cancel</c> ile iptal edilir; aynı dosyaya bağlı başka bir plan
    /// siparişinin fiyatı değişir, komut dosyayı yeniden türetir. Dosya siparişlerden ÖNCE yüklendiği için komutun bayat
    /// okuması dosya jetonunda yakalanır: komut 409 <c>stale</c>, iptal kazanır, dosyanın tutarı iptal edilen siparişi
    /// içermez.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_okumasindan_sonraki_eszamanli_iptal_dosya_tutarini_bozamaz()
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var shipment = await _kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 220m, derived: true);
        var corrected = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = shipment.Id; });
        var samePrice = await _kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.ShipmentId = shipment.Id;
            o.Price = 120m;
        });
        HttpResponseMessage? cancel = null;
        _factory.Queries.Arm(
            after: sql => sql.Contains("FROM [Orders]") && sql.Contains("[MessageText]"),
            when: sql => sql.Contains("FROM [Shipments]"),
            before: async () => cancel = await _kit.SendAsync(_kit.Body(
                Guid.NewGuid(), "cancel", new { items = new[] { samePrice.Id }, reason = "customer" },
                [(samePrice.Id, samePrice.SyncVersion)], replicaId: Guid.NewGuid())));

        var command = await _kit.SendAsync(
            _kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, [corrected, samePrice]));

        cancel.Should().NotBeNull("kanca iptali komutun sipariş okumasından sonra koşturmalı");
        cancel!.StatusCode.Should().Be(HttpStatusCode.OK, await cancel.Content.ReadAsStringAsync());
        command.StatusCode.Should().Be(HttpStatusCode.Conflict, await command.Content.ReadAsStringAsync());
        (await Kit.ProblemAsync(command)).GetProperty("title").GetString().Should().Be("stale");
        (await _kit.OrderAsync(corrected.Id)).Should().BeEquivalentTo(
            new { Price = 100m, SyncVersion = 1, PriceSetAt = (DateTimeOffset?)null }, "komutun yazımı geri alındı");
        (await _kit.OrderAsync(samePrice.Id)).CancelledAt.Should().NotBeNull();
        (await _kit.ShipmentAsync(shipment.Id)).CumulativeAmount
            .Should().Be(100m, "DM7: bağlı, iptal edilmemiş siparişlerin toplamı");
    }

    /// <summary>
    /// Plan kurulurken (ön okumadan sonra) eşzamanlı bir yazıcı (ör. geri doldurma) plan siparişini kargoya verilmiş bir
    /// dosyaya bağlar ve sürümünü ilerletir. Sipariş o dosyanın durumuyla sınıflanır (K6, <c>Classify</c>): planın
    /// dışındadır — <c>plan-stale</c>'in planı onu düzeltilebilir göstermez — ve hiçbir şey yazılmaz.
    /// </summary>
    [Fact]
    public async Task SetCodePrice_plan_kurulurken_kargolanmis_dosyaya_baglanan_siparis_planda_yer_almaz()
    {
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var open = await _kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 100m, derived: true);
        var shipped = await _kit.SeedShipmentAsync(customer, ShipmentStatus.Shipped, 90m, derived: true);
        var linked = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = open.Id; });
        var linkedLate = await _kit.SeedOrderAsync(session, o => o.CustomerId = customer);
        _factory.Queries.Arm(
            after: sql => sql.Contains("FROM [Orders]") && sql.Contains("[ShipmentId]") && !sql.Contains("[MessageText]"),
            when: sql => sql.Contains("FROM [Shipments]"),
            before: () => _kit.WithDbAsync(async db =>
            {
                var row = await db.Orders.SingleAsync(o => o.Id == linkedLate.Id);
                row.ShipmentId = shipped.Id;
                row.SyncVersion++;
            }));

        var command = await _kit.SendAsync(
            _kit.SetCodePrice(Guid.NewGuid(), session, "A1", 120m, [linked, linkedLate]));

        command.StatusCode.Should().Be(HttpStatusCode.Conflict, await command.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(command);
        problem.GetProperty("title").GetString().Should().Be("plan-stale");
        problem.GetProperty("plan").EnumerateArray()
            .Select(p => (p.GetProperty("orderId").GetGuid(), p.GetProperty("version").GetInt32()))
            .Should().Equal([(linked.Id, 1)], "kargolanmış dosyadaki sipariş düzeltilebilir değildir");
        (await _kit.OrderAsync(linked.Id)).Price.Should().Be(100m);
        (await _kit.OrderAsync(linkedLate.Id)).Should().BeEquivalentTo(new { Price = 100m, SyncVersion = 2 });
    }

    // ── Uygulama kilidi ──────────────────────────────────────────────────────

    /// <summary>
    /// <see cref="SalesCommandLock"/>: kilit işleme aittir — tutan işlem sürerken aynı kaynağı isteyen ikinci işlem
    /// zaman aşımında 409 <c>stale</c> alır (sınırsız beklemez); ilk işlem bitince kilit serbesttir. İşlemsiz çağrı
    /// programlama hatasıdır.
    /// </summary>
    [Fact]
    public async Task Uygulama_kilidi_islem_boyunca_tutulur_zaman_asiminda_409_stale()
    {
        var resource = $"ship:{_kit.LicenseId:N}:{Guid.NewGuid():N}";
        await using var holder = NewDb();
        await using var held = await holder.Database.BeginTransactionAsync();
        await SalesCommandLock.AcquireAsync(holder, resource, TimeSpan.FromSeconds(1), default);

        await using var waiter = NewDb();
        await using (var waiting = await waiter.Database.BeginTransactionAsync())
        {
            var blocked = () => SalesCommandLock.AcquireAsync(waiter, resource, TimeSpan.FromMilliseconds(200), default);
            (await blocked.Should().ThrowAsync<SalesCommandRejection>()).Which.Code.Should().Be("stale");

            await held.CommitAsync();
            await SalesCommandLock.AcquireAsync(waiter, resource, TimeSpan.FromSeconds(1), default);
            await waiting.RollbackAsync();
        }

        var withoutTransaction = () => SalesCommandLock.AcquireAsync(waiter, resource, TimeSpan.FromSeconds(1), default);
        await withoutTransaction.Should().ThrowAsync<InvalidOperationException>();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private string OrdersUrl => $"/api/v1/licenses/{_kit.LicenseId}/orders/sync";

    /// <summary>Zorla bitirilmiş, devredilmiş bir yayının siparişi: sahibin v2 push'u K2 ile yargılanır (K5 istisnası i).</summary>
    private async Task<Order> SeedForceEndedOrderAsync() =>
        await _kit.SeedOrderAsync(await _kit.SeedSessionAsync(Kit.Phase.Released, forceEndedBy: "cli"));

    /// <summary>Yayın sahibinin (PC-A) v2 push'u.</summary>
    private Task<HttpResponseMessage> OwnerPushAsync(Item item) =>
        _kit.SendToAsync(OrdersUrl, new
        {
            orders = new[] { item },
            format = 2,
            replicaId = _kit.OwnerReplicaId,
            protocol = 2,
            seenMode = 1,
        });

    /// <summary>Siparişin push öğesi; <c>baseVersion</c> satırın şimdiki sürümü.</summary>
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

    private async Task<DateTimeOffset?> LegacyTraceAsync() =>
        await _kit.ReadDbAsync(db => db.SalesLegacyTraces.AsNoTracking()
            .Where(t => t.LicenseId == _kit.LicenseId)
            .Select(t => (DateTimeOffset?)t.LastLegacySalesPushAt)
            .SingleOrDefaultAsync());

    private LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>().UseSqlServer(_connectionString).Options);

    /// <summary>Yalnız <see cref="When"/>'i tutan kaydetmenin hemen öncesinde bir kez koşar; yeniden girmez (kanca
    /// gövdesindeki rakip istek kendi kaydetmesinde onu tetiklemez).</summary>
    private sealed class PredicateSaveHook : SaveChangesInterceptor
    {
        private bool _running;

        public Func<DbContext, bool>? When { get; set; }

        public Func<Task>? Before { get; set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (Before is { } hook && When is { } when && !_running && when(eventData.Context!))
            {
                Before = null;
                _running = true;
                try { await hook(); }
                finally { _running = false; }
            }
            return result;
        }
    }

    /// <summary>
    /// İki taraflı engel: <c>When</c>'i tutan kaydetme, ikinci bir kaydetme de gelene ya da süre dolana kadar bekler.
    /// Süre dolması "öbür istek gelmedi" demektir (ör. kilitte bekliyor); bekleyen istek o zaman yoluna devam eder.
    /// </summary>
    private sealed class BarrierSaveHook : SaveChangesInterceptor
    {
        private Func<DbContext, bool>? _when;
        private TimeSpan _timeout;
        private int _arrived;
        private TaskCompletionSource _both = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Arm(Func<DbContext, bool> when, TimeSpan timeout)
        {
            _arrived = 0;
            _timeout = timeout;
            _both = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _when = when;
        }

        public void Disarm() => _when = null;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (_when is { } when && when(eventData.Context!))
            {
                if (Interlocked.Increment(ref _arrived) >= 2) _both.TrySetResult();
                await Task.WhenAny(_both.Task, Task.Delay(_timeout, cancellationToken));
            }
            return result;
        }
    }

    /// <summary>
    /// Sorgu kancası: <c>after</c>'ı tutan bir sorgu görüldükten sonra, <c>when</c>'i tutan ilk sorgunun hemen öncesinde
    /// <c>before</c>'u bir kez koşturur — bir isteğin iki okuması arasına eşzamanlı bir yazımı sırayla yerleştirir.
    /// Yeniden girmez (kanca gövdesinin sorguları onu tetiklemez).
    /// </summary>
    private sealed class QueryHook : DbCommandInterceptor
    {
        private Func<string, bool>? _after;
        private Func<string, bool>? _when;
        private Func<Task>? _before;
        private bool _afterSeen;
        private bool _running;

        public void Arm(Func<string, bool> after, Func<string, bool> when, Func<Task> before)
        {
            _afterSeen = false;
            _after = after;
            _when = when;
            _before = before;
        }

        public void Disarm() => _before = null;

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (_before is { } hook && !_running)
            {
                var sql = command.CommandText;
                if (_afterSeen && _when!(sql))
                {
                    _before = null;
                    _running = true;
                    try { await hook(); }
                    finally { _running = false; }
                }
                else if (_after!(sql))
                {
                    _afterSeen = true;
                }
            }
            return result;
        }
    }

    private sealed class CommandHookFactory(string connectionString) : RelationalApiFactory(connectionString)
    {
        public PredicateSaveHook Hook { get; } = new();

        public BarrierSaveHook Barrier { get; } = new();

        public QueryHook Queries { get; } = new();

        protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
        {
            ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
        };

        protected override void ConfigureDbContextOptions(DbContextOptionsBuilder opt) =>
            opt.AddInterceptors(Hook, Barrier, Queries);
    }
}
