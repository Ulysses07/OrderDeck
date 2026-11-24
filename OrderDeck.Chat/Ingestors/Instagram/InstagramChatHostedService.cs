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
///
/// <para><b>Oturum içinde değişen bağlantı.</b> Oturum açıkken bağlı Sayfa listesi
/// <see cref="WatchInterval"/>'da bir yeniden okunur: eklenen Sayfanın döngüsü başlar,
/// kaldırılanınki durur, ötekilere dokunulmaz (<see cref="RunSessionAsync"/>).</para>
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
    private readonly ChatReconnectSignal? _reconnect;

    /// <summary>Üst döngünün ve hesap döngülerinin uyandırılabilir beklemesi: yayın oturumu başlayınca
    /// ve "Sohbeti yenile" istenince bekleyen döngüler hemen yeniden dener.</summary>
    private readonly InstagramWake _wake = new();

    /// <summary>PageId → o Sayfanın çözümleyicisi. Servis ömrünce yaşar: başarılı çözümleme
    /// önbelleği oturumlar arasında korunur, oturum başına Graph isteği eklenmez. Yalnız üst
    /// döngü okur ve yazar.</summary>
    private readonly Dictionary<string, InstagramAccountResolver> _resolvers = new(StringComparer.Ordinal);

    /// <summary>Instagram hesabı (IgUserId) → onu yoklayan Sayfa (PageId). İki Sayfa aynı hesaba
    /// çözülürse yalnız ilk sahiplenen yoklar (çift yorum olmasın); kaydı hesap döngüleri alır
    /// ve çıkarken bırakır.</summary>
    private readonly ConcurrentDictionary<string, string> _owners = new(StringComparer.Ordinal);

    /// <summary>Sayfa (PageId) → o Sayfanın çözümleyicisine en son verilen Sayfa token'ı.
    /// Çözümleyicilerle aynı ömürde yaşar (oturumlar ve döngü örnekleri arasında korunur): token
    /// hangi yolda değişirse değişsin sonraki kurulumda hesap bir kez yeniden çözülür. Kaydı o
    /// Sayfanın hesap döngüsü okur ve yazar; hiçbir yere loglanmaz.</summary>
    private readonly ConcurrentDictionary<string, string> _resolvedTokens = new(StringComparer.Ordinal);

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
    /// <param name="reconnect">"Sohbeti yenile" sinyali; verilirse her istekte bekleyen döngüler
    /// uyandırılır.</param>
    public InstagramChatHostedService(
        FacebookOAuthService oauth,
        IChatBus bus,
        ILoggerFactory loggerFactory,
        SpamFilter? spamFilter = null,
        StreamSessionService? sessions = null,
        IHttpClientFactory? httpFactory = null,
        InstagramHealthMonitor? health = null,
        TimeProvider? time = null,
        ChatReconnectSignal? reconnect = null)
        : this(oauth, bus, loggerFactory, spamFilter, sessions, httpFactory, health, time,
            InstagramChatTimings.Default, reconnect)
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
        InstagramChatTimings timings,
        ChatReconnectSignal? reconnect = null)
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
        _reconnect = reconnect;

        if (_sessions is not null)
        {
            _sessions.SessionEnded += OnSessionEnded;
            _sessions.SessionStarted += OnSessionStarted;
        }

        if (_reconnect is not null)
            _reconnect.Requested += OnReconnectRequested;
    }

    private void OnSessionEnded(object? sender, SessionEndedEventArgs e)
    {
        // Operatör "Yayını Bitir" dedi → bütün hesapların yoklamasını hemen kes.
        try { _sessionCts?.Cancel(); } catch { /* ignore */ }
    }

    private void OnSessionStarted(object? sender, SessionStartedEventArgs e)
    {
        // "Yayın Başlat": üst döngü oturumu uyuyarak yokluyordu, sohbet bir dakikaya kadar
        // gecikiyordu. Bekleyen döngü hemen uyanır ve oturum kapısına yeniden bakar.
        _wake.Signal();
    }

    private void OnReconnectRequested(object? sender, EventArgs e)
    {
        // Çağıranı (arayüz) BEKLETMEZ: yalnız sinyal verir.
        if (_sessions is not null && _sessions.GetActive() is null)
        {
            _log.LogInformation("[ChatReconnect] instagram: oturum yok; yapılacak iş yok");
            return;
        }

        // Bekleyen hesap döngüleri (hesap okunamadı, hata sonrası bekleme, atlanmış Sayfa) hemen
        // yeniden dener. Sağlıklı çalışan okuyucu beklemede değildir: ona dokunulmaz, filigranı
        // korunur. Hız sınırı beklemesi okuyucunun içindedir ve kesilmez (kesmek sınırı uzatır).
        var woken = _wake.Signal();
        _log.LogInformation(
            "[ChatReconnect] instagram: bekleyen {Count} döngü uyandırıldı; çalışan okuyuculara dokunulmadı",
            woken);
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

            // Uyandırma sıra numarası kapılara BAKILMADAN önce okunur: oturum, kapı "oturum yok"
            // dedikten sonra ve bekleme başlamadan önce başlarsa sinyali kaçmaz, bekleme hemen döner.
            var epoch = _wake.Epoch;
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
                    await _wake.DelayAsync(_timings.IdleWhenOffline, _time, ct, epoch).ConfigureAwait(false);
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
                        await _wake.DelayAsync(_timings.IdleWhenOffline, _time, ct, epoch).ConfigureAwait(false);
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

                // Oturum sonrası bekleme de uyandırılabilir: yeni oturum onu beklemez. Sıra numarası
                // turun başındakidir; oturum toplanırken başlayan yeni oturum da böylece kaçmaz
                // (oturum içinde gelen bir yenileme bu beklemeyi atlatır; zararsız, döngü kapıya döner).
                await _wake.DelayAsync(_timings.IdleAfterPollerExit, _time, ct, epoch).ConfigureAwait(false);
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
    /// birleştirme kapısıyla), sonra oturum kaynağı iptal olana dek bağlı Sayfa listesini izler
    /// ve çalışan döngüleri listeye uydurur. Yayın ortasında eklenen hesap yeni oturumu, kaldırılan
    /// hesabın yoklaması oturum sonunu beklemez.
    ///
    /// <para><b>İzleme.</b> Liste <see cref="InstagramChatTimings.WatchInterval"/>'da bir yeniden
    /// okunur (bellek okuması; bağlantı yokken ek olarak token dosyasına bakılır, Graph'a istek
    /// atılmaz) ve Sayfa kümesi çalışan döngülerle karşılaştırılır. Küme aynıysa hiçbir şey
    /// yapılmaz: token'ı değişen Sayfanın sağlıklı poller'ı kurulduğu token'la sürer. Küme
    /// değiştiyse, sırayla:</para>
    /// <list type="number">
    /// <item>Listede olmayan Sayfaların döngüleri durdurulur ve BİTMELERİ beklenir; öbür
    /// hesapların poller'ına ve filigranına dokunulmaz.</item>
    /// <item>Liste boşaldıysa ("Bağlantıyı Kaldır") oturum sonuyla aynı sırayla kapı kapanır ve
    /// girdiler durur. Girdiler SİLİNMEZ: aynı Sayfa yeniden bağlanırsa "bu oturumda canlıydı"
    /// bilgisi durur, bozuk çıkan yeni bağlantı tek Sayfalı kurulumda da görünür. İzleme sürer
    /// (bağlantı kapısının uzun beklemesine düşülmez); liste dolunca kurulum, oturum sıfırlaması
    /// olmadan, baştan yapılır.</item>
    /// <item>Liste boş değilse sağlık girdileri güncel listeye uydurulur: kaldırılanın girdisi
    /// döngüsü bittikten SONRA silinir, eklenenin girdisi döngüsü başlamadan ÖNCE eklenir
    /// (izleyici girdisi olmayan Sayfanın bildirimini yok sayar; girdisiz başlayan hesap hiç
    /// görünmezdi). Sonra eklenen Sayfaların döngüleri başlar (<see cref="StartLoops"/>).</item>
    /// </list>
    ///
    /// <para><b>Oturum sonu sırası:</b> hesap döngülerinin BİTMESİ beklenir → kapı kapanır (özet
    /// satırı) → sağlık girdileri durur. Sıra bağlayıcıdır: sağlık izleyicisi geç gelen tur
    /// olayını durmuş girdide de işler; poller'lar bitmeden durdurulan girdi yeniden canlı
    /// görünürdü.</para>
    /// </summary>
    /// <param name="pages">Oturum başında bağlı Sayfalar, birincil önce; boş değil. Sağlık girdileri
    /// bu listeyle yapılandırılmıştır.</param>
    /// <param name="session">Oturum belirteci: oturum bitince ya da servis durunca iptal olur.</param>
    private async Task RunSessionAsync(IReadOnlyList<FacebookLinkedPage> pages, CancellationToken session)
    {
        var services = new InstagramLoopServices(
            _oauth, _bus, _loggerFactory, _log, _spamFilter, _httpFactory, _health, _time, _timings, _wake);

        // Sayfa (PageId) → o Sayfanın hesap döngüsü.
        var loops = new Dictionary<string, RunningLoop>(StringComparer.Ordinal);
        InstagramMergeGate? gate = null;
        try
        {
            StartLoops(pages, pages, loops, ref gate, services, session);

            while (true)
            {
                await Task.Delay(_timings.WatchInterval, _time, session).ConfigureAwait(false);
                var current = await _oauth.GetConnectedPagesAsync(session).ConfigureAwait(false);

                // Önce çıkarmalar. Kendi kendine bitmiş döngü de (1. adımında Sayfasını bağlı
                // bulamadı) çıkarılır: çalışan bir döngü değildir. Sayfası o arada yeniden
                // bağlandıysa aşağıda eklenen sayılır ve yeni bir döngüyle başlar; sözlükte bitmiş
                // hâliyle kalsaydı Sayfa oturum sonuna dek yoklanmazdı.
                List<string>? removed = null;
                foreach (var (pageId, running) in loops)
                {
                    if (running.Completion.IsCompleted || !IsListed(current, pageId))
                        (removed ??= []).Add(pageId);
                }

                if (removed is not null)
                    await StopLoopsAsync(loops, removed).ConfigureAwait(false);

                List<FacebookLinkedPage>? added = null;
                foreach (var page in current)
                {
                    if (!loops.ContainsKey(page.PageId))
                        (added ??= []).Add(page);
                }

                // Küme aynı: yapılacak iş yok (Sayfanın token'ı değişmiş olsa da).
                if (removed is null && added is null) continue;

                if (current.Count == 0)
                {
                    // Liste boşaldı; bütün döngüler yukarıda durdu. Girdiler silinmez, yalnız durur.
                    gate?.Dispose();
                    gate = null;
                    _health.StopAll();
                    continue;
                }

                // Çıkarmalar bitti, yeni döngüler henüz başlamadı: girdiler tam burada uydurulur.
                _health.Configure(current);
                if (added is not null)
                    StartLoops(current, added, loops, ref gate, services, session);
            }
        }
        catch (OperationCanceledException) when (session.IsCancellationRequested)
        {
            // Oturum bitti ya da servis duruyor: aşağıda toplanır.
        }
        finally
        {
            try
            {
                // Oturum iptalinde döngüler zaten duruyordur (kaynakları oturum kaynağına bağlı);
                // izleme beklenmeyen bir istisnayla çıktıysa burada durdurulurlar.
                await StopLoopsAsync(loops, [.. loops.Keys]).ConfigureAwait(false);
            }
            finally
            {
                gate?.Dispose();
                _health.StopAll();
            }
        }
    }

    /// <summary>
    /// Eklenen Sayfaların hesap döngülerini başlatır; gerekiyorsa önce birleştirme kapısını kurar.
    /// Her döngünün kendi iptal kaynağı vardır (oturum kaynağına bağlı): Sayfa kaldırılınca yalnız
    /// o döngü durur.
    ///
    /// <para><b>Kapı.</b> Ölçüt bağlı Sayfa sayısıdır (iki ve üzeri), çözülmüş Instagram hesabı
    /// sayısı değil: Sayfa sayısı hemen bellidir, hesap çözümlemesi döngünün içinde ve gecikmeli
    /// olur. Kapı kurulu değilken sayı ikiye ulaşırsa kapı kurulur ve ÇALIŞAN her döngüye takılır
    /// (<see cref="InstagramAccountLoop.AttachGate"/>: tekten çoka geçişte çalışan hesap kulvara
    /// filigranıyla ve beklemeden taşınır); oturum başında çalışan döngü yoktur. Kapı kuruluyken
    /// gelen ekleme çalışan döngülere dokunmaz. Kapı, Sayfa sayısı bire inse de liste boşalana ya
    /// da oturum bitene dek yaşar: kalan hesap kulvarıyla, etiketli sürer (tek kulvarlı kapı
    /// gecikme eklemez); tek hesap kipine sonraki oturumda dönülür.</para>
    /// </summary>
    /// <param name="current">Bağlı Sayfaların güncel listesi; boş değil.</param>
    /// <param name="added">Döngüsü başlatılacak Sayfalar: <paramref name="current"/>'ta olup
    /// çalışan döngüsü olmayanlar.</param>
    /// <param name="loops">Oturumun çalışan döngüleri; başlatılanlar eklenir.</param>
    /// <param name="gate">Oturumun kapısı; kurulu değilse null. Burada kurulursa atanır.</param>
    /// <param name="services">Döngülerin ortak servisleri.</param>
    /// <param name="session">Oturum belirteci.</param>
    private void StartLoops(
        IReadOnlyList<FacebookLinkedPage> current,
        IReadOnlyList<FacebookLinkedPage> added,
        Dictionary<string, RunningLoop> loops,
        ref InstagramMergeGate? gate,
        InstagramLoopServices services,
        CancellationToken session)
    {
        // Çözümleyiciler kapıdan ve döngülerden ÖNCE hazırlanır: istemci alınamazsa geride
        // yarım bir kurulum kalmaz.
        var resolvers = new InstagramAccountResolver[added.Count];
        for (var i = 0; i < added.Count; i++)
            resolvers[i] = ResolverFor(added[i].PageId);

        if (gate is null && current.Count >= 2)
        {
            gate = new InstagramMergeGate(_bus, _time, _loggerFactory.CreateLogger<InstagramMergeGate>());
            foreach (var running in loops.Values)
                running.Loop.AttachGate(gate);
        }

        for (var i = 0; i < added.Count; i++)
        {
            var loop = new InstagramAccountLoop(
                added[i].PageId, services, resolvers[i], _owners, _resolvedTokens, gate);
            var stop = CancellationTokenSource.CreateLinkedTokenSource(session);
            var ct = stop.Token;
            loops.Add(
                added[i].PageId,
                new RunningLoop(loop, stop, Task.Run(() => loop.RunAsync(ct), CancellationToken.None)));
        }
    }

    /// <summary>
    /// Verilen Sayfaların döngülerini durdurur, BİTMELERİNİ bekler ve sözlükten çıkarır. Dönüşte o
    /// hesapların poller'ı durmuş, kulvarı kapanmış, sahipliği bırakılmıştır. Sağlık girdilerine
    /// (silme, durdurma) ancak bundan sonra dokunulur: izleyici geç gelen tur olayını durmuş
    /// girdide de işler.
    /// </summary>
    private static async Task StopLoopsAsync(Dictionary<string, RunningLoop> loops, IReadOnlyList<string> pageIds)
    {
        var stopping = new Task[pageIds.Count];
        for (var i = 0; i < pageIds.Count; i++)
        {
            var running = loops[pageIds[i]];
            try { running.Stop.Cancel(); } catch { /* ignore */ }
            stopping[i] = running.Completion;
        }

        await Task.WhenAll(stopping).ConfigureAwait(false);

        foreach (var pageId in pageIds)
        {
            if (loops.Remove(pageId, out var stopped))
                stopped.Stop.Dispose();
        }
    }

    private static bool IsListed(IReadOnlyList<FacebookLinkedPage> pages, string pageId)
    {
        foreach (var page in pages)
        {
            if (string.Equals(page.PageId, pageId, StringComparison.Ordinal)) return true;
        }

        return false;
    }

    /// <summary>Oturumda başlatılmış bir hesap döngüsü.</summary>
    /// <param name="Loop">Döngü.</param>
    /// <param name="Stop">Yalnız bu döngüyü durduran iptal kaynağı; oturum kaynağına bağlıdır.</param>
    /// <param name="Completion">Döngü bitince tamamlanır; fırlatmaz.</param>
    private sealed record RunningLoop(InstagramAccountLoop Loop, CancellationTokenSource Stop, Task Completion);

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
        {
            _sessions.SessionEnded -= OnSessionEnded;
            _sessions.SessionStarted -= OnSessionStarted;
        }

        if (_reconnect is not null)
            _reconnect.Requested -= OnReconnectRequested;

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
/// <param name="IdleAfterPollerExit">Poller kalıcı hata olmadan bittikten, hesap döngüsü ya da üst
/// döngü beklenmeyen bir istisna yakaladıktan ve oturum toplandıktan sonraki bekleme.</param>
/// <param name="IdleAfterFatal">Kalıcı hatadan, çözülemeyen hesaptan ve başka bir Sayfanın
/// yokladığı hesaptan sonra hesabın yeniden denenmesine kadarki bekleme. İlk ikisi Sayfanın
/// saklı token'ı değişince beklenmez.</param>
/// <param name="WatchInterval">Oturum açıkken bağlı Sayfa listesinin izlenme aralığı; token'a
/// bakan beklemelerin saklı token'ı yeniden okuma adımı.</param>
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
