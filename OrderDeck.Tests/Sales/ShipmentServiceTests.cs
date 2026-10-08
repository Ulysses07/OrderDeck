using System;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class ShipmentServiceTests
{
    private const string Cid = "c1";
    private const string Sid = "s1";

    private static (InMemorySqlite Db, ShipmentService Svc, ShipmentRepository ShipRepo,
                    LabelRepository LabelRepo, AppSettings Settings, long NowRef)
        Fx(decimal? threshold = 5000m, decimal? fee = 150m)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession(Sid, null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer(Cid, "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        var shipRepo = new ShipmentRepository(db);
        var labelRepo = new LabelRepository(db);
        var settings = new AppSettings();
        settings.Shipping.FreeShippingThreshold = threshold;
        settings.Shipping.ShippingFee = fee;

        long now = 1_000L;
        var svc = new ShipmentService(shipRepo, labelRepo, () => settings, () => now);
        return (db, svc, shipRepo, labelRepo, settings, now);
    }

    private static Label MakeLabel(string id, decimal price, long addedAt = 200) =>
        new(id, Sid, Cid, "instagram", "@a", $"msg-{id}", null, price, AddedAt: addedAt,
            PrintedAt: null);

    // ── GetOrCreateOpenShipment ─────────────────────────────────────────

    [Fact]
    public void GetOrCreateOpenShipment_creates_new_pending_when_none_exists()
    {
        var (db, svc, repo, _, _, now) = Fx();
        using var _d = db;

        var shipment = svc.GetOrCreateOpenShipment(Cid);

        shipment.CustomerId.Should().Be(Cid);
        shipment.Status.Should().Be(ShipmentStatus.Pending);
        shipment.CreatedAt.Should().Be(now);
        shipment.CumulativeAmount.Should().Be(0m);
        repo.GetById(shipment.Id).Should().NotBeNull();
    }

    [Fact]
    public void GetOrCreateOpenShipment_returns_existing_pending()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var first = svc.GetOrCreateOpenShipment(Cid);
        var second = svc.GetOrCreateOpenShipment(Cid);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public void GetOrCreateOpenShipment_returns_existing_held()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var first = svc.GetOrCreateOpenShipment(Cid);
        svc.ApplyDecision(first.Id, ShipmentDecision.Hold);

        var second = svc.GetOrCreateOpenShipment(Cid);
        second.Id.Should().Be(first.Id);
        second.Status.Should().Be(ShipmentStatus.Held);
    }

    [Fact]
    public void GetOrCreateOpenShipment_creates_new_after_shipped()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var first = svc.GetOrCreateOpenShipment(Cid);
        svc.ApplyDecision(first.Id, ShipmentDecision.ShipNow);

        var second = svc.GetOrCreateOpenShipment(Cid);
        second.Id.Should().NotBe(first.Id);
        second.Status.Should().Be(ShipmentStatus.Pending);
    }

    // ── AttachLabels ────────────────────────────────────────────────────

    [Fact]
    public void AttachLabels_updates_cumulative_amount()
    {
        var (db, svc, _, labels, _, _) = Fx();
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1200m));
        labels.Insert(MakeLabel("l2", 800m));

        var shipment = svc.GetOrCreateOpenShipment(Cid);
        var updated = svc.AttachLabels(shipment.Id, new[] { "l1", "l2" });

        updated.CumulativeAmount.Should().Be(2000m);
        labels.GetById("l1")!.ShipmentId.Should().Be(shipment.Id);
        labels.GetById("l2")!.ShipmentId.Should().Be(shipment.Id);
    }

    [Fact]
    public void AttachLabels_is_idempotent_for_same_label()
    {
        var (db, svc, _, labels, _, _) = Fx();
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1200m));
        var shipment = svc.GetOrCreateOpenShipment(Cid);

        svc.AttachLabels(shipment.Id, new[] { "l1" });
        var afterSecond = svc.AttachLabels(shipment.Id, new[] { "l1" });

        afterSecond.CumulativeAmount.Should().Be(1200m); // sadece 1 kez sayıldı
    }

    [Fact]
    public void AttachLabels_throws_when_label_already_attached_to_another_shipment()
    {
        var (db, svc, _, labels, _, _) = Fx();
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1200m));
        var s1 = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s1.Id, new[] { "l1" });
        svc.ApplyDecision(s1.Id, ShipmentDecision.ShipNow); // close

        var s2 = svc.GetOrCreateOpenShipment(Cid);
        var act = () => svc.AttachLabels(s2.Id, new[] { "l1" });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"*already attached to Shipment {s1.Id}*");
    }

    [Fact]
    public void AttachLabels_throws_for_shipped_shipment()
    {
        var (db, svc, _, labels, _, _) = Fx();
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1200m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.ApplyDecision(s.Id, ShipmentDecision.ShipNow);

        var act = () => svc.AttachLabels(s.Id, new[] { "l1" });
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Shipped Shipment*");
    }

    // ── EvaluateAfterPayment ────────────────────────────────────────────

    [Fact]
    public void EvaluateAfterPayment_silent_when_not_all_labels_paid()
    {
        var (db, svc, _, labels, _, _) = Fx();
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1000m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s.Id, new[] { "l1" });

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: false);

        ctx.ShouldPrompt.Should().BeFalse();
        ctx.AllLabelsPaid.Should().BeFalse();
    }

    [Fact]
    public void EvaluateAfterPayment_silent_when_no_open_shipment()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);

        ctx.ShouldPrompt.Should().BeFalse();
        ctx.Shipment.Should().BeNull();
    }

    [Fact]
    public void EvaluateAfterPayment_silent_when_shipping_feature_disabled()
    {
        var (db, svc, _, labels, _, _) = Fx(threshold: null, fee: null);
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1000m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s.Id, new[] { "l1" });

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);

        ctx.ShouldPrompt.Should().BeFalse();
        ctx.Shipment.Should().NotBeNull();
    }

    [Fact]
    public void EvaluateAfterPayment_threshold_not_reached_signals_under_amount()
    {
        var (db, svc, _, labels, _, _) = Fx(threshold: 5000m);
        using var _d = db;

        labels.Insert(MakeLabel("l1", 1200m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s.Id, new[] { "l1" });

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);

        ctx.ShouldPrompt.Should().BeTrue();
        ctx.ThresholdReached.Should().BeFalse();
        ctx.AmountToThreshold.Should().Be(3800m);
    }

    [Fact]
    public void EvaluateAfterPayment_threshold_reached()
    {
        var (db, svc, _, labels, _, _) = Fx(threshold: 5000m);
        using var _d = db;

        labels.Insert(MakeLabel("l1", 2000m));
        labels.Insert(MakeLabel("l2", 1500m));
        labels.Insert(MakeLabel("l3", 1800m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s.Id, new[] { "l1", "l2", "l3" });

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);

        ctx.ShouldPrompt.Should().BeTrue();
        ctx.ThresholdReached.Should().BeTrue();
        ctx.AmountToThreshold.Should().Be(0m);
        ctx.Shipment!.CumulativeAmount.Should().Be(5300m);
    }

    [Fact]
    public void EvaluateAfterPayment_threshold_exactly_reached_is_treated_as_reached()
    {
        var (db, svc, _, labels, _, _) = Fx(threshold: 5000m);
        using var _d = db;

        labels.Insert(MakeLabel("l1", 5000m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s.Id, new[] { "l1" });

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);

        ctx.ThresholdReached.Should().BeTrue();
    }

    // ── ApplyDecision ───────────────────────────────────────────────────

    [Fact]
    public void ApplyDecision_ShipNow_closes_shipment_with_timestamp()
    {
        var (db, svc, _, _, _, now) = Fx();
        using var _d = db;

        var s = svc.GetOrCreateOpenShipment(Cid);
        var shipped = svc.ApplyDecision(s.Id, ShipmentDecision.ShipNow);

        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        shipped.ShippedAt.Should().Be(now);
    }

    [Fact]
    public void ApplyDecision_Hold_sets_HeldAt_only_first_time()
    {
        var (db, svc, _, _, _, now) = Fx();
        using var _d = db;

        var s = svc.GetOrCreateOpenShipment(Cid);
        var firstHold = svc.ApplyDecision(s.Id, ShipmentDecision.Hold);
        firstHold.HeldAt.Should().Be(now);

        // İkinci Hold (örn. vendor "beklemeye devam" dedi) HeldAt'i değiştirmesin
        var secondHold = svc.ApplyDecision(s.Id, ShipmentDecision.Hold);
        secondHold.HeldAt.Should().Be(now);
    }

    [Fact]
    public void ApplyDecision_RecipientPays_transition()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var s = svc.GetOrCreateOpenShipment(Cid);
        var rp = svc.ApplyDecision(s.Id, ShipmentDecision.RecipientPays);

        rp.Status.Should().Be(ShipmentStatus.RecipientPays);
    }

    [Fact]
    public void ApplyDecision_throws_when_shipment_already_shipped()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.ApplyDecision(s.Id, ShipmentDecision.ShipNow);

        var act = () => svc.ApplyDecision(s.Id, ShipmentDecision.Hold);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*already Shipped*");
    }

    [Fact]
    public void ApplyDecision_Held_to_Shipped_transition()
    {
        var (db, svc, _, _, _, _) = Fx();
        using var _d = db;

        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.ApplyDecision(s.Id, ShipmentDecision.Hold);
        var shipped = svc.ApplyDecision(s.Id, ShipmentDecision.ShipNow);

        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        shipped.HeldAt.Should().NotBeNull(); // preserved
    }

    // ── End-to-end scenario ─────────────────────────────────────────────

    [Fact]
    public void Three_session_cumulative_scenario_works_end_to_end()
    {
        // Spec brainstorm senaryosu: Ayşe 3 yayında alım yapar, ilk 2'si beklet,
        // 3.'de threshold aşılır.
        var (db, svc, _, labels, _, _) = Fx(threshold: 5000m);
        using var _d = db;

        // 1. yayın: 2000 TL
        labels.Insert(MakeLabel("l1", 2000m, addedAt: 100));
        var s1 = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s1.Id, new[] { "l1" });
        var ctx1 = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);
        ctx1.ThresholdReached.Should().BeFalse();
        svc.ApplyDecision(s1.Id, ShipmentDecision.Hold);

        // 2. yayın: +1500 TL
        labels.Insert(MakeLabel("l2", 1500m, addedAt: 200));
        var s2 = svc.GetOrCreateOpenShipment(Cid);
        s2.Id.Should().Be(s1.Id); // hala aynı Shipment, Held
        svc.AttachLabels(s2.Id, new[] { "l2" });
        var ctx2 = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);
        ctx2.ThresholdReached.Should().BeFalse();
        ctx2.AmountToThreshold.Should().Be(1500m); // 5000 - 3500
        svc.ApplyDecision(s2.Id, ShipmentDecision.Hold);

        // 3. yayın: +1800 TL, threshold aşılır
        labels.Insert(MakeLabel("l3", 1800m, addedAt: 300));
        var s3 = svc.GetOrCreateOpenShipment(Cid);
        s3.Id.Should().Be(s1.Id);
        svc.AttachLabels(s3.Id, new[] { "l3" });
        var ctx3 = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);
        ctx3.ThresholdReached.Should().BeTrue();
        ctx3.Shipment!.CumulativeAmount.Should().Be(5300m);

        // Vendor onaylar → kapanır
        var shipped = svc.ApplyDecision(s3.Id, ShipmentDecision.ShipNow);
        shipped.Status.Should().Be(ShipmentStatus.Shipped);

        // Yeni alım → yeni Shipment açılır
        labels.Insert(MakeLabel("l4", 500m, addedAt: 400));
        var s4 = svc.GetOrCreateOpenShipment(Cid);
        s4.Id.Should().NotBe(s1.Id);
        s4.Status.Should().Be(ShipmentStatus.Pending);
        s4.CumulativeAmount.Should().Be(0m);
    }

    // ── U12: yerel taşımadan kalan açık dosyalar tek karar havuzu ───────

    /// <summary>Kopya ve asıl kayıt ayrı açık dosyalarla; push yanıtındaki taşıma ikisini aynı
    /// kişide bırakır (taşıma kargoları birleştirmez). Eski dosya daha önce açılmış.</summary>
    private static void TwoOpenAfterRekey(InMemorySqlite db, ShipmentRepository repo,
        decimal staleAmount, decimal newestAmount)
    {
        new CustomerRepository(db).Insert(new Customer("c-kopya", "instagram", "@A", null, null, 100, 100,
            false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        repo.Insert(new Shipment("kargo-eski", "c-kopya", ShipmentStatus.Pending, 50, null, null, staleAmount));
        repo.Insert(new Shipment("kargo-yeni", Cid, ShipmentStatus.Held, 60, 70, null, newestAmount));
        new CustomerSyncRepository(db).RekeyToLocal("c-kopya", Cid, pushedThroughSeq: long.MaxValue, nowUnix: 1_791_000_000)
            .Should().Be(RekeyResult.Rekeyed);
        repo.CountOpenByCustomer(Cid).Should().Be(2);
    }

    [Fact]
    public void Iki_acik_dosyada_esik_toplamdan_hesaplanir_ve_karar_ikisini_kapatir()
    {
        var (db, svc, repo, _, _, now) = Fx(threshold: 5000m);
        using var _d = db;
        TwoOpenAfterRekey(db, repo, staleAmount: 3000m, newestAmount: 2500m);

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);

        ctx.ShouldPrompt.Should().BeTrue();
        ctx.ThresholdReached.Should().BeTrue("eşik kişinin bütün açık dosyalarından (3000 + 2500)");
        ctx.AmountToThreshold.Should().Be(0m);
        ctx.Shipment!.Id.Should().Be("kargo-yeni", "karar en yeni dosya üstünden verilir");
        ctx.Shipment.CumulativeAmount.Should().Be(2500m, "Shipment nesnesi her zaman satırın aynası");
        ctx.PooledAmount.Should().Be(5500m, "çekmece havuzun toplamını gösterir");

        var shipped = svc.ApplyDecision(ctx.Shipment.Id, ShipmentDecision.ShipNow, out var pooled);

        pooled.Should().Be(5500m, "'kazandın' mesajı havuzun toplamını söyler");
        shipped.Should().BeEquivalentTo(repo.GetById("kargo-yeni"), "dönen dosya satırın aynası");
        repo.GetById("kargo-yeni")!.Status.Should().Be(ShipmentStatus.Shipped);
        var stale = repo.GetById("kargo-eski")!;
        stale.Status.Should().Be(ShipmentStatus.Shipped, "fazla dosya bir sonraki kararda kapanır — durum kendiliğinden düzelir");
        stale.ShippedAt.Should().Be(now);
        stale.CumulativeAmount.Should().Be(3000m, "dosyaların kendi tutarı değişmez");
        repo.GetById("kargo-yeni")!.CumulativeAmount.Should().Be(2500m);
        repo.CountOpenByCustomer(Cid).Should().Be(0);
    }

    [Fact]
    public void Karardan_sonra_eski_dosya_yeni_etiket_almaz()
    {
        var (db, svc, repo, labels, _, _) = Fx();
        using var _d = db;
        TwoOpenAfterRekey(db, repo, staleAmount: 3000m, newestAmount: 2500m);
        svc.ApplyDecision("kargo-yeni", ShipmentDecision.ShipNow);

        labels.Insert(MakeLabel("l-sonra", 100m));
        var open = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(open.Id, new[] { "l-sonra" });

        open.Id.Should().NotBe("kargo-eski", "eski toplamıyla yeni etiket alsaydı eşik kararı yanlış olurdu");
        open.Id.Should().NotBe("kargo-yeni");
        repo.GetLabelIds("kargo-eski").Should().BeEmpty();
        repo.GetById("kargo-eski")!.CumulativeAmount.Should().Be(3000m);
    }

    [Fact]
    public void Beklet_karari_ikisine_uygulanir_sonraki_etiket_en_yeniye_esik_havuzdan()
    {
        var (db, svc, repo, labels, _, now) = Fx(threshold: 5000m);
        using var _d = db;
        TwoOpenAfterRekey(db, repo, staleAmount: 1000m, newestAmount: 1500m);

        svc.ApplyDecision("kargo-yeni", ShipmentDecision.Hold);

        var stale = repo.GetById("kargo-eski")!;
        stale.Status.Should().Be(ShipmentStatus.Held);
        stale.HeldAt.Should().Be(now);
        repo.GetById("kargo-yeni")!.HeldAt.Should().Be(70, "ilk bekletme anı korunur");

        labels.Insert(MakeLabel("l-sonra", 3000m));
        var open = svc.GetOrCreateOpenShipment(Cid);
        open.Id.Should().Be("kargo-yeni");
        svc.AttachLabels(open.Id, new[] { "l-sonra" });

        var ctx = svc.EvaluateAfterPayment(Cid, allLabelsPaid: true);
        ctx.ThresholdReached.Should().BeTrue("1000 + 1500 + 3000");
        ctx.PooledAmount.Should().Be(5500m);
        ctx.Shipment!.CumulativeAmount.Should().Be(4500m);
    }

    [Fact]
    public void Tek_dosyada_havuz_dosyanin_kendi_tutari()
    {
        var (db, svc, repo, labels, _, _) = Fx(threshold: 5000m);
        using var _d = db;
        labels.Insert(MakeLabel("l1", 1200m));
        var s = svc.GetOrCreateOpenShipment(Cid);
        svc.AttachLabels(s.Id, new[] { "l1" });

        svc.EvaluateAfterPayment(Cid, allLabelsPaid: true).PooledAmount.Should().Be(1200m);
        svc.ApplyDecision(s.Id, ShipmentDecision.ShipNow, out var pooled);
        pooled.Should().Be(1200m);
        svc.EvaluateAfterPayment(Cid, allLabelsPaid: false).PooledAmount.Should().Be(0m, "sessiz bağlam");
    }

    [Fact]
    public void Karar_tek_islemde_ikinci_dosya_dusarse_ilki_de_geri_alinir()
    {
        var (db, svc, repo, _, _, _) = Fx();
        using var _d = db;
        TwoOpenAfterRekey(db, repo, staleAmount: 3000m, newestAmount: 2500m);
        using (var c = db.Open())
            c.Execute(@"CREATE TRIGGER kargo_bozuk BEFORE UPDATE ON Shipment WHEN NEW.Id = 'kargo-eski'
                        BEGIN SELECT RAISE(ABORT, 'disk dolu'); END");

        var act = () => svc.ApplyDecision("kargo-yeni", ShipmentDecision.ShipNow);

        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
        repo.GetById("kargo-yeni")!.Status.Should().Be(ShipmentStatus.Held,
            "karar bütün dosyalara birlikte uygulanır ya da hiçbirine — yarım havuz eşiği yanlış hesaplatırdı");
        repo.GetById("kargo-eski")!.Status.Should().Be(ShipmentStatus.Pending);
    }

    [Fact]
    public void Kapali_dosyaya_verilen_karar_havuza_dokunmaz()
    {
        // Hedef dosya karardan ÖNCE açık değildi (alıcı öder): o kişinin açık dosyaları bu
        // kararın konusu değil.
        var (db, svc, repo, _, _, _) = Fx();
        using var _d = db;
        repo.Insert(new Shipment("kargo-alici", Cid, ShipmentStatus.RecipientPays, 40, null, null, 700m));
        repo.Insert(new Shipment("kargo-acik", Cid, ShipmentStatus.Pending, 50, null, null, 300m));

        var shipped = svc.ApplyDecision("kargo-alici", ShipmentDecision.ShipNow, out var pooled);

        shipped.Status.Should().Be(ShipmentStatus.Shipped);
        pooled.Should().Be(700m);
        repo.GetById("kargo-acik")!.Status.Should().Be(ShipmentStatus.Pending);
    }
}
