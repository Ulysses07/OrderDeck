using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class GiveawayRepositoryTests
{
    private static (InMemorySqlite Db, GiveawayRepository Repo, string SessionId, string CustomerId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, null, null, null));

        return (db, new GiveawayRepository(db), "s1", "c1");
    }

    private static Giveaway NewGiveaway(string id = "g1", string sessionId = "s1") =>
        new(id, sessionId, "🌹", DurationSeconds: 60, WinnerCount: 1,
            PlatformFilter: null, PreventRewinning: true,
            RandomSeed: "seed", StartedAt: 200, EndedAt: null, CancelledAt: null);

    [Fact]
    public void Insert_then_GetById_returns_giveaway()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        repo.Insert(NewGiveaway());

        var g = repo.GetById("g1");
        g.Should().NotBeNull();
        g!.Keyword.Should().Be("🌹");
        g.WinnerCount.Should().Be(1);
        g.PreventRewinning.Should().BeTrue();
        g.PlatformFilter.Should().BeNull();
    }

    [Fact]
    public void Insert_with_platform_filter_round_trips_json_array()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        var withFilter = NewGiveaway() with { PlatformFilter = new[] { "tiktok" } };
        repo.Insert(withFilter);

        var fresh = repo.GetById("g1")!;
        fresh.PlatformFilter.Should().BeEquivalentTo(new[] { "tiktok" });
    }

    [Fact]
    public void GetActiveBySession_returns_only_running_giveaway()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        repo.Insert(NewGiveaway("g1", sid) with { EndedAt = 500 });        // ended
        repo.Insert(NewGiveaway("g2", sid));                                // active
        repo.Insert(NewGiveaway("g3", sid) with { CancelledAt = 600 });    // cancelled

        var active = repo.GetActiveBySession(sid);
        active.Should().NotBeNull();
        active!.Id.Should().Be("g2");
    }

    [Fact]
    public void GetActiveBySession_returns_null_when_none_active()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;
        repo.Insert(NewGiveaway() with { EndedAt = 500 });

        repo.GetActiveBySession(sid).Should().BeNull();
    }

    [Fact]
    public void AddParticipant_then_GetParticipants_returns_inserted()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(NewGiveaway());

        repo.AddParticipant(new GiveawayParticipant(
            Id: "p1", GiveawayId: "g1", CustomerId: cid,
            Platform: "instagram", Username: "@a",
            EnteredAt: 300, IsWinner: false));

        var ps = repo.GetParticipants("g1");
        ps.Should().HaveCount(1);
        ps[0].Username.Should().Be("@a");
    }

    [Fact]
    public void AddParticipant_duplicate_username_throws()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(NewGiveaway());
        repo.AddParticipant(new GiveawayParticipant(
            "p1", "g1", cid, "instagram", "@a", 300, false));

        var dup = new GiveawayParticipant("p2", "g1", cid, "instagram", "@a", 301, false);

        // UNIQUE INDEX on (GiveawayId, Platform, Username) raises Sqlite constraint error
        var act = () => repo.AddParticipant(dup);
        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public void MarkWinners_flips_IsWinner_for_given_ids()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(NewGiveaway());
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, false));
        repo.AddParticipant(new GiveawayParticipant("p2", "g1", cid, "instagram", "@b", 300, false));
        repo.AddParticipant(new GiveawayParticipant("p3", "g1", cid, "instagram", "@c", 300, false));

        repo.MarkWinners(new[] { "p1", "p3" });

        var ps = repo.GetParticipants("g1");
        ps.Single(p => p.Id == "p1").IsWinner.Should().BeTrue();
        ps.Single(p => p.Id == "p2").IsWinner.Should().BeFalse();
        ps.Single(p => p.Id == "p3").IsWinner.Should().BeTrue();
    }

    [Fact]
    public void Update_sets_endedAt_and_cancelledAt()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;
        repo.Insert(NewGiveaway());

        repo.MarkEnded("g1", endedAt: 999);
        repo.GetById("g1")!.EndedAt.Should().Be(999);

        repo.MarkCancelled("g1", cancelledAt: 1000);
        repo.GetById("g1")!.CancelledAt.Should().Be(1000);
    }

    [Fact]
    public void GetWinnerCustomerIdsForSession_returns_distinct_winners_excluding_current()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;

        new CustomerRepository(db).Insert(
            new Customer("c2", "instagram", "@b", null, null, 100, 100,
                false, null, null, 0, 0m, null, null, null));

        repo.Insert(NewGiveaway("g1") with { EndedAt = 500 });
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid,  "instagram", "@a", 300, IsWinner: true));
        repo.AddParticipant(new GiveawayParticipant("p2", "g1", "c2", "instagram", "@b", 300, IsWinner: false));

        repo.Insert(NewGiveaway("g2"));
        // expect: while drawing g2, c1 (the previous winner) is filtered out

        var ids = repo.GetWinnerCustomerIdsForSession(sessionId: sid, currentGiveawayId: "g2");
        ids.Should().BeEquivalentTo(new[] { cid });
    }

    [Fact]
    public void GetSessionTotals_counts_completed_giveaways_and_winners()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;

        repo.Insert(NewGiveaway("g1") with { EndedAt = 500 });
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, IsWinner: true));

        repo.Insert(NewGiveaway("g2") with { EndedAt = 600 });
        repo.AddParticipant(new GiveawayParticipant("p2", "g2", cid, "instagram", "@x", 400, IsWinner: true));
        repo.AddParticipant(new GiveawayParticipant("p3", "g2", cid, "instagram", "@y", 400, IsWinner: true));

        repo.Insert(NewGiveaway("g3") with { CancelledAt = 700 });   // cancelled, not counted
        repo.Insert(NewGiveaway("g4"));                                // active, not counted

        var totals = repo.GetSessionTotals(sid);
        totals.Count.Should().Be(2);
        totals.TotalWinners.Should().Be(3);
    }

    [Fact]
    public void GetParticipantCount_returns_count_for_giveaway()
    {
        var (db, repo, _, cid) = Fx();
        using var _2 = db;
        repo.Insert(NewGiveaway());
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, false));
        repo.AddParticipant(new GiveawayParticipant("p2", "g1", cid, "instagram", "@b", 301, false));

        repo.GetParticipantCount("g1").Should().Be(2);
        repo.GetParticipantCount("g-nonexistent").Should().Be(0);
    }

    [Fact]
    public void GetParticipationsByCustomer_returns_history_with_status_flags()
    {
        var (db, repo, _, cid) = Fx();
        using var _2 = db;

        // g1: ended, customer won
        repo.Insert(NewGiveaway("g1", "s1") with { EndedAt = 500 });
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, IsWinner: true));

        // g2: ended, customer participated but did not win
        repo.Insert(NewGiveaway("g2", "s1") with { EndedAt = 600 });
        repo.AddParticipant(new GiveawayParticipant("p2", "g2", cid, "instagram", "@a", 400, IsWinner: false));

        // g3: cancelled
        repo.Insert(NewGiveaway("g3", "s1") with { CancelledAt = 700 });
        repo.AddParticipant(new GiveawayParticipant("p3", "g3", cid, "instagram", "@a", 500, IsWinner: false));

        // g4: another customer's participation — must not appear
        new CustomerRepository(db).Insert(new Customer("c-2", "instagram", "@other", null, null,
            100, 100, false, null, null, 0, 0m, null, null, null));
        repo.Insert(NewGiveaway("g4", "s1") with { EndedAt = 800 });
        repo.AddParticipant(new GiveawayParticipant("p4", "g4", "c-2", "instagram", "@other", 600, IsWinner: false));

        var rows = repo.GetParticipationsByCustomer(cid);

        rows.Select(r => r.GiveawayId).Should().Equal(new[] { "g3", "g2", "g1" });
        rows[0].GiveawayCancelledAt.Should().Be(700);
        rows[1].IsWinner.Should().BeFalse();
        rows[2].IsWinner.Should().BeTrue();
        rows[2].GiveawayEndedAt.Should().Be(500);
    }
}
