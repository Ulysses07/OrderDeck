using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using OrderDeck.Core.Chat;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// Polls YouTube's internal live_chat continuation API and forwards each new
/// message into the shared <see cref="IChatBus"/>. Ported from UniCast's
/// YouTubeChatScraper (see /Downloads/UniCast/UniCast/UniCast.Core/Chat/Ingestors/
/// YouTubeChatScraper.cs) with three changes:
///   - Logs via Microsoft.Extensions.ILogger instead of Serilog.Log static.
///   - Publishes OrderDeck's flat ChatMessage record rather than UniCast's
///     class hierarchy.
///   - StartAsync/StopAsync conform to OrderDeck.Core.Chat.IChatIngestor so
///     the existing hosted-service plumbing manages lifecycle.
///
/// API key is harvested from the rendered live_chat HTML (not Google API
/// credentials) — same approach UniCast has been using in production for
/// months. No quota, no auth.
/// </summary>
public sealed class YouTubeLiveChatScraper : IChatIngestor, IDisposable
{
    private static readonly Regex InnertubeApiKey = new(
        @"""INNERTUBE_API_KEY""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ClientVersion = new(
        @"""clientVersion""\s*:\s*""([^""]+)""", RegexOptions.Compiled);
    private static readonly Regex ContinuationField = new(
        @"""continuation""\s*:\s*""([^""]+)""", RegexOptions.Compiled);

    private const string FallbackClientVersion = "2.20240101.00.00";

    private readonly string _videoId;
    /// <summary>How long the runner can go without seeing a fresh chat row before
    /// declaring the stream ended. YouTube doesn't reliably send a "chat closed"
    /// signal — when a live ends, get_live_chat keeps 200-ing with empty actions.
    /// 3 minutes is the trade-off between false positives (chat naturally goes
    /// quiet during a price negotiation) and recovery latency when the
    /// broadcaster cycles streams (close → 2nd open should pick up chat without
    /// a 10-minute dead window). Operators noted that closing/reopening a stream
    /// left chat dead because the scraper was pinned to the stale video ID.</summary>
    private static readonly TimeSpan StreamSilenceTimeout = TimeSpan.FromMinutes(3);

    private readonly IChatBus _bus;
    private readonly ILogger<YouTubeLiveChatScraper> _log;
    private readonly HttpClient _http;
    // Bounded dedup ring. HashSet alone gave O(1) lookup but the trim
    // (Skip().ToArray() + Clear() + foreach Add()) was O(n) and allocated
    // 2500 strings each time we hit the 5000 cap — visible as GC pressure
    // / chat freezes during long streams. Pairing it with a FIFO queue
    // makes both insert and eviction O(1).
    private const int MaxSeenIds = 5000;
    private readonly HashSet<string> _seenIds = new(MaxSeenIds);
    private readonly Queue<string> _seenIdsOrder = new(MaxSeenIds);

    private string? _apiKey;
    private string? _clientVersion;
    private string? _continuation;
    private int _pollingMs = 2000;
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private DateTimeOffset _lastEventAt = DateTimeOffset.UtcNow;
    private readonly TaskCompletionSource _completionTcs =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Resolves when the runner exits — cancellation, terminal error,
    /// or detected stream-end via the silence heuristic. Lets the hosted service
    /// await scraper completion instead of pinning Task.Delay(InfiniteTimeSpan).</summary>
    public Task Completion => _completionTcs.Task;

    public string Platform => "youtube";

    private readonly SpamFilter? _spamFilter;

    // Browser headers added per-request now (see AddBrowserHeaders) so the
    // HttpClient handed in by IHttpClientFactory is safe to share.
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

    /// <summary>HttpClient comes from IHttpClientFactory now — pooled handler,
    /// no per-instance socket leak. Caller (YouTubeChatHostedService)
    /// resolves it from the named "youtube-scraper" client which already
    /// configures the SocketsHttpHandler this class used to own.</summary>
    public YouTubeLiveChatScraper(string videoId, IChatBus bus, ILogger<YouTubeLiveChatScraper> log,
        HttpClient http, SpamFilter? spamFilter = null)
    {
        _videoId = videoId;
        _bus = bus;
        _log = log;
        _spamFilter = spamFilter;
        _http = http;
        if (_http.Timeout == TimeSpan.FromSeconds(100)) // factory default — override
            _http.Timeout = TimeSpan.FromSeconds(30);
    }

    private static void AddBrowserHeaders(HttpRequestMessage req)
    {
        req.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        req.Headers.AcceptLanguage.ParseAdd("tr-TR,tr;q=0.9,en-US;q=0.8,en;q=0.7");
        req.Headers.Accept.ParseAdd("text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("[YouTube Scraper] Bağlanılıyor: {VideoId}", _videoId);
        await BootstrapAsync(ct);
        _log.LogInformation("[YouTube Scraper] Bağlantı başarılı, mesaj döngüsü başlıyor");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loopCt = _cts.Token;
        _runner = Task.Run(() => RunLoopAsync(loopCt), loopCt);
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(ct); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _log.LogDebug(ex, "[YouTube Scraper] runner stop swallowed"); }
        }
        _continuation = null;
        _apiKey = null;
        _seenIds.Clear();
        _seenIdsOrder.Clear();
    }

    private async Task BootstrapAsync(CancellationToken ct)
    {
        var chatUrl = $"https://www.youtube.com/live_chat?v={_videoId}&is_popout=1";
        using var bootstrapReq = new HttpRequestMessage(HttpMethod.Get, chatUrl);
        AddBrowserHeaders(bootstrapReq);
        using var resp = await _http.SendAsync(bootstrapReq, ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"YouTube live_chat HTTP {resp.StatusCode}");

        var html = await resp.Content.ReadAsStringAsync(ct);

        var keyMatch = InnertubeApiKey.Match(html);
        if (!keyMatch.Success)
            throw new InvalidOperationException("YouTube INNERTUBE_API_KEY not found — page layout may have changed");
        _apiKey = keyMatch.Groups[1].Value;

        var versionMatch = ClientVersion.Match(html);
        _clientVersion = versionMatch.Success ? versionMatch.Groups[1].Value : FallbackClientVersion;

        var contMatch = ContinuationField.Match(html);
        if (contMatch.Success)
        {
            _continuation = contMatch.Groups[1].Value;
        }
        else
        {
            // Deep search through ytInitialData JSON for any "continuation" key.
            var ytData = Regex.Match(html, @"ytInitialData\s*=\s*(\{.+?\});\s*</script>", RegexOptions.Singleline);
            if (ytData.Success)
            {
                try
                {
                    using var doc = JsonDocument.Parse(ytData.Groups[1].Value);
                    _continuation = FindFirstByKey(doc.RootElement, "continuation");
                }
                catch (Exception ex)
                {
                    _log.LogWarning(ex, "[YouTube Scraper] ytInitialData parse failed");
                }
            }
        }

        if (string.IsNullOrEmpty(_continuation))
            throw new InvalidOperationException("Live chat is not available — stream may not be live or chat is disabled");
    }

    // Rate-limited failure logging: at 2-second polling, a 10-minute
    // outage was producing 300 LogWarning lines (~10 MB of disk in a
    // long stream). Now: log the FIRST failure as Warning, suppress
    // intermediate ones to Debug, then re-log as Warning every 30
    // failures (= ~5 minutes of consecutive failure) so the operator
    // sees we're still trying. On recovery, log a single Information
    // line so the gap is bookended in the file sink.
    private int _consecutiveFetchFailures;

    private async Task RunLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await FetchOnceAsync(ct);

                    if (_consecutiveFetchFailures > 0)
                    {
                        _log.LogInformation(
                            "[YouTube Scraper] recovered after {Count} failed iterations",
                            _consecutiveFetchFailures);
                        _consecutiveFetchFailures = 0;
                    }

                    // Stream-end heuristic: if get_live_chat keeps returning but
                    // nothing has been Publish()d for StreamSilenceTimeout we
                    // assume the broadcaster ended their stream. The hosted
                    // service then re-resolves the channel handle and starts
                    // fresh when the streamer goes live again.
                    if (DateTimeOffset.UtcNow - _lastEventAt > StreamSilenceTimeout)
                    {
                        _log.LogInformation(
                            "[YouTube Scraper] no chat activity for {Min} min — assuming stream ended",
                            StreamSilenceTimeout.TotalMinutes);
                        break;
                    }

                    await Task.Delay(_pollingMs, ct);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    _consecutiveFetchFailures++;
                    if (_consecutiveFetchFailures == 1 || _consecutiveFetchFailures % 30 == 0)
                    {
                        _log.LogWarning(ex,
                            "[YouTube Scraper] iteration failed (consecutive={Count}); backing off 5s",
                            _consecutiveFetchFailures);
                    }
                    else
                    {
                        _log.LogDebug(ex,
                            "[YouTube Scraper] iteration failed (consecutive={Count}); backing off 5s",
                            _consecutiveFetchFailures);
                    }
                    try { await Task.Delay(5000, ct); } catch { break; }
                }
            }
        }
        finally
        {
            _completionTcs.TrySetResult();
        }
    }

    private async Task FetchOnceAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_continuation) || string.IsNullOrEmpty(_apiKey)) return;

        var url = $"https://www.youtube.com/youtubei/v1/live_chat/get_live_chat?key={_apiKey}";
        var requestBody = new
        {
            context = new
            {
                client = new
                {
                    clientName = "WEB",
                    clientVersion = _clientVersion,
                    hl = "tr",
                    gl = "TR",
                    timeZone = "Europe/Istanbul"
                }
            },
            continuation = _continuation
        };

        var content = new StringContent(
            JsonSerializer.Serialize(requestBody), System.Text.Encoding.UTF8, "application/json");
        using var pollReq = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        AddBrowserHeaders(pollReq);
        using var resp = await _http.SendAsync(pollReq, ct);
        if (!resp.IsSuccessStatusCode)
        {
            _log.LogWarning("[YouTube Scraper] poll HTTP {Status}", (int)resp.StatusCode);
            return;
        }

        var json = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // Continuation rotation — every poll returns a fresh token.
        if (TryNested(root, "continuationContents.liveChatContinuation.continuations", out var continuations))
        {
            foreach (var c in continuations.EnumerateArray())
            {
                if (c.TryGetProperty("invalidationContinuationData", out var inv))
                    UpdateContinuation(inv);
                else if (c.TryGetProperty("timedContinuationData", out var timed))
                    UpdateContinuation(timed);
            }
        }

        // Action items (each is one chat row).
        if (TryNested(root, "continuationContents.liveChatContinuation.actions", out var actions))
        {
            foreach (var action in actions.EnumerateArray())
                ProcessAction(action);
        }
    }

    private void UpdateContinuation(JsonElement node)
    {
        if (node.TryGetProperty("continuation", out var t)) _continuation = t.GetString();
        if (node.TryGetProperty("timeoutMs", out var to))
            _pollingMs = Math.Clamp(to.GetInt32(), 1000, 5000);
    }

    private void ProcessAction(JsonElement action)
    {
        try
        {
            JsonElement itemAction;
            if (action.TryGetProperty("addChatItemAction", out var add))
                itemAction = add;
            else if (action.TryGetProperty("replayChatItemAction", out var replay))
                itemAction = replay;
            else
                return;

            if (!itemAction.TryGetProperty("item", out var item)) return;

            // Only text + paid messages publish for now. Stickers / memberships
            // are noise for OrderDeck's order-taking workflow.
            if (item.TryGetProperty("liveChatTextMessageRenderer", out var text))
                Publish(text, isPaid: false);
            else if (item.TryGetProperty("liveChatPaidMessageRenderer", out var paid))
                Publish(paid, isPaid: true);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[YouTube Scraper] action parse failed");
        }
    }

    private void Publish(JsonElement renderer, bool isPaid)
    {
        var text = ExtractRunsText(renderer, "message");
        var displayName = ExtractRunsText(renderer, "authorName");
        var channelId = renderer.TryGetProperty("authorExternalChannelId", out var cid)
            ? cid.GetString() ?? string.Empty
            : string.Empty;
        var avatar = ExtractFirstThumbnail(renderer, "authorPhoto");

        var dedupKey = renderer.TryGetProperty("id", out var idEl) && idEl.GetString() is { Length: > 0 } realId
            ? realId
            : $"{channelId}|{ExtractTimestampUsec(renderer)}|{text}";

        if (!_seenIds.Add(dedupKey)) return;
        _seenIdsOrder.Enqueue(dedupKey);
        // O(1) eviction: pop the oldest key from the FIFO order queue and
        // mirror the removal in the lookup set. Bounded at MaxSeenIds.
        while (_seenIdsOrder.Count > MaxSeenIds)
        {
            var evicted = _seenIdsOrder.Dequeue();
            _seenIds.Remove(evicted);
        }

        if (string.IsNullOrEmpty(text) && !isPaid) return;

        // Reset the stream-silence clock. RunLoopAsync uses this to detect
        // when YouTube quietly stops returning new actions (broadcaster ended
        // the live) without sending us an explicit close signal.
        _lastEventAt = DateTimeOffset.UtcNow;

        var badges = new List<string>();
        if (renderer.TryGetProperty("authorBadges", out var badgesEl))
        {
            var badgesJson = badgesEl.ToString();
            if (badgesJson.Contains("OWNER"))     badges.Add("owner");
            if (badgesJson.Contains("MODERATOR")) badges.Add("moderator");
            if (badgesJson.Contains("MEMBER"))    badges.Add("member");
        }
        if (isPaid) badges.Add("superchat");

        // Spam filter — apply AFTER dedup + badges resolve so paid super-chats
        // are never accidentally filtered (rules ignore the badge list, but a
        // future tuning might whitelist superchat by checking it here).
        if (_spamFilter is not null && !isPaid)
        {
            var dropReason = _spamFilter.ShouldDrop(
                text,
                !string.IsNullOrEmpty(channelId) ? channelId : displayName,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (dropReason is not null)
            {
                _log.LogDebug("[YouTube Scraper] spam filter drop ({Reason}) by {User}: {Text}",
                    dropReason, displayName, text);
                return;
            }
        }

        _bus.Publish(new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "youtube",
            ExternalId: dedupKey,
            Username: !string.IsNullOrEmpty(channelId) ? channelId : displayName,
            DisplayName: displayName,
            AvatarUrl: avatar,
            Text: isPaid && string.IsNullOrEmpty(text) ? "[Super Chat]" : text,
            ReceivedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Badges: badges));
    }

    // ─── JSON helpers (1:1 from UniCast, condensed) ──────────────────

    private static bool TryNested(JsonElement root, string path, out JsonElement result)
    {
        result = root;
        foreach (var part in path.Split('.'))
        {
            if (!result.TryGetProperty(part, out result)) return false;
        }
        return true;
    }

    private static string ExtractRunsText(JsonElement renderer, string field)
    {
        if (!renderer.TryGetProperty(field, out var node)) return string.Empty;
        if (node.TryGetProperty("simpleText", out var simple)) return simple.GetString() ?? string.Empty;
        if (!node.TryGetProperty("runs", out var runs)) return string.Empty;
        var sb = new System.Text.StringBuilder();
        foreach (var run in runs.EnumerateArray())
        {
            if (run.TryGetProperty("text", out var t)) sb.Append(t.GetString());
            else if (run.TryGetProperty("emoji", out var emoji) &&
                     emoji.TryGetProperty("shortcuts", out var shortcuts) &&
                     shortcuts.GetArrayLength() > 0)
            {
                sb.Append(shortcuts[0].GetString());
            }
        }
        return sb.ToString();
    }

    private static string? ExtractFirstThumbnail(JsonElement renderer, string field)
    {
        if (!renderer.TryGetProperty(field, out var photo)) return null;
        if (!photo.TryGetProperty("thumbnails", out var thumbs)) return null;
        if (thumbs.GetArrayLength() == 0) return null;
        return thumbs[0].TryGetProperty("url", out var u) ? u.GetString() : null;
    }

    private static long ExtractTimestampUsec(JsonElement renderer)
    {
        if (renderer.TryGetProperty("timestampUsec", out var ts) &&
            long.TryParse(ts.GetString(), out var v))
            return v;
        return 0;
    }

    private static string? FindFirstByKey(JsonElement element, string key)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty(key, out var found) && found.ValueKind == JsonValueKind.String)
                return found.GetString();
            foreach (var prop in element.EnumerateObject())
            {
                var r = FindFirstByKey(prop.Value, key);
                if (r is not null) return r;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var r = FindFirstByKey(item, key);
                if (r is not null) return r;
            }
        }
        return null;
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { }
        // HttpClient lifecycle is owned by IHttpClientFactory now — Dispose
        // here would tear down the pooled handler for the next scraper.
        _seenIds.Clear();
        _seenIdsOrder.Clear();
    }
}
