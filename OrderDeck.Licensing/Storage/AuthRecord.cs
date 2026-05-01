namespace OrderDeck.Licensing.Storage;

/// <summary>
/// RefreshToken / RefreshExpiresAt are nullable for backward compat with auth.dat
/// files written before the refresh-token feature shipped (Phase 5b). When null
/// the client falls back to the legacy "let token expire and re-login" behavior.
/// </summary>
public sealed record AuthRecord(
    Guid CustomerId,
    string Email,
    string Name,
    string Token,
    DateTimeOffset TokenExpiresAt,
    string? RefreshToken = null,
    DateTimeOffset? RefreshExpiresAt = null);
