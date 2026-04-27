using FluentAssertions;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Sales;

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
        var customerSvc = new CustomerService(customerRepo, clock);
        var labelRepo = new LabelRepository(db);

        var svc = new LabelService(labelRepo, customerSvc, clock);
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
}
