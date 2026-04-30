using System;
using System.Collections.Generic;
using FluentAssertions;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class ChatBusTests
{
    private static ChatMessage Msg(string text, string id = "m1") =>
        new(id, "instagram", null, "@a", null, null, text, 0, Array.Empty<string>());

    [Fact]
    public void Subscribe_then_Publish_invokes_handler()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new List<ChatMessage>();
        using var sub = bus.Subscribe(received.Add);

        bus.Publish(Msg("hello"));

        received.Should().HaveCount(1);
        received[0].Text.Should().Be("hello");
    }

    [Fact]
    public void Disposing_subscription_stops_invocations()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var received = new List<ChatMessage>();
        var sub = bus.Subscribe(received.Add);
        sub.Dispose();

        bus.Publish(Msg("after"));

        received.Should().BeEmpty();
    }

    [Fact]
    public void RecentMessages_returns_last_N_in_arrival_order()
    {
        var bus = new ChatBus(ringBufferSize: 3);
        bus.Publish(Msg("a", "1"));
        bus.Publish(Msg("b", "2"));
        bus.Publish(Msg("c", "3"));
        bus.Publish(Msg("d", "4"));

        var recent = bus.RecentMessages();
        recent.Should().HaveCount(3);
        recent[0].Text.Should().Be("b");
        recent[2].Text.Should().Be("d");
    }
}
