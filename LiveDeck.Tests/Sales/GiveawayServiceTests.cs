using System;
using System.Linq;
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

public class GiveawayServiceTests
{
    private static (GiveawayService Svc, GiveawayRepository Repo, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) Fx(long unixNow = 1000L)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(unixNow);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram", "tiktok" }, null));

        var customerRepo  = new CustomerRepository(db);
        var customerSvc   = new CustomerService(customerRepo, clock.Object);
        var giveawayRepo  = new GiveawayRepository(db);
        var drawer        = new GiveawayDrawer();

        var svc = new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object);
        return (svc, giveawayRepo, customerRepo, db, "s1");
    }

    private static ChatMessage Msg(string username, string text, string platform = "instagram") =>
        new(System.Guid.NewGuid().ToString("N"),
            platform, null, username, username, null, text, 1000,
            System.Array.Empty<string>());

    [Fact]
    public void Start_creates_giveaway_with_seed_and_returns_it()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;

        var g = svc.Start(sid, keyword: "🌹", durationSeconds: 60, winnerCount: 1,
                          platformFilter: null, preventRewinning: true);

        g.Keyword.Should().Be("🌹");
        g.RandomSeed.Should().NotBeNullOrEmpty();

        var fresh = repo.GetActiveBySession(sid);
        fresh.Should().NotBeNull();
        fresh!.Id.Should().Be(g.Id);
    }

    [Fact]
    public void AddParticipantFromChat_adds_when_keyword_matches()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "hadi 🌹 katılıyorum"));

        repo.GetParticipants(g.Id).Should().HaveCount(1);
    }

    [Fact]
    public void AddParticipantFromChat_is_case_insensitive_for_alphanumeric_keywords()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        // "katıl" (dotless ı) ↔ "KATIL" (dotless I) — correct Turkish case pair.
        // The previous "katil" (dotted i) was masquerading as a match only because
        // OrdinalIgnoreCase ignored the dot distinction (P3d Task 1 fixed this).
        var g = svc.Start(sid, "katıl", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "KATIL ben"));

        repo.GetParticipants(g.Id).Should().HaveCount(1);
    }

    [Fact]
    public void AddParticipantFromChat_skips_message_without_keyword()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "merhaba"));

        repo.GetParticipants(g.Id).Should().BeEmpty();
    }

    [Fact]
    public void AddParticipantFromChat_dedupes_same_user()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "🌹"));
        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "🌹 yine"));
        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "tekrar 🌹"));

        repo.GetParticipants(g.Id).Should().HaveCount(1);
    }

    [Fact]
    public void AddParticipantFromChat_skips_blacklisted_user()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _2 = db;

        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        // Pre-create + blacklist
        var c = customers.FindByPlatformAndUsername("instagram", "@bad")
                ?? new Customer(System.Guid.NewGuid().ToString("N"),
                    "instagram", "@bad", null, null, 100, 100,
                    false, null, null, 0, 0m, null, null, null);
        if (customers.GetById(c.Id) is null) customers.Insert(c);
        customers.UpdateBlacklist(c.Id, true, "test", 999);

        svc.AddParticipantFromChat(g.Id, Msg("@bad", "🌹"));

        repo.GetParticipants(g.Id).Should().BeEmpty();
    }

    [Fact]
    public void AddParticipantFromChat_respects_platform_filter()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1,
                          platformFilter: new[] { "tiktok" }, preventRewinning: true);

        svc.AddParticipantFromChat(g.Id, Msg("@a", "🌹", platform: "instagram"));
        svc.AddParticipantFromChat(g.Id, Msg("@b", "🌹", platform: "tiktok"));

        var ps = repo.GetParticipants(g.Id);
        ps.Should().HaveCount(1);
        ps[0].Platform.Should().Be("tiktok");
    }

    [Fact]
    public void AddParticipantFromChat_skips_previous_winner_when_PreventRewinning()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _2 = db;

        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner", "🌹"));
        svc.Draw(g1.Id);   // @winner becomes winner

        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g2.Id, Msg("@winner", "🎁"));
        svc.AddParticipantFromChat(g2.Id, Msg("@new",    "🎁"));

        var ps = repo.GetParticipants(g2.Id);
        ps.Select(p => p.Username).Should().BeEquivalentTo(new[] { "@new" });
    }

    [Fact]
    public void Draw_picks_winners_marks_them_and_ends_giveaway()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 2, null, true);
        svc.AddParticipantFromChat(g.Id, Msg("@a", "🌹"));
        svc.AddParticipantFromChat(g.Id, Msg("@b", "🌹"));
        svc.AddParticipantFromChat(g.Id, Msg("@c", "🌹"));

        var winners = svc.Draw(g.Id);

        winners.Should().HaveCount(2);
        var fresh = repo.GetById(g.Id)!;
        fresh.EndedAt.Should().NotBeNull();
        repo.GetParticipants(g.Id).Where(p => p.IsWinner).Should().HaveCount(2);
    }

    [Fact]
    public void Draw_with_no_participants_returns_empty_and_ends_giveaway()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        var winners = svc.Draw(g.Id);

        winners.Should().BeEmpty();
        repo.GetById(g.Id)!.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_marks_giveaway_cancelled_and_GetActive_returns_null()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.Cancel(g.Id);

        repo.GetById(g.Id)!.CancelledAt.Should().NotBeNull();
        repo.GetActiveBySession(sid).Should().BeNull();
    }
}
