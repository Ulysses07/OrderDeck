using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class CustomerRepositoryTests
{
    private static Customer NewCustomer(string id = "c1") =>
        new(id, "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 1000, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null);

    [Fact]
    public void Insert_then_FindByPlatformAndUsername_returns_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(NewCustomer());

        var found = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        found.Should().NotBeNull();
        found!.Id.Should().Be("c1");
        found.IsBlacklisted.Should().BeFalse();
        found.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void FindByPlatformAndUsername_returns_null_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.FindByPlatformAndUsername("instagram", "@nonexistent").Should().BeNull();
    }

    [Fact]
    public void IncrementLabelStats_adds_count_and_amount_and_lastSeen()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());

        repo.IncrementLabelStats("c1", labelDelta: 2, amountDelta: 250m, lastSeenAt: 5000);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        fresh!.TotalLabelsPrinted.Should().Be(2);
        fresh.TotalAmount.Should().Be(250m);
        fresh.LastSeenAt.Should().Be(5000);
    }

    [Fact]
    public void UpdateBlacklist_sets_flag_reason_and_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());

        repo.UpdateBlacklist("c1", isBlacklisted: true, reason: "Ödemedi", blacklistedAt: 9000);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        fresh.IsBlacklisted.Should().BeTrue();
        fresh.BlacklistReason.Should().Be("Ödemedi");
        fresh.BlacklistedAt.Should().Be(9000);
    }

    [Fact]
    public void UpdateBlacklist_can_clear_flag_and_reason()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());
        repo.UpdateBlacklist("c1", isBlacklisted: true, reason: "test", blacklistedAt: 9000);

        repo.UpdateBlacklist("c1", isBlacklisted: false, reason: null, blacklistedAt: null);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        fresh.IsBlacklisted.Should().BeFalse();
        fresh.BlacklistReason.Should().BeNull();
        fresh.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void GetBlacklisted_returns_only_blacklisted_newest_first()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(NewCustomer("c1"));
        repo.Insert(NewCustomer("c2") with { Username = "@b" });
        repo.Insert(NewCustomer("c3") with { Username = "@c" });

        repo.UpdateBlacklist("c1", true, "r1", 1000);
        repo.UpdateBlacklist("c3", true, "r3", 3000);

        var list = repo.GetBlacklisted();
        list.Should().HaveCount(2);
        list[0].Id.Should().Be("c3");
        list[1].Id.Should().Be("c1");
    }
}
