using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Moq;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Coverage for <see cref="GiveawayBannerViewModel"/> — the live banner that
/// runs while a giveaway is active. The <c>DispatcherTimer</c> won't fire
/// natural ticks in the xUnit thread (no dispatcher pump), so countdown / race
/// scenarios drive <c>UpdateState</c> via reflection. That's deliberate: the
/// state-machine logic is what we want to pin, not the timer plumbing itself.
///
/// Race-condition focus: the banner can fire <c>AutoDrawRequested</c> at most
/// once per active giveaway, doesn't fire after <c>StopTracking</c>, and the
/// manual-end mode (DurationSeconds=0) never auto-draws.
/// </summary>
public class GiveawayBannerViewModelTests
{
    private const long Now = 1_000_000L;

    private sealed record Harness(
        InMemorySqlite Db,
        GiveawayRepository Repo,
        SessionRepository Sessions,
        CustomerRepository Customers,
        Mock<IClock> Clock,
        GiveawayBannerViewModel Vm);

    private const string SessionId = "s-test";

    private static Harness Build(long now = Now)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new GiveawayRepository(db);
        var sessions = new SessionRepository(db);
        var customers = new CustomerRepository(db);
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(now);
        // Giveaway has a FK to StreamSession; seed the parent row once per harness.
        sessions.Insert(new StreamSession(SessionId, "Live", now, null, new[] { "instagram" }, null));
        var vm = new GiveawayBannerViewModel(repo, clock.Object);
        return new Harness(db, repo, sessions, customers, clock, vm);
    }

    private static Giveaway InsertGiveaway(Harness h, int durationSeconds, long startedAt,
        string id = "g1", string keyword = "kazan",
        long? endedAt = null, long? cancelledAt = null)
    {
        var g = new Giveaway(id, SessionId, keyword, durationSeconds, 1,
                             null, true, "seed", startedAt, endedAt, cancelledAt);
        h.Repo.Insert(g);
        return g;
    }

    private static void AddParticipant(Harness h, string giveawayId, string customerId,
        string username, long enteredAt = Now)
    {
        // GiveawayParticipant.CustomerId has a FK to Customer; ensure the row exists.
        if (h.Customers.GetById(customerId) is null)
        {
            h.Customers.Insert(new Customer(customerId, "instagram", username, null, null,
                                             enteredAt, enteredAt, false, null, null,
                                             0, 0m, null, null, null));
        }
        h.Repo.AddParticipant(new GiveawayParticipant(
            Guid.NewGuid().ToString("N"), giveawayId, customerId,
            "instagram", username, enteredAt, IsWinner: false));
    }

    /// <summary>Drives the private timer-tick state-machine without an actual
    /// Dispatcher pump. Lets us sequence "clock advances → state recomputes"
    /// the way real WPF would, but deterministically.</summary>
    private static void TickState(GiveawayBannerViewModel vm)
    {
        var m = typeof(GiveawayBannerViewModel)
            .GetMethod("UpdateState", BindingFlags.Instance | BindingFlags.NonPublic)!;
        m.Invoke(vm, null);
    }

    // ─── Initial state ───────────────────────────────────────────────────────

    [Fact]
    public void Initial_state_is_inactive_with_empty_observables()
    {
        var h = Build();

        h.Vm.IsActive.Should().BeFalse();
        h.Vm.Keyword.Should().Be("");
        h.Vm.CountdownText.Should().Be("");
        h.Vm.ParticipantCount.Should().Be(0);
        h.Vm.IsManualEnd.Should().BeFalse();
    }

    // ─── StartTracking — observables ─────────────────────────────────────────

    [Fact]
    public void StartTracking_populates_observables_for_timed_giveaway()
    {
        var h = Build(now: Now);
        // 90 seconds remaining: started 30s ago with a 120s duration.
        var g = InsertGiveaway(h, durationSeconds: 120, startedAt: Now - 30, keyword: "yarış");

        h.Vm.StartTracking(g);

        h.Vm.IsActive.Should().BeTrue();
        h.Vm.Keyword.Should().Be("yarış");
        h.Vm.IsManualEnd.Should().BeFalse();
        h.Vm.CountdownText.Should().Be("1:30");
    }

    [Fact]
    public void StartTracking_zero_padding_in_seconds_component()
    {
        var h = Build(now: Now);
        // 9 seconds remaining → expect "0:09" (zero-padded), not "0:9".
        var g = InsertGiveaway(h, durationSeconds: 9, startedAt: Now);

        h.Vm.StartTracking(g);

        h.Vm.CountdownText.Should().Be("0:09");
    }

    [Fact]
    public void StartTracking_manual_end_mode_shows_indefinite_label()
    {
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 0, startedAt: Now);

        h.Vm.StartTracking(g);

        h.Vm.IsManualEnd.Should().BeTrue();
        h.Vm.CountdownText.Should().Be("(süre limitsiz)");
    }

    [Fact]
    public void StartTracking_seeds_participant_count_from_repository()
    {
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 60, startedAt: Now);
        AddParticipant(h, g.Id, "c1", "alice");
        AddParticipant(h, g.Id, "c2", "bob");

        h.Vm.StartTracking(g);

        h.Vm.ParticipantCount.Should().Be(2);
    }

    [Fact]
    public void Tick_refreshes_participant_count_when_new_entries_arrive()
    {
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 60, startedAt: Now);
        AddParticipant(h, g.Id, "c1", "alice");
        h.Vm.StartTracking(g);
        h.Vm.ParticipantCount.Should().Be(1);

        AddParticipant(h, g.Id, "c2", "bob");
        TickState(h.Vm);

        h.Vm.ParticipantCount.Should().Be(2);
    }

    // ─── StopTracking ────────────────────────────────────────────────────────

    [Fact]
    public void StopTracking_clears_active_state()
    {
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 60, startedAt: Now);
        h.Vm.StartTracking(g);

        h.Vm.StopTracking();

        h.Vm.IsActive.Should().BeFalse();
    }

    [Fact]
    public void StopTracking_makes_subsequent_ticks_a_noop()
    {
        // Race protection: a Tick already queued in the dispatcher when we call
        // StopTracking would otherwise still hit UpdateState. _active=null
        // gates that path so the queued tick is harmless.
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 0, startedAt: Now);
        h.Vm.StartTracking(g);
        AddParticipant(h, g.Id, "c1", "alice");

        h.Vm.StopTracking();
        TickState(h.Vm);

        h.Vm.ParticipantCount.Should().Be(0,
            "StopTracking nulled _active so the queued tick must early-return without re-reading the repo");
    }

    // ─── AutoDrawRequested race scenarios ───────────────────────────────────

    [Fact]
    public void AutoDrawRequested_fires_when_countdown_reaches_zero()
    {
        var h = Build(now: Now);
        var g = InsertGiveaway(h, durationSeconds: 30, startedAt: Now - 30); // remaining = 0
        var fired = 0;
        h.Vm.AutoDrawRequested += () => fired++;

        h.Vm.StartTracking(g);

        fired.Should().Be(1);
        h.Vm.CountdownText.Should().Be("0:00");
    }

    [Fact]
    public void AutoDrawRequested_fires_when_clock_overruns_endsAt()
    {
        // Remaining is *negative* (clock ran past endsAt while we weren't
        // looking — e.g. system suspended). Same branch, same outcome.
        var h = Build(now: Now);
        var g = InsertGiveaway(h, durationSeconds: 10, startedAt: Now - 30);
        var fired = 0;
        h.Vm.AutoDrawRequested += () => fired++;

        h.Vm.StartTracking(g);

        fired.Should().Be(1);
        h.Vm.CountdownText.Should().Be("0:00");
    }

    [Fact]
    public void Manual_end_mode_never_fires_AutoDrawRequested()
    {
        // DurationSeconds=0 means the streamer ends manually; no clock-based
        // auto-draw can ever happen, regardless of what UpdateState computes.
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 0, startedAt: Now - 9_999);
        var fired = 0;
        h.Vm.AutoDrawRequested += () => fired++;

        h.Vm.StartTracking(g);
        TickState(h.Vm);
        TickState(h.Vm);

        fired.Should().Be(0);
        h.Vm.CountdownText.Should().Be("(süre limitsiz)");
    }

    [Fact]
    public void Stopping_before_countdown_zero_prevents_AutoDrawRequested()
    {
        var h = Build(now: Now);
        var g = InsertGiveaway(h, durationSeconds: 60, startedAt: Now); // remaining = 60
        var fired = 0;
        h.Vm.AutoDrawRequested += () => fired++;
        h.Vm.StartTracking(g);

        h.Vm.StopTracking();
        h.Clock.Setup(c => c.UnixNow()).Returns(Now + 120); // would have expired
        TickState(h.Vm);

        fired.Should().Be(0,
            "after StopTracking _active is null, so the timer-driven UpdateState path early-returns and never reaches the auto-draw branch");
    }

    [Fact]
    public void StartTracking_after_StopTracking_rearms_with_a_fresh_giveaway()
    {
        // Sequential giveaways within the same banner: stop the old, start the
        // new. AutoDrawRequested for the new one must work independently of
        // the old one's history.
        var h = Build(now: Now);
        var first  = InsertGiveaway(h, durationSeconds: 30, startedAt: Now - 30, id: "g1");
        var fired = 0;
        h.Vm.AutoDrawRequested += () => fired++;
        h.Vm.StartTracking(first); // fires → 1
        h.Vm.StopTracking();

        var second = InsertGiveaway(h, durationSeconds: 60, startedAt: Now, id: "g2", keyword: "ikinci");
        h.Vm.StartTracking(second);

        h.Vm.IsActive.Should().BeTrue();
        h.Vm.Keyword.Should().Be("ikinci");
        h.Vm.CountdownText.Should().Be("1:00");
        fired.Should().Be(1, "StartTracking with positive remaining time should not refire");
    }

    [Fact]
    public void Tick_after_AutoDraw_does_not_refire_when_consumer_calls_StopTracking()
    {
        // Models the real consumer pattern (MainShellViewModel.DrawGiveawayNow):
        // AutoDrawRequested handler synchronously calls StopTracking, so any
        // tick still in flight after the auto-draw branch must early-return.
        var h = Build(now: Now);
        var g = InsertGiveaway(h, durationSeconds: 30, startedAt: Now - 30); // remaining = 0
        var fired = 0;
        h.Vm.AutoDrawRequested += () =>
        {
            fired++;
            h.Vm.StopTracking();
        };

        h.Vm.StartTracking(g);
        TickState(h.Vm); // simulates a queued dispatcher tick that arrived after the auto-draw

        fired.Should().Be(1,
            "the consumer's StopTracking nulls _active, gating any subsequent UpdateState call");
    }

    // ─── Disposal ────────────────────────────────────────────────────────────

    [Fact]
    public void Dispose_is_safe_when_idle()
    {
        var h = Build();

        var act = () => h.Vm.Dispose();

        act.Should().NotThrow();
    }

    [Fact]
    public void Dispose_after_StartTracking_stops_the_timer()
    {
        var h = Build();
        var g = InsertGiveaway(h, durationSeconds: 60, startedAt: Now);
        h.Vm.StartTracking(g);

        var act = () => h.Vm.Dispose();

        act.Should().NotThrow();
        // Disposal does NOT clear IsActive (by design — the banner is just
        // letting go of its timer); test pins this so a future change has to
        // be deliberate.
        h.Vm.IsActive.Should().BeTrue();
    }
}
