using FluentAssertions;
using Moq;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class GiveawayServiceAnimationTests
{
    private static (GiveawayService svc, GiveawayRepository repo, string sessionId) Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        var sessions = new SessionRepository(db);
        var customers = new CustomerRepository(db);
        var labels = new LabelRepository(db);
        var giveaways = new GiveawayRepository(db);

        var customerSvc = new CustomerService(customers, sessions, labels, clock.Object);
        var drawer = new GiveawayDrawer();
        var svc = new GiveawayService(giveaways, customerSvc, drawer, clock.Object);

        var sessionId = "s-anim";
        sessions.Insert(new StreamSession(sessionId, "Live", 100, null, new[] { "instagram" }, null));
        return (svc, giveaways, sessionId);
    }

    [Fact]
    public void Start_with_explicit_animationId_persists_it()
    {
        var (svc, repo, sessionId) = Build();

        var g = svc.Start(sessionId, "kazan", 60, 1, null, true,
            animationId: "wheel");

        repo.GetById(g.Id)!.AnimationId.Should().Be("wheel");
    }

    [Fact]
    public void Start_with_null_animationId_falls_back_to_default()
    {
        var (svc, repo, sessionId) = Build();

        var g = svc.Start(sessionId, "kazan", 60, 1, null, true,
            animationId: null);

        g.AnimationId.Should().Be(AnimationCatalog.DefaultId);
        repo.GetById(g.Id)!.AnimationId.Should().Be(AnimationCatalog.DefaultId);
    }

    [Fact]
    public void Start_with_unknown_animationId_falls_back_to_default()
    {
        var (svc, repo, sessionId) = Build();

        var g = svc.Start(sessionId, "kazan", 60, 1, null, true,
            animationId: "does-not-exist");

        g.AnimationId.Should().Be(AnimationCatalog.DefaultId);
    }

    [Fact]
    public void Start_event_payload_contains_resolved_animationId()
    {
        var (svc, _, sessionId) = Build();
        GiveawayStartedEvent? captured = null;
        svc.Started += e => captured = e;

        svc.Start(sessionId, "kazan", 60, 1, null, true, animationId: "wheel");

        captured.Should().NotBeNull();
        captured!.AnimationId.Should().Be("wheel");
    }
}
