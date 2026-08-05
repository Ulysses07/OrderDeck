using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Ingestors.Instagram;
using OrderDeck.Core.Chat;
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
            // Extension ile aynı anahtar biçimi — müşteri eşleştirmesi buna bağlı.
            received[0].Username.Should().Be("@veli");
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
