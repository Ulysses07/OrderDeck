using System;
using System.Collections.Generic;
using System.Linq;
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

    // ── Tek etkin kaynak kapısı (R7-08) ──────────────────────────────────────
    //
    // Aynı TikTok yayını iki sekmede açıksa aynı yorum iki AYRI externalId ile
    // gelir: id, sekme-yerel runId + emitSeq'ten türetilir (chat-bridge-core.js)
    // ve sekmeler birbirinden habersizdir. externalId dedupe bu kopyayı
    // yakalayamaz → aynı sipariş iki kez düşer. Uzantı tarafında çözüm yok
    // (TikTok DOM'unda gerçek mesaj id'si yok; körlemesine metin dedupe'u aynı
    // kodu tekrar yazan gerçek müşteriyi de yerdi — bkz. #93).
    //
    // Çözüm: platform başına TEK etkin kaynak. İlk mesajını ulaştıran bağlantı
    // etkin olur; diğer sekmeler bağlı kalır ama mesajları sayılıp atlanır —
    // yani pasif sekme kopukluk anında hazır bekleyen bir YEDEK. Devir iki
    // yoldan olur:
    //   1. Etkin bağlantı kapanır → kaydı anında silinir, sıradaki mesajını
    //      ulaştıran devralır (anlık failover, kayıpsız — pasif sekme zaten
    //      her şeyi gönderiyordu).
    //   2. Etkin bağlantı açık ama SUSKUN (yayın bitmiş, sekme açık unutulmuş
    //      "zombi"): son kabul edilen mesajın üstünden _sourceStaleAfterMs
    //      geçtiyse yeni kaynak devralır. TikTok canlı URL'si her yayında aynı
    //      (@kullanici/live) olduğu için URL ile ayırt etmek mümkün değil —
    //      tek güvenilir sinyal sessizlik.
    //
    // Sakin yayında flip-flop olmaz: aynı yayının sekmeleri AYNI olayları
    // gönderdiği için etkin sekmenin her kabulü damgayı tazeler; kopya, damga
    // taze olduğundan düşer.
    private readonly int _sourceStaleAfterMs;
    private readonly object _sourceLock = new();
    private readonly Dictionary<string, (object Conn, long LastAcceptedAt)> _activeSources =
        new(StringComparer.OrdinalIgnoreCase);
    // R7-08 devir görünürlüğü: platformun SON sahibi (kapanışta silinmez).
    // Yeni bağlantının devralması logda görünsün diye tutulur; platform
    // sayısı kadar büyür, sınırsız değil.
    private readonly Dictionary<string, object> _lastOwnerByPlatform =
        new(StringComparer.OrdinalIgnoreCase);
    private long _passiveSourceDroppedCount;
    public long PassiveSourceDroppedCount => Volatile.Read(ref _passiveSourceDroppedCount);

    // ── Devir penceresi kopya süzgeci (R7-08 / R9-AC-047-048) ────────────────
    //
    // Devir anının açığı: A aynı yorumu ulaştırıp kapanır (veya bayatlar),
    // B'nin GECİKMİŞ kopyası devirden sonra gelir. externalId sekme-yerel
    // olduğu için _seen bunu yakalayamaz → aynı sipariş ikinci kez düşerdi
    // (denetim ölçümü: sent=2, received=2). Çözüm körlemesine metin dedupe'u
    // DEĞİL (#93: aynı kodu yeniden yazan gerçek müşteriyi yer): yalnız
    // BAŞKA bağlantının kısa pencere içinde ulaştırdığı birebir (kullanıcı+
    // metin) süzülür. Aynı bağlantıdan gelen tekrar (gerçek re-buy) hiç
    // etkilenmez; pencere dışına düşen eski kopya ise bilinçli tasarım sınırı
    // olarak kalır — pencereyi büyütmek re-buy'ı yeme riskini büyütür.
    private readonly int _handoverDedupeWindowMs;
    private const int RecentAcceptLimit = 256;
    private readonly object _recentLock = new();
    private readonly Queue<(string Key, object Conn, long At)> _recentAccepts = new();
    private long _handoverDedupedCount;
    public long HandoverDedupedCount => Volatile.Read(ref _handoverDedupedCount);

    private static string RecentKey(string platform, string username, string text) =>
        string.Concat(platform, "\n", username, "\n", text);

    /// <summary>Yayınlanan her mesajın (platform,kullanıcı,metin,bağlantı)
    /// kaydı — devir penceresi süzgecinin karşılaştırma tabanı.</summary>
    private void RecordAccept(string platform, string username, string text, object connId)
    {
        var entry = (RecentKey(platform, username, text), connId, Environment.TickCount64);
        lock (_recentLock)
        {
            _recentAccepts.Enqueue(entry);
            while (_recentAccepts.Count > RecentAcceptLimit)
                _recentAccepts.Dequeue();
        }
    }

    /// <summary>BAŞKA bir bağlantının pencere içinde ulaştırdığı birebir aynı
    /// (platform,kullanıcı,metin) mi? Yalnız devirden hemen sonra tetiklenebilir:
    /// pasif kaynağın mesajları kaynak kapısında zaten düşer, etkin kaynağın
    /// kendi kayıtları ise aynı bağlantı olduğu için eşleşmez.</summary>
    private bool IsHandoverDuplicate(string platform, string username, string text, object connId)
    {
        var key = RecentKey(platform, username, text);
        var now = Environment.TickCount64;
        lock (_recentLock)
        {
            foreach (var e in _recentAccepts)
            {
                if (!ReferenceEquals(e.Conn, connId) &&
                    now - e.At <= _handoverDedupeWindowMs &&
                    string.Equals(e.Key, key, StringComparison.Ordinal))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Etkin kaynağın kabulü/devri. Kabulde damga tazelenir.
    /// <paramref name="tookOverClosed"/>: önceki sahip kapanmış, bu bağlantı
    /// devraldı (anlık failover) — logda görünür olsun diye ayrıştırılır.</summary>
    private bool TryAcceptSource(string platform, object connId,
        out bool tookOverStale, out bool tookOverClosed)
    {
        tookOverStale = false;
        tookOverClosed = false;
        var now = Environment.TickCount64;
        lock (_sourceLock)
        {
            if (!_activeSources.TryGetValue(platform, out var active))
            {
                tookOverClosed = _lastOwnerByPlatform.TryGetValue(platform, out var prev) &&
                                 !ReferenceEquals(prev, connId);
                _activeSources[platform] = (connId, now);
                _lastOwnerByPlatform[platform] = connId;
                return true;
            }
            if (ReferenceEquals(active.Conn, connId))
            {
                _activeSources[platform] = (connId, now);
                return true;
            }
            if (now - active.LastAcceptedAt > _sourceStaleAfterMs)
            {
                _activeSources[platform] = (connId, now);
                _lastOwnerByPlatform[platform] = connId;
                tookOverStale = true;
                return true;
            }
            return false;
        }
    }

    /// <summary>Bağlantı kapanınca etkin olduğu tüm platformları bırakır —
    /// pasif sekme bir sonraki mesajında beklemeden devralabilsin.</summary>
    private void ReleaseSources(object connId)
    {
        lock (_sourceLock)
        {
            var released = _activeSources
                .Where(kv => ReferenceEquals(kv.Value.Conn, connId))
                .Select(kv => kv.Key).ToList();
            foreach (var platform in released)
                _activeSources.Remove(platform);
        }
    }

    /// <summary>Uzantının bağlandığı tek yol; başka yola gelen yükseltme reddedilir.</summary>
    private const string WebSocketPath = "/extension";

    /// <summary>
    /// Tek bir WebSocket mesajının azami boyutu. Sınır yokken tek bir istemci
    /// <c>EndOfMessage</c>'ı hiç göndermeden çerçeve akıtarak MemoryStream'i
    /// büyütüp uygulamanın belleğini tüketebiliyordu — köprü localhost'ta
    /// dinlediği için bunu yayıncının açtığı herhangi bir sayfa (kaynak
    /// denetiminden geçen bir tiktok.com sekmesi ya da yerel bir süreç)
    /// yapabilirdi. Gerçek trafik çok altında: en büyük yük, avatar URL'si
    /// içeren bir "chat" satırı, ~1 KB.
    /// </summary>
    private const int MaxMessageBytes = 64 * 1024;

    /// <summary>Kabul döngüsü üst üste bu kadar hata alırsa pes eder.</summary>
    private const int MaxConsecutiveAcceptFailures = 20;

    private const int AcceptRetryDelayMs = 250;

    public int Port { get; private set; }

    public ExtensionBridgeServer(IChatBus bus, int port = 4748,
        ILogger<ExtensionBridgeServer>? log = null,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        ViewerCountTracker? viewers = null,
        int sourceStaleAfterMs = 60_000,
        int handoverDedupeWindowMs = 10_000)
    {
        _bus = bus;
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _viewers = viewers;
        // R7-08 bayatlama eşiği. 60 sn: gerçek bir canlı satış yayınında
        // 60 sn boyunca TEK yorum bile gelmemesi fiilen "yayın bitti" demek;
        // daha kısası, seyrek sohbetli sakin bir yayında gereksiz devir
        // yapabilirdi. Testler kısa eşikle bayatlama yolunu sınar.
        _sourceStaleAfterMs = sourceStaleAfterMs;
        // Devir penceresi. 10 sn: gecikmiş kopyanın gerçekçi gecikmesi
        // saniyeler mertebesinde (WS teslim + sekme zamanlayıcı kayması);
        // daha uzunu aynı kodu bilinçli tekrar yazan müşteriyi yeme
        // riskini büyütür (#93).
        _handoverDedupeWindowMs = handoverDedupeWindowMs;
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
        var consecutiveFailures = 0;

        while (!ct.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
                consecutiveFailures = 0;
            }
            catch (Exception) when (!_listener.IsListening || ct.IsCancellationRequested)
            {
                // Beklenen kapanış: StopAsync listener'ı durdurdu.
                return;
            }
            catch (Exception ex)
            {
                // Tek bir el sıkışmanın düşmesi (istemci yarıda kesti, geçici
                // soket hatası) TÜM köprüyü öldürmemeli. Eskiden buradaki
                // `catch { return; }` tam bunu yapıyordu: sohbet sessizce
                // duruyor, uygulama çalışmaya devam ediyor, operatör yorumların
                // neden gelmediğini anlayamıyordu — yeniden başlatmaktan başka
                // çare yoktu ve logda da tek satır yoktu.
                consecutiveFailures++;
                if (consecutiveFailures >= MaxConsecutiveAcceptFailures)
                {
                    _log.LogError(ex,
                        "Köprü dinleyicisi üst üste {Count} kez başarısız oldu; kabul döngüsü durduruluyor",
                        consecutiveFailures);
                    return;
                }

                _log.LogWarning(ex,
                    "Köprü el sıkışması alınamadı ({Count}/{Max}); dinlemeye devam ediliyor",
                    consecutiveFailures, MaxConsecutiveAcceptFailures);

                // Kalıcı bir arızada döngünün CPU yakmasını engeller.
                try { await Task.Delay(AcceptRetryDelayMs, ct); }
                catch (OperationCanceledException) { return; }
                continue;
            }

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

            // Yol denetimi. Eskiden HERHANGİ bir yola gelen yükseltme isteği
            // kabul ediliyordu; uzantı her zaman /extension'a bağlanır, o
            // yüzden başka bir yola gelen istek ya eski/bozuk bir istemci ya
            // da köprüyü tarayan bir şey. Kaynak denetiminden ÖNCE, çünkü
            // daha ucuz ve reddi daha kesin.
            var path = context.Request.Url?.AbsolutePath;
            if (!string.Equals(path, WebSocketPath, StringComparison.Ordinal))
            {
                _log.LogWarning(
                    "Köprüye beklenmeyen yoldan WebSocket bağlantısı reddedildi: {Path}", path);
                context.Response.StatusCode = 404;
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

            HttpListenerWebSocketContext wsContext;
            try
            {
                wsContext = await context.AcceptWebSocketAsync(subProtocol: null);
            }
            catch (Exception ex)
            {
                // Yükseltme el sıkışması yarıda kalabilir (istemci vazgeçti,
                // bozuk başlık). Bu istisna korumasızken döngünün DIŞINA
                // taşıyor ve kabul görevini öldürüyordu — kaynak/yol
                // denetimlerini geçmiş sıradan bir kopukluk köprüyü topyekûn
                // susturabilirdi.
                _log.LogWarning(ex, "WebSocket yükseltmesi tamamlanamadı");
                try { context.Response.Abort(); } catch { /* ignore */ }
                continue;
            }

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
    /// şema kısıtı da oradaki <c>*://</c> ile hizalı. Facebook ve Instagram
    /// uzantıdan kaldırıldığı (resmi Graph API devraldı) için listede tek alan
    /// adı kaldı: TikTok'un resmi bir canlı sohbet API'si yok, köprü artık
    /// yalnızca onun için var.</para>
    ///
    /// <para><b>Neyi kapatmaz.</b> Yerel bir süreç <c>Origin</c>'i serbestçe
    /// uydurabilir; tiktok.com üstündeki bir XSS de bu kapıdan geçer. Bunları
    /// ancak paylaşılan bir sır ya da native messaging kapatır — köprünün
    /// emekliye ayrılma planı orada.</para>
    /// </summary>
    private static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)) return false;

        // Chrome ve Edge'in ikisi de uzantı kaynağı için chrome-extension:// kullanır.
        if (origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase))
            return true;

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return false;

        return IsHostOrSubdomainOf(uri.Host, "tiktok.com");
    }

    /// <summary>Nokta sınırlı alt alan eşleşmesi — "eviltiktok.com" geçmemeli.</summary>
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
        // R7-08: bağlantının kaynak-kapısı kimliği. Referans eşitliğiyle
        // karşılaştırılan yalın bir nesne — soketin kendisini sözlükte
        // tutmamak için ayrı (yaşam süresi bu metotla sınırlı).
        var connId = new object();
        Interlocked.Increment(ref _activeWebSocketCount);
        try
        {
            await HandleCore(ws, connId, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeWebSocketCount);
            // Kopan bağlantı etkin kaynaksa anında bırakır — pasif sekme bir
            // sonraki mesajında bayatlama beklemeden devralır.
            ReleaseSources(connId);
        }
    }

    private async Task HandleCore(WebSocket ws, object connId, CancellationToken ct)
    {
        // R7-08: pasif kaynak uyarısı bağlantı+platform başına BİR kez —
        // ikinci sekme her yorumda log basarsa yoğun yayında log boğulur.
        var warnedPassivePlatforms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var buf = new byte[8192];
        var ms = new System.IO.MemoryStream();
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            ms.SetLength(0);
            WebSocketReceiveResult res;
            var tooLarge = false;
            try
            {
                do
                {
                    res = await ws.ReceiveAsync(buf, ct);
                    if (ms.Length + res.Count > MaxMessageBytes)
                    {
                        tooLarge = true;
                        break;
                    }
                    ms.Write(buf, 0, res.Count);
                } while (!res.EndOfMessage);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Extension WS receive failed");
                break;
            }

            if (tooLarge)
            {
                // Bağlantıyı KAPATIYORUZ, mesajı atlayıp devam etmiyoruz:
                // taşan mesajın kalan çerçeveleri okunmadığı için sonraki
                // ReceiveAsync onları yeni bir mesajın başı sanar ve akış
                // kalıcı olarak bozulur.
                _log.LogWarning(
                    "Extension WS mesajı {Limit} bayt sınırını aştı; bağlantı kapatılıyor",
                    MaxMessageBytes);
                try
                {
                    await ws.CloseAsync(
                        WebSocketCloseStatus.MessageTooBig, "message too large", ct);
                }
                catch (Exception ex) { _log.LogDebug(ex, "Oversize close failed"); }
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
                    // Deneme sürümünde köprü sessiz. Eskiden "instagram" hariç
                    // tutuluyordu; IG uzantıdan kaldırıldığı (resmi Graph API
                    // devraldı) ve el sıkışma artık yalnız tiktok.com'u kabul
                    // ettiği için o istisnanın karşılığı kalmadı. Deneme
                    // kullanıcısı IG/FB/YouTube'u resmi API'lerden almaya
                    // devam ediyor — kısıtlanan tek yol köprü.
                    if (_trialProbe?.IsTrialMode == true)
                    {
                        _log.LogDebug(
                            "Trial mode: dropping message from platform '{Platform}' by {Username}",
                            msg.Platform, msg.Username);
                        continue;
                    }

                    // R7-08: tek etkin kaynak kapısı. Spam filtresi ve
                    // externalId dedupe'undan ÖNCE, çünkü pasif sekmenin seli
                    // ne spam pencerelerini kirletmeli ne de 20k'lık _seen
                    // FIFO'sunu doldurup gerçek dedupe kayıtlarını itmeli.
                    if (!TryAcceptSource(msg.Platform, connId,
                            out var tookOverStale, out var tookOverClosed))
                    {
                        Interlocked.Increment(ref _passiveSourceDroppedCount);
                        if (warnedPassivePlatforms.Add(msg.Platform))
                            _log.LogWarning(
                                "R7-08: {Platform} için ikinci bir kaynak (sekme) mesaj gönderiyor; " +
                                "etkin kaynak başka bağlantıda — pasif kopyalar atlanacak",
                                msg.Platform);
                        else
                            _log.LogDebug(
                                "Pasif kaynak mesajı atlandı {Platform}:{Username}: {Text}",
                                msg.Platform, msg.Username, msg.Text);
                        continue;
                    }
                    if (tookOverStale)
                        _log.LogWarning(
                            "R7-08: {Platform} etkin kaynağı {StaleMs}ms'dir suskundu; " +
                            "yeni kaynak devraldı (eski sekme muhtemelen bitmiş yayında açık kalmış)",
                            msg.Platform, _sourceStaleAfterMs);
                    else if (tookOverClosed)
                        _log.LogInformation(
                            "R7-08: {Platform} etkin kaynağı kapanmıştı; yeni bağlantı devraldı — " +
                            "{WindowMs}ms devir penceresinde kopyalar süzülecek",
                            msg.Platform, _handoverDedupeWindowMs);

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

                    // R7-08 devir penceresi: BAŞKA bağlantının az önce
                    // ulaştırdığı birebir aynı (kullanıcı+metin) — devirden
                    // hemen sonra gelen gecikmiş kopya (AC-047/048).
                    if (IsHandoverDuplicate(msg.Platform, msg.Username!, msg.Text!, connId))
                    {
                        Interlocked.Increment(ref _handoverDedupedCount);
                        _log.LogWarning(
                            "R7-08: devir sonrası gecikmiş kopya süzüldü {Platform}:{Username}: {Text}",
                            msg.Platform, msg.Username, msg.Text);
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
                    RecordAccept(msg.Platform, msg.Username!, msg.Text!, connId);
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
