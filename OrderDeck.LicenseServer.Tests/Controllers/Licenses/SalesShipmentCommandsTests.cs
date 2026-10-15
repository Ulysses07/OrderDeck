using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.WhatsApp;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;
using Kit = OrderDeck.LicenseServer.Tests.Controllers.Licenses.SalesCommandTestKit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

/// <summary>
/// Sunucuya ait kargo komutları — spec 2026-10-13 §4.6 (<c>attach-shipment</c>), §7.1, DM7, M22, §10.2 (InMemory).
/// Eşzamanlılık (§10.1-6, -7) ve kabul edilmiş dosya (§10.1-27) gerçek SQL Server'da
/// (<see cref="SalesCommandsRelationalTests"/>).
/// </summary>
public sealed class SalesShipmentCommandsTests : IClassFixture<SalesSyncConfiguredApiFactory>
{
    private static readonly DateTimeOffset StartedAt = Kit.StartedAt;

    private readonly SalesSyncConfiguredApiFactory _factory;

    public SalesShipmentCommandsTests(SalesSyncConfiguredApiFactory factory) => _factory = factory;

    // ── attach-shipment ──────────────────────────────────────────────────────

    /// <summary>
    /// Açık dosyası olmayan müşteri (§4.6, yerel <c>GetOrCreateOpenShipment</c> + <c>AttachLabels</c>): komut
    /// <c>proposedShipmentId</c> ile tek bir türetilmiş dosya açar — Pending, sunucu saatiyle oluşum ve türetme anı, kaynak
    /// kopya çağıran, müşteri asıl kayıt — ve adayları bağlar (kargo ücreti satırı dahil; damga <c>ShipmentSetAt</c>,
    /// sürüm bir artar). Tutar bağlardan türetilir (DM7), dosyanın sürümü bir kez artar, <c>CommandedAt</c> basılır.
    /// Sonuç dosya ve havuz; yanıtın <c>shipments</c>'ı havuzun dosyaları. Yeniden oynatma ikinci dosya açmaz.
    /// </summary>
    [Fact]
    public async Task Attach_acik_dosyasi_olmayan_musteriye_turetilmis_tek_dosya_acar()
    {
        var kit = await Kit.CreateAsync(_factory);
        var customer = NewCustomer();
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var k1 = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.Price = 300m; });
        var fee = await kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.Price = 60m;
            o.IsShippingFee = true;
            o.PrintedAt = null;
        });
        var proposed = Guid.NewGuid();
        var body = kit.AttachShipment(Guid.NewGuid(), customer, proposed, [k1, fee]);
        var before = DateTimeOffset.UtcNow;

        var resp = await kit.SendAsync(body);

        var after = DateTimeOffset.UtcNow;
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var reply = await Kit.ReadAsync(resp);
        reply.Status.Should().Be("applied");
        reply.Result.GetProperty("shipmentId").GetGuid().Should().Be(proposed);
        reply.Result.GetProperty("pool").GetDecimal().Should().Be(360m);
        Guids(reply.Result, "poolShipmentIds").Should().Equal(proposed);
        Guids(reply.Result, "changed").Should().BeEquivalentTo([k1.Id, fee.Id]);
        reply.Orders.Should().OnlyContain(o => o.ShipmentId == proposed && o.Version == 2 && o.ShipmentSetAt != null);
        var item = reply.Shipments.Should().ContainSingle().Subject;
        item.Should().BeEquivalentTo(new
        {
            Id = proposed,
            CustomerId = customer,
            Status = "pending",
            CumulativeAmount = 360m,
            Version = 1,
            OriginReplicaId = (Guid?)kit.ReplicaId,
        });

        var saved = await kit.ShipmentAsync(proposed);
        saved.LicenseId.Should().Be(kit.LicenseId);
        saved.CreatedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after);
        saved.AmountDerivedAt.Should().Be(saved.CreatedAt);
        saved.CommandedAt.Should().Be(saved.CreatedAt);
        saved.UpdatedAt.Should().Be(saved.CreatedAt);
        saved.HeldAt.Should().BeNull();
        saved.ShippedAt.Should().BeNull();
        foreach (var order in new[] { k1, fee })
            (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(new
            {
                ShipmentId = (Guid?)proposed,
                ShipmentSetAt = saved.CommandedAt,
                SyncVersion = 2,
                CancelledAt = (DateTimeOffset?)null,
            });
        var log = (await kit.LogsAsync()).Should().ContainSingle().Subject;
        log.Kind.Should().Be("attach-shipment");
        log.Status.Should().Be("applied");

        var replay = await kit.SendAsync(body);

        replay.StatusCode.Should().Be(HttpStatusCode.OK, await replay.Content.ReadAsStringAsync());
        var replayed = await Kit.ReadAsync(replay);
        replayed.Result.GetRawText().Should().Be(reply.Result.GetRawText());
        replayed.Shipments.Select(s => s.Id).Should().Equal(proposed);
        (await kit.ShipmentsOfAsync(customer)).Should().ContainSingle("yeniden oynatma ikinci dosya açmaz");
        (await kit.ShipmentAsync(proposed)).Version.Should().Be(1);
    }

    /// <summary>
    /// Hedef, müşterinin en yeni TÜRETİLMİŞ açık (Pending/Held) dosyasıdır — masaüstündeki sıra: <c>CreatedAt</c> azalan,
    /// eşitlikte "N" biçimli Id azalan. Daha yeni olsa da kabul edilmiş (türetilmemiş, §4.10-6), kargolanmış ya da alıcı
    /// ödemeli dosya seçilmez; önerilen Id kullanılmaz. Havuz bütün Pending/Held dosyalardır: kabul edilmiş dosya saklanan
    /// tutarıyla girer; kargolanmış ve alıcı ödemeli girmez. Seçilmeyen dosyalar değişmez.
    /// </summary>
    [Fact]
    public async Task Attach_en_yeni_turetilmis_acik_dosyayi_kullanir_kabul_edilmis_ve_kapali_dosyalari_secmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var customer = NewCustomer();
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var older = await kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 100m, derived: true,
            createdAt: StartedAt.AddHours(3));
        await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = older.Id; });
        var tied = new List<Shipment>();
        for (var i = 0; i < 2; i++)
            tied.Add(await kit.SeedShipmentAsync(customer, ShipmentStatus.Pending, 0m, derived: true,
                createdAt: StartedAt.AddHours(4)));
        var accepted = await kit.SeedShipmentAsync(customer, ShipmentStatus.Pending, 500m, derived: false,
            createdAt: StartedAt.AddHours(6));
        var shipped = await kit.SeedShipmentAsync(customer, ShipmentStatus.Shipped, 70m, derived: true,
            createdAt: StartedAt.AddHours(7));
        var recipientPays = await kit.SeedShipmentAsync(customer, ShipmentStatus.RecipientPays, 80m, derived: true,
            createdAt: StartedAt.AddHours(8));
        var k1 = await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.Price = 300m; });
        var proposed = Guid.NewGuid();
        var target = tied.OrderByDescending(s => s.Id.ToString("N"), StringComparer.Ordinal).First();
        var otherTied = tied.Single(s => s.Id != target.Id);

        var resp = await kit.SendAsync(kit.AttachShipment(Guid.NewGuid(), customer, proposed, [k1]));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var reply = await Kit.ReadAsync(resp);
        reply.Result.GetProperty("shipmentId").GetGuid().Should().Be(target.Id);
        reply.Result.GetProperty("pool").GetDecimal().Should().Be(100m + 0m + 300m + 500m);
        Guids(reply.Result, "poolShipmentIds").Should().Equal(accepted.Id, target.Id, otherTied.Id, older.Id);
        reply.Shipments.Select(s => s.Id).Should().BeEquivalentTo([accepted.Id, target.Id, otherTied.Id, older.Id]);
        (await kit.OrderAsync(k1.Id)).ShipmentId.Should().Be(target.Id);
        (await kit.ShipmentAsync(target.Id)).Should().BeEquivalentTo(
            new { CumulativeAmount = 300m, Version = 2, Status = ShipmentStatus.Pending });
        (await kit.ShipmentAsync(target.Id)).CommandedAt.Should().NotBeNull();
        foreach (var untouched in new[] { older, otherTied, accepted, shipped, recipientPays })
            (await kit.ShipmentAsync(untouched.Id)).Should().BeEquivalentTo(
                new { untouched.CumulativeAmount, untouched.Version, CommandedAt = (DateTimeOffset?)null });
        (await kit.ShipmentsOfAsync(customer)).Should().NotContain(s => s.Id == proposed);
    }

    /// <summary>
    /// Aday süzgeci (§4.6, M22, §4.6 adım 3): bağsız, iptal edilmemiş, geçici yedek olmayan ve kargo bağı bilinen
    /// (<c>OriginReplicaId</c> dolu) siparişler; yayını devredilmiş ya da çağıranın kendi yayını (canlı olsa da). İptal
    /// edilmiş, geçici yedek, başka dosyaya bağlı, kaynaksız, başka kopyanın canlı ya da devrediliyor yayınındaki,
    /// yayınsız ve başka müşterinin siparişi aday değildir ve dokunulmaz.
    /// </summary>
    [Fact]
    public async Task Attach_aday_suzgeci_cagiranin_canli_yayinini_alir_obur_satirlari_disarida_birakir()
    {
        var kit = await Kit.CreateAsync(_factory);
        var customer = NewCustomer();
        var released = await kit.SeedSessionAsync(Kit.Phase.Released);
        var callerLive = await kit.SeedSessionAsync(Kit.Phase.Live, owner: kit.ReplicaId);
        var otherLive = await kit.SeedSessionAsync(Kit.Phase.Live);
        var otherHandingOver = await kit.SeedSessionAsync(Kit.Phase.HandingOver);
        var shipped = await kit.SeedShipmentAsync(customer, ShipmentStatus.Shipped, 100m, derived: true);
        var included = new[]
        {
            await kit.SeedOrderAsync(released, o => o.CustomerId = customer),
            await kit.SeedOrderAsync(callerLive, o => { o.CustomerId = customer; o.PrintedAt = null; }),
        };
        var excluded = new[]
        {
            await kit.SeedOrderAsync(released, o =>
            {
                o.CustomerId = customer;
                o.CancelledAt = StartedAt.AddHours(1);
                o.CancelReason = "customer";
            }),
            await kit.SeedOrderAsync(released, o => { o.CustomerId = customer; o.IsTentativeBackup = true; }),
            await kit.SeedOrderAsync(released, o => { o.CustomerId = customer; o.ShipmentId = shipped.Id; }),
            await kit.SeedOrderAsync(released, o => { o.CustomerId = customer; o.OriginReplicaId = null; }),
            await kit.SeedOrderAsync(otherLive, o => o.CustomerId = customer),
            await kit.SeedOrderAsync(otherHandingOver, o => o.CustomerId = customer),
            await kit.SeedOrderAsync(null, o => o.CustomerId = customer),
            await kit.SeedOrderAsync(released),
        };

        var resp = await kit.SendAsync(kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), included));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var shipmentId = (await Kit.ReadAsync(resp)).Result.GetProperty("shipmentId").GetGuid();
        foreach (var order in included)
            (await kit.OrderAsync(order.Id)).ShipmentId.Should().Be(shipmentId);
        foreach (var order in excluded)
            (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(
                new { order.ShipmentId, SyncVersion = 1, ShipmentSetAt = (DateTimeOffset?)null });
        (await kit.ShipmentAsync(shipmentId)).CumulativeAmount.Should().Be(200m);
    }

    /// <summary>
    /// Aday kümesi (Id ve sürüm) <c>expected</c>'den farklıysa — eksik, aday olmayan fazla bir satır ya da bayat sürüm —
    /// 409 <c>stale</c>: yanıt sunucunun güncel kümesini (<c>candidates</c>) ve adayların ve beklenen satırların güncel
    /// hâlini (<c>current</c>) taşır. Hiçbir şey yazılmaz: dosya açılmaz, bağ ve günlük yok.
    /// </summary>
    [Fact]
    public async Task Attach_aday_kumesi_beklenenden_farkliysa_409_stale_guncel_kume_yazim_yok()
    {
        var kit = await Kit.CreateAsync(_factory);
        var customer = NewCustomer();
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var a = await kit.SeedOrderAsync(session, o => o.CustomerId = customer);
        var b = await kit.SeedOrderAsync(session, o => o.CustomerId = customer);
        var cancelled = await kit.SeedOrderAsync(session, o =>
        {
            o.CustomerId = customer;
            o.CancelledAt = StartedAt.AddHours(1);
        });
        var expectedSets = new (string Why, object Body)[]
        {
            ("eksik", kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), [a])),
            ("aday olmayan fazla", kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), [a, b, cancelled])),
            ("bayat sürüm", kit.Body(Guid.NewGuid(), "attach-shipment",
                new { customerId = customer, proposedShipmentId = Guid.NewGuid() }, [(a.Id, 0), (b.Id, 1)])),
        };

        foreach (var (why, body) in expectedSets)
        {
            var resp = await kit.SendAsync(body);

            resp.StatusCode.Should().Be(HttpStatusCode.Conflict, why);
            var problem = await Kit.ProblemAsync(resp);
            problem.GetProperty("title").GetString().Should().Be("stale", why);
            problem.GetProperty("candidates").EnumerateArray()
                .Select(c => (c.GetProperty("orderId").GetGuid(), c.GetProperty("version").GetInt32()))
                .Should().Equal(new[] { (a.Id, 1), (b.Id, 1) }.OrderBy(c => c.Item1), why);
            problem.GetProperty("current").EnumerateArray().Select(o => o.GetProperty("id").GetGuid())
                .Should().Contain([a.Id, b.Id], why);
        }

        (await kit.ShipmentsOfAsync(customer)).Should().BeEmpty();
        foreach (var order in new[] { a, b })
            (await kit.OrderAsync(order.Id)).Should().BeEquivalentTo(
                new { ShipmentId = (Guid?)null, SyncVersion = 1 });
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>
    /// Bağlanacak aday yoksa ve istemci de boş küme gördüyse komut etkisizdir (<c>unchanged</c>): dosya açılmaz, önerilen
    /// Id kullanılmaz. Sonuç yine havuzu (kabul edilmiş dosya saklanan tutarıyla) ve dosyalarını taşır.
    /// </summary>
    [Fact]
    public async Task Attach_aday_yoksa_etkisiz_dosya_acilmaz_havuz_doner()
    {
        var kit = await Kit.CreateAsync(_factory);
        var customer = NewCustomer();
        var accepted = await kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 200m, derived: false);
        var proposed = Guid.NewGuid();

        var resp = await kit.SendAsync(kit.AttachShipment(Guid.NewGuid(), customer, proposed, []));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        var reply = await Kit.ReadAsync(resp);
        reply.Status.Should().Be("unchanged");
        reply.Result.TryGetProperty("shipmentId", out _).Should().BeFalse("dosya açılmadı");
        reply.Result.GetProperty("pool").GetDecimal().Should().Be(200m);
        Guids(reply.Result, "poolShipmentIds").Should().Equal(accepted.Id);
        reply.Shipments.Select(s => s.Id).Should().Equal(accepted.Id);
        reply.Orders.Should().BeEmpty();
        (await kit.ShipmentsOfAsync(customer)).Select(s => s.Id).Should().Equal(accepted.Id);
        (await kit.ShipmentAsync(accepted.Id)).Version.Should().Be(accepted.Version);
        (await kit.LogsAsync()).Should().ContainSingle().Which.Status.Should().Be("unchanged");
    }

    /// <summary>
    /// Birleştirilmiş kopyanın Id'siyle gelen komut asıl kayda çözülür (<c>CustomerIdResolver</c>): asıl kaydın dosyası
    /// ve siparişleri kullanılır; açılan ya da seçilen dosyanın müşterisi asıl kayıttır.
    /// </summary>
    [Fact]
    public async Task Attach_birlestirilmis_kopya_Id_si_asil_kaydin_dosyasina_cozulur()
    {
        var kit = await Kit.CreateAsync(_factory);
        var canonical = Guid.NewGuid();
        var alias = Guid.NewGuid();
        await kit.SeedProjectionAsync(canonical);
        await kit.SeedProjectionAsync(alias, mergedInto: canonical);
        var customer = canonical.ToString("N");
        var session = await kit.SeedSessionAsync(Kit.Phase.Released);
        var file = await kit.SeedShipmentAsync(customer, ShipmentStatus.Held, 100m, derived: true);
        await kit.SeedOrderAsync(session, o => { o.CustomerId = customer; o.ShipmentId = file.Id; });
        var k1 = await kit.SeedOrderAsync(session, o => o.CustomerId = customer);

        var resp = await kit.SendAsync(
            kit.AttachShipment(Guid.NewGuid(), alias.ToString("N"), Guid.NewGuid(), [k1]));

        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());
        (await Kit.ReadAsync(resp)).Result.GetProperty("shipmentId").GetGuid().Should().Be(file.Id);
        (await kit.OrderAsync(k1.Id)).ShipmentId.Should().Be(file.Id);
        (await kit.ShipmentAsync(file.Id)).CumulativeAmount.Should().Be(200m);
        (await kit.ShipmentsOfAsync(alias.ToString("N"))).Should().BeEmpty();
    }

    /// <summary>
    /// Açılacak dosyanın Id'si sunucuda zaten varsa — başka lisansta ya da bu lisansta (ör. kabul edilmiş dosya; seçilmez,
    /// §4.10-6) — 409 <c>content-conflict</c>, 500 değil (Shipments'ın birincil anahtarı yalnız Id). Hiçbir şey yazılmaz;
    /// öbür satır değişmez.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Attach_onerilen_dosya_Id_si_varsa_409_content_conflict(bool otherLicense)
    {
        var kit = await Kit.CreateAsync(_factory);
        var customer = NewCustomer();
        var owner = otherLicense ? await kit.SecondLicenseAsync() : kit;
        var existing = await owner.SeedShipmentAsync(
            otherLicense ? NewCustomer() : customer, ShipmentStatus.Pending, 400m, derived: false);
        var k1 = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released), o => o.CustomerId = customer);

        var resp = await kit.SendAsync(kit.AttachShipment(Guid.NewGuid(), customer, existing.Id, [k1]));

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict, await resp.Content.ReadAsStringAsync());
        var problem = await Kit.ProblemAsync(resp);
        problem.GetProperty("title").GetString().Should().Be("content-conflict");
        problem.GetProperty("shipmentId").GetGuid().Should().Be(existing.Id);
        (await kit.OrderAsync(k1.Id)).Should().BeEquivalentTo(new { ShipmentId = (Guid?)null, SyncVersion = 1 });
        (await owner.ShipmentAsync(existing.Id)).Should().BeEquivalentTo(
            new { existing.LicenseId, existing.CustomerId, CumulativeAmount = 400m, existing.Version });
        (await kit.LogsAsync()).Should().BeEmpty();
    }

    /// <summary>§10.2: <c>attach-shipment</c> bildirim üretmez — durum değişmez, etiket kuralı uygulanmaz, push yok.</summary>
    [Fact]
    public async Task Attach_bildirim_uretmez()
    {
        var kit = await Kit.CreateAsync(_factory);
        var (customer, conversationId) = await SeedLabelRuleAsync(kit);
        var k1 = await kit.SeedOrderAsync(await kit.SeedSessionAsync(Kit.Phase.Released), o => o.CustomerId = customer);

        (await kit.SendAsync(kit.AttachShipment(Guid.NewGuid(), customer, Guid.NewGuid(), [k1])))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        (await LabelCountAsync(kit, conversationId)).Should().Be(0);
        _factory.Push.Sent.Where(n => n.CustomerId == kit.CustomerId).Should().BeEmpty();
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static string NewCustomer() => Guid.NewGuid().ToString("N");

    /// <summary>
    /// <c>ShipmentStatusChanged</c> kuralı ve müşterinin WhatsApp sohbeti (telefon projeksiyonda). Kişisel alanlar
    /// üretilir.
    /// </summary>
    private static async Task<(string Customer, Guid ConversationId)> SeedLabelRuleAsync(Kit kit)
    {
        var wpfCustomerId = Guid.NewGuid();
        var conversationId = Guid.NewGuid();
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
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, Name = "Kargo", Color = "#eab308", CreatedAt = now,
            };
            db.WaLabels.Add(label);
            db.WaLabelRules.Add(new WaLabelRule
            {
                Id = Guid.NewGuid(), LicenseId = kit.LicenseId, EventKey = WaLabelEvent.ShipmentStatusChanged,
                WaLabelId = label.Id, CreatedAt = now,
            });
            return Task.CompletedTask;
        });
        return (wpfCustomerId.ToString("N"), conversationId);
    }

    private static Task<int> LabelCountAsync(Kit kit, Guid conversationId) =>
        kit.ReadDbAsync(db => db.WaConversationLabels.CountAsync(x => x.ConversationId == conversationId));

    private static List<Guid> Guids(JsonElement result, string property) =>
        result.GetProperty(property).EnumerateArray().Select(e => e.GetGuid()).ToList();
}
