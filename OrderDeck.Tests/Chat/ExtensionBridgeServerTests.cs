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
        string? displayName = null) =>
        JsonSerializer.Serialize(new ExtensionMessage(
            Type: "chat",
            Platform: platform,
            Username: username,
            DisplayName: displayName,
            AvatarUrl: null,
            Text: text,
            ExternalId: null,
            Timestamp: null,
            Stats: null));

    private static async Task SendRaw(ClientWebSocket ws, string json) =>
        await ws.SendAsync(Encoding.UTF8.GetBytes(json),
            WebSocketMessageType.Text, true, CancellationToken.None);

    // ── Baseline ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Forwards_chat_message_from_extension_to_ChatBus()
    {
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new TaskCompletionSource<ChatMessage>();
        using var sub = bus.Subscribe(m => received.TrySetResult(m));

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("instagram", "@ayse_y", "MAVI XL aldım"));

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Platform.Should().Be("instagram");
        msg.Username.Should().Be("@ayse_y");
        msg.Text.Should().Be("MAVI XL aldım");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    // ── Dedupe tests ──────────────────────────────────────────────────────────

    [Fact]
    public async Task Duplicate_within_5s_dropped()
    {
        // Same (platform, username, text) sent twice in quick succession — bus
        // must receive exactly 1 message.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "@ali", "AB-25"));
        await Task.Delay(200); // give the server time to process both

        received.Count.Should().Be(1, because: "second identical message within 5s should be dropped");
        server.DedupedCount.Should().Be(1);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Duplicate_after_5s_passes()
    {
        // Same (platform, username, text) sent 6s apart — both must arrive.
        // NOTE: This test sleeps 5.1s intentionally; that's the only reliable
        // way to cross the TTL window without injecting a TimeProvider dependency.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("tiktok", "@veli", "RED-L"));
        await Task.Delay(5_100); // cross the 5s DedupeWindow
        await SendRaw(ws, SerializeChat("tiktok", "@veli", "RED-L"));
        await Task.Delay(200);

        received.Count.Should().Be(2, because: "same message 6s later must be treated as new");
        server.DedupedCount.Should().Be(0);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Case_differences_treated_as_duplicate()
    {
        // Username casing difference — "Ahmet" vs "AHMET" — same payload → 1 msg.
        // The dedupeKey lowercases both username and platform.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("Instagram", "Ahmet", "AB-25"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "AHMET", "AB-25"));
        await Task.Delay(200);

        received.Count.Should().Be(1, because: "case-variant username+platform should match dedupeKey");
        server.DedupedCount.Should().Be(1);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task Different_users_same_text_both_pass()
    {
        // "ali: AB-25" and "veli: AB-25" are different keys — both must arrive.
        var bus = new ChatBus(ringBufferSize: 10);
        await using var server = new ExtensionBridgeServer(bus, port: 0);
        await server.StartAsync(CancellationToken.None);

        var received = new System.Collections.Generic.List<ChatMessage>();
        using var sub = bus.Subscribe(m => { lock (received) received.Add(m); });

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

        await SendRaw(ws, SerializeChat("instagram", "ali", "AB-25"));
        await Task.Delay(50);
        await SendRaw(ws, SerializeChat("instagram", "veli", "AB-25"));
        await Task.Delay(200);

        received.Count.Should().Be(2, because: "different users sending the same code are independent messages");
        server.DedupedCount.Should().Be(0);

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

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"),
            CancellationToken.None);

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
}
