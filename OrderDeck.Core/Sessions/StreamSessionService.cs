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

    public void End(string sessionId) => _repo.End(sessionId, _clock.UnixNow());

    public StreamSession? GetActive() => _repo.GetActive();
}
