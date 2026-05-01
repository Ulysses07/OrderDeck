namespace OrderDeck.Licensing.Backup;

public sealed record BackupMetadata(
    Guid Id,
    long SizeBytes,
    DateTimeOffset CreatedAt,
    bool IsMonthlyMilestone,
    string? MachineName);
