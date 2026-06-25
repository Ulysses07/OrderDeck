using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>
/// Resolves the currently-LIVE video id for a connected Page. The Live
/// Comments streaming endpoint is keyed by <c>{live_video_id}</c>, so the
/// hosted service has to look it up before opening the SSE stream.
///
/// <para>This is the only polling element of the Facebook ingestor —
/// 60 seconds is plenty because a live broadcast lasts hours and the
/// stream itself pushes comments in real time once we know the id.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookLiveVideoResolver
{
    private static readonly string GraphBase =
        $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}";

    private readonly HttpClient _http;
    private readonly ILogger<FacebookLiveVideoResolver> _log;

    public FacebookLiveVideoResolver(HttpClient http, ILogger<FacebookLiveVideoResolver> log)
    {
        _http = http;
        _log = log;
    }

    /// <summary>
    /// Returns the id of the Page's most-recent LIVE_NOW broadcast, or null
    /// if the Page isn't currently live. Errors are swallowed → null so the
    /// hosted service treats them the same as "offline" and just retries.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string pageId, string pageAccessToken, CancellationToken ct)
    {
        var url = $"{GraphBase}/{System.Uri.EscapeDataString(pageId)}/live_videos" +
                  $"?broadcast_status=%5B%22LIVE%22%2C%22LIVE_NOW%22%5D" +
                  $"&fields=id,status" +
                  $"&access_token={System.Uri.EscapeDataString(pageAccessToken)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogDebug(
                    "[FacebookLiveVideoResolver] {Status} {Reason}: {Body}",
                    (int)resp.StatusCode, resp.ReasonPhrase, body);
                return null;
            }

            var parsed = await resp.Content.ReadFromJsonAsync<LiveVideosResponse>(
                cancellationToken: ct).ConfigureAwait(false);

            // Pages can have at most one active broadcast at a time per Meta —
            // but the API returns a list, so we just pick the first LIVE one.
            if (parsed?.Data is not null)
            {
                foreach (var v in parsed.Data)
                {
                    if (!string.IsNullOrEmpty(v.Id) && IsLive(v.Status))
                        return v.Id;
                }
            }
            return null;
        }
        catch (System.Exception ex)
        {
            _log.LogDebug(ex, "[FacebookLiveVideoResolver] resolve failed (treating as offline)");
            return null;
        }
    }

    private static bool IsLive(string? status) =>
        // Graph returns "LIVE" for currently-broadcasting; "LIVE_STOPPED",
        // "VOD", "PROCESSING", etc. for the various post-stream states.
        // Older API versions emitted "LIVE_NOW" — keep tolerating it.
        status is "LIVE" or "LIVE_NOW";

    private sealed class LiveVideosResponse
    {
        [JsonPropertyName("data")] public List<LiveVideo>? Data { get; set; }
    }

    private sealed class LiveVideo
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("status")] public string? Status { get; set; }
    }
}
