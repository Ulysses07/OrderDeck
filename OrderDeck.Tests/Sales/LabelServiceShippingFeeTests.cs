using System;
using Dapper;
using FluentAssertions;
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

public sealed class LabelServiceShippingFeeTests
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

        var svc = new LabelService(labelRepo, customerSvc, clock);
        return (svc, labelRepo, customerRepo, db, "s1");
    }

    private static Customer NewCustomer(string id = "c1") =>
        new(id, "instagram", "@ayse_y", "Ayşe Y", null,
            FirstSeenAt: 500, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null);

    [Fact]
    public void AddShippingFee_persists_label_flagged_as_shipping()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        var customer = NewCustomer();
        customers.Insert(customer);

        var label = svc.AddShippingFee(sid, customer, 150m);

        label.IsShippingFee.Should().BeTrue();
        label.Price.Should().Be(150m);
        label.SessionId.Should().Be(sid);
        label.CustomerId.Should().Be(customer.Id);
        label.PrintedAt.Should().BeNull("kargo label da kuyruğa girer, sonradan basılır");
        label.MessageText.Should().Contain("Kargo");
    }

    [Fact]
    public void AddShippingFee_roundtrip_through_repository_preserves_flag()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        var customer = NewCustomer();
        customers.Insert(customer);

        var created = svc.AddShippingFee(sid, customer, 150m);
        var fromDb = labels.GetById(created.Id);

        fromDb.Should().NotBeNull();
        fromDb!.IsShippingFee.Should().BeTrue();
    }

    [Fact]
    public void Regular_Add_keeps_IsShippingFee_false_by_default()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        // Use the regular Add flow with a chat message — no IsShippingFee param.
        var msg = new OrderDeck.Core.Chat.ChatMessage(
            Guid.NewGuid().ToString("N"), "instagram", null, "@ayse", "Ayşe", null,
            "MAVI XL", 1000, Array.Empty<string>());
        var label = svc.Add(sid, msg, 200m, code: null);

        label.IsShippingFee.Should().BeFalse();
        var fromDb = labels.GetById(label.Id)!;
        fromDb.IsShippingFee.Should().BeFalse();
    }

    [Fact]
    public void AddShippingFee_appears_in_unprinted_queue()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        var customer = NewCustomer();
        customers.Insert(customer);

        svc.AddShippingFee(sid, customer, 150m);

        var queue = labels.GetUnprintedBySession(sid);
        queue.Should().HaveCount(1);
        queue[0].IsShippingFee.Should().BeTrue();
        queue[0].Price.Should().Be(150m);
    }

    [Fact]
    public void Migration_015_adds_IsShippingFee_column()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        using var conn = db.Open();
        var hasColumn = conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM pragma_table_info('Label') WHERE name = 'IsShippingFee'");
        hasColumn.Should().Be(1);
    }
}
