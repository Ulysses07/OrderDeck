using System;
using System.Collections.Generic;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class ActiveCodeService
{
    private readonly ActiveCodeRepository _repo;
    private readonly IClock _clock;

    public ActiveCodeService(ActiveCodeRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

    public ActiveCode Add(string sessionId, string code, IReadOnlyList<string> sizes,
        decimal price, string? imageUrl = null, IReadOnlyList<string>? aliases = null)
    {
        var ac = new ActiveCode(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            Code: code.Trim().ToUpperInvariant(),
            Sizes: sizes,
            Price: price,
            ImageUrl: imageUrl,
            Aliases: aliases ?? Array.Empty<string>(),
            StartedAt: _clock.UnixNow(),
            EndedAt: null);
        _repo.Insert(ac);
        return ac;
    }

    public void UpdatePrice(string id, decimal newPrice)
    {
        var existing = _repo.GetById(id) ?? throw new InvalidOperationException($"Code {id} not found");
        _repo.Update(existing with { Price = newPrice });
    }

    public void UpdateSizes(string id, IReadOnlyList<string> sizes)
    {
        var existing = _repo.GetById(id) ?? throw new InvalidOperationException($"Code {id} not found");
        _repo.Update(existing with { Sizes = sizes });
    }

    public void Close(string id) => _repo.End(id, _clock.UnixNow());

    public IReadOnlyList<ActiveCode> GetActive(string sessionId) => _repo.GetActiveBySession(sessionId);
}
