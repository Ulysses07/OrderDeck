using System.Collections.Generic;

namespace LiveDeck.Core.Sales;

/// <summary>
/// JSON-friendly DTOs raised by <see cref="GiveawayService"/>. Consumed by
/// <c>OverlayHost</c> and broadcast to OBS overlay clients via WebSocket.
/// Property names are the wire format — JS reads them verbatim.
/// </summary>
public sealed record GiveawayStartedEvent(
    string GiveawayId,
    string Keyword,
    int WinnerCount,
    int DurationSeconds,
    long StartedAt);

public sealed record GiveawayParticipantEvent(
    string GiveawayId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Platform,
    int TotalCount);

public sealed record GiveawayWinnerDto(
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Platform);

public sealed record GiveawayWinnersDrawnEvent(
    string GiveawayId,
    IReadOnlyList<GiveawayWinnerDto> Winners,
    IReadOnlyList<GiveawayWinnerDto> AnimationPool,
    int ParticipantCount);

public sealed record GiveawayCancelledEvent(string GiveawayId);
