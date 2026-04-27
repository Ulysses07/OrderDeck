using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class CustomerServiceTests
{
    [Fact]
    public void GetOrCreate_creates_customer_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = new CustomerService(repo, clock);

        var customer = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        customer.Id.Should().NotBeNullOrEmpty();
        customer.Platform.Should().Be("instagram");
        customer.Username.Should().Be("@ayse_y");
        customer.FirstSeenAt.Should().Be(1234L);
        customer.TrustScore.Should().Be(100);
    }

    [Fact]
    public void GetOrCreate_returns_existing_customer_on_second_call()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = new CustomerService(repo, clock);

        var first = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);
        var second = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        second.Id.Should().Be(first.Id);
    }
}
