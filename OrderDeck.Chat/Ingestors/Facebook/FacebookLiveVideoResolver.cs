using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Json;
using System.Runtime.Versioning;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>Canlı yayın aramasının sonucunun türü.</summary>
public enum FacebookLiveLookupStatus
{
    /// <summary>Sayfa canlı; video kimliği sonuçta.</summary>
    Live,

    /// <summary>Sayfa şu an canlı değil.</summary>
    NotLive,

    /// <summary>Sayfa token'ı geçersiz ya da izin eksik: beklemek düzeltmez.</summary>
    AuthProblem,

    /// <summary>Geçici hata (ağ, sunucu, hız sınırı).</summary>
    Failed,
}

/// <summary>Canlı yayın aramasının sonucu.</summary>
/// <param name="Status">Sonucun türü.</param>
/// <param name="VideoId">Canlı yayının video kimliği; yalnız <see cref="FacebookLiveLookupStatus.Live"/>'da.</param>
public readonly record struct FacebookLiveLookup(FacebookLiveLookupStatus Status, string? VideoId = null);

/// <summary>
/// Resolves the currently-LIVE video id for a connected Page. The Live
/// Comments streaming endpoint is keyed by the underlying <c>{video_id}</c>
/// (NOT the LiveVideo id — the two differ), so the hosted service has to
/// look it up before opening the SSE stream.
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
    /// caller treats them the same as "offline" and just retries. Bekçi
    /// (watchdog) bunu kullanır; ana döngü hatanın türünü ayırt eden
    /// <see cref="ResolveDetailedAsync"/>'i kullanır.
    /// </summary>
    public async Task<string?> ResolveAsync(
        string pageId, string pageAccessToken, CancellationToken ct)
    {
        try
        {
            var lookup = await ResolveDetailedAsync(pageId, pageAccessToken, ct).ConfigureAwait(false);
            return lookup.Status == FacebookLiveLookupStatus.Live ? lookup.VideoId : null;
        }
        catch (System.OperationCanceledException)
        {
            return null; // bu çağrının sözleşmesi: hiçbir şey fırlatmaz
        }
    }

    /// <summary>
    /// Sayfanın canlı yayınını arar ve sonucu türüyle bildirir: canlı (video kimliğiyle), canlı
    /// değil, oturum ya da izin sorunu, geçici hata.
    ///
    /// <para>Oturum ve izin sorunu (Graph'ın 190/463 ve 200/10 kodları) "canlı değil"den ayrılır:
    /// beklemek onu düzeltmez, operatör izinleri güncellemelidir. Hız sınırı ve sunucu hatası
    /// geçici hatadır.</para>
    /// </summary>
    /// <exception cref="System.OperationCanceledException"><paramref name="ct"/> iptal edildi
    /// (yenileme süren aramayı keser).</exception>
    public async Task<FacebookLiveLookup> ResolveDetailedAsync(
        string pageId, string pageAccessToken, CancellationToken ct)
    {
        // Graph API only accepts the canonical "LIVE" status here — older docs
        // and some community posts mention "LIVE_NOW" but that's rejected with
        // (#100) Param broadcast_status[1] must be one of {...}.
        var url = $"{GraphBase}/{System.Uri.EscapeDataString(pageId)}/live_videos" +
                  $"?broadcast_status=%5B%22LIVE%22%5D" +
                  $"&fields=id,status,video{{id}}" +
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

                var kind = InstagramGraphError.Classify((int)resp.StatusCode, body);
                return new FacebookLiveLookup(
                    kind is InstagramErrorKind.TokenExpired or InstagramErrorKind.PermissionDenied
                        ? FacebookLiveLookupStatus.AuthProblem
                        : FacebookLiveLookupStatus.Failed);
            }

            var parsed = await resp.Content.ReadFromJsonAsync<LiveVideosResponse>(
                cancellationToken: ct).ConfigureAwait(false);

            // Pages can have at most one active broadcast at a time per Meta —
            // but the API returns a list, so we just pick the first LIVE one.
            if (parsed?.Data is not null)
            {
                foreach (var v in parsed.Data)
                {
                    if (!IsLive(v.Status)) continue;
                    // live_comments is keyed by the VIDEO id, not the LiveVideo
                    // id — feeding the LiveVideo id to the SSE endpoint yields a
                    // 400. Prefer video.id; fall back to the LiveVideo id only if
                    // the subfield is unexpectedly absent.
                    var videoId = v.Video?.Id ?? v.Id;
                    if (!string.IsNullOrEmpty(videoId))
                        return new FacebookLiveLookup(FacebookLiveLookupStatus.Live, videoId);
                }
            }
            return new FacebookLiveLookup(FacebookLiveLookupStatus.NotLive);
        }
        catch (System.OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (System.Exception ex)
        {
            _log.LogDebug(ex, "[FacebookLiveVideoResolver] resolve failed (treating as offline)");
            return new FacebookLiveLookup(FacebookLiveLookupStatus.Failed);
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
        [JsonPropertyName("video")] public VideoRef? Video { get; set; }
    }

    private sealed class VideoRef
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
    }
}
