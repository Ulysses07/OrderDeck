namespace LiveDeck.Licensing.Storage;

public sealed record AuthRecord(
    Guid CustomerId,
    string Email,
    string Name,
    string Token,
    DateTimeOffset TokenExpiresAt);
