using System;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using OrderDeck.Core.Chat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace OrderDeck.Chat.Bridge;

/// <summary>
/// Hosts a localhost WebSocket endpoint (`ws://localhost:{port}/extension`) that the browser
/// extension content scripts connect to. Each incoming JSON payload is parsed as an
/// <see cref="ExtensionMessage"/>; "chat" messages are forwarded to the supplied
/// <see cref="IChatBus"/>.
///
/// <para>El sıkışmalar <see cref="IsAllowedOrigin"/> ile süzülür — localhost'ta
/// dinlemek tek başına kapıyı kapatmaz.</para>
/// </summary>
public sealed class ExtensionBridgeServer : IAsyncDisposable
{
    // Static so we don't recreate the options per-message — used to be inside
    // Handle()'s parse loop, allocating on every chat row.
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IChatBus _bus;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly ViewerCountTracker? _viewers;
    // OfficialApi modunda extension'dan gelen Instagram yorumlarını bastırırız;
    // aynı yorum Graph poller'dan da geliyor ve iki yolun externalId uzayları
    // ayrık olduğu için dedupe yakalayamaz. Func<> çünkü operatör Ayarlar'dan
    // modu değiştirince köprüyü yeniden başlatmadan etkili olmalı.
    private readonly Func<bool>? _isInstagramOfficial;
    private readonly ILogger<ExtensionBridgeServer> _log;
    private readonly HttpListener _listener = new();
    private CancellationTokenSource? _cts;
    private Task? _runner;

    // Active WebSocket connection counter — read by GET /_health so the
    // first-run wizard can confirm the operator's Chrome extension is
    // installed AND connected. Interlocked so the AcceptLoop publisher
    // and the /_health request thread agree without a lock.
    private int _activeWebSocketCount;
    public int ActiveWebSocketCount => Volatile.Read(ref _activeWebSocketCount);

    // Belt-and-suspenders server-side dedupe. The extension does primary
    // dedupe via DOM-element identity (WeakSet). Server dedup keys on
    // externalId — which the extension generates per comment-DOM-node and
    // is therefore unique per logical comment, including re-buys of the
    // same code by the same customer (a new DOM node → new externalId →
    // new order, intentionally).
    //
    // We still catch true duplicates that arise from: reconnects, multiple
    // tabs of the same platform open to the same broadcast, or operator
    // dev-reloading the extension within the server's uptime.
    //
    // 2026-05-22 (#92): replaced 5s TTL with session-scoped to fix a
    // regression where IG kept comments in DOM > 5s and TTL expiry caused
    // re-emit.
    // 2026-05-22 (#93): switched dedupe key from (platform,user,text) to
    // externalId so customers can re-buy the same code multiple times.
    private const int SeenLimit = 20_000;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _seen = new();
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _seenOrder = new();
    private long _dedupedCount;
    public long DedupedCount => Volatile.Read(ref _dedupedCount);

    private bool TryRegisterSeen(string externalId)
    {
        if (!_seen.TryAdd(externalId, 0)) return false;
        _seenOrder.Enqueue(externalId);
        // FIFO eviction once we exceed the cap. We may briefly overshoot under
        // concurrency, but bounded growth is what matters.
        while (_seen.Count > SeenLimit && _seenOrder.TryDequeue(out var oldest))
        {
            _seen.TryRemove(oldest, out _);
        }
        return true;
    }

    public int Port { get; private set; }

    public ExtensionBridgeServer(IChatBus bus, int port = 4748,
        ILogger<ExtensionBridgeServer>? log = null,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        ViewerCountTracker? viewers = null,
        Func<bool>? isInstagramOfficial = null)
    {
        _bus = bus;
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _viewers = viewers;
        _isInstagramOfficial = isInstagramOfficial;
        _log = log ?? NullLogger<ExtensionBridgeServer>.Instance;
        Port = port == 0 ? FindFreePort() : port;
        _listener.Prefixes.Add($"http://localhost:{Port}/");
    }

    public Task StartAsync(CancellationToken ct)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _listener.Start();
        _runner = Task.Run(() => AcceptLoop(_cts.Token), _cts.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        _cts?.Cancel();
        try { _listener.Stop(); } catch { /* ignore */ }
        if (_runner is not null)
            try { await _runner; } catch { /* ignore */ }
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch { return; }

            if (!context.Request.IsWebSocketRequest)
            {
                // GET /_health → JSON status. Used by the first-run wizard's
                // "Doğrula" button to confirm the Chrome extension is loaded
                // AND connected to the bridge (the WS handshake is what
                // actually proves it; HTTP just exposes the counter).
                if (context.Request.HttpMethod == "GET" &&
                    context.Request.Url?.AbsolutePath == "/_health")
                {
                    HandleHealthRequest(context);
                    continue;
                }
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            var origin = context.Request.Headers["Origin"];
            if (!IsAllowedOrigin(origin))
            {
                // Güvenlik olayı: sahada log taramasıyla görülebilsin.
                _log.LogWarning(
                    "Köprüye izinsiz kaynaktan WebSocket bağlantısı reddedildi: Origin={Origin}",
                    string.IsNullOrEmpty(origin) ? "(başlık yok)" : origin);
                context.Response.StatusCode = 403;
                context.Response.Close();
                continue;
            }

            var wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            // Fire-and-forget per-connection handler. ContinueWith logs any
            // unobserved exception so we don't lose runtime errors silently —
            // Handle has its own per-frame catch but scheduling/setup errors
            // would otherwise vanish into the unobserved-task stream.
            _ = Task.Run(() => Handle(wsContext.WebSocket, ct), ct)
                .ContinueWith(t => _log.LogError(t.Exception, "Extension WS handler crashed"),
                    CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
        }
    }

    /// <summary>
    /// WebSocket el sıkışmalarında kabul edilen kaynaklar.
    ///
    /// <para><b>Neden gerekli.</b> WebSocket CORS'a tabi değildir: tarayıcı
    /// <c>Origin</c> başlığını gönderir ama <i>zorlamaz</i> — doğrulama tamamen
    /// sunucunun işidir. Kapı açık kaldığında yayıncının açtığı herhangi bir web
    /// sayfası <c>ws://localhost:4748/extension</c>'a bağlanıp sahte "chat"
    /// çerçevesi basabilir. Mezat modelinde ürün kodu içeren yorum bir sipariş
    /// demek; yani uydurma sipariş, uydurma müşteri, sahte izleyici sayısı ve
    /// sahte "kayıp yorum" hata logu.</para>
    ///
    /// <para><b>Başlık yoksa reddedilir.</b> Tarayıcılar <c>Origin</c>'i her
    /// zaman gönderir; göndermeyen istemci tarayıcı değildir — köprünün tek
    /// meşru istemcisi ise bir tarayıcı uzantısıdır.</para>
    ///
    /// <para><b>Küme neden bu.</b> Uzantının kendi bağlamı (service worker,
    /// popup) <c>chrome-extension://</c> kaynağıyla gelir; content script'ler
    /// sayfanın bağlamında çalıştığı için el sıkışmada sayfanın kaynağı görünür.
    /// İkisi de kabul ediliyor — hangi kaynağın görüneceği Chrome sürümüne göre
    /// değişebilir ve yanlış tahmin sohbetin tamamen susması demek. Alan adı
    /// kümesi <c>manifest.json</c>'daki <c>content_scripts.matches</c> ile aynı,
    /// şema kısıtı da oradaki <c>*://</c> ile hizalı.</para>
    ///
    /// <para><b>Neyi kapatmaz.</b> Yerel bir süreç <c>Origin</c>'i serbestçe
    /// uydurabilir; instagram.com/tiktok.com üstündeki bir XSS de bu kapıdan
    /// geçer. Bunları ancak paylaşılan bir sır ya da native messaging kapatır —
    /// köprünün emekliye ayrılma planı orada.</para>
    /// </summary>
    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        // Chrome ve Edge'in ikisi de uzantı kaynağı için chrome-extension:// kullanır.
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

        return IsHostOrSubdomainOf(uri.Host, "instagram.com")
            || IsHostOrSubdomainOf(uri.Host, "tiktok.com");
    }

    /// <summary>Nokta sınırlı alt alan eşleşmesi — "evilinstagram.com" geçmemeli.</summary>
    private static bool IsHostOrSubdomainOf(string host, string domain) =>
        string.Equals(host, domain, StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);

    private void HandleHealthRequest(HttpListenerContext context)
    {
        try
        {
            var count = ActiveWebSocketCount;
            var json = JsonSerializer.Serialize(new
            {
                connected = count > 0,
                clientCount = count
            }, JsonOpts);
            var payload = Encoding.UTF8.GetBytes(json);
            context.Response.StatusCode = 200;
            context.Response.ContentType = "application/json; charset=utf-8";
            context.Response.ContentLength64 = payload.Length;
            // `Access-Control-Allow-Origin: *` bilinçli olarak YOK. Tek tüketici
            // sihirbazın kendi HttpClient'ı; CORS onu hiç ilgilendirmiyor.
            // Wildcard varken rastgele bir web sayfası yanıtı okuyup yayıncının
            // OrderDeck çalıştırdığını parmak izleyebiliyordu.
            context.Response.OutputStream.Write(payload, 0, payload.Length);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Health endpoint write failed");
        }
        finally
        {
            try { context.Response.Close(); } catch { /* ignore */ }
        }
    }

    private async Task Handle(WebSocket ws, CancellationToken ct)
    {
        Interlocked.Increment(ref _activeWebSocketCount);
        try
        {
            await HandleCore(ws, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWebSocketCount);
        }
    }

    private async Task HandleCore(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[8192];
        var ms = new System.IO.MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            try
            {
                do
                {
                    res = await ws.ReceiveAsync(buf, ct);
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Extension WS receive failed");
                break;
            }

            if (res.MessageType == WebSocketMessageType.Close)
            {
                if (ws.State == WebSocketState.CloseReceived)
                    await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", ct);
                break;
            }

            try
            {
                var json = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
                var msg = JsonSerializer.Deserialize<ExtensionMessage>(json, JsonOpts);

                if (msg is { Type: "chat", Platform: not null, Username: not null, Text: not null })
                {
                    // Instagram resmi API'deyken extension kopyası düşer.
                    // Trial modda bastırma YOK: InstagramChatHostedService trial'da
                    // hiç çalışmıyor, burada da düşürürsek IG tamamen kararır.
                    if (string.Equals(msg.Platform, "instagram", StringComparison.OrdinalIgnoreCase) &&
                        _isInstagramOfficial?.Invoke() == true &&
                        _trialProbe?.IsTrialMode != true)
                    {
                        _log.LogDebug(
                            "Instagram OfficialApi mode: dropping extension message from {Username}",
                            msg.Username);
                        continue;
                    }

                    if (_trialProbe?.IsTrialMode == true &&
                        !string.Equals(msg.Platform, "instagram", StringComparison.OrdinalIgnoreCase))
                    {
                        _log.LogDebug(
                            "Trial mode: dropping non-Instagram message from platform '{Platform}' by {Username}",
                            msg.Platform, msg.Username);
                        continue;
                    }

                    // Spam filter — runs AFTER trial mode + payload-shape checks
                    // because the cheaper rules (length, links) reject lots of
                    // messages and we don't want to bother evaluating them when
                    // the message is already going to be dropped for other reasons.
                    if (_spamFilter is not null)
                    {
                        var nowSec = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                        var dropReason = _spamFilter.ShouldDrop(msg.Text, msg.Username, nowSec);
                        if (dropReason is not null)
                        {
                            _log.LogDebug(
                                "Spam filter dropped message ({Reason}) from {Platform}:{Username}: {Text}",
                                dropReason, msg.Platform, msg.Username, msg.Text);
                            continue;
                        }
                    }

                    // Belt-and-suspenders dedupe by externalId (extension-generated
                    // per-comment-node). True dupes (reconnect / multiple tabs / dev
                    // reload) have the same externalId. Customer re-buying the same
                    // code generates a new DOM node → new externalId → not a dupe.
                    if (!string.IsNullOrEmpty(msg.ExternalId) && !TryRegisterSeen(msg.ExternalId))
                    {
                        _log.LogDebug(
                            "Dropping duplicate externalId={ExternalId} {Platform}:{Username}: {Text}",
                            msg.ExternalId, msg.Platform, msg.Username, msg.Text);
                        Interlocked.Increment(ref _dedupedCount);
                        continue;
                    }

                    _bus.Publish(new ChatMessage(
                        Id: Guid.NewGuid().ToString("N"),
                        Platform: msg.Platform,
                        ExternalId: msg.ExternalId,
                        Username: msg.Username!,
                        DisplayName: msg.DisplayName,
                        AvatarUrl: msg.AvatarUrl,
                        Text: msg.Text!,
                        ReceivedAt: msg.Timestamp ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                        Badges: Array.Empty<string>()));
                }
                else if (msg is { Type: "debug-stats", Platform: not null })
                {
                    // Extension'dan gelen scan/dedupe sayaçları — diyagnoz için loglanır.
                    // 10s window: scanCount, commentsObserved, deduped, sent, observerBursts.
                    _log.LogInformation(
                        "Extension stats [{Platform}]: scans={Scans} observed={Observed} deduped={Deduped} sent={Sent} queued={Queued} bursts={Bursts} cache={Cache} (window {WindowMs}ms)",
                        msg.Platform,
                        msg.Stats?.ScanCount ?? 0,
                        msg.Stats?.CommentsObserved ?? 0,
                        msg.Stats?.Deduped ?? 0,
                        msg.Stats?.Sent ?? 0,
                        msg.Stats?.Queued ?? 0,
                        msg.Stats?.ObserverBursts ?? 0,
                        msg.Stats?.DedupeCacheSize ?? 0,
                        msg.Stats?.WindowDurationMs ?? 0);
                }
                else if (msg is { Type: "watchdog", Platform: not null })
                {
                    // Extension'ın stall kurtarma olayı (IG donması — 2026-07-15).
                    // action=nudge: chat scroll dürtüldü; action=reload: sekme
                    // otomatik yenilendi. Warning seviyesi: sahada ne sıklıkta
                    // tetiklendiğini log taramasıyla görmek için.
                    _log.LogWarning(
                        "Extension watchdog [{Platform}]: action={Action} sinceSendMs={SinceSendMs} rows={Rows}",
                        msg.Platform, msg.Action ?? "?", msg.SinceSendMs ?? 0, msg.Rows ?? 0);
                }
                else if (msg is { Type: "chat-dropped", Platform: not null, Count: not null })
                {
                    // Köprü kapalıyken extension yorumları outbox'ta biriktirir;
                    // outbox taştıysa ya da başka yayına geçildiyse bir kısmı
                    // atılır. Kayıp SESSİZ KALMAMALI — mezat modelinde kaybolan
                    // yorum kaybolan sipariş demek. Error seviyesi bilinçli:
                    // operatör "kaç sipariş kaçtı" sorusunu logdan yanıtlayabilsin.
                    _log.LogError(
                        "Extension outbox [{Platform}]: köprü kapalıyken {Count} yorum kaybedildi",
                        msg.Platform, msg.Count.Value);
                }
                else if (msg is { Type: "viewers", Platform: not null, Count: not null })
                {
                    // Canlı izleyici sayısı — content script periyodik gönderir.
                    // Tek toplayıcıya yazılır; WPF üst barda platform başına + toplam okur.
                    _viewers?.Report(msg.Platform, msg.Count.Value);
                    _log.LogDebug("Viewer count [{Platform}]: {Count}", msg.Platform, msg.Count.Value);
                }
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Bad extension payload");
            }
        }
    }

    private static int FindFreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
        if (_listener.IsListening) _listener.Close();
    }
}
