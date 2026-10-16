using FluentAssertions;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Sync;
using Xunit;
using static OrderDeck.LicenseServer.Services.Sync.SalesModeDisable;

namespace OrderDeck.LicenseServer.Tests.Services.Sync;

/// <summary>
/// <c>sales-sync disable</c>'ın saf planı (spec 2026-10-13 §4.10 "disable", §8.7, M19, §12 risk 15, §15-38): Shipped
/// olmayan dosyanın mod Off'taki tek yazıcısı bağlı etiketlerin sahibidir. Sahip oturumun sahibi, yoksa siparişin
/// kaynağı; geri yükleme zinciri en yeni kopyaya çözülür; karışık dosya bağlı tutarı (DM7) büyük olan kopyaya, eşitlikte
/// "N" metni ordinal küçük olana gider. Gerçek SQL Server'daki uçtan uca hâli
/// <c>SalesSyncDisableRelationalTests</c>'te.
/// </summary>
public sealed class SalesModeDisableTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    // ── Geri yükleme zinciri ─────────────────────────────────────────────────

    [Fact]
    public void Zincir_ardillari_izleyerek_en_yeni_kopyaya_cozulur()
    {
        var (a1, a2, a3) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var replicas = new[]
        {
            new ReplicaLink(a1, null, T0),
            new ReplicaLink(a2, a1, T0.AddDays(1)),
            new ReplicaLink(a3, a2, T0.AddDays(2)),
        };

        Newest(a1, replicas).Should().Be(a3);
        Newest(a2, replicas).Should().Be(a3);
        Newest(a3, replicas).Should().Be(a3);
    }

    /// <summary>Kopya satırı olmayan (ya da ardılı olmayan) sahip kendisidir.</summary>
    [Fact]
    public void Ardili_olmayan_ya_da_bilinmeyen_kopya_kendisine_cozulur()
    {
        var known = Guid.NewGuid();
        var stranger = Guid.NewGuid();
        var replicas = new[] { new ReplicaLink(known, null, T0) };

        Newest(known, replicas).Should().Be(known);
        Newest(stranger, replicas).Should().Be(stranger);
    }

    /// <summary>Çatal (aynı öncülden iki geri yükleme): son görülen ardıl seçilir; son görülme eşitse "N" metni
    /// ordinal küçük olan.</summary>
    [Fact]
    public void Catalda_son_gorulen_ardil_esitlikte_kucuk_Id_secilir()
    {
        var root = Guid.NewGuid();
        var older = Guid.NewGuid();
        var newer = Guid.NewGuid();
        Newest(root, [new ReplicaLink(older, root, T0), new ReplicaLink(newer, root, T0.AddHours(1))])
            .Should().Be(newer);

        var (small, large) = Ordered(Guid.NewGuid(), Guid.NewGuid());
        Newest(root, [new ReplicaLink(large, root, T0), new ReplicaLink(small, root, T0)])
            .Should().Be(small);
    }

    /// <summary>Bozuk veri: döngü durur (kendi kendinin ardılı dahil); zincir en çok
    /// <see cref="SalesReplicaService.MaxLineageDepth"/> halka ilerler.</summary>
    [Fact]
    public void Dongu_durur_derinlik_siniri_asilmaz()
    {
        var (x, y) = (Guid.NewGuid(), Guid.NewGuid());
        Newest(x, [new ReplicaLink(y, x, T0), new ReplicaLink(x, y, T0)]).Should().Be(y);
        Newest(x, [new ReplicaLink(x, x, T0)]).Should().Be(x);

        var chain = Enumerable.Range(0, SalesReplicaService.MaxLineageDepth + 5).Select(_ => Guid.NewGuid()).ToList();
        var links = chain.Select((id, i) => new ReplicaLink(id, i == 0 ? null : chain[i - 1], T0.AddMinutes(i))).ToList();
        Newest(chain[0], links).Should().Be(chain[SalesReplicaService.MaxLineageDepth]);
    }

    // ── Dosyanın sahibi ──────────────────────────────────────────────────────

    /// <summary>Oturumun sahibi siparişin kaynağından önce gelir; oturum sahibi boşsa siparişin kaynağı.</summary>
    [Fact]
    public void Sahip_oturum_sahibi_yoksa_siparisin_kaynagi()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var bySession = NewShipment(origin: b);
        var byOrder = NewShipment(origin: b);

        var plan = Build(
            [bySession, byOrder],
            [Linked(bySession, sessionOwner: a, origin: b, 100m), Linked(byOrder, sessionOwner: null, origin: c, 100m)],
            []);

        Single(plan, bySession).Should().BeEquivalentTo(new
        {
            CurrentOriginReplicaId = (Guid?)b, NewOriginReplicaId = (Guid?)a, Outcome = ShipmentOutcome.Rewrite,
        });
        Single(plan, byOrder).NewOriginReplicaId.Should().Be(c);
        plan.Rewrites.Select(p => p.Id).Should().BeEquivalentTo([bySession.Id, byOrder.Id]);
    }

    /// <summary>Karışık dosya: bağlı tutar DM7 kuralıyla (iptal edilmiş ve geçici yedek sayılmaz); büyük pay kazanır,
    /// paylar büyükten küçüğe. Eşitlikte "N" metni ordinal küçük kopya.</summary>
    [Fact]
    public void Karisik_dosya_DM7_tutari_buyuk_olana_esitlikte_kucuk_Id_ye_gider()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var mixed = NewShipment(origin: b);
        var plan = Build(
            [mixed],
            [
                Linked(mixed, a, a, 100m), Linked(mixed, a, a, 200m),
                Linked(mixed, b, b, 200m), Linked(mixed, b, b, 500m, cancelled: true),
                Linked(mixed, b, b, 400m, tentative: true),
            ],
            []);

        var only = Single(plan, mixed);
        only.Mixed.Should().BeTrue();
        only.NewOriginReplicaId.Should().Be(a, "iptal ve geçici yedek sayılsaydı B 1100 ile kazanırdı");
        only.Shares.Should().Equal(new OwnerShare(a, 300m), new OwnerShare(b, 200m));
        plan.Mixed.Should().ContainSingle().Which.Id.Should().Be(mixed.Id);

        var (small, large) = Ordered(a, b);
        var tie = NewShipment(origin: large);
        var tied = Single(Build([tie], [Linked(tie, large, large, 150m), Linked(tie, small, small, 150m)], []), tie);
        tied.NewOriginReplicaId.Should().Be(small);
        tied.Shares.Should().Equal(new OwnerShare(small, 150m), new OwnerShare(large, 150m));
        tied.Outcome.Should().Be(ShipmentOutcome.Rewrite);
    }

    /// <summary>Yalnız iptal edilmiş etiketleri bağlı kopya da sahiptir (tutarı 0): bağ çözülmez (Y5).</summary>
    [Fact]
    public void Yalniz_iptal_edilmis_bagli_kopya_da_sahip_sayilir()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var shipment = NewShipment(origin: b);

        var plan = Single(Build([shipment], [Linked(shipment, a, a, 80m, cancelled: true)], []), shipment);

        plan.NewOriginReplicaId.Should().Be(a);
        plan.Shares.Should().Equal(new OwnerShare(a, 0m));
    }

    /// <summary>Geri yüklenmiş kopyanın etiketleri zincirin en yeni halkasına sayılır; iki halkanın etiketi tek
    /// kopyanın payında toplanır, karışık sayılmaz.</summary>
    [Fact]
    public void Zincirdeki_halkalarin_etiketleri_en_yeni_kopyada_toplanir()
    {
        var (a1, a2) = (Guid.NewGuid(), Guid.NewGuid());
        var shipment = NewShipment(origin: a1);
        var replicas = new[] { new ReplicaLink(a1, null, T0), new ReplicaLink(a2, a1, T0.AddDays(1)) };

        var plan = Single(Build([shipment], [Linked(shipment, a1, a1, 100m), Linked(shipment, a2, a2, 50m)], replicas),
            shipment);

        plan.Mixed.Should().BeFalse();
        plan.Shares.Should().Equal(new OwnerShare(a2, 150m));
        plan.Outcome.Should().Be(ShipmentOutcome.Rewrite);
    }

    /// <summary>Sahip zaten doğruysa yazılmaz; bağlı siparişi olmayan ya da hiçbir bağlı siparişinin sahibi bilinmeyen
    /// dosya değişmez. Sahibi bilinmeyen sipariş paya girmez ama sayılır.</summary>
    [Fact]
    public void Dogru_sahip_bagsiz_ve_sahibi_bilinmeyen_dosya_degismez()
    {
        var (a, b) = (Guid.NewGuid(), Guid.NewGuid());
        var correct = NewShipment(origin: a);
        var empty = NewShipment(origin: b);
        var unknown = NewShipment(origin: b);
        var partly = NewShipment(origin: b);

        var plan = Build(
            [correct, empty, unknown, partly],
            [
                Linked(correct, a, b, 100m),
                Linked(unknown, null, null, 100m),
                Linked(partly, null, null, 900m), Linked(partly, a, a, 10m),
            ],
            []);

        Single(plan, correct).Outcome.Should().Be(ShipmentOutcome.AlreadyOwner);
        Single(plan, empty).Should().BeEquivalentTo(new
        {
            Outcome = ShipmentOutcome.NoKnownOwner, NewOriginReplicaId = (Guid?)null, LinkedOrders = 0,
        });
        Single(plan, unknown).Should().BeEquivalentTo(new { Outcome = ShipmentOutcome.NoKnownOwner, LinkedOrders = 1 });
        Single(plan, partly).Should().BeEquivalentTo(new
        {
            Outcome = ShipmentOutcome.Rewrite, NewOriginReplicaId = (Guid?)a, Mixed = false,
        });
        plan.LinkedOrders.Should().Be(4);
        plan.LinkedOrdersWithUnknownOwner.Should().Be(2);
        plan.Rewrites.Should().ContainSingle().Which.Id.Should().Be(partly.Id);
        plan.AlreadyOwned.Should().ContainSingle().Which.Id.Should().Be(correct.Id);
        plan.NoKnownOwner.Select(p => p.Id).Should().BeEquivalentTo([empty.Id, unknown.Id]);
    }

    /// <summary>Plan dosyaları Id'nin "N" metnine göre sıralar (çıktı kararlı).</summary>
    [Fact]
    public void Plan_dosyalari_Id_metnine_gore_siralar()
    {
        var shipments = Enumerable.Range(0, 20).Select(_ => NewShipment(origin: Guid.NewGuid())).ToList();

        Build(shipments, [], []).Shipments.Select(p => p.Id.ToString("N"))
            .Should().BeInAscendingOrder(StringComparer.Ordinal);
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────

    private static Shipment NewShipment(Guid? origin) => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid().ToString("N"),
        Status = ShipmentStatus.Pending,
        OriginReplicaId = origin,
    };

    private static LinkedOrder Linked(
        Shipment shipment, Guid? sessionOwner, Guid? origin, decimal price, bool cancelled = false, bool tentative = false)
        => new(Guid.NewGuid(), shipment.Id, sessionOwner, origin, price, cancelled, tentative);

    private static ShipmentPlan Single(Plan plan, Shipment shipment) =>
        plan.Shipments.Single(p => p.Id == shipment.Id);

    /// <summary>"N" metnine göre ordinal (küçük, büyük).</summary>
    private static (Guid Small, Guid Large) Ordered(Guid x, Guid y) =>
        string.CompareOrdinal(x.ToString("N"), y.ToString("N")) < 0 ? (x, y) : (y, x);
}
