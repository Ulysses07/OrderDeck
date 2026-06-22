using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace OrderDeck.Core.Chat;

/// <summary>
/// Aggregates live concurrent-viewer counts reported by every chat source —
/// the browser-extension bridge (Instagram / TikTok / Facebook) and the
/// YouTube scraper. Single source of truth so the WPF shell reads one place
/// instead of stitching counts from the bridge and the bus separately.
///
/// Thread-safe: writers (bridge WS loop, YouTube poll loop) and the WPF
/// dispatcher reader race freely. Each report stamps UtcNow so the reader can
/// drop stale platforms (a tab closed, a scraper wedged) instead of summing
/// numbers that are no longer live.
/// </summary>
public sealed class ViewerCountTracker
{
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset At)> _counts =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Record the latest viewer count for a platform (e.g. "instagram").</summary>
    public void Report(string platform, int count)
    {
        if (string.IsNullOrWhiteSpace(platform) || count < 0) return;
        _counts[platform] = (count, DateTimeOffset.UtcNow);
    }

    /// <summary>Forget all counts — called when the broadcast ends so stale
    /// numbers don't linger on the next session.</summary>
    public void Clear() => _counts.Clear();

    /// <summary>Snapshot of platforms reported within <paramref name="maxAge"/>,
    /// with their summed total. Stale entries are excluded.</summary>
    public ViewerSnapshot GetSnapshot(TimeSpan maxAge)
    {
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        var fresh = _counts
            .Where(kv => kv.Value.At >= cutoff)
            .OrderBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .Select(kv => new PlatformViewers(kv.Key, kv.Value.Count))
            .ToList();
        return new ViewerSnapshot(fresh.Sum(p => p.Count), fresh);
    }
}

/// <summary>Aggregated viewer counts at a moment in time.</summary>
public sealed record ViewerSnapshot(int Total, IReadOnlyList<PlatformViewers> PerPlatform);

/// <summary>One platform's live viewer count.</summary>
public sealed record PlatformViewers(string Platform, int Count);
