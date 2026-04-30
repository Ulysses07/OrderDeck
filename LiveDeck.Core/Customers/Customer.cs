namespace LiveDeck.Core.Customers;

public sealed record Customer(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    long FirstSeenAt,
    long LastSeenAt,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes,
    int TotalLabelsPrinted,
    decimal TotalAmount,
    long? BlacklistedAt,
    string? Address);   // Phase 4f
