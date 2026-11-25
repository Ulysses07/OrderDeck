using System.Net.Http;
using System.Text.Json;
using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Chat;
using OrderDeck.Chat.Ingestors.YouTube.Grpc;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// Faz 2: resmi YouTube Data API ingestor — gRPC <c>liveChatMessages.streamList</c>
/// (server-push) ile canlı sohbeti çeker. Scraper'ın aksine Top/Canlı filtresi
/// YOK (eksiksiz akış) ve resmi/stabil sözleşme. Kota tüketir (~5 birim/RPC;
/// reconnect başına bir RPC). API key yeterli — OAuth gerekmez (PoC doğruladı).
///
/// Lifecycle scraper ile simetrik (StartAsync/StopAsync/Completion) ki hosted
/// service iki yolu da aynı şekilde yönetebilsin.
/// </summary>
public sealed class YouTubeOfficialChatIngestor : IChatIngestor, IYouTubeChatIngestor
{
    // SUPER_CHAT_EVENT (stream_list.proto LiveChatMessageSnippet.Type)
    private const int SuperChatType = 15;

    private readonly string _videoId;
    private readonly string _apiKey;
    private readonly IChatBus _bus;
    private readonly ILogger<YouTubeOfficialChatIngestor> _log;
    private readonly HttpClient _http;
    private readonly SpamFilter? _spamFilter;
    private readonly ViewerCountTracker? _viewers;
    // İzleyici sayısı videos.list'ten periyodik çekilir (concurrentViewers).
    // 30 sn → ~540 çağrı/4.5sa yayın; chat'ten ayrı, hata toleranslı.
    private static readonly TimeSpan ViewerPollInterval = TimeSpan.FromSeconds(30);

    // Çift-yayın koruması. Okuyucuya dışarıdan verilir ki okuyucu yeniden kurulunca
    // (yenileme, yayın oturumunu bitirip açma) sıfırlanmasın: bkz. YouTubeSeenMessages.
    private readonly YouTubeSeenMessages _seen;

    private GrpcChannel? _channel;
    private CancellationTokenSource? _cts;
    private Task? _runner;
    private Task? _viewerRunner;
    private string? _liveChatId;
    private volatile int _endReason = (int)YouTubeIngestorEnd.Cancelled;
    private readonly TaskCompletionSource _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string Platform => "youtube";

    /// <summary>Runner çıkınca (yayın offline / terminal hata / iptal) tamamlanır —
    /// hosted service bunu await edip yeniden resolve eder.</summary>
    public Task Completion => _completion.Task;

    /// <inheritdoc />
    public YouTubeIngestorEnd EndReason => (YouTubeIngestorEnd)_endReason;

    /// <param name="liveChatId">Biliniyorsa (liveBroadcasts.list zaten dönüyor)
    /// geçilir ve videos.list ile ikinci bir çözümleme yapılmaz — 1 birim tasarruf.</param>
    /// <param name="seen">Gösterilmiş mesaj belleği. Servis, kurduğu bütün okuyuculara aynı örneği
    /// verir; verilmezse okuyucu kendi (kendi ömrüyle sınırlı) belleğini kullanır.</param>
    public YouTubeOfficialChatIngestor(
        string videoId, string apiKey, IChatBus bus,
        ILogger<YouTubeOfficialChatIngestor> log, HttpClient http,
        SpamFilter? spamFilter = null, ViewerCountTracker? viewers = null,
        string? liveChatId = null, YouTubeSeenMessages? seen = null)
    {
        _videoId = videoId;
        _apiKey = apiKey;
        _bus = bus;
        _log = log;
        _http = http;
        _spamFilter = spamFilter;
        _viewers = viewers;
        _liveChatId = string.IsNullOrWhiteSpace(liveChatId) ? null : liveChatId;
        _seen = seen ?? new YouTubeSeenMessages();
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _liveChatId ??= await ResolveLiveChatIdAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrEmpty(_liveChatId))
            throw new YouTubeNoLiveChatException(
                "activeLiveChatId yok — yayın canlı değil ya da chat kapalı");

        _log.LogInformation("[YouTube Official] bağlanılıyor (streamList) liveChatId={LiveChatId}", _liveChatId);
        _channel = GrpcChannel.ForAddress("https://youtube.googleapis.com");

        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var loopCt = _cts.Token;
        _runner = Task.Run(() => RunLoopAsync(loopCt), loopCt);
        // İzleyici sayısı sohbetten bağımsız hafif döngüde (videos.list).
        if (_viewers is not null)
            _viewerRunner = Task.Run(() => ViewerLoopAsync(loopCt), loopCt);
    }

    /// <summary>videos.list concurrentViewers'ı periyodik çekip ViewerCountTracker'a
    /// yollar. Sohbet akışından bağımsız; bir tur patlarsa sonraki turda dener.</summary>
    private async Task ViewerLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var n = await FetchConcurrentViewersAsync(ct).ConfigureAwait(false);
                if (n is not null)
                {
                    _viewers!.Report("youtube", n.Value);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[YouTube Official] viewer count fetch failed");
            }
            try { await Task.Delay(ViewerPollInterval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task<int?> FetchConcurrentViewersAsync(CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(_videoId)}&key={Uri.EscapeDataString(_apiKey)}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return null;
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            return null;
        if (!items[0].TryGetProperty("liveStreamingDetails", out var details))
            return null;
        if (details.TryGetProperty("concurrentViewers", out var cv)
            && cv.GetString() is { Length: > 0 } s
            && int.TryParse(s, out var n))
            return n;
        return null;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var client = new V3DataLiveChatMessageService.V3DataLiveChatMessageServiceClient(_channel!);
        var metadata = new Metadata { { "x-goog-api-key", _apiKey } };
        string? pageToken = null;
        var reconnects = 0;
        var offline = false;
        Exception? error = null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var req = new LiveChatMessageListRequest { LiveChatId = _liveChatId, MaxResults = 2000 };
                req.Part.Add("snippet");
                req.Part.Add("authorDetails");
                if (pageToken is not null) req.PageToken = pageToken;

                using var call = client.StreamList(req, metadata, cancellationToken: ct);
                await foreach (var resp in call.ResponseStream.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    if (resp.HasNextPageToken && resp.NextPageToken.Length > 0)
                        pageToken = resp.NextPageToken;

                    foreach (var item in resp.Items)
                        PublishMapped(item);

                    if (resp.HasOfflineAt && resp.OfflineAt.Length > 0)
                    {
                        _log.LogInformation("[YouTube Official] yayın offline (offline_at={OfflineAt})", resp.OfflineAt);
                        offline = true;
                        return;
                    }
                }

                if (ct.IsCancellationRequested) break;
                // Stream sunucu tarafından kapandı → page_token ile devam et (kayıpsız).
                reconnects++;
                _log.LogDebug("[YouTube Official] stream sonu; reconnect #{N}", reconnects);
            }
        }
        catch (OperationCanceledException ex) { error = ex; /* normal kapanış */ }
        catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition)
        {
            // gRPC FAILED_PRECONDITION: chat kapalı VEYA bitti (ayırt edilemez).
            error = ex;
            _log.LogInformation("[YouTube Official] chat kapalı/bitti (FAILED_PRECONDITION)");
        }
        catch (RpcException ex) when (ct.IsCancellationRequested)
        {
            // İptal edilen çağrı RpcException(Cancelled) atabilir: hata değil, normal kapanış.
            error = ex;
        }
        catch (RpcException ex)
        {
            error = ex;
            _log.LogWarning(ex, "[YouTube Official] gRPC hata {Status}; runner çıkıyor", ex.StatusCode);
        }
        catch (Exception ex)
        {
            error = ex;
            _log.LogWarning(ex, "[YouTube Official] beklenmeyen hata; runner çıkıyor");
        }
        finally
        {
            // Sebep, tamamlanma bildirilmeden ÖNCE yazılır: Completion'ı bekleyen döngü onu okur.
            _endReason = (int)ClassifyEnd(error, offline, ct.IsCancellationRequested);
            _completion.TrySetResult();
        }
    }

    /// <summary>
    /// Okuyucunun çıkış sebebi. Saf: test doğrudan çağırır.
    /// <para>İptal her şeyin önündedir: yenileme ve oturum bitişi okuyucuyu iptalle keser ve iptal
    /// edilen gRPC çağrısı <see cref="OperationCanceledException"/> yerine
    /// <c>RpcException(Cancelled)</c> atabilir; bu "hata" sayılırsa her yenileme hata sayacını
    /// büyütür.</para>
    /// </summary>
    /// <param name="error">Akışı bitiren istisna; akış istisnasız bittiyse null.</param>
    /// <param name="offline">Akış "yayın çevrimdışı" işaretini gönderdi mi.</param>
    /// <param name="cancelled">Okuyucunun iptal belirteci tetiklenmiş mi.</param>
    internal static YouTubeIngestorEnd ClassifyEnd(Exception? error, bool offline, bool cancelled)
    {
        if (cancelled) return YouTubeIngestorEnd.Cancelled;
        if (offline) return YouTubeIngestorEnd.BroadcastEnded;

        return error switch
        {
            null or OperationCanceledException => YouTubeIngestorEnd.Cancelled,
            // Sohbet kapatıldı ya da yayın bitti; gRPC ikisini ayırt etmez.
            RpcException { StatusCode: StatusCode.FailedPrecondition } => YouTubeIngestorEnd.BroadcastEnded,
            _ => YouTubeIngestorEnd.Failed,
        };
    }

    private void PublishMapped(LiveChatMessage item)
    {
        var msg = MapToChatMessage(item);
        if (msg is null) return;

        // Çift-yayın koruması (reconnect/backlog örtüşmesine karşı), id-bazlı.
        if (!_seen.TryAdd(msg.ExternalId!)) return;

        if (_spamFilter is not null)
        {
            var reason = _spamFilter.ShouldDrop(msg.Text, msg.Username,
                DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            if (reason is not null)
            {
                _log.LogDebug("[YouTube Official] spam filter drop ({Reason})", reason);
                return;
            }
        }

        _bus.Publish(msg);
    }

    /// <summary>Proto LiveChatMessage → OrderDeck ChatMessage. Saf/statik —
    /// birim test örnek proto nesnesiyle doğrudan çağırabilsin. Metin yoksa
    /// (örn. salt-rozet event) null döner.</summary>
    internal static ChatMessage? MapToChatMessage(LiveChatMessage item)
    {
        var id = item.Id;
        if (string.IsNullOrEmpty(id)) return null;

        var snippet = item.Snippet;
        var author = item.AuthorDetails;

        // proto2: set edilmemiş optional string "" döner (null değil), o yüzden
        // ?? yerine boş-kontrolüyle text_message_details'e düş.
        var text = snippet is null
            ? string.Empty
            : !string.IsNullOrEmpty(snippet.DisplayMessage)
                ? snippet.DisplayMessage
                : snippet.TextMessageDetails?.MessageText ?? string.Empty;
        var isSuperChat = snippet?.Type == SuperChatType;
        if (string.IsNullOrEmpty(text) && !isSuperChat) return null;

        var displayName = author?.DisplayName ?? string.Empty;
        var channelId = author?.ChannelId ?? string.Empty;

        var badges = new List<string>();
        if (author is not null)
        {
            if (author.IsChatOwner) badges.Add("owner");
            if (author.IsChatModerator) badges.Add("moderator");
            if (author.IsChatSponsor) badges.Add("member");
        }
        if (isSuperChat) badges.Add("superchat");

        return new ChatMessage(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "youtube",
            ExternalId: id,
            Username: !string.IsNullOrEmpty(channelId) ? channelId : displayName,
            DisplayName: displayName,
            AvatarUrl: author?.ProfileImageUrl,
            Text: isSuperChat && string.IsNullOrEmpty(text) ? "[Super Chat]" : text,
            ReceivedAt: DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Badges: badges);
    }

    /// <summary>videoId → activeLiveChatId (videos.list, API key). ~1 birim kota.</summary>
    private async Task<string?> ResolveLiveChatIdAsync(CancellationToken ct)
    {
        var url = $"https://www.googleapis.com/youtube/v3/videos?part=liveStreamingDetails&id={Uri.EscapeDataString(_videoId)}&key={Uri.EscapeDataString(_apiKey)}";
        using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"videos.list HTTP {(int)resp.StatusCode}: {body}");

        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("items", out var items) || items.GetArrayLength() == 0)
            return null;
        if (!items[0].TryGetProperty("liveStreamingDetails", out var details))
            return null;
        return details.TryGetProperty("activeLiveChatId", out var id) ? id.GetString() : null;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _log.LogDebug(ex, "[YouTube Official] stop wait swallowed"); }
        }
        if (_viewerRunner is not null)
        {
            try { await _viewerRunner.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _log.LogDebug(ex, "[YouTube Official] viewer stop wait swallowed"); }
        }
        _completion.TrySetResult();
    }

    public void Dispose()
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _channel?.Dispose();
    }
}
