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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FacebookChatHostedService : IHostedService, IDisposable
{
    private static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan IdleAfterStreamExit = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);

    // "Hızlı" mod: oturum başladıktan / yayın kapandıktan sonraki pencere
    // boyunca resolver 60s yerine 5s'te bir denenir — yayıncının kapat/aç
    // döngüsünde yeni yayını saniyeler içinde yakalamak için.
    private static readonly TimeSpan FastResolveInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastResolveWindow = TimeSpan.FromMinutes(2);

    // Aktif poller yanında koşan bekçi: canlı listesini periyodik sorgular;
    // video listeden düşmüş ya da yenisi başlamışsa poller'ı keser. (Biten
    // yayının comments ucu 200 dönmeye devam ettiği için code:100 tespiti
    // hiç gelmeyebilir — bekçi olmadan poller ölü videoda sonsuza dek döner.)
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(10);

    // Yayın kendi kendine bittiğinde Meta live_videos listesinde bir süre
    // daha LIVE gösterebiliyor; bu pencere içinde aynı id'ye yeniden
    // bağlanmayı reddediyoruz.
    private static readonly TimeSpan StaleWindow = TimeSpan.FromMinutes(2);

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

    private CancellationTokenSource? _cts;
    private Task? _runner;

    // Per-stream cancellation source so SessionEnded can cut the SSE
    // connection immediately when the operator ends the session.
    private CancellationTokenSource? _streamCts;

    // SessionStarted bekleme uykularını anında böler (max 1 bilet yeter:
    // amaç "bir sonraki uyku kısalsın", sayaç değil).
    private readonly SemaphoreSlim _wake = new(0, 1);

    private DateTimeOffset _fastUntil;
    private string? _staleVideoId;
    private DateTimeOffset _staleUntil;

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
        IHttpClientFactory? httpFactory = null)
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

        if (_sessions is not null)
        {
            _sessions.SessionEnded += OnSessionEnded;
            _sessions.SessionStarted += OnSessionStarted;
        }
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operator pressed "Yayını Bitir" → drop the SSE so the next start
        // re-resolves immediately rather than waiting for Meta to time out.
        try { _streamCts?.Cancel(); } catch { /* ignore */ }
    }

    private void OnSessionStarted(object? sender, SessionStartedEventArgs e)
    {
        // "Yayın Başlat" → uyuyan döngüyü hemen uyandır ve resolver'ı
        // hızlı moda al: yeni canlı yayın saniyeler içinde aransın.
        _fastUntil = DateTimeOffset.UtcNow + FastResolveWindow;
        try { _wake.Release(); } catch (SemaphoreFullException) { /* zaten sinyalli */ }
    }

    /// <summary>
    /// <see cref="Task.Delay(TimeSpan, CancellationToken)"/> ama
    /// SessionStarted sinyaliyle erken uyanabilir. Kaybeden görev linked
    /// CTS ile iptal edilir — aksi hâlde terk edilmiş WaitAsync ileride
    /// gelecek bir Release'i yutardı.
    /// </summary>
    private async Task IdleAsync(TimeSpan delay, CancellationToken ct)
    {
        using var lcts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delayTask = Task.Delay(delay, lcts.Token);
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

        int consecutiveCrashes = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (_trialProbe?.IsTrialMode == true)
                {
                    await IdleAsync(IdleWhenOffline, ct);
                    continue;
                }

                // No Page bound yet (operator hasn't run OAuth) → idle.
                var creds = await _oauth.GetPageCredentialsAsync(ct).ConfigureAwait(false);
                if (creds is null)
                {
                    await IdleAsync(IdleWhenOffline, ct);
                    continue;
                }

                // Stream session gate — same semantics as YouTube. Operator
                // must press "Yayın Başlat" before we touch the Graph API.
                if (_sessions is not null && _sessions.GetActive() is null)
                {
                    await IdleAsync(IdleWhenOffline, ct);
                    continue;
                }

                var videoId = await resolver.ResolveAsync(
                    creds.Value.PageId, creds.Value.PageAccessToken, ct).ConfigureAwait(false);

                // Bayat-kimlik koruması: az önce kendi kendine biten yayın
                // Meta listesinde hâlâ LIVE görünebilir — aynı id'ye geri
                // bağlanmak ölü videoda sonsuz döngü demek.
                if (!string.IsNullOrEmpty(videoId)
                    && videoId == _staleVideoId
                    && DateTimeOffset.UtcNow < _staleUntil)
                {
                    _log.LogDebug(
                        "[FacebookChatHostedService] resolver returned recently-ended video {VideoId}; treating as not live",
                        videoId);
                    videoId = null;
                }

                if (string.IsNullOrEmpty(videoId))
                {
                    var notLiveIdle = DateTimeOffset.UtcNow < _fastUntil
                        ? FastResolveInterval
                        : IdleWhenOffline;
                    _log.LogDebug(
                        "[FacebookChatHostedService] page {PageId} not currently live; retry in {Idle}s",
                        creds.Value.PageId, notLiveIdle.TotalSeconds);
                    await IdleAsync(notLiveIdle, ct);
                    continue;
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
                    creds.Value.PageAccessToken,
                    _bus,
                    streamHttp,
                    _loggerFactory.CreateLogger<FacebookLiveCommentsStream>(),
                    _spamFilter,
                    _seen);

                using var streamCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                _streamCts = streamCts;

                bool crashed = false;
                bool cancelled = false;
                Task? watchdog = null;
                try
                {
                    await stream.StartAsync(streamCts.Token);
                    // R10-CHAT01: sayaç artık burada SIFIRLANMAZ (StartAsync
                    // hep başarılı — reset backoff eskalasyonunu öldürüyordu);
                    // sağlıklı çıkışta (FastRebind) sıfırlanır.
                    watchdog = RunWatchdogAsync(
                        resolver, creds.Value.PageId, creds.Value.PageAccessToken,
                        videoId, streamCts);
                    await stream.Completion.WaitAsync(streamCts.Token);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (OperationCanceledException) { cancelled = true; /* SessionEnded veya bekçi */ }
                catch (Exception ex)
                {
                    crashed = true;
                    consecutiveCrashes++;
                    _log.LogWarning(ex,
                        "[FacebookChatHostedService] stream crashed (#{Count} consecutive); rescheduling",
                        consecutiveCrashes);
                }
                finally
                {
                    _streamCts = null;
                    try { streamCts.Cancel(); } catch { /* ignore */ }
                    if (watchdog is not null)
                    {
                        try { await watchdog.ConfigureAwait(false); } catch { /* ignore */ }
                    }
                    try { await stream.StopAsync(CancellationToken.None); } catch { /* ignore */ }
                }

                if (!ct.IsCancellationRequested)
                {
                    // R10-CHAT01: çıkış sınıflandırması artık poller'ın
                    // bildirdiği SEBEBE bakar. Eski kod "kendi kendine çıktı
                    // = yayın bitti" sayıp 5 ardışık geçici ağ hatasında da
                    // id'yi karalisteye alıyordu — yayın hâlâ canlıyken
                    // toparlanan ağ 2 dakika aynı yayına dönemiyordu.
                    TimeSpan idle;
                    switch (ClassifyStreamExit(crashed, cancelled, stream.EndReason))
                    {
                        case StreamExitAction.Backoff:
                            // Çökme ya da geçici hata limiti: yayının
                            // bittiğine kanıt yok. Karaliste YOK — resolver
                            // AYNI canlı videoya yeniden bağlanabilir;
                            // sınırlı backoff fırtınayı engeller.
                            if (!crashed) consecutiveCrashes++; // çökme yolunu catch saydı
                            idle = ComputeBackoff(consecutiveCrashes);
                            break;

                        case StreamExitAction.StaleThenFastRebind:
                            // code:100 — yayın KANITLI bitti, ama Meta
                            // listede bir süre daha LIVE gösterebilir; bu
                            // id'ye geri bağlanma.
                            _staleVideoId = videoId;
                            _staleUntil = DateTimeOffset.UtcNow + StaleWindow;
                            goto case StreamExitAction.FastRebind;

                        case StreamExitAction.FastRebind:
                        default:
                            // Yayıncı büyük olasılıkla yeniden yayın açacak
                            // → hızlı arama penceresi.
                            consecutiveCrashes = 0;
                            _fastUntil = DateTimeOffset.UtcNow + FastResolveWindow;
                            idle = FastResolveInterval;
                            break;
                    }
                    await IdleAsync(idle, ct);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[FacebookChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(IdleAfterStreamExit, ct); } catch { break; }
            }
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
                await Task.Delay(WatchdogInterval, streamCts.Token).ConfigureAwait(false);
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
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
        _wake.Dispose();
    }
}
