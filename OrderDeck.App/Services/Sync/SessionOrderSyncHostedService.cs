using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Oturum ve etiket gönderiminin zamanlayıcısı (PR siparis-sync 2026-05-13). Pattern: PaymentSyncHostedService.
///
/// <para><b>Ritim (sahip kararı Y9, 2026-10-14; spec 2026-10-13 §11):</b> "İnternet yoksa yayında yok, satış da yok."
/// Zamanlayıcı <see cref="LiveInterval"/>'da (5 sn) bir tıklar:</para>
/// <list type="bullet">
/// <item>Bu bilgisayarda canlı (aktif) yayın varken her tıkta tur: bilgisayar yayın ortasında kapanırsa sunucuda
/// eksik kalan en çok son birkaç saniyedir.</item>
/// <item>Yayının bittiği görülen tıkta bir tur daha: kapanış ve son etiketler aktif yayın yokken uygulanan bekleme
/// olmadan gider.</item>
/// <item>Aktif yayın yokken en çok <see cref="IdleInterval"/>'da (60 sn, bugünkü ritim) bir tur. Açılıştaki ilk
/// tıkta tur koşar.</item>
/// </list>
/// <para>Aktif yayın her tıkta <see cref="SessionRepository.GetActive"/>'ten (Authority = 'local' ∧ EndedAt NULL,
/// §5.9) okunur: yayını başlatma ve bitirme ritmi en geç bir tıkta değiştirir. Tur gönderilecek bir şey yoksa
/// hiçbir istek atmaz (<see cref="SessionOrderSyncService"/>'in ucuz kuyruk denetimi). Tur arka plan iş
/// parçacığında ve gönderim kapısından geçer; canlı yol (etiket yazma, baskı, iptal) onu beklemez.</para>
///
/// <para>Canlı ritimdeki tur (<c>liveCadence</c>, W1-07): devir bekleyen bitmiş oturum her tıkta değil, gönderilmemiş
/// etiket sayısı değişince ya da en çok <see cref="SessionOrderSyncService.HeldResendInterval"/>'da bir yeniden
/// gider.</para>
///
/// <para><b>Hatalar:</b> tur hata verirse (yerel SQLite hatası — HTTP hatalarını servis kendisi yutar) uyarı en çok
/// <see cref="FailureWarningInterval"/>'da bir yazılır, arası Debug; zamanlayıcı sürer.</para>
/// </summary>
public sealed class SessionOrderSyncHostedService : BackgroundService
{
    /// <summary>Tık aralığı; canlı yayında tur aralığı (Y9).</summary>
    public static readonly TimeSpan LiveInterval = TimeSpan.FromSeconds(5);

    /// <summary>Aktif yayın yokken iki tur arasındaki en kısa süre (bugünkü ritim).</summary>
    public static readonly TimeSpan IdleInterval = TimeSpan.FromSeconds(60);

    /// <summary>İki tur hatası uyarısı arasındaki en kısa süre.</summary>
    internal static readonly TimeSpan FailureWarningInterval = TimeSpan.FromMinutes(1);

    private readonly SessionOrderSyncService _syncService;
    private readonly SessionRepository _sessions;
    private readonly ILogger<SessionOrderSyncHostedService> _log;
    private readonly TimeSpan _tick;
    private readonly TimeSpan _idleInterval;
    private readonly TimeProvider _time;

    private long? _lastTourAt;
    private bool _wasLive;
    private long? _lastWarnAt;

    public SessionOrderSyncHostedService(
        SessionOrderSyncService syncService,
        SessionRepository sessions,
        ILogger<SessionOrderSyncHostedService> log)
        : this(syncService, sessions, log, LiveInterval, IdleInterval, TimeProvider.System) { }

    /// <param name="tick">Tık aralığı (canlı yayında tur aralığı).</param>
    /// <param name="idleInterval">Aktif yayın yokken iki tur arasındaki en kısa süre.</param>
    /// <param name="time">Zaman kaynağı: zamanlayıcı ve aralık ölçümü (testlerde elle ilerletilir).</param>
    internal SessionOrderSyncHostedService(
        SessionOrderSyncService syncService,
        SessionRepository sessions,
        ILogger<SessionOrderSyncHostedService> log,
        TimeSpan tick,
        TimeSpan idleInterval,
        TimeProvider time)
    {
        _syncService = syncService;
        _sessions = sessions;
        _log = log;
        _tick = tick;
        _idleInterval = idleInterval;
        _time = time;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_tick, _time);
        while (await WaitSafe(timer, stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await TickAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Bir tık: ritim kuralına göre tur koşar ya da atlar. Hata dışarı sızmaz (iptal hariç).</summary>
    /// <returns>Tur koştu mu.</returns>
    internal async Task<bool> TickAsync(CancellationToken ct)
    {
        bool live;
        try
        {
            live = _sessions.GetActive() is not null;
        }
        catch (Exception ex)
        {
            // Okunamadıysa aktif yayın varmış gibi davranmak her tıkta tur demekti; boşta ritmi güvenli taraf.
            Warn(ex, "Session/Order sync: active session could not be read; using the idle interval");
            live = false;
        }

        // Canlı ritim (yayında ya da yayının bittiği görülen tıkta): devir bekleyen oturum her tıkta değil, sayısı
        // değişince ya da dakikada bir yeniden gider (SessionOrderSyncService.HeldResendInterval).
        var liveCadence = live || _wasLive;
        var due = liveCadence                                               // _wasLive: yayın bu tıkta bitti, bir tur daha
            || _lastTourAt is not { } last
            || _time.GetElapsedTime(last) >= _idleInterval;
        _wasLive = live;
        if (!due) return false;

        _lastTourAt = _time.GetTimestamp();
        try
        {
            await _syncService.SyncOnceAsync(liveCadence, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            Warn(ex, "Session/Order sync tick failed; will retry next interval");
        }
        return true;
    }

    private void Warn(Exception ex, string message)
    {
        if (_lastWarnAt is { } last && _time.GetElapsedTime(last) < FailureWarningInterval)
        {
            _log.LogDebug(ex, message);
            return;
        }
        _lastWarnAt = _time.GetTimestamp();
        _log.LogWarning(ex, message);
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { return false; }
    }
}
