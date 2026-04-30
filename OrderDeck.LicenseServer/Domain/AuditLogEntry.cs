namespace OrderDeck.LicenseServer.Domain;

public sealed class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid AdminId { get; set; }
    public string AdminUsername { get; set; } = "";
    public string EventType { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
