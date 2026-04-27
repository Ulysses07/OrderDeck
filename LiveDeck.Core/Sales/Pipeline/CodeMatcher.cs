using System;
using System.Collections.Generic;
using System.Linq;

namespace LiveDeck.Core.Sales.Pipeline;

/// <summary>
/// Finds the best <see cref="ActiveCode"/> referenced inside a normalized message.
/// Strategy: for every active code (and its aliases), search the message for any
/// substring whose Levenshtein distance to the code/alias is ≤ 1. The shortest distance
/// wins; ties broken by code length (longer codes preferred — fewer false positives).
/// Input is assumed already normalised by <see cref="MessageNormalizer"/>.
/// </summary>
public sealed class CodeMatcher
{
    private const int MaxDistance = 1;

    public ActiveCode? Match(string normalisedMessage, IEnumerable<ActiveCode> activeCodes)
    {
        if (string.IsNullOrWhiteSpace(normalisedMessage)) return null;

        ActiveCode? best = null;
        int bestDistance = int.MaxValue;
        int bestCodeLength = -1;

        foreach (var code in activeCodes)
        {
            foreach (var candidate in EnumerateCandidates(code))
            {
                var distance = FindMinDistanceWindow(normalisedMessage, candidate);
                if (distance > MaxDistance) continue;

                if (distance < bestDistance ||
                    (distance == bestDistance && candidate.Length > bestCodeLength))
                {
                    best = code;
                    bestDistance = distance;
                    bestCodeLength = candidate.Length;
                }
            }
        }
        return best;
    }

    private static IEnumerable<string> EnumerateCandidates(ActiveCode code)
    {
        yield return code.Code;
        foreach (var alias in code.Aliases)
        {
            if (!string.IsNullOrWhiteSpace(alias))
                yield return alias.ToUpperInvariant();
        }
    }

    private static int FindMinDistanceWindow(string haystack, string needle)
    {
        if (haystack.Contains(needle, StringComparison.Ordinal)) return 0;

        var needleTokens = needle.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var hayTokens = haystack.Split(' ', StringSplitOptions.RemoveEmptyEntries);

        if (needleTokens.Length > hayTokens.Length) return int.MaxValue;

        int min = int.MaxValue;
        for (int i = 0; i <= hayTokens.Length - needleTokens.Length; i++)
        {
            var window = string.Join(' ', hayTokens, i, needleTokens.Length);
            var d = Levenshtein(window, needle);
            if (d < min) min = d;
            if (min == 0) return 0;
        }
        return min;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a == b) return 0;
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) prev[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }
}
