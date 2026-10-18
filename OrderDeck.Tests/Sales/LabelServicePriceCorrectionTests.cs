using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// "Basılanlar" sekmesinin veri tarafı (göç 048) ve toplu fiyat düzeltme (sahip kararları K1–K6).
/// Fiyat etiket yazılırken her etikete kopyalanıyor; sonradan değiştiren tek yol
/// <see cref="LabelService.CorrectPrices"/> ve müşteri toplamı, kargo dosyası, senkron, stok aynı
/// pakette doğru kalmalı.
/// </summary>
public class LabelServicePriceCorrectionTests
{
    private sealed record Fx(
        InMemorySqlite Db, LabelService Svc, LabelRepository Labels, CustomerRepository Customers,
        ShipmentRepository Shipments, Mock<IClock> Clock) : IDisposable
    {
        public void Dispose() => Db.Dispose();

        public Label Add(string user, decimal price, string code = "A12")
            => Svc.Add("s1", Msg(user), price, code);

        public Customer Customer(string user) => Customers.FindByPlatformAndUsername("instagram", user)!;
    }

    private static ChatMessage Msg(string user) => new(
        Guid.NewGuid().ToString("N"), "instagram", null, user, user, null, $"{user} aldım", 1000,
        Array.Empty<string>());

    private static Fx Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        // Y5'in tabanı açılışta türetilmiş say (ShipmentAmountDerivation): damga boşken dosya bloğu yazmaz.
        using (var c = db.Open()) c.Execute("UPDATE SalesSyncState SET ShipmentsDerivedAt = 900 WHERE Id = 1");
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        var customers = new CustomerRepository(db);
        var labels = new LabelRepository(db);
        var customerSvc = new CustomerService(customers, new SessionRepository(db), labels, clock.Object);
        var shipments = new ShipmentRepository(db);
        var svc = new LabelService(labels, customerSvc, db, clock.Object, shipments);
        return new Fx(db, svc, labels, customers, shipments, clock);
    }

    // ── Basış grupları (K1) ────────────────────────────────────────────

    [Fact]
    public void Each_print_press_is_a_group_in_paper_order()
    {
        using var fx = Build();
        var a = fx.Add("@a", 100m);
        var b = fx.Add("@b", 100m);
        var c = fx.Add("@c", 100m);

        // Seçili satırlar seçim sırasıyla basılır — kâğıt sırası AddedAt değil.
        fx.Svc.MarkPrintedAndRecord(new[] { c.Id, a.Id });
        fx.Svc.MarkPrintedAndRecord(new[] { b.Id });

        var rows = fx.Labels.GetPrintedBySession("s1");
        rows.Should().HaveCount(3);
        var first = rows.Where(r => r.Label.Id is var id && (id == c.Id || id == a.Id)).ToList();
        first.Select(r => r.BatchKey).Distinct().Should().ContainSingle("tek Yazdır basışı = tek grup");
        first.Single(r => r.Label.Id == c.Id).PrintSeq.Should().Be(1);
        first.Single(r => r.Label.Id == a.Id).PrintSeq.Should().Be(2);
        rows.Single(r => r.Label.Id == b.Id).BatchKey.Should().NotBe(first[0].BatchKey,
            "aynı saniyedeki ikinci basış ayrı grup — PrintedAt saniyelik, bunu ayıramazdı");
    }

    [Fact]
    public void Legacy_rows_without_batch_group_by_printed_at()
    {
        using var fx = Build();
        var a = fx.Add("@a", 100m);
        var b = fx.Add("@b", 100m);
        fx.Labels.MarkPrinted(new[] { a.Id, b.Id }, 1500); // 048 öncesi yol: basış kimliği yok

        var rows = fx.Labels.GetPrintedBySession("s1");
        rows.Should().OnlyContain(r => r.PrintBatchId == null && r.PrintSeq == null);
        rows.Select(r => r.BatchKey).Distinct().Should().ContainSingle();
    }

    [Fact]
    public void Printed_list_excludes_cancelled_and_unprinted()
    {
        using var fx = Build();
        var printed = fx.Add("@a", 100m);
        var cancelled = fx.Add("@b", 100m);
        fx.Add("@c", 100m); // kuyrukta
        fx.Svc.MarkPrintedAndRecord(new[] { printed.Id, cancelled.Id });
        fx.Svc.Cancel(new[] { cancelled.Id }, CancelReasonCodes.WrongProduct);

        fx.Labels.GetPrintedBySession("s1").Should().ContainSingle().Which.Label.Id.Should().Be(printed.Id);
    }

    // ── Fiyat düzeltme (K5) ────────────────────────────────────────────

    [Fact]
    public void Correcting_printed_labels_moves_customer_totals_by_the_difference()
    {
        using var fx = Build();
        var a1 = fx.Add("@a", 250m);
        var a2 = fx.Add("@a", 250m);
        var b = fx.Add("@b", 250m);
        fx.Svc.MarkPrintedAndRecord(new[] { a1.Id, a2.Id, b.Id });

        var result = fx.Svc.CorrectPrices(new[] { a1.Id, a2.Id, b.Id }, 205m);

        result.Corrected.Should().HaveCount(3);
        fx.Customer("@a").TotalAmount.Should().Be(410m, "2 × 205");
        fx.Customer("@a").TotalLabelsPrinted.Should().Be(2, "adet değişmez");
        fx.Customer("@b").TotalAmount.Should().Be(205m);
        fx.Labels.GetSessionTotals("s1").TotalAmount.Should().Be(615m);
        fx.Labels.GetById(a1.Id)!.Price.Should().Be(205m);
    }

    [Fact]
    public void Correcting_a_queued_label_does_not_touch_totals_until_it_prints()
    {
        using var fx = Build();
        var q = fx.Add("@a", 250m);

        fx.Svc.CorrectPrices(new[] { q.Id }, 205m);
        fx.Customer("@a").TotalAmount.Should().Be(0m, "basılmamış satır ciroya girmemişti");

        fx.Svc.MarkPrintedAndRecord(new[] { q.Id });
        fx.Customer("@a").TotalAmount.Should().Be(205m, "basılınca yeni fiyatla yazılır");
    }

    [Fact]
    public void Tentative_backup_price_changes_but_customer_total_does_not()
    {
        using var fx = Build();
        var parent = fx.Add("@asil", 250m);
        var backup = fx.Svc.AddBackup(parent.Id, "instagram", "@yedek", "Yedek", "ben de");
        fx.Svc.MarkPrintedAndRecord(new[] { parent.Id, backup.Id });

        fx.Svc.CorrectPrices(new[] { parent.Id, backup.Id }, 205m);

        fx.Labels.GetById(backup.Id)!.Price.Should().Be(205m, "yedek ana etiketin fiyatını taşıyordu");
        fx.Customer("@yedek").TotalAmount.Should().Be(0m, "onaysız yedek ciroya girmez — düzeltmede de");
        fx.Customer("@asil").TotalAmount.Should().Be(205m);

        fx.Svc.ConfirmBackup(backup.Id);
        fx.Customer("@yedek").TotalAmount.Should().Be(205m, "onayda yeni fiyatla yazılır");
    }

    [Fact]
    public void Open_shipment_total_follows_the_correction_shipped_one_is_untouched()
    {
        using var fx = Build();
        var openLabel = fx.Add("@a", 250m);
        var shippedLabel = fx.Add("@b", 250m);
        fx.Svc.MarkPrintedAndRecord(new[] { openLabel.Id, shippedLabel.Id });

        var shipmentSvc = new ShipmentService(fx.Shipments, fx.Labels, LocalShipping.Off());
        var open = shipmentSvc.GetOrCreateOpenShipment(openLabel.CustomerId);
        shipmentSvc.AttachLabels(open.Id, new[] { openLabel.Id });
        var gone = shipmentSvc.GetOrCreateOpenShipment(shippedLabel.CustomerId);
        shipmentSvc.AttachLabels(gone.Id, new[] { shippedLabel.Id });
        shipmentSvc.ApplyDecision(gone.Id, ShipmentDecision.ShipNow);

        var result = fx.Svc.CorrectPrices(new[] { openLabel.Id, shippedLabel.Id }, 205m);

        fx.Shipments.GetById(open.Id)!.CumulativeAmount.Should().Be(205m);
        fx.Shipments.GetById(open.Id)!.SyncedAt.Should().BeNull("dosya toplamı sunucuya da gitmeli");
        fx.Shipments.GetById(gone.Id)!.CumulativeAmount.Should().Be(250m, "kargoya verilmiş dosya DEĞİŞMEZ");
        fx.Labels.GetById(shippedLabel.Id)!.Price.Should().Be(250m, "kargoya verilmiş etiket değişmez");
        fx.Customer("@b").TotalAmount.Should().Be(250m);
        result.Shipped.Should().ContainSingle().Which.Id.Should().Be(shippedLabel.Id);
        result.Corrected.Should().ContainSingle().Which.Before.Id.Should().Be(openLabel.Id);
    }

    [Fact]
    public void Correction_requeues_sync_but_not_stock()
    {
        using var fx = Build();
        var l = fx.Add("@a", 250m);
        fx.Svc.MarkPrintedAndRecord(new[] { l.Id });
        var revision = fx.Labels.GetById(l.Id)!.Revision;
        fx.Labels.MarkSynced(l.Id, 2000, revision);
        fx.Labels.GetUnsynced().Should().BeEmpty();

        fx.Svc.CorrectPrices(new[] { l.Id }, 205m);

        using var conn = fx.Db.Open();
        conn.ExecuteScalar<long?>("SELECT StockSyncedAt FROM Label WHERE Id=@id", new { id = l.Id })
            .Should().Be(2000, "fiyat stok-ilgili değil — satır stok açısından yeniden bekleyen olmamalı");
        fx.Labels.GetUnsynced().Should().ContainSingle()
            .Which.Price.Should().Be(205m, "yeni fiyat mevcut senkronla sunucuya gider");
        fx.Labels.GetById(l.Id)!.Revision.Should().Be(revision + 1, "uçuştaki onay düzeltmeyi ezemez (F05)");
    }

    [Fact]
    public void Cancelled_shipping_fee_and_same_price_labels_are_left_alone()
    {
        using var fx = Build();
        var cancelled = fx.Add("@a", 250m);
        var same = fx.Add("@b", 205m);
        var fee = fx.Svc.AddShippingFee("s1", fx.Customer("@a"), 50m);
        fx.Svc.Cancel(new[] { cancelled.Id }, CancelReasonCodes.Customer);

        var plan = fx.Svc.PlanPriceCorrection(new[] { cancelled.Id, same.Id, fee.Id });
        plan.Correctable.Should().ContainSingle().Which.Id.Should().Be(same.Id);

        var result = fx.Svc.CorrectPrices(new[] { cancelled.Id, same.Id, fee.Id }, 205m);

        result.Corrected.Should().BeEmpty();
        fx.Labels.GetById(cancelled.Id)!.Price.Should().Be(250m);
        fx.Labels.GetById(fee.Id)!.Price.Should().Be(50m);
    }

    [Fact]
    public void Plan_lists_price_groups_of_the_selection()
    {
        using var fx = Build();
        var ids = Enumerable.Range(0, 3).Select(i => fx.Add($"@a{i}", 250m).Id)
            .Append(fx.Add("@b", 300m).Id).ToList();

        fx.Svc.PlanPriceCorrection(ids).PriceGroups.Should().Equal(
            new PriceGroup(250m, 3), new PriceGroup(300m, 1));
    }

    [Fact]
    public void Stale_row_is_rejected_by_the_conditional_write()
    {
        // Okuma ile yazma arasında satır değiştiyse (ör. fiyatı başka yoldan değişti) koşullu yazım
        // 0 satır etkiler — servis paketi geri sarar.
        using var fx = Build();
        var l = fx.Add("@a", 250m);
        using var write = DbWrite.Begin(fx.Db);

        fx.Labels.UpdatePriceForCorrection(l.Id, expectedPrice: 300m, expectPrinted: false,
            expectTentative: false, paidFromBalance: false, newPrice: 205m, correctedAt: 1000, write).Should().Be(0);
        fx.Labels.UpdatePriceForCorrection(l.Id, expectedPrice: 250m, expectPrinted: true,
            expectTentative: false, paidFromBalance: false, newPrice: 205m, correctedAt: 1000, write).Should().Be(0,
            "basılmışlık durumu da değişmiş sayılır");
        fx.Labels.UpdatePriceForCorrection(l.Id, expectedPrice: 250m, expectPrinted: false,
            expectTentative: false, paidFromBalance: false, newPrice: 205m, correctedAt: 1000, write).Should().Be(1);
    }

    [Fact]
    public void Negative_price_is_refused()
    {
        using var fx = Build();
        var l = fx.Add("@a", 250m);

        var act = () => fx.Svc.CorrectPrices(new[] { l.Id }, -1m);

        act.Should().Throw<ArgumentOutOfRangeException>();
        fx.Labels.GetById(l.Id)!.Price.Should().Be(250m);
    }

    // ── Yeniden baskı (K3/K5) ──────────────────────────────────────────

    [Fact]
    public void Reprint_keeps_first_print_and_revenue_and_marks_corrected_labels()
    {
        using var fx = Build();
        var corrected = fx.Add("@a", 250m);
        var plain = fx.Add("@b", 250m);
        fx.Svc.MarkPrintedAndRecord(new[] { corrected.Id, plain.Id });
        var before = fx.Labels.GetPrintedBySession("s1").ToDictionary(r => r.Label.Id);
        fx.Svc.CorrectPrices(new[] { corrected.Id }, 205m);
        fx.Clock.Setup(c => c.UnixNow()).Returns(5000L);

        var reprint = fx.Svc.PrepareReprint(new[] { plain.Id, corrected.Id });

        reprint.Labels.Select(l => l.Id).Should().Equal(plain.Id, corrected.Id);
        reprint.Labels.Single(l => l.Id == corrected.Id).Price.Should().Be(205m, "kâğıda GÜNCEL fiyat basılır");
        reprint.CorrectionIds.Should().BeEquivalentTo(new[] { corrected.Id });
        var after = fx.Labels.GetPrintedBySession("s1").ToDictionary(r => r.Label.Id);
        after[plain.Id].Label.PrintedAt.Should().Be(before[plain.Id].Label.PrintedAt);
        after[plain.Id].BatchKey.Should().Be(before[plain.Id].BatchKey, "yeniden baskı grubu değiştirmez");
        fx.Labels.GetSessionTotals("s1").TotalAmount.Should().Be(455m, "ciro ikinci kez artmaz");
    }

    // ── Bir yayında bir kodun TEK fiyatı (sahip kararı F1/F3) ──────────

    private static Label AddIn(Fx fx, string sessionId, string user, decimal price, string? code)
        => fx.Svc.Add(sessionId, Msg(user), price, code);

    [Fact]
    public void Code_price_is_read_from_the_sessions_uncancelled_labels_of_that_code()
    {
        using var fx = Build();
        new SessionRepository(fx.Db).Insert(new StreamSession("s2", null, 900, null, new[] { "instagram" }, null));
        fx.Svc.GetCodePrice("s1", "A1").Should().BeNull("kodun bu yayında etiketi yok");
        AddIn(fx, "s2", "@eski", 999m, "A1");                  // başka yayın
        var cancelled = AddIn(fx, "s1", "@iptal", 500m, "A1");
        fx.Svc.Cancel(new[] { cancelled.Id }, CancelReasonCodes.Customer);
        AddIn(fx, "s1", "@b7", 80m, "B7");                     // başka kod
        AddIn(fx, "s1", "@a", 250m, "A1");
        AddIn(fx, "s1", "@b", 250m, "A1");

        var price = fx.Svc.GetCodePrice("s1", "a1 ");

        price.Should().NotBeNull("kod harf duyarsız, kırpılmış");
        price!.Price.Should().Be(250m, "başka yayın, iptal edilmiş etiket ve başka kod sayılmaz");
        price.Correctable.Should().Be(2);
        price.IsMixed.Should().BeFalse();
    }

    [Fact]
    public void Code_price_of_mixed_labels_is_the_most_common_one_then_the_latest_written()
    {
        using var fx = Build();
        AddIn(fx, "s1", "@a", 250m, "A1");
        AddIn(fx, "s1", "@b", 230m, "A1");                     // ör. pazarlıklı yedek

        fx.Svc.GetCodePrice("s1", "A1")!.Price.Should().Be(230m, "eşitlikte en son yazılanınki");

        AddIn(fx, "s1", "@c", 250m, "A1");
        var price = fx.Svc.GetCodePrice("s1", "A1")!;
        price.Price.Should().Be(250m, "en çok tekrarlanan fiyat");
        price.IsMixed.Should().BeTrue();
        price.Groups.Should().Equal(new PriceGroup(250m, 2), new PriceGroup(230m, 1));
    }

    [Fact]
    public void Code_price_counts_labels_in_a_shipped_file_as_unchangeable()
    {
        using var fx = Build();
        var shippedLabel = AddIn(fx, "s1", "@a", 250m, "A1");
        fx.Svc.MarkPrintedAndRecord(new[] { shippedLabel.Id });
        var shipmentSvc = new ShipmentService(fx.Shipments, fx.Labels, LocalShipping.Off());
        var gone = shipmentSvc.GetOrCreateOpenShipment(shippedLabel.CustomerId);
        shipmentSvc.AttachLabels(gone.Id, new[] { shippedLabel.Id });
        shipmentSvc.ApplyDecision(gone.Id, ShipmentDecision.ShipNow);

        var onlyShipped = fx.Svc.GetCodePrice("s1", "A1")!;
        onlyShipped.Price.Should().Be(250m, "kodun bu yayındaki fiyatı — değiştirilemese de");
        onlyShipped.Correctable.Should().Be(0, "kargoya verilmiş etiket düzelmez");
        onlyShipped.Shipped.Should().Be(1);

        AddIn(fx, "s1", "@b", 200m, "A1");
        var mixed = fx.Svc.GetCodePrice("s1", "A1")!;
        mixed.Price.Should().Be(200m, "düzeltilebilir etiketlerin fiyatı kargoya verilmişlerden önce gelir");
        mixed.Correctable.Should().Be(1);
    }

    [Fact]
    public void Code_plan_covers_every_label_of_the_selected_codes_in_the_session()
    {
        using var fx = Build();
        new SessionRepository(fx.Db).Insert(new StreamSession("s2", null, 900, null, new[] { "instagram" }, null));
        var a = AddIn(fx, "s1", "@a", 250m, "A1");
        var b = AddIn(fx, "s1", "@b", 250m, "A1");
        var other = AddIn(fx, "s1", "@c", 80m, "B7");
        var carried = AddIn(fx, "s2", "@d", 250m, "a1");       // önceki yayından kuyruğa taşınmış, seçili
        var codeless1 = AddIn(fx, "s1", "@e", 40m, null);
        AddIn(fx, "s1", "@f", 40m, null);                      // kodsuz, seçilmedi

        var plans = fx.Svc.PlanCodeCorrection("s1", new[] { a.Id, codeless1.Id, carried.Id });

        plans.Select(p => p.Code).Should().Equal("A1", null);
        plans[0].Plan.Correctable.Select(l => l.Id).Should().BeEquivalentTo(new[] { a.Id, b.Id, carried.Id },
            "kodun yayındaki BÜTÜN etiketleri + seçimde o koddan olanlar; başka kod girmez");
        plans[1].Plan.Correctable.Select(l => l.Id).Should().Equal(new[] { codeless1.Id },
            "kodsuz etikette genişletilecek kod yok: yalnız seçilen");
        plans.SelectMany(p => p.Plan.Correctable).Should().NotContain(l => l.Id == other.Id);

        fx.Svc.PlanCodeCorrection("s1", "a1").Plan.Correctable.Select(l => l.Id)
            .Should().BeEquivalentTo(new[] { a.Id, b.Id });
    }
}
