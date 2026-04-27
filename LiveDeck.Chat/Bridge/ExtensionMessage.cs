namespace LiveDeck.Chat.Bridge;

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
    long? Timestamp);
