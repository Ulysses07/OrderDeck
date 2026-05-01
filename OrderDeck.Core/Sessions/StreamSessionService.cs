using System;
using System.Collections.Generic;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;

namespace OrderDeck.Core.Sessions;

public sealed class StreamSessionService
{
    private readonly SessionRepository _repo;
    private readonly IClock _clock;

    public StreamSessionService(SessionRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public StreamSession Start(string? title, IReadOnlyList<string> platforms)
    {
        var session = new StreamSession(
            Id: Guid.NewGuid().ToString("N"),
            Title: title,
            StartedAt: _clock.UnixNow(),
            EndedAt: null,
            Platforms: platforms,
            Notes: null);
        _repo.Insert(session);
        return session;
    }

    public event EventHandler<SessionEndedEventArgs>? SessionEnded;

    public void End(string sessionId)
    {
        var endedAt = _clock.UnixNow();
        _repo.End(sessionId, endedAt);
        SessionEnded?.Invoke(this, new SessionEndedEventArgs(sessionId, endedAt));
    }

    public StreamSession? GetActive() => _repo.GetActive();
}
