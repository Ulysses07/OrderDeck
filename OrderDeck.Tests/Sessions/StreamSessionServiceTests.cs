using System;
using FluentAssertions;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sessions;

/// <summary>
/// SessionStarted/SessionEnded olay sözleşmesini kilitler. Facebook hosted
/// service'i SessionStarted'a abone olup uykusunu erken bölüyor — olay
/// Start() içinde, repo insert'ten SONRA ateşlenmeli ki abone GetActive()
/// çağırdığında oturumu görebilsin.
/// </summary>
public class StreamSessionServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    [Fact]
    public void Start_raises_SessionStarted_with_session_id_and_time()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = new StreamSessionService(new SessionRepository(db), new FakeClock());

        SessionStartedEventArgs? received = null;
        svc.SessionStarted += (_, e) => received = e;

        var session = svc.Start("Akşam Yayını", new[] { "facebook" });

        received.Should().NotBeNull();
        received!.SessionId.Should().Be(session.Id);
        received.StartedAt.Should().Be(1714521600L);
    }

    [Fact]
    public void Start_raises_SessionStarted_after_insert_so_GetActive_sees_it()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = new StreamSessionService(new SessionRepository(db), new FakeClock());

        StreamSession? activeDuringEvent = null;
        svc.SessionStarted += (_, _) => activeDuringEvent = svc.GetActive();

        var session = svc.Start(null, new[] { "facebook" });

        activeDuringEvent.Should().NotBeNull();
        activeDuringEvent!.Id.Should().Be(session.Id);
    }

    [Fact]
    public void End_raises_SessionEnded_with_session_id()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var svc = new StreamSessionService(new SessionRepository(db), new FakeClock());
        var session = svc.Start(null, new[] { "facebook" });

        SessionEndedEventArgs? received = null;
        svc.SessionEnded += (_, e) => received = e;

        svc.End(session.Id);

        received.Should().NotBeNull();
        received!.SessionId.Should().Be(session.Id);
        received.EndedAt.Should().Be(1714521600L);
    }
}
