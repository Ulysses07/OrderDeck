using System.Collections.Generic;

namespace OrderDeck.Core.Sessions;

public sealed record StreamSession(
    string Id,
    string? Title,
    long StartedAt,
    long? EndedAt,
    IReadOnlyList<string> Platforms,
    string? Notes);
