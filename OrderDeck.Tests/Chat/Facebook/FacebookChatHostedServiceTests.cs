using System;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Facebook;
using Xunit;

namespace OrderDeck.Tests.Chat.Facebook;

/// <summary>
/// Locks down the bootstrap-crash exponential backoff. Same shape as
/// <see cref="OrderDeck.Chat.Ingestors.YouTube.YouTubeOfficialChatHostedService"/>'s
/// — keeps reconnect cadence consistent across platforms so operators
/// don't see one ingestor recover much faster than the other.
/// </summary>
public class FacebookChatHostedServiceTests
{
    [Fact]
    public void ComputeBackoff_zero_or_one_crash_returns_short_idle()
    {
        FacebookChatHostedService.ComputeBackoff(0)
            .Should().Be(TimeSpan.FromSeconds(30));
        FacebookChatHostedService.ComputeBackoff(1)
            .Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(2,  60)]    // 30s × 2^1
    [InlineData(3, 120)]    // 30s × 2^2
    [InlineData(4, 240)]    // 30s × 2^3
    public void ComputeBackoff_doubles_until_cap(int crashes, int expectedSeconds)
    {
        FacebookChatHostedService.ComputeBackoff(crashes)
            .Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Theory]
    [InlineData(5)]
    [InlineData(8)]
    [InlineData(20)]
    public void ComputeBackoff_caps_at_five_minutes(int crashes)
    {
        FacebookChatHostedService.ComputeBackoff(crashes)
            .Should().Be(TimeSpan.FromMinutes(5));
    }

    // ---- WatchdogShouldFire: biten/değişen yayını tespit kuralı ----
    //
    // Bağlam: biten FB yayınının comments ucu 200 dönmeye devam ettiği için
    // poller kendi kendine hiç çıkmayabilir. Bekçi canlı listesini sorgular;
    // bu saf fonksiyon "poller'ı kes" kararını verir.

    [Fact]
    public void Watchdog_fires_immediately_on_different_live_video()
    {
        int streak = 0;
        FacebookChatHostedService.WatchdogShouldFire("video-B", "video-A", ref streak)
            .Should().BeTrue("yeni yayın başladıysa eski poller'ı bekletmenin anlamı yok");
    }

    [Fact]
    public void Watchdog_does_not_fire_on_single_null()
    {
        int streak = 0;
        FacebookChatHostedService.WatchdogShouldFire(null, "video-A", ref streak)
            .Should().BeFalse("resolver geçici ağ hatasında da null döner — tek null kanıt değil");
        streak.Should().Be(1);
    }

    [Fact]
    public void Watchdog_fires_on_second_consecutive_null()
    {
        int streak = 0;
        FacebookChatHostedService.WatchdogShouldFire(null, "video-A", ref streak).Should().BeFalse();
        FacebookChatHostedService.WatchdogShouldFire(null, "video-A", ref streak)
            .Should().BeTrue("iki ardışık boş liste = yayın gerçekten kapanmış");
    }

    [Fact]
    public void Watchdog_same_video_resets_null_streak()
    {
        int streak = 0;
        FacebookChatHostedService.WatchdogShouldFire(null, "video-A", ref streak).Should().BeFalse();
        FacebookChatHostedService.WatchdogShouldFire("video-A", "video-A", ref streak)
            .Should().BeFalse("bağlı video hâlâ canlı — sorun yok");
        streak.Should().Be(0, "araya giren başarılı tur null sayacını sıfırlamalı");
        FacebookChatHostedService.WatchdogShouldFire(null, "video-A", ref streak)
            .Should().BeFalse("sıfırlandıktan sonra tek null yine yetmez");
    }

    [Fact]
    public void Watchdog_empty_string_counts_as_null()
    {
        int streak = 0;
        FacebookChatHostedService.WatchdogShouldFire("", "video-A", ref streak).Should().BeFalse();
        FacebookChatHostedService.WatchdogShouldFire("", "video-A", ref streak).Should().BeTrue();
    }

    // ---- ClassifyStreamExit: R10-CHAT01 çıkış sınıflandırması ----
    //
    // Bağlam: eski kod "kendi kendine çıktı = yayın bitti" sayıyordu ve
    // 5 ardışık GEÇİCİ ağ hatasında da video id'yi 2 dk karalisteye
    // alıyordu — ağ toparlanınca hâlâ canlı olan yayına geri bağlanılamıyordu.

    [Fact]
    public void Gecici_hata_cikisi_karaliste_DEGIL_backoff()
    {
        FacebookChatHostedService.ClassifyStreamExit(
                crashed: false, cancelled: false,
                FacebookStreamEndReason.TransientFailure)
            .Should().Be(FacebookChatHostedService.StreamExitAction.Backoff,
                "geçici hata 'yayın bitti' kanıtı değil — id karalistelenmemeli, " +
                "aynı canlı videoya backoff sonrası yeniden bağlanılabilmeli");
    }

    [Fact]
    public void Kanitli_bitis_karalisteye_alinir()
    {
        FacebookChatHostedService.ClassifyStreamExit(
                crashed: false, cancelled: false,
                FacebookStreamEndReason.BroadcastEnded)
            .Should().Be(FacebookChatHostedService.StreamExitAction.StaleThenFastRebind,
                "code:100 tek kanıtlı bitiş — Meta listede LIVE göstermeye devam " +
                "edebileceği için id bir süre karalistede kalmalı");
    }

    [Fact]
    public void Iptal_hizli_yeniden_baglanma()
    {
        FacebookChatHostedService.ClassifyStreamExit(
                crashed: false, cancelled: true,
                FacebookStreamEndReason.Cancelled)
            .Should().Be(FacebookChatHostedService.StreamExitAction.FastRebind);
    }

    [Fact]
    public void Cokme_backoff()
    {
        // crashed=true her sebebi ezer — Completion normalde fault olmaz ama
        // olursa karaliste yine yanlış olurdu.
        FacebookChatHostedService.ClassifyStreamExit(
                crashed: true, cancelled: false,
                FacebookStreamEndReason.BroadcastEnded)
            .Should().Be(FacebookChatHostedService.StreamExitAction.Backoff);
    }
}
