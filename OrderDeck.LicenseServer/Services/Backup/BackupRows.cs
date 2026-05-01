namespace OrderDeck.LicenseServer.Services.Backup;

public sealed record CustomerRow(
    string Id, string Platform, string Username, string? DisplayName,
    string? Address, string? Phone, decimal TotalAmount, long LastSeenAt);

public sealed record SessionRow(
    string Id, string? Title, long StartedAt, long? EndedAt,
    int LabelCount, decimal TotalAmount);

public sealed record LabelRow(
    string Id, string SessionId, string Username, string? Code,
    decimal Price, long AddedAt, long? PrintedAt);

public sealed record GiveawayRow(
    string Id, string Keyword, long? StartedAt, long? EndedAt,
    int ParticipantCount, int WinnerCount);
