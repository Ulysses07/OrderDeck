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

    // ── O-09: abone yalıtımı ────────────────────────────────────────────────
    //
    // Sahadaki iki abone: MainShellViewModel (arayüz) ve OverlayHost (OBS).
    // Eskiden dağıtım düz bir döngüydü, yani arayüz tarafındaki bir hata
    // overlay'i yayın ortasında susturuyor, istisna da uzantı köprüsünün
    // WebSocket döngüsüne gidip TikTok sohbetini büsbütün düşürüyordu.

    [Fact]
    public void Throwing_handler_does_not_block_the_ones_after_it()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        var later = new List<ChatMessage>();

        using var bad = bus.Subscribe(_ => throw new InvalidOperationException("abone bozuk"));
        using var good = bus.Subscribe(later.Add);

        bus.Publish(Msg("hello"));

        later.Should().HaveCount(1,
            "bir abonenin hatası sonraki abonelerin mesajı almasını engellememeli");
    }

    [Fact]
    public void Throwing_handler_does_not_reach_the_publisher()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        using var bad = bus.Subscribe(_ => throw new InvalidOperationException("abone bozuk"));

        Action publish = () => bus.Publish(Msg("hello"));

        publish.Should().NotThrow(
            "istisna ingest döngüsüne geri giderse kaynak bağlantısı kopuyor");
    }

    [Fact]
    public void Handler_subscribed_before_a_throwing_one_still_runs()
    {
        // Sıra bağımsızlığı: kusur eskiden yalnızca patlayandan SONRAKİLERİ
        // vuruyordu, yani abonelik sırası sonucu belirliyordu.
        var bus = new ChatBus(ringBufferSize: 50);
        var earlier = new List<ChatMessage>();

        using var good = bus.Subscribe(earlier.Add);
        using var bad = bus.Subscribe(_ => throw new InvalidOperationException("abone bozuk"));

        bus.Publish(Msg("hello"));

        earlier.Should().HaveCount(1);
    }

    [Fact]
    public void A_permanently_broken_handler_is_kept_subscribed()
    {
        // Sessizce aboneliği düşürmek overlay'i kalıcı olarak karartır ve
        // sebebi görünmez olurdu; kusur logda adıyla duruyor.
        var bus = new ChatBus(ringBufferSize: 50);
        var calls = 0;

        using var bad = bus.Subscribe(_ =>
        {
            calls++;
            throw new InvalidOperationException("abone bozuk");
        });

        for (var i = 0; i < 250; i++) bus.Publish(Msg("x", i.ToString()));

        calls.Should().Be(250, "patlayan abone otomatik olarak çıkarılmamalı");
    }

    [Fact]
    public void Ring_buffer_still_records_when_every_handler_throws()
    {
        var bus = new ChatBus(ringBufferSize: 50);
        using var bad = bus.Subscribe(_ => throw new InvalidOperationException("abone bozuk"));

        bus.Publish(Msg("hello"));

        bus.RecentMessages().Should().ContainSingle()
            .Which.Text.Should().Be("hello",
                "geç bağlanan overlay'in yakalayacağı geçmiş abonelerden bağımsız");
    }
}
