using System.Net.Http;
using OrderDeck.Chat.YouTube;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.Chat.Ingestors.YouTube;

/// <summary>
/// YouTube canlı sohbetinin TEK yolu: resmi gRPC <c>liveChatMessages.streamList</c>
/// ingestor'ının lifecycle'ı. (2026-08-01'de kota 760k/gün onaylanınca ToS'a aykırı
/// InnerTube scraper'ı ve /live sayfasını kazıyan resolver kaldırıldı.)
///
/// <para><b>Dış döngü turlar hâlinde koşar.</b> Her tur: kapılar (deneme sürümü, kanal yazılı mı,
/// yayın oturumu açık mı, API anahtarı) → yayını bul → okuyucuyu başlat → okuyucunun çıkmasını bekle
/// → çıkış sebebine göre bekle. Akış içi yeniden bağlanmayı okuyucu kendi yönetir
/// (streamList page_token).</para>
///
/// <para><b>Tur kesilebilir.</b> Yayın oturumunun başlaması, bitmesi ve "Sohbeti yenile" isteği o an
/// süren turu keser (bekleme de, süren yayın araması da, çalışan okuyucu da kesilir) ve döngü
/// beklemeden baştan değerlendirir. Eskiden döngü oturumu 60 sn uyuyarak yokluyordu: "Yayın
/// Başlat"tan sonra sohbet bir dakikaya kadar gecikiyor, platformda düşüp yeniden açılan yayına
/// ancak uygulamadaki oturum bitirilip açılarak dönülebiliyordu.</para>
///
/// <para><b>Kesme anında geçerlidir.</b> Olay dinleyicisi kesmeyi kilit altında verir ve turun
/// belirteci o an iptal edilmiş görünür (<see cref="CancellationTokenSource.CancelAsync"/>; geri
/// çağrılar havuzda koşar, çağıran beklemez). Tur her adımını (işe başlama, durum yazma, beklemeye
/// geçme) aynı kilit altında belirtece bakarak atar: kesilmiş tur bundan sonra yayın aramaz, okuyucu
/// kurmaz, durum yazmaz. Kesme havuzda gecikmeli işlenseydi kesilen tur önce bağlanır, sonra bir kez
/// daha bağlanılırdı (fazladan arama ve akış bağlantısı; şeritte de bir an eski durum görünürdü).
/// Tek istisna: oturum başlangıcı, o oturumu zaten görüp onun için çalışan turu kesmez
/// (<see cref="OnSessionStarted"/>).</para>
///
/// <para><b>Yayının bulunması.</b> Ayardaki metin doğrudan video bağlantısı/kimliği ise
/// <see cref="YouTubeVideoIdExtractor"/> (yetki gerekmez, arama yapılmaz); kanal adıysa
/// <c>broadcastLookup</c> → <c>liveBroadcasts.list?broadcastStatus=active</c> (hesap bağlı olmalı;
/// tek çağrıda hem video hem sohbet kimliği döner). Arama etkileşimli yetkilendirme başlatmaz:
/// bkz. <c>YouTubeModerationService.LookupActiveBroadcastAsync</c>.</para>
///
/// <para><b>Durum.</b> Servis durumunu <see cref="ChatSourceMonitor"/>'a kendisi yazar (sohbet
/// şeridindeki YouTube satırı). Her durum DEĞİŞİMİ günlüğe bir satır düşer; sahadaki "sohbet neden
/// düştü" sorusu o satırlarla cevaplanır.</para>
/// </summary>
public sealed class YouTubeOfficialChatHostedService : IHostedService, IDisposable
{
    /// <summary>Named HttpClient: gRPC dışı REST çağrıları (videos.list —
    /// liveChatId çözümleme + izleyici sayısı).</summary>
    public const string HttpClientName = "youtube-official";

    private const string Platform = "youtube";

    /// <summary>Geri çekilmenin tabanı (<see cref="ComputeBackoff"/>).</summary>
    private static readonly TimeSpan IdleAfterExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    private readonly Func<AppSettings> _settingsProvider;
    private readonly IChatBus _bus;
    private readonly ILogger<YouTubeOfficialChatHostedService> _log;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ViewerCountTracker? _viewers;
    // Delegate, tip değil: gerçek uygulama YouTubeModerationService (Windows-only,
    // [SupportedOSPlatform]) — doğrudan bağımlılık o kısıtı bu platform-agnostik
    // servise yayardı. İlk parametre "önbelleği atla".
    private readonly Func<bool, CancellationToken, Task<BroadcastLookup>>? _broadcastLookup;
    private readonly ChatReconnectSignal? _reconnect;
    private readonly ChatSourceMonitor? _sources;
    private readonly TimeProvider _time;
    private readonly YouTubeChatTimings _timings;
    private readonly Func<YouTubeIngestorRequest, IYouTubeChatIngestor> _ingestorFactory;

    /// <summary>Gösterilmiş mesaj belleği: servis ömrünce tek örnek, kurulan her okuyucuya verilir.</summary>
    private readonly YouTubeSeenMessages _seen = new();

    private CancellationTokenSource? _cts;
    private Task? _runner;
    private bool _warnedNoKey;

    // Aşağıdakiler hem döngüden hem olay dinleyicilerinden (arayüz iş parçacığı) okunur/yazılır.
    private readonly Lock _gate = new();
    private CancellationTokenSource? _turn;

    /// <summary>
    /// Süren tur bu yayın oturumu için ÇALIŞIYOR (yayını arıyor ya da okuyor). Tur kapılardayken ve
    /// beklerken null: o hâllerde gelen her olay turu keser.
    /// </summary>
    private string? _turnSession;
    private DateTimeOffset _fastUntil;
    private bool _freshNext;
    private int _consecutiveCrashes;
    private bool _reading;
    private ChatSourceState? _reported;

    public YouTubeOfficialChatHostedService(
        Func<AppSettings> settingsProvider,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null,
        ViewerCountTracker? viewers = null,
        Func<bool, CancellationToken, Task<BroadcastLookup>>? broadcastLookup = null,
        ChatReconnectSignal? reconnect = null,
        ChatSourceMonitor? sources = null,
        TimeProvider? time = null)
        : this(settingsProvider, bus, loggerFactory, trialProbe, spamFilter, sessions, httpFactory, viewers,
            broadcastLookup, reconnect, sources, time, YouTubeChatTimings.Default, ingestorFactory: null)
    {
    }

    /// <summary>Test kurucusu: süreler ve okuyucu fabrikası dışarıdan verilir.</summary>
    internal YouTubeOfficialChatHostedService(
        Func<AppSettings> settingsProvider,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe,
        SpamFilter? spamFilter,
        StreamSessionService? sessions,
        IHttpClientFactory? httpFactory,
        ViewerCountTracker? viewers,
        Func<bool, CancellationToken, Task<BroadcastLookup>>? broadcastLookup,
        ChatReconnectSignal? reconnect,
        ChatSourceMonitor? sources,
        TimeProvider? time,
        YouTubeChatTimings timings,
        Func<YouTubeIngestorRequest, IYouTubeChatIngestor>? ingestorFactory)
    {
        _settingsProvider = settingsProvider;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<YouTubeOfficialChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;
        _viewers = viewers;
        _broadcastLookup = broadcastLookup;
        _reconnect = reconnect;
        _sources = sources;
        _time = time ?? TimeProvider.System;
        _timings = timings;
        _ingestorFactory = ingestorFactory ?? CreateIngestor;

        if (_sessions is not null)
        {
            _sessions.SessionStarted += OnSessionStarted;
            _sessions.SessionEnded += OnSessionEnded;
        }

        if (_reconnect is not null)
            _reconnect.Requested += OnReconnectRequested;
    }

    // ── Olay dinleyicileri: çağıranı (arayüz) BEKLETMEZ ─────────────────────────────────────────

    private void OnSessionStarted(object? sender, SessionStartedEventArgs e)
    {
        lock (_gate)
        {
            // "Yayın Başlat": operatör çoğu zaman önce uygulamada oturumu açar, sonra platformda yayını
            // başlatır. Tek uyandırma yetmez; pencere boyunca yayın sık aranır.
            _fastUntil = _time.GetUtcNow() + _timings.FastResolveWindow;

            // Oturum satırı bu olaydan ÖNCE yazılır: o arada başlayan tur oturumu görüp olayı beklemeden
            // bağlanmış olabilir. Bu oturum için çalışan tur kesilmez; kesmek az önce kurulan okuyucuyu
            // durdurup bir kez daha kurdururdu. Çalışması bitip beklemeye geçerken pencereye aynı kilit
            // altında bakar (RestAsync), yani pencere açılışını kaçırmaz.
            if (_turnSession != e.SessionId) InterruptTurnLocked();
        }
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        lock (_gate) InterruptTurnLocked();
    }

    private void OnReconnectRequested(object? sender, EventArgs e)
    {
        if (!IsRunnable(_settingsProvider(), out _))
        {
            _log.LogInformation(
                "[ChatReconnect] youtube: kaynak çalışmıyor (oturum yok ya da YouTube kurulu değil); yapılacak iş yok");
            return;
        }

        bool wasReading;
        lock (_gate)
        {
            _fastUntil = _time.GetUtcNow() + _timings.FastResolveWindow;
            _freshNext = true;        // yayın az önce yeniden açılmış olabilir: önbelleği atla
            _consecutiveCrashes = 0;  // operatör "şimdi dene" dedi: geri çekilme baştan başlar
            wasReading = _reading;

            // Kesme ile "bağlanıyor" aynı kilit altında: kesilen tur bundan sonra durum yazamaz, yerine
            // gelen tur ise sonucunu "bağlanıyor"un ardından yazar.
            InterruptTurnLocked();
            ReportLocked(ChatSourceState.Connecting);
        }

        if (wasReading)
            _log.LogInformation("[ChatReconnect] youtube: okuyucu kesildi, yayın yeniden aranıyor");
        else
            _log.LogInformation("[ChatReconnect] youtube: bekleme ya da arama kesildi, yayın yeniden aranıyor");
    }

    /// <summary>
    /// Süren turu keser; döngü beklemeden baştan değerlendirir. <see cref="_gate"/> altında çağrılır.
    /// Belirteç bu çağrıyla birlikte iptal edilmiş görünür; iptalin geri çağrıları (ve onlara bağlı
    /// devam kodu) havuzda koşar, çağıranın (arayüz) iş parçacığında değil.
    /// </summary>
    private void InterruptTurnLocked()
    {
        // Tur yoksa (iki tur arası) yapılacak iş yok: sıradaki tur zaten baştan değerlendirir. Kayıtlı
        // tur henüz bırakılmamıştır: döngü kaydı bu kilit altında siler, belirteci ondan sonra bırakır.
        _ = _turn?.CancelAsync().ContinueWith(
            static (cancel, log) => ((ILogger)log!).LogDebug(
                cancel.Exception, "[YouTube Official] tur iptali sırasında hata"),
            _log,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    // ── Yaşam döngüsü ───────────────────────────────────────────────────────────────────────────

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ct = _cts.Token;
        _runner = Task.Run(() => RunAsync(ct), ct);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        try { _cts?.Cancel(); } catch { /* ignore */ }
        if (_runner is not null)
        {
            try { await _runner.WaitAsync(cancellationToken); }
            catch (OperationCanceledException) { /* expected */ }
            catch (Exception ex) { _log.LogDebug(ex, "[YouTube Official] stop wait swallowed"); }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var turn = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_gate) _turn = turn;
            try
            {
                await RunTurnAsync(turn.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (OperationCanceledException) when (turn.IsCancellationRequested)
            {
                // Tur kesildi (oturum başladı ya da bitti, yenileme istendi): beklemeden baştan.
            }
            catch (Exception ex)
            {
                // Beklenmeyen hata. Tur kesilmediği hâlde gelen iptal istisnası da (ör. iç zaman aşımı)
                // buraya düşer: "tur kesildi" sayılıp beklemeden yeniden denenseydi döngü saniyede
                // binlerce arama yapar, günlük kotayı dakikalar içinde bitirirdi.
                _log.LogError(ex, "[YouTube Official] outer loop error; sleeping before retry");
                lock (_gate)
                {
                    _turnSession = null; // tur artık çalışmıyor, bekliyor: her olay bu beklemeyi keser

                    // Şeritte satırı olan kaynak "bağlanıyor"da asılı kalmasın: bu da bir "yeniden
                    // deneniyor"dur. Kesilmiş tur yazmaz (kesen olay kendi durumunu yazdı).
                    if (!turn.IsCancellationRequested && _reported is not (null or ChatSourceState.Stopped))
                    {
                        ReportLocked(
                            ChatSourceState.Retrying, retryAt: _time.GetUtcNow() + _timings.IdleAfterExit);
                    }
                }

                try { await IdleAsync(_timings.IdleAfterExit, turn.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) { /* döngü koşulu karar verir */ }
            }
            finally
            {
                // Önce kayıt silinir (kilit altında), sonra belirteç bırakılır: dinleyiciler kayıtlı
                // turu keserken bırakılmış bir belirteçle karşılaşmaz.
                lock (_gate)
                {
                    _turn = null;
                    _turnSession = null;
                }

                turn.Dispose();
            }
        }

        lock (_gate) ReportLocked(ChatSourceState.Stopped);
    }

    /// <summary>Bir tur: kapılar → yayını bul → okuyucu → çıkışa göre bekle. İptalle kesilebilir.</summary>
    private async Task RunTurnAsync(CancellationToken turn)
    {
        var settings = _settingsProvider();

        // Kapılar: kaynak çalışmıyor, şeritte satırı yok.
        if (!IsRunnable(settings, out var sessionId))
        {
            await RestAsync(turn, ChatSourceState.Stopped, _timings.IdleWhenOffline).ConfigureAwait(false);
            return;
        }

        // Çözümleme sırası: AppSettings override (QA/ayrı proje) →
        // binary'ye gömülü YouTubeApiDefaults.ApiKey (normal kurulum).
        var apiKey = !string.IsNullOrWhiteSpace(settings.YouTubeApiKey)
            ? settings.YouTubeApiKey
            : YouTubeApiDefaults.ApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            if (!_warnedNoKey)
            {
                _log.LogWarning("[YouTube Official] YouTube API key yok (ne ayarda ne gömülü default'ta) — sohbet çekilemez. Release'e key gömülmeli ya da ayarlardan girilmeli.");
                _warnedNoKey = true;
            }

            await RestAsync(
                turn, ChatSourceState.NeedsAttention, _timings.IdleWhenOffline, YouTubeStatusMessages.NoApiKey)
                .ConfigureAwait(false);
            return;
        }

        _warnedNoKey = false;

        // Kapılar geçildi: tur bu oturum için çalışmaya başlıyor.
        var fresh = BeginWork(turn, sessionId);

        // Ayarda tam video bağlantısı/kimliği varsa arama yapılmaz, yetki de gerekmez.
        var videoId = YouTubeVideoIdExtractor.TryExtract(settings.YouTubeChannelHandle);
        string? liveChatId = null;
        if (videoId is null)
        {
            if (_broadcastLookup is null)
            {
                await RestAsync(
                    turn, ChatSourceState.NeedsAttention, _timings.IdleWhenOffline, YouTubeStatusMessages.NotConnected)
                    .ConfigureAwait(false);
                return;
            }

            var lookup = await _broadcastLookup(fresh, turn).ConfigureAwait(false);
            switch (lookup.Status)
            {
                case BroadcastLookupStatus.Found when !string.IsNullOrEmpty(lookup.VideoId):
                    videoId = lookup.VideoId;
                    liveChatId = lookup.LiveChatId;
                    break;

                case BroadcastLookupStatus.NotConnected:
                case BroadcastLookupStatus.NeedsAttention:
                    // Hesap ya da yetki sorunu: beklemek düzeltmez, hızlı arama da boşuna olurdu.
                    await RestAsync(
                        turn, ChatSourceState.NeedsAttention, _timings.IdleWhenOffline,
                        lookup.Message ?? YouTubeStatusMessages.NotConnected)
                        .ConfigureAwait(false);
                    return;

                case BroadcastLookupStatus.Failed:
                    await RestAsync(turn, ChatSourceState.Retrying, _timings.IdleWhenOffline).ConfigureAwait(false);
                    return;

                default:
                    // Etkin yayın yok. Pencere içindeyse (oturum yeni başladı ya da yenilendi) sık ara.
                    await RestAsync(turn, ChatSourceState.NoBroadcast).ConfigureAwait(false);
                    return;
            }
        }

        // Arama sürerken kesilmiş tur okuyucu kurmaz (akış bağlantısı boşuna açılıp kapanmasın).
        turn.ThrowIfCancellationRequested();

        _log.LogInformation("[YouTube Official] ingestor başlatılıyor, video {VideoId}", videoId);
        using var ingestor = _ingestorFactory(new YouTubeIngestorRequest(videoId, apiKey, liveChatId, _seen));

        var crashed = false;
        var noLiveChat = false;
        try
        {
            lock (_gate) _reading = true;
            await ingestor.StartAsync(turn).ConfigureAwait(false);
            lock (_gate)
            {
                turn.ThrowIfCancellationRequested(); // kesilmiş tur "bağlı" yazamaz
                _consecutiveCrashes = 0;
                ReportLocked(ChatSourceState.Live);
            }

            await ingestor.Completion.WaitAsync(turn).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (turn.IsCancellationRequested)
        {
            throw; // tur kesildi: okuyucu finally'de durdurulur, döngü baştan değerlendirir
        }
        catch (YouTubeNoLiveChatException)
        {
            noLiveChat = true;
        }
        catch (Exception ex)
        {
            crashed = true;
            _log.LogWarning(ex, "[YouTube Official] ingestor crash; yeniden planlanıyor");
        }
        finally
        {
            lock (_gate) _reading = false;
            try { await ingestor.StopAsync(CancellationToken.None).ConfigureAwait(false); } catch { /* ignore */ }
        }

        if (noLiveChat)
        {
            // Ayardaki sabit video canlı değil. Çökme değil: geri çekilme büyütülmez; operatör yeni
            // yayının bağlantısını yazınca (ya da o video canlıya geçince) bağlanılır.
            await RestAsync(
                turn, ChatSourceState.NeedsAttention, detail: YouTubeStatusMessages.FixedVideoNotLive)
                .ConfigureAwait(false);
            return;
        }

        if (crashed || ingestor.EndReason == YouTubeIngestorEnd.Failed)
        {
            int crashes;
            lock (_gate)
            {
                turn.ThrowIfCancellationRequested(); // yenileme sayacı sıfırladıysa kesilen tur artırmaz
                crashes = ++_consecutiveCrashes;
            }

            var backoff = ComputeBackoff(crashes);
            _log.LogInformation(
                "[YouTube Official] okuyucu hatayla çıktı (#{Count}); {Seconds} sn sonra yeniden denenecek",
                crashes, backoff.TotalSeconds);
            await RestAsync(turn, ChatSourceState.Retrying, backoff).ConfigureAwait(false);
            return;
        }

        // Platform yayının bittiğini bildirdi (ya da sohbet kapandı).
        await RestAsync(turn, ChatSourceState.NoBroadcast, _timings.IdleAfterExit).ConfigureAwait(false);
    }

    // ── Turun adımları: her biri tek kilit altında, kesilmiş tur hiçbirini atamaz ───────────────

    /// <summary>
    /// Kapılar geçildi: tur <paramref name="sessionId"/> oturumu için çalışmaya başlıyor. "Önbelleği
    /// atla" bayrağı burada okunup indirilir: yenileme bu adımdan önce geldiyse tur zaten kesilmiştir
    /// (bayrak sıradaki tura kalır), sonra geldiyse bu tur kesilir ve bayrağı sıradaki tur alır.
    /// Kaynak durmuş hâlden çıkıyorsa "bağlanıyor" yazılır; sorun durumundan yapılan rutin yeniden
    /// denemeler satırı çevirmez (her 5 sn'de bir "bağlanıyor" yanıp sönmesin), sonuç değişirse yeni
    /// durum yazılır.
    /// </summary>
    /// <returns>Yayın araması önbelleği atlasın mı.</returns>
    private bool BeginWork(CancellationToken turn, string? sessionId)
    {
        lock (_gate)
        {
            turn.ThrowIfCancellationRequested();
            _turnSession = sessionId;
            var fresh = _freshNext;
            _freshNext = false;
            if (_reported is null or ChatSourceState.Stopped) ReportLocked(ChatSourceState.Connecting);
            return fresh;
        }
    }

    /// <summary>
    /// Turun sonu: durumu yazar ve bekler. <paramref name="delay"/> verilmemişse süre hızlı arama
    /// penceresine göre seçilir. Tur artık "çalışmıyor" sayılır ve süre aynı kilit altında seçilir:
    /// oturum başlangıcı bundan önce geldiyse pencere açıktır, sonra gelirse bu beklemeyi keser.
    /// "Yeniden deneniyor" durumunda deneme saati (şimdi + süre) da yazılır.
    /// </summary>
    private Task RestAsync(
        CancellationToken turn, ChatSourceState state, TimeSpan? delay = null, string? detail = null)
    {
        TimeSpan wait;
        lock (_gate)
        {
            turn.ThrowIfCancellationRequested();
            _turnSession = null;
            var now = _time.GetUtcNow();
            wait = delay ?? (now < _fastUntil ? _timings.FastResolveInterval : _timings.IdleWhenOffline);
            ReportLocked(state, detail, state == ChatSourceState.Retrying ? now + wait : null);
        }

        return IdleAsync(wait, turn);
    }

    // ── Yardımcılar ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Kaynak çalışabilir mi: deneme sürümü değil, kanal/video yazılı, yayın oturumu açık.
    /// </summary>
    /// <param name="settings">Güncel ayarlar.</param>
    /// <param name="sessionId">Açık yayın oturumu; oturum servisi yoksa ya da kaynak çalışamıyorsa null.</param>
    private bool IsRunnable(AppSettings settings, out string? sessionId)
    {
        sessionId = null;
        if (_trialProbe?.IsTrialMode == true || string.IsNullOrWhiteSpace(settings.YouTubeChannelHandle))
            return false;
        if (_sessions is null) return true;

        sessionId = _sessions.GetActive()?.Id;
        return sessionId is not null;
    }

    private Task IdleAsync(TimeSpan delay, CancellationToken turn) => Task.Delay(delay, _time, turn);

    /// <summary>
    /// Durumu izleyiciye yazar; DEĞİŞTİYSE günlüğe de bir satır düşer. <see cref="_gate"/> altında
    /// çağrılır: döngü ile dinleyiciler aynı anda bildirirse izleyicideki son durum ile günlükteki
    /// sıra birbirini tutar. Günlük satırı ÖNCE yazılır: izleyicide görünen her durumun satırı
    /// günlükte hazırdır.
    /// </summary>
    private void ReportLocked(ChatSourceState state, string? detail = null, DateTimeOffset? retryAt = null)
    {
        var changed = _reported != state;
        _reported = state;
        if (changed) _log.LogInformation("[YouTube Official] durum: {State}", state);
        _sources?.Report(Platform, state, detail, retryAt);
    }

    private IYouTubeChatIngestor CreateIngestor(YouTubeIngestorRequest request)
    {
        var http = _httpFactory?.CreateClient(HttpClientName)
                   ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        return new YouTubeOfficialChatIngestor(
            request.VideoId, request.ApiKey, _bus,
            _loggerFactory.CreateLogger<YouTubeOfficialChatIngestor>(),
            http, _spamFilter, _viewers, request.LiveChatId, request.Seen);
    }

    /// <summary>Exponential backoff: 30s × 2^(n-1) capped at 5 min.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterExit.TotalSeconds * Math.Pow(2, exp);
        return seconds >= MaxBackoff.TotalSeconds ? MaxBackoff : TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        if (_sessions is not null)
        {
            _sessions.SessionStarted -= OnSessionStarted;
            _sessions.SessionEnded -= OnSessionEnded;
        }

        if (_reconnect is not null)
            _reconnect.Requested -= OnReconnectRequested;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}

/// <summary>Döngünün bekleme süreleri; testler kısa değerlerle kurar.</summary>
/// <param name="IdleWhenOffline">Kaynak çalışmıyorken ve olağan (pencere dışı) yayın aramaları arasında.</param>
/// <param name="IdleAfterExit">Yayın bittikten sonra yeniden aramadan önce.</param>
/// <param name="FastResolveInterval">Hızlı arama penceresinde iki arama arası.</param>
/// <param name="FastResolveWindow">Oturum başlangıcından ya da yenilemeden sonra hızlı aramanın sürdüğü süre.</param>
internal sealed record YouTubeChatTimings(
    TimeSpan IdleWhenOffline,
    TimeSpan IdleAfterExit,
    TimeSpan FastResolveInterval,
    TimeSpan FastResolveWindow)
{
    /// <summary>
    /// Ürün değerleri. Hızlı arama: 2 dakika boyunca 5 sn'de bir <c>liveBroadcasts.list</c> (1 kota
    /// birimi; pencere başına en çok 24 birim, günlük kota 760 bin).
    /// </summary>
    public static YouTubeChatTimings Default { get; } = new(
        IdleWhenOffline: TimeSpan.FromSeconds(60),
        IdleAfterExit: TimeSpan.FromSeconds(30),
        FastResolveInterval: TimeSpan.FromSeconds(5),
        FastResolveWindow: TimeSpan.FromMinutes(2));
}

/// <summary>Bir okuyucunun kurulması için gerekenler.</summary>
/// <param name="VideoId">Yayının video kimliği.</param>
/// <param name="ApiKey">YouTube Data API anahtarı.</param>
/// <param name="LiveChatId">Biliniyorsa sohbet kimliği (yayın araması verir); yoksa okuyucu çözer.</param>
/// <param name="Seen">Servisin gösterilmiş mesaj belleği.</param>
internal sealed record YouTubeIngestorRequest(string VideoId, string ApiKey, string? LiveChatId, YouTubeSeenMessages Seen);
