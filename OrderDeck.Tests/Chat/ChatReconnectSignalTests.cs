using FluentAssertions;
using OrderDeck.Core.Chat;

namespace OrderDeck.Tests.Chat;

/// <summary>
/// "Sohbeti yenile" isteğinin sinyali: sohbet servisleri, oturum olaylarını dinledikleri gibi bunu
/// dinler.
///
/// NEDEN: İstek arayüz iş parçacığından gelir ve dört bağımsız kaynağa gider. Birinin dinleyicisi
/// hata atarsa öbür üç kaynak yenilenmeden kalmamalı, hata da operatörün tıklamasına sıçramamalı.
/// </summary>
public class ChatReconnectSignalTests
{
    [Fact]
    public void Request_calls_every_listener()
    {
        var signal = new ChatReconnectSignal();
        var calls = new List<string>();
        signal.Requested += (_, _) => calls.Add("a");
        signal.Requested += (_, _) => calls.Add("b");

        signal.Request();

        calls.Should().Equal("a", "b");
    }

    [Fact]
    public void A_throwing_listener_does_not_stop_the_others()
    {
        var signal = new ChatReconnectSignal();
        var calls = new List<string>();
        signal.Requested += (_, _) => calls.Add("a");
        signal.Requested += (_, _) => throw new InvalidOperationException("dinleyici bozuk");
        signal.Requested += (_, _) => calls.Add("c");

        var act = signal.Request;

        act.Should().NotThrow();
        calls.Should().Equal("a", "c");
    }

    [Fact]
    public void Request_without_listeners_does_nothing()
    {
        var act = new ChatReconnectSignal().Request;

        act.Should().NotThrow();
    }
}
