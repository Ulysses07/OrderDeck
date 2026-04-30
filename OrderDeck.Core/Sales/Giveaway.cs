using System.Collections.Generic;

namespace OrderDeck.Core.Sales;

/// <summary>
/// A live giveaway run during a stream. While active, viewers who type the
/// <see cref="Keyword"/> in chat are added to <see cref="GiveawayParticipant"/>.
/// At draw time, <see cref="WinnerCount"/> winners are selected via
/// <see cref="GiveawayDrawer"/>. <see cref="EndedAt"/> set when drawn or 0-participant;
/// <see cref="CancelledAt"/> set when the streamer aborts.
/// </summary>
public sealed record Giveaway(
    string Id,
    string SessionId,
    string Keyword,
    int DurationSeconds,                          // 0 = manual end
    int WinnerCount,
    IReadOnlyList<string>? PlatformFilter,        // null = all platforms
    bool PreventRewinning,
    string RandomSeed,
    long StartedAt,
    long? EndedAt,
    long? CancelledAt);
