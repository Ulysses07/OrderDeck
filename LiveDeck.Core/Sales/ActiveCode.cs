using System.Collections.Generic;

namespace LiveDeck.Core.Sales;

public sealed record ActiveCode(
    string Id,
    string SessionId,
    string Code,
    IReadOnlyList<string> Sizes,
    decimal Price,
    string? ImageUrl,
    IReadOnlyList<string> Aliases,
    long StartedAt,
    long? EndedAt)
{
    public bool IsActive => EndedAt is null;
}
