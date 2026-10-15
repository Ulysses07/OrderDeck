using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Sunucuya ait kargo komutları gerçek SQL Server'da — spec 2026-10-13 §10.1-6, -7, -19, -27: müşteri kilidi
/// (<c>ship:{lisans}:{müşteri}</c>), sipariş ve dosya jetonları, kargoya verirken iptalli bağın çözülmesi ve onarım işinin
/// (c) adımı. InMemory işlem, kilit ve jeton uygulamadığı için yarışın hakemi burada kanıtlanır.
/// </summary>
public sealed partial class SalesCommandsRelationalTests
{
    // ── §10.1-6: aynı müşteriye eşzamanlı iki attach-shipment ────────────────

    /// <summary>
    /// §10.1-6: aynı müşteriye, aynı aday kümesini görmüş, farklı dosya Id'leri öneren eşzamanlı iki
    /// <c>attach-shipment</c>. Engel, kaydetmesine gelen isteği öbürü de gelene kadar (en çok 1,5 sn — kilit bekleme
    /// sınırının altında) bekletir. Müşteri kilidi ikinciyi kilitte tutar: o, kümeyi birincinin yazımından SONRA okur,
    /// adayların bağlandığını görür ve 409 <c>stale</c> alır (güncel küme boş) — jetona çarpmaz. Tek dosya açılır; bütün
    /// adaylar ona bağlıdır.
    /// </summary>
    [Fact]
    public async Task Ayni_musteriye_eszamanli_iki_attach_shipment_tek_dosya_obur_409_stale()
    {
        var customer = Guid.NewGuid().ToString("N");
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var k1 = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.Price = 300m; });
        var k2 = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.Price = 250m; });
        var proposals = new[] { Guid.NewGuid(), Guid.NewGuid() };
        _factory.Barrier.Arm(db => db.ChangeTracker.Entries<SalesCommandLog>().Any(), TimeSpan.FromSeconds(1.5));

        var responses = await Task.WhenAll(proposals.Select(p => _kit.SendAsync(
            _kit.AttachShipment(Guid.NewGuid(), customer, p, [k1, k2], replicaId: Guid.NewGuid()))));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict], string.Join(" | ", bodies));
        var winner = proposals[responses[0].StatusCode == HttpStatusCode.OK ? 0 : 1];
        var problem = await Kit.ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict));
        problem.GetProperty("title").GetString().Should().Be("stale");
        problem.GetProperty("candidates").GetArrayLength().Should().Be(0, "kilit kümeyi yazımdan sonra okutur");

        var file = (await _kit.ShipmentsOfAsync(customer)).Should().ContainSingle().Subject;
        file.Id.Should().Be(winner);
        file.CumulativeAmount.Should().Be(550m);
        foreach (var order in new[] { k1, k2 })
            (await _kit.OrderAsync(order.Id)).Should().BeEquivalentTo(
                new { ShipmentId = (Guid?)winner, SyncVersion = 2 });
        (await _kit.LogsAsync()).Should().ContainSingle();
    }

    // ── §10.1-7: aynı siparişe eşzamanlı cancel ve attach-shipment ───────────

    /// <summary>
    /// §10.1-7, DM7: aynı bağlanmamış siparişe eşzamanlı <c>cancel</c> ve <c>attach-shipment</c> (iki kopyadan). Engel
    /// ikisini de siparişi okuduktan sonra kaydetmede buluşturur; iptal müşteri kilidini almaz. Sipariş jetonu hakemdir:
    /// tam olarak biri 409 <c>stale</c>, öbürünün yazımı kalır. Hangisi kazanırsa kazansın dosyanın tutarı veritabanındaki
    /// bağlı, iptal edilmemiş siparişlerin toplamıdır: iptal kazandıysa sipariş bağsız ve tutar onsuz, bağlama
    /// kazandıysa sipariş bağlı, iptal edilmemiş ve tutarda.
    /// </summary>
    [Fact]
    public async Task Ayni_siparise_eszamanli_cancel_ve_attach_biri_409_dosya_tutari_DM7_saglar()
    {
        var customer = Guid.NewGuid().ToString("N");
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var file = await _kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 300m, derived: true);
        await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = file.Id; o.Price = 300m; });
        var k5 = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.Price = 250m; });
        _factory.Barrier.Arm(db => db.ChangeTracker.Entries<SalesCommandLog>().Any(), TimeSpan.FromSeconds(1.5));

        var responses = await Task.WhenAll(
            _kit.SendAsync(_kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), [k5], replicaId: Guid.NewGuid())),
            _kit.SendAsync(_kit.Body(Guid.NewGuid(), "cancel", new { items = new[] { k5.Id }, reason = "customer" },
                [(k5.Id, k5.SyncVersion)], replicaId: Guid.NewGuid())));

        var bodies = await Task.WhenAll(responses.Select(r => r.Content.ReadAsStringAsync()));
        responses.Select(r => r.StatusCode).Should().BeEquivalentTo(
            [HttpStatusCode.OK, HttpStatusCode.Conflict], string.Join(" | ", bodies));
        (await Kit.ProblemAsync(responses.Single(r => r.StatusCode == HttpStatusCode.Conflict)))
            .GetProperty("title").GetString().Should().Be("stale");
        var attachWon = responses[0].StatusCode == HttpStatusCode.OK;

        var saved = await _kit.ShipmentAsync(file.Id);
        saved.CumulativeAmount.Should().Be(await _kit.DerivedAmountInDbAsync(file.Id), "DM7");
        var order = await _kit.OrderAsync(k5.Id);
        if (attachWon)
        {
            order.ShipmentId.Should().Be(file.Id);
            order.CancelledAt.Should().BeNull();
            saved.CumulativeAmount.Should().Be(550m);
        }
        else
        {
            order.ShipmentId.Should().BeNull();
            order.CancelledAt.Should().NotBeNull();
            saved.CumulativeAmount.Should().Be(300m);
        }
        order.SyncVersion.Should().Be(2, "yalnız kazanan yazdı");
        (await _kit.ShipmentsOfAsync(customer)).Should().ContainSingle();
        (await _kit.LogsAsync()).Should().ContainSingle();
    }

    // ── §10.1-19: kargoya verme, iptal ve geri alma, onarım işi ──────────────

    /// <summary>
    /// §10.1-19, §7.2 (Y5): Shipped olmayan dosyada iptal ve geri alma bağı ve ödenen fiyatı korur, tutar düşer ve
    /// döner. Etiket iptalliyken dosya kargoya verilirse bağı çözülür ve dosya onsuz donar; iptal sonra geri alınınca
    /// sipariş bağsız kalır ve sonraki <c>attach-shipment</c> onu YENİ bir dosyaya koyar. Kargolanmış dosyaya bağlı
    /// siparişin sonradan iptali donmuş tutarı değiştirmez; <c>sales-release-repair</c>'ın (c) adımı da ona dokunmaz.
    /// </summary>
    [Fact]
    public async Task Kargoya_verme_iptalli_bagi_cozer_geri_alma_yeni_dosyaya_gider_onarim_donmus_tutara_dokunmaz()
    {
        var customer = Guid.NewGuid().ToString("N");
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var file = await _kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 550m, derived: true);
        var k1 = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = file.Id; o.Price = 300m; });
        var k5 = await _kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.ShipmentId = file.Id;
            o.Price = 250m;
            o.PaidPrice = 250m;
        });

        await SendOkAsync(_kit.Cancel(Guid.NewGuid(), "customer", await _kit.OrderAsync(k5.Id)));
        (await _kit.ShipmentAsync(file.Id)).CumulativeAmount.Should().Be(300m);
        await SendOkAsync(_kit.Uncancel(Guid.NewGuid(), await _kit.OrderAsync(k5.Id)));
        (await _kit.ShipmentAsync(file.Id)).CumulativeAmount.Should().Be(550m);
        (await _kit.OrderAsync(k5.Id)).Should().BeEquivalentTo(
            new { ShipmentId = (Guid?)file.Id, PaidPrice = (decimal?)250m }, "geri alma bağı ve ödendi bilgisini korur");

        await SendOkAsync(_kit.Cancel(Guid.NewGuid(), "customer", await _kit.OrderAsync(k5.Id)));
        await SendOkAsync(_kit.ShipmentDecision(
            Guid.NewGuid(), file.Id, "ship-now", (await _kit.ShipmentAsync(file.Id)).Version));

        var shipped = await _kit.ShipmentAsync(file.Id);
        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        shipped.CumulativeAmount.Should().Be(300m, "iptalli etiket kolide değil, tutar onsuz donar");
        (await _kit.OrderAsync(k5.Id)).Should().BeEquivalentTo(
            new { ShipmentId = (Guid?)null, ShipmentSetAt = shipped.CommandedAt });
        (await _kit.OrderAsync(k1.Id)).ShipmentId.Should().Be(file.Id);

        await SendOkAsync(_kit.Uncancel(Guid.NewGuid(), await _kit.OrderAsync(k5.Id)));
        (await _kit.OrderAsync(k5.Id)).ShipmentId.Should().BeNull("geri alma bağı geri getirmez");
        (await _kit.ShipmentAsync(file.Id)).CumulativeAmount.Should().Be(300m);

        var next = Guid.NewGuid();
        var attach = await Kit.ReadAsync(
            await SendOkAsync(_kit.AttachShipment(Guid.NewGuid(), customer, next, [await _kit.OrderAsync(k5.Id)])));
        attach.Result.GetProperty("shipmentId").GetGuid().Should().Be(next);
        (await _kit.OrderAsync(k5.Id)).ShipmentId.Should().Be(next);
        (await _kit.ShipmentAsync(next)).Should().BeEquivalentTo(
            new { Status = ShipmentStatus.Pending, CumulativeAmount = 250m });

        await SendOkAsync(_kit.Cancel(Guid.NewGuid(), "customer", await _kit.OrderAsync(k1.Id)));
        var frozen = await _kit.ShipmentAsync(file.Id);
        frozen.CumulativeAmount.Should().Be(300m, "kargolanmış dosyanın tutarı donmuştur");
        (await _kit.DerivedAmountInDbAsync(file.Id)).Should().Be(0m, "ön koşul: türetilen değer farklı");

        using (var scope = _factory.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<SalesReleaseRepairJob>().RunAsync(default);

        (await _kit.ShipmentAsync(file.Id)).Should().BeEquivalentTo(
            new { CumulativeAmount = 300m, frozen.Version, Status = ShipmentStatus.Shipped });
        (await _kit.ShipmentAsync(next)).CumulativeAmount.Should().Be(250m);
    }

    // ── §10.1-27: kargo bağı bilinmeyen sipariş ve kabul edilmiş dosya ───────

    /// <summary>
    /// §10.1-27, M22, §15-44, §15-53: <c>OriginReplicaId</c>'si boş sipariş aday değildir (onu bekleyen küme 409
    /// <c>stale</c>, güncel küme onsuz). <c>--accept-shipment</c> ile kabul edilmiş (türetilmemiş) açık dosya seçilmez —
    /// müşteriye yeni türetilmiş dosya açılır — ve türetilmez: saklanan tutarı bağlarından farklı olsa da değişmez, ona
    /// bağlı siparişin iptali de (K7) dokunmaz. Havuz onu saklanan tutarıyla sayar.
    /// </summary>
    [Fact]
    public async Task Attach_kaynaksiz_siparisi_aday_saymaz_kabul_edilmis_dosyayi_secmez_turetmez()
    {
        var customer = Guid.NewGuid().ToString("N");
        var session = await _kit.SeedSessionAsync(Kit.Phase.Released);
        var accepted = await _kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 400m, derived: false);
        var linkedToAccepted = await _kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.ShipmentId = accepted.Id;
            o.Price = 100m;
        });
        var unknownLink = await _kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.Price = 500m;
            o.OriginReplicaId = null;
        });
        var k1 = await _kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.Price = 300m; });

        var stale = await _kit.SendAsync(_kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), [k1, unknownLink]));

        stale.StatusCode.Should().Be(HttpStatusCode.Conflict, await stale.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(stale);
        problem.GetProperty("title").GetString().Should().Be("stale");
        problem.GetProperty("candidates").EnumerateArray().Select(c => c.GetProperty("orderId").GetGuid())
            .Should().Equal(k1.Id);

        var proposed = Guid.NewGuid();
        var reply = await Kit.ReadAsync(await SendOkAsync(_kit.AttachShipment(Guid.NewGuid(), customer, proposed, [k1])));

        reply.Result.GetProperty("shipmentId").GetGuid().Should().Be(proposed, "kabul edilmiş dosya seçilmez");
        reply.Result.GetProperty("pool").GetDecimal().Should().Be(400m + 300m);
        var opened = await _kit.ShipmentAsync(proposed);
        opened.Should().BeEquivalentTo(new { Status = ShipmentStatus.Pending, CumulativeAmount = 300m });
        opened.AmountDerivedAt.Should().NotBeNull();
        (await _kit.OrderAsync(unknownLink.Id)).Should().BeEquivalentTo(
            new { ShipmentId = (Guid?)null, SyncVersion = 1 });
        (await _kit.ShipmentAsync(accepted.Id)).Should().BeEquivalentTo(new
        {
            CumulativeAmount = 400m,
            accepted.Version,
            AmountDerivedAt = (DateTimeOffset?)null,
            CommandedAt = (DateTimeOffset?)null,
        });

        await SendOkAsync(_kit.Cancel(Guid.NewGuid(), "customer", linkedToAccepted));

        (await _kit.ShipmentAsync(accepted.Id)).Should().BeEquivalentTo(
            new { CumulativeAmount = 400m, accepted.Version }, "kabul edilmiş dosya türetilmez");
    }

    private async Task<HttpResponseMessage> SendOkAsync(object body)
    {
        var resp = await _kit.SendAsync(body);
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        return resp;
    }
}
