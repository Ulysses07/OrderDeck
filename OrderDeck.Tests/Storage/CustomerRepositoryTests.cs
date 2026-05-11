using System;
using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class CustomerRepositoryTests
{
    private static CustomerRepository CreateRepository()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CustomerRepository(db);
    }

    private static Customer NewCustomer(string id = "c1") =>
        new(id, "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 1000, LastSeenAt: 1000,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null);

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

    [Fact]
    public void UpdateNotes_sets_notes_or_normalizes_whitespace_to_null()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var c = new Customer("c-1", "instagram", "@ali", "Ali", null,
            FirstSeenAt: 100, LastSeenAt: 100,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null);
        repo.Insert(c);

        repo.UpdateNotes("c-1", "VIP müşteri");
        repo.GetById("c-1")!.Notes.Should().Be("VIP müşteri");

        repo.UpdateNotes("c-1", "   ");
        repo.GetById("c-1")!.Notes.Should().BeNull();

        repo.UpdateNotes("c-1", null);
        repo.GetById("c-1")!.Notes.Should().BeNull();
    }

    [Fact]
    public void Search_returns_matching_customers_ordered_by_last_seen()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(new Customer("c-1", "instagram", "@ali",     "Ali", null,
            100, 200, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c-2", "instagram", "@alican",  "Alican", null,
            100, 300, false, null, null, 0, 0m, null, null, null));
        repo.Insert(new Customer("c-3", "tiktok",    "@veli",    "Veli", null,
            100, 400, false, null, null, 0, 0m, null, null, null));

        var results = repo.Search("ali", limit: 50);
        results.Select(c => c.Id).Should().Equal(new[] { "c-2", "c-1" });

        repo.Search("ALI", limit: 50).Select(c => c.Id)
            .Should().Equal(new[] { "c-2", "c-1" });

        repo.Search("xyz", limit: 50).Should().BeEmpty();

        repo.Search("ali", limit: 1).Should().HaveCount(1);
    }

    [Fact]
    public void UpsertFromIntakeForm_creates_new_customer_with_form_platform()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        var customer = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Canlı", "Atatürk Cad. No:12", null, now);

        customer.Platform.Should().Be("form");
        customer.Username.Should().Be("bilalcanli");
        customer.DisplayName.Should().Be("Bilal Canlı");
        customer.Address.Should().Be("Atatürk Cad. No:12");
        customer.FirstSeenAt.Should().Be(now);
        customer.LastSeenAt.Should().Be(now);
    }

    [Fact]
    public void UpsertFromIntakeForm_updates_existing_customer_by_platform_username()
    {
        var repo = CreateRepository();
        var firstNow = 1714521600L;
        var secondNow = 1714608000L;

        var first = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Eski", "Eski Adres", null, firstNow);
        var second = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Yeni", "Yeni Adres", null, secondNow);

        second.Id.Should().Be(first.Id);    // same row
        second.DisplayName.Should().Be("Bilal Yeni");
        second.Address.Should().Be("Yeni Adres");
        second.FirstSeenAt.Should().Be(firstNow);
        second.LastSeenAt.Should().Be(secondNow);
    }

    [Fact]
    public void UpsertFromIntakeForm_treats_form_platform_as_distinct_from_instagram()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        // Same username, different platform — distinct customers
        repo.UpsertFromIntakeForm("bilalcanli", "Bilal F", "Form Adres", null, now);
        // Mevcut Insert API ile Instagram customer create
        repo.Insert(new Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "instagram",
            Username: "bilalcanli",
            DisplayName: "Bilal IG",
            AvatarUrl: null, FirstSeenAt: now, LastSeenAt: now,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null, Phone: null));

        var allByUsername = repo.Search("bilalcanli", limit: 10);
        allByUsername.Should().HaveCount(2);
        allByUsername.Should().Contain(c => c.Platform == "form");
        allByUsername.Should().Contain(c => c.Platform == "instagram");
    }

    [Fact]
    public void UpdatePhone_PersistsE164ValueAndCanBeReadBack()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var c = new Customer("id1", "twitch", "alice", "Alice", null,
            1000, 1000, false, null, null, 0, 0m, null, null, null);
        repo.Insert(c);

        repo.UpdatePhone("id1", "+905551234567");

        var loaded = repo.GetById("id1");
        loaded!.Phone.Should().Be("+905551234567");
    }

    [Fact]
    public void UpdatePhone_OnNonExistentId_DoesNotThrow()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        Action act = () => repo.UpdatePhone("nonexistent-id", "+905551234567");
        act.Should().NotThrow();
    }

    // ── Kargo PR F: RecipientPaysActive ─────────────────────────────────

    [Fact]
    public void Insert_default_RecipientPaysActive_is_false()
    {
        var repo = CreateRepository();
        repo.Insert(NewCustomer());

        var loaded = repo.GetById("c1");
        loaded!.RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void SetRecipientPaysActive_flips_flag_true_then_false()
    {
        var repo = CreateRepository();
        repo.Insert(NewCustomer());

        repo.SetRecipientPaysActive("c1", true);
        repo.GetById("c1")!.RecipientPaysActive.Should().BeTrue();

        repo.SetRecipientPaysActive("c1", false);
        repo.GetById("c1")!.RecipientPaysActive.Should().BeFalse();
    }

    [Fact]
    public void SetRecipientPaysActive_on_unknown_id_does_not_throw()
    {
        var repo = CreateRepository();
        Action act = () => repo.SetRecipientPaysActive("nonexistent", true);
        act.Should().NotThrow();
    }

    [Fact]
    public void Insert_with_RecipientPaysActive_true_persists_flag()
    {
        var repo = CreateRepository();
        var c = NewCustomer() with { RecipientPaysActive = true };
        repo.Insert(c);

        repo.GetById("c1")!.RecipientPaysActive.Should().BeTrue();
    }
}
