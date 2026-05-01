namespace OrderDeck.Licensing;

public interface IHardwareIdProvider
{
    /// <summary>SHA-256 hex (lowercase, 64 chars) deterministically derived from
    /// machine + immutable user identity (SID on Windows). Stable across Windows
    /// account renames.</summary>
    string GetHardwareId();

    /// <summary>Pre-Phase-5d hash that mixed in the mutable Environment.UserName
    /// instead of the SID. Sent alongside the new hash during the transition
    /// window so the server can migrate existing activation rows from the
    /// legacy fingerprint to the new one. Returns null on platforms / fallback
    /// paths where the legacy formula can't be reproduced.</summary>
    string? GetLegacyHardwareId();
}
