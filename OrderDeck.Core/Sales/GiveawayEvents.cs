using System.Collections.Generic;

namespace OrderDeck.Core.Sales;

/// <summary>
/// Domain event fired when a giveaway starts. Note: audio settings are NOT
/// here — they're added by OverlayHost at broadcast time, since they're a
/// presentation concern, not a domain event.
/// </summary>
public sealed record GiveawayStartedEvent(
    string GiveawayId,
    string Keyword,
    int WinnerCount,
    int DurationSeconds,
    long StartedAt,
    string AnimationId);

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
