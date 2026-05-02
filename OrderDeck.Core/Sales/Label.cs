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
    long? PrintedAt,
    long? CancelledAt = null,
    string? CancelReason = null,
    /// <summary>True when this label originated as a backup (created via
    /// <c>LabelService.AddBackup</c>). Drives the small "Y" stamp on the
    /// printed sticker so the picker can spot a re-routed sale without
    /// confusing it with the original line. Stays true after the backup is
    /// confirmed (<see cref="IsTentativeBackup"/> flips to false) — the
    /// physical paper already has the Y mark.</summary>
    bool IsBackupPromoted = false,
    /// <summary>The Label.Id this row is a standby for. Null for normal sales.
    /// Set when a backup is added; preserved after promotion for audit so
    /// reports can show "this sale replaced label X".</summary>
    string? ParentLabelId = null,
    /// <summary>1 while the backup hasn't been confirmed: the sticker may have
    /// been printed and set aside, but the sale isn't real yet, so it must be
    /// excluded from session revenue and customer aggregates. Flipped to 0 by
    /// <c>LabelService.ConfirmBackup</c> when the operator promotes the
    /// backup after the original buyer cancels.</summary>
    bool IsTentativeBackup = false);
