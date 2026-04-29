namespace LiveDeck.Licensing;

/// <summary>
/// Client-side license state. Active and OfflineGrace allow writing; everything else is soft-gated.
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
    NoLicense
}

public static class LicenseStatusExtensions
{
    /// <summary>True only when the app is allowed to perform write actions (print, create, etc.).</summary>
    public static bool IsWritable(this LicenseStatus status) =>
        status is LicenseStatus.Active or LicenseStatus.OfflineGrace;
}
