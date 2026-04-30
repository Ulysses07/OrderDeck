namespace OrderDeck.LicenseServer.Domain;

public sealed class Activation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string HardwareFingerprint { get; set; } = "";
    public string? MachineName { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
}
