using System.Collections.Generic;
using System.Linq;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Picks the active-code's size that appears in the normalised message.
/// Special case: if the active code only offers "TEK BEDEN" (one-size), that size is
/// returned even if the message doesn't mention it explicitly.
/// Returns null when no size matches OR when multiple sizes match (ambiguous).
/// </summary>
public sealed class VariantExtractor
{
    public string? Extract(string normalisedMessage, IReadOnlyList<string> sizes)
    {
        if (sizes.Count == 0) return null;

        if (sizes.Count == 1 && IsSingleSize(sizes[0]))
            return sizes[0];

        var tokens = normalisedMessage.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var matches = sizes
            .Where(s => ContainsSize(tokens, s))
            .ToList();

        if (matches.Count == 1) return matches[0];

        if (matches.Count == 0 && sizes.Any(IsSingleSize))
            return sizes.First(IsSingleSize);

        return null;
    }

    private static bool IsSingleSize(string size) =>
        size.Equals("TEK BEDEN", System.StringComparison.OrdinalIgnoreCase) ||
        size.Equals("TEK", System.StringComparison.OrdinalIgnoreCase);

    private static bool ContainsSize(string[] tokens, string size)
    {
        var sizeTokens = size.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        if (sizeTokens.Length == 1)
            return tokens.Contains(sizeTokens[0]);

        for (int i = 0; i <= tokens.Length - sizeTokens.Length; i++)
        {
            bool ok = true;
            for (int j = 0; j < sizeTokens.Length; j++)
            {
                if (tokens[i + j] != sizeTokens[j]) { ok = false; break; }
            }
            if (ok) return true;
        }
        return false;
    }
}
