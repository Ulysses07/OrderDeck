using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>
/// Ingests a Page's live-broadcast comments by polling the Graph API
/// comments edge (<c>graph.facebook.com/{video-id}/comments</c>) and
/// publishing each new comment to the shared <see cref="IChatBus"/>.
///
/// <para>We poll rather than use the <c>streaming-graph.../live_comments</c>
/// SSE endpoint: that endpoint returns a generic <c>400 / Proxy-Status:
/// http_request_error</c> for this app regardless of id, params, or token —
/// it is simply not usable here. The standard comments edge works reliably
/// (verified live: real + test broadcasts, with author names), so a 2-second
/// poll gives near-real-time chat that is plenty fast for order-taking.</para>
///
/// <para>Implements <see cref="IChatIngestor"/> for parity with the YouTube
/// scraper: <c>StartAsync</c> opens the poll loop, <c>StopAsync</c> tears it
/// down, and <see cref="Completion"/> resolves when the broadcast ends (the
/// comments edge starts returning a <c>#100</c> object-gone error) or the
/// operator stops the session.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookLiveCommentsStream : IChatIngestor, IDisposable
{
    /// <summary>How often we re-poll the comments edge. 1.5s is well within
    /// Graph rate limits for a single Page token (~2,400 calls/hour) and gives
    /// chat that feels live for order-taking.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(1500);

    /// <summary>Per-request timeout. The named client may be configured with
    /// an infinite timeout (it was shared with the old SSE path), so we bound
    /// each poll ourselves via a linked CTS.</summary>
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Give up the loop after this many consecutive transient errors
    /// (network drops, timeouts, 5xx) and let the hosted service re-resolve.</summary>
    private const int MaxConsecutiveErrors = 5;

    /// <summary>
    /// Fields we ask Meta to include per comment. Mirrors what
    /// <see cref="ChatMessage"/> needs: id, author id/name/avatar, body,
    /// timestamp. <c>filter=stream</c> captures the full live comment stream;
    /// <c>order=reverse_chronological</c> returns newest-first (we re-order
    /// oldest-first before publishing).
    /// </summary>
    private const string Fields = "id,message,from{id,name,picture},created_time";

    private readonly string _liveVideoId;
    private readonly string _pageAccessToken;
    private readonly IChatBus _bus;
    private readonly HttpClient _http;
    private readonly ILogger<FacebookLiveCommentsStream> _log;
    private readonly SpamFilter? _spamFilter;

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private readonly TaskCompletionSource _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    // Dedupe ring — successive polls overlap (reverse_chronological returns
    // the same newest comments until they scroll past the limit), so we drop
    // ids we've already published. Bounded to keep memory flat.
    private const int MaxSeenIds = 5000;
    private readonly HashSet<string> _seenIds = new(MaxSeenIds);
    private readonly Queue<string> _seenIdsOrder = new(MaxSeenIds);

    public string Platform => "facebook";

    /// <summary>Resolves when the runner exits — cancellation, repeated
    /// errors, or broadcast end. Lets the hosted service await this instead
    /// of pinning <c>Task.Delay(InfiniteTimeSpan)</c>.</summary>
    public Task Completion => _completionTcs.Task;

    public FacebookLiveCommentsStream(
        string liveVideoId,
        string pageAccessToken,
        IChatBus bus,
        HttpClient http,
        ILogger<FacebookLiveCommentsStream> log,
        SpamFilter? spamFilter = null)
    {
        _liveVideoId = liveVideoId;
        _pageAccessToken = pageAccessToken;
        _bus = bus;
        _http = http;
        _log = log;
        _spamFilter = spamFilter;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* already disposed */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[FacebookLiveCommentsStream] stop wait swallowed");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var url =
            $"https://graph.facebook.com/{FacebookOAuthDefaults.GraphApiVersion}" +
            $"/{Uri.EscapeDataString(_liveVideoId)}/comments" +
            $"?fields={Uri.EscapeDataString(Fields)}" +
            $"&filter=stream&order=reverse_chronological&limit=100" +
            $"&access_token={Uri.EscapeDataString(_pageAccessToken)}";

        _log.LogInformation(
            "[FacebookLiveCommentsStream] polling comments for video {VideoId}", _liveVideoId);

        int consecutiveErrors = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var (batch, ended) = await PollOnceAsync(url, ct).ConfigureAwait(false);
                if (ended) break; // broadcast ended → hosted service re-resolves

                if (batch is not null)
                {
                    consecutiveErrors = 0;
                    // API returns newest-first; publish oldest-first so order
                    // chat reads chronologically. Dedup ring drops overlap.
                    for (int i = batch.Count - 1; i >= 0; i--)
                    {
                        if (TryMap(batch[i], out var msg))
                            TryPublish(msg);
                    }
                }
                else if (++consecutiveErrors >= MaxConsecutiveErrors)
                {
                    _log.LogWarning(
                        "[FacebookLiveCommentsStream] giving up video {VideoId} after {Count} errors",
                        _liveVideoId, consecutiveErrors);
                    break;
                }

                await Task.Delay(PollInterval, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { /* expected on stop / app shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[FacebookLiveCommentsStream] poll loop failed for video {VideoId}", _liveVideoId);
        }
        finally
        {
            _completionTcs.TrySetResult();
        }
    }

    /// <summary>One poll. Returns the fetched comments (newest-first), or
    /// <c>(null, false)</c> on a transient error, or <c>(null, true)</c> when
    /// the broadcast has ended (Graph returns a <c>#100</c> object-gone error
    /// for the live video's comments edge).</summary>
    private async Task<(List<CommentEvent>? batch, bool ended)> PollOnceAsync(
        string url, CancellationToken ct)
    {
        using var reqCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        reqCts.CancelAfter(RequestTimeout);
        try
        {
            using var resp = await _http.GetAsync(url, reqCts.Token).ConfigureAwait(false);
            var body = await resp.Content.ReadAsStringAsync(reqCts.Token).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                // code 100 == the live video object no longer exposes comments
                // (broadcast ended). Anything else == transient; retry.
                bool ended = body.Contains("\"code\":100");
                _log.Log(ended ? LogLevel.Information : LogLevel.Debug,
                    "[FacebookLiveCommentsStream] poll {Status} for {VideoId}: {Body}",
                    (int)resp.StatusCode, _liveVideoId, Truncate(body));
                return (null, ended);
            }

            var parsed = JsonSerializer.Deserialize<CommentsResponse>(body);
            return (parsed?.Data ?? new List<CommentEvent>(), false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // real cancellation propagates to RunAsync
        }
        catch (Exception ex)
        {
            // Per-request timeout or network blip → transient, keep polling.
            _log.LogDebug(ex,
                "[FacebookLiveCommentsStream] poll request failed for {VideoId}", _liveVideoId);
            return (null, false);
        }
    }

    private bool TryMap(CommentEvent? ev, out ChatMessage msg)
    {
        msg = null!;
        if (ev is null || string.IsNullOrEmpty(ev.Id)) return false;

        // Skip comments with no text (rare metadata-only frames).
        if (string.IsNullOrWhiteSpace(ev.Message)) return false;

        // Facebook sends created_time as "2026-07-02T12:56:37+0000" — a
        // colon-less offset that System.Text.Json's DateTime reader rejects
        // (it would throw and the whole poll would be counted as an error).
        // Parse it leniently as a string via DateTimeOffset instead.
        var unixSec = DateTimeOffset.TryParse(
            ev.CreatedTime,
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AssumeUniversal,
            out var created)
            ? created.ToUnixTimeSeconds()
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        msg = new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: Platform,
            ExternalId: ev.Id,
            Username: ev.From?.Id ?? string.Empty,
            DisplayName: ev.From?.Name,
            AvatarUrl: ev.From?.Picture?.Data?.Url,
            Text: ev.Message,
            ReceivedAt: unixSec,
            Badges: System.Array.Empty<string>());
        return true;
    }

    private void TryPublish(ChatMessage msg)
    {
        // Dedupe by ExternalId — bounded HashSet + FIFO queue → O(1) insert
        // and eviction. Same semantics as the YouTube scraper.
        var key = msg.ExternalId ?? msg.Id;
        if (!_seenIds.Add(key)) return;
        _seenIdsOrder.Enqueue(key);
        if (_seenIdsOrder.Count > MaxSeenIds)
        {
            var evicted = _seenIdsOrder.Dequeue();
            _seenIds.Remove(evicted);
        }

        if (_spamFilter is not null)
        {
            var reason = _spamFilter.ShouldDrop(msg.Text, msg.Username, msg.ReceivedAt);
            if (reason is not null)
            {
                _log.LogDebug(
                    "[FacebookLiveCommentsStream] dropped {ExternalId} ({Reason})",
                    msg.ExternalId, reason);
                return;
            }
        }

        _bus.Publish(msg);
    }

    private static string Truncate(string s) =>
        s.Length <= 200 ? s : s.Substring(0, 200);

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }

    // ---- wire shapes ----------------------------------------------------

    private sealed class CommentsResponse
    {
        [JsonPropertyName("data")] public List<CommentEvent>? Data { get; set; }
    }

    private sealed class CommentEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("created_time")] public string? CreatedTime { get; set; }
        [JsonPropertyName("from")] public From? From { get; set; }
    }

    private sealed class From
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("name")] public string? Name { get; set; }
        [JsonPropertyName("picture")] public Picture? Picture { get; set; }
    }

    private sealed class Picture
    {
        [JsonPropertyName("data")] public PictureData? Data { get; set; }
    }

    private sealed class PictureData
    {
        [JsonPropertyName("url")] public string? Url { get; set; }
    }
}
