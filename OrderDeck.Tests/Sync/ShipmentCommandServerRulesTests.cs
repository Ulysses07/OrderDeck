using System.Text.Json;
using FluentAssertions;
using OrderDeck.Licensing.Api.Models;
using Xunit;

namespace OrderDeck.Tests.Sync;

/// <summary>
/// Sahte sunucunun kargo komutları (W2-08; spec 2026-10-13 §4.6 <c>attach-shipment</c> ve <c>shipment-decision</c>,
/// §7.1–§7.3, M22, U12) — tel üzerinden, sunucunun işleyicileriyle (<c>AttachShipmentCommandHandler</c>,
/// <c>ShipmentDecisionCommandHandler</c>) aynı kurallar: dekont adımının testleri bu ikize güveniyor.
/// </summary>
public sealed class ShipmentCommandServerRulesTests : IDisposable
{
    private static readonly DateTimeOffset T0 = SalesEditTestBed.T0;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private readonly SalesEditTestBed _bed = new();
    private readonly Guid _otherPc = Guid.NewGuid();

    public void Dispose() => _bed.Dispose();

    private FakeSalesServer Server => _bed.Server;

    private Guid Me => _bed.Envelope.ReplicaId!.Value;

    private Guid Session(bool released, Guid? owner = null)
    {
        var id = Guid.NewGuid();
        Server.SeedSession(id, T0, released ? T0.AddHours(1) : null, owner: owner ?? _otherPc, ownerMachineName: "PC-B");
        if (released) Server.ReleaseSession(id);
        return id;
    }

    private Guid Order(Guid session, string customer, decimal price, Guid? origin, bool cancelled = false,
        bool tentative = false, Guid? shipment = null)
    {
        var id = Guid.NewGuid();
        Server.SeedOrder(new FakeSalesServer.OrderBody(id, session, customer, "tiktok", "alici_ornek", null, "aldım",
                "A1", price, T0.AddMinutes(1), T0.AddMinutes(2), cancelled ? T0.AddMinutes(3) : null,
                cancelled ? "musteri" : null, false, tentative, tentative, null, null, null, null, null, null, null, null,
                shipment),
            version: 1, origin: origin);
        return id;
    }

    private Guid File(string customer, string status, decimal amount, long createdOffset, bool derived)
    {
        var id = Guid.NewGuid();
        Server.SeedShipment(new FakeSalesServer.ShipmentBody(id, customer, status, amount, T0.AddMinutes(createdOffset),
            null, null), origin: _otherPc, derived: derived);
        return id;
    }

    private Task<SalesCommandHttpResult> SendAsync(SalesCommandPayload payload,
        IReadOnlyList<SalesCommandExpected>? expected = null, Guid? commandId = null)
    {
        using var doc = JsonDocument.Parse(payload.ToWireJson());
        return _bed.Api.PostSalesCommandAsync(Server.LicenseId, new SalesCommandRequest(commandId ?? Guid.NewGuid(), Me, 2,
            1, payload.Kind, doc.RootElement.Clone(), expected ?? []));
    }

    private static SalesCommandExpected V(Guid id, int version = 1) => new(id, version);

    [Fact]
    public async Task Adaylar_kargo_bagi_bilinen_gorunur_siparisler_hedef_en_yeni_turetilmis_acik_dosya_havuz_commitden_sonra()
    {
        var customer = Guid.NewGuid().ToString("N");
        var released = Session(released: true);
        var otherLive = Session(released: false);
        var ownLive = Session(released: false, owner: Me);
        var candidate = Order(released, customer, 150m, origin: _otherPc);
        var ownCandidate = Order(ownLive, customer, 70m, origin: Me);
        Order(released, customer, 999m, origin: null);                        // M22: kargo bağı bilinmiyor
        Order(otherLive, customer, 999m, origin: _otherPc);                    // başka PC'nin canlı yayını görünmez
        Order(released, customer, 999m, origin: _otherPc, cancelled: true);
        Order(released, customer, 999m, origin: _otherPc, tentative: true);
        var accepted = File(customer, "held", 400m, createdOffset: 50, derived: false);  // kabul edilmiş: seçilmez
        var derived = File(customer, "pending", 0m, createdOffset: 10, derived: true);

        var http = await SendAsync(new SalesAttachShipmentPayload(customer, Guid.NewGuid()),
            [V(candidate), V(ownCandidate)]);

        http.Transport.Should().Be(SalesCommandTransport.Ok, string.Join(" | ", Server.Faults));
        var result = http.Response!.Result;
        result.ShipmentId.Should().Be(derived, "en yeni TÜRETİLMİŞ açık dosya; kabul edilmiş dosya seçilmez");
        Server.Order(candidate)!.Row.ShipmentId.Should().Be(derived);
        Server.Order(candidate)!.ShipmentSetAt.Should().NotBeNull();
        Server.Order(candidate)!.Version.Should().Be(2);
        Server.Order(ownCandidate)!.Row.ShipmentId.Should().Be(derived, "çağıranın kendi yayını görünür");
        Server.Shipment(derived)!.Row.CumulativeAmount.Should().Be(220m, "tutar bağlardan türetilir (DM7)");
        result.Pool.Should().Be(620m, "havuz: kişinin bütün açık dosyaları (kabul edilmiş saklanan tutarıyla)");
        result.PoolShipmentIds.Should().Equal(accepted, derived);
        http.Response.Shipments.Select(s => s.Id).Should().BeEquivalentTo([accepted, derived]);
        http.Response.Orders.Select(o => o.Id).Should().BeEquivalentTo([candidate, ownCandidate]);
    }

    [Fact]
    public async Task Turetilmis_acik_dosya_yoksa_onerilen_Id_ile_acilir_aday_kumesi_farkliysa_stale_ve_adaylar()
    {
        var customer = Guid.NewGuid().ToString("N");
        var session = Session(released: true);
        var candidate = Order(session, customer, 150m, origin: _otherPc);
        var unknown = Guid.NewGuid();                                          // istemcinin gördüğü ama sunucuda olmayan
        var proposed = Guid.NewGuid();

        var stale = await SendAsync(new SalesAttachShipmentPayload(customer, proposed), [V(candidate), V(unknown, 0)]);

        stale.Problem!.Code.Should().Be(SalesCommandProblemCodes.Stale);
        stale.Problem.Candidates.Should().Equal(V(candidate));
        stale.Problem.Current!.Select(o => o.Id).Should().Equal(candidate);
        Server.Shipment(proposed).Should().BeNull("ret hiçbir şey yazmaz");

        var applied = await SendAsync(new SalesAttachShipmentPayload(customer, proposed), stale.Problem.Candidates);

        applied.Response!.Result.ShipmentId.Should().Be(proposed);
        var opened = Server.Shipment(proposed)!;
        opened.Row.Status.Should().Be("pending");
        opened.AmountDerivedAt.Should().NotBeNull();
        opened.CommandedAt.Should().NotBeNull();
        opened.OriginReplicaId.Should().Be(Me);
        opened.Row.CumulativeAmount.Should().Be(150m);
        applied.Response.Result.Pool.Should().Be(150m);
    }

    [Fact]
    public async Task Aday_yoksa_dosya_acilmaz_havuz_yine_doner_tavan_asilinca_too_many_candidates()
    {
        var customer = Guid.NewGuid().ToString("N");
        var open = File(customer, "held", 300m, createdOffset: 5, derived: true);

        var none = await SendAsync(new SalesAttachShipmentPayload(customer, Guid.NewGuid()), []);

        none.Response!.Status.Should().Be(SalesCommandResponse.StatusUnchanged);
        none.Response.Result.ShipmentId.Should().BeNull();
        none.Response.Result.Pool.Should().Be(300m);
        none.Response.Result.PoolShipmentIds.Should().Equal(open);

        var session = Session(released: true);
        var a = Order(session, customer, 10m, origin: _otherPc);
        var b = Order(session, customer, 10m, origin: _otherPc);
        Server.MaxAttachCandidates = 1;
        var tooMany = await SendAsync(new SalesAttachShipmentPayload(customer, Guid.NewGuid()), [V(a), V(b)]);

        tooMany.Problem!.Code.Should().Be(SalesCommandProblemCodes.TooManyCandidates);
        tooMany.Problem.Count.Should().Be(2);
        tooMany.Problem.Max.Should().Be(1);
    }

    [Fact]
    public async Task Kargoya_verme_U12_ile_acik_dosyalari_kapatir_turetilmis_dosyada_iptalli_bagi_cozer()
    {
        var customer = Guid.NewGuid().ToString("N");
        var session = Session(released: true);
        var newest = File(customer, "pending", 0m, createdOffset: 20, derived: true);
        var older = File(customer, "held", 500m, createdOffset: 10, derived: false);   // kabul edilmiş
        var kept = Order(session, customer, 200m, origin: _otherPc, shipment: newest);
        var cancelledInDerived = Order(session, customer, 80m, origin: _otherPc, cancelled: true, shipment: newest);
        var cancelledInAccepted = Order(session, customer, 60m, origin: _otherPc, cancelled: true, shipment: older);
        var version = Server.Shipment(newest)!.Version;

        var http = await SendAsync(new SalesShipmentDecisionPayload(newest, SalesShipmentDecisionPayload.ShipNow, version));

        http.Transport.Should().Be(SalesCommandTransport.Ok, string.Join(" | ", Server.Faults));
        Server.Shipment(newest)!.Row.Status.Should().Be("shipped");
        Server.Shipment(older)!.Row.Status.Should().Be("shipped", "U12: kişinin öbür açık dosyası aynı karara uyar");
        Server.Order(cancelledInDerived)!.Row.ShipmentId.Should().BeNull("türetilmiş dosyada iptalli bağ çözülür (§7.2)");
        Server.Order(cancelledInDerived)!.ShipmentSetAt.Should().NotBeNull();
        Server.Order(cancelledInAccepted)!.Row.ShipmentId.Should().Be(older, "kabul edilmiş dosyada bağ kalır");
        Server.Order(kept)!.Row.ShipmentId.Should().Be(newest);
        Server.Shipment(newest)!.Row.CumulativeAmount.Should().Be(200m, "tutar iptalsiz bağlarla donar");
        Server.Shipment(older)!.Row.CumulativeAmount.Should().Be(500m, "kabul edilmiş dosya saklanan tutarıyla");
        http.Response!.Result.Decided!.Select(d => (d.Id, d.Amount)).Should().BeEquivalentTo([(newest, 200m), (older, 500m)]);
        http.Response.Result.Changed.Should().Equal(cancelledInDerived);
        http.Response.Shipments.Select(s => s.Id).Should().Contain([newest, older]);
    }

    [Fact]
    public async Task Karar_kargolanmis_dosyada_shipment_shipped_bayat_surumde_stale_ikisi_de_guncel_dosyayi_tasir()
    {
        var customer = Guid.NewGuid().ToString("N");
        var open = File(customer, "held", 100m, createdOffset: 1, derived: true);
        var shipped = File(customer, "shipped", 300m, createdOffset: 0, derived: true);

        var stale = await SendAsync(new SalesShipmentDecisionPayload(open, SalesShipmentDecisionPayload.Hold,
            Server.Shipment(open)!.Version + 5));
        var done = await SendAsync(new SalesShipmentDecisionPayload(shipped, SalesShipmentDecisionPayload.Hold,
            Server.Shipment(shipped)!.Version));

        stale.Problem!.Code.Should().Be(SalesCommandProblemCodes.Stale);
        stale.Problem.CurrentShipments.Should().ContainSingle(s => s.Id == open);
        done.Problem!.Code.Should().Be(SalesCommandProblemCodes.ShipmentShipped);
        done.Problem.CurrentShipments.Should().ContainSingle(s => s.Id == shipped && s.Status == "shipped");
        Server.Shipment(open)!.Row.Status.Should().Be("held", "ret hiçbir şey yazmaz");
    }
}
