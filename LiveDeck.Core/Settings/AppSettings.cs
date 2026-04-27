namespace LiveDeck.Core.Settings;

/// <summary>
/// Application settings persisted to settings.json under the user's Documents folder.
/// All defaults are chosen so the app works out-of-the-box for a new user.
/// </summary>
public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";
    public string CaptureOrderHotkey { get; set; } = "F9";
    public int ParserHighConfidence { get; set; } = 80;
    public int ParserLowConfidence { get; set; } = 50;
    public bool EtiketIntegrationEnabled { get; set; } = false;
    public string? EtiketWindowTitle { get; set; } = "etiket";
}
