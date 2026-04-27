using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class OrderRepositoryTests
{
    [Fact]
    public void Insert_then_GetBySession_returns_inserted_order()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100, 0, 0, 0, 100, false, null, null));
        var codeRepo = new ActiveCodeRepository(db);
        codeRepo.Insert(new ActiveCode("ac1", "s1", "MAVI",
            new[] { "M" }, 199m, null, System.Array.Empty<string>(), 100, null));

        var orderRepo = new OrderRepository(db);
        var order = new OrderItem("o1", "s1", "ac1", "c1", "MAVI", "M", 1,
            199m, 199m, 95, OrderStatus.New, "@a MAVI M aldım", 200, 200, null, null);
        orderRepo.Insert(order);

        var orders = orderRepo.GetBySession("s1");
        orders.Should().HaveCount(1);
        orders[0].Code.Should().Be("MAVI");
    }
}
