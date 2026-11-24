using System;
using System.Collections.Generic;
using System.Linq;
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
using OrderDeck.Tests.TestHelpers;
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

    // ── Tek etkin kaynak kapısı (R7-08) ──────────────────────────────────────
    //
    // Aynı TikTok yayını iki sekmede açıksa aynı yorum iki AYRI externalId ile
    // gelir (id sekme-yerel runId içerir) → externalId dedupe ikisini de
    // geçirir → aynı sipariş iki kez düşer. Uzantı bunu çözemez (sekmeler
    // birbirinden habersiz); köprü platform başına TEK etkin kaynağı kabul
    // eder, pasif sekmeler yedek olarak bağlı kalır.

    [Fact]
    public async Task Ikinci_sekmeden_gelen_ayni_yorum_yayina_dusmez()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var sekme1 = await ConnectAsync(server);
        using var sekme2 = await ConnectAsync(server);

        // Aynı yorum, iki sekme, iki FARKLI externalId (sekme-yerel runId).
        await SendRaw(sekme1, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-100-run1-a"));
        await Task.Delay(200);
        await SendRaw(sekme2, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-100-run2-a"));
        await Task.Delay(200);

        received.Count.Should().Be(1,
            because: "aynı yayını gösteren ikinci sekmenin kopyası siparişe dönüşmemeli");
        server.PassiveSourceDroppedCount.Should().Be(1);

        await sekme1.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await sekme2.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Aktif_kaynak_kapaninca_pasif_kaynak_hemen_devralir()
    {
        // Sekme kapanır/çöker → bağlantı düşer → pasif sekme İLK mesajında
        // devralır. Bayatlama süresi beklenmez; kopuş anında failover.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        var sekme1 = await ConnectAsync(server);
        using var sekme2 = await ConnectAsync(server);

        await SendRaw(sekme1, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-r1-a"));
        await Task.Delay(200);

        await sekme1.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        sekme1.Dispose();
        await Task.Delay(300); // sunucu kapanışı işleyip etkin kaynağı bıraksın

        await SendRaw(sekme2, SerializeChat("tiktok", "@veli", "CD-10", externalId: "tt-r2-b"));
        await Task.Delay(200);

        received.Count.Should().Be(2,
            because: "etkin sekme koptuğunda pasif sekme beklemeden devralmalı");
        received[1].Text.Should().Be("CD-10");
    }

    [Fact]
    public async Task Bayat_aktif_kaynak_yeni_kaynaga_devredilir()
    {
        // Yayın bitmiş ama sekme açık kalmış (zombi): bağlantı canlı, mesaj yok.
        // Yeni yayının sekmesi geldiğinde bayatlama eşiği aşılmışsa devralmalı.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0, sourceStaleAfterMs: 300);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var zombi = await ConnectAsync(server);
        using var yeni = await ConnectAsync(server);

        await SendRaw(zombi, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-z-1"));
        await Task.Delay(500); // bayatlama eşiğinin (300ms) üstünde sessizlik

        await SendRaw(yeni, SerializeChat("tiktok", "@veli", "CD-10", externalId: "tt-y-1"));
        await Task.Delay(200);

        received.Count.Should().Be(2,
            because: "susan etkin kaynak bayatlayınca yeni kaynak devralmalı");
        received[1].Text.Should().Be("CD-10");

        // Devirden sonra eski (zombi) kaynak artık pasif — mesajı düşmemeli.
        await SendRaw(zombi, SerializeChat("tiktok", "@ayse", "EF-5", externalId: "tt-z-2"));
        await Task.Delay(200);

        received.Count.Should().Be(2);
        server.PassiveSourceDroppedCount.Should().Be(1);

        await zombi.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await yeni.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    // ── Devir penceresi kopya süzgeci (R7-08 / R9-AC-047-048) ────────────────
    //
    // Devir anının açığı: eski kaynak yorumu ulaştırıp gider, yeni kaynağın
    // GECİKMİŞ kopyası devirden sonra gelir — externalId sekme-yerel olduğu
    // için _seen yakalayamaz. Süzgeç yalnız BAŞKA bağlantının pencere içinde
    // ulaştırdığı birebir (kullanıcı+metin) çiftini düşürür; aynı bağlantıdan
    // tekrar (gerçek re-buy, #93) ve pencere dışı aynı metin etkilenmez.

    [Fact]
    public async Task Devir_sonrasi_gecikmis_kopya_suzulur()
    {
        // A yorumu ulaştırır ve KAPANIR; B'nin aynı yoruma ait gecikmiş
        // kopyası (farklı externalId) devirden sonra gelir → süzülmeli.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        var sekmeA = await ConnectAsync(server);
        using var sekmeB = await ConnectAsync(server);

        await SendRaw(sekmeA, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-run1-a"));
        await Task.Delay(200);

        await sekmeA.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        sekmeA.Dispose();
        await Task.Delay(300); // sunucu kapanışı işleyip etkin kaynağı bıraksın

        // B devralır ama ilk mesajı A'nın az önce ulaştırdığı yorumun kopyası.
        await SendRaw(sekmeB, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-run2-a"));
        await Task.Delay(200);

        received.Count.Should().Be(1,
            because: "devir sonrası gecikmiş kopya ikinci siparişe dönüşmemeli");
        server.HandoverDedupedCount.Should().Be(1);

        // B'nin YENİ yorumu normal akmalı — süzgeç devri kilitlemez.
        await SendRaw(sekmeB, SerializeChat("tiktok", "@veli", "CD-10", externalId: "tt-run2-b"));
        await Task.Delay(200);
        received.Count.Should().Be(2);

        await sekmeB.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Bayat_devir_sonrasi_gecikmis_kopya_suzulur()
    {
        // Bayatlama yoluyla devirde de aynı açık: zombi son yorumu ulaştırdı,
        // yeni kaynağın o yoruma ait kopyası devir anında gelir → süzülmeli.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0, sourceStaleAfterMs: 300);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var zombi = await ConnectAsync(server);
        using var yeni = await ConnectAsync(server);

        await SendRaw(zombi, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-z-1"));
        await Task.Delay(500); // bayatlama eşiğinin (300ms) üstünde sessizlik

        await SendRaw(yeni, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-y-1"));
        await Task.Delay(200);

        received.Count.Should().Be(1,
            because: "bayat devirde de gecikmiş kopya siparişe dönüşmemeli");
        server.HandoverDedupedCount.Should().Be(1);

        await zombi.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await yeni.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Devir_penceresi_disinda_ayni_metin_yeni_siparistir()
    {
        // Pencere dolduktan sonra gelen aynı (kullanıcı+metin) gerçek bir
        // yeniden alım olabilir (#93) — süzülmemeli. Kısa pencereyle sınanır.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0,
            handoverDedupeWindowMs: 300);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        var sekmeA = await ConnectAsync(server);
        using var sekmeB = await ConnectAsync(server);

        await SendRaw(sekmeA, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-p1-a"));
        await Task.Delay(200);

        await sekmeA.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        sekmeA.Dispose();
        await Task.Delay(500); // pencere (300ms) dolmuş olsun

        await SendRaw(sekmeB, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-p2-a"));
        await Task.Delay(200);

        received.Count.Should().Be(2,
            because: "pencere dışı aynı metin gerçek yeniden alım olabilir; süzülmemeli");
        server.HandoverDedupedCount.Should().Be(0);

        await sekmeB.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Farkli_platformlarin_etkin_kaynaklari_bagimsiz()
    {
        // Kapı platform BAŞINA: bir bağlantının tiktok'ta etkin olması başka
        // bağlantının başka platformda etkin olmasını engellememeli.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var conn1 = await ConnectAsync(server);
        using var conn2 = await ConnectAsync(server);

        await SendRaw(conn1, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-p1"));
        await Task.Delay(200);
        await SendRaw(conn2, SerializeChat("instagram", "@veli", "CD-10", externalId: "ig-p1"));
        await Task.Delay(200);

        received.Count.Should().Be(2,
            because: "farklı platformların kaynak kapıları birbirinden bağımsız");
        server.PassiveSourceDroppedCount.Should().Be(0);

        await conn1.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await conn2.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Etkin_kaynagin_kendi_mesajlari_akmaya_devam_eder()
    {
        // Kapı tek bağlantılı olağan akışı hiç etkilememeli.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = await ConnectAsync(server);

        await SendRaw(ws, SerializeChat("tiktok", "@ali", "AB-25", externalId: "tt-s1"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("tiktok", "@veli", "CD-10", externalId: "tt-s2"));
        await Task.Delay(200);

        received.Count.Should().Be(2);
        server.PassiveSourceDroppedCount.Should().Be(0);

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

    // ── Yol denetimi (K-02) ──────────────────────────────────────────────────
    //
    // Köprü eskiden HERHANGİ bir yola gelen yükseltme isteğini kabul ediyordu.
    // Uzantı her zaman /extension'a bağlanır; başka yol ya eski bir istemci ya
    // da köprüyü tarayan bir şey.

    [Theory]
    [InlineData("/")]
    [InlineData("/ws")]
    [InlineData("/extension/")]
    [InlineData("/Extension")]   // büyük/küçük harf duyarlı olmalı
    public async Task Rejects_websocket_upgrade_on_any_other_path(string path)
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Origin", "https://www.tiktok.com");

        await Assert.ThrowsAnyAsync<WebSocketException>(
            () => ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}{path}"),
                CancellationToken.None));
    }

    // ── Mesaj boyu sınırı (K-02) ─────────────────────────────────────────────

    [Fact]
    public async Task Oversized_message_closes_the_connection_without_publishing()
    {
        // Sınır yokken tek bir istemci EndOfMessage'ı hiç göndermeden çerçeve
        // akıtarak uygulamanın belleğini tüketebiliyordu.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var publishCount = 0;
        using var sub = bus.Subscribe(_ => Interlocked.Increment(ref publishCount));

        using var ws = await ConnectAsync(server);

        // 64 KB sınırının üstünde tek bir chat mesajı.
        await SendRaw(ws, SerializeChat(
            "tiktok", "@spam", new string('A', 200_000), externalId: "dev-1"));

        // Sunucu MessageTooBig ile kapatmalı.
        var buf = new byte[1024];
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (ws.State == WebSocketState.Open && DateTime.UtcNow < deadline)
        {
            try { await ws.ReceiveAsync(buf, CancellationToken.None); }
            catch (WebSocketException) { break; }
        }

        publishCount.Should().Be(0, "sınırı aşan mesaj sohbet akışına girmemeli");
        ws.State.Should().NotBe(WebSocketState.Open);
    }

    [Fact]
    public async Task Normal_sized_message_still_flows_after_the_size_cap()
    {
        // Sınır gerçek trafiği kesmemeli: avatar URL'li bir chat satırı ~1 KB.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = await ConnectAsync(server);
        await SendRaw(ws, SerializeChat(
            "tiktok", "@ayse", new string('m', 4_000), externalId: "uzun-ama-normal"));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Text.Should().HaveLength(4_000);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    // ── TikTok sekmesi izleme (tasarım: Sohbeti yenile §5.4) ─────────────────
    //
    // NEDEN: şeritteki TikTok satırı "köprüye bir soket bağlı" demek değil, "kendini TikTok canlı
    // yayın sekmesi olarak tanıtmış bir bağlantı açık" demek. Uzantının arka planı ve açılır
    // penceresi de aynı uca kısa ömürlü yoklama soketleri açar; açık soket saymak satırı yanlış
    // yere yeşil yapardı. Köprü durumu yalnız `connected` iletisinden türetir.

    private static string SerializeConnected(string platform) =>
        JsonSerializer.Serialize(new
        {
            type = "connected",
            platform,
            url = "https://www.tiktok.com/live",
            timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        });

    private static string NewUser() => $"@u{Guid.NewGuid():N}";

    /// <summary>Koşul sağlanana dek yoklar. Üst sınır bekleme süresi değildir: koşul sağlanınca
    /// hemen döner.</summary>
    private static async Task Eventually(Func<bool> condition, string because)
    {
        var deadline = DateTime.UtcNow + TestPatience.Upper;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                throw new Xunit.Sdk.XunitException($"Koşul süresinde sağlanmadı: {because}");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Bağlantının o ana dek gönderdiği her şeyin işlenmesini bekler: aynı soketten bir izleyici
    /// sayısı yollar ve sayacın onu gördüğünü yoklar (bir bağlantının çerçeveleri sırayla işlenir).
    /// "Bir şey OLMADI" diyen testler, iletinin henüz işlenmediği bir ana bakıp boşuna geçmesin.
    /// </summary>
    private static async Task Drain(ClientWebSocket ws, ViewerCountTracker viewers, int marker)
    {
        await SendRaw(ws, JsonSerializer.Serialize(new { type = "viewers", platform = "tiktok", count = marker }));
        await Eventually(
            () => viewers.GetSnapshot(TimeSpan.FromMinutes(1)).Total == marker,
            "köprü bağlantının önceki çerçevelerini işledi");
    }

    private static ChatSourceHealth? TikTok(ChatSourceMonitor sources) =>
        sources.Snapshot().SingleOrDefault(e => e.Platform == "tiktok");

    private static int Count(List<ChatMessage> received)
    {
        lock (received) return received.Count;
    }

    private sealed class TrialSwitch(bool isTrialMode) : ITrialModeProbe
    {
        public bool IsTrialMode { get; set; } = isTrialMode;
    }

    [Fact]
    public async Task Connected_message_marks_the_platform_live()
    {
        var sources = new ChatSourceMonitor();
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, sources: sources);
        await server.StartAsync(CancellationToken.None);

        using var tab = await ConnectAsync(server);
        await SendRaw(tab, SerializeConnected("tiktok"));

        await Eventually(() => TikTok(sources)?.State == ChatSourceState.Live, "sekme kendini tanıttı");

        await tab.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task A_socket_that_never_says_connected_is_not_a_tab()
    {
        // Uzantının arka planı ve açılır penceresi köprüyü böyle yoklar: bağlanır, kapatır.
        var sources = new ChatSourceMonitor();
        var viewers = new ViewerCountTracker();
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, viewers: viewers, sources: sources);
        await server.StartAsync(CancellationToken.None);

        using (var probe = await ConnectAsync(server, "chrome-extension://abcdefghijklmnopabcdefghijklmnop"))
        {
            await Drain(probe, viewers, marker: 3);
            await probe.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        }
        await Eventually(() => server.ActiveWebSocketCount == 0, "yoklama soketinin kapanışı işlendi");

        sources.Snapshot().Should().BeEmpty("açık soket saymak 'TikTok sekmesi bağlı' demek değildir");
    }

    [Fact]
    public async Task Connected_message_naming_another_platform_is_ignored()
    {
        // Köprünün tek kaynağı TikTok. Kendini başka platform diye tanıtan bir bağlantı, o
        // platformun kendi servisinin yazdığı durumu ezememeli; şeride keyfi bir ad da sokamamalı.
        var sources = new ChatSourceMonitor();
        var viewers = new ViewerCountTracker();
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, viewers: viewers, sources: sources);
        await server.StartAsync(CancellationToken.None);

        using var tab = await ConnectAsync(server);
        await SendRaw(tab, SerializeConnected("youtube"));
        await Drain(tab, viewers, marker: 5);

        sources.Snapshot().Should().BeEmpty();

        await tab.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Closing_the_last_tab_reports_no_broadcast()
    {
        var sources = new ChatSourceMonitor();
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, sources: sources);
        await server.StartAsync(CancellationToken.None);

        var tab = await ConnectAsync(server);
        // Kendini iki kez tanıtan sekme yine TEK sekmedir: kapanınca satır yeşil kalmamalı.
        await SendRaw(tab, SerializeConnected("tiktok"));
        await SendRaw(tab, SerializeConnected("tiktok"));
        await Eventually(() => TikTok(sources)?.State == ChatSourceState.Live, "sekme kendini tanıttı");

        await tab.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        tab.Dispose();

        await Eventually(() => TikTok(sources)?.State == ChatSourceState.NoBroadcast, "son sekme kapandı");
        TikTok(sources)!.WasLiveThisSession.Should().BeTrue("şerit 'sekme bağlı değil' diyebilsin");
    }

    [Fact]
    public async Task Second_tab_keeps_the_platform_live_when_the_first_closes()
    {
        var sources = new ChatSourceMonitor();
        var viewers = new ViewerCountTracker();
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, viewers: viewers, sources: sources);
        await server.StartAsync(CancellationToken.None);

        var first = await ConnectAsync(server);
        using var second = await ConnectAsync(server);
        await SendRaw(first, SerializeConnected("tiktok"));
        await Drain(first, viewers, marker: 7);
        await SendRaw(second, SerializeConnected("tiktok"));
        await Drain(second, viewers, marker: 9);

        await first.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        first.Dispose();
        // Köprü sayaçtan düşmeden ÖNCE bağlantının sekme kaydını siler; sayaç 1 ise kayıt silinmiştir.
        await Eventually(() => server.ActiveWebSocketCount == 1, "ilk sekmenin kapanışı işlendi");

        TikTok(sources)!.State.Should().Be(ChatSourceState.Live, "ikinci sekme hâlâ bağlı");

        await second.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await Eventually(() => TikTok(sources)?.State == ChatSourceState.NoBroadcast, "son sekme de kapandı");
    }

    [Fact]
    public async Task Tab_in_trial_mode_has_no_row_until_the_trial_ends()
    {
        // Denemede köprü sessiz: mesajları atılan sekmenin satırı yeşil görünmemeli. Lisans sekme
        // bağlıyken etkinleşirse (uygulama açılırken de olur) satır, sekmenin sonraki mesajında gelir.
        var sources = new ChatSourceMonitor();
        var viewers = new ViewerCountTracker();
        var trial = new TrialSwitch(isTrialMode: true);
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, trialProbe: trial, viewers: viewers, sources: sources);
        await server.StartAsync(CancellationToken.None);

        using var tab = await ConnectAsync(server);
        await SendRaw(tab, SerializeConnected("tiktok"));
        await Drain(tab, viewers, marker: 11);
        sources.Snapshot().Should().BeEmpty("denemede TikTok satırı yok");

        trial.IsTrialMode = false;
        await SendRaw(tab, SerializeChat("tiktok", NewUser(), "AB-25", externalId: "tt-trial-1"));

        await Eventually(() => TikTok(sources)?.State == ChatSourceState.Live, "deneme bitti, sekme bağlı");

        await tab.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Entering_trial_mode_removes_the_row_on_the_next_message()
    {
        var sources = new ChatSourceMonitor();
        var trial = new TrialSwitch(isTrialMode: false);
        await using var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, trialProbe: trial, sources: sources);
        await server.StartAsync(CancellationToken.None);

        using var tab = await ConnectAsync(server);
        await SendRaw(tab, SerializeConnected("tiktok"));
        await Eventually(() => TikTok(sources)?.State == ChatSourceState.Live, "sekme kendini tanıttı");

        trial.IsTrialMode = true;
        await SendRaw(tab, SerializeChat("tiktok", NewUser(), "AB-25", externalId: "tt-trial-2"));

        await Eventually(
            () => TikTok(sources)?.State == ChatSourceState.Stopped,
            "mesajları atılan sekmenin satırı yeşil kalmamalı");

        await tab.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    // ── "Sohbeti yenile": etkin kaynak kilidini bırakma (tasarım §5.2) ───────
    //
    // NEDEN: etkin sekme açık ama donmuşsa (bağlantı canlı, yorum gelmiyor) yedek sekme ancak
    // 60 sn'lik bayatlama dolunca devralır. Yenileme kilidi bırakır: sıradaki mesajını ulaştıran
    // sekme hemen etkin olur. Devir penceresi süzgeci çalışmayı sürdürür; yenileme çift sipariş
    // üretmemeli.

    [Fact]
    public async Task Releasing_sources_lets_another_tab_take_over_immediately()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var active = await ConnectAsync(server);
        using var standby = await ConnectAsync(server);

        await SendRaw(active, SerializeChat("tiktok", NewUser(), "AB-25", externalId: "tt-a-1"));
        await Eventually(() => Count(received) == 1, "etkin sekmenin mesajı yayımlandı");
        await SendRaw(standby, SerializeChat("tiktok", NewUser(), "CD-10", externalId: "tt-s-1"));
        await Eventually(() => server.PassiveSourceDroppedCount == 1, "yedek sekmenin mesajı atlandı");

        server.ReleaseActiveSources();

        await SendRaw(standby, SerializeChat("tiktok", NewUser(), "EF-5", externalId: "tt-s-2"));
        await Eventually(() => Count(received) == 2, "kilit bırakılınca yedek sekme hemen devraldı");
        lock (received) received[1].Text.Should().Be("EF-5");

        // Devralan artık etkin: eski etkin sekme yedeğe düşer, iki sekme birden yayımlamaz.
        await SendRaw(active, SerializeChat("tiktok", NewUser(), "GH-7", externalId: "tt-a-2"));
        await Eventually(() => server.PassiveSourceDroppedCount == 2, "eski etkin sekme artık yedek");
        Count(received).Should().Be(2);

        await active.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await standby.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Handover_duplicate_is_still_filtered_after_release()
    {
        // Etkin sekme yorumu ulaştırdı; kilit bırakıldı; öbür sekmenin AYNI yoruma ait kopyası
        // (sekme-yerel, farklı externalId) geldi. İkinci siparişe dönüşmemeli.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var active = await ConnectAsync(server);
        using var standby = await ConnectAsync(server);
        var user = NewUser();

        await SendRaw(active, SerializeChat("tiktok", user, "AB-25", externalId: "tt-run1-a"));
        await Eventually(() => Count(received) == 1, "etkin sekmenin mesajı yayımlandı");

        server.ReleaseActiveSources();

        await SendRaw(standby, SerializeChat("tiktok", user, "AB-25", externalId: "tt-run2-a"));
        await Eventually(() => server.HandoverDedupedCount == 1, "gecikmiş kopya süzüldü");
        Count(received).Should().Be(1);

        await active.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await standby.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Reconnect_signal_releases_sources()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        var reconnect = new ChatReconnectSignal();
        var log = new CapturingLogger<ExtensionBridgeServer>();
        await using var server = new ExtensionBridgeServer(bus, port: 0, log: log, reconnect: reconnect);
        await server.StartAsync(CancellationToken.None);

        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var active = await ConnectAsync(server);
        using var standby = await ConnectAsync(server);

        await SendRaw(active, SerializeChat("tiktok", NewUser(), "AB-25", externalId: "tt-sig-a"));
        await Eventually(() => Count(received) == 1, "etkin sekmenin mesajı yayımlandı");

        reconnect.Request();

        await SendRaw(standby, SerializeChat("tiktok", NewUser(), "CD-10", externalId: "tt-sig-s"));
        await Eventually(() => Count(received) == 2, "yenileme sinyali kilidi bıraktı");

        // Tasarım §5.7: her yenileme isteği ve kaynağın karşılığı günlüğe bir satır yazar.
        log.Entries.Should().ContainSingle(e => e.Message.Contains("Sohbet yenileme"));

        await active.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
        await standby.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Refresh_with_nothing_to_release_still_logs_one_line()
    {
        // "Kimin yapacak işi yoktu" da günlükte görünür: sahada "yeniledim, bir şey olmadı"
        // sorusunun cevabı bu satır.
        var log = new CapturingLogger<ExtensionBridgeServer>();
        await using var server = new ExtensionBridgeServer(new ChatBus(ringBufferSize: 10), port: 0, log: log);

        server.ReleaseActiveSources();

        log.Entries.Should().ContainSingle(e => e.Message.Contains("Sohbet yenileme"));
    }

    [Fact]
    public async Task Disposed_bridge_no_longer_listens_to_the_reconnect_signal()
    {
        var reconnect = new ChatReconnectSignal();
        var log = new CapturingLogger<ExtensionBridgeServer>();
        var server = new ExtensionBridgeServer(
            new ChatBus(ringBufferSize: 10), port: 0, log: log, reconnect: reconnect);
        await server.DisposeAsync();

        reconnect.Request();

        log.Entries.Should().NotContain(e => e.Message.Contains("Sohbet yenileme"));
    }
}
