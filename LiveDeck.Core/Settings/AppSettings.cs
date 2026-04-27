namespace LiveDeck.Core.Settings;

/// <summary>
/// Application settings persisted to settings.json under the user's Documents folder.
/// All defaults are chosen so the app works out-of-the-box for a new user.
/// </summary>
public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";
    public int ParserHighConfidence { get; set; } = 80;
    public int ParserLowConfidence { get; set; } = 50;

    // Printing
    public string? PrinterName { get; set; }            // null = use Windows default printer
    public int LabelWidthMm  { get; set; } = 60;
    public int LabelHeightMm { get; set; } = 30;
    public int LabelGapMm    { get; set; } = 5;
    public string LabelFontFamily { get; set; } = "Arial";
    public int   LabelUserFontSize  { get; set; } = 14;
    public int   LabelMessageFontSize { get; set; } = 12;
}
