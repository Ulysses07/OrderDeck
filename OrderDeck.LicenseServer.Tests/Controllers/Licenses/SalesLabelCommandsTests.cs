using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Item = OrderDeck.LicenseServer.Tests.Controllers.Licenses.OrderSyncV2Tests.Item;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>Alt sürümü tanımlı, KVKK metin boşaltması KAPALI (varsayılan; sahip kararı, §11) sunucu.</summary>
public sealed class SalesSyncNoScrubApiFactory : ApiFactory
{
    protected override IDictionary<string, string?> ExtraConfig => new Dictionary<string, string?>
    {
        ["SalesSync:MinClientVersion"] = SalesSyncConfiguredApiFactory.MinClientVersion,
    };
}

/// <summary>
/// Etiket yaşam döngüsü komutları — spec 2026-10-13 §4.6 (<c>add-backup</c>, <c>mark-printed</c>), §2 "Oluşum
/// alanları", DM3, DM4, K3, §9 (InMemory). <c>add-backup</c>'ın akışa girişi ve eşzamanlı aynı Id gerçek SQL Server'da
/// (<see cref="SalesCommandsRelationalTests"/>).
/// </summary>
public sealed class SalesLabelCommandsTests
    : IClassFixture<SalesSyncConfiguredApiFactory>, IClassFixture<SalesSyncNoScrubApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;
    private readonly SalesSyncNoScrubApiFactory _noScrub;

    public SalesLabelCommandsTests(SalesSyncConfiguredApiFactory factory, SalesSyncNoScrubApiFactory noScrub)
    {
        _factory = factory;
        _noScrub = noScrub;
    }

    // ── add-backup ───────────────────────────────────────────────────────────

    /// <summary>
    /// Devredilmiş yayına geçici yedek (§4.6, yerel <c>AddBackup</c> aynası): ebeveynin yayınına, geçici ve "Y" damgalı,
    /// ebeveyne bağlı, kaynak kopya çağıran, devir anı yayınınki (akışa hemen girer, DM3), ekleme anı sunucu saati;
    /// fiyat ve kod ebeveyninki. Müşteri kopya Id'siyle gelse de asıl kayda çözülür. Ürün KOPYALANMAZ (§12): defter
    /// satırı yok; birim damgası yok. Ebeveyn değişmez. Günlüğün sonucu Id ve sürüm taşır; ad, kullanıcı adı ve yorum
    /// taşımaz (§9).
    /// </summary>
    [Fact]
    public async Task AddBackup_devredilmis_yayina_gecici_yedek_ekler_musteri_asil_kayda_cozulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var productId = await kit.SeedProductAsync();
        var parent = await kit.SeedOrderAsync(session, o =>
        {
            o.ProductId = productId;
            o.Code = "A7";
            o.Price = 150m;
        });
        var canonical = Guid.NewGuid();
        var alias = Guid.NewGuid();
        await kit.SeedProjectionAsync(canonical);
        await kit.SeedProjectionAsync(alias, mergedInto: canonical);
        var customer = Kit.BackupCustomer.New(alias.ToString("N"));
        var message = $"yorum-{Guid.NewGuid():N}";
        var newId = Guid.NewGuid();
        var commandId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.AddBackup(commandId, newId, parent.Id, customer, message));

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("applied");
        var item = body.Orders.Should().ContainSingle().Subject;
        item.Id.Should().Be(newId);
        item.CustomerId.Should().Be(canonical.ToString("N"));
        item.Version.Should().Be(0);
        body.Sessions.Select(s => s.Id).Should().Equal(session);

        var saved = await kit.OrderAsync(newId);
        saved.Should().BeEquivalentTo(new
        {
            LicenseId = kit.LicenseId,
            SessionId = (Guid?)session,
            CustomerId = canonical.ToString("N"),
            customer.Platform,
            customer.Username,
            customer.DisplayName,
            MessageText = message,
            Code = "A7",
            Price = 150m,
            PrintedAt = (DateTimeOffset?)null,
            CancelledAt = (DateTimeOffset?)null,
            IsShippingFee = false,
            IsBackupPromoted = true,
            IsTentativeBackup = true,
            ParentOrderId = (Guid?)parent.Id,
            OriginReplicaId = (Guid?)kit.ReplicaId,
            SessionReleasedAt = (DateTimeOffset?)Kit.StartedAt.AddHours(2),
            ProductId = (Guid?)null,
            ProductVariantId = (Guid?)null,
            ShipmentId = (Guid?)null,
            PriceSetAt = (DateTimeOffset?)null,
            CancelSetAt = (DateTimeOffset?)null,
            BackupSetAt = (DateTimeOffset?)null,
            TextScrubbedAt = (DateTimeOffset?)null,
            SyncVersion = 0,
        });
        saved.AddedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        (await kit.MovementsAsync(newId)).Should().BeEmpty("ürünsüz yedek defterde yok");
        (await kit.OrderAsync(parent.Id)).SyncVersion.Should().Be(1, "ebeveyn yazılmaz");

        var log = (await kit.LogsAsync()).Should().ContainSingle().Subject;
        log.Kind.Should().Be("add-backup");
        log.Status.Should().Be("applied");
        log.ResultJson.Should().Contain(newId.ToString());
        log.ResultJson.Should().NotContain(message).And.NotContain(customer.DisplayName!)
            .And.NotContain(customer.Username);
    }

    /// <summary>Verilen fiyat ve kod ebeveyninkinin yerine yazılır; yorum yoksa boş metin. GUID olmayan müşteri Id'si
    /// ham kalır (projeksiyonu yok).</summary>
    [Fact]
    public async Task AddBackup_fiyat_ve_kod_verilirse_onlar_GUID_olmayan_musteri_ham_kalir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var parent = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var rawId = $"yerel-{Guid.NewGuid():N}";
        var newId = Guid.NewGuid();

        var resp = await kit.SendAsync(kit.AddBackup(
            Guid.NewGuid(), newId, parent.Id, Kit.BackupCustomer.New(rawId), messageText: null, price: 80m, code: "B2"));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await kit.OrderAsync(newId)).Should().BeEquivalentTo(new
        {
            Price = 80m, Code = "B2", MessageText = "", CustomerId = rawId, IsTentativeBackup = true,
        });
    }

    /// <summary>Ebeveynin yayını devredilmemiş (canlı ya da devrediliyor) ya da ebeveyn yayınsız: 409
    /// <c>session-live</c> — canlı yayına yalnız sahibi ekler (Y1), devrediliyor yayının etiketleri sahipte bekliyor
    /// (Y8). Hiçbir şey eklenmez, günlük yok.</summary>
    [Theory]
    [InlineData(Kit.Phase.Live)]
    [InlineData(Kit.Phase.HandingOver)]
    [InlineData(null)]
    public async Task AddBackup_devredilmemis_yayindaki_ebeveyn_409_session_live(Kit.Phase? phase)
    {
        var kit = await Kit.CreateAsync(_factory);
        var parent = await kit.SeedOrderAsync(phase is { } p ? await kit.SeedSessionAsync(p) : null);
        var newId = Guid.NewGuid();

        var resp = await kit.SendAsync(kit.AddBackup(
            Guid.NewGuid(), newId, parent.Id, Kit.BackupCustomer.New(), $"yorum-{Guid.NewGuid():N}"));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("session-live");
        if (phase is not null) problem.GetProperty("machine").GetString().Should().Be("PC-A");
        (await kit.OrdersWithIdAsync(newId)).Should().BeEmpty();
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Yeni Id sunucuda varsa 409 <c>content-conflict</c>, 500 değil: bu lisansta başka bir siparişin Id'si, başka
    /// yayıncının lisansındaki bir siparişin Id'si (Orders'ın birincil anahtarı yalnız Id) ve önceki bir
    /// <c>add-backup</c>'ın eklediği Id'yi yeni CommandId ile yeniden istemek. Aynı CommandId ise yeniden oynatmadır.
    /// Var olan satırlar değişmez.
    /// </summary>
    [Fact]
    public async Task AddBackup_var_olan_Id_409_content_conflict_baska_lisansta_da()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var parent = await kit.SeedOrderAsync(session);
        var sibling = await kit.SeedOrderAsync(session);
        var other = await Kit.CreateAsync(_factory);
        var foreign = await other.SeedOrderAsync(await other.SeedSessionAsync(Kit.Phase.Released));
        var customer = Kit.BackupCustomer.New();
        var message = $"yorum-{Guid.NewGuid():N}";

        var sameLicense = await kit.SendAsync(kit.AddBackup(Guid.NewGuid(), sibling.Id, parent.Id, customer, message));
        var foreignLicense = await kit.SendAsync(kit.AddBackup(Guid.NewGuid(), foreign.Id, parent.Id, customer, message));

        var newId = Guid.NewGuid();
        var first = kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer, message);
        (await kit.SendAsync(first)).StatusCode.Should().Be(HttpStatusCode.OK);
        var replay = await kit.SendAsync(first);
        var again = await kit.SendAsync(kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer, message));

        foreach (var (resp, id) in new[] { (sameLicense, sibling.Id), (foreignLicense, foreign.Id), (again, newId) })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
            var problem = await Kit.ProblemAsync(resp);
            problem.GetProperty("title").GetString().Should().Be("content-conflict");
            problem.GetProperty("orderId").GetGuid().Should().Be(id);
            problem.TryGetProperty("current", out _).Should().BeFalse("başka lisansın satırı dönmez");
        }
        replay.StatusCode.Should().Be(HttpStatusCode.OK, "aynı komut yeniden oynatılır");
        (await Kit.ReadAsync(replay)).Status.Should().Be("applied");

        (await kit.OrderAsync(sibling.Id)).Should().BeEquivalentTo(new
        {
            IsTentativeBackup = false, ParentOrderId = (Guid?)null, SyncVersion = 1,
        });
        (await kit.OrdersWithIdAsync(foreign.Id)).Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new { LicenseId = other.LicenseId, IsTentativeBackup = false });
        (await kit.OrdersWithIdAsync(newId)).Should().ContainSingle();
        (await kit.LogsAsync()).Should().ContainSingle("reddedilen komut günlüğe girmez");
    }

    /// <summary>
    /// KVKK (§9, §4.2 ekleme kuralı): ayar açıkken asıl projeksiyonu silinmiş müşteriye eklenen yedek metinsiz yazılır
    /// (<c>TextScrubbedAt</c> basılır) — müşteri kopyanın Id'siyle gelse de; ayar kapalıyken (varsayılan) metin kalır.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AddBackup_KVKK_ayar_acikken_silinmis_musterinin_metni_bos_yazilir(bool scrub)
    {
        var kit = await Kit.CreateAsync(scrub ? _factory : _noScrub);
        var parent = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var canonical = Guid.NewGuid();
        var alias = Guid.NewGuid();
        await kit.SeedProjectionAsync(canonical, purged: true);
        await kit.SeedProjectionAsync(alias, mergedInto: canonical);
        var customer = Kit.BackupCustomer.New(alias.ToString("N"));
        var message = $"yorum-{Guid.NewGuid():N}";
        var newId = Guid.NewGuid();
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer, message));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.OrderAsync(newId);
        saved.Username.Should().Be(customer.Username, "kullanıcı adı mali kayıttır, kalır");
        if (scrub)
        {
            saved.DisplayName.Should().BeNull();
            saved.MessageText.Should().BeEmpty();
            saved.TextScrubbedAt.Should().NotBeNull().And.BeOnOrAfter(before);
        }
        else
        {
            saved.DisplayName.Should().Be(customer.DisplayName);
            saved.MessageText.Should().Be(message);
            saved.TextScrubbedAt.Should().BeNull();
        }
    }

    /// <summary>Yük doğrulaması 400 <c>invalid-payload</c>: müşteri eksik ya da alanı boş, yeni Id ebeveynin Id'si,
    /// boş olmayan <c>expected</c>, kolondan uzun alan, eksi fiyat. Bilinmeyen ebeveyn 400 <c>unknown-order</c>.</summary>
    [Fact]
    public async Task AddBackup_gecersiz_yuk_400()
    {
        var kit = await Kit.CreateAsync(_factory);
        var parent = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));
        var customer = Kit.BackupCustomer.New();
        var newId = Guid.NewGuid();

        var invalid = new object[]
        {
            kit.Body(Guid.NewGuid(), "add-backup", new { newOrderId = newId, parentOrderId = parent.Id }, []),
            kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer with { Username = " " }, null),
            kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer with { Id = new string('a', 65) }, null),
            kit.AddBackup(Guid.NewGuid(), parent.Id, parent.Id, customer, null),
            kit.Body(Guid.NewGuid(), "add-backup",
                new { newOrderId = newId, parentOrderId = parent.Id, customer }, [(parent.Id, 1)]),
            kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer, null, code: new string('K', 65)),
            kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer, new string('m', 2001)),
            kit.AddBackup(Guid.NewGuid(), newId, parent.Id, customer, null, price: -1m),
        };
        foreach (var body in invalid)
        {
            var resp = await kit.SendAsync(body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-payload");
        }

        var unknownParent = await kit.SendAsync(kit.AddBackup(Guid.NewGuid(), newId, Guid.NewGuid(), customer, null));
        unknownParent.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await Kit.ProblemAsync(unknownParent)).GetProperty("title").GetString().Should().Be("unknown-order");
        (await kit.OrdersWithIdAsync(newId)).Should().BeEmpty();
    }

    // ── mark-printed ─────────────────────────────────────────────────────────

    /// <summary>
    /// Karışık parti (§4.6, <c>MarkPrintedAndRecord</c> aynası): basılmamış ve iptal edilmemiş sipariş (geçici yedek
    /// dahil) sunucu saatiyle basılır, basış kimliği ve kâğıttaki sırası yazılır, sürüm bir artar → <c>marked</c>; zaten
    /// basılmış sipariş hiç değişmez → <c>alreadyPrinted</c>; iptal edilmiş sipariş basılmaz → <c>cancelled</c>. Baskı
    /// için birim damgası yok.
    /// </summary>
    [Fact]
    public async Task MarkPrinted_karisik_parti_marked_alreadyPrinted_cancelled()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var fresh = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var printed = await kit.SeedOrderAsync(session, o =>
        {
            o.PrintBatchId = "eski-parti";
            o.PrintSeq = 3;
        });
        var cancelled = await kit.SeedOrderAsync(session, o =>
        {
            o.PrintedAt = null;
            o.CancelledAt = Kit.StartedAt.AddMinutes(20);
            o.CancelReason = "customer";
        });
        var tentative = await kit.SeedOrderAsync(session, o =>
        {
            o.PrintedAt = null;
            o.IsTentativeBackup = true;
        });
        var batchId = Guid.NewGuid().ToString("N");
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), batchId, fresh, printed, cancelled, tentative));

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("applied");
        Guids(body.Result, "marked").Should().BeEquivalentTo(new[] { fresh.Id, tentative.Id });
        Guids(body.Result, "alreadyPrinted").Should().Equal(printed.Id);
        Guids(body.Result, "cancelled").Should().Equal(cancelled.Id);
        body.Orders.Select(o => o.Id).Should().BeEquivalentTo(new[] { fresh.Id, printed.Id, cancelled.Id, tentative.Id });

        foreach (var (order, seq) in new[] { (fresh, 1), (tentative, 4) })
        {
            var saved = await kit.OrderAsync(order.Id);
            saved.PrintedAt.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(after);
            saved.Should().BeEquivalentTo(new
            {
                PrintBatchId = batchId, PrintSeq = (int?)seq, SyncVersion = 2, IsTentativeBackup = order.IsTentativeBackup,
                CancelledAt = (DateTimeOffset?)null, PriceSetAt = (DateTimeOffset?)null,
                CancelSetAt = (DateTimeOffset?)null, BackupSetAt = (DateTimeOffset?)null,
                ShipmentSetAt = (DateTimeOffset?)null,
            });
        }
        (await kit.OrderAsync(printed.Id)).Should().BeEquivalentTo(new
        {
            printed.PrintedAt, PrintBatchId = "eski-parti", PrintSeq = (int?)3, SyncVersion = 1,
        });
        (await kit.OrderAsync(cancelled.Id)).Should().BeEquivalentTo(new
        {
            PrintedAt = (DateTimeOffset?)null, PrintBatchId = (string?)null, PrintSeq = (int?)null, SyncVersion = 1,
        });
    }

    /// <summary>
    /// İlk yazan kazanır: aynı parti yeni bir CommandId ve güncel sürümlerle yeniden gönderilirse hepsi
    /// <c>alreadyPrinted</c>, durum <c>unchanged</c>, hiçbir satır değişmez. Eski (baskı öncesi) sürümlerle gönderim 409
    /// <c>stale</c> ve güncel satırlar (istemci taze sürümlerle bir kez yeniden dener, §5.3).
    /// </summary>
    [Fact]
    public async Task MarkPrinted_ayni_parti_yeniden_hepsi_alreadyPrinted_bayat_surum_409_stale()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var a = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var b = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var batchId = Guid.NewGuid().ToString("N");
        (await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), batchId, a, b))).StatusCode.Should().Be(HttpStatusCode.OK);
        var firstA = await kit.OrderAsync(a.Id);
        var firstB = await kit.OrderAsync(b.Id);

        var again = await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), Guid.NewGuid().ToString("N"), firstA, firstB));
        var stale = await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), batchId, a, b));

        again.StatusCode.Should().Be(HttpStatusCode.OK, await again.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(again);
        body.Status.Should().Be("unchanged");
        Guids(body.Result, "alreadyPrinted").Should().BeEquivalentTo(new[] { a.Id, b.Id });
        Guids(body.Result, "marked").Should().BeEmpty();
        Guids(body.Result, "cancelled").Should().BeEmpty();
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await Kit.ProblemAsync(stale);
        problem.GetProperty("title").GetString().Should().Be("stale");
        problem.GetProperty("current").EnumerateArray().Should().HaveCount(2);
        (await kit.OrderAsync(a.Id)).Should().BeEquivalentTo(firstA);
        (await kit.OrderAsync(b.Id)).Should().BeEquivalentTo(firstB);
    }

    /// <summary>
    /// K3 (S1): komutun yazdığı baskıyı sonraki bir push silemez ve kaydıramaz — sahibin v2 push'u (baskısız ya da başka
    /// bir basışla) ve protokolsüz push (başka bir baskı anıyla). İlk baskı anı, basış kimliği ve sıra kalır.
    /// </summary>
    [Fact]
    public async Task MarkPrinted_sonrasi_push_baskiyi_silemez_kaydiramaz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released), o => o.PrintedAt = null);
        var batchId = Guid.NewGuid().ToString("N");
        (await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), batchId, order))).StatusCode.Should().Be(HttpStatusCode.OK);
        var marked = await kit.OrderAsync(order.Id);

        var v2 = await kit.SendToAsync(OrdersUrl(kit), new
        {
            orders = new[] { ItemOf(marked) with { PrintedAt = null, PrintBatchId = "baska-parti", PrintSeq = 9 } },
            format = 2, replicaId = kit.OwnerReplicaId, protocol = 2, seenMode = 1,
        });
        var v1 = await kit.SendToAsync(
            OrdersUrl(kit), new { orders = new[] { ItemOf(marked) with { PrintedAt = marked.PrintedAt!.Value.AddHours(1) } } },
            userAgent: null);

        v2.StatusCode.Should().Be(HttpStatusCode.OK, await v2.Content.ReadAsStringAsync());
        v1.StatusCode.Should().Be(HttpStatusCode.OK, await v1.Content.ReadAsStringAsync());
        (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new
        {
            marked.PrintedAt, PrintBatchId = batchId, PrintSeq = (int?)1,
        });
    }

    /// <summary>Devredilmemiş yayında 409 <c>session-live</c>: canlı yayının baskısını yalnız sahibi yazar (§2 tablosu);
    /// hiçbir şey yazılmaz.</summary>
    [Theory]
    [InlineData(Kit.Phase.Live)]
    [InlineData(Kit.Phase.HandingOver)]
    public async Task MarkPrinted_devredilmemis_yayinda_409_session_live(Kit.Phase phase)
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(phase), o => o.PrintedAt = null);

        var resp = await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), Guid.NewGuid().ToString("N"), order));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("session-live");
        (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new
        {
            PrintedAt = (DateTimeOffset?)null, PrintBatchId = (string?)null, SyncVersion = 1,
        });
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>Yük doğrulaması 400 <c>invalid-payload</c>: 41 karakterlik ya da boş <c>batchId</c> (kolon 40), 1'den
    /// küçük sıra, iki kez verilen sipariş, öğeyi kapsamayan <c>expected</c>. 40 karakterlik <c>batchId</c> geçer.</summary>
    [Fact]
    public async Task MarkPrinted_gecersiz_yuk_400_batchId_en_cok_40()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var order = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);
        var other = await kit.SeedOrderAsync(session, o => o.PrintedAt = null);

        var invalid = new[]
        {
            kit.MarkPrinted(Guid.NewGuid(), new string('b', 41), order),
            kit.MarkPrinted(Guid.NewGuid(), " ", order),
            kit.Body(Guid.NewGuid(), "mark-printed",
                new { batchId = "parti", items = new[] { new { orderId = order.Id, seq = 0 } } },
                [(order.Id, order.SyncVersion)]),
            kit.Body(Guid.NewGuid(), "mark-printed",
                new
                {
                    batchId = "parti",
                    items = new[] { new { orderId = order.Id, seq = 1 }, new { orderId = order.Id, seq = 2 } },
                },
                [(order.Id, order.SyncVersion)]),
            kit.Body(Guid.NewGuid(), "mark-printed",
                new { batchId = "parti", items = new[] { new { orderId = order.Id, seq = 1 } } },
                [(other.Id, other.SyncVersion)]),
        };
        foreach (var body in invalid)
        {
            var resp = await kit.SendAsync(body);
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, await resp.Content.ReadAsStringAsync());
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-payload");
        }
        (await kit.OrderAsync(order.Id)).PrintedAt.Should().BeNull();

        var longest = new string('b', 40);
        var ok = await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), longest, order));
        ok.StatusCode.Should().Be(HttpStatusCode.OK, await ok.Content.ReadAsStringAsync());
        (await kit.OrderAsync(order.Id)).PrintBatchId.Should().Be(longest);
    }

    // ── Bildirim ─────────────────────────────────────────────────────────────

    /// <summary>§10.2, risk 13: etiket komutları bildirim üretmez — yayıncıya ve alıcıya push yok, WhatsApp
    /// <c>OrderReceived</c> etiket kuralı uygulanmaz. Basılmadan gönderilip sonra komutla basılan etiket de (bugün
    /// push'la basılan gibi) bildirim üretmez.</summary>
    [Fact]
    public async Task Etiket_komutlari_bildirim_uretmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (wpfCustomerId, conversationId, shopperId) = await SeedNotifiableCustomerAsync(kit);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var parent = await kit.SeedOrderAsync(session, o => o.CustomerId = wpfCustomerId.ToString("N"));
        var unprinted = await kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = wpfCustomerId.ToString("N");
            o.PrintedAt = null;
        });

        var backup = await kit.SendAsync(kit.AddBackup(
            Guid.NewGuid(), Guid.NewGuid(), parent.Id, Kit.BackupCustomer.New(wpfCustomerId.ToString("N")), null));
        backup.StatusCode.Should().Be(HttpStatusCode.OK, await backup.Content.ReadAsStringAsync());
        var print = await kit.SendAsync(kit.MarkPrinted(Guid.NewGuid(), Guid.NewGuid().ToString("N"), unprinted));
        print.StatusCode.Should().Be(HttpStatusCode.OK, await print.Content.ReadAsStringAsync());

        _factory.Push.Sent.Where(n => n.CustomerId == kit.CustomerId).Should().BeEmpty();
        _factory.Push.SentToShoppers.Where(n => n.ShopperIds.Contains(shopperId)).Should().BeEmpty();
        (await kit.ReadDbAsync(db => db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId)))
            .Should().Be(0, "etiket kuralı uygulanmaz");
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    /// <summary>Telefonu, WhatsApp sohbeti, etiket kuralları ve bildirimi açık bir alıcısı olan müşteri.</summary>
    private static async Task<(Guid WpfCustomerId, Guid ConversationId, Guid ShopperId)> SeedNotifiableCustomerAsync(
        Kit kit)
    {
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
            db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, EventKey = WaLabelEvent.OrderReceived,
                WaLabelId = label.Id, CreatedAt = now,
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
        return (wpfCustomerId, conversationId, shopperId);
    }

    private static List<Guid> Guids(JsonElement result, string property) =>
        result.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).ToList();

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
        BaseVersion: o.SyncVersion,
        PrintBatchId: o.PrintBatchId,
        PrintSeq: o.PrintSeq);

    private static string OrdersUrl(Kit kit) => $"/api/v1/licenses/{kit.LicenseId}/orders/sync";
}
