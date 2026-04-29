namespace LiveDeck.Licensing.Trial;

/// <summary>
/// Persistent trial state record. Same shape across all 3 storage locations
/// (HKCU registry, ProgramData JSON, LocalAppData DPAPI).
/// Version field reserved for future schema migration.
/// </summary>
public sealed record TrialRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    string HardwareFingerprint,
    int Version);
