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

    // Browser-like headers added per-request rather than via DefaultRequestHeaders
    // so we can be safely handed an HttpClient owned by HttpClientFactory.
    // (DefaultRequestHeaders aren't thread-safe and are tied to the client
    // instance lifetime — bad fit for a pooled / shared client.)
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>Caller owns the HttpClient — pass one from
    /// IHttpClientFactory.CreateClient() so socket handles are reused
    /// across resolve attempts and the previous "new HttpClient + own
    /// SocketsHttpHandler per resolver instance" leak is gone.</summary>
    public YouTubeLiveResolver(ILogger<YouTubeLiveResolver> log, HttpClient http)
    {
        _log = log;
        _http = http;
        if (_http.Timeout == TimeSpan.FromSeconds(100)) // factory default — override
            _http.Timeout = TimeSpan.FromSeconds(15);
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
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            req.Headers.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9,en;q=0.8");
            using var resp = await _http.SendAsync(req, ct);
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

    // HttpClient lifecycle is owned by IHttpClientFactory now — Dispose
    // here would prematurely tear down the pooled handler chain. Kept as
    // a no-op so existing callers' `using` statements still compile.
    public void Dispose() { }
}
