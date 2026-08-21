using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.Chat.Bridge;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class ExtensionBridgeServerTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static string SerializeChat(string platform, string username, string text,
        string? displayName = null, string? externalId = null) =>
        JsonSerializer.Serialize(new ExtensionMessage(
            Type: "chat",
            Platform: platform,
            Username: username,
            DisplayName: displayName,
            AvatarUrl: null,
            Text: text,
            ExternalId: externalId,
            Timestamp: null,
            Stats: null));

    private static async Task SendRaw(ClientWebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>
    /// Köprü artık el sıkışmada <c>Origin</c> doğruluyor (K-02), o yüzden her
    /// test istemcisi uzantının gerçekte göndereceği türden bir kaynak sunmalı.
    /// <see cref="ClientWebSocket"/> kendiliğinden Origin göndermez.
    /// </summary>
    private static async Task<ClientWebSocket> ConnectAsync(
        ExtensionBridgeServer server, string origin = "https://www.tiktok.com")
    {
        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", origin);
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);
        return ws;
    }

    // ── Baseline ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Forwards_chat_message_from_extension_to_ChatBus()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("instagram", "@ayse_y", "MAVI XL aldım"));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Platform.Should().Be("instagram");
        msg.Username.Should().Be("@ayse_y");
        msg.Text.Should().Be("MAVI XL aldım");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    // ── Dedupe tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_externalId_dropped()
    {
        // Same externalId twice → second is a dupe (reconnect / multi-tab).
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25", externalId: "ig-1-abc"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25", externalId: "ig-1-abc"));
        await Task.Delay(200);

        received.Count.Should().Be(1, because: "same externalId is a duplicate (reconnect / dual-tab)");
        server.DedupedCount.Should().Be(1);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Same_text_different_externalId_both_pass()
    {
        // Customer re-types same code in the live broadcast → extension emits
        // a new externalId per DOM node → both must reach the bus (= two orders).
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25", externalId: "ig-1-abc"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25", externalId: "ig-2-def"));
        await Task.Delay(200);

        received.Count.Should().Be(2, because: "different externalIds = different DOM nodes = different orders (re-buy)");
        server.DedupedCount.Should().Be(0);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task ExternalId_dedupe_time_independent()
    {
        // Same externalId resent 5s+ later — still a duplicate (e.g. reconnect
        // long after first send). Server dedupe is per-server-lifetime, not TTL.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("tiktok", "@veli", "RED-L", externalId: "tt-1-x"));
        await Task.Delay(5_100);
        await SendRaw(ws, SerializeChat("tiktok", "@veli", "RED-L", externalId: "tt-1-x"));
        await Task.Delay(200);

        received.Count.Should().Be(1);
        server.DedupedCount.Should().Be(1);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Different_users_same_text_both_pass()
    {
        // Different externalIds → both messages must reach the bus.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("instagram", "ali", "AB-25", externalId: "ig-1-a"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "veli", "AB-25", externalId: "ig-2-b"));
        await Task.Delay(200);

        received.Count.Should().Be(2);
        server.DedupedCount.Should().Be(0);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Missing_externalId_passes_through()
    {
        // Backwards-compat: if a payload arrives without externalId (very old
        // extension version or test traffic), don't drop it. Server dedupe is
        // a belt-and-suspenders layer, not a hard requirement.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("instagram", "@x", "hello", externalId: null));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "@x", "hello", externalId: null));
        await Task.Delay(200);

        received.Count.Should().Be(2, because: "no externalId → server can't dedupe, both pass");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task DebugStats_message_logged_not_published()
    {
        // A "debug-stats" payload must NOT produce a ChatMessage on the bus.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var publishCount = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref publishCount));

        using var ws = await ConnectAsync(server);

        var statsPayload = JsonSerializer.Serialize(new
        {
            type = "debug-stats",
            platform = "instagram",
            stats = new
            {
                scanCount = 50,
                commentsObserved = 120,
                deduped = 5,
                sent = 115,
                observerBursts = 30,
                scanIntervalMs = 200,
                dedupeWindowMs = 5000,
                windowStart = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() - 10_000,
                windowEnd = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                windowDurationMs = 10_000,
                dedupeCacheSize = 42
            }
        });

        await SendRaw(ws, statsPayload);
        await Task.Delay(200); // let server process the frame

        publishCount.Should().Be(0, because: "debug-stats must not be forwarded to the chat bus");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Watchdog_message_logged_not_published()
    {
        // "watchdog" (stall kurtarma: nudge/reload) olayı ChatBus'a düşmemeli;
        // sadece loglanır. Eski payload alanlarıyla da çakışmamalı.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var publishCount = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref publishCount));

        using var ws = await ConnectAsync(server);

        var payload = JsonSerializer.Serialize(new
        {
            type = "watchdog",
            platform = "instagram",
            action = "reload",
            sinceSendMs = 372_000L,
            rows = 245
        });
        await SendRaw(ws, payload);
        await Task.Delay(200);

        publishCount.Should().Be(0, because: "watchdog events must not be forwarded to the chat bus");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task ChatDropped_message_logged_not_published()
    {
        // "chat-dropped": extension köprü kapalıyken biriktirdiği yorumların
        // bir kısmını atmak zorunda kaldığını bildirir. Bu bir sayaç raporu —
        // ChatBus'a sipariş olarak düşmemeli, yalnızca loglanmalı.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var publishCount = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref publishCount));

        using var ws = await ConnectAsync(server);

        var payload = JsonSerializer.Serialize(new
        {
            type = "chat-dropped",
            platform = "instagram",
            count = 37,
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
        await SendRaw(ws, payload);
        await Task.Delay(200);

        publishCount.Should().Be(0,
            because: "kayıp raporu bir yorum değil — sohbete sipariş olarak düşmemeli");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Viewers_message_updates_tracker_not_bus()
    {
        // A "viewers" payload feeds the ViewerCountTracker and must NOT produce
        // a ChatMessage on the bus.
        var bus = new ChatBus(ringBufferSize: 10);
        var viewers = new ViewerCountTracker();
        await using var server = new ExtensionBridgeServer(bus, port: 0, viewers: viewers);
        await server.StartAsync(CancellationToken.None);

        var publishCount = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref publishCount));

        using var ws = await ConnectAsync(server);

        var payload = JsonSerializer.Serialize(new
        {
            type = "viewers",
            platform = "instagram",
            count = 412
        });
        await SendRaw(ws, payload);
        await Task.Delay(200);

        viewers.GetSnapshot(TimeSpan.FromMinutes(1)).Total.Should().Be(412);
        publishCount.Should().Be(0, because: "viewers must not be forwarded to the chat bus");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    // ── Origin kapısı (K-02) ─────────────────────────────────────────────────
    //
    // WebSocket CORS'a tabi değil: tarayıcı Origin'i gönderir ama zorlamaz.
    // Kapı açıkken yayıncının açtığı herhangi bir sayfa köprüye bağlanıp sahte
    // sipariş basabiliyordu.

    [Theory]
    [InlineData("https://evil.example")]
    [InlineData("http://localhost:3000")]
    [InlineData("https://eviltiktok.com")]        // sonek karışıklığı
    [InlineData("https://tiktok.com.evil.test")]  // önek karışıklığı
    [InlineData("https://www.instagram.com")]     // IG uzantıdan kaldırıldı
    [InlineData("null")]                          // sandbox'lı iframe
    public async Task Rejects_handshake_from_a_foreign_origin(string origin)
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => ConnectAsync(server, origin));
    }

    [Fact]
    public async Task Rejects_handshake_without_an_origin_header()
    {
        // Tarayıcılar Origin'i her zaman gönderir; göndermeyen istemci tarayıcı
        // değildir — köprünün tek meşru istemcisi ise bir tarayıcı uzantısı.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        using var ws = new ClientWebSocket();
        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
                CancellationToken.None));
    }

    [Theory]
    [InlineData("chrome-extension://abcdefghijklmnopabcdefghijklmnop")]
    [InlineData("https://www.tiktok.com")]
    [InlineData("https://tiktok.com")]
    public async Task Accepts_the_extension_and_its_host_pages(string origin)
    {
        // Kabul edilen kaynak yalnız el sıkışmayı geçmemeli, mesaj da akmalı.
        // Content script'in hangi kaynağı gösterdiği Chrome sürümüne göre
        // değişebilir; yanlış tahmin sohbetin tamamen susması demek olduğu için
        // her iki bağlam da kilitleniyor.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = await ConnectAsync(server, origin);
        await SendRaw(ws, SerializeChat("tiktok", "@veli", "RED-L", externalId: "tt-ok"));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Text.Should().Be("RED-L");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Foreign_page_cannot_inject_a_fake_order()
    {
        // Asıl zarar bu: mezat modelinde ürün kodu içeren yorum bir sipariştir.
        // Yayıncının açtığı kötü niyetli bir sekme, köprüye bağlanabildiği anda
        // uydurma müşteri adına sipariş yaratabiliyordu.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var publishCount = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref publishCount));

        var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://evil.example");
        try
        {
            await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
                CancellationToken.None);
            await SendRaw(ws, SerializeChat("instagram", "@kurban", "AB-25", externalId: "sahte-1"));
        }
        catch (WebSocketException) { /* beklenen: el sıkışma reddedildi */ }
        finally { ws.Dispose(); }

        await Task.Delay(200);
        publishCount.Should().Be(0, "yabancı bir sayfa sohbet akışına sipariş enjekte edememeli");
    }

    [Fact]
    public async Task Health_endpoint_does_not_advertise_wildcard_CORS()
    {
        // Tek tüketici sihirbazın kendi HttpClient'ı — CORS onu ilgilendirmiyor.
        // Wildcard varken rastgele bir sayfa yanıtı okuyup yayıncının OrderDeck
        // çalıştırdığını parmak izleyebiliyordu.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        using var http = new System.Net.Http.HttpClient();
        using var res = await http.GetAsync($"http://localhost:{server.Port}/_health");

        res.IsSuccessStatusCode.Should().BeTrue("sihirbazın 'Doğrula' butonu bu uca bağlı");
        res.Headers.Contains("Access-Control-Allow-Origin").Should().BeFalse();
    }
}
