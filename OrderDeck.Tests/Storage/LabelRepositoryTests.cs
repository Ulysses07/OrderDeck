using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class LabelRepositoryTests
{
    private static (InMemorySqlite Db, LabelRepository Repo, string SessionId, string CustomerId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        return (db, new LabelRepository(db), "s1", "c1");
    }

    private static Label MakeLabel(string id, string sessionId, string customerId,
        decimal price = 100m, long? printedAt = null) =>
        new(id, sessionId, customerId, "instagram", "@a", "Mavi XL aldım", "MAVI",
            price, AddedAt: 200, PrintedAt: printedAt);

    [Fact]
    public void Insert_then_GetUnprinted_returns_inserted_label()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;

        repo.Insert(MakeLabel("l1", sid, cid));

        var unprinted = repo.GetUnprintedBySession(sid);
        unprinted.Should().HaveCount(1);
        unprinted[0].MessageText.Should().Be("Mavi XL aldım");
    }

    [Fact]
    public void Delete_removes_label_from_unprinted()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(MakeLabel("l1", sid, cid));

        repo.Delete("l1");

        repo.GetUnprintedBySession(sid).Should().BeEmpty();
    }

    [Fact]
    public void MarkPrinted_excludes_from_unprinted_and_sets_PrintedAt()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(MakeLabel("l1", sid, cid));

        repo.MarkPrinted(new[] { "l1" }, printedAt: 999);

        repo.GetUnprintedBySession(sid).Should().BeEmpty();
        var totals = repo.GetSessionTotals(sid);
        totals.PrintedCount.Should().Be(1);
    }

    [Fact]
    public void GetSessionTotals_aggregates_printed_only()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(MakeLabel("l1", sid, cid, price: 100m, printedAt: 500));
        repo.Insert(MakeLabel("l2", sid, cid, price: 150m, printedAt: 600));
        repo.Insert(MakeLabel("l3", sid, cid, price: 200m, printedAt: null));

        var t = repo.GetSessionTotals(sid);

        t.PrintedCount.Should().Be(2);
        t.TotalAmount.Should().Be(250m);
        t.UniqueCustomers.Should().Be(1);
    }

    [Fact]
    public void GetTopCustomersBySession_orders_by_amount_desc()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        var customers = new CustomerRepository(db);
        customers.Insert(new Customer("c2", "instagram", "@b", null, null,
            100, 100, false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        customers.Insert(new Customer("c3", "instagram", "@c", null, null,
            100, 100, false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        repo.Insert(MakeLabel("l1", sid, "c1", price: 100m, printedAt: 500));
        repo.Insert(MakeLabel("l2", sid, "c1", price: 100m, printedAt: 500));
        repo.Insert(MakeLabel("l3", sid, "c2", price: 500m, printedAt: 500));
        repo.Insert(MakeLabel("l4", sid, "c3", price: 50m,  printedAt: 500));

        var top = repo.GetTopCustomersBySession(sid, limit: 5);

        top.Should().HaveCount(3);
        top[0].Username.Should().Be("@b");
        top[0].LabelCount.Should().Be(1);
        top[0].TotalAmount.Should().Be(500m);
        top[1].Username.Should().Be("@a");
        top[1].LabelCount.Should().Be(2);
    }

    [Fact]
    public void GetByCustomer_returns_labels_ordered_by_recent_for_only_that_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        var customerRepo = new CustomerRepository(db);
        customerRepo.Insert(new Customer("c-A", "instagram", "@ali",  null, null, 100, 100,
            false, null, null, 0, 0m, null, null, null));
        customerRepo.Insert(new Customer("c-B", "instagram", "@veli", null, null, 100, 100,
            false, null, null, 0, 0m, null, null, null));
        var repo = new LabelRepository(db);

        repo.Insert(new Label("l1", "s1", "c-A", "instagram", "@ali",  "kazak", "K01", 100m, AddedAt: 100, PrintedAt: 110));
        repo.Insert(new Label("l2", "s1", "c-A", "instagram", "@ali",  "ceket", "C02", 250m, AddedAt: 200, PrintedAt: null));
        repo.Insert(new Label("l3", "s1", "c-B", "instagram", "@veli", "etek",  null,  150m, AddedAt: 150, PrintedAt: 160));

        var rows = repo.GetByCustomer("c-A");
        rows.Should().HaveCount(2);
        rows[0].Id.Should().Be("l2");
        rows[0].IsPrinted.Should().BeFalse();
        rows[1].Id.Should().Be("l1");
        rows[1].IsPrinted.Should().BeTrue();

        repo.GetByCustomer("c-X").Should().BeEmpty();
    }
}
