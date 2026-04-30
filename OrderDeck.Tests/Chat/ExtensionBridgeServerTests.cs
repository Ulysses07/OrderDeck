using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using OrderDeck.Chat.Bridge;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class ExtensionBridgeServerTests
{
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

        var payload = JsonSerializer.Serialize(new ExtensionMessage(
            Type: "chat",
            Platform: "instagram",
            Username: "@ayse_y",
            DisplayName: "Ayşe",
            AvatarUrl: null,
            Text: "MAVI XL aldım",
            ExternalId: "ig-001",
            Timestamp: 1700000000));

        await ws.SendAsync(Encoding.UTF8.GetBytes(payload),
            WebSocketMessageType.Text, true, CancellationToken.None);

        var msg = await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
        msg.Platform.Should().Be("instagram");
        msg.Username.Should().Be("@ayse_y");
        msg.Text.Should().Be("MAVI XL aldım");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
    }
}
