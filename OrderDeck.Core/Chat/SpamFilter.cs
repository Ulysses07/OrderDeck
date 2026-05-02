using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace OrderDeck.Core.Chat;

/// <summary>
/// Lightweight rule-based spam/troll filter for incoming chat messages.
/// Stateless except for a short rolling window of recent messages used by
/// the dedup rule. Designed to drop the obvious noise — bot ads, all-caps
/// trolling, link spam, repeated copy-pasta — without heavy NLP.
///
/// Disabled by default; the auctioneer enables individual rules from the
/// Settings dialog. Each rule maps to a setter on <see cref="SpamFilterSettings"/>
/// and is independently togglable.
/// </summary>
public sealed class SpamFilter
{
    private static readonly Regex UrlRegex = new(
        @"(https?://|www\.)\S+|\b\S+\.(com|net|org|io|tr|biz|info|co)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly Func<SpamFilterSettings> _settingsProvider;

    // Rolling window of (lowercased text, unix-seconds-received) for the
    // duplicate-message rule. Bounded — we only need the last few seconds.
    private readonly LinkedList<(string Text, long ReceivedAt)> _recent = new();
    private const int RecentWindowSeconds = 30;
    private const int RecentMaxEntries    = 200;

    public SpamFilter(Func<SpamFilterSettings> settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    /// <summary>Inspects the message; returns null when the message should pass
    /// or a short reason code when it should be dropped. Reason codes are stable
    /// strings the bridge logs at Debug level so the operator can audit which
    /// rule fired for which message.</summary>
    public string? ShouldDrop(string text, long unixSecondsNow)
    {
        var s = _settingsProvider();
        if (!s.Enabled || string.IsNullOrWhiteSpace(text)) return null;

        var trimmed = text.Trim();

        if (s.DropShortMessages && trimmed.Length < s.MinMessageLength)
            return "short";

        if (s.DropAllCaps && IsAllCaps(trimmed))
            return "allcaps";

        if (s.DropLinks && UrlRegex.IsMatch(trimmed))
            return "link";

        if (s.DropProfanity && ContainsBlockedWord(trimmed, s.BlockedWords))
            return "profanity";

        // Repeat detection. Consult the rolling window AFTER the cheaper rules
        // so common cases short-circuit before we touch the window.
        if (s.DropDuplicates)
        {
            var key = trimmed.ToLowerInvariant();
            EvictExpired(unixSecondsNow);
            foreach (var entry in _recent)
            {
                if (entry.Text == key) return "duplicate";
            }
            _recent.AddLast((key, unixSecondsNow));
            if (_recent.Count > RecentMaxEntries) _recent.RemoveFirst();
        }

        return null;
    }

    private void EvictExpired(long now)
    {
        var cutoff = now - RecentWindowSeconds;
        while (_recent.First is { } first && first.Value.ReceivedAt < cutoff)
        {
            _recent.RemoveFirst();
        }
    }

    private static bool IsAllCaps(string text)
    {
        // Need at least 5 letters and >70 % of them uppercased to count as
        // "shouting" — a single all-caps word in normal sentences passes.
        var letters = 0;
        var upper = 0;
        foreach (var c in text)
        {
            if (!char.IsLetter(c)) continue;
            letters++;
            if (char.IsUpper(c)) upper++;
        }
        if (letters < 5) return false;
        return (double)upper / letters >= 0.7;
    }

    private static bool ContainsBlockedWord(string text, IReadOnlyList<string> words)
    {
        if (words.Count == 0) return false;
        var lower = text.ToLowerInvariant();
        foreach (var w in words)
        {
            if (string.IsNullOrWhiteSpace(w)) continue;
            // Whole-word match using simple boundary check; matches "kötü" but
            // not "köyüm" so common-substring false positives stay rare.
            if (Regex.IsMatch(lower, $@"\b{Regex.Escape(w.Trim().ToLowerInvariant())}\b"))
                return true;
        }
        return false;
    }
}
