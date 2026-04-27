using System.Collections.Generic;
using System.Linq;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Scores buying intent (0-100) for a normalised Turkish message.
/// Buying words add points; question patterns ("VAR MI", "KALDI MI", "NE KADAR") subtract.
/// Buy-intent emoji in the original (un-normalised) text adds a small boost.
/// </summary>
public sealed class IntentScorer
{
    private static readonly HashSet<string> BuyingWords = new()
    {
        "ALDIM", "ALIYORUM", "ALABILIRMI", "ALABILIRMIYIM",
        "ISTIYORUM", "ISTERIM", "ALMAK", "OLSUN", "LUTFEN",
        "RICA", "RICAEDERIM", "EKLE", "EKLERMISIN", "AYIRIN", "AYIRINIZ"
    };

    private static readonly string[] BuyEmojis = { "🛒", "🛍", "❤", "❤️", "💖", "🌹", "🌸", "🛍️" };

    public int Score(string normalisedMessage, string originalText)
    {
        if (string.IsNullOrWhiteSpace(normalisedMessage)) return 0;

        int score = 50;
        var tokens = normalisedMessage.Split(' ', System.StringSplitOptions.RemoveEmptyEntries);
        var tokenSet = new HashSet<string>(tokens);

        // Buying-word boost
        foreach (var token in tokens)
        {
            if (BuyingWords.Contains(token)) score += 30;
        }

        // Question-pattern penalties (multi-token detection)
        // "VAR MI" pattern
        if (tokenSet.Contains("VAR") && tokenSet.Contains("MI"))
            score -= 25;

        // "KALDI MI" pattern
        if (tokenSet.Contains("KALDI") && tokenSet.Contains("MI"))
            score -= 25;

        // "NE KADAR" pattern
        if (tokenSet.Contains("NE") && tokenSet.Contains("KADAR"))
            score -= 25;

        // Literal question mark fallback
        if (originalText.Contains('?'))
            score -= 20;

        // Buy-intent emoji boost
        if (BuyEmojis.Any(e => originalText.Contains(e, System.StringComparison.Ordinal)))
            score += 15;

        return System.Math.Clamp(score, 0, 100);
    }
}
