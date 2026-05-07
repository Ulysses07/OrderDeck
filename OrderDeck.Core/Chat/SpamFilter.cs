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

    // Rolling window of (username, lowercased text, unix-seconds-received)
    // for the duplicate-message rule. Keyed on (Username, Text) so a single
    // user pasting the same line twice within the window is dropped, but
    // 50 different viewers all typing the giveaway keyword (the obvious
    // legitimate case) are not. Bounded — we only need the last few seconds.
    private readonly LinkedList<(string Username, string Text, long ReceivedAt)> _recent = new();
    private const int RecentWindowSeconds = 2;
    private const int RecentMaxEntries    = 200;

    public SpamFilter(Func<SpamFilterSettings> settingsProvider)
    {
        _settingsProvider = settingsProvider;
    }

    /// <summary>Inspects the message; returns null when the message should pass
    /// or a short reason code when it should be dropped. Reason codes are stable
    /// strings the bridge logs at Debug level so the operator can audit which
    /// rule fired for which message. <paramref name="username"/> is the
    /// platform-side identity used to scope the duplicate-rule window so a
    /// giveaway keyword typed by 50 different viewers is not all dropped after
    /// the first.</summary>
    public string? ShouldDrop(string text, string username, long unixSecondsNow)
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

        // Repeat detection. Scoped to (Username, Text) over a 2-second
        // window — same user pastes same line twice → drop; different
        // users typing the giveaway keyword → all pass. Consult the
        // rolling window AFTER the cheaper rules so common cases
        // short-circuit before we touch the window.
        if (s.DropDuplicates)
        {
            var textKey = trimmed.ToLowerInvariant();
            var userKey = username ?? string.Empty;
            EvictExpired(unixSecondsNow);
            foreach (var entry in _recent)
            {
                if (entry.Username == userKey && entry.Text == textKey)
                    return "duplicate";
            }
            _recent.AddLast((userKey, textKey, unixSecondsNow));
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
