using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Chat.Facebook;
using OrderDeck.Core.Chat;

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Hesap döngülerinin <see cref="InstagramChatHostedService"/>'ten aldığı ortak servisler.
/// </summary>
/// <param name="OAuth">Bağlı Sayfaların ve güncel token'larının kaynağı.</param>
/// <param name="Bus">Sohbet bus'ı: tek Sayfada poller doğrudan buna yazar.</param>
/// <param name="LoggerFactory">Poller'ların günlüğü buradan kurulur.</param>
/// <param name="Log">Hosted service'in günlüğü: hesap döngüsünün satırları da onun kategorisine
/// ve <c>[InstagramChatHostedService]</c> önekine yazılır.</param>
/// <param name="SpamFilter">Poller'lara verilen tekrar süzgeci; yoksa null.</param>
/// <param name="HttpFactory">Poller istemcilerinin fabrikası; yoksa her kurulumda yeni bir
/// <see cref="HttpClient"/>.</param>
/// <param name="Health">Hesap başına sağlığın yazıldığı izleyici.</param>
/// <param name="Time">Saat kaynağı: beklemeler, yeniden deneme saatleri, kota satırının aralığı.</param>
/// <param name="Timings">Bekleme süreleri.</param>
[SupportedOSPlatform("windows")]
internal sealed record InstagramLoopServices(
    FacebookOAuthService OAuth,
    IChatBus Bus,
    ILoggerFactory LoggerFactory,
    ILogger Log,
    SpamFilter? SpamFilter,
    IHttpClientFactory? HttpFactory,
    InstagramHealthMonitor Health,
    TimeProvider Time,
    InstagramChatTimings Timings);

/// <summary>
/// Bağlı TEK bir Sayfanın Instagram hesabını bir oturum boyunca yoklar: hesabı çözer, poller'ı
/// kurar, poller bitince bekleyip yeniden kurar. Her Sayfanın kendi döngüsü vardır; çöküş sayacı
/// ve beklemeler döngüye özeldir, bir döngüdeki istisna o döngüde kalır. Bir hesabın kalıcı
/// hatası, hız sınırı ya da çökmesi böylece yalnız o hesabı bekletir.
///
/// <para><b>Sağlık ve günlük.</b> Hesabın durumu <see cref="InstagramHealthMonitor"/>'a buradan
/// yazılır; izleyici günlük yazmaz. Durum geçişi ve kota satırlarını bu döngü yazar
/// (<see cref="Track"/>, <see cref="RoundObserver"/>), tek Sayfalı kurulumda da.</para>
///
/// <para><b>Tek yazar.</b> Bir Sayfanın sağlık girdisine aynı anda yalnız bir yerden yazılır:
/// poller çalışırken poller'ın gözlemcisi, öbür zamanlarda döngünün kendisi. Geçiş satırı
/// "önce oku, değiştir, sonra oku" diye yazıldığı için ikinci bir yazar yanlış geçiş yazdırırdı;
/// döngü bu yüzden poller'ının BİTMESİNİ bekler, çağıran da girdileri ancak döngü bittikten
/// sonra durdurur.</para>
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class InstagramAccountLoop
{
    private readonly string _pageId;
    private readonly InstagramLoopServices _services;
    private readonly InstagramAccountResolver _resolver;
    private readonly ConcurrentDictionary<string, string> _owners;
    private readonly InstagramMergeGate? _gate;
    private readonly RoundObserver _observer;

    /// <summary>Bu hesabın kulvarı: ilk poller kurulumunda bir kez açılır, sonraki kurulumlarda
    /// aynısı kullanılır. Kapı yoksa (tek Sayfa) hep null.</summary>
    private InstagramMergeGate.Lane? _lane;

    /// <param name="pageId">Yoklanacak Sayfa.</param>
    /// <param name="services">Ortak servisler.</param>
    /// <param name="resolver">O Sayfanın çözümleyicisi; önbelleği oturumlar arasında korunsun
    /// diye servis ömrünce yaşar, bu yüzden dışarıdan verilir.</param>
    /// <param name="owners">Instagram hesabı (IgUserId) → onu yoklayan Sayfa (PageId); bütün
    /// döngülerin ortak sözlüğü.</param>
    /// <param name="gate">İki ve üzeri Sayfada birleştirme kapısı; tek Sayfada null (poller
    /// doğrudan sohbet bus'ına yazar, mesaj etiketsizdir).</param>
    internal InstagramAccountLoop(
        string pageId,
        InstagramLoopServices services,
        InstagramAccountResolver resolver,
        ConcurrentDictionary<string, string> owners,
        InstagramMergeGate? gate)
    {
        _pageId = pageId;
        _services = services;
        _resolver = resolver;
        _owners = owners;
        _gate = gate;
        _observer = new RoundObserver(this);
    }

    /// <summary>
    /// Döngüyü <paramref name="ct"/> iptal olana ya da Sayfa bağlı Sayfalar arasından çıkana dek
    /// koşturur. Fırlatmaz: iptal olağan çıkıştır, beklenmeyen istisna günlüğe yazılıp beklenir
    /// ve döngü sürer. Dönmeden önce poller'ı durmuştur, kulvarı kapanmış ve hesabın sahipliği
    /// bırakılmıştır.
    /// </summary>
    /// <param name="ct">Oturum belirteci. Bütün beklemeler bununla yapılır: oturum bitince 5
    /// dakikalık beklemeler de kesilir, yeni oturum hesabı hemen yeniden dener.</param>
    internal async Task RunAsync(CancellationToken ct)
    {
        var log = _services.Log;
        var health = _services.Health;
        var timings = _services.Timings;
        var consecutiveCrashes = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    // 1) Kimlik: Sayfanın güncel token'ı. Sayfa artık bağlı değilse döngü biter.
                    var page = await FindPageAsync(ct).ConfigureAwait(false);
                    if (page is null) return;

                    // Yalnız durmuş girdide etkilidir: poller yeniden kurulurken sürmekte olan
                    // durum ve süresi silinmez.
                    Track(() => health.Report(_pageId, InstagramAccountState.Starting));

                    // 2) Çözümleme. "Hesap yok" ile "çağrı düştü" ayrı durumlardır; ikisi de
                    // uzun bekler.
                    var (resolved, status) = await _resolver
                        .ResolveDetailedAsync(_pageId, page.PageAccessToken, ct).ConfigureAwait(false);
                    if (resolved is not { } account)
                    {
                        ReportWaiting(status == InstagramResolveStatus.NotLinked
                            ? InstagramAccountState.NotLinked
                            : InstagramAccountState.ResolveFailed);
                        await IdleAsync(timings.IdleAfterFatal, ct).ConfigureAwait(false);
                        continue;
                    }

                    // 3) Sahiplik: aynı Instagram hesabını başka bir Sayfanın döngüsü yokluyorsa
                    // bu Sayfa atlanır (çift yorum olmasın). Sahiplik döngü yaşadıkça, beklemeleri
                    // dahil sürer.
                    var owner = _owners.GetOrAdd(account.IgUserId, _pageId);
                    if (!string.Equals(owner, _pageId, StringComparison.Ordinal))
                    {
                        log.LogWarning(
                            "[InstagramChatHostedService] page {PageId} skipped: Instagram account " +
                            "{IgUserId} is already polled through page {OwnerPageId}",
                            _pageId, account.IgUserId, owner);
                        ReportWaiting(InstagramAccountState.Skipped);
                        await IdleAsync(timings.IdleAfterFatal, ct).ConfigureAwait(false);
                        continue;
                    }

                    // Sahiplik alındıktan SONRA: "hesap çözüldü" bildirimi bekleyen girdiyi
                    // Starting'e alır; önce çağrılsaydı atlanan Sayfa her yeniden bakışta
                    // Skipped ↔ Starting yanıp sönerdi.
                    Track(() => health.SetAccount(_pageId, account.Username));

                    // 4) Poller.
                    log.LogInformation(
                        "[InstagramChatHostedService] starting poller for IG @{Username}",
                        account.Username ?? account.IgUserId);

                    var pollHttp = _services.HttpFactory?.CreateClient(InstagramChatHostedService.HttpClientName)
                                   ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };

                    _lane ??= _gate?.AddLane(_pageId, AccountLabel(account.Username, page.PageName));

                    // Filigran her kurulumda tazedir: yeni poller'ın ilk çekimi yutulur.
                    using var poller = new InstagramLiveCommentsPoller(
                        account.IgUserId,
                        page.PageAccessToken,
                        _lane is null ? _services.Bus : _lane,
                        pollHttp,
                        _services.LoggerFactory.CreateLogger<InstagramLiveCommentsPoller>(),
                        _services.SpamFilter,
                        _observer,
                        new InstagramCommentWatermark());

                    bool crashed = false;
                    try
                    {
                        await poller.StartAsync(ct).ConfigureAwait(false);
                        consecutiveCrashes = 0;
                        // Poller döngüsü bitince tamamlanır; oturum iptalinde de olağan biçimde.
                        await poller.Completion.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        // Oturum bitti; aşağıda çıkılır.
                    }
                    catch (Exception ex)
                    {
                        crashed = true;
                        consecutiveCrashes++;
                        log.LogWarning(ex,
                            "[InstagramChatHostedService] poller crashed (#{Count} consecutive)",
                            consecutiveCrashes);
                    }
                    finally
                    {
                        // Poller'ın BİTMESİ beklenir: buradan sonra tur olayı gelmez, sağlık
                        // girdisine yalnız bu döngü yazar.
                        try { await poller.StopAsync(CancellationToken.None).ConfigureAwait(false); }
                        catch { /* ignore */ }
                    }

                    if (ct.IsCancellationRequested) return;

                    // 5) Kalıcı hata (token/izin) → hemen tekrar denemek anlamsız.
                    if (poller.FatalReason is { } reason)
                    {
                        log.LogWarning(
                            "[InstagramChatHostedService] {Reason} — retrying in {Idle} min",
                            reason, timings.IdleAfterFatal.TotalMinutes);

                        // Geçiş satırı burada, yeniden deneme saatiyle yazılır: kalıcı hata turu
                        // satır yazmadı, çünkü izleyici o an saati temizler. Eski durum, o turdan
                        // ÖNCEKİ durumdur.
                        Track(
                            () => health.Report(_pageId, InstagramAccountState.Faulted, reason, RetryAt()),
                            from: _observer.TakeStateBeforeFatal());
                        await IdleAsync(timings.IdleAfterFatal, ct).ConfigureAwait(false);
                        continue;
                    }

                    await IdleAsync(
                        crashed
                            ? InstagramChatHostedService.ComputeBackoff(consecutiveCrashes)
                            : timings.IdleAfterPollerExit,
                        ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    // Yalıtım: istisna bu döngüde kalır; üst döngüye ve öbür hesaplara çıkmaz.
                    log.LogError(ex,
                        "[InstagramChatHostedService] page {PageId} loop error; sleeping before retry",
                        _pageId);
                    try { await IdleAsync(timings.IdleAfterPollerExit, ct).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                }
            }
        }
        finally
        {
            _lane?.Complete();

            // Sahiplik bırakılır: sözlükte değeri bu Sayfa olan kayıtlar silinir (başka bir
            // Sayfanın kaydına dokunulmaz).
            foreach (var owned in _owners)
            {
                if (string.Equals(owned.Value, _pageId, StringComparison.Ordinal))
                    _owners.TryRemove(owned);
            }
        }
    }

    /// <summary>Bu Sayfanın bağlı Sayfalar arasındaki güncel kaydı; Sayfa artık bağlı değilse null.</summary>
    private async Task<FacebookLinkedPage?> FindPageAsync(CancellationToken ct)
    {
        var pages = await _services.OAuth.GetConnectedPagesAsync(ct).ConfigureAwait(false);
        foreach (var page in pages)
        {
            if (string.Equals(page.PageId, _pageId, StringComparison.Ordinal)) return page;
        }

        return null;
    }

    private Task IdleAsync(TimeSpan duration, CancellationToken ct) => Task.Delay(duration, _services.Time, ct);

    /// <summary>Uzun beklemenin biteceği an: şimdi + <see cref="InstagramChatTimings.IdleAfterFatal"/>.</summary>
    private DateTimeOffset RetryAt() => _services.Time.GetUtcNow() + _services.Timings.IdleAfterFatal;

    /// <summary>Uzun beklemeye giren bir durumu (hesap yok, hesap okunamadı, Sayfa atlandı)
    /// yeniden deneme saatiyle bildirir.</summary>
    private void ReportWaiting(InstagramAccountState state) =>
        Track(() => _services.Health.Report(_pageId, state, retryAt: RetryAt()));

    /// <summary>
    /// Sağlık izleyicisini değiştiren HER çağrının ortak geçidi: girdinin durumunu çağrıdan önce
    /// ve sonra okur, değiştiyse geçiş satırını yazar. Girdi yoksa (izleyici bilinmeyen Sayfayı
    /// yok sayar) satır da yoktur.
    /// </summary>
    /// <param name="change">İzleyiciyi değiştiren çağrı.</param>
    /// <param name="from">Verilirse eski durum budur (çağrıdan önce okunmaz).</param>
    private void Track(Action change, InstagramAccountState? from = null)
    {
        from ??= Entry()?.State;
        change();

        if (from is not { } before || Entry() is not { } after || after.State == before) return;

        var account = AccountLabel(after.Username, after.PageName);
        if (after.RetryAt is { } retryAt)
        {
            _services.Log.LogInformation(
                "[InstagramChatHostedService] account {Account} (page {PageId}): {From} -> {To}, " +
                "retry at {RetryAt:HH:mm:ss}",
                account, _pageId, before, after.State,
                TimeZoneInfo.ConvertTime(retryAt, _services.Time.LocalTimeZone));
        }
        else
        {
            _services.Log.LogInformation(
                "[InstagramChatHostedService] account {Account} (page {PageId}): {From} -> {To}",
                account, _pageId, before, after.State);
        }
    }

    /// <summary>Bu Sayfanın sağlık girdisinin o anki kopyası; girdi yoksa null.</summary>
    private InstagramAccountHealth? Entry()
    {
        foreach (var entry in _services.Health.Snapshot())
        {
            if (string.Equals(entry.PageId, _pageId, StringComparison.Ordinal)) return entry;
        }

        return null;
    }

    /// <summary>Gösterime hazır hesap adı: <c>@kullanıcıadı</c>; kullanıcı adı bilinmiyorsa Sayfa
    /// adı ("@"sız). Kulvar etiketi ve günlük satırlarındaki hesap adı aynı kuralla yazılır.</summary>
    private static string AccountLabel(string? username, string pageName) =>
        string.IsNullOrWhiteSpace(username) ? pageName : "@" + username;

    /// <summary>
    /// Poller'a verilen tek gözlemci (döngü başına bir örnek; poller yeniden kurulunca aynısı
    /// verilir). Tur olayını önce sağlık izleyicisine, kulvar varsa sonra kulvara iletir; durum
    /// geçişi ve kota satırlarını yazar.
    /// </summary>
    private sealed class RoundObserver(InstagramAccountLoop loop) : IInstagramPollObserver
    {
        private readonly IInstagramPollObserver _health = loop._services.Health.ObserverFor(loop._pageId);

        /// <summary>Kota satırının son yazıldığı an (<see cref="TimeProvider.GetTimestamp"/>);
        /// henüz yazılmadıysa null. Döngüyle yaşar: poller yeniden kurulunca sıfırlanmaz.</summary>
        private long? _usageLoggedAt;

        /// <summary>Kalıcı hata turundan ÖNCEKİ durum; <see cref="TakeStateBeforeFatal"/> okuyana dek.</summary>
        private InstagramAccountState? _stateBeforeFatal;

        /// <summary>
        /// Son kalıcı hata turundan önceki durumu verir ve unutur; öyle bir tur olmadıysa null.
        /// Poller bittikten sonra çağrılır.
        /// </summary>
        public InstagramAccountState? TakeStateBeforeFatal()
        {
            var state = _stateBeforeFatal;
            _stateBeforeFatal = null;
            return state;
        }

        public void RoundStarting()
        {
            loop.Track(_health.RoundStarting);
            loop._lane?.RoundStarting();
        }

        public void RoundCompleted(
            InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader)
        {
            if (kind == InstagramPollKind.Fatal)
            {
                // Geçiş satırı burada YAZILMAZ: izleyici bu olayda yeniden deneme saatini
                // temizler, saati döngü poller bittikten sonra yazar. Satır o zaman, buradaki
                // eski durumla yazılır.
                _stateBeforeFatal = loop.Entry()?.State;
                _health.RoundCompleted(kind, message, retryAfter, usageHeader);
            }
            else
            {
                loop.Track(() => _health.RoundCompleted(kind, message, retryAfter, usageHeader));
            }

            loop._lane?.RoundCompleted(kind, message, retryAfter, usageHeader);

            // Geçici hatalı turda kota satırı yazılmaz: yanıt yoktur ya da başlığına güvenilmez.
            if (kind != InstagramPollKind.Transient) LogUsageIfDue(usageHeader);
        }

        /// <summary>
        /// Kota satırını yazar: ilk turda hemen, sonra en çok
        /// <see cref="InstagramChatTimings.UsageLogInterval"/>'da bir. Saniyede bir tur var; her
        /// turda yazmak günlüğü doldururdu.
        /// </summary>
        private void LogUsageIfDue(string? usageHeader)
        {
            var services = loop._services;
            if (_usageLoggedAt is { } loggedAt
                && services.Time.GetElapsedTime(loggedAt) < services.Timings.UsageLogInterval)
            {
                return;
            }

            if (loop.Entry() is not { } entry) return;

            _usageLoggedAt = services.Time.GetTimestamp();
            services.Log.LogInformation(
                "[InstagramChatHostedService] usage {Account} (page {PageId}): {Usage}",
                AccountLabel(entry.Username, entry.PageName), loop._pageId, DescribeUsage(usageHeader));
        }

        /// <summary>
        /// Kota başlığının günlük metni: kova başına işletme kimliği, üç oran (yüzde) ve erişimin
        /// geri gelmesine kalan dakika; kovalar "; " ile ayrılır. Sayılar kültürden bağımsız
        /// yazılır. Başlık yoksa ya da okunamıyorsa metin bunu söyler.
        /// </summary>
        private static string DescribeUsage(string? usageHeader)
        {
            if (usageHeader is null) return "X-Business-Use-Case-Usage header absent";
            if (!InstagramGraphError.TryGetUsage(usageHeader, out var usage))
                return "X-Business-Use-Case-Usage header unreadable";

            var buckets = new string[usage.Count];
            for (var i = 0; i < usage.Count; i++)
            {
                var bucket = usage[i];
                buckets[i] = string.Create(
                    CultureInfo.InvariantCulture,
                    $"business {bucket.BusinessId} call_count {bucket.CallCountPercent:0.##}% " +
                    $"total_cputime {bucket.TotalCpuTimePercent:0.##}% " +
                    $"total_time {bucket.TotalTimePercent:0.##}% " +
                    $"regain {(bucket.MinutesToRegainAccess ?? 0):0.##} min");
            }

            return string.Join("; ", buckets);
        }
    }
}
