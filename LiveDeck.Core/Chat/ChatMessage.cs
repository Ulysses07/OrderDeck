using System.Collections.Generic;

namespace LiveDeck.Core.Chat;

public sealed record ChatMessage(
    string Id,
    string Platform,
    string? ExternalId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Text,
    long ReceivedAt,
    IReadOnlyList<string> Badges);
