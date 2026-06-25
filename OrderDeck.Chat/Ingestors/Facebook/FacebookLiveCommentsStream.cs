using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Chat;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>
/// Consumes the Facebook Live Comments streaming endpoint
/// (<c>streaming-graph.facebook.com/{video-id}/live_comments</c>) and
/// publishes each comment to the shared <see cref="IChatBus"/>.
///
/// <para>The endpoint is a long-lived HTTP response in Server-Sent Events
/// (SSE) format. Each event is a <c>data: { ... json ... }</c> line,
/// terminated by a blank line. We don't get heartbeats or explicit "end"
/// markers — the connection just closes when the broadcast ends or Meta
/// resets it, so the owner (hosted service) decides to retry vs back off.</para>
///
/// <para>Implements <see cref="IChatIngestor"/> for parity with
/// <see cref="OrderDeck.Chat.Ingestors.YouTube.YouTubeLiveChatScraper"/>:
/// <c>StartAsync</c> opens the stream, <c>StopAsync</c> tears it down, and
/// <see cref="Completion"/> resolves when the stream ends naturally.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookLiveCommentsStream : IChatIngestor, IDisposable
{
    /// <summary>
    /// <c>comment_rate</c> setting we send to Meta. <c>one_per_two_seconds</c>
    /// is the lowest (highest throttle) — for live sales chat that's plenty
    /// and keeps us well clear of any per-stream cap. Override per env via
    /// <see cref="StartAsync"/> consumers if needed.
    /// </summary>
    private const string CommentRate = "one_per_two_seconds";

    /// <summary>
    /// Fields we ask Meta to include in each comment event. Mirrors what
    /// <see cref="ChatMessage"/> needs: id, author handle/name, message body,
    /// timestamp. Avatar URL comes via <c>from.picture</c>.
    /// </summary>
    private const string Fields = "id,from{id,name,picture},message,created_time";

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

    // Dedupe ring — Meta sometimes redelivers the same id when the
    // underlying TCP connection flaps. Bounded to keep memory flat.
    private const int MaxSeenIds = 5000;
    private readonly HashSet<string> _seenIds = new(MaxSeenIds);
    private readonly Queue<string> _seenIdsOrder = new(MaxSeenIds);

    public string Platform => "facebook";

    /// <summary>Resolves when the runner exits — cancellation, network error,
    /// or stream end (Meta closed the response). Lets the hosted service
    /// await this instead of pinning <c>Task.Delay(InfiniteTimeSpan)</c>.</summary>
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
        try
        {
            var url = $"https://streaming-graph.facebook.com/{Uri.EscapeDataString(_liveVideoId)}" +
                      $"/live_comments" +
                      $"?access_token={Uri.EscapeDataString(_pageAccessToken)}" +
                      $"&comment_rate={CommentRate}" +
                      $"&fields={Uri.EscapeDataString(Fields)}";

            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("text/event-stream");

            using var resp = await _http.SendAsync(
                req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _log.LogWarning(
                    "[FacebookLiveCommentsStream] open failed for video {VideoId}: {Status} {Reason}: {Body}",
                    _liveVideoId, (int)resp.StatusCode, resp.ReasonPhrase, body);
                return;
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream);

            _log.LogInformation(
                "[FacebookLiveCommentsStream] connected to video {VideoId}", _liveVideoId);

            // SSE format: each event is one or more lines, ended by a blank line.
            // We only care about "data:" lines — everything else (event:, id:,
            // retry:, comments) is ignored. Multi-line data: payloads (RFC says
            // concat with '\n') are theoretically possible but Meta sends each
            // comment as a single data line, so we parse line-by-line.
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break; // Meta closed the response → stream ended.
                if (line.Length == 0) continue; // event delimiter
                if (!line.StartsWith("data:", StringComparison.Ordinal)) continue;

                var payload = line.AsSpan(5).TrimStart().ToString();
                if (payload.Length == 0) continue;

                if (TryParse(payload, out var msg))
                    TryPublish(msg);
            }
        }
        catch (OperationCanceledException) { /* expected on stop / app shutdown */ }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "[FacebookLiveCommentsStream] runner failed for video {VideoId}", _liveVideoId);
        }
        finally
        {
            _completionTcs.TrySetResult();
        }
    }

    private bool TryParse(string json, out ChatMessage msg)
    {
        msg = null!;
        try
        {
            var ev = JsonSerializer.Deserialize<CommentEvent>(json);
            if (ev is null || string.IsNullOrEmpty(ev.Id)) return false;

            // Meta occasionally streams metadata frames (e.g. heartbeat-ish
            // events with no message text). Skip them — nothing to publish.
            if (string.IsNullOrWhiteSpace(ev.Message)) return false;

            var unixSec = ev.CreatedTime.HasValue
                ? new DateTimeOffset(ev.CreatedTime.Value, TimeSpan.Zero).ToUnixTimeSeconds()
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
        catch (JsonException ex)
        {
            _log.LogDebug(ex, "[FacebookLiveCommentsStream] failed to parse SSE payload");
            return false;
        }
    }

    private void TryPublish(ChatMessage msg)
    {
        // Dedupe by ExternalId — same redelivery semantics as the YouTube
        // scraper. Bounded HashSet + FIFO queue → O(1) insert + eviction.
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

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }

    // ---- wire shapes ----------------------------------------------------

    private sealed class CommentEvent
    {
        [JsonPropertyName("id")] public string? Id { get; set; }
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("created_time")] public DateTime? CreatedTime { get; set; }
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
