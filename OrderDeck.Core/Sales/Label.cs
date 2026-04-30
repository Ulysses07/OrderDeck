namespace OrderDeck.Core.Sales;

/// <summary>
/// A queued or printed label. Created when the user double-clicks a chat message in the
/// MainShell; persisted to SQLite. PrintedAt = null means it's still in the queue.
/// </summary>
public sealed record Label(
    string Id,
    string SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    long? PrintedAt);
