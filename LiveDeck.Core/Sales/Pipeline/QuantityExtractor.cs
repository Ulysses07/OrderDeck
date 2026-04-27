using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Detects an explicit quantity in a normalised Turkish chat message. Supported forms:
///   * "2 TANE", "3 ADET"
///   * "X2", "+2"
///   * Number words "IKI", "UC", "DORT", ...
///   * Distributive "IKISER", "UCER"
/// Defaults to 1 when nothing matches. Caps at 50 to defang accidental large numbers.
/// </summary>
public sealed class QuantityExtractor
{
    private const int MaxQuantity = 50;

    private static readonly Regex DigitTane    = new(@"(?:\b|^)(\d{1,3})\s*(?:TANE|ADET)\b", RegexOptions.Compiled);
    private static readonly Regex XDigit       = new(@"(?<!\w)[X\+](\d{1,3})(?!\d)", RegexOptions.Compiled);
    private static readonly Regex DigitX       = new(@"\b(\d{1,3})X\b", RegexOptions.Compiled);

    private static readonly Dictionary<string, int> Words = new()
    {
        { "BIR", 1 }, { "IKI", 2 }, { "UC", 3 }, { "DORT", 4 }, { "BES", 5 },
        { "ALTI", 6 }, { "YEDI", 7 }, { "SEKIZ", 8 }, { "DOKUZ", 9 }, { "ON", 10 },
        { "BIRER", 1 }, { "IKISER", 2 }, { "UCER", 3 }, { "DORDER", 4 }, { "BESER", 5 }
    };

    public int Extract(string normalisedMessage)
    {
        if (string.IsNullOrWhiteSpace(normalisedMessage)) return 1;

        var m = DigitTane.Match(normalisedMessage);
        if (m.Success) return Cap(int.Parse(m.Groups[1].Value));

        m = XDigit.Match(normalisedMessage);
        if (m.Success) return Cap(int.Parse(m.Groups[1].Value));

        m = DigitX.Match(normalisedMessage);
        if (m.Success) return Cap(int.Parse(m.Groups[1].Value));

        var tokens = normalisedMessage.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        foreach (var t in tokens)
        {
            if (Words.TryGetValue(t, out var n))
                return Cap(n);
        }
        return 1;
    }

    private static int Cap(int n) => n < 1 ? 1 : (n > MaxQuantity ? MaxQuantity : n);
}
