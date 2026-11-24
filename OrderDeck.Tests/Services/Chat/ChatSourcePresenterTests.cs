using FluentAssertions;
using OrderDeck.App.Services.Chat;
using OrderDeck.Core.Chat;
using Severity = OrderDeck.App.Services.Chat.ChatStatusSeverity;
using State = OrderDeck.Core.Chat.ChatSourceState;

namespace OrderDeck.Tests.Services.Chat;

/// <summary>
/// YouTube, Facebook ve TikTok durum satırlarının görünüm kuralları (tasarım: Sohbeti yenile §5.4).
///
/// NEDEN: izleyici yalnız olgu tutar; operatörün şeritte gördüğü metin, nokta rengi ve ipucu bu saf
/// işlevde türetilir. Kural yanlışsa düşen platform sağlam görünür ya da o gece o platformda yayın
/// yapmayan yayıncının önüne sarı satır çıkar. Saat parametredir: testler beklemez. Arayüz metinleri
/// bilerek literal'dir (üretim sabitine başvuran test metin değişikliğini yakalamaz).
/// </summary>
public sealed class ChatSourcePresenterTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 24, 21, 14, 7, TimeSpan.FromHours(3));

    private const string Retry =
        "Bağlantı koptu; kendiliğinden yeniden denenecek. Beklemeden denemek için Sohbeti yenile'ye bas.";

    private static DateTimeOffset FromNow(double seconds) => Now.AddSeconds(seconds).ToUniversalTime();

    private static ChatSourceHealth H(string platform, State state, double sinceAgo = 30, bool wasLive = false,
        string? detail = null, double? retryIn = null)
        => new(platform, state, detail, FromNow(-sinceAgo), retryIn is { } r ? FromNow(r) : null, wasLive);

    private static ChatSourceView Eval(bool otherSourceLive, params ChatSourceHealth[] snapshot)
        => ChatSourcePresenter.Evaluate(snapshot, Now, otherSourceLive);

    private static ChatStatusRow Row(bool otherSourceLive, ChatSourceHealth entry) => Eval(otherSourceLive, entry).Rows.Single();

    private static ChatStatusRow Expect(string key, string text, Severity severity, string? tip,
        bool problem = false, bool connecting = false)
        => new(key, GroupLabel: null, text, severity, tip, problem, connecting);

    [Fact]
    public void Stopped_source_has_no_row()
    {
        Eval(false, H("youtube", State.Stopped)).Rows.Should().BeEmpty();
    }

    [Fact]
    public void Live_source_shows_only_its_name()
    {
        // 21:14:07'den 4 dakika 7 saniye önce = 21:10.
        Row(false, H("youtube", State.Live, sinceAgo: 247, wasLive: true))
            .Should().Be(Expect("youtube", "YouTube", Severity.Live, "YouTube sohbeti bağlı (21:10'dan beri)."));
        Row(false, H("facebook", State.Live, sinceAgo: 667, wasLive: true))
            .Should().Be(Expect("facebook", "Facebook", Severity.Live, "Facebook sohbeti bağlı (21:03'ten beri)."));
    }

    [Fact]
    public void Connecting_source_is_neutral_and_flagged_as_connecting()
    {
        Row(false, H("youtube", State.Connecting))
            .Should().Be(Expect("youtube", "YouTube bağlanıyor", Severity.Neutral, null, connecting: true));
    }

    [Fact]
    public void Source_that_was_never_live_waits_for_a_broadcast_even_when_others_are_live()
    {
        // O gece o platformda yayın yapmayan yayıncı uyarı görmez.
        var youtube = Expect("youtube", "YouTube yayın bekleniyor", Severity.Neutral,
            "YouTube'da canlı yayın açılınca yorumlar akmaya başlar.");
        Row(false, H("youtube", State.NoBroadcast)).Should().Be(youtube);
        Row(true, H("youtube", State.NoBroadcast)).Should().Be(youtube);
        Row(true, H("facebook", State.NoBroadcast)).Should().Be(Expect("facebook", "Facebook yayın bekleniyor",
            Severity.Neutral, "Facebook'ta canlı yayın açılınca yorumlar akmaya başlar."));
    }

    [Fact]
    public void Source_that_dropped_while_another_is_live_is_cut_and_yellow()
    {
        Row(true, H("youtube", State.NoBroadcast, sinceAgo: 7, wasLive: true)).Should().Be(Expect(
            "youtube", "YouTube yayın kesildi (21:14)", Severity.Warning,
            "YouTube yayını kapandı; yorumları gelmiyor. Yayını yeniden açtıysan Sohbeti yenile'ye bas.",
            problem: true));
        Row(true, H("facebook", State.NoBroadcast, sinceAgo: 7, wasLive: true)).Should().Be(Expect(
            "facebook", "Facebook yayın kesildi (21:14)", Severity.Warning,
            "Facebook yayını kapandı; yorumları gelmiyor. Yayını yeniden açtıysan birkaç saniye içinde kendiliğinden bağlanır.",
            problem: true));
    }

    [Fact]
    public void Another_platform_in_the_same_snapshot_counts_as_another_live_source()
    {
        var view = Eval(false, H("youtube", State.NoBroadcast, sinceAgo: 7, wasLive: true),
            H("facebook", State.Live, wasLive: true));

        view.Rows.Select(r => (r.DisplayText, r.Severity))
            .Should().Equal(("YouTube yayın kesildi (21:14)", Severity.Warning), ("Facebook", Severity.Live));
        view.AnyLive.Should().BeTrue();
    }

    [Fact]
    public void Source_that_ended_with_nothing_else_live_is_simply_over()
    {
        Row(false, H("youtube", State.NoBroadcast, sinceAgo: 247, wasLive: true))
            .Should().Be(Expect("youtube", "YouTube yayın bitti (21:10)", Severity.Neutral, null));
    }

    [Fact]
    public void Retrying_source_names_the_retry_time_and_is_yellow_only_if_it_was_live()
    {
        // 21:14:07 + 353 sn = 21:20:00.
        Row(false, H("youtube", State.Retrying, wasLive: true, retryIn: 353)).Should().Be(Expect(
            "youtube", "YouTube bağlantı koptu, 21:20'de dener", Severity.Warning, Retry, problem: true));
        Row(false, H("youtube", State.Retrying, retryIn: 353)).Should().Be(Expect(
            "youtube", "YouTube bağlantı koptu, 21:20'de dener", Severity.Neutral, Retry, problem: true));
        Row(false, H("youtube", State.Retrying, wasLive: true)).DisplayText.Should().Be("YouTube bağlantı koptu");
    }

    [Fact]
    public void Source_that_needs_attention_is_yellow_with_the_reported_reason()
    {
        const string reason = "YouTube hesabı bağlı değil. Ayarlar > YouTube'dan bağla.";

        Row(false, H("youtube", State.NeedsAttention, detail: reason)).Should().Be(Expect(
            "youtube", "YouTube bağlantı kurulamıyor", Severity.Warning, reason, problem: true));
        Row(false, H("facebook", State.NeedsAttention)).Tooltip.Should().Be("Facebook sohbetine bağlanılamıyor.");
    }

    [Fact]
    public void Tiktok_row_follows_the_tab()
    {
        Row(false, H("tiktok", State.Live, wasLive: true))
            .Should().Be(Expect("tiktok", "TikTok", Severity.Live, "TikTok canlı yayın sekmesi bağlı."));

        // Bu oturumda hiç bağlanmadıysa satır yok: TikTok kullanmayan yayıncı bir şey görmez.
        Eval(true, H("tiktok", State.NoBroadcast)).Rows.Should().BeEmpty();

        const string tip = "TikTok canlı yayın sekmesi kapalı ya da uzantı bağlı değil. Sekmeyi aç ya da yenile.";
        Row(true, H("tiktok", State.NoBroadcast, wasLive: true))
            .Should().Be(Expect("tiktok", "TikTok sekme bağlı değil", Severity.Warning, tip, problem: true));
        Row(false, H("tiktok", State.NoBroadcast, wasLive: true))
            .Should().Be(Expect("tiktok", "TikTok sekme bağlı değil", Severity.Neutral, tip));
    }

    [Fact]
    public void Rows_keep_the_snapshot_order_and_any_live_reflects_the_snapshot()
    {
        var view = Eval(false, H("youtube", State.Connecting), H("tiktok", State.Live, wasLive: true),
            H("facebook", State.NoBroadcast));

        view.Rows.Select(r => r.Key).Should().Equal("youtube", "tiktok", "facebook");
        view.AnyLive.Should().BeTrue();
        Eval(true, H("youtube", State.Connecting)).AnyLive.Should().BeFalse("otherSourceLive bu platformların canlılığı değildir");
    }

    [Fact]
    public void Yellow_rows_raise_the_top_dot_and_a_cut_broadcast_only_for_two_minutes()
    {
        Eval(false, H("youtube", State.Live, wasLive: true)).TopDotTooltip.Should().BeNull();

        Eval(true, H("youtube", State.NoBroadcast, sinceAgo: 119, wasLive: true)).TopDotTooltip
            .Should().Be("YouTube: yayın kesildi (21:12).");
        Eval(true, H("youtube", State.NoBroadcast, sinceAgo: 121, wasLive: true)).TopDotTooltip
            .Should().BeNull("iki dakikadan sonra yalnız şeritte sarı kalır");

        Eval(false, H("youtube", State.NeedsAttention, sinceAgo: 3600),
                H("facebook", State.Retrying, wasLive: true, retryIn: 353)).TopDotTooltip
            .Should().Be("YouTube: bağlantı kurulamıyor. Facebook: bağlantı koptu, 21:20'de dener.");

        Eval(false, H("youtube", State.Retrying, retryIn: 353)).TopDotTooltip
            .Should().BeNull("soluk satır noktayı sarartmaz");
    }
}
