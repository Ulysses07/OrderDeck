using System.Net;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// <c>confirm-backup</c> — spec 2026-10-13 §4.6, §7.2, K6, DM7 (InMemory): geçici yedeğin onayı, yeni fiyatla onay,
/// geçici olmayan siparişte etkisizlik ve retler (görünürlük, kargoya verilmiş dosyada fiyat, yük).
/// </summary>
public sealed class SalesBackupCommandsTests : IClassFixture<SalesSyncConfiguredApiFactory>
{
    private readonly SalesSyncConfiguredApiFactory _factory;

    public SalesBackupCommandsTests(SalesSyncConfiguredApiFactory factory) => _factory = factory;

    /// <summary>
    /// <c>confirm-backup</c> (§4.6, <c>ConfirmBackup</c> aynası): geçici yedek onaylanır, <c>BackupSetAt</c> sunucu saati,
    /// sürüm bir artar; fiyat verilmediyse fiyat ve <c>PriceSetAt</c> değişmez. Onaylanan yedek artık satıştır: ürünü
    /// biliniyorsa defter satışı düşer.
    /// </summary>
    [Fact]
    public async Task ConfirmBackup_gecici_yedegi_onaylar_damgalar_defteri_yazar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var productId = await kit.SeedProductAsync();
        var backup = await kit.SeedOrderAsync(session, o =>
        {
            o.IsTentativeBackup = true;
            o.IsBackupPromoted = true;
            o.ProductId = productId;
        });
        (await kit.MovementsAsync(backup.Id)).Should().BeEmpty("ön koşul: geçici yedek defterde yok");
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), backup));

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(resp)).Status.Should().Be("applied");
        var saved = await kit.OrderAsync(backup.Id);
        saved.IsTentativeBackup.Should().BeFalse();
        saved.BackupSetAt.Should().NotBeNull().And.BeOnOrAfter(before).And.BeOnOrBefore(after);
        saved.Price.Should().Be(100m);
        saved.PriceSetAt.Should().BeNull("fiyat değişmedi");
        saved.SyncVersion.Should().Be(2);
        (await kit.MovementsAsync(backup.Id)).Select(m => (m.Quantity, m.Reason))
            .Should().Equal((-1, StockMovementReason.Sale));
    }

    /// <summary>
    /// Yeni fiyatla onay: <c>Price</c> ve <c>PriceSetAt</c> (onayla aynı an); <c>PriceCorrectedAt</c> ve
    /// <c>PaidPrice</c> yazılmaz. Bağlı türetilmiş açık dosya (olağan değil) onaylanan yedeği tutara katar (K7, DM7).
    /// Aynı fiyat verilirse fiyat birimi değişmez.
    /// </summary>
    [Fact]
    public async Task ConfirmBackup_yeni_fiyatla_fiyat_damgasi_ve_dosya_turetmesi()
    {
        var kit = await Kit.CreateAsync(_factory);
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var customer = Guid.NewGuid().ToString("N");
        var open = await kit.SeedShipmentAsync(customer, ShipmentStatus.Pending, 100m, derived: true, version: 1);
        await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = open.Id; });
        var backup = await kit.SeedOrderAsync(session, o =>
        {
            o.IsTentativeBackup = true;
            o.CustomerId = customer;
            o.ShipmentId = open.Id;
        });
        var samePrice = await kit.SeedOrderAsync(session, o => o.IsTentativeBackup = true);

        var resp = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), backup, 80m));
        var same = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), samePrice, 100m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var saved = await kit.OrderAsync(backup.Id);
        saved.Should().BeEquivalentTo(new
        {
            IsTentativeBackup = false, Price = 80m, PriceCorrectedAt = (DateTimeOffset?)null,
            PaidPrice = (decimal?)null, SyncVersion = 2,
        });
        saved.PriceSetAt.Should().NotBeNull().And.Be(saved.BackupSetAt);
        (await kit.ShipmentAsync(open.Id)).Should().BeEquivalentTo(new { CumulativeAmount = 180m, Version = 2 });

        same.StatusCode.Should().Be(HttpStatusCode.OK, await same.Content.ReadAsStringAsync());
        var unchangedPrice = await kit.OrderAsync(samePrice.Id);
        unchangedPrice.IsTentativeBackup.Should().BeFalse();
        unchangedPrice.BackupSetAt.Should().NotBeNull();
        unchangedPrice.PriceSetAt.Should().BeNull("aynı fiyat fiyat birimine dokunmaz");
    }

    /// <summary>Geçici yedek olmayan sipariş: etkisiz — durum <c>unchanged</c>, damga yok, sürüm ilerlemez; fiyat
    /// verilse de yazılmaz.</summary>
    [Fact]
    public async Task ConfirmBackup_gecici_olmayan_sipariste_etkisiz()
    {
        var kit = await Kit.CreateAsync(_factory);
        var order = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released));

        var resp = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), order, 80m));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var body = await Kit.ReadAsync(resp);
        body.Status.Should().Be("unchanged");
        Guids(body.Result, "skipped").Should().Equal(order.Id);
        (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new
        {
            Price = 100m, SyncVersion = 1, BackupSetAt = (DateTimeOffset?)null, PriceSetAt = (DateTimeOffset?)null,
        });
        (await kit.LogsAsync()).Should().ContainSingle().Which.Status.Should().Be("unchanged");
    }

    /// <summary>
    /// Ret: devredilmemiş yayın 409 <c>session-live</c>; kargoya verilmiş dosyaya bağlı yedeğin fiyatı değişmez (K6) — 409
    /// <c>shipment-shipped</c>, hiçbir şey yazılmaz (fiyatsız onay geçer); eksi fiyat ve yedeği kapsamayan
    /// <c>expected</c> 400.
    /// </summary>
    [Fact]
    public async Task ConfirmBackup_retleri()
    {
        var kit = await Kit.CreateAsync(_factory);
        var released = await kit.SeedSessionAsync(Kit.Phase.Released);
        var live = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Live), o => o.IsTentativeBackup = true);
        var customer = Guid.NewGuid().ToString("N");
        var shipped = await kit.SeedShipmentAsync(customer, ShipmentStatus.Shipped, 100m, derived: true, version: 3);
        var inShipped = await kit.SeedOrderAsync(released, o =>
        {
            o.IsTentativeBackup = true;
            o.CustomerId = customer;
            o.ShipmentId = shipped.Id;
        });
        var other = await kit.SeedOrderAsync(released, o => o.IsTentativeBackup = true);

        var liveResp = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), live));
        var shippedResp = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), inShipped, 80m));
        var negative = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), other, -1m));
        var notCovering = await kit.SendAsync(kit.Body(
            Guid.NewGuid(), "confirm-backup", new { backupOrderId = other.Id }, [(inShipped.Id, 1)]));

        liveResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await Kit.ProblemAsync(liveResp)).GetProperty("title").GetString().Should().Be("session-live");
        shippedResp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await Kit.ProblemAsync(shippedResp);
        problem.GetProperty("title").GetString().Should().Be("shipment-shipped");
        problem.GetProperty("shipmentId").GetGuid().Should().Be(shipped.Id);
        problem.GetProperty("current").EnumerateArray().Should().ContainSingle();
        foreach (var resp in new[] { negative, notCovering })
        {
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await Kit.ProblemAsync(resp)).GetProperty("title").GetString().Should().Be("invalid-payload");
        }
        foreach (var order in new[] { live, inShipped, other })
            (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new
            {
                IsTentativeBackup = true, Price = 100m, SyncVersion = 1, BackupSetAt = (DateTimeOffset?)null,
            });
        (await kit.LogsAsync()).Should().BeEmpty();

        var withoutPrice = await kit.SendAsync(kit.ConfirmBackup(Guid.NewGuid(), inShipped));
        withoutPrice.StatusCode.Should().Be(HttpStatusCode.OK, await withoutPrice.Content.ReadAsStringAsync());
        (await kit.OrderAsync(inShipped.Id)).IsTentativeBackup.Should().BeFalse();
        (await kit.ShipmentAsync(shipped.Id)).Should().BeEquivalentTo(new { CumulativeAmount = 100m, Version = 3 },
            "Shipped dosyanın tutarı donmuştur");
    }

    private static List<Guid> Guids(JsonElement result, string property) =>
        result.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).ToList();
}
