namespace LiveDeck.Overlay.Models;

/// <summary>JSON envelope sent to overlay clients over WebSocket.</summary>
public sealed record OverlayEvent(string Type, object Data);

public sealed record ChatMessageEvent(
    string Id, string Platform, string Username,
    string? DisplayName, string? AvatarUrl, string Text, long Timestamp);

public sealed record ChatSnapshotEvent(System.Collections.Generic.IReadOnlyList<ChatMessageEvent> RecentMessages);
