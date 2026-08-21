using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Overlay;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Overlay;

/// <summary>
/// OBS overlay'ine giden yayının hızlı sohbet ve takılan istemci altında nasıl
/// davrandığı.
///
/// Gerçek bir <see cref="ClientWebSocket"/> ile bağlanılıyor: ölçmek
/// istediğimiz şey TCP penceresi dolduğunda sunucu tarafında ne biriktiği —
/// sahte bir soketle geri basınç hiç oluşmaz, o yüzden uçtan uca kuruluyor.
///
/// Not: .NET 10'da tek sokete eşzamanlı <c>SendAsync</c> soketi abort etmiyor,
/// çağrılar iç kilitte sıraya giriyor. Buradaki kusur eşzamanlılık değil,
/// ateşle-unut gönderimlerin <b>tavansız birikmesi</b>;
/// <see cref="Stalled_client_backlog_is_bounded_and_catches_up_to_the_newest"/>
/// bunu ölçüyor. İlk iki test normal yükte kuyruğun bir şey kaybetmediğini ve
/// sırayı bozmadığını koruyan gerileme testleri.
///
/// Port aralığı (42000+) üretimden (4747, 4757-4760) uzak seçildi.
/// </summary>
public sealed class OverlayBroadcastDeliveryTests : IAsyncLifetime
{
    private readonly List<OverlayHost> _hosts = new();
    private readonly List<ClientWebSocket> _clients = new();

    private (OverlayHost host, ChatBus bus) CreateHost(int port)
    {
        var bus = new ChatBus();
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1714521600L);

        var customerSvc = new CustomerService(
            new CustomerRepository(db), new SessionRepository(db),
            new LabelRepository(db), clock.Object);
        var giveaway = new GiveawayService(
            new GiveawayRepository(db), customerSvc, new GiveawayDrawer(), clock.Object);

        var host = new OverlayHost(bus, giveaway, port: port,
            log: NullLogger<OverlayHost>.Instance, fallbackPorts: Array.Empty<int>());
        _hosts.Add(host);
        return (host, bus);
    }

    private async Task<ClientWebSocket> ConnectAsync(int port, string path, CancellationToken ct)
    {
        var ws = new ClientWebSocket();
        _clients.Add(ws);
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}{path}"), ct);
        return ws;
    }

    private static ChatMessage Msg(int i) => new(
        Id: i.ToString(),
        Platform: "youtube",
        ExternalId: null,
        Username: $"user{i}",
        DisplayName: $"User {i}",
        AvatarUrl: null,
        Text: $"mesaj-{i}",
        ReceivedAt: 1714521600L + i,
        Badges: Array.Empty<string>());

    /// <summary>
    /// Arka planda okuyup gelen çerçeveleri biriktirir. <b>Bekleyen bir
    /// <c>ReceiveAsync</c> iptal edilirse .NET soketi abort eder</b> — yani
    /// okumayı token'la kesmek, ölçmek istediğimiz durumu testin kendisi
    /// üretir. Bu yüzden okuma hiç iptal edilmiyor; doğal olarak soket
    /// kapandığında bitiyor.
    /// </summary>
    private sealed class FrameReader
    {
        private readonly List<JsonElement> _frames = new();
        private readonly WebSocket _ws;

        public FrameReader(WebSocket ws)
        {
            _ws = ws;
            Loop = ReadLoopAsync();
        }

        public Task Loop { get; }

        public IReadOnlyList<JsonElement> Frames
        {
            get { lock (_frames) return _frames.ToList(); }
        }

        public int CountOf(string type)
        {
            lock (_frames)
                return _frames.Count(f => f.GetProperty("type").GetString() == type);
        }

        private async Task ReadLoopAsync()
        {
            var buf = new byte[64 * 1024];
            var pending = new MemoryStream();
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    var res = await _ws.ReceiveAsync(buf, CancellationToken.None);
                    if (res.MessageType == WebSocketMessageType.Close) break;

                    pending.Write(buf, 0, res.Count);
                    if (!res.EndOfMessage) continue;

                    var text = Encoding.UTF8.GetString(pending.ToArray());
                    pending.SetLength(0);
                    var frame = JsonDocument.Parse(text).RootElement.Clone();
                    lock (_frames) _frames.Add(frame);
                }
            }
            catch (WebSocketException) { /* soket koptu — döngü biter */ }
            catch (OperationCanceledException) { }
        }

        /// <summary>Beklenen sayıya ulaşana ya da süre dolana dek bekler.</summary>
        public async Task WaitForAsync(string type, int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (CountOf(type) >= count) return;
                if (Loop.IsCompleted) return; // soket öldü, daha fazla gelmeyecek
                await Task.Delay(25);
            }
        }

        /// <summary>Sohbet mesajlarının metinleri, geliş sırasıyla.</summary>
        public IReadOnlyList<string> ChatTexts()
        {
            lock (_frames)
                return _frames
                    .Where(f => f.GetProperty("type").GetString() == "chat.message")
                    .Select(f => f.GetProperty("data").GetProperty("text").GetString()!)
                    .ToList();
        }

        /// <summary>Belirli bir sohbet mesajı gelene dek bekler.</summary>
        public async Task<bool> WaitForChatTextAsync(Func<string, bool> match, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (ChatTexts().Any(match)) return true;
                if (Loop.IsCompleted) return ChatTexts().Any(match);
                await Task.Delay(25);
            }
            return ChatTexts().Any(match);
        }
    }

    // ── Hızlı sohbet: tek bir mesaj bile düşmemeli, soket ayakta kalmalı ─────

    [Fact]
    public async Task Rapid_publishes_all_reach_the_client_and_socket_stays_open()
    {
        const int port = 42000;
        const int messageCount = 200;

        var (host, bus) = CreateHost(port);
        await host.StartAsync();

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ws = await ConnectAsync(port, "/ws/chat", ct.Token);
        var reader = new FrameReader(ws);

        // Sunucu istemciyi kaydedip snapshot'ı yollasın.
        await reader.WaitForAsync("chat.snapshot", 1, TimeSpan.FromSeconds(5));

        // ChatBus.Publish çağıranın ipliğinde senkron koşuyor: mesajlar peş
        // peşe aynı sokete gidiyor.
        for (var i = 0; i < messageCount; i++)
            bus.Publish(Msg(i));

        await reader.WaitForAsync("chat.message", messageCount, TimeSpan.FromSeconds(15));

        ws.State.Should().Be(WebSocketState.Open,
            "eşzamanlı SendAsync soketi abort ederse OBS overlay'i yayın ortasında ölür");

        var delivered = reader.Frames
            .Where(f => f.GetProperty("type").GetString() == "chat.message")
            .Select(f => f.GetProperty("data").GetProperty("text").GetString())
            .ToList();

        delivered.Should().HaveCount(messageCount,
            "yayınlanan her sohbet mesajı overlay'e ulaşmalı");
        delivered.Should().BeEquivalentTo(
            Enumerable.Range(0, messageCount).Select(i => $"mesaj-{i}"),
            options => options.WithStrictOrdering(),
            "mesajlar yayın sırasını korumalı");
    }

    // ── İki istemci: biri düşse bile diğeri etkilenmemeli ────────────────────

    [Fact]
    public async Task One_client_disconnecting_does_not_disturb_the_other()
    {
        const int port = 42100;
        const int messageCount = 100;

        var (host, bus) = CreateHost(port);
        await host.StartAsync();

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var survivor = await ConnectAsync(port, "/ws/chat", ct.Token);
        var quitter = await ConnectAsync(port, "/ws/chat", ct.Token);
        var reader = new FrameReader(survivor);
        await reader.WaitForAsync("chat.snapshot", 1, TimeSpan.FromSeconds(5));

        quitter.Abort();

        for (var i = 0; i < messageCount; i++)
            bus.Publish(Msg(i));

        await reader.WaitForAsync("chat.message", messageCount, TimeSpan.FromSeconds(15));

        survivor.State.Should().Be(WebSocketState.Open);
        reader.CountOf("chat.message").Should().Be(messageCount,
            "bir istemcinin kopması diğerinin yayınını kesmemeli");
    }

    // ── Takılan istemci: biriken iş tavanlı olmalı, en yeniye yetişmeli ──────

    /// <summary>
    /// Y-04'ün ölçtüğü asıl kusur. İstemci bilerek okumuyor; TCP penceresi
    /// dolunca sunucudaki gönderim askıda kalıyor. Eski kodda her yayın
    /// <c>_ = SendBytes(...)</c> ile yeni bir ateşle-unut görev açıyordu:
    /// görev de, çerçevenin <c>byte[]</c>'i de gönderim tamamlanana dek canlı
    /// kalıyordu ve bunun bir <b>tavanı yoktu</b>. İstemci sonunda okumaya
    /// dönünce de 3000 mesajlık birikmiş sırayı baştan izliyordu — canlı
    /// yayında değersiz, bellekte pahalı.
    ///
    /// Sınırlı kuyrukla: birikme kapasiteyle sınırlı, en eskiler düşüyor ve
    /// overlay saniyeler içinde <i>en yeni</i> mesaja yetişiyor.
    /// </summary>
    [Fact]
    public async Task Stalled_client_backlog_is_bounded_and_catches_up_to_the_newest()
    {
        const int port = 42200;
        const int messageCount = 3000;

        var (host, bus) = CreateHost(port);
        await host.StartAsync();

        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        var stalled = await ConnectAsync(port, "/ws/chat", ct.Token);

        // Loopback tamponlarını taşıracak kadar veri: mesaj başına 8 KB.
        var padding = new string('x', 8 * 1024);
        for (var i = 0; i < messageCount; i++)
            bus.Publish(Msg(i) with { Text = $"mesaj-{i}-{padding}" });

        // Gönderimin gerçekten askıda kalması için zaman tanı.
        await Task.Delay(1000, ct.Token);

        // Şimdi okumaya başla.
        var reader = new FrameReader(stalled);
        var newest = $"mesaj-{messageCount - 1}-";
        var caughtUp = await reader.WaitForChatTextAsync(
            t => t.StartsWith(newest, StringComparison.Ordinal),
            TimeSpan.FromSeconds(60));

        stalled.State.Should().Be(WebSocketState.Open);
        caughtUp.Should().BeTrue("overlay okumaya dönünce en yeni mesaja yetişmeli");

        var delivered = reader.ChatTexts();

        // Asıl ayrım noktası: tavansız birikmede 3000'in tamamı teslim edilirdi.
        // Slack, kuyruk dışında yolda olan çerçeveler içindir: pompadaki bir
        // gönderim + Kestrel çıkış tamponu + soketin SO_SNDBUF/SO_RCVBUF'u.
        delivered.Count.Should().BeLessThanOrEqualTo(OverlayHost.ClientQueueCapacity + 128,
            "takılan istemcide biriken iş kuyruk kapasitesiyle sınırlı olmalı");

        // Düşen çerçeveler hep en eskiler: gelen dizi yayın sırasını korumalı.
        var indices = delivered
            .Select(t => int.Parse(t.Substring("mesaj-".Length, t.IndexOf("-x", StringComparison.Ordinal) - "mesaj-".Length)))
            .ToList();
        indices.Should().BeInAscendingOrder("kuyruk FIFO; düşen çerçeveler sırayı bozmamalı");
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        foreach (var c in _clients)
        {
            try { c.Abort(); c.Dispose(); } catch { }
        }
        foreach (var h in _hosts)
        {
            try { await h.StopAsync(); } catch { }
        }
    }
}
