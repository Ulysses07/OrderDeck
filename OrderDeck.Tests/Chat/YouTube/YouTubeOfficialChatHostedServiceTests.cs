using System.Collections.Concurrent;
using System.Diagnostics;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using OrderDeck.Chat.Ingestors.YouTube;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Core.Chat.ChatSourceState;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// YouTube sohbet servisinin dış döngüsü: erken bağlanma, yenileme ve durum bildirimi (tasarım:
/// Sohbeti yenile §5.2, §5.3, §5.4).
///
/// NEDEN: bu döngünün hiç testi yoktu ve operatörün şikâyeti tam burada: yayın düşünce sohbet,
/// uygulamadaki yayın oturumu bitirilip yeniden açılmadan geri gelmiyordu; oturum başladıktan sonra
/// da 60 sn'ye kadar gecikiyordu. Ağ yok: okuyucu sahte, yayın araması betikli. Saat elle ilerler
/// ama zamanlayıcı sürmez; bekleyen her doğrulama koşulu yoklar (üst sınır
/// <see cref="TestPatience.Upper"/>). "Olmaması gereken" beklemeler <see cref="Long"/>'dur: dolarsa
/// test süre aşımıyla düşer.
/// </summary>
public sealed class YouTubeOfficialChatHostedServiceTests : IAsyncLifetime
{
    private const string StatePrefix = "[YouTube Official] durum: ";
    private const string ReconnectPrefix = "[ChatReconnect] youtube: ";

    /// <summary>Dolması beklenmeyen süre: "uyandırıldı / kesildi" ile "süre doldu"yu ayırır.</summary>
    private static readonly TimeSpan Long = TestPatience.Upper * 4;

    private static readonly YouTubeChatTimings Fast = new(
        IdleWhenOffline: Long,
        IdleAfterExit: TimeSpan.FromMilliseconds(30),
        FastResolveInterval: TimeSpan.FromMilliseconds(30),
        FastResolveWindow: TimeSpan.FromMinutes(2));

    private readonly ManualTime _time = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly InMemorySqlite _db = new();
    private readonly StreamSessionService _sessions;
    private readonly ChatSourceMonitor _sources;
    private readonly ChatReconnectSignal _reconnect = new();
    private readonly Lookups _lookups = new();
    private readonly TrialSwitch _trial = new();
    private readonly ConcurrentQueue<FakeIngestor> _ingestors = new();
    private readonly AppSettings _settings = new()
    {
        YouTubeChannelHandle = $"@kanal{Guid.NewGuid():N}",
        YouTubeApiKey = $"anahtar-{Guid.NewGuid():N}",
    };

    /// <summary>Kurulan her okuyucunun <c>StartAsync</c>'te fırlatacağı istisna; null ise başlar.</summary>
    private volatile Func<Exception?> _startError = () => null;

    /// <summary>
    /// Servis ayarları bir SONRAKİ okuyuşunda (her turun ilk işi, döngünün iş parçacığında) bir kez
    /// çalışır. "Olay tam tur başlarken geldi" sıralarını zamana güvenmeden kurmak için.
    /// </summary>
    private Action? _onNextSettingsRead;

    private YouTubeOfficialChatHostedService? _service;

    public YouTubeOfficialChatHostedServiceTests()
    {
        new MigrationRunner(_db).Run();
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        _sessions = new StreamSessionService(new SessionRepository(_db), clock.Object);

        // Servisle AYNI saat: yeniden deneme saatleri "şimdi + süre" diye birebir doğrulanır.
        _sources = new ChatSourceMonitor(_time);
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_service is not null)
        {
            await _service.StopAsync(CancellationToken.None);
            _service.Dispose();
        }

        _db.Dispose();
    }

    // ── Sahte okuyucu ve betikli arama ──────────────────────────────────────────────────────────

    private sealed class TrialSwitch : ITrialModeProbe
    {
        private volatile bool _isTrialMode;

        public bool IsTrialMode
        {
            get => _isTrialMode;
            set => _isTrialMode = value;
        }
    }

    /// <summary>Ağa çıkmayan okuyucu: test onu kendiliğinden çıkarır (<see cref="End"/>).</summary>
    private sealed class FakeIngestor(YouTubeIngestorRequest request, Exception? startError) : IYouTubeChatIngestor
    {
        private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private volatile bool _started;
        private volatile bool _stopped;
        private volatile bool _disposed;
        private volatile int _endReason = (int)YouTubeIngestorEnd.Cancelled;

        public YouTubeIngestorRequest Request { get; } = request;

        public bool Stopped => _stopped;

        public bool Disposed => _disposed;

        /// <summary>Okuyucu başladı ve durdurulmadı.</summary>
        public bool Running => _started && !_stopped;

        public Task Completion => _completion.Task;

        public YouTubeIngestorEnd EndReason => (YouTubeIngestorEnd)_endReason;

        public Task StartAsync(CancellationToken ct)
        {
            if (ct.IsCancellationRequested) return Task.FromCanceled(ct);
            if (startError is not null) return Task.FromException(startError);
            _started = true;
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct)
        {
            _stopped = true;
            _completion.TrySetResult();
            return Task.CompletedTask;
        }

        /// <summary>Okuyucu kendiliğinden çıkar: yayın bitti ya da akış koptu.</summary>
        public void End(YouTubeIngestorEnd reason)
        {
            _endReason = (int)reason;
            _completion.TrySetResult();
        }

        public void Dispose() => _disposed = true;
    }

    /// <summary>Betikli yayın araması: her çağrıyı (ve <c>fresh</c> bayrağını) kaydeder.</summary>
    private sealed class Lookups
    {
        private readonly object _gate = new();
        private readonly List<bool> _calls = new();
        private volatile bool _hang;
        private int _cancelled;
        private BroadcastLookup _result = Found();

        /// <summary>Sıradaki aramaların cevabı.</summary>
        public BroadcastLookup Result
        {
            get { lock (_gate) return _result; }
            set { lock (_gate) _result = value; }
        }

        /// <summary>Doğruyken başlayan arama, iptal edilene dek dönmez (süren ağ çağrısı).</summary>
        public bool Hang
        {
            get => _hang;
            set => _hang = value;
        }

        /// <summary>İptalle kesilen arama sayısı.</summary>
        public int Cancelled => Volatile.Read(ref _cancelled);

        /// <summary>Yapılan aramaların <c>fresh</c> bayrakları, çağrı sırasıyla.</summary>
        public IReadOnlyList<bool> Calls
        {
            get { lock (_gate) return _calls.ToArray(); }
        }

        public async Task<BroadcastLookup> Run(bool fresh, CancellationToken ct)
        {
            lock (_gate) _calls.Add(fresh);
            if (Hang)
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    Interlocked.Increment(ref _cancelled);
                    throw;
                }
            }

            return Result;
        }
    }

    private static BroadcastLookup Found() => new(BroadcastLookupStatus.Found, NewVideoId(), Guid.NewGuid().ToString("N"));

    /// <summary>Video kimliği biçiminde (11 karakter) üretilmiş değer.</summary>
    private static string NewVideoId() => Guid.NewGuid().ToString("N")[..11];

    private IYouTubeChatIngestor Create(YouTubeIngestorRequest request)
    {
        var ingestor = new FakeIngestor(request, _startError());
        _ingestors.Enqueue(ingestor);
        return ingestor;
    }

    // ── Düzenek ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Servisi iç kurucuyla (kısa süreler, sahte okuyucu) kurup başlatır. Oturum AÇMAZ.</summary>
    private async Task RunAsync(YouTubeChatTimings? timings = null)
    {
        _service = new YouTubeOfficialChatHostedService(
            ReadSettings, new ChatBus(ringBufferSize: 10), _logs, _trial, spamFilter: null, _sessions,
            httpFactory: null, viewers: null, _lookups.Run, _reconnect, _sources, _time,
            timings ?? Fast, Create);
        await _service.StartAsync(CancellationToken.None);
    }

    private AppSettings ReadSettings()
    {
        Interlocked.Exchange(ref _onNextSettingsRead, null)?.Invoke();
        return _settings;
    }

    /// <summary>
    /// "Oturum satırı yazıldı ama servis olayı henüz görmedi" aralığını kurar: ilk tur ayarları okurken
    /// tutulur, oturum satırı yazılınca bırakılır; servisin dinleyicisi ancak <paramref name="turnDid"/>
    /// sağlandıktan sonra çalışır. Servis kurulmadan ÖNCE çağrılır: buradaki dinleyici önce abone olur,
    /// olayda servisinkinden önce çalışır.
    /// </summary>
    /// <returns>İlk tur ayarları okumaya başladığında tamamlanan görev.</returns>
    private Task TurnSeesTheSessionBeforeTheServiceHearsOfIt(Func<bool> turnDid)
    {
        var turnStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sessionWritten = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _onNextSettingsRead = () =>
        {
            turnStarted.TrySetResult();
            sessionWritten.Task.Wait(TestPatience.Upper);
        };
        _sessions.SessionStarted += (_, _) =>
        {
            sessionWritten.TrySetResult();
            SpinWait.SpinUntil(turnDid, TestPatience.Upper);
        };
        return turnStarted.Task;
    }

    /// <summary>
    /// Servisi başlatır, döngü oturumsuz beklemeye girince oturumu açar (oturum başlangıcı hızlı arama
    /// penceresini açar). Beklemek şart: sahada da operatör "Yayın Başlat"a bastığında döngü uykudadır.
    /// Beklenmezse ilk tur ile oturum başlangıcı yarışır ve ilk bildirimin "Stopped" olacağı kesin olmaz.
    /// O yarışın kendisi ayrıca, sırası zorlanarak sınanır ("Olay, turla aynı anda gelirse").
    /// </summary>
    private async Task<string> RunInSessionAsync(YouTubeChatTimings? timings = null)
    {
        await RunAsync(timings);
        await Until(() => State == Stopped);
        return StartSession();
    }

    private string StartSession() => _sessions.Start("Test", ["youtube"]).Id;

    private void EndSession(string id) => _sessions.End(id);

    private ChatSourceHealth? Health => _sources.Snapshot().SingleOrDefault(h => h.Platform == "youtube");

    private ChatSourceState? State => Health?.State;

    private int RunningIngestors => _ingestors.Count(i => i.Running);

    /// <summary>Servisin bildirdiği durum DEĞİŞİMLERİ, sırayla (günlükten): "Connecting", "Live"…</summary>
    private IReadOnlyList<string> Transitions() => Lines(StatePrefix).Select(l => l[StatePrefix.Length..]).ToList();

    private IReadOnlyList<string> Lines(string prefix) => _logs.Entries
        .Where(e => e.Level >= LogLevel.Information && e.Message.StartsWith(prefix, StringComparison.Ordinal))
        .Select(e => e.Message)
        .ToList();

    /// <summary>Koşul sağlanana dek 20 ms'de bir yoklar; <see cref="TestPatience.Upper"/> dolarsa düşer.</summary>
    private static async Task Until(Func<bool> condition)
    {
        var waited = Stopwatch.StartNew();
        while (!condition())
        {
            if (waited.Elapsed > TestPatience.Upper)
                throw new TimeoutException($"koşul {TestPatience.Upper.TotalSeconds} sn'de sağlanmadı");
            await Task.Delay(20);
        }
    }

    /// <summary>"Bir şey OLMUYOR" denetimleri için kısa, sabit bekleme: hızlı aralığın (30 ms) on katı.</summary>
    private static Task Settle() => Task.Delay(300);

    // ── Erken bağlanma ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_start_wakes_the_idle_loop_and_connects_without_waiting()
    {
        await RunAsync(); // oturum yok: döngü uzun beklemede
        await Until(() => State == Stopped);

        StartSession();

        await Until(() => State == Live); // uzun bekleme dolmadan: oturum başlangıcı döngüyü uyandırdı
        _lookups.Calls.Should().Equal(false);
        Transitions().Should().Equal("Stopped", "Connecting", "Live");
    }

    [Fact]
    public async Task No_broadcast_is_retried_at_the_fast_interval_inside_the_window_then_at_the_offline_interval()
    {
        // Operatör önce uygulamada "Yayın Başlat"a basıp sonra platformda yayını açar: tek uyandırma
        // yetmez, iki dakika boyunca sık aranır.
        _lookups.Result = new BroadcastLookup(BroadcastLookupStatus.NoBroadcast);
        await RunInSessionAsync();

        await Until(() => _lookups.Calls.Count >= 4); // uzun aralıkla dördüncü aramaya varılamazdı
        State.Should().Be(NoBroadcast);

        var beforeClose = _lookups.Calls.Count;
        _time.Advance(Fast.FastResolveWindow + TimeSpan.FromSeconds(1)); // pencere kapandı
        await Task.Delay(1000); // hızlı aralık sürseydi bu sürede otuz kadar arama daha olurdu

        // Sınır zamana bağlı değil: sayı okunurken süren bir arama ile o an beklenen kısa aralığın
        // sonundaki arama, en çok iki arama daha getirir; ondan sonraki bekleme uzun aralıktır.
        _lookups.Calls.Count.Should().BeLessThanOrEqualTo(
            beforeClose + 2, "pencere dışında arama uzun aralığa döner");
        // Rutin aramalar satırı "bağlanıyor"a çevirip durmaz: tek geçiş.
        Transitions().Should().Equal("Stopped", "Connecting", "NoBroadcast");
        _ingestors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_missing_account_never_opens_a_browser_and_is_not_retried_fast()
    {
        // Ayarda kanal yazılı, hesap bağlı değil. Arama tarayıcı açmadan "bağlı değil" der; döngü
        // bunu sebebiyle bildirir ve hızlı aramaz (her deneme boşuna olurdu).
        _lookups.Result = new BroadcastLookup(
            BroadcastLookupStatus.NotConnected, Message: YouTubeStatusMessages.NotConnected);
        await RunInSessionAsync(); // hızlı arama penceresi açık

        await Until(() => State == NeedsAttention);
        Health!.Detail.Should().Be("YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla.");
        await Settle();
        _lookups.Calls.Should().HaveCount(1);
        _ingestors.Should().BeEmpty();
    }

    [Fact]
    public async Task A_lookup_that_needs_attention_shows_its_reason_and_a_failed_one_is_retried_later()
    {
        _lookups.Result = new BroadcastLookup(
            BroadcastLookupStatus.NeedsAttention, Message: YouTubeStatusMessages.QuotaExhausted);
        var session = await RunInSessionAsync();
        await Until(() => State == NeedsAttention);
        Health!.Detail.Should().Be("Günlük YouTube API kotası doldu; sohbet yarına kadar okunamayabilir.");

        // Geçici hata: sebep şeride çıkmaz, yeniden deneme saati yazılır.
        _lookups.Result = new BroadcastLookup(BroadcastLookupStatus.Failed);
        EndSession(session);
        StartSession();

        await Until(() => State == Retrying);
        Health!.Should().Match<ChatSourceHealth>(h =>
            h.Detail == null && h.RetryAt == _time.GetUtcNow() + Fast.IdleWhenOffline);
    }

    // ── Olay, turla aynı anda gelirse ───────────────────────────────────────────────────────────
    //
    // Oturum satırı, oturum olayından ÖNCE yazılır; yenileme isteği de her an gelebilir. Bu testler
    // sırayı zorlar, zamana güvenmez: tur ayarları okuduğu anda tutulur ya da olay tam o anda gönderilir.

    [Fact]
    public async Task Session_start_leaves_alone_a_turn_that_already_connected_for_that_session()
    {
        // Tur oturumu olaydan önce görüp bağlandı. Olay sonradan geldiğinde o turu kesmemeli: kesseydi
        // az önce kurulan okuyucu durdurulup bir kez daha kurulurdu (fazladan arama ve akış bağlantısı).
        var turnStarted = TurnSeesTheSessionBeforeTheServiceHearsOfIt(() => State == Live);
        await RunAsync();
        await turnStarted.WaitAsync(TestPatience.Upper);

        StartSession();

        State.Should().Be(Live, "ön koşul: tur, servis oturum olayını görmeden bağlandı");
        await Settle();
        _ingestors.Should().ContainSingle("bu oturum için çalışan tur kesilmez");
        _lookups.Calls.Should().Equal(false);
        Transitions().Should().Equal("Connecting", "Live");
    }

    [Fact]
    public async Task Session_start_still_opens_fast_retries_for_a_turn_that_already_found_no_broadcast()
    {
        // Aynı sıranın öbür yüzü: tur oturumu olaydan önce görüp aradı, yayın yoktu ve hızlı arama
        // penceresi henüz açılmadığı için UZUN beklemeye girdi. Olay o beklemeyi kesmeli; kesmezse
        // "Yayın Başlat"tan sonra yayın bir dakika boyunca aranmaz.
        _lookups.Result = new BroadcastLookup(BroadcastLookupStatus.NoBroadcast);
        var turnStarted = TurnSeesTheSessionBeforeTheServiceHearsOfIt(() => State == NoBroadcast);
        await RunAsync();
        await turnStarted.WaitAsync(TestPatience.Upper);

        StartSession();

        State.Should().Be(NoBroadcast, "ön koşul: tur, servis oturum olayını görmeden aradı");
        await Until(() => _lookups.Calls.Count >= 4); // uzun aralıkla ikinci aramaya bile varılamazdı
    }

    [Fact]
    public async Task A_refresh_that_arrives_as_a_turn_starts_reconnects_exactly_once()
    {
        // Yenileme, yeni başlayan tur henüz kapılardayken geliyor. O tur kesilmiştir: yayın aramaz,
        // okuyucu kurmaz; yerine gelen tur bir kez, önbelleği atlayarak bağlanır. Kesme gecikmeli
        // işlenseydi kesilen tur da bağlanır, ardından bir kez daha bağlanılırdı (iki arama, iki okuyucu).
        await RunInSessionAsync();
        await Until(() => State == Live);

        _onNextSettingsRead = _reconnect.Request; // sıradaki turun başında, döngünün iş parçacığında
        _ingestors.Single().End(YouTubeIngestorEnd.BroadcastEnded); // tur biter, kısa beklemeden sonra yenisi başlar

        await Until(() => _ingestors.Count >= 2 && State == Live);
        await Settle();
        _ingestors.Should().HaveCount(2);
        _lookups.Calls.Should().Equal(false, true);
        Lines(ReconnectPrefix).Should().ContainSingle();
    }

    // ── Yenileme ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_cuts_the_running_ingestor_and_reconnects_immediately_with_a_fresh_lookup()
    {
        await RunInSessionAsync();
        await Until(() => State == Live);
        var first = _ingestors.Single();

        _reconnect.Request();

        await Until(() => _ingestors.Count == 2 && State == Live);
        first.Stopped.Should().BeTrue();
        first.Disposed.Should().BeTrue();
        _ingestors.Last().Running.Should().BeTrue();
        // İkinci arama önbelleği atlar: yayın az önce yeniden açılmış olabilir.
        _lookups.Calls.Should().Equal(false, true);
        Transitions().Should().Equal("Stopped", "Connecting", "Live", "Connecting", "Live");
        Lines(ReconnectPrefix).Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_cancels_a_lookup_that_is_still_running()
    {
        _lookups.Hang = true;
        await RunInSessionAsync();
        await Until(() => _lookups.Calls.Count == 1);

        _lookups.Hang = false; // sıradaki arama hemen dönsün
        _reconnect.Request();

        await Until(() => State == Live);
        _lookups.Cancelled.Should().Be(1, "yenileme yalnız uyuyan döngüyü uyandırmaz, süren aramayı da keser");
        _lookups.Calls.Should().Equal(false, true);
    }

    [Fact]
    public async Task Refresh_resets_the_crash_backoff()
    {
        _startError = () => new InvalidOperationException("okuyucu kurulamadı");
        await RunInSessionAsync();
        await Until(() => State == Retrying);
        Health!.RetryAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromSeconds(30));

        _reconnect.Request();

        await Until(() => _ingestors.Count == 2 && State == Retrying);
        Health!.RetryAt.Should().Be(
            _time.GetUtcNow() + TimeSpan.FromSeconds(30),
            "yenileme hata sayacını sıfırlar; sıfırlamasaydı ikinci hata 60 sn beklerdi");
    }

    [Fact]
    public async Task Repeated_refreshes_leave_exactly_one_ingestor_running()
    {
        await RunInSessionAsync();
        await Until(() => State == Live);

        for (var i = 0; i < 5; i++) _reconnect.Request();

        // Son istek durumu "bağlanıyor"a aldı; yeniden "bağlı" olduysa son tur tamamlandı.
        await Until(() => State == Live);
        RunningIngestors.Should().Be(1);
        _ingestors.Where(i => !i.Running).Should().OnlyContain(i => i.Stopped && i.Disposed);
        Lines(ReconnectPrefix).Should().HaveCount(5);
    }

    [Fact]
    public async Task Every_ingestor_gets_the_same_seen_message_memory()
    {
        // YouTube akışı her bağlantıda yakın geçmişi yeniden gönderir; bellek okuyucuyla birlikte
        // sıfırlansaydı yenileme aynı mesajları sohbete ikinci kez düşürürdü.
        await RunInSessionAsync();
        await Until(() => State == Live);

        _reconnect.Request();

        await Until(() => _ingestors.Count == 2 && State == Live);
        _ingestors.Select(i => i.Request.Seen).Distinct().Should().ContainSingle();
    }

    // ── Okuyucunun çıkışı ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_finished_broadcast_reports_no_broadcast_and_a_failure_reports_retrying_with_a_time()
    {
        await RunInSessionAsync();
        await Until(() => State == Live);

        _lookups.Result = new BroadcastLookup(BroadcastLookupStatus.NoBroadcast);
        _ingestors.Single().End(YouTubeIngestorEnd.BroadcastEnded);

        await Until(() => State == NoBroadcast);
        Health!.WasLiveThisSession.Should().BeTrue("şerit 'yayın kesildi' diyebilsin");

        // Yayın yeniden açıldı; bu kez akış hatayla kopuyor.
        _lookups.Result = Found();
        await Until(() => _ingestors.Count == 2 && State == Live);
        _ingestors.Last().End(YouTubeIngestorEnd.Failed);

        await Until(() => State == Retrying);
        Health!.RetryAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task A_fixed_video_that_is_not_live_needs_attention_without_escalating_backoff()
    {
        // Ayarda sabit video bağlantısı var ve o video canlı değil. Bu bir çökme değil: beklemek
        // düzeltmez, operatör yeni yayının bağlantısını yazmalı. Hata sayacı büyümez.
        _settings.YouTubeChannelHandle = "https://www.youtube.com/watch?v=" + NewVideoId();
        _startError = () => new YouTubeNoLiveChatException("canlı sohbet yok");
        await RunInSessionAsync();

        await Until(() => _ingestors.Count >= 3); // geri çekilme büyüseydi ikinci deneme 30 sn sonra olurdu
        State.Should().Be(NeedsAttention);
        Health!.Detail.Should().Be(
            "Ayarlardaki YouTube bağlantısı canlı bir yayına ait değil. " +
            "Ayarlar > YouTube'a kanal adını ya da yeni yayının bağlantısını yaz.");
        _lookups.Calls.Should().BeEmpty("sabit videoda yayın aranmaz");
        Transitions().Should().Equal("Stopped", "Connecting", "NeedsAttention");
    }

    // ── Oturum bitişi ve kapılar ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Session_end_stops_the_ingestor_and_reports_stopped()
    {
        var session = await RunInSessionAsync();
        await Until(() => State == Live);

        EndSession(session);

        await Until(() => State == Stopped);
        _ingestors.Single().Should().Match<FakeIngestor>(i => i.Stopped && i.Disposed);
    }

    [Fact]
    public async Task Refresh_racing_with_session_end_leaves_no_ingestor_and_ends_stopped()
    {
        var session = await RunInSessionAsync();
        await Until(() => State == Live);

        _reconnect.Request();
        EndSession(session);

        await Until(() => State == Stopped && RunningIngestors == 0);
        await Settle();
        State.Should().Be(Stopped, "oturum bitti: yenileme kaynağı yeniden canlandırmamalı");
        RunningIngestors.Should().Be(0);

        // Ters sıra: oturum bittikten sonra gelen yenileme hiçbir şeyi başlatmaz.
        var created = _ingestors.Count;
        _reconnect.Request();
        await Settle();
        State.Should().Be(Stopped);
        _ingestors.Should().HaveCount(created);
    }

    [Fact]
    public async Task Trial_mode_and_missing_handle_report_stopped()
    {
        _trial.IsTrialMode = true;
        var session = await RunInSessionAsync();
        await Until(() => State == Stopped);
        await Settle();
        _lookups.Calls.Should().BeEmpty("deneme sürümünde YouTube sohbeti okunmaz");

        _trial.IsTrialMode = false;
        _settings.YouTubeChannelHandle = "  ";
        EndSession(session);
        session = StartSession();
        await Settle();
        _lookups.Calls.Should().BeEmpty("kanal yazılı değilken aranacak yayın yok");
        State.Should().Be(Stopped);

        // Kapılar açılınca aynı servis bağlanır: susmasının sebebi kapılardı.
        _settings.YouTubeChannelHandle = $"@kanal{Guid.NewGuid():N}";
        EndSession(session);
        StartSession();
        await Until(() => State == Live);
    }
}
