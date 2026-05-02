using System.Collections.Generic;

namespace OrderDeck.Core.Chat;

/// <summary>
/// Toggles + thresholds for <see cref="SpamFilter"/>. Persisted as part of
/// <c>AppSettings.SpamFilter</c>. Defaults are conservative: filter enabled
/// but only the cheapest two rules (short messages + duplicates) on, since
/// they have near-zero false-positive risk on auction streams. The operator
/// opts into the more aggressive rules from Settings → Spam Filtresi.
/// </summary>
public sealed class SpamFilterSettings
{
    public bool Enabled            { get; set; } = true;

    public bool DropShortMessages  { get; set; } = false;
    public int  MinMessageLength   { get; set; } = 2;

    public bool DropDuplicates     { get; set; } = true;
    public bool DropAllCaps        { get; set; } = false;
    public bool DropLinks          { get; set; } = true;
    public bool DropProfanity      { get; set; } = false;

    /// <summary>Operator-curated word list for the profanity rule. Words are
    /// lower-cased and matched whole-word. Empty list = rule fires nothing
    /// even when DropProfanity=true.</summary>
    public List<string> BlockedWords { get; set; } = new();
}
