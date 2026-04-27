using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Normalises a Turkish chat message for matching:
///   * collapses whitespace, strips emoji and most non-letter symbols,
///   * folds Turkish diacritics (İ→I, ı→I, ğ→G, ü→U, ş→S, ö→O, ç→C),
///   * uppercases the result.
///
/// Output is suitable for fuzzy comparison against active product codes.
/// </summary>
public sealed class MessageNormalizer
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex EmojiOrSymbol = new(
        @"[\p{So}\p{Sk}\p{Sm}\p{Cs}\p{Cn}\p{Mn}\p{Cf}]+|[\uD800-\uDFFF]+",
        RegexOptions.Compiled);

    public string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var stripped = EmojiOrSymbol.Replace(input, " ");

        var sb = new StringBuilder(stripped.Length);
        foreach (var ch in stripped)
        {
            sb.Append(FoldTurkish(ch));
        }

        var collapsed = Whitespace.Replace(sb.ToString(), " ").Trim();
        return collapsed.ToUpper(CultureInfo.InvariantCulture);
    }

    private static char FoldTurkish(char c) => c switch
    {
        'ı' or 'İ' => 'I',
        'ğ' or 'Ğ' => 'G',
        'ü' or 'Ü' => 'U',
        'ş' or 'Ş' => 'S',
        'ö' or 'Ö' => 'O',
        'ç' or 'Ç' => 'C',
        _ => c
    };
}
