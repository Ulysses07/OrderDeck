using FluentAssertions;
using OrderDeck.Core.Chat;
using OrderDeck.Tests.TestHelpers;

namespace OrderDeck.Tests.Chat;

/// <summary>
/// YouTube, Facebook ve TikTok sohbet kaynaklarının durum izleyicisi.
///
/// NEDEN: İzleyici yalnız OLGU tutar (durum, duruma giriş anı, yeniden deneme saati, açıklama, "bu
/// oturumda canlıydı"). "Yayın kesildi mi bitti mi", "sarı mı soluk mu" gibi yorumlar okuma anında
/// sunucuda türetilir; izleyici yorum yaparsa iki ekran (şerit, kenar çubuğu) farklı şey söyler.
/// </summary>
public class ChatSourceMonitorTests
{
    private readonly ManualTime _time = new();

    private ChatSourceHealth Only(ChatSourceMonitor monitor) => monitor.Snapshot().Should().ContainSingle().Subject;

    [Fact]
    public void Unknown_platform_is_absent_from_the_snapshot()
    {
        new ChatSourceMonitor(_time).Snapshot().Should().BeEmpty();
    }

    [Fact]
    public void Repeating_a_state_keeps_since_but_rewrites_detail_and_retry_time()
    {
        var monitor = new ChatSourceMonitor(_time);
        var start = _time.GetUtcNow();
        monitor.Report("youtube", ChatSourceState.Retrying, "ilk", start.AddSeconds(30));

        _time.Advance(TimeSpan.FromSeconds(10));
        monitor.Report("youtube", ChatSourceState.Retrying, "ikinci", start.AddSeconds(90));

        Only(monitor).Should().Be(new ChatSourceHealth(
            "youtube", ChatSourceState.Retrying, "ikinci", start, start.AddSeconds(90), WasLiveThisSession: false));
    }

    [Fact]
    public void A_new_state_moves_since_to_now_and_drops_the_old_detail()
    {
        var monitor = new ChatSourceMonitor(_time);
        monitor.Report("youtube", ChatSourceState.NeedsAttention, "sebep");

        _time.Advance(TimeSpan.FromSeconds(7));
        monitor.Report("youtube", ChatSourceState.Connecting);

        Only(monitor).Should().Be(new ChatSourceHealth(
            "youtube", ChatSourceState.Connecting, null, _time.GetUtcNow(), null, WasLiveThisSession: false));
    }

    [Fact]
    public void Live_marks_the_source_as_live_this_session_and_it_survives_later_states()
    {
        var monitor = new ChatSourceMonitor(_time);
        monitor.Report("facebook", ChatSourceState.Live);
        Only(monitor).WasLiveThisSession.Should().BeTrue();

        monitor.Report("facebook", ChatSourceState.NoBroadcast);

        Only(monitor).Should().Match<ChatSourceHealth>(h => h.State == ChatSourceState.NoBroadcast && h.WasLiveThisSession);
    }

    [Fact]
    public void Reset_session_forgets_past_liveness_but_keeps_a_currently_live_source()
    {
        // TikTok sekmesi oturumdan önce açılıp bağlı kalmış olabilir: yeni oturumda da "bu oturumda bağlıydı" sayılır.
        var monitor = new ChatSourceMonitor(_time);
        monitor.Report("youtube", ChatSourceState.Live);
        monitor.Report("youtube", ChatSourceState.NoBroadcast);
        monitor.Report("tiktok", ChatSourceState.Live);

        monitor.ResetSession();

        monitor.Snapshot().Select(h => (h.Platform, h.WasLiveThisSession))
            .Should().Equal(("youtube", false), ("tiktok", true));
    }

    [Fact]
    public void Snapshot_is_ordered_youtube_tiktok_facebook_then_the_rest_by_name()
    {
        var monitor = new ChatSourceMonitor(_time);
        foreach (var platform in new[] { "zeta", "facebook", "alfa", "tiktok", "youtube" })
            monitor.Report(platform, ChatSourceState.Connecting);

        monitor.Snapshot().Select(h => h.Platform).Should().Equal("youtube", "tiktok", "facebook", "alfa", "zeta");
    }

    [Fact]
    public void Platform_names_are_case_insensitive_and_stored_in_lower_case()
    {
        var monitor = new ChatSourceMonitor(_time);
        monitor.Report("YouTube", ChatSourceState.Connecting);

        monitor.Report("YOUTUBE", ChatSourceState.Live);

        Only(monitor).Should().Match<ChatSourceHealth>(h => h.Platform == "youtube" && h.State == ChatSourceState.Live);
    }
}
