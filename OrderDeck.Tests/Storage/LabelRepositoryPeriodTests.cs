using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Dönem raporu sorgusu (muhasebe Excel'ini besleyen kaynak). Fatura tutarını
/// doğrudan etkilediği için sınırlar ve hariç tutma kuralları tek tek test edilir.
/// </summary>
public class LabelRepositoryPeriodTests
{
    private const long From = 1_000;   // dönem başı (dahil)
    private const long To = 2_000;     // dönem sonu (hariç)

    private static (InMemorySqlite Db, LabelRepository Labels, CustomerRepository Customers) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        return (db, new LabelRepository(db), new CustomerRepository(db));
    }

    private static Customer Cust(
        string id, string platform = "instagram", string username = "@a",
        string? fullName = null, string? phone = null, string? address = null,
        string? groupId = null, string? tckn = null, long lastSeenAt = 100) =>
        new(id, platform, username, DisplayName: null, AvatarUrl: null,
            FirstSeenAt: 100, LastSeenAt: lastSeenAt, IsBlacklisted: false,
            BlacklistReason: null, Notes: null, TotalLabelsPrinted: 0, TotalAmount: 0m,
            BlacklistedAt: null, Address: address, Phone: phone,
            GroupId: groupId, Tckn: tckn, FullName: fullName);

    private static Label Lbl(
        string id, string customerId, decimal price, long? printedAt,
        string platform = "instagram", bool shippingFee = false,
        bool tentative = false) =>
        new(id, "s1", customerId, platform, "@a", "ürün", "KOD", price,
            AddedAt: 500, PrintedAt: printedAt,
            IsTentativeBackup: tentative, IsShippingFee: shippingFee);

    [Fact]
    public void Period_includes_only_labels_printed_inside_the_range()
    {
        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1"));

        labels.Insert(Lbl("before", "c1", 10m, printedAt: From - 1));
        labels.Insert(Lbl("start", "c1", 20m, printedAt: From));       // alt sınır DAHİL
        labels.Insert(Lbl("inside", "c1", 30m, printedAt: 1_500));
        labels.Insert(Lbl("end", "c1", 40m, printedAt: To));           // üst sınır HARİÇ
        labels.Insert(Lbl("after", "c1", 50m, printedAt: To + 1));

        var rows = labels.GetPeriodAccountRows(From, To);

        rows.Should().ContainSingle();
        rows[0].OrderCount.Should().Be(2);
        rows[0].TotalAmount.Should().Be(50m);
    }

    [Fact]
    public void Period_excludes_unprinted_cancelled_and_tentative_labels()
    {
        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1"));

        labels.Insert(Lbl("ok", "c1", 100m, printedAt: 1_500));
        labels.Insert(Lbl("unprinted", "c1", 100m, printedAt: null));
        labels.Insert(Lbl("tentative", "c1", 100m, printedAt: 1_500, tentative: true));
        // Insert CancelledAt yazmaz — iptal ayrı UPDATE ile yapılır.
        labels.Insert(Lbl("cancelled", "c1", 100m, printedAt: 1_500));
        labels.MarkCancelled(new[] { "cancelled" }, 1_600, "müşteri vazgeçti");

        var rows = labels.GetPeriodAccountRows(From, To);

        rows.Should().ContainSingle();
        rows[0].OrderCount.Should().Be(1);
        rows[0].TotalAmount.Should().Be(100m);
    }

    [Fact]
    public void Period_includes_shipping_fee_rows_in_the_total()
    {
        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1"));

        labels.Insert(Lbl("urun", "c1", 100m, printedAt: 1_500));
        labels.Insert(Lbl("kargo", "c1", 25m, printedAt: 1_500, shippingFee: true));

        var rows = labels.GetPeriodAccountRows(From, To);

        rows[0].TotalAmount.Should().Be(125m);
    }

    [Fact]
    public void Period_carries_invoice_fields_from_the_customer_row()
    {
        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1", fullName: "Ayşe Yılmaz", phone: "+905551112233",
            address: "Moda Cad. 5", tckn: "12345678901"));
        labels.Insert(Lbl("l1", "c1", 100m, printedAt: 1_500));

        var row = labels.GetPeriodAccountRows(From, To).Single();

        row.FullName.Should().Be("Ayşe Yılmaz");
        row.Phone.Should().Be("+905551112233");
        row.Address.Should().Be("Moda Cad. 5");
        row.Tckn.Should().Be("12345678901");
    }

    [Fact]
    public void Period_returns_one_row_per_platform_account()
    {
        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1", platform: "instagram", groupId: "g1"));
        customers.Insert(Cust("c2", platform: "youtube", username: "UCxx", groupId: "g1"));

        labels.Insert(Lbl("l1", "c1", 100m, printedAt: 1_500, platform: "instagram"));
        labels.Insert(Lbl("l2", "c2", 200m, printedAt: 1_500, platform: "youtube"));

        var rows = labels.GetPeriodAccountRows(From, To);

        rows.Should().HaveCount(2);
        rows.Select(r => r.GroupId).Should().AllBe("g1");
    }

    [Fact]
    public void Period_splits_the_same_account_into_one_row_per_day()
    {
        // Fatura günlük kesildiği için sorgu gün bazında gruplamalı.
        // Saatler öğle vaktine ayarlı: yerel/UTC farkı gün sınırını kaydırmasın.
        const long day1Noon = 1_782_511_200;  // 2026-07-07 12:00 UTC
        const long day1Late = day1Noon + 3600;
        const long day2Noon = day1Noon + 86_400;

        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1"));

        labels.Insert(Lbl("a", "c1", 100m, printedAt: day1Noon));
        labels.Insert(Lbl("b", "c1", 50m, printedAt: day1Late));
        labels.Insert(Lbl("c", "c1", 70m, printedAt: day2Noon));

        var rows = labels.GetPeriodAccountRows(day1Noon, day2Noon + 3600);

        rows.Should().HaveCount(2);
        rows.Select(r => r.Day).Distinct().Should().HaveCount(2);

        var first = rows.Single(r => r.TotalAmount == 150m);
        first.OrderCount.Should().Be(2);
        first.LastPrintedAt.Should().Be(day1Late);   // fatura saati günün son etiketi
    }

    [Fact]
    public void Period_is_empty_when_nothing_printed_in_range()
    {
        var (db, labels, customers) = Fx();
        using var _ = db;
        customers.Insert(Cust("c1"));
        labels.Insert(Lbl("l1", "c1", 100m, printedAt: To + 500));

        labels.GetPeriodAccountRows(From, To).Should().BeEmpty();
    }
}
