using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Web;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using OrderDeck.Chat.Facebook;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using static OrderDeck.Chat.Ingestors.Instagram.InstagramAccountState;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Bir Instagram hesabının <c>live_media</c> ucunu betikler: poller'ın saniyede bir attığı isteği karşılar, test
/// yorum ekler, yanıtı değiştirir ya da isteği düşürür. Eşzamanlı güvenlidir (istekler havuz iş parçacıklarından
/// gelir). Bekleyen üyelerin üst sınırı <see cref="TestPatience.Upper"/>'dır.
///
/// NEDEN başta tek tohum yorumu: poller ilk çekimi yutar (geçmiş basılmaz). Besleme boş başlasaydı ilk gerçek
/// yorumun geldiği sayfa "hepsi yeni" görünür, taşma sayılır ve poller aralığını sıkılaştırırdı.
/// </summary>
internal sealed class InstagramLiveFeed
{
    /// <summary>Beslemeye eklenmiş bir yorum: Graph'taki kimliği, izleyicinin kullanıcı adı ve metni.</summary>
    public sealed record Comment(string Id, string Viewer, string Text);

    private readonly object _gate = new();
    private readonly string _mediaId = FacebookOAuthTestKit.NewId();
    private readonly List<(Comment Comment, DateTimeOffset At)> _comments = new();

    // Karşılanan her isteğin token'ı, geliş sırasıyla: sayaç da bu listenin uzunluğudur.
    private readonly List<string> _served = new();

    private DateTimeOffset _lastStamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private volatile string? _usageHeader;
    private volatile bool _noBroadcast;
    private volatile Func<string, HttpResponseMessage?>? _intercept;

    /// <summary>Yolu <c>/{igUserId}/live_media</c> ile biten istekleri üstlenir.</summary>
    public InstagramLiveFeed(FakeGraph graph, string igUserId)
    {
        Add(); // tohum: ilk çekimde yutulur
        var path = $"/{igUserId}/live_media";
        graph.Map(request => request.AbsolutePath.EndsWith(path, StringComparison.Ordinal), Respond);
    }

    /// <summary>Karşılanan istek sayısı. <see cref="Intercept"/> çağrılmadan ÖNCE artar.</summary>
    public int Served
    {
        get { lock (_gate) return _served.Count; }
    }

    /// <summary>Verilen Sayfa token'ıyla gelen istek sayısı.</summary>
    public int ServedFor(string accessToken)
    {
        lock (_gate) return _served.Count(token => token == accessToken);
    }

    /// <summary>İsteklerde görülen farklı token'lar, ilk görülme sırasıyla.</summary>
    public IReadOnlyCollection<string> Tokens
    {
        get { lock (_gate) return _served.Distinct().ToArray(); }
    }

    /// <summary>Doluysa her olağan yanıta <c>X-Business-Use-Case-Usage</c> başlığı olarak eklenir.</summary>
    public string? UsageHeader
    {
        get => _usageHeader;
        set => _usageHeader = value;
    }

    /// <summary>true iken olağan yanıt "canlı yayın yok"tur (<c>{"data":[]}</c>).</summary>
    public bool NoBroadcast
    {
        get => _noBroadcast;
        set => _noBroadcast = value;
    }

    /// <summary>
    /// İstekteki token'ı alır; null dönmezse yanıt budur (olağan yanıt yerine), fırlatırsa istisna isteği yapana
    /// (poller'a) çıkar: ağ hatası kurgusu.
    /// </summary>
    public Func<string, HttpResponseMessage?>? Intercept
    {
        get => _intercept;
        set => _intercept = value;
    }

    /// <summary>
    /// Yayına bir yorum yazar: kimlik, izleyici adı ve metin verilmezse üretilir (metin noktasız: bağlantı
    /// sayılmasın); damgası bir önceki yorumdan 1 sn sonradır.
    /// </summary>
    public Comment Add(string? viewer = null, string? text = null)
    {
        var comment = new Comment(
            FacebookOAuthTestKit.NewId(),
            viewer ?? $"izleyici-{Guid.NewGuid():N}",
            text ?? $"yorum-{Guid.NewGuid():N}");
        lock (_gate)
        {
            _lastStamp = _lastStamp.AddSeconds(1);
            _comments.Add((comment, _lastStamp));
        }

        return comment;
    }

    /// <summary>En az bir istek karşılanana dek bekler: poller ilk çekimini yaptı.</summary>
    public Task PrimedAsync() => UntilServedAsync(1);

    /// <summary>
    /// Çağrıdan SONRA bir istek daha karşılanana dek bekler. Dönüşten sonra <see cref="Add"/> ile yazılan yorum o
    /// isteğin yanıtında yoktur: poller onu bir sonraki çekimde, ilk kez ve yeni olarak görür.
    /// </summary>
    public Task NextAsync() => UntilServedAsync(Served + 1);

    /// <summary>İstek adresindeki <c>access_token</c>; yoksa boş metin.</summary>
    public static string TokenIn(Uri request) =>
        HttpUtility.ParseQueryString(request.Query)["access_token"] ?? string.Empty;

    /// <summary>
    /// Tek kovalı <c>X-Business-Use-Case-Usage</c> başlık değeri:
    /// <c>{"&lt;businessId&gt;":[{"type":"instagram","call_count":…,"total_cputime":…,"total_time":…}]}</c>;
    /// <paramref name="regainMinutes"/> verilirse <c>estimated_time_to_regain_access</c> de yazılır.
    ///
    /// NEDEN JSON yazıcısı: sayılar kültürden bağımsız yazılmalı. Geliştirme makinesi tr-TR; metin
    /// enterpolasyonu "12,5" üretir, JSON bozulur ve başlık okunamaz.
    /// </summary>
    public static string Usage(
        string businessId, double callCount, double cpuTime, double totalTime, double? regainMinutes = null)
    {
        var bucket = new JsonObject
        {
            ["type"] = "instagram",
            ["call_count"] = callCount,
            ["total_cputime"] = cpuTime,
            ["total_time"] = totalTime,
        };
        if (regainMinutes is { } minutes) bucket["estimated_time_to_regain_access"] = minutes;
        return new JsonObject { [businessId] = new JsonArray(bucket) }.ToJsonString();
    }

    private HttpResponseMessage Respond(Uri request)
    {
        var token = TokenIn(request);
        string body;
        lock (_gate)
        {
            // Sayaç ile yanıtın yorum listesi AYNI kilit altında: sayaç arttıktan sonra eklenen yorum bu yanıta
            // girmez (NextAsync'in sözü).
            _served.Add(token);
            body = Body();
        }

        // Kilit DIŞINDA: bekleten ya da fırlatan bir betik öteki istekleri ve sayaç okumalarını tutmasın.
        return _intercept?.Invoke(token) ?? FakeGraph.Json(body, usageHeader: _usageHeader);
    }

    /// <summary>Olağan yanıt gövdesi; Graph gibi en yeni yorum önce. Kilit altında çağrılır.</summary>
    private string Body()
    {
        if (_noBroadcast) return """{"data":[]}""";

        var comments = new JsonArray();
        for (var i = _comments.Count - 1; i >= 0; i--)
        {
            var (comment, at) = _comments[i];
            comments.Add(new JsonObject
            {
                ["id"] = comment.Id,
                ["text"] = comment.Text,
                ["timestamp"] = at.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + "+0000",
                ["username"] = comment.Viewer,
            });
        }

        var media = new JsonObject { ["id"] = _mediaId, ["comments"] = new JsonObject { ["data"] = comments } };
        return new JsonObject { ["data"] = new JsonArray(media) }.ToJsonString();
    }

    private async Task UntilServedAsync(int count)
    {
        var waited = Stopwatch.StartNew();
        while (Served < count)
        {
            if (waited.Elapsed > TestPatience.Upper)
                throw new TimeoutException(
                    $"besleme {TestPatience.Upper.TotalSeconds} sn'de {count}. isteği almadı");
            await Task.Delay(20);
        }
    }
}

/// <summary>
/// Instagram hosted service'inin çok hesaplı yüzü: üst döngü (bağlantı ve oturum kapıları, kapı, oturum sonu
/// toplaması) ve bağlı her Sayfanın kendi hesap döngüsü (çoklu hesap tasarımı §8.3.2, §8.3.7, §8.5, §8.8, §9.3).
///
/// NEDEN: iki hesap aynı mezadı yayınlıyor; birinin token'ı düşer, hız sınırına girer ya da çökerse öteki
/// akmaya devam etmeli (AH2) ve susan hesap sağlık izleyicisinde görünmeli (AH3). Tek Sayfalı kurulumun
/// yoklama yolu ise bugünküyle aynı kalmalı: kapı yok, etiket yok (AH4).
///
/// Gerçek OAuth servisi + tohumlanmış gerçek depo + sahte Graph; ağa çıkılmaz. Poller'da süre dikişi yoktur:
/// yorum akıtan testler gerçek 1 sn'lik aralıkla koşar. Saat elle ilerletilir ama zamanlayıcı sürmez; bekleyen
/// her doğrulama koşulu yoklar (üst sınır <see cref="TestPatience.Upper"/>). Kimlik, token, hesap ve izleyici
/// adı literal yazılmaz, üretilir (depo herkese açık).
/// </summary>
public sealed partial class InstagramMultiAccountHostedServiceTests : IAsyncLifetime
{
    private const string AccountPrefix = "[InstagramChatHostedService] account ";
    private const string UsagePrefix = "[InstagramChatHostedService] usage ";

    /// <summary>Dolması beklenmeyen süre: "iptal edildi / kesildi" ile "süre doldu"yu ayırır.</summary>
    private static readonly TimeSpan Long = TestPatience.Upper * 4;

    private static readonly InstagramChatTimings Fast = new(
        IdleWhenOffline: TimeSpan.FromMilliseconds(50),
        IdleAfterPollerExit: TimeSpan.FromMilliseconds(50),
        IdleAfterFatal: TimeSpan.FromMilliseconds(200),
        WatchInterval: TimeSpan.FromMilliseconds(50),
        UsageLogInterval: TimeSpan.FromMinutes(1));

    private readonly FacebookOAuthTestKit _kit = new();
    private readonly ManualTime _time = new();
    private readonly CapturingLoggerFactory _logs = new();
    private readonly ChatBus _bus = new(ringBufferSize: 50);
    private readonly ConcurrentQueue<ChatMessage> _received = new();
    private readonly InMemorySqlite _db = new();
    private readonly StreamSessionService _sessions;
    private readonly InstagramHealthMonitor _health;
    private readonly FacebookOAuthService _oauth;
    private InstagramChatHostedService? _service;

    public InstagramMultiAccountHostedServiceTests()
    {
        _bus.Subscribe(_received.Enqueue);

        new MigrationRunner(_db).Run();
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);
        _sessions = new StreamSessionService(new SessionRepository(_db), clock.Object);

        // Servisle AYNI saat: yeniden deneme saatleri "şimdi + süre" diye birebir doğrulanır.
        _health = new InstagramHealthMonitor(_time);
        _oauth = _kit.CreateService();
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

    /// <summary>Tek Sayfalı bağlı kurulum ve Instagram hesabının beslemesi.</summary>
    private async Task<(SeededPage Page, InstagramLiveFeed Feed)> OneAsync()
    {
        var page = (await _kit.SeedAsync())[0];
        return (page, new InstagramLiveFeed(_kit.Graph, page.IgUserId!));
    }

    /// <summary>İki Sayfalı bağlı kurulum (A birincil, B ek) ve iki hesabın beslemeleri.</summary>
    private async Task<(SeededPage A, SeededPage B, InstagramLiveFeed FeedA, InstagramLiveFeed FeedB)> TwoAsync()
    {
        var pages = await _kit.SeedAsync(extraPages: 1);
        return (pages[0], pages[1],
            new InstagramLiveFeed(_kit.Graph, pages[0].IgUserId!),
            new InstagramLiveFeed(_kit.Graph, pages[1].IgUserId!));
    }

    /// <summary>Oturum açar (kimliğini döner), servisi iç kurucuyla kurup başlatır.</summary>
    private async Task<string> RunAsync(InstagramChatTimings? timings = null, IHttpClientFactory? httpFactory = null)
    {
        var session = StartSession();
        _service = new InstagramChatHostedService(
            _oauth, _bus, _logs, spamFilter: null, _sessions, httpFactory ?? _kit.HttpFactory, _health, _time,
            timings ?? Fast);
        await _service.StartAsync(CancellationToken.None);
        return session;
    }

    private string StartSession() => _sessions.Start("Test", ["instagram"]).Id;

    private void EndSession(string id) => _sessions.End(id);

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

    /// <summary>
    /// Beslemeden sohbete bir yorum akıtır: bir isteğin karşılanmasını bekler (poller ilk çekimini yutmuş olur),
    /// yorumu yazar ve bus'a varana dek bekler.
    /// </summary>
    private async Task<InstagramLiveFeed.Comment> FlowAsync(InstagramLiveFeed feed)
    {
        await feed.NextAsync();
        var comment = feed.Add();
        await Until(() => Got(comment));
        return comment;
    }

    private InstagramAccountHealth? Health(SeededPage page) =>
        _health.Snapshot().FirstOrDefault(h => h.PageId == page.Page.PageId);

    private InstagramAccountState? State(SeededPage page) => Health(page)?.State;

    private bool Got(InstagramLiveFeed.Comment c) => _received.Any(m => m.ExternalId == c.Id);

    private ChatMessage Message(InstagramLiveFeed.Comment c) => _received.Single(m => m.ExternalId == c.Id);

    /// <summary>O hesap için kurulan poller sayısı: poller döngüsünün ilk günlük satırı sayılır.</summary>
    private int PollerStarts(SeededPage page) => _logs.Entries.Count(e =>
        e.Message.EndsWith($"polling live_media for IG user {page.IgUserId}", StringComparison.Ordinal));

    /// <summary>İstek o Sayfanın Instagram hesabını çözme isteği mi.</summary>
    private static bool IsResolve(Uri request, SeededPage page) =>
        request.AbsolutePath.EndsWith("/" + page.Page.PageId, StringComparison.Ordinal)
        && request.Query.Contains("instagram_business_account", StringComparison.Ordinal);

    private int ResolveRequests(SeededPage page) => _kit.Graph.Requests.Count(u => IsResolve(u, page));

    /// <summary>Information ve üstü, iletisi <paramref name="prefix"/> ile başlayan satırlar (yazım sırasıyla).</summary>
    private IReadOnlyList<string> Lines(string prefix) => _logs.Entries
        .Where(e => e.Level >= LogLevel.Information && e.Message.StartsWith(prefix, StringComparison.Ordinal))
        .Select(e => e.Message)
        .ToList();

    /// <summary><paramref name="failCall"/>'uncu istemci isteğinde fırlatır; öbürlerini asıl fabrikaya devreder.</summary>
    private sealed class FailingOnce(IHttpClientFactory inner, int failCall) : IHttpClientFactory
    {
        private int _calls;

        public HttpClient CreateClient(string name) =>
            Interlocked.Increment(ref _calls) == failCall
                ? throw new InvalidOperationException("istemci kurulamadı")
                : inner.CreateClient(name);
    }

    /// <summary>
    /// İki hesap, B'nin beslemesi betikli (bozuk): A'dan bir yorum akıtır, B <paramref name="settled"/> olana dek
    /// bekler ve A'nın canlı olduğunu doğrular. 5 dakikalık bekleme <see cref="Long"/>'dur: B beklemede kalır.
    /// </summary>
    private async Task<(SeededPage B, InstagramLiveFeed FeedB)> WithFaultyBAsync(
        Func<string, HttpResponseMessage?> intercept, Func<SeededPage, bool> settled)
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        feedB.Intercept = intercept;
        await RunAsync(Fast with { IdleAfterFatal = Long });
        await FlowAsync(feedA);
        await Until(() => settled(b));
        State(a).Should().Be(Live);
        return (b, feedB);
    }

    [Fact]
    public async Task Acik_kurucu_varsayilan_surelerle_izleyici_ve_oturum_servisi_olmadan_yoklar()
    {
        InstagramChatTimings.Default.Should().Be(new InstagramChatTimings(
            TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(2),
            TimeSpan.FromMinutes(1)));
        var (_, feed) = await OneAsync();

        _service = new InstagramChatHostedService(_oauth, _bus, _logs, httpFactory: _kit.HttpFactory);
        await _service.StartAsync(default);

        await feed.PrimedAsync(); // sessions null: oturum hep açık; izleyiciyi servis kurdu
    }

    [Fact]
    public async Task Baglanti_yokken_saglik_temizlenir_ve_graph_cagrilmaz()
    {
        _health.Configure([new FacebookLinkedPage(
            FacebookOAuthTestKit.NewId(), FacebookOAuthTestKit.NewToken(), FacebookOAuthTestKit.NewId())]);

        await RunAsync();

        await Until(() => _health.Snapshot().Count == 0);
        _kit.Graph.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Iki_sayfa_iki_poller_ve_kaynak_hesap_etiketi_kullanici_adi_ya_da_sayfa_adi()
    {
        var (a, b, feedA, feedB) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName, b.IgUserId); // B'nin kullanıcı adı yanıtta yok

        await RunAsync();
        var c = await Task.WhenAll(FlowAsync(feedA), FlowAsync(feedB));

        Message(c[0]).SourceAccount.Should().Be("@" + a.IgUsername);
        Message(c[1]).SourceAccount.Should().Be(b.Page.PageName); // "@"sız
        (PollerStarts(a), PollerStarts(b)).Should().Be((1, 1));
    }

    [Fact] // AH4, TK6
    public async Task Tek_sayfada_kaynak_hesap_bos_kapi_yok_gozlemci_gecis_ve_dakikada_en_cok_bir_kota_satiri_yazar()
    {
        var (page, feed) = await OneAsync();
        var (business, who) = (FacebookOAuthTestKit.NewId(), $"@{page.IgUsername} (page {page.Page.PageId}): ");
        feed.UsageHeader = InstagramLiveFeed.Usage(business, callCount: 12.5, cpuTime: 3, totalTime: 4);

        var session = await RunAsync();

        Message(await FlowAsync(feed)).SourceAccount.Should().BeNull();
        await feed.NextAsync(); // en az iki tur tamamlandı
        Lines(AccountPrefix).Should().Contain(AccountPrefix + who + "Starting -> Live"); // durum izleyiciye ulaştı (tek Sayfada da)
        Lines(UsagePrefix).Should().Equal(
            UsagePrefix + who + $"business {business} call_count 12.5% total_cputime 3% total_time 4% regain 0 min"); // satır tek
        _time.Advance(TimeSpan.FromMinutes(1));
        await Until(() => Lines(UsagePrefix).Count == 2); // aralık dolunca ikincisi
        EndSession(session);
        await Until(() => State(page) == Stopped);
        Lines("[InstagramMergeGate]").Should().BeEmpty();
    }

    [Fact]
    public async Task Oturum_bitince_butun_hesaplar_durur_ve_arada_yazilan_yorum_basilmaz()
    {
        var (a, _, feedA, feedB) = await TwoAsync();
        var session = await RunAsync();
        await Task.WhenAll(feedA.PrimedAsync(), feedB.PrimedAsync());

        EndSession(session);

        await Until(() => _health.Snapshot().All(h => h.State == Stopped));
        Lines("[InstagramMergeGate]").Should().NotBeEmpty(); // kapı kapandı: özet satırı
        var gap = feedA.Add(); // oturum yokken yazıldı
        StartSession();
        await Until(() => PollerStarts(a) == 2);
        await FlowAsync(feedA); // yeni poller bir tam aralık çalıştı
        Got(gap).Should().BeFalse(); // eski poller sürseydi bu arada basardı
    }

    [Fact] // AH2, AH3
    public async Task Bir_hesap_190_donerken_oteki_akar()
    {
        var (b, _) = await WithFaultyBAsync(_ => FakeGraph.Error(190), x => Health(x)?.RetryAt is not null);

        (Health(b)!.State, Health(b)!.Detail).Should().Be((Faulted, "Instagram bağlantını yenilemen gerekiyor."));
        Health(b)!.RetryAt.Should().Be(_time.GetUtcNow() + Long);
        // Kalıcı hatanın geçiş satırı yeniden deneme saatini taşır (§8.8).
        await Until(() => Lines(AccountPrefix).Any(l => Regex.IsMatch(l, @"-> Faulted, retry at \d\d:\d\d:\d\d$")));
    }

    [Fact]
    public async Task Bir_hesap_hiz_sinirindayken_oteki_etkilenmez()
    {
        var (b, _) = await WithFaultyBAsync(_ => FakeGraph.Error(4), x => State(x) == RateLimited);

        Health(b)!.RetryAt.Should().Be(_time.GetUtcNow() + TimeSpan.FromMinutes(1)); // başlık yokken poller'ın varsayılan beklemesi
    }

    [Fact]
    public async Task Bir_hesabin_istegi_istisna_atarken_oteki_akar()
    {
        var (b, feedB) = await WithFaultyBAsync(_ => throw new HttpRequestException(), _ => true);

        await Until(() => feedB.Served >= 2); // B yeniden deniyor
        State(b).Should().Be(Starting); // geçici hata durumu değiştirmez
    }

    [Fact]
    public async Task Instagram_hesabi_olmayan_sayfa_otekini_engellemez()
    {
        var (a, b, feedA, _) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName); // ek Sayfa Instagram'sız

        await RunAsync(Fast with { IdleAfterFatal = Long });

        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername); // iki Sayfa + tek hesap: etiket dolu
        await Until(() => State(b) == NotLinked);
        Health(b)!.RetryAt.Should().Be(_time.GetUtcNow() + Long); // TK15
        Lines(UsagePrefix).Should().Equal(
            $"{UsagePrefix}@{a.IgUsername} (page {a.Page.PageId}): X-Business-Use-Case-Usage header absent");
        // Geçiş satırı durum izleyiciye yazıldıktan hemen SONRA günlüğe düşer: doğrudan bakmak yerine yoklanır.
        await Until(() => Lines(AccountPrefix).Any(l => Regex.IsMatch(
            l,
            @"^\[InstagramChatHostedService\] account .+ \(page " + Regex.Escape(b.Page.PageId)
            + @"\): Starting -> NotLinked, retry at \d\d:\d\d:\d\d$")));
    }

    [Fact]
    public async Task Ayni_instagram_hesabina_cozulen_iki_sayfada_tek_poller_ve_atlanan_girdi()
    {
        var (a, b, feedA, _) = await TwoAsync();
        _kit.Graph.AddPage(b.Page.PageId, b.Page.PageName, a.IgUserId, a.IgUsername);

        await RunAsync(Fast with { IdleAfterFatal = Long });

        Message(await FlowAsync(feedA)).SourceAccount.Should().Be("@" + a.IgUsername);
        await Until(() => _health.Snapshot().Count(h => h.State == Skipped) == 1);
        _health.Snapshot().Should().HaveCount(2);
        feedA.Tokens.Should().ContainSingle(); // tek poller: tek Sayfa token'ı
        var skipped = _health.Snapshot().Single(h => h.State == Skipped);
        skipped.RetryAt.Should().Be(_time.GetUtcNow() + Long);
        _logs.Entries.Should().Contain(e =>
            e.Level == LogLevel.Warning
            && e.Message.StartsWith("[InstagramChatHostedService]")
            && e.Message.Contains(skipped.PageId));
    }

    [Fact]
    public async Task Oturum_bitince_bes_dakikalik_bekleme_iptal_olur_yeni_oturum_hemen_dener_ve_cozumleme_yinelenmez()
    {
        var (page, feed) = await OneAsync();
        var session = await RunAsync(Fast with { IdleAfterFatal = Long });
        await Until(() => State(page) == Live);
        feed.Intercept = _ => FakeGraph.Error(190);
        await Until(() => Health(page)?.RetryAt is not null); // kalıcı hata beklemesinde (Long)

        EndSession(session);

        await Until(() => State(page) == Stopped); // bekleme oturumla iptal oldu
        (feed.Intercept, feed.NoBroadcast) = (null, true);
        StartSession();
        await Until(() => State(page) == WaitingForBroadcast); // Long dolmadan denedi; BroadcastEnded değil: ResetSession çağrıldı
        ResolveRequests(page).Should().Be(1); // AH4: önbellek oturumlar arasında korunur
    }

    [Fact]
    public async Task Hesap_dongusundeki_beklenmeyen_istisna_dongude_kalir_ve_dongu_surer()
    {
        var (page, _) = await OneAsync();

        // 1. istemci: çözümleyici (hosted service), 2. istemci: poller (hesap döngüsü).
        await RunAsync(httpFactory: new FailingOnce(_kit.HttpFactory, failCall: 2));

        await Until(() => State(page) == Live);
        _logs.Entries.Should().ContainSingle(e =>
            e.Level == LogLevel.Error
            && e.Message.StartsWith("[InstagramChatHostedService]")
            && e.Message.Contains(page.Page.PageId));
    }
}
