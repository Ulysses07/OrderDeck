namespace OrderDeck.Licensing;

public sealed class LicensingOptions
{
    public string ServerBaseUrl { get; set; } = "https://license.orderdeckapp.com";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int OfflineGraceDays { get; set; } = 14;
    /// <summary>How often HeartbeatHostedService calls /licenses/validate.
    /// Was 24h, but that meant "license expires today" only surfaced ~24h
    /// late and "license server unreachable" took a full day to show in
    /// the UI banner. 1h gives the operator enough warning before a live
    /// to renew, and surfaces network issues fast enough to act on.</summary>
    public int HeartbeatIntervalHours { get; set; } = 1;
    // Phase 4c (full settings genişlemesi Task 10'da)
    public string TrialRegistrySubKey { get; set; } = @"Software\OrderDeck\Trial";
    public int TrialDurationDays { get; set; } = 14;
    public string TrialProgramDataPath { get; set; } = @"C:\ProgramData\OrderDeck\trial.dat";
}
