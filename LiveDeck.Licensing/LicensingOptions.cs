namespace LiveDeck.Licensing;

public sealed class LicensingOptions
{
    public string ServerBaseUrl { get; set; } = "https://license.livedeck.app";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int OfflineGraceDays { get; set; } = 14;
    public int HeartbeatIntervalHours { get; set; } = 24;
}
