using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class LabelServiceTests
{
    private static (LabelService Svc, LabelRepository Labels, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var labelRepo = new LabelRepository(db);
        var customerSvc = new CustomerService(customerRepo, new SessionRepository(db), labelRepo, clock);

        var svc = new LabelService(labelRepo, customerSvc, db, clock);
        return (svc, labelRepo, customerRepo, db, "s1");
    }

    private static ChatMessage Msg(string username = "@ayse_y", string text = "MAVI XL aldım") =>
        new(System.Guid.NewGuid().ToString("N"),
            "instagram", null, username, "Ayşe", null, text, 1000,
            System.Array.Empty<string>());

    [Fact]
    public void Add_creates_customer_and_unprinted_label()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        var label = svc.Add(sid, Msg(), price: 199m, code: "MAVI");

        label.Price.Should().Be(199m);
        label.PrintedAt.Should().BeNull();

        labels.GetUnprintedBySession(sid).Should().HaveCount(1);
        customers.FindByPlatformAndUsername("instagram", "@ayse_y").Should().NotBeNull();
    }

    [Fact]
    public void Add_called_twice_for_same_user_creates_two_labels_one_customer()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        svc.Add(sid, Msg(), 100m, "MAVI");
        svc.Add(sid, Msg(), 150m, "MAVI");

        labels.GetUnprintedBySession(sid).Should().HaveCount(2);
    }

    [Fact]
    public void MarkPrintedAndRecord_marks_labels_and_bumps_customer_aggregates()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l1 = svc.Add(sid, Msg(), 100m, null);
        var l2 = svc.Add(sid, Msg(), 150m, null);

        svc.MarkPrintedAndRecord(new[] { l1.Id, l2.Id });

        labels.GetUnprintedBySession(sid).Should().BeEmpty();
        var c = customers.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        c.TotalLabelsPrinted.Should().Be(2);
        c.TotalAmount.Should().Be(250m);
    }

    [Fact]
    public void MarkPrintedAndRecord_aggregates_per_customer()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var aLabel = svc.Add(sid, Msg("@a", "Mavi"), 100m, null);
        var bLabel = svc.Add(sid, Msg("@b", "Mavi"), 150m, null);

        svc.MarkPrintedAndRecord(new[] { aLabel.Id, bLabel.Id });

        customers.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(100m);
        customers.FindByPlatformAndUsername("instagram", "@b")!.TotalAmount.Should().Be(150m);
    }

    // ── R7-04: yazdırmanın ekonomik etkisi bir kez ────────────────────
    //
    // MarkPrintedAndRecord transaction içinde çalışıyor (atomik), ama aynı
    // transaction'ın iki kez BAŞARILI olması idempotent olduğu anlamına
    // gelmiyor. Baskı arka planda beklerken EndStream/ClearQueue gibi başka
    // komutlar aynı etiketi tekrar gönderebiliyor.

    [Fact]
    public void MarkPrintedAndRecord_ayni_etiket_ikinci_kez_ciroyu_tekrar_artirmaz()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l = svc.Add(sid, Msg(), 100m, null);

        svc.MarkPrintedAndRecord(new[] { l.Id });
        svc.MarkPrintedAndRecord(new[] { l.Id });

        var c = customers.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        c.TotalLabelsPrinted.Should().Be(1, "ekonomik tamamlanma etiketin kendisine ait");
        c.TotalAmount.Should().Be(100m);
    }

    [Fact]
    public void MarkPrintedAndRecord_iptal_edilmis_etiketi_ciroya_yazmaz()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l = svc.Add(sid, Msg(), 100m, null);
        svc.Cancel(new[] { l.Id }, CancelReasonCodes.Customer);

        // Kuyruk temizlendikten sonra arka plandaki baskı işi aynı satırı
        // "basıldı" diye geri yazıyordu: iptal edilmiş etiket hem PrintedAt
        // hem +100 ciro alıyordu.
        svc.MarkPrintedAndRecord(new[] { l.Id });

        var c = customers.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        c.TotalAmount.Should().Be(0m);
        c.TotalLabelsPrinted.Should().Be(0);
        labels.GetById(l.Id)!.PrintedAt.Should().BeNull("iptal edilmiş iş basılmış sayılmaz");
    }

    [Fact] // karışık paket: yalnız uygun satır ilerler, diğerleri paketi bozmaz
    public void MarkPrintedAndRecord_karisik_pakette_yalniz_taze_etiketi_sayar()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var basili = svc.Add(sid, Msg(), 100m, null);
        var iptal = svc.Add(sid, Msg(), 40m, null);
        var taze = svc.Add(sid, Msg(), 60m, null);
        svc.MarkPrintedAndRecord(new[] { basili.Id });
        svc.Cancel(new[] { iptal.Id }, CancelReasonCodes.Customer);

        svc.MarkPrintedAndRecord(new[] { basili.Id, iptal.Id, taze.Id });

        var c = customers.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        c.TotalAmount.Should().Be(160m, "100 (ilk baskı) + 60 (taze) — iptal ve tekrar hariç");
        c.TotalLabelsPrinted.Should().Be(2);
        labels.GetById(taze.Id)!.PrintedAt.Should().NotBeNull();
        labels.GetById(iptal.Id)!.PrintedAt.Should().BeNull();
    }

    // ── Cancel / Uncancel ─────────────────────────────────────────────

    [Fact]
    public void Cancel_marks_labels_and_subtracts_revenue_from_customer()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        var l1 = svc.Add(sid, Msg("@ayse", "Kapuçino"), 75m, null);
        var l2 = svc.Add(sid, Msg("@ayse", "Latte"), 90m, null);
        svc.MarkPrintedAndRecord(new[] { l1.Id, l2.Id });

        var cust = customers.FindByPlatformAndUsername("instagram", "@ayse")!;
        cust.TotalLabelsPrinted.Should().Be(2);
        cust.TotalAmount.Should().Be(165m);

        svc.Cancel(new[] { l1.Id }, CancelReasonCodes.Customer);

        var refreshed = customers.GetById(cust.Id)!;
        refreshed.TotalLabelsPrinted.Should().Be(1);
        refreshed.TotalAmount.Should().Be(90m);

        // Persisted state on the label.
        var stored = labels.GetById(l1.Id)!;
        stored.CancelledAt.Should().NotBeNull();
        stored.CancelReason.Should().Be(CancelReasonCodes.Customer);
    }

    [Fact]
    public void Cancel_excludes_cancelled_rows_from_session_totals()
    {
        var (svc, labels, _, db, sid) = Fx();
        using var _ = db;

        var l1 = svc.Add(sid, Msg("@a", "X"), 100m, null);
        var l2 = svc.Add(sid, Msg("@b", "Y"), 150m, null);
        svc.MarkPrintedAndRecord(new[] { l1.Id, l2.Id });
        svc.Cancel(new[] { l1.Id }, CancelReasonCodes.WrongProduct);

        var totals = labels.GetSessionTotals(sid);
        totals.PrintedCount.Should().Be(1);
        totals.TotalAmount.Should().Be(150m);
        totals.UniqueCustomers.Should().Be(1);
    }

    [Fact]
    public void Cancel_already_cancelled_is_noop()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l = svc.Add(sid, Msg("@a", "X"), 100m, null);
        svc.MarkPrintedAndRecord(new[] { l.Id });

        svc.Cancel(new[] { l.Id }, CancelReasonCodes.Customer);
        svc.Cancel(new[] { l.Id }, CancelReasonCodes.Customer);  // second call: no-op

        // Aggregate must not double-subtract.
        var cust = customers.FindByPlatformAndUsername("instagram", "@a")!;
        cust.TotalLabelsPrinted.Should().Be(0);
        cust.TotalAmount.Should().Be(0m);
    }

    [Fact]
    public void Uncancel_restores_revenue_and_clears_flags()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l = svc.Add(sid, Msg("@a", "X"), 200m, null);
        svc.MarkPrintedAndRecord(new[] { l.Id });
        svc.Cancel(new[] { l.Id }, CancelReasonCodes.Customer);

        svc.Uncancel(new[] { l.Id });

        var stored = labels.GetById(l.Id)!;
        stored.CancelledAt.Should().BeNull();
        stored.CancelReason.Should().BeNull();

        var cust = customers.FindByPlatformAndUsername("instagram", "@a")!;
        cust.TotalLabelsPrinted.Should().Be(1);
        cust.TotalAmount.Should().Be(200m);
    }

    [Fact]
    public void Cancel_unprinted_label_does_not_touch_revenue()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l = svc.Add(sid, Msg("@a", "X"), 100m, null);
        // not printed → aggregates were never incremented
        svc.Cancel(new[] { l.Id }, CancelReasonCodes.OutOfStock);

        var cust = customers.FindByPlatformAndUsername("instagram", "@a")!;
        cust.TotalLabelsPrinted.Should().Be(0);
        cust.TotalAmount.Should().Be(0m);

        labels.GetById(l.Id)!.CancelledAt.Should().NotBeNull();
    }

    [Fact]
    public void GetByCustomerAndSession_returns_only_that_sessions_labels_oldest_first()
    {
        var (svc, labels, _, db, sid) = Fx();
        using var _ = db;

        // Seed a second session and a label in it.
        new SessionRepository(db).Insert(
            new StreamSession("s2", null, 2000, null, new[] { "instagram" }, null));

        var first = svc.Add("s1", Msg("@a", "Bu yayın"), 50m, null);
        var older = svc.Add("s1", Msg("@a", "Daha önce bu yayında"), 60m, null);
        var otherSession = svc.Add("s2", Msg("@a", "Başka yayında"), 70m, null);

        var cust = otherSession.CustomerId;
        var rows = labels.GetByCustomerAndSession(cust, "s1");

        rows.Should().HaveCount(2);
        rows.Should().NotContain(r => r.Id == otherSession.Id);
        // Oldest-first ordering.
        rows[0].Id.Should().Be(first.Id);
        rows[1].Id.Should().Be(older.Id);
    }
}
