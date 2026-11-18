using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramLiveCommentsPollerTests
{
    /// <summary>Sıradaki yanıtları tek tek döndürür; bitince sonuncuyu tekrarlar.</summary>
    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _queue;
        private (HttpStatusCode Status, string Body) _last;

        public ScriptedHandler(params (HttpStatusCode, string)[] responses)
        {
            _queue = new Queue<(HttpStatusCode, string)>(responses);
            _last = responses[^1];
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var next = _queue.Count > 0 ? _queue.Dequeue() : _last;
            return Task.FromResult(new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body),
            });
        }
    }

    private static InstagramLiveCommentsPoller Make(ScriptedHandler handler, IChatBus bus) =>
        new("ig1", "tok", bus, new HttpClient(handler),
            NullLogger<InstagramLiveCommentsPoller>.Instance);

    private static string MediaJson(params string[] comments) =>
        "{\"data\":[{\"id\":\"m1\",\"comments\":{\"data\":[" + string.Join(",", comments) + "]}}]}";

    private static string Comment(string id, string text, string ts, string user) =>
        $"{{\"id\":\"{id}\",\"text\":\"{text}\",\"timestamp\":\"{ts}\",\"username\":\"{user}\"}}";

    // ── Uyarlanabilir aralık (saf) ───────────────────────────────────────────

    [Fact]
    public void NextInterval_tightens_on_overflow()
    {
        var i1 = InstagramLiveCommentsPoller.NextInterval(TimeSpan.FromSeconds(1), overflowed: true);
        i1.Should().Be(TimeSpan.FromMilliseconds(500));

        var i2 = InstagramLiveCommentsPoller.NextInterval(i1, overflowed: true);
        i2.Should().Be(TimeSpan.FromMilliseconds(300)); // taban

        var i3 = InstagramLiveCommentsPoller.NextInterval(i2, overflowed: true);
        i3.Should().Be(TimeSpan.FromMilliseconds(300)); // tabandan aşağı inmez
    }

    [Fact]
    public void NextInterval_relaxes_gradually_back_to_one_second()
    {
        var i = TimeSpan.FromMilliseconds(300);
        for (int n = 0; n < 20; n++)
            i = InstagramLiveCommentsPoller.NextInterval(i, overflowed: false);

        i.Should().Be(TimeSpan.FromSeconds(1)); // tavanı aşmaz
    }

    [Fact]
    public void NextInterval_relaxation_is_one_step_at_a_time()
    {
        InstagramLiveCommentsPoller
            .NextInterval(TimeSpan.FromMilliseconds(300), overflowed: false)
            .Should().Be(TimeSpan.FromMilliseconds(400));
    }

    // ── Döngü davranışı ──────────────────────────────────────────────────────

    [Fact]
    public async Task Publishes_only_comments_that_arrive_after_priming()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        var handler = new ScriptedHandler(
            // 1. çekim: geçmiş → yutulur
            (HttpStatusCode.OK, MediaJson(Comment("c1", "eski", "2026-08-05T12:00:00+0000", "ayse_y"))),
            // 2. çekim: yeni yorum → basılır
            (HttpStatusCode.OK, MediaJson(
                Comment("c2", "yeni", "2026-08-05T12:00:05+0000", "veli"),
                Comment("c1", "eski", "2026-08-05T12:00:00+0000", "ayse_y"))));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await WaitUntil(() => { lock (received) return received.Count >= 1; }, TimeSpan.FromSeconds(5));
        await poller.StopAsync(CancellationToken.None);

        lock (received)
        {
            received.Should().ContainSingle();
            received[0].Text.Should().Be("yeni");
            received[0].ExternalId.Should().Be("c2");
            received[0].Platform.Should().Be("instagram");
            // Form ve eski eklentiyle aynı biçim ("@"sız) — müşteri eşleştirmesi buna bağlı.
            received[0].Username.Should().Be("veli");
            received[0].DisplayName.Should().Be("veli");
        }
    }

    [Fact]
    public async Task Token_error_stops_the_loop_with_a_fatal_reason()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        var handler = new ScriptedHandler(
            (HttpStatusCode.BadRequest,
             "{\"error\":{\"message\":\"expired\",\"type\":\"OAuthException\",\"code\":190}}"));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await poller.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        poller.FatalReason.Should().NotBeNull();
        poller.FatalReason.Should().Contain("bağlantı");
    }

    [Fact]
    public async Task Permission_error_stops_the_loop_with_a_different_message()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        var handler = new ScriptedHandler(
            (HttpStatusCode.Forbidden,
             "{\"error\":{\"message\":\"no perm\",\"type\":\"OAuthException\",\"code\":200}}"));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await poller.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        poller.FatalReason.Should().Contain("izni");
    }

    [Fact]
    public async Task Missing_comments_field_stops_the_loop_as_permission_problem()
    {
        // Alan hiç gelmiyorsa sessizce "yorum yok" sanıp saatlerce boş
        // dönmemeliyiz — bu bir izin arızasıdır, operatöre söylenmeli.
        var bus = new ChatBus(ringBufferSize: 10);
        var handler = new ScriptedHandler((HttpStatusCode.OK, "{\"data\":[{\"id\":\"m1\"}]}"));

        using var poller = Make(handler, bus);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await poller.StartAsync(cts.Token);

        await poller.Completion.WaitAsync(TimeSpan.FromSeconds(5));

        poller.FatalReason.Should().Contain("izni");
    }

    // ── Gözlemci, kota başlığı, devredilen filigran ──────────────────────────
    // Bu bölümde kimlik, token ve izleyici adı yazılmaz, üretilir (depo herkese açık).

    private sealed record Round(InstagramPollKind Kind, string? Message, TimeSpan RetryAfter, string? UsageHeader);

    /// <summary>Log'a "start" / "done:{Kind}", Completed'a tur sonucu yazar; Throws true ise
    /// iki yöntem de kayıttan sonra InvalidOperationException fırlatır.</summary>
    private sealed class RecordingObserver(ConcurrentQueue<string>? log = null) : IInstagramPollObserver
    {
        public ConcurrentQueue<string> Log { get; } = log ?? new();
        public ConcurrentQueue<Round> Completed { get; } = new();
        public bool Throws { get; init; }

        public void RoundStarting()
        {
            Log.Enqueue("start");
            if (Throws) throw new InvalidOperationException("observer failed");
        }

        public void RoundCompleted(InstagramPollKind kind, string? message, TimeSpan retryAfter, string? usageHeader)
        {
            Log.Enqueue($"done:{kind}");
            Completed.Enqueue(new Round(kind, message, retryAfter, usageHeader));
            if (Throws) throw new InvalidOperationException("observer failed");
        }
    }

    /// <summary>ScriptedHandler'ın başlık taşıyan eşi: sıradaki yanıtı döner, bitince sonuncuyu
    /// yineler; Usage null ise X-Business-Use-Case-Usage eklenmez.</summary>
    private sealed class UsageHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body, string? Usage)> _queue;
        private readonly (HttpStatusCode Status, string Body, string? Usage) _last;

        public UsageHandler(params (HttpStatusCode Status, string Body, string? Usage)[] responses)
        {
            _queue = new Queue<(HttpStatusCode Status, string Body, string? Usage)>(responses);
            _last = responses[^1];
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var next = _queue.Count > 0 ? _queue.Dequeue() : _last;
            var response = new HttpResponseMessage(next.Status)
            {
                Content = new StringContent(next.Body),
            };
            if (next.Usage is not null)
                response.Headers.TryAddWithoutValidation("X-Business-Use-Case-Usage", next.Usage);
            return Task.FromResult(response);
        }
    }

    /// <summary>UsageHandler'ın çok değerli eşi: her isteğe aynı yanıtı döner; usages'ın her
    /// öğesi X-Business-Use-Case-Usage başlığının AYRI bir değeri olarak eklenir.</summary>
    private sealed class MultiValueUsageHandler(HttpStatusCode status, string body, params string[] usages)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            };
            response.Headers.TryAddWithoutValidation("X-Business-Use-Case-Usage", usages);
            return Task.FromResult(response);
        }
    }

    private static string New(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    /// <summary>Tek kovalı kota başlığı; işletme kimliği üretilir, <paramref name="minutes"/>
    /// <c>estimated_time_to_regain_access</c> alanına yazılır.</summary>
    private static string UsageHeader(int minutes) =>
        $"{{\"{New("b")}\":[{{\"type\":\"instagram\",\"call_count\":100,\"total_cputime\":25," +
        $"\"total_time\":25,\"estimated_time_to_regain_access\":{minutes}}}]}}";

    /// <summary>MediaJson'ın media kimliği verilen eşi.</summary>
    private static string MediaJsonFor(string mediaId, params string[] comments) =>
        "{\"data\":[{\"id\":\"" + mediaId + "\",\"comments\":{\"data\":[" + string.Join(",", comments) + "]}}]}";

    /// <summary>1. çekim: eski yorum (12:00:00); sonraki çekimler: yeni (12:00:05) + eski.</summary>
    private static ScriptedHandler PrimeThenNew(string oldId, string newId, string user)
    {
        var media = New("m");
        var old = Comment(oldId, "eski", "2026-08-05T12:00:00+0000", user);
        return new ScriptedHandler(
            (HttpStatusCode.OK, MediaJsonFor(media, old)),
            (HttpStatusCode.OK, MediaJsonFor(media,
                Comment(newId, "yeni", "2026-08-05T12:00:05+0000", user), old)));
    }

    /// <summary>Kimliği ve token'ı üretilmiş poller.</summary>
    private static InstagramLiveCommentsPoller MakeObserved(
        HttpMessageHandler handler, IChatBus bus, IInstagramPollObserver? observer = null,
        InstagramCommentWatermark? watermark = null, ILogger<InstagramLiveCommentsPoller>? log = null) =>
        new(New("ig"), New("t"), bus, new HttpClient(handler),
            log ?? NullLogger<InstagramLiveCommentsPoller>.Instance,
            observer: observer, watermark: watermark);

    /// <summary>Poller'ı başlatır, koşul sağlanana dek (üst sınır TestPatience.Upper) bekler, durdurur.</summary>
    private static async Task RunUntil(InstagramLiveCommentsPoller poller, Func<bool> until)
    {
        await poller.StartAsync(CancellationToken.None);
        await WaitUntil(until, TestPatience.Upper);
        await poller.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task Observer_sees_start_then_completion_and_ok_arrives_after_the_publishes()
    {
        var order = new ConcurrentQueue<string>();
        var bus = new ChatBus(ringBufferSize: 50);
        using var sub = bus.Subscribe(_ => order.Enqueue("pub"));

        using var poller = MakeObserved(
            PrimeThenNew(New("c"), New("c"), New("u")), bus, new RecordingObserver(order));
        await RunUntil(poller, () => order.Count >= 5);

        // 1. tur ilk çekim: yayım yok.
        order.Take(5).Should().Equal("start", "done:Ok", "start", "pub", "done:Ok");
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":190}}",
        InstagramPollKind.Fatal, "Instagram bağlantını yenilemen gerekiyor.")]
    [InlineData(HttpStatusCode.OK, "{\"data\":[]}", InstagramPollKind.NoBroadcast, null)]
    public async Task Observer_gets_fatal_with_the_operator_message_and_no_broadcast_on_empty_data(
        HttpStatusCode status, string body, InstagramPollKind kind, string? message)
    {
        var observer = new RecordingObserver();

        using var poller = MakeObserved(new ScriptedHandler((status, body)), new ChatBus(), observer);
        await RunUntil(poller, () => !observer.Completed.IsEmpty);

        observer.Log.Take(2).Should().Equal("start", $"done:{kind}");
        observer.Completed.First().Should().Be(new Round(kind, message, TimeSpan.Zero, null));
    }

    [Fact]
    public async Task Observer_gets_rate_limited_with_the_wait_before_the_poller_sleeps()
    {
        var observer = new RecordingObserver();
        var header = UsageHeader(minutes: 7);
        var handler = new UsageHandler(
            (HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":4}}", header));

        using var poller = MakeObserved(handler, new ChatBus(), observer);
        await RunUntil(poller, () => !observer.Completed.IsEmpty); // bildirim 7 dk'lık beklemeden önce gelir

        observer.Completed.First().Should().Be(
            new Round(InstagramPollKind.RateLimited, null, TimeSpan.FromMinutes(7), header));
    }

    [Fact]
    public async Task Usage_header_is_carried_raw_on_every_response()
    {
        var observer = new RecordingObserver();
        var header = UsageHeader(minutes: 0);
        var body = MediaJson(Comment(New("c"), "x", "2026-08-05T12:00:00+0000", New("u")));

        using var poller = MakeObserved(
            new UsageHandler((HttpStatusCode.OK, body, header), (HttpStatusCode.OK, body, null)),
            new ChatBus(), observer);
        await RunUntil(poller, () => observer.Completed.Count >= 2);

        observer.Completed.Take(2).Should().Equal(
            new Round(InstagramPollKind.Ok, null, TimeSpan.Zero, header),
            new Round(InstagramPollKind.Ok, null, TimeSpan.Zero, null)); // başlık yoksa null
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, "{\"data\":[]}", InstagramPollKind.NoBroadcast)]
    [InlineData(HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":190}}",
        InstagramPollKind.Fatal)]
    [InlineData(HttpStatusCode.InternalServerError, "{\"error\":{\"message\":\"x\",\"code\":2}}",
        InstagramPollKind.Transient)]
    public async Task Usage_header_is_carried_on_non_ok_outcomes(
        HttpStatusCode status, string body, InstagramPollKind kind)
    {
        var observer = new RecordingObserver();
        var header = UsageHeader(minutes: 0);

        using var poller = MakeObserved(new UsageHandler((status, body, header)), new ChatBus(), observer);
        await RunUntil(poller, () => !observer.Completed.IsEmpty);

        var first = observer.Completed.First();
        first.Kind.Should().Be(kind);
        first.UsageHeader.Should().Be(header);
    }

    [Fact]
    public async Task Multi_valued_usage_header_is_joined_and_retry_after_is_read_per_value()
    {
        var observer = new RecordingObserver();
        // İlk değerde bekleme yok (0), ikincide 7 dk: süre ancak değerler tek tek okunursa bulunur.
        var (first, second) = (UsageHeader(minutes: 0), UsageHeader(minutes: 7));
        var handler = new MultiValueUsageHandler(
            HttpStatusCode.BadRequest, "{\"error\":{\"message\":\"x\",\"code\":4}}", first, second);

        using var poller = MakeObserved(handler, new ChatBus(), observer);
        await RunUntil(poller, () => !observer.Completed.IsEmpty);

        // Birleşik metin geçerli JSON değildir; bekleme ondan okunsaydı 1 dakikaya düşerdi.
        observer.Completed.First().Should().Be(
            new Round(InstagramPollKind.RateLimited, null, TimeSpan.FromMinutes(7), first + "," + second));
    }

    [Fact]
    public async Task Throwing_observer_does_not_stop_the_loop()
    {
        var log = new CapturingLogger<InstagramLiveCommentsPoller>();
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new ConcurrentQueue<ChatMessage>();
        using var sub = bus.Subscribe(received.Enqueue);
        var newId = New("c");

        using var poller = MakeObserved(
            PrimeThenNew(New("c"), newId, New("u")), bus, new RecordingObserver { Throws = true }, log: log);
        await RunUntil(poller, () => !received.IsEmpty);

        received.Should().ContainSingle().Which.ExternalId.Should().Be(newId); // iki tur da tamamlandı
        log.Entries.Should().Contain(e => e.Level == LogLevel.Debug && e.Exception != null);
        log.Warnings.Should().BeEmpty();
        poller.FatalReason.Should().BeNull();
    }

    [Fact]
    public async Task Handed_over_watermark_publishes_the_first_fetch()
    {
        var (media, oldId, newId, user) = (New("m"), New("c"), New("c"), New("u"));
        var at = new DateTimeOffset(2026, 8, 5, 12, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();
        var watermark = new InstagramCommentWatermark(); // önceki poller'ın filigranı
        watermark.Advance(media, new[] { new InstagramComment(oldId, "eski", at, user) });
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new ConcurrentQueue<ChatMessage>();
        using var sub = bus.Subscribe(received.Enqueue);
        var handler = new ScriptedHandler((HttpStatusCode.OK, MediaJsonFor(media,
            Comment(newId, "yeni", "2026-08-05T12:00:05+0000", user),
            Comment(oldId, "eski", "2026-08-05T12:00:00+0000", user))));

        using var poller = MakeObserved(handler, bus, watermark: watermark);
        await RunUntil(poller, () => !received.IsEmpty);

        received.Should().ContainSingle().Which.ExternalId.Should().Be(newId); // İLK çekimde basıldı, yutulmadı
    }

    private static async Task WaitUntil(Func<bool> predicate, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (predicate()) return;
            await Task.Delay(25);
        }
    }
}
