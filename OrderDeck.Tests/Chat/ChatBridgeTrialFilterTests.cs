using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiveDeck.Chat.Bridge;
using LiveDeck.Core.Chat;
using Xunit;

namespace LiveDeck.Tests.Chat;

/// <summary>
/// Verifies that <see cref="ExtensionBridgeServer"/> drops non-Instagram messages when
/// the injected <see cref="ITrialModeProbe"/> reports trial mode is active.
/// </summary>
public class ChatBridgeTrialFilterTests
{
    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private sealed class CapturingChatBus : IChatBus
    {
        public List<ChatMessage> Published { get; } = new();

        public void Publish(ChatMessage message) => Published.Add(message);

        public IDisposable Subscribe(Action<ChatMessage> handler) =>
            new NoOpSubscription();

        public IReadOnlyList<ChatMessage> RecentMessages() =>
            Published.AsReadOnly();

        private sealed class NoOpSubscription : IDisposable
        {
            public void Dispose() { }
        }
    }

    private sealed class FakeTrialProbe(bool isTrialMode) : ITrialModeProbe
    {
        public bool IsTrialMode { get; } = isTrialMode;
    }

    private static async Task SendChatMessage(ClientWebSocket ws, string platform, string username, string text)
    {
        var payload = JsonSerializer.Serialize(new ExtensionMessage(
            Type: "chat",
            Platform: platform,
            Username: username,
            DisplayName: null,
            AvatarUrl: null,
            Text: text,
            ExternalId: null,
            Timestamp: null));

        await ws.SendAsync(
            Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text,
            endOfMessage: true,
            CancellationToken.None);
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> ms for at least one message to arrive in the bus,
    /// then returns; or returns after the timeout.
    /// </summary>
    private static async Task WaitForPublishOrTimeout(CapturingChatBus bus, int timeoutMs = 500)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (bus.Published.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
    }

    // -------------------------------------------------------------------------
    // Tests
    // -------------------------------------------------------------------------

    [Fact]
    public async Task TrialActive_TikTok_message_is_dropped()
    {
        var bus = new CapturingChatBus();
        var probe = new FakeTrialProbe(isTrialMode: true);
        await using var server = new ExtensionBridgeServer(bus, port: 0, trialProbe: probe);
        await server.StartAsync(CancellationToken.None);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"), CancellationToken.None);

        await SendChatMessage(ws, platform: "tiktok", username: "@tester", text: "hello");

        // Give enough time for the server to process (it should drop, so nothing arrives).
        await WaitForPublishOrTimeout(bus, timeoutMs: 400);

        bus.Published.Should().BeEmpty("TikTok messages must be dropped in trial mode");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task TrialActive_Instagram_message_passes_through()
    {
        var bus = new CapturingChatBus();
        var probe = new FakeTrialProbe(isTrialMode: true);
        await using var server = new ExtensionBridgeServer(bus, port: 0, trialProbe: probe);
        await server.StartAsync(CancellationToken.None);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"), CancellationToken.None);

        await SendChatMessage(ws, platform: "instagram", username: "@ayse", text: "mavi m");

        await WaitForPublishOrTimeout(bus, timeoutMs: 2000);

        bus.Published.Should().HaveCount(1, "Instagram messages must pass through in trial mode");
        bus.Published[0].Platform.Should().Be("instagram");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task TrialExpired_TikTok_message_is_dropped()
    {
        // TrialExpired also triggers IsTrialMode == true (per LicenseStatusExtensions).
        var bus = new CapturingChatBus();
        var probe = new FakeTrialProbe(isTrialMode: true);   // expired trial → still IsTrialMode
        await using var server = new ExtensionBridgeServer(bus, port: 0, trialProbe: probe);
        await server.StartAsync(CancellationToken.None);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"), CancellationToken.None);

        await SendChatMessage(ws, platform: "tiktok", username: "@expired_user", text: "buy now");

        await WaitForPublishOrTimeout(bus, timeoutMs: 400);

        bus.Published.Should().BeEmpty("TikTok messages must be dropped when trial is expired");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }

    [Fact]
    public async Task ActiveLicense_TikTok_message_passes_through()
    {
        // Active license → IsTrialMode == false → filter not applied.
        var bus = new CapturingChatBus();
        var probe = new FakeTrialProbe(isTrialMode: false);
        await using var server = new ExtensionBridgeServer(bus, port: 0, trialProbe: probe);
        await server.StartAsync(CancellationToken.None);

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{server.Port}/extension"), CancellationToken.None);

        await SendChatMessage(ws, platform: "tiktok", username: "@premium_user", text: "hello world");

        await WaitForPublishOrTimeout(bus, timeoutMs: 2000);

        bus.Published.Should().HaveCount(1, "TikTok messages must pass through when license is active");
        bus.Published[0].Platform.Should().Be("tiktok");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }
}
