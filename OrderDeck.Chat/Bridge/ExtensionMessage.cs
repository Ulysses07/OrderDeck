namespace OrderDeck.Chat.Bridge;

/// <summary>
/// Wire format produced by the browser extension content scripts. Field names use camelCase
/// to match the JS sender. All fields are nullable except the discriminators.
/// </summary>
public sealed record ExtensionMessage(
    string Type,
    string? Platform,
    string? Username,
    string? DisplayName,
    string? AvatarUrl,
    string? Text,
    string? ExternalId,
    long? Timestamp,
    ExtensionStats? Stats,
    int? Count = null,
    // type: "watchdog" — extension'ın stall kurtarma olayları (nudge/reload).
    string? Action = null,
    long? SinceSendMs = null,
    int? Rows = null);

/// <summary>
/// Debug stats payload emitted by the extension every 10 seconds (type: "debug-stats").
/// All fields are counts/durations for the preceding measurement window.
/// </summary>
public sealed record ExtensionStats(
    int ScanCount,
    int CommentsObserved,
    int Deduped,
    int Sent,
    int ObserverBursts,
    int ScanIntervalMs,
    int DedupeWindowMs,
    long WindowStart,
    long WindowEnd,
    long WindowDurationMs,
    int DedupeCacheSize);
