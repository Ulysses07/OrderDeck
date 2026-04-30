using System;
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class CustomerServiceTests
{
    private static CustomerService MakeSvc(InMemorySqlite db, IClock clock,
        out CustomerRepository customers, out SessionRepository sessions, out LabelRepository labels)
    {
        customers = new CustomerRepository(db);
        sessions = new SessionRepository(db);
        labels = new LabelRepository(db);
        return new CustomerService(customers, sessions, labels, clock);
    }

    [Fact]
    public void GetOrCreate_creates_customer_with_zero_aggregates()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        var customer = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        customer.TotalLabelsPrinted.Should().Be(0);
        customer.TotalAmount.Should().Be(0m);
        customer.IsBlacklisted.Should().BeFalse();
        customer.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void GetOrCreate_returns_existing_on_second_call()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        var first  = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);
        var second = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public void RecordPrintedLabels_bumps_aggregates()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 5000L);
        var svc = MakeSvc(db, clock, out var repo, out _, out _);
        var c = svc.GetOrCreate("instagram", "@a", null, null);

        svc.RecordPrintedLabels(c.Id, labelCount: 3, amount: 450m);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@a")!;
        fresh.TotalLabelsPrinted.Should().Be(3);
        fresh.TotalAmount.Should().Be(450m);
    }

    [Fact]
    public void AddToBlacklist_flips_flag_with_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 7000L);
        var svc = MakeSvc(db, clock, out var repo, out _, out _);
        var c = svc.GetOrCreate("instagram", "@bad", null, null);

        svc.AddToBlacklist(c.Id, "Ödemedi 3 kez");

        var fresh = repo.GetById(c.Id)!;
        fresh.IsBlacklisted.Should().BeTrue();
        fresh.BlacklistReason.Should().Be("Ödemedi 3 kez");
        fresh.BlacklistedAt.Should().Be(7000L);
    }

    [Fact]
    public void RemoveFromBlacklist_clears_flag_reason_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 7000L);
        var svc = MakeSvc(db, clock, out var repo, out _, out _);
        var c = svc.GetOrCreate("instagram", "@bad", null, null);
        svc.AddToBlacklist(c.Id, "test");

        svc.RemoveFromBlacklist(c.Id);

        var fresh = repo.GetById(c.Id)!;
        fresh.IsBlacklisted.Should().BeFalse();
        fresh.BlacklistReason.Should().BeNull();
        fresh.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void EnsureBlacklistedManual_creates_then_blacklists_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 9000L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        var c = svc.EnsureBlacklistedManual("tiktok", "@spammer", "Spam");

        c.Platform.Should().Be("tiktok");
        c.Username.Should().Be("@spammer");
        c.IsBlacklisted.Should().BeTrue();
        c.BlacklistReason.Should().Be("Spam");
        c.BlacklistedAt.Should().Be(9000L);
    }

    [Fact]
    public void EnsureBlacklistedManual_blacklists_existing_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 9000L);
        var svc = MakeSvc(db, clock, out _, out _, out _);
        var existing = svc.GetOrCreate("instagram", "@a", null, null);

        var blacklisted = svc.EnsureBlacklistedManual("instagram", "@a", "Reason");

        blacklisted.Id.Should().Be(existing.Id);
        blacklisted.IsBlacklisted.Should().BeTrue();
    }

    // --- Phase 4g Task 6: GetLastStreamShoppers ---

    [Fact]
    public void GetLastStreamShoppers_NoEndedSession_ReturnsEmpty()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var svc = MakeSvc(db, clock, out _, out _, out _);

        svc.GetLastStreamShoppers().Should().BeEmpty();
    }

    [Fact]
    public void GetLastStreamShoppers_HydratesCustomersFromLatestEndedSession()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var svc = MakeSvc(db, clock, out var customers, out var sessions, out var labels);

        customers.Insert(new Customer("c1", "twitch", "alice", "Alice", null,
            100, 100, false, null, null, 0, 0m, null, null, "+905551111111"));

        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "alice",
            "Apple aldım", "APPLE", 50m, AddedAt: 110, PrintedAt: 120));
        sessions.End("s1", 200);

        var result = svc.GetLastStreamShoppers();

        result.Should().HaveCount(1);
        result[0].Id.Should().Be("c1");
        result[0].Phone.Should().Be("+905551111111");
    }

    [Fact]
    public void GetLastStreamShoppers_UnprintedLabelsExcluded()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1L);
        var svc = MakeSvc(db, clock, out var customers, out var sessions, out var labels);

        customers.Insert(new Customer("c1", "twitch", "bob", "Bob", null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        sessions.Insert(new StreamSession("s1", "Live", 100, null, Array.Empty<string>(), null));
        labels.Insert(new Label("l1", "s1", "c1", "twitch", "bob",
            "Apple", "APPLE", 50m, AddedAt: 110, PrintedAt: null));
        sessions.End("s1", 200);

        svc.GetLastStreamShoppers().Should().BeEmpty();
    }
}
