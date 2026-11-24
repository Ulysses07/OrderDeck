using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Facebook;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Core.Chat.ChatSourceState;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Facebook sohbet servisinin dış döngüsü: yenileme ve durum bildirimi (tasarım: Sohbeti yenile
/// §5.2, §5.4).
///
/// NEDEN: döngünün yalnız saf karar kuralları sınanıyordu; döngünün kendisi (kapılar, çözümleme,
/// okuyucunun kurulup kesilmesi) hiç koşmuyordu. Yenileme tam o döngüye dokunuyor: çalışan okuyucuyu
/// kesip baştan bağlanmalı, bunu yaparken yayımlanmış yorumları ikinci kez yayımlamamalı ve oturum
/// bitişiyle yarışınca asılı okuyucu bırakmamalı.
///
/// Gerçek OAuth servisi + tohumlanmış gerçek depo + sahte Graph; ağa çıkılmaz. Okuyucunun yoklama
/// aralığı (1,5 sn) gerçek: yorum akıtan testler birkaç saniye sürebilir. Bekleyen her doğrulama
/// koşulu yoklar (üst sınır <see cref="TestPatience.Upper"/>).
/// </summary>
public sealed class FacebookChatHostedServiceLoopTests : IAsyncLifetime
{
    private const string ReconnectPrefix = "[ChatReconnect] facebook: ";
    private const string PollerStartPrefix = "[FacebookLiveCommentsStream] polling comments for video ";

    /// <summary>Dolması beklenmeyen süre: "uyandırıldı / kesildi" ile "süre doldu"yu ayırır.</summary>
    private static readonly TimeSpan Long = TestPatience.Upper * 4;

    private static readonly FacebookChatTimings Fast = new(
        IdleWhenOffline: Long,
        IdleAfterStreamExit: TimeSpan.FromMilliseconds(30),
        FastResolveInterval: TimeSpan.FromMilliseconds(30),
        FastResolveWindow: TimeSpan.FromMinutes(2),
        WatchdogInterval: Long, // bekçi bu testlerin konusu değil; araya girmesin
        StaleWindow: TimeSpan.FromMinutes(2));

    private readonly FacebookOAuthTestKit _kit = new();
    private readonly ManualTime _time = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly ChatBus _bus = new(ringBufferSize: 50);
    private readonly ConcurrentQueue<ChatMessage> _received = new();
    private readonly InMemorySqlite _db = new();
    private readonly StreamSessionService _sessions;
    private readonly ChatSourceMonitor _sources;
    private readonly ChatReconnectSignal _reconnect = new();
    private readonly LiveScript _live = new();
    private readonly HookedHttpFactory _http;
    private FacebookChatHostedService? _service;

    public FacebookChatHostedServiceLoopTests()
    {
        _http = new HookedHttpFactory(_kit.HttpFactory);
        _bus.Subscribe(_received.Enqueue);

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
        _kit.Dispose();
    }

    // ── Sahte Graph betiği ──────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Sayfanın canlı yayın listesini (<c>live_videos</c>) ve yayının yorum ucunu (<c>comments</c>)
    /// betikler. Eşzamanlı güvenlidir: istekler havuz iş parçacıklarından gelir.
    /// </summary>
    private sealed class LiveScript
    {
        /// <summary>Yayına yazılmış bir yorum.</summary>
        public sealed record Comment(string Id, string Text);

        private readonly object _gate = new();
        private readonly List<Comment> _comments = new();
        private string? _videoId = FacebookOAuthTestKit.NewId();
        private Func<HttpResponseMessage>? _liveVideosOverride;
        private Func<HttpResponseMessage>? _commentsOverride;
        private int _liveVideoRequests;
        private int _commentRequests;

        /// <summary>Canlı yayının video kimliği; null ise Sayfa canlı değil.</summary>
        public string? VideoId
        {
            get { lock (_gate) return _videoId; }
            set { lock (_gate) _videoId = value; }
        }

        /// <summary>Doluysa canlı yayın listesi isteklerinin cevabı budur (hata kurguları).</summary>
        public Func<HttpResponseMessage>? LiveVideosOverride
        {
            get { lock (_gate) return _liveVideosOverride; }
            set { lock (_gate) _liveVideosOverride = value; }
        }

        /// <summary>Doluysa yorum isteklerinin cevabı budur (ör. yayın bitti: Graph kodu 100).</summary>
        public Func<HttpResponseMessage>? CommentsOverride
        {
            get { lock (_gate) return _commentsOverride; }
            set { lock (_gate) _commentsOverride = value; }
        }

        public int LiveVideoRequests => Volatile.Read(ref _liveVideoRequests);

        public int CommentRequests => Volatile.Read(ref _commentRequests);

        public void Attach(FakeGraph graph, string pageId)
        {
            graph.Map(
                request => request.AbsolutePath.EndsWith($"/{pageId}/live_videos", StringComparison.Ordinal),
                _ => LiveVideos());
            graph.Map(
                request => request.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal),
                _ => Comments());
        }

        /// <summary>Yayına bir yorum yazar; metin noktasız üretilir (bağlantı sayılmasın).</summary>
        public Comment Add()
        {
            var comment = new Comment(FacebookOAuthTestKit.NewId(), $"yorum-{Guid.NewGuid():N}");
            lock (_gate) _comments.Add(comment);
            return comment;
        }

        private HttpResponseMessage LiveVideos()
        {
            Interlocked.Increment(ref _liveVideoRequests);
            if (LiveVideosOverride is { } respond) return respond();

            var rows = new JsonArray();
            if (VideoId is { } videoId)
            {
                rows.Add(new JsonObject
                {
                    ["id"] = FacebookOAuthTestKit.NewId(),
                    ["status"] = "LIVE",
                    ["video"] = new JsonObject { ["id"] = videoId },
                });
            }

            return FakeGraph.Json(new JsonObject { ["data"] = rows }.ToJsonString());
        }

        private HttpResponseMessage Comments()
        {
            Interlocked.Increment(ref _commentRequests);
            if (CommentsOverride is { } respond) return respond();

            var rows = new JsonArray();
            lock (_gate)
            {
                // Graph gibi en yeni yorum önce.
                for (var i = _comments.Count - 1; i >= 0; i--)
                {
                    rows.Add(new JsonObject
                    {
                        ["id"] = _comments[i].Id,
                        ["message"] = _comments[i].Text,
                        ["created_time"] = "2026-01-01T00:00:01+0000",
                        ["from"] = new JsonObject
                        {
                            ["id"] = FacebookOAuthTestKit.NewId(),
                            ["name"] = $"izleyici-{Guid.NewGuid():N}",
                        },
                    });
                }
            }

            return FakeGraph.Json(new JsonObject { ["data"] = rows }.ToJsonString());
        }
    }

    /// <summary>
    /// Düzeneğin HTTP fabrikasını sarar: servis okuyucunun istemcisini istediği anda (yayın bulundu,
    /// okuyucu kurulmak üzere) bir kez araya girilebilir. "Olay tam o anda geldi" sırasını zamana
    /// güvenmeden kurmak için.
    /// </summary>
    private sealed class HookedHttpFactory(IHttpClientFactory inner) : IHttpClientFactory
    {
        private Action? _onNextStreamClient;

        /// <summary>Okuyucunun istemcisi bir SONRAKİ istenişinde, servisin iş parçacığında bir kez çalışır.</summary>
        public Action? OnNextStreamClient
        {
            set => Volatile.Write(ref _onNextStreamClient, value);
        }

        public HttpClient CreateClient(string name)
        {
            if (name == FacebookChatHostedService.StreamClientName)
                Interlocked.Exchange(ref _onNextStreamClient, null)?.Invoke();
            return inner.CreateClient(name);
        }
    }

    // ── Düzenek ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>Bağlı tek Sayfa tohumlar ve canlı yayın betiğini ona bağlar.</summary>
    private async Task SeedPageAsync()
    {
        var page = (await _kit.SeedAsync())[0];
        _live.Attach(_kit.Graph, page.Page.PageId);
    }

    /// <summary>Servisi iç kurucuyla (kısa süreler) kurup başlatır. Oturum AÇMAZ.</summary>
    private async Task RunAsync()
    {
        _service = new FacebookChatHostedService(
            () => _kit.Settings, _kit.CreateService(), _bus, _logs, trialProbe: null, spamFilter: null,
            _sessions, _http, _reconnect, _sources, _time, Fast);
        await _service.StartAsync(CancellationToken.None);
    }

    /// <summary>
    /// Servisi başlatır, döngü oturumsuz beklemeye girince oturumu açar (oturum başlangıcı hızlı arama
    /// penceresini açar). Beklemek şart: sahada da operatör "Yayın Başlat"a bastığında döngü uykudadır.
    /// Beklenmezse ilk tur ile oturum başlangıcı yarışır: döngü oturumu olaydan önce görürse olayın
    /// "uyan" sinyali bir sonraki beklemeyi keser ve fazladan bir arama yapılır (zararsız, ama istek
    /// sayısını birebir doğrulayan testi yük altında düşürür).
    /// </summary>
    private async Task<string> RunInSessionAsync()
    {
        await RunAsync();
        await Until(() => State == Stopped);
        return StartSession();
    }

    private string StartSession() => _sessions.Start("Test", ["facebook"]).Id;

    private void EndSession(string id) => _sessions.End(id);

    private ChatSourceHealth? Health => _sources.Snapshot().SingleOrDefault(h => h.Platform == "facebook");

    private ChatSourceState? State => Health?.State;

    /// <summary>Kurulan okuyucu sayısı: okuyucu döngüsünün ilk günlük satırı sayılır.</summary>
    private int PollerStarts => Lines(PollerStartPrefix).Count;

    private bool Got(LiveScript.Comment comment) => _received.Any(m => m.ExternalId == comment.Id);

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

    // ── Durum ───────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Live_page_reports_live_once_the_poller_starts()
    {
        await SeedPageAsync();
        var comment = _live.Add();

        await RunInSessionAsync();

        await Until(() => State == Live && Got(comment));
        PollerStarts.Should().Be(1);
    }

    [Fact]
    public async Task Page_that_is_not_live_waits_for_a_broadcast_and_is_retried_fast()
    {
        await SeedPageAsync();
        _live.VideoId = null;

        await RunInSessionAsync();

        await Until(() => _live.LiveVideoRequests >= 4); // uzun aralıkla dördüncü isteğe varılamazdı
        State.Should().Be(NoBroadcast);
        Health!.WasLiveThisSession.Should().BeFalse("şerit 'yayın bekleniyor' der, 'yayın kesildi' değil");
        PollerStarts.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_session_needs_attention_instead_of_waiting_for_a_broadcast()
    {
        // Sayfa token'ı geçersiz. Eskiden bu da "canlı değil" sayılıyordu: operatör yayını açıp
        // bekliyor, satır soluk "yayın bekleniyor" diyordu.
        await SeedPageAsync();
        _live.LiveVideosOverride = () => FakeGraph.Error(190);

        await RunInSessionAsync(); // hızlı arama penceresi açık

        await Until(() => State == NeedsAttention);
        Health!.Detail.Should().Be(
            "Facebook oturumu ya da izni geçersiz. Ayarlar > Facebook > \"Facebook izinlerini güncelle\".");
        await Settle();
        _live.LiveVideoRequests.Should().Be(1, "oturum sorunu hızlı aranmaz: beklemek düzeltmez");
    }

    [Fact]
    public async Task No_page_or_no_session_reports_stopped()
    {
        // Sayfa bağlı değil.
        var session = await RunInSessionAsync();
        await Until(() => State == Stopped);
        await Settle();
        _kit.Graph.Requests.Should().BeEmpty("Sayfa bağlı değilken Graph'a gidilmez");

        // Sayfa bağlandı ama oturum yok.
        EndSession(session);
        await SeedPageAsync();
        _reconnect.Request(); // oturum yok: yenileme hiçbir şeyi başlatmaz
        await Settle();
        State.Should().Be(Stopped);
        _live.LiveVideoRequests.Should().Be(0, "yayın oturumu açılmadan Graph'a gidilmez");

        // Kapılar açılınca aynı servis bağlanır: susmasının sebebi kapılardı.
        StartSession();
        await Until(() => State == Live);
    }

    // ── Yenileme ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Refresh_cuts_the_poller_clears_the_stale_blacklist_and_rebinds()
    {
        await SeedPageAsync();
        await RunInSessionAsync();
        await Until(() => State == Live);

        _reconnect.Request();

        await Until(() => PollerStarts == 2 && State == Live);
        Lines(ReconnectPrefix).Should().ContainSingle();

        // Yayın kanıtlı bitti (yorum ucu Graph kodu 100) ama Meta listede bir süre daha canlı
        // gösteriyor: o kimlik kara listede, servis ona geri bağlanmıyor.
        _live.CommentsOverride = () => FakeGraph.Error(100);
        await Until(() => State == NoBroadcast);
        await Settle();
        PollerStarts.Should().Be(2);
        Health!.WasLiveThisSession.Should().BeTrue("şerit 'yayın kesildi' diyebilsin");

        // Yenileme kara listeyi temizler: aynı yayına yeniden bağlanılabilir.
        _live.CommentsOverride = null;
        _reconnect.Request();

        await Until(() => PollerStarts == 3 && State == Live);
    }

    [Fact]
    public async Task A_refresh_that_arrives_after_the_broadcast_was_found_binds_exactly_once()
    {
        // Yenileme, yayın bulunduktan hemen sonra, okuyucu kurulmak üzereyken geliyor. O iş kesilmiştir:
        // okuyucu kurmaz, durum yazmaz; yerine gelen iş bir kez bağlanır. Kesme gecikmeli işlenseydi
        // kesilen iş de okuyucu kurar, hemen ardından kesilir ve bir kez daha bağlanılırdı (iki okuyucu;
        // şeritte bir an "bağlı", sonra yine "bağlanıyor"). Sıra zorlanır: istek, servis okuyucunun
        // HTTP istemcisini istediği anda (aramadan sonraki ilk adım) gönderilir.
        await SeedPageAsync();
        _http.OnNextStreamClient = _reconnect.Request;

        await RunInSessionAsync();

        await Until(() => PollerStarts >= 1 && State == Live);
        await Settle();
        PollerStarts.Should().Be(1);
        Lines(ReconnectPrefix).Should().ContainSingle();
    }

    [Fact]
    public async Task Refresh_keeps_the_seen_comment_memory()
    {
        // Yeni okuyucunun ilk yoklaması son 100 yorumu yeniden getirir; bellek okuyucuyla birlikte
        // sıfırlansaydı yenileme aynı yorumları (aynı siparişleri) ikinci kez yayımlardı.
        await SeedPageAsync();
        var before = _live.Add();
        await RunInSessionAsync();
        await Until(() => Got(before));

        _reconnect.Request();
        await Until(() => PollerStarts == 2 && State == Live);
        var after = _live.Add();

        // Yorumlar eskiden yeniye işlenir: "after" yayımlandıysa "before" o yoklamada görülmüştür.
        await Until(() => Got(after));
        _received.Count(m => m.ExternalId == before.Id).Should().Be(1);
    }

    [Fact]
    public async Task Refresh_racing_with_session_end_ends_stopped()
    {
        await SeedPageAsync();
        var session = await RunInSessionAsync();
        await Until(() => State == Live);

        _reconnect.Request();
        EndSession(session);

        await Until(() => State == Stopped);
        // Asılı okuyucu kalmadı: yoklama aralığından (1,5 sn) uzun sürede yeni yorum isteği gelmez.
        await Task.Delay(500);
        var requests = _live.CommentRequests;
        await Task.Delay(2500);
        _live.CommentRequests.Should().Be(requests, "oturum bitti: okuyucu çalışmamalı");
        State.Should().Be(Stopped);
    }
}
