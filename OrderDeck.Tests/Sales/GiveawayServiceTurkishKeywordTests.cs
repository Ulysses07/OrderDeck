using System;
using System.Linq;
using FluentAssertions;
using OrderDeck.Core.Chat;
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

public class GiveawayServiceTurkishKeywordTests
{
    private static (GiveawayService Svc, GiveawayRepository Repo, string SessionId, InMemorySqlite Db) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var customerSvc  = new CustomerService(customerRepo, new SessionRepository(db), new LabelRepository(db), clock.Object);
        var giveawayRepo = new GiveawayRepository(db);
        var drawer       = new GiveawayDrawer();

        return (new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object),
                giveawayRepo, "s1", db);
    }

    private static ChatMessage Msg(string username, string text) =>
        new(Guid.NewGuid().ToString("N"), "instagram", null, username, username, null, text, 1000,
            Array.Empty<string>());

    [Theory]
    [InlineData("istanbul", "İSTANBUL gel",     true)]
    [InlineData("İstanbul", "istanbul gel",     true)]
    [InlineData("ışık",     "IŞIK kapı",        true)]
    [InlineData("kazan",    "kaybetti",         false)]
    [InlineData("🌹",       "alıyorum 🌹",       true)]
    public void AddParticipantFromChat_matches_keyword_with_turkish_culture(
        string keyword, string text, bool shouldAdd)
    {
        var (svc, repo, sid, db) = Fx();
        using var _ = db;
        var g = svc.Start(sid, keyword, durationSeconds: 60, winnerCount: 1,
                          platformFilter: null, preventRewinning: false);

        svc.AddParticipantFromChat(g.Id, Msg("@user", text));

        repo.GetParticipants(g.Id).Count.Should().Be(shouldAdd ? 1 : 0);
    }
}
