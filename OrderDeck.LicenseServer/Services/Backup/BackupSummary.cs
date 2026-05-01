namespace OrderDeck.LicenseServer.Services.Backup;

public sealed record BackupSummary(
    int TotalSessions,
    int TotalLabels,
    int TotalUniqueCustomers,
    decimal TotalRevenue,
    decimal AvgRevenuePerSession,
    decimal AvgRevenuePerCustomer,
    TopSession? HighestSession,
    TopCustomer? TopCustomer);

public sealed record TopSession(string? Title, DateTimeOffset? StartedAt, decimal Total);
public sealed record TopCustomer(string Username, decimal Total, int LabelCount);
