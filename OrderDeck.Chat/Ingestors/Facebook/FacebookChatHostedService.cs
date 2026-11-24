using System;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;

namespace OrderDeck.Chat.Ingestors.Facebook;

/// <summary>
/// Owns the lifecycle of the Facebook Live comments ingestor. Mirrors
/// <see cref="OrderDeck.Chat.Ingestors.YouTube.YouTubeOfficialChatHostedService"/>
/// in shape but the inner work is "resolve current live video id → open
/// SSE stream → wait for it to end" rather than "poll continuation tokens".
///
/// <para>Loop (only when an operator has connected a Page):</para>
/// <list type="number">
///   <item>Gate on trial mode + active session (same as YouTube).</item>
///   <item>Ask <see cref="FacebookLiveVideoResolver"/> for the current
///     LIVE_NOW video id. Null → idle 60s (5s inside the fast-resolve
///     window that opens on SessionStarted and after a stream exit).</item>
///   <item>Open <see cref="FacebookLiveCommentsStream"/>; wait for it to
///     finish (broadcast ended, network drop, or operator stopped). A
///     watchdog polls the live list alongside and cuts the poller if the
///     bound video disappears or a new live starts — the ended broadcast's
///     comments edge keeps returning 200, so code:100 alone is not enough.</item>
///   <item>On crash → exponential backoff (30s → 1m → 2m → 4m → 5m cap).
///     Graceful/cancelled end → 5s fast re-resolve; graceful self-exit also
///     blacklists the ended video id for 2min (Meta lists it LIVE a while).</item>
/// </list>
///
/// <para><b>"Sohbeti yenile".</b> Yenileme isteği süren yayın aramasını ve çalışan okuyucuyu
/// keser, bayat yayın kara listesini ve hata sayacını sıfırlar, hızlı arama penceresini açar ve
/// döngüyü uyandırır: kaynak "oturum yeni başlamış gibi" baştan bağlanır. Yayımlanmış yorum
/// belleği korunur (aynı yorum ikinci kez yayımlanmaz).</para>
///
/// <para><b>Kesme anında geçerlidir.</b> Oturum bitişi ve yenileme işi kilit altında keser
/// (<see cref="CutWorkLocked"/>): iş o andan sonra okuyucu kuramaz, durum yazamaz; döngü beklemeden
/// baştan değerlendirir. İptalin geri çağrıları havuzda koşar, çağıran (arayüz) beklemez.</para>
///
/// <para><b>Durum.</b> Servis durumunu <see cref="ChatSourceMonitor"/>'a kendisi yazar (sohbet
/// şeridindeki Facebook satırı); her durum DEĞİŞİMİ günlüğe bir satır düşer.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookChatHostedService : IHostedService, IDisposable
{
    private const string Platform = "facebook";

    /// <summary>Geri çekilmenin tabanı ve tavanı (<see cref="ComputeBackoff"/>).</summary>
    private static readonly TimeSpan IdleAfterStreamExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    public const string ResolverClientName = "facebook-resolver";
    public const string StreamClientName = "facebook-stream";

    private readonly Func<AppSettings> _settingsProvider;
    private readonly FacebookOAuthService _oauth;
    private readonly IChatBus _bus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<FacebookChatHostedService> _log;
    private readonly ITrialModeProbe? _trialProbe;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly ChatReconnectSignal? _reconnect;
    private readonly ChatSourceMonitor? _sources;
    private readonly TimeProvider _time;
    private readonly FacebookChatTimings _timings;

    private CancellationTokenSource? _cts;
    private Task? _runner;

    // SessionStarted ve yenileme bekleme uykularını anında böler (max 1 bilet
    // yeter: amaç "bir sonraki uyku kısalsın", sayaç değil).
    private readonly SemaphoreSlim _wake = new(0, 1);

    // Aşağıdakiler hem döngüden hem olay dinleyicilerinden (arayüz iş parçacığı) okunur/yazılır.
    private readonly Lock _gate = new();

    // Kesilebilir iş (yayın araması + okuyucu): oturum bitişi ve yenileme bunu keser.
    private CancellationTokenSource? _workCts;

    /// <summary>
    /// Kayıtlı iş bir OLAYLA (oturum bitişi, yenileme) kesildi: bundan sonra okuyucu kuramaz, durum
    /// yazamaz (<see cref="ThrowIfWorkCutLocked"/>). Bekçinin kesmesi bunu açmaz: o yolda iş, çıkışını
    /// kendisi bildirir.
    /// </summary>
    private bool _workCut;
    private DateTimeOffset _fastUntil;
    private string? _staleVideoId;
    private DateTimeOffset _staleUntil;
    private int _consecutiveCrashes;
    private bool _pageBound;
    private ChatSourceState? _reported;

    /// <summary>
    /// R11-CHAT01: yayınlanmış yorum hafızası poller'ları AŞAR. Poller'ın
    /// alanıyken her yeniden bağlanma onu sıfırlıyordu — aşağıdaki döngü her
    /// turda yeni bir poller kuruyor ve yeni poller'ın ilk anketi son 100
    /// yorumu tekrar yayımlıyordu. R10-CHAT01 geçici hatada AYNI videoya geri
    /// bağlanmayı getirdiği için bu yol artık çok daha sık geziliyor.
    /// </summary>
    private readonly FacebookSeenComments _seen = new();

    public FacebookChatHostedService(
        Func<AppSettings> settingsProvider,
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe = null,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null,
        ChatReconnectSignal? reconnect = null,
        ChatSourceMonitor? sources = null,
        TimeProvider? time = null)
        : this(settingsProvider, oauth, bus, loggerFactory, trialProbe, spamFilter, sessions, httpFactory,
            reconnect, sources, time, FacebookChatTimings.Default)
    {
    }

    /// <summary>Test kurucusu: süreler dışarıdan verilir.</summary>
    internal FacebookChatHostedService(
        Func<AppSettings> settingsProvider,
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        ITrialModeProbe? trialProbe,
        SpamFilter? spamFilter,
        StreamSessionService? sessions,
        IHttpClientFactory? httpFactory,
        ChatReconnectSignal? reconnect,
        ChatSourceMonitor? sources,
        TimeProvider? time,
        FacebookChatTimings timings)
    {
        _settingsProvider = settingsProvider;
        _oauth = oauth;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<FacebookChatHostedService>();
        _trialProbe = trialProbe;
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;
        _reconnect = reconnect;
        _sources = sources;
        _time = time ?? TimeProvider.System;
        _timings = timings;

        if (_sessions is not null)
        {
            _sessions.SessionEnded += OnSessionEnded;
            _sessions.SessionStarted += OnSessionStarted;
        }

        if (_reconnect is not null)
            _reconnect.Requested += OnReconnectRequested;
    }

    // ── Olay dinleyicileri: çağıranı (arayüz) BEKLETMEZ ─────────────────────────────────────────

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operator pressed "Yayını Bitir" → drop the poller (ya da süren yayın
        // aramasını) so the next start re-resolves immediately rather than
        // waiting for Meta to time out.
        lock (_gate) CutWorkLocked();
    }

    private void OnSessionStarted(object? sender, SessionStartedEventArgs e)
    {
        // "Yayın Başlat" → uyuyan döngüyü hemen uyandır ve resolver'ı
        // hızlı moda al: yeni canlı yayın saniyeler içinde aransın.
        lock (_gate) _fastUntil = _time.GetUtcNow() + _timings.FastResolveWindow;
        Wake();
    }

    private void OnReconnectRequested(object? sender, EventArgs e)
    {
        if (_trialProbe?.IsTrialMode == true || !SessionActive())
        {
            _log.LogInformation(
                "[ChatReconnect] facebook: kaynak çalışmıyor (oturum yok ya da deneme sürümü); yapılacak iş yok");
            return;
        }

        bool pageBound;
        bool hadWork;
        lock (_gate)
        {
            _fastUntil = _time.GetUtcNow() + _timings.FastResolveWindow;
            _staleVideoId = null;     // az önce biten yayın yeniden açılmış olabilir: kara liste kalkar
            _consecutiveCrashes = 0;  // operatör "şimdi dene" dedi: geri çekilme baştan başlar
            pageBound = _pageBound;

            // Kesme ile "bağlanıyor" aynı kilit altında: kesilen iş bundan sonra durum yazamaz, yerine
            // gelen iş ise sonucunu "bağlanıyor"un ardından yazar. Sayfa bağlı değilse şeritte Facebook
            // satırı yoktur; "bağlanıyor" da yazılmaz.
            hadWork = CutWorkLocked();
            if (pageBound) ReportLocked(ChatSourceState.Connecting);
        }

        // Sayfa bağlı görünmese de döngü uyandırılır: Sayfa az önce bağlanmış olabilir.
        Wake();

        if (!pageBound)
            _log.LogInformation("[ChatReconnect] facebook: bağlı Sayfa görünmüyor; yeniden denetleniyor");
        else if (hadWork)
            _log.LogInformation("[ChatReconnect] facebook: okuyucu ya da yayın araması kesildi, yeniden bağlanılıyor");
        else
            _log.LogInformation("[ChatReconnect] facebook: bekleme kesildi, yeniden bağlanılıyor");
    }

    /// <summary>
    /// Kayıtlı işi (yayın araması ya da okuyucu) keser. <see cref="_gate"/> altında çağrılır. İş o an
    /// "kesildi" sayılır: bundan sonra okuyucu kuramaz, durum yazamaz. Kesme havuzda gecikmeli işlenseydi
    /// kesilen iş önce bağlanır, hemen ardından bir kez daha bağlanılırdı. Belirtecin iptali de bu
    /// çağrıyla görünür olur (<see cref="CancellationTokenSource.CancelAsync"/>); geri çağrılar (ve
    /// onlara bağlı devam kodu) havuzda koşar, çağıranın (arayüz) iş parçacığında değil.
    /// </summary>
    /// <returns>Kesilecek iş var mıydı.</returns>
    private bool CutWorkLocked()
    {
        // Kayıtlı iş henüz bırakılmamıştır: döngü kaydı bu kilit altında siler, belirteci ondan sonra bırakır.
        var work = _workCts;
        if (work is null) return false;

        _workCut = true;
        _ = work.CancelAsync().ContinueWith(
            static (cancel, log) => ((ILogger)log!).LogDebug(
                cancel.Exception, "[FacebookChatHostedService] iptal sırasında hata"),
            _log,
            CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        return true;
    }

    private void Wake()
    {
        try { _wake.Release(); }
        catch (SemaphoreFullException) { /* zaten sinyalli */ }
        catch (ObjectDisposedException) { /* servis kapanıyor */ }
    }

    /// <summary>Bekleyen "uyan" sinyali varsa alır; beklemez.</summary>
    private void DrainWake()
    {
        try { _wake.Wait(0); }
        catch (ObjectDisposedException) { /* servis kapanıyor */ }
    }

    /// <summary>
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> ama
    /// SessionStarted ya da yenileme sinyaliyle erken uyanabilir. Kaybeden
    /// görev linked CTS ile iptal edilir — aksi hâlde terk edilmiş WaitAsync
    /// ileride gelecek bir Release'i yutardı.
    /// </summary>
    private async Task IdleAsync(TimeSpan delay, CancellationToken ct)
    {
        using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, _time, lcts.Token);
        var wakeTask = _wake.WaitAsync(lcts.Token);
        await Task.WhenAny(delayTask, wakeTask).ConfigureAwait(false);
        lcts.Cancel();
        try { await Task.WhenAll(delayTask, wakeTask).ConfigureAwait(false); }
        catch (OperationCanceledException) { /* kaybeden iptal edildi */ }
        ct.ThrowIfCancellationRequested();
    }

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
            catch (Exception ex)
            {
                _log.LogDebug(ex, "[FacebookChatHostedService] stop wait swallowed");
            }
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        var resolverHttp = _httpFactory?.CreateClient(ResolverClientName)
                          ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var resolver = new FacebookLiveVideoResolver(
            resolverHttp, _loggerFactory.CreateLogger<FacebookLiveVideoResolver>());

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_trialProbe?.IsTrialMode == true)
                {
                    Report(ChatSourceState.Stopped);
                    await IdleAsync(_timings.IdleWhenOffline, ct);
                    continue;
                }

                // No Page bound yet (operator hasn't run OAuth) → idle.
                var creds = await _oauth.GetPageCredentialsAsync(ct).ConfigureAwait(false);
                lock (_gate) _pageBound = creds is not null;
                if (creds is null)
                {
                    Report(ChatSourceState.Stopped);
                    await IdleAsync(_timings.IdleWhenOffline, ct);
                    continue;
                }

                // Stream session gate — same semantics as YouTube. Operator
                // must press "Yayın Başlat" before we touch the Graph API.
                if (!SessionActive())
                {
                    Report(ChatSourceState.Stopped);
                    await IdleAsync(_timings.IdleWhenOffline, ct);
                    continue;
                }

                // Birikmiş "uyan" sinyali artık hükümsüz: döngü zaten uyanık ve
                // şimdi baştan arayacak. Boşaltılmazsa (ör. oturum başlangıcının
                // sinyali, döngü beklemeye girmeden gelmişse) bir sonraki beklemeyi
                // boş yere keser ve fazladan bir arama yaptırır. İş SIRASINDA gelen
                // sinyal (yenileme) boşaltmadan sonra geldiği için korunur.
                DrainWake();

                // Kesilebilir iş: yayın araması + okuyucu. Oturum bitişi ve
                // yenileme bunu keser; döngü beklemeden baştan değerlendirir.
                TimeSpan? idle;
                using (var work = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    lock (_gate)
                    {
                        _workCts = work;
                        _workCut = false;
                    }

                    try
                    {
                        idle = await BindAndReadAsync(
                            resolver, creds.Value.PageId, creds.Value.PageAccessToken, work, ct)
                            .ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        idle = null; // iş kesildi (oturum bitti ya da yenileme istendi): beklemeden baştan
                    }
                    finally
                    {
                        // Önce kayıt silinir (kilit altında), sonra belirteç bırakılır (using):
                        // dinleyiciler kayıtlı işi keserken bırakılmış bir belirteçle karşılaşmaz.
                        lock (_gate)
                        {
                            _workCts = null;
                            _workCut = false;
                        }
                    }
                }

                if (idle is { } wait)
                    await IdleAsync(wait, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[FacebookChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(_timings.IdleAfterStreamExit, _time, ct); } catch { break; }
            }
        }

        Report(ChatSourceState.Stopped);
    }

    /// <summary>
    /// Kapılar geçildikten sonraki iş: canlı yayını bul, okuyucuyu kur, çıkmasını bekle.
    /// </summary>
    /// <returns>Döngünün başa dönmeden önce bekleyeceği süre; null ise beklemeden döner.</returns>
    private async Task<TimeSpan?> BindAndReadAsync(
        FacebookLiveVideoResolver resolver, string pageId, string pageAccessToken,
        CancellationTokenSource work, CancellationToken ct)
    {
        // Oturum, kapıdan sonra ve bu iş kaydedilmeden önce bitmiş olabilir: bitiş olayı o an kesecek
        // bir iş bulamadı. Yeniden bakılmazsa oturumu bitmiş bir yayına okuyucu kurulurdu.
        if (!SessionActive()) return null;

        // Kapılar yeni açıldı: "bağlanıyor". Sorun durumundan yapılan rutin yeniden aramalar satırı
        // çevirmez (her 5 sn'de bir "bağlanıyor" yanıp sönmesin); sonuç değişirse yeni durum yazılır.
        lock (_gate)
        {
            ThrowIfWorkCutLocked();
            if (_reported is null or ChatSourceState.Stopped) ReportLocked(ChatSourceState.Connecting);
        }

        var lookup = await resolver.ResolveDetailedAsync(pageId, pageAccessToken, work.Token)
            .ConfigureAwait(false);

        if (lookup.Status == FacebookLiveLookupStatus.AuthProblem)
        {
            // Sayfa token'ı geçersiz ya da izin eksik: beklemek düzeltmez, hızlı arama da boşuna olurdu.
            ReportFromWork(ChatSourceState.NeedsAttention, FacebookStatusMessages.AuthProblem);
            return _timings.IdleWhenOffline;
        }

        // Canlı değil ve geçici hata aynı yoldan gider: yayın yok sayılır, yeniden aranır.
        var videoId = lookup.Status == FacebookLiveLookupStatus.Live ? lookup.VideoId : null;

        // Bayat-kimlik koruması: az önce kendi kendine biten yayın
        // Meta listesinde hâlâ LIVE görünebilir — aynı id'ye geri
        // bağlanmak ölü videoda sonsuz döngü demek.
        if (!string.IsNullOrEmpty(videoId) && IsStale(videoId))
        {
            _log.LogDebug(
                "[FacebookChatHostedService] resolver returned recently-ended video {VideoId}; treating as not live",
                videoId);
            videoId = null;
        }

        if (string.IsNullOrEmpty(videoId))
        {
            ReportFromWork(ChatSourceState.NoBroadcast);
            var notLiveIdle = InFastWindow() ? _timings.FastResolveInterval : _timings.IdleWhenOffline;
            _log.LogDebug(
                "[FacebookChatHostedService] page {PageId} not currently live; retry in {Idle}s",
                pageId, notLiveIdle.TotalSeconds);
            return notLiveIdle;
        }

        _log.LogInformation(
            "[FacebookChatHostedService] opening comment poller for video {VideoId}", videoId);

        // streaming-graph endpoint expects a long-lived connection —
        // never let the named-client default timeout (typically
        // 100s) kill it. We override to InfiniteTimeSpan locally.
        var streamHttp = _httpFactory?.CreateClient(StreamClientName)
                         ?? new HttpClient();
        streamHttp.Timeout = Timeout.InfiniteTimeSpan;

        // Video değiştiyse hafızayı temizler, aynı videoya yeniden
        // bağlanıyorsak hatırlamayı sürdürür (tekrar yayını engeller).
        _seen.ResetFor(videoId);

        using var stream = new FacebookLiveCommentsStream(
            videoId,
            pageAccessToken,
            _bus,
            streamHttp,
            _loggerFactory.CreateLogger<FacebookLiveCommentsStream>(),
            _spamFilter,
            _seen);

        // Yayın aranırken kesilmiş iş okuyucu kurmaz: kesen olayın ardından zaten baştan bağlanılacak.
        lock (_gate) ThrowIfWorkCutLocked();

        bool crashed = false;
        bool cancelled = false;
        Task? watchdog = null;
        try
        {
            await stream.StartAsync(work.Token);
            ReportFromWork(ChatSourceState.Live);
            // R10-CHAT01: sayaç artık burada SIFIRLANMAZ (StartAsync
            // hep başarılı — reset backoff eskalasyonunu öldürüyordu);
            // sağlıklı çıkışta (FastRebind) sıfırlanır.
            watchdog = RunWatchdogAsync(resolver, pageId, pageAccessToken, videoId, work);
            await stream.Completion.WaitAsync(work.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { cancelled = true; /* oturum bitişi, yenileme ya da bekçi */ }
        catch (Exception ex)
        {
            crashed = true;
            int count;
            lock (_gate) count = ++_consecutiveCrashes;
            _log.LogWarning(ex,
                "[FacebookChatHostedService] stream crashed (#{Count} consecutive); rescheduling",
                count);
        }
        finally
        {
            try { work.Cancel(); } catch { /* ignore */ }
            if (watchdog is not null)
            {
                try { await watchdog.ConfigureAwait(false); } catch { /* ignore */ }
            }
            try { await stream.StopAsync(CancellationToken.None); } catch { /* ignore */ }
        }

        // R10-CHAT01: çıkış sınıflandırması artık poller'ın
        // bildirdiği SEBEBE bakar. Eski kod "kendi kendine çıktı
        // = yayın bitti" sayıp 5 ardışık geçici ağ hatasında da
        // id'yi karalisteye alıyordu — yayın hâlâ canlıyken
        // toparlanan ağ 2 dakika aynı yayına dönemiyordu.
        switch (ClassifyStreamExit(crashed, cancelled, stream.EndReason))
        {
            case StreamExitAction.Backoff:
            {
                // Çökme ya da geçici hata limiti: yayının
                // bittiğine kanıt yok. Karaliste YOK — resolver
                // AYNI canlı videoya yeniden bağlanabilir;
                // sınırlı backoff fırtınayı engeller.
                int crashes;
                lock (_gate)
                {
                    if (!crashed) _consecutiveCrashes++; // çökme yolunu catch saydı
                    crashes = _consecutiveCrashes;
                }

                var backoff = ComputeBackoff(crashes);
                ReportFromWork(ChatSourceState.Retrying, retryAt: _time.GetUtcNow() + backoff);
                return backoff;
            }

            case StreamExitAction.StaleThenFastRebind:
                // code:100 — yayın KANITLI bitti, ama Meta
                // listede bir süre daha LIVE gösterebilir; bu
                // id'ye geri bağlanma.
                lock (_gate)
                {
                    _staleVideoId = videoId;
                    _staleUntil = _time.GetUtcNow() + _timings.StaleWindow;
                }

                // Pencere ve sayaç durum bildiriminden ÖNCE: iş bir olayla kesildiyse bildirim işi
                // bitirir (aşağıdaki FastRebind'de de öyle), bunlar yine de yapılmış olmalı.
                OpenFastWindowAndResetCrashes();
                ReportFromWork(ChatSourceState.NoBroadcast);
                return _timings.FastResolveInterval;

            case StreamExitAction.FastRebind:
            default:
                // Okuyucu kesildi (oturum bitişi, yenileme, bekçi). Yayıncı
                // büyük olasılıkla yeniden yayın açacak → hızlı arama penceresi.
                // Bekçi kestiyse iş durumu kendisi yazar; olay kestiyse durumu olay yazmıştır
                // (yenileme "bağlanıyor"; oturum bitişinde kapılar "durdu" der) ve döngü beklemeden
                // baştan değerlendirir.
                OpenFastWindowAndResetCrashes();
                ReportFromWork(SessionActive() ? ChatSourceState.Connecting : ChatSourceState.Stopped);
                return _timings.FastResolveInterval;
        }
    }

    /// <summary>
    /// Aktif poller yanında koşar: canlı listesini periyodik sorgular ve
    /// bağlı video artık geçerli değilse <paramref name="streamCts"/>'i
    /// iptal eder. Yalnız iptal eder, paylaşılan alanlara YAZMAZ — çıkış
    /// sınıflandırması ana döngüde yapılır.
    /// </summary>
    private async Task RunWatchdogAsync(
        FacebookLiveVideoResolver resolver,
        string pageId,
        string pageAccessToken,
        string boundVideoId,
        CancellationTokenSource streamCts)
    {
        int nullStreak = 0;
        try
        {
            while (!streamCts.IsCancellationRequested)
            {
                await Task.Delay(_timings.WatchdogInterval, _time, streamCts.Token).ConfigureAwait(false);
                var current = await resolver.ResolveAsync(pageId, pageAccessToken, streamCts.Token)
                    .ConfigureAwait(false);
                if (WatchdogShouldFire(current, boundVideoId, ref nullStreak))
                {
                    _log.LogInformation(
                        "[FacebookChatHostedService] watchdog: bound video {Bound} superseded (current: {Current}); cutting poller",
                        boundVideoId, current ?? "<none>");
                    try { streamCts.Cancel(); } catch { /* ignore */ }
                    return;
                }
            }
        }
        catch (OperationCanceledException) { /* stream önce bitti */ }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "[FacebookChatHostedService] watchdog swallowed");
        }
    }

    // ── Yardımcılar ─────────────────────────────────────────────────────────────────────────────

    private bool SessionActive() => _sessions is null || _sessions.GetActive() is not null;

    private bool InFastWindow()
    {
        lock (_gate) return _time.GetUtcNow() < _fastUntil;
    }

    private bool IsStale(string videoId)
    {
        lock (_gate) return videoId == _staleVideoId && _time.GetUtcNow() < _staleUntil;
    }

    private void OpenFastWindowAndResetCrashes()
    {
        lock (_gate)
        {
            _consecutiveCrashes = 0;
            _fastUntil = _time.GetUtcNow() + _timings.FastResolveWindow;
        }
    }

    /// <summary>Döngünün iş DIŞINDAKİ durum bildirimi (kapılar, kapanış).</summary>
    private void Report(ChatSourceState state)
    {
        lock (_gate) ReportLocked(state);
    }

    /// <summary>
    /// İşin (yayın araması + okuyucu) durum bildirimi. İş bir olayla kesildiyse yazmaz ve işi bitirir:
    /// kesen olay kendi durumunu yazdı, döngü beklemeden baştan değerlendirecek.
    /// </summary>
    private void ReportFromWork(ChatSourceState state, string? detail = null, DateTimeOffset? retryAt = null)
    {
        lock (_gate)
        {
            ThrowIfWorkCutLocked();
            ReportLocked(state, detail, retryAt);
        }
    }

    /// <summary><see cref="_gate"/> altında çağrılır. İş bir olayla kesildiyse iptal istisnası fırlatır.</summary>
    private void ThrowIfWorkCutLocked()
    {
        if (_workCut)
            throw new OperationCanceledException("Facebook sohbet işi oturum bitişi ya da yenilemeyle kesildi.");
    }

    /// <summary>
    /// Durumu izleyiciye yazar; DEĞİŞTİYSE günlüğe de bir satır düşer. <see cref="_gate"/> altında
    /// çağrılır: döngü ile dinleyiciler aynı anda bildirirse izleyicideki son durum ile günlükteki
    /// sıra birbirini tutar.
    /// </summary>
    private void ReportLocked(ChatSourceState state, string? detail = null, DateTimeOffset? retryAt = null)
    {
        var changed = _reported != state;
        _reported = state;
        _sources?.Report(Platform, state, detail, retryAt);
        if (changed) _log.LogInformation("[FacebookChatHostedService] durum: {State}", state);
    }

    /// <summary>
    /// Bekçi karar kuralı (saf, test edilebilir): farklı bir canlı video
    /// görülür görülmez ateşle; "canlı yok" için 2 ardışık tur bekle —
    /// resolver geçici hatalarda da null döner, tek null kanıt değildir.
    /// </summary>
    internal static bool WatchdogShouldFire(string? current, string boundVideoId, ref int nullStreak)
    {
        if (string.IsNullOrEmpty(current))
        {
            nullStreak++;
            return nullStreak >= 2;
        }
        if (!string.Equals(current, boundVideoId, StringComparison.Ordinal))
            return true; // yeni yayın başladı — eskisini bekletme
        nullStreak = 0;
        return false;
    }

    /// <summary>Akış çıkışında ana döngünün yapacağı iş (R10-CHAT01).</summary>
    internal enum StreamExitAction
    {
        /// <summary>Hızlı arama penceresi + kısa bekleme; sayaç sıfırlanır.</summary>
        FastRebind,
        /// <summary>Yayın kanıtlı bitti: id karalisteye, sonra FastRebind.</summary>
        StaleThenFastRebind,
        /// <summary>Bitti kanıtı yok: karaliste YOK, sınırlı backoff ile
        /// AYNI videoya yeniden bağlanılabilir.</summary>
        Backoff,
    }

    /// <summary>
    /// Çıkış sınıflandırma kuralı (saf, test edilebilir): karaliste YALNIZ
    /// kanıtlı bitişte (<see cref="FacebookStreamEndReason.BroadcastEnded"/> =
    /// Graph code:100). Geçici hata limiti "yayın bitti" değildir — eski
    /// davranış onu da karaliste + hızlı-arama sayıp canlı yayına 2 dakika
    /// geri bağlanamamaya yol açıyordu.
    /// </summary>
    internal static StreamExitAction ClassifyStreamExit(
        bool crashed, bool cancelled, FacebookStreamEndReason endReason)
    {
        if (crashed) return StreamExitAction.Backoff;
        if (cancelled) return StreamExitAction.FastRebind;
        return endReason switch
        {
            FacebookStreamEndReason.BroadcastEnded => StreamExitAction.StaleThenFastRebind,
            FacebookStreamEndReason.TransientFailure => StreamExitAction.Backoff,
            _ => StreamExitAction.FastRebind,
        };
    }

    /// <summary>Exponential backoff: 30s × 2^(n-1) capped at 5min. Same
    /// shape as YouTube's so operator never sees one platform recover
    /// faster than the other after a network hiccup.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterStreamExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterStreamExit.TotalSeconds * Math.Pow(2, exp);
        if (seconds >= MaxBackoff.TotalSeconds) return MaxBackoff;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        if (_sessions is not null)
        {
            _sessions.SessionEnded -= OnSessionEnded;
            _sessions.SessionStarted -= OnSessionStarted;
        }

        if (_reconnect is not null)
            _reconnect.Requested -= OnReconnectRequested;

        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _wake.Dispose();
    }
}

/// <summary>Döngünün süreleri; testler kısa değerlerle kurar.</summary>
/// <param name="IdleWhenOffline">Kaynak çalışmıyorken ve olağan (pencere dışı) yayın aramaları arasında.</param>
/// <param name="IdleAfterStreamExit">Beklenmeyen döngü hatasından sonra.</param>
/// <param name="FastResolveInterval">Hızlı arama penceresinde iki arama arası.</param>
/// <param name="FastResolveWindow">Oturum başlangıcından, yenilemeden ve okuyucu çıkışından sonra hızlı
/// aramanın sürdüğü süre.</param>
/// <param name="WatchdogInterval">Bekçinin canlı listesini sorgulama aralığı.</param>
/// <param name="StaleWindow">Kanıtlı biten yayının kimliğine geri bağlanmanın reddedildiği süre.</param>
internal sealed record FacebookChatTimings(
    TimeSpan IdleWhenOffline,
    TimeSpan IdleAfterStreamExit,
    TimeSpan FastResolveInterval,
    TimeSpan FastResolveWindow,
    TimeSpan WatchdogInterval,
    TimeSpan StaleWindow)
{
    /// <summary>Ürün değerleri.</summary>
    public static FacebookChatTimings Default { get; } = new(
        IdleWhenOffline: TimeSpan.FromSeconds(60),
        IdleAfterStreamExit: TimeSpan.FromSeconds(30),
        // "Hızlı" mod: oturum başladıktan / yayın kapandıktan sonraki pencere
        // boyunca resolver 60s yerine 5s'te bir denenir — yayıncının kapat/aç
        // döngüsünde yeni yayını saniyeler içinde yakalamak için.
        FastResolveInterval: TimeSpan.FromSeconds(5),
        FastResolveWindow: TimeSpan.FromMinutes(2),
        // Aktif poller yanında koşan bekçi: canlı listesini periyodik sorgular;
        // video listeden düşmüş ya da yenisi başlamışsa poller'ı keser. (Biten
        // yayının comments ucu 200 dönmeye devam ettiği için code:100 tespiti
        // hiç gelmeyebilir — bekçi olmadan poller ölü videoda sonsuza dek döner.)
        WatchdogInterval: TimeSpan.FromSeconds(10),
        // Yayın kendi kendine bittiğinde Meta live_videos listesinde bir süre
        // daha LIVE gösterebiliyor; bu pencere içinde aynı id'ye yeniden
        // bağlanmayı reddediyoruz.
        StaleWindow: TimeSpan.FromMinutes(2));
}

/// <summary>Facebook sohbet satırının "elle düzeltilmeli" ipuçları (tasarım: Sohbeti yenile §5.4).</summary>
public static class FacebookStatusMessages
{
    public const string AuthProblem =
        "Facebook oturumu ya da izni geçersiz. Ayarlar > Facebook > \"Facebook izinlerini güncelle\".";
}
