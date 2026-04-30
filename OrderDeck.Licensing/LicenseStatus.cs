namespace LiveDeck.Licensing;

/// <summary>
/// Client-side license state. Active, OfflineGrace, and TrialActive allow writing;
/// everything else is soft-gated. TrialActive/TrialExpired also drop non-Instagram chat.
/// </summary>
public enum LicenseStatus
{
    /// <summary>App is starting up; status not yet determined.</summary>
    Initializing,
    /// <summary>License is valid and verified online.</summary>
    Active,
    /// <summary>Server unreachable, but within offline grace window.</summary>
    OfflineGrace,
    /// <summary>Server unreachable and grace window exceeded — soft gate.</summary>
    OfflineExpired,
    /// <summary>Server reports license expired.</summary>
    ExpiredOnline,
    /// <summary>Server reports license revoked.</summary>
    Revoked,
    /// <summary>No license / no auth token / not activated on this device.</summary>
    NoLicense,
    /// <summary>14-day trial running; Instagram-only chat platforms.</summary>
    TrialActive,
    /// <summary>Trial used; soft-gate identical to Phase 4b expired state.</summary>
    TrialExpired
}

public static class LicenseStatusExtensions
{
    /// <summary>True only when the app is allowed to perform write actions (print, create, etc.).</summary>
    public static bool IsWritable(this LicenseStatus status) =>
        status is LicenseStatus.Active
             or LicenseStatus.OfflineGrace
             or LicenseStatus.TrialActive;

    /// <summary>True when app should drop non-Instagram chat platforms (Phase 4c trial restriction).</summary>
    public static bool IsTrialMode(this LicenseStatus status) =>
        status is LicenseStatus.TrialActive or LicenseStatus.TrialExpired;
}
