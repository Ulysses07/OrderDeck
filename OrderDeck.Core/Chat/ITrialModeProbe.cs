namespace OrderDeck.Core.Chat;

/// <summary>
/// Minimal probe interface consumed by the chat bridge to decide whether to apply
/// trial-mode platform filtering (Instagram-only). Implemented by LicenseService.
/// Placed in OrderDeck.Core so cross-platform assemblies (net10.0) can reference it
/// without depending on OrderDeck.Licensing (net10.0-windows).
/// </summary>
public interface ITrialModeProbe
{
    /// <summary>True when the app should drop non-Instagram chat messages.</summary>
    bool IsTrialMode { get; }
}
