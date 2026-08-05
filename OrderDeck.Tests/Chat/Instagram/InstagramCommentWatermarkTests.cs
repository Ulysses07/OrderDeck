using System;
using System.Collections.Generic;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

/// <summary>
/// Watermark saf bir durum makinesi — ağ yok, saat yok. Testler spec'teki
/// "Test stratejisi / Watermark" maddelerinin birebir karşılığı.
/// </summary>
public class InstagramCommentWatermarkTests
{
    private static InstagramComment C(string id, long ts, string text = "x") =>
        new(id, text, ts, "@u");

    /// <summary>Graph ters kronolojik döner — testlerde de öyle veriyoruz.</summary>
    private static List<InstagramComment> Page(params InstagramComment[] newestFirst) =>
        new(newestFirst);

    [Fact]
    public void First_poll_primes_and_publishes_nothing()
    {
        // Yayına ortadan bağlanma: geçmişi chat'e basmıyoruz.
        var w = new InstagramCommentWatermark();

        var r = w.Advance("m1", Page(C("c3", 300), C("c2", 200), C("c1", 100)));

        r.Primed.Should().BeTrue();
        r.NewComments.Should().BeEmpty();
        r.Overflowed.Should().BeFalse();
    }

    [Fact]
    public void Second_poll_returns_only_newer_comments_oldest_first()
    {
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("c1", 100)));

        var r = w.Advance("m1", Page(C("c3", 300), C("c2", 200), C("c1", 100)));

        r.NewComments.Should().HaveCount(2);
        r.NewComments[0].Id.Should().Be("c2"); // kronolojik sıraya çevrildi
        r.NewComments[1].Id.Should().Be("c3");
    }

    [Fact]
    public void Same_second_multiple_comments_all_published_once()
    {
        // Aynı saniyede 3 yorum: ilk çekimde biri görüldü, diğer ikisi
        // sonraki çekimde gelmeli ve tekrar basılmamalı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a", 500)));

        var first = w.Advance("m1", Page(C("c", 500), C("b", 500), C("a", 500)));
        first.NewComments.Should().HaveCount(2);
        first.NewComments[0].Id.Should().Be("b"); // id ikincil anahtar → deterministik
        first.NewComments[1].Id.Should().Be("c");

        var second = w.Advance("m1", Page(C("c", 500), C("b", 500), C("a", 500)));
        second.NewComments.Should().BeEmpty();
    }

    [Fact]
    public void Deleted_comment_does_not_cause_replay()
    {
        // Yayıncı "c2"yi IG uygulamasından siliyor. Eski "son görülen id'ye
        // kadar yürü" yaklaşımı burada tüm sayfayı yeni sanardı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("c2", 200), C("c1", 100)));

        var r = w.Advance("m1", Page(C("c1", 100)));

        r.NewComments.Should().BeEmpty();
    }

    [Fact]
    public void New_media_id_resets_state()
    {
        // Yayıncı yayını kapatıp yenisini açtı → temiz başla, geçmiş basma.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("c9", 900)));

        var r = w.Advance("m2", Page(C("d1", 100)));

        r.Primed.Should().BeTrue();
        r.NewComments.Should().BeEmpty();
    }

    [Fact]
    public void Overflow_detected_when_every_comment_in_a_full_page_is_new()
    {
        // Sayfa doldu ve en eski yorum bile watermark'tan yeni → mesaj kaybı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a3", 300), C("a2", 200), C("a1", 100)));

        var r = w.Advance("m1", Page(C("b3", 900), C("b2", 800), C("b1", 700)));

        r.Overflowed.Should().BeTrue();
        r.NewComments.Should().HaveCount(3);
    }

    [Fact]
    public void No_overflow_when_page_still_contains_a_known_comment()
    {
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a3", 300), C("a2", 200), C("a1", 100)));

        var r = w.Advance("m1", Page(C("b1", 400), C("a3", 300), C("a2", 200)));

        r.Overflowed.Should().BeFalse();
        r.NewComments.Should().ContainSingle().Which.Id.Should().Be("b1");
    }

    [Fact]
    public void Empty_page_is_not_overflow_and_publishes_nothing()
    {
        var w = new InstagramCommentWatermark();
        w.Advance("m1", Page(C("a1", 100)));

        var r = w.Advance("m1", new List<InstagramComment>());

        r.NewComments.Should().BeEmpty();
        r.Overflowed.Should().BeFalse();
    }

    [Fact]
    public void Priming_on_empty_page_still_publishes_later_comments()
    {
        // Yayın açıldı, henüz yorum yok. Sonra gelen yorumlar basılmalı.
        var w = new InstagramCommentWatermark();
        w.Advance("m1", new List<InstagramComment>());

        var r = w.Advance("m1", Page(C("c1", 100)));

        r.NewComments.Should().ContainSingle().Which.Id.Should().Be("c1");
    }
}
