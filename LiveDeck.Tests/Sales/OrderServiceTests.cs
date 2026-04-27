using System.Collections.Generic;
using FluentAssertions;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class OrderServiceTests
{
    private static (OrderService Svc, OrderRepository Orders, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) BuildFixture()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));
        var codes = new ActiveCodeRepository(db);
        codes.Insert(new ActiveCode("ac1", "s1", "MAVI", new[] { "M", "XL" }, 199m, null,
            System.Array.Empty<string>(), 1000, null));

        var engine = new OrderCaptureEngine(
            new MessageNormalizer(), new CodeMatcher(), new VariantExtractor(),
            new QuantityExtractor(), new IntentScorer(), new ConfidenceScorer());

        var orderRepo = new OrderRepository(db);
        var customerRepo = new CustomerRepository(db);
        var customerSvc = new CustomerService(customerRepo, clock);

        var svc = new OrderService(orderRepo, codes, customerSvc, engine, clock);
        return (svc, orderRepo, customerRepo, db, "s1");
    }

    [Fact]
    public void High_confidence_message_creates_OrderItem_with_status_New()
    {
        var (svc, orders, customers, db, sessionId) = BuildFixture();
        using var _ = db;

        var msg = new ChatMessage("m1", "instagram", null, "@ayse_y", "Ayşe", null,
            "MAVİ XL aldım", 1100, System.Array.Empty<string>());

        var result = svc.Process(sessionId, msg);

        result.Should().NotBeNull();
        result!.Status.Should().Be(OrderStatus.New);
        result.Code.Should().Be("MAVI");
        result.Size.Should().Be("XL");

        orders.GetBySession(sessionId).Should().HaveCount(1);
        customers.FindByPlatformAndUsername("instagram", "@ayse_y").Should().NotBeNull();
    }

    [Fact]
    public void Mid_confidence_message_creates_OrderItem_with_status_Pending()
    {
        var (svc, orders, customers, db, sessionId) = BuildFixture();
        using var _ = db;

        var msg = new ChatMessage("m1", "instagram", null, "@a", null, null,
            "MAVİ aldım", 1100, System.Array.Empty<string>());

        var result = svc.Process(sessionId, msg);

        result.Should().NotBeNull();
        result!.Status.Should().Be(OrderStatus.Pending);
    }

    [Fact]
    public void Unmatched_or_low_confidence_returns_null_and_persists_nothing()
    {
        var (svc, orders, customers, db, sessionId) = BuildFixture();
        using var _ = db;

        var msg = new ChatMessage("m1", "instagram", null, "@a", null, null,
            "merhaba nasılsınız", 1100, System.Array.Empty<string>());

        var result = svc.Process(sessionId, msg);

        result.Should().BeNull();
        orders.GetBySession(sessionId).Should().BeEmpty();
    }
}
