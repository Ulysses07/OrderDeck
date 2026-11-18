using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

namespace OrderDeck.Chat.Ingestors.Instagram;

/// <summary>
/// Instagram canlı yorum ingestor'ının yaşam döngüsü.
/// <see cref="OrderDeck.Chat.Ingestors.Facebook.FacebookChatHostedService"/>
/// ile aynı şekil; tek farkı <b>kalıcı hatada</b> (token/izin) yeniden
/// denemek yerine uzun idle'a düşmesi — sonsuz retry kullanıcının token'ını
/// düzeltmiyor, sadece log dolduruyor.
///
/// <para>Ayrı OAuth yok: bağlı Facebook Sayfa token'ına biniyoruz.</para>
///
/// <para><b>Üst döngü + hesap başına döngü.</b> Bağlantı ve oturum kapıları yalnız üst
/// döngüdedir; oturum açıkken bağlı her Sayfa kendi <see cref="InstagramAccountLoop"/>'unda
/// yoklanır ve bütün hesaplar birlikte başlar. Çöküş sayacı ve beklemeler hesap başınadır: bir
/// hesabın kalıcı hatası, hız sınırı ya da çökmesi öbürünün sohbetini kesmez. İki ve üzeri
/// Sayfada poller'lar <see cref="InstagramMergeGate"/> kulvarlarına yazar (mesaj kaynak
/// hesabıyla etiketlenir); tek Sayfada kapı kurulmaz, poller doğrudan sohbet bus'ına yazar.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class InstagramChatHostedService : IHostedService, IDisposable
{
    // Üretim süreleri: InstagramChatTimings.Default bunlardan kurulur. Servisin kendi kodu
    // bunları DEĞİL _timings'i okur (testler kısasını verir); doğrudan okuyan tek yer saf
    // ComputeBackoff'tur.
    internal static readonly TimeSpan IdleWhenOffline = TimeSpan.FromSeconds(60);
    internal static readonly TimeSpan IdleAfterPollerExit = TimeSpan.FromSeconds(30);
    internal static readonly TimeSpan IdleAfterFatal = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(5);
    internal static readonly TimeSpan WatchInterval = TimeSpan.FromSeconds(2);
    internal static readonly TimeSpan UsageLogInterval = TimeSpan.FromMinutes(1);

    public const string HttpClientName = "instagram-graph";

    private readonly FacebookOAuthService _oauth;
    private readonly IChatBus _bus;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<InstagramChatHostedService> _log;
    private readonly SpamFilter? _spamFilter;
    private readonly StreamSessionService? _sessions;
    private readonly IHttpClientFactory? _httpFactory;
    private readonly InstagramHealthMonitor _health;
    private readonly TimeProvider _time;
    private readonly InstagramChatTimings _timings;

    /// <summary>PageId → o Sayfanın çözümleyicisi. Servis ömrünce yaşar: başarılı çözümleme
    /// önbelleği oturumlar arasında korunur, oturum başına Graph isteği eklenmez. Yalnız üst
    /// döngü okur ve yazar.</summary>
    private readonly Dictionary<string, InstagramAccountResolver> _resolvers = new(StringComparer.Ordinal);

    /// <summary>Instagram hesabı (IgUserId) → onu yoklayan Sayfa (PageId). İki Sayfa aynı hesaba
    /// çözülürse yalnız ilk sahiplenen yoklar (çift yorum olmasın); kaydı hesap döngüleri alır
    /// ve çıkarken bırakır.</summary>
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    private CancellationTokenSource? _cts;
    private Task? _runner;

    /// <summary>Üst döngünün o turdaki oturum kaynağı; turlar arasında null.</summary>
    private volatile CancellationTokenSource? _sessionCts;

    /// <summary>Sağlık izleyicisinin en son sıfırlandığı oturum. Yalnız üst döngü okur ve yazar.</summary>
    private string? _lastSessionId;

    /// <param name="health">Hesap başına sağlığın yazıldığı izleyici. Arayüz AYNI örneği okur;
    /// verilmezse servis kendi izleyicisini kurar (durum yine tutulur, yalnız okuyan olmaz).</param>
    /// <param name="time">Saat kaynağı (beklemeler, yeniden deneme saatleri, kota satırının
    /// aralığı); verilmezse sistem saati.</param>
    public InstagramChatHostedService(
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null,
        InstagramHealthMonitor? health = null,
        TimeProvider? time = null)
        : this(oauth, bus, loggerFactory, spamFilter, sessions, httpFactory, health, time,
            InstagramChatTimings.Default)
    {
    }

    /// <summary>Test girişi: bekleme süreleri dışarıdan verilir.</summary>
    internal InstagramChatHostedService(
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        SpamFilter? spamFilter,
        StreamSessionService? sessions,
        IHttpClientFactory? httpFactory,
        InstagramHealthMonitor? health,
        TimeProvider? time,
        InstagramChatTimings timings)
    {
        _oauth = oauth;
        _bus = bus;
        _loggerFactory = loggerFactory;
        _log = loggerFactory.CreateLogger<InstagramChatHostedService>();
        _spamFilter = spamFilter;
        _sessions = sessions;
        _httpFactory = httpFactory;
        _time = time ?? TimeProvider.System;
        _health = health ?? new InstagramHealthMonitor(_time);
        _timings = timings;

        if (_sessions is not null)
            _sessions.SessionEnded += OnSessionEnded;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operatör "Yayını Bitir" dedi → bütün hesapların yoklamasını hemen kes.
        try { _sessionCts?.Cancel(); } catch { /* ignore */ }
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
                _log.LogDebug(ex, "[InstagramChatHostedService] stop wait swallowed");
            }
        }
    }

    /// <summary>
    /// Üst döngü: bağlantı kapısı → oturum kapısı → oturum (<see cref="RunSessionAsync"/>) →
    /// kısa bekleme → başa. Servis durdurulunca sürmekte olan oturum toplanır ve döngü son
    /// beklemeye girmeden çıkar; <see cref="StopAsync"/> bunu bekler.
    /// </summary>
    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Oturum kaynağı oturum kapısı okunmadan ÖNCE kurulur. Kapı "oturum açık" dedikten
            // sonra, hesap döngüleri daha başlamadan gelen SessionEnded de bu kaynağı iptal eder;
            // kaynak sonradan kurulsaydı o olay kaçar, poller'lar bitmiş oturumda sürerdi.
            using var session = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _sessionCts = session;
            try
            {
                // Kip kapısı ve trial kapısı YOK: uzantıdan Instagram kaldırıldığı
                // için resmi API tek yol. Kapatılabilir olması sohbetin sessizce
                // susması demekti, açık kalması ise çift-post riski taşımıyor.

                // 1) Facebook Sayfa bağlantısı yoksa IG'ye de erişemeyiz.
                var pages = await _oauth.GetConnectedPagesAsync(ct).ConfigureAwait(false);
                if (pages.Count == 0)
                {
                    _health.Clear();
                    await Task.Delay(_timings.IdleWhenOffline, _time, ct).ConfigureAwait(false);
                    continue;
                }

                // 2) Operatör "Yayın Başlat"a basmadıysa Graph'a dokunma. Oturum kapısı yalnız
                // burada durur (hesap döngüleri ayrıca beklemez): bütün hesaplar birlikte başlar.
                // Oturum servisi verilmediyse oturum hep açıktır.
                StreamSession? active = null;
                if (_sessions is not null)
                {
                    active = _sessions.GetActive();
                    if (active is null)
                    {
                        await Task.Delay(_timings.IdleWhenOffline, _time, ct).ConfigureAwait(false);
                        continue;
                    }
                }

                // 3) Oturum açık: sağlık girdileri güncel Sayfa listesine uydurulur; oturum yeni
                // görüldüyse oturum durumu ("bu oturumda canlıydı", son temas) sıfırlanır.
                _health.Configure(pages);
                if (active is not null && !string.Equals(active.Id, _lastSessionId, StringComparison.Ordinal))
                {
                    _lastSessionId = active.Id;
                    _health.ResetSession();
                }

                await RunSessionAsync(pages, session.Token).ConfigureAwait(false);

                if (ct.IsCancellationRequested) break;

                await Task.Delay(_timings.IdleAfterPollerExit, _time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogError(ex,
                    "[InstagramChatHostedService] outer loop error; sleeping before retry");
                try { await Task.Delay(_timings.IdleAfterPollerExit, _time, ct).ConfigureAwait(false); }
                catch { break; }
            }
            finally
            {
                _sessionCts = null;
            }
        }
    }

    /// <summary>
    /// Bir oturum: bağlı her Sayfa için bir hesap döngüsü başlatır (iki ve üzeri Sayfada
    /// birleştirme kapısıyla) ve oturum kaynağı iptal olana dek bekler. Sayfa listesi oturum
    /// başında okunur.
    ///
    /// <para><b>Oturum sonu sırası:</b> hesap döngülerinin BİTMESİ beklenir → kapı kapanır (özet
    /// satırı) → sağlık girdileri durur. Sıra bağlayıcıdır: sağlık izleyicisi geç gelen tur
    /// olayını durmuş girdide de işler; poller'lar bitmeden durdurulan girdi yeniden canlı
    /// görünürdü.</para>
    /// </summary>
    /// <param name="pages">Bağlı Sayfalar, birincil önce; boş değil.</param>
    /// <param name="session">Oturum belirteci: oturum bitince ya da servis durunca iptal olur.</param>
    private async Task RunSessionAsync(IReadOnlyList<FacebookLinkedPage> pages, CancellationToken session)
    {
        // Çözümleyiciler kapıdan ve döngülerden ÖNCE hazırlanır: istemci alınamazsa geride
        // toplanacak bir şey kalmaz.
        var resolvers = new InstagramAccountResolver[pages.Count];
        for (var i = 0; i < pages.Count; i++)
            resolvers[i] = ResolverFor(pages[i].PageId);

        var services = new InstagramLoopServices(
            _oauth, _bus, _loggerFactory, _log, _spamFilter, _httpFactory, _health, _time, _timings);

        // Ölçüt bağlı Sayfa sayısıdır, çözülmüş Instagram hesabı sayısı değil: Sayfa sayısı
        // oturum başında bellidir, hesap çözümlemesi döngünün içinde ve gecikmeli olur.
        var gate = pages.Count >= 2
            ? new InstagramMergeGate(_bus, _time, _loggerFactory.CreateLogger<InstagramMergeGate>())
            : null;

        var loops = new List<Task>(pages.Count);
        try
        {
            for (var i = 0; i < pages.Count; i++)
            {
                var loop = new InstagramAccountLoop(pages[i].PageId, services, resolvers[i], _owners, gate);
                loops.Add(Task.Run(() => loop.RunAsync(session), CancellationToken.None));
            }

            await Task.Delay(Timeout.InfiniteTimeSpan, _time, session).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Oturum bitti ya da servis duruyor: aşağıda toplanır.
        }
        finally
        {
            try
            {
                await Task.WhenAll(loops).ConfigureAwait(false);
            }
            finally
            {
                gate?.Dispose();
                _health.StopAll();
            }
        }
    }

    /// <summary>O Sayfanın çözümleyicisi; ilk istekte kurulur (HttpClient'ı da o an alınır),
    /// sonra hep aynı örnek döner.</summary>
    private InstagramAccountResolver ResolverFor(string pageId)
    {
        if (_resolvers.TryGetValue(pageId, out var resolver)) return resolver;

        var http = _httpFactory?.CreateClient(HttpClientName)
                   ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        resolver = new InstagramAccountResolver(
            http, _loggerFactory.CreateLogger<InstagramAccountResolver>());
        _resolvers[pageId] = resolver;
        return resolver;
    }

    /// <summary>30s × 2^(n-1), 5dk tavan. Facebook/YouTube ile aynı eğri.</summary>
    internal static TimeSpan ComputeBackoff(int consecutiveCrashes)
    {
        if (consecutiveCrashes <= 1) return IdleAfterPollerExit;
        var exp = Math.Min(consecutiveCrashes - 1, 10);
        var seconds = IdleAfterPollerExit.TotalSeconds * Math.Pow(2, exp);
        if (seconds >= MaxBackoff.TotalSeconds) return MaxBackoff;
        return TimeSpan.FromSeconds(seconds);
    }

    public void Dispose()
    {
        if (_sessions is not null)
            _sessions.SessionEnded -= OnSessionEnded;
        try { _cts?.Cancel(); } catch { /* ignore */ }
        _cts?.Dispose();
    }
}

/// <summary>
/// <see cref="InstagramChatHostedService"/>'in bekleme süreleri. Üretim <see cref="Default"/> ile
/// koşar; testler kısasını servisin iç kurucusundan verir (poller'ın kendi aralıkları buraya
/// dahil değildir).
/// </summary>
/// <param name="IdleWhenOffline">Bağlı Sayfa ya da etkin oturum yokken üst döngünün beklemesi.</param>
/// <param name="IdleAfterPollerExit">Poller kalıcı hata olmadan bittikten, hesap döngüsü
/// beklenmeyen bir istisna yakaladıktan ve oturum toplandıktan sonraki bekleme.</param>
/// <param name="IdleAfterFatal">Kalıcı hatadan, çözülemeyen hesaptan ve başka bir Sayfanın
/// yokladığı hesaptan sonra hesabın yeniden denenmesine kadarki bekleme.</param>
/// <param name="WatchInterval">Oturum açıkken bağlı Sayfa listesinin izlenme aralığı.</param>
/// <param name="UsageLogInterval">Hesap başına kota günlük satırının en sık yazılma aralığı.</param>
[SupportedOSPlatform("windows")] // Default, yalnız Windows'ta desteklenen servisin sabitlerinden kurulur.
internal sealed record InstagramChatTimings(
    TimeSpan IdleWhenOffline,
    TimeSpan IdleAfterPollerExit,
    TimeSpan IdleAfterFatal,
    TimeSpan WatchInterval,
    TimeSpan UsageLogInterval)
{
    /// <summary>Üretim süreleri: 60 sn / 30 sn / 5 dk / 2 sn / 1 dk.</summary>
    public static InstagramChatTimings Default { get; } = new(
        InstagramChatHostedService.IdleWhenOffline,
        InstagramChatHostedService.IdleAfterPollerExit,
        InstagramChatHostedService.IdleAfterFatal,
        InstagramChatHostedService.WatchInterval,
        InstagramChatHostedService.UsageLogInterval);
}
