using System;
using System.Linq;
using Dapper;
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

public class GiveawayServicePreventRewinningCacheTests
{
    private static (GiveawayService Svc, GiveawayRepository Repo, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var customerSvc  = new CustomerService(customerRepo, clock.Object);
        var giveawayRepo = new GiveawayRepository(db);
        var drawer       = new GiveawayDrawer();

        return (new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object),
                giveawayRepo, customerRepo, db, "s1");
    }

    private static ChatMessage Msg(string username, string text) =>
        new(Guid.NewGuid().ToString("N"), "instagram", null, username, username, null, text, 1000,
            Array.Empty<string>());

    [Fact]
    public void Start_caches_previous_winners_so_AddParticipantFromChat_does_not_requery()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _ = db;

        // First giveaway: @winner wins
        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner", "🌹"));
        svc.Draw(g1.Id);

        // Second giveaway starts → cache is built from DB at Start time
        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);

        // Tamper with DB: forcibly clear the IsWinner flag from g1's participant.
        // If the service cached the winner set at Start, @winner is still filtered.
        // If it re-queries on every AddParticipantFromChat, @winner would now sneak in.
        using (var conn = db.Open())
        {
            conn.Execute("UPDATE GiveawayParticipant SET IsWinner = 0 WHERE Username = '@winner'");
        }

        svc.AddParticipantFromChat(g2.Id, Msg("@winner", "🎁"));
        svc.AddParticipantFromChat(g2.Id, Msg("@new",    "🎁"));

        // @winner is still filtered (cache used), only @new is added
        var ps = repo.GetParticipants(g2.Id);
        ps.Should().HaveCount(1);
        ps[0].Username.Should().Be("@new");
    }

    [Fact]
    public void Draw_clears_cache_so_next_Start_rebuilds()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;

        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner1", "🌹"));
        svc.Draw(g1.Id);

        // Cache from g1 is cleared on Draw. New Start should re-query.
        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g2.Id, Msg("@winner1", "🎁"));   // previous winner → filtered
        svc.AddParticipantFromChat(g2.Id, Msg("@fresh",   "🎁"));

        var ps = repo.GetParticipants(g2.Id);
        ps.Select(p => p.Username).Should().BeEquivalentTo(new[] { "@fresh" });
    }

    [Fact]
    public void Cancel_clears_cache()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;

        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner1", "🌹"));
        svc.Draw(g1.Id);

        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);
        svc.Cancel(g2.Id);

        // After Cancel, Start a fresh giveaway — cache must be rebuilt
        var g3 = svc.Start(sid, "✨", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g3.Id, Msg("@winner1", "✨"));   // still filtered

        repo.GetParticipants(g3.Id).Should().BeEmpty();
    }
}
