using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// Maps a YouTube channel handle (e.g. "@orderdeck") to the currently-live
/// video ID, by hitting youtube.com/{handle}/live and harvesting the canonical
/// /watch?v= redirect target out of the returned HTML.
///
/// Channel goes offline → no video ID found → caller treats as "not live yet"
/// and re-polls later.
/// </summary>
public sealed class YouTubeLiveResolver : IDisposable
{
    private static readonly Regex CanonicalUrl = new(
        @"<link\s+rel=""canonical""\s+href=""https?://www\.youtube\.com/watch\?v=([a-zA-Z0-9_-]{11})""",
        RegexOptions.Compiled);

    private static readonly Regex VideoIdJson = new(
        @"""videoId""\s*:\s*""([a-zA-Z0-9_-]{11})""",
        RegexOptions.Compiled);

    private readonly HttpClient _http;
    private readonly ILogger<YouTubeLiveResolver> _log;

    public YouTubeLiveResolver(ILogger<YouTubeLiveResolver> log)
    {
        _log = log;
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        // Browser-like headers — YT sometimes serves a stripped JSON-only page to non-browser UAs.
        _http.DefaultRequestHeaders.Add("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.Add("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
        _http.DefaultRequestHeaders.Add("Accept-Language", "tr-TR,tr;q=0.9,en;q=0.8");
    }

    /// <summary>Returns the active live video ID for a channel handle, or null if
    /// the channel is not currently live (or the page didn't expose a video ID,
    /// which we treat the same way — caller retries later).</summary>
    public async Task<string?> ResolveAsync(string handle, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(handle)) return null;
        var trimmed = handle.Trim().TrimStart('@');
        if (string.IsNullOrEmpty(trimmed)) return null;

        var url = $"https://www.youtube.com/@{trimmed}/live";
        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("[YouTubeLiveResolver] {Handle} → HTTP {Status}", trimmed, (int)resp.StatusCode);
                return null;
            }

            var html = await resp.Content.ReadAsStringAsync(ct);

            // First preference: rel="canonical" — only set when the redirect resolved to a watch page.
            var m = CanonicalUrl.Match(html);
            if (m.Success) return m.Groups[1].Value;

            // Fallback: first videoId in the page JSON. May surface a non-live recent video,
            // so this branch is only useful when canonical is absent.
            m = VideoIdJson.Match(html);
            if (m.Success) return m.Groups[1].Value;

            _log.LogInformation("[YouTubeLiveResolver] {Handle} appears offline (no videoId in /live page)", trimmed);
            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "[YouTubeLiveResolver] resolve failed for {Handle}", trimmed);
            return null;
        }
    }

    public void Dispose() => _http.Dispose();
}
