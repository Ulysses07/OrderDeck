namespace LiveDeck.Licensing;

public sealed class LicensingOptions
{
    public string ServerBaseUrl { get; set; } = "https://license.livedeck.app";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int OfflineGraceDays { get; set; } = 14;
    public int HeartbeatIntervalHours { get; set; } = 24;
    // Phase 4c (full settings genişlemesi Task 10'da)
    public string TrialRegistrySubKey { get; set; } = @"Software\LiveDeck\Trial";
    public int TrialDurationDays { get; set; } = 14;
    public string TrialProgramDataPath { get; set; } = @"C:\ProgramData\LiveDeck\trial.dat";
}
