using FluentAssertions;
using OrderDeck.Chat.Ingestors.Instagram;
using Xunit;

namespace OrderDeck.Tests.Chat.Instagram;

public class InstagramLiveMediaParserTests
{
    [Fact]
    public void Empty_data_means_no_active_broadcast()
    {
        InstagramLiveMediaParser.Parse("{\"data\":[]}").Should().BeNull();
    }

    [Fact]
    public void Missing_data_property_returns_null()
    {
        InstagramLiveMediaParser.Parse("{}").Should().BeNull();
    }

    [Fact]
    public void Garbage_returns_null()
    {
        InstagramLiveMediaParser.Parse("<html>oops</html>").Should().BeNull();
        InstagramLiveMediaParser.Parse("").Should().BeNull();
    }

    [Fact]
    public void Parses_media_id_and_comments()
    {
        const string json = """
        {"data":[{"id":"17895695668004550","comments":{"data":[
          {"id":"17870913088019932","text":"MAVI XL","timestamp":"2026-08-05T12:34:56+0000","username":"ayse_y"},
          {"id":"17870913088019931","text":"kac lira","timestamp":"2026-08-05T12:34:55+0000","username":"veli"}
        ]}}]}
        """;

        var page = InstagramLiveMediaParser.Parse(json);

        page.Should().NotBeNull();
        page!.MediaId.Should().Be("17895695668004550");
        page.Comments.Should().NotBeNull().And.HaveCount(2);
        page.Comments![0].Id.Should().Be("17870913088019932");
        page.Comments[0].Text.Should().Be("MAVI XL");
        page.Comments[0].Username.Should().Be("ayse_y");
        // 2026-08-05T12:34:56+0000 → unix saniye
        page.Comments[0].TimestampUnix.Should().Be(1785933296);
    }

    [Fact]
    public void Absent_comments_field_is_null_not_empty()
    {
        // İzin eksikse Meta comments alanını hiç göndermez. Bunu "yorum yok"
        // sanıp sessizce çalışmaya devam edersek arıza görünmez olur.
        var page = InstagramLiveMediaParser.Parse("{\"data\":[{\"id\":\"m1\"}]}");

        page.Should().NotBeNull();
        page!.Comments.Should().BeNull();
    }

    [Fact]
    public void Present_but_empty_comments_is_empty_list()
    {
        var page = InstagramLiveMediaParser.Parse(
            "{\"data\":[{\"id\":\"m1\",\"comments\":{\"data\":[]}}]}");

        page!.Comments.Should().NotBeNull().And.BeEmpty();
    }

    [Fact]
    public void Comment_without_text_is_skipped()
    {
        // Metinsiz yorum (nadir metadata çerçevesi) chat'e basılamaz.
        var page = InstagramLiveMediaParser.Parse("""
        {"data":[{"id":"m1","comments":{"data":[
          {"id":"c1","timestamp":"2026-08-05T12:34:56+0000","username":"a"},
          {"id":"c2","text":"  ","timestamp":"2026-08-05T12:34:56+0000","username":"a"},
          {"id":"c3","text":"ok","timestamp":"2026-08-05T12:34:56+0000","username":"a"}
        ]}}]}
        """);

        page!.Comments.Should().ContainSingle().Which.Id.Should().Be("c3");
    }

    [Fact]
    public void Comment_without_username_still_parses()
    {
        // username eksikse (izin sorunu) yorumu atmıyoruz — operatör metni
        // görsün, kim yazdığı "bilinmiyor" kalsın.
        var page = InstagramLiveMediaParser.Parse("""
        {"data":[{"id":"m1","comments":{"data":[
          {"id":"c1","text":"selam","timestamp":"2026-08-05T12:34:56+0000"}
        ]}}]}
        """);

        page!.Comments.Should().ContainSingle();
        page.Comments![0].Username.Should().BeNull();
    }

    [Fact]
    public void Unparsable_timestamp_drops_the_comment()
    {
        // Watermark'ın tek dayanağı timestamp. Ayrıştırılamayan bir damgaya
        // "şimdi" atamak watermark'ı bozar ve mesaj kaybına yol açar.
        var page = InstagramLiveMediaParser.Parse("""
        {"data":[{"id":"m1","comments":{"data":[
          {"id":"c1","text":"selam","timestamp":"dun aksam"}
        ]}}]}
        """);

        page!.Comments.Should().BeEmpty();
    }

    [Fact]
    public void First_live_media_wins_when_multiple_returned()
    {
        var page = InstagramLiveMediaParser.Parse(
            "{\"data\":[{\"id\":\"m1\"},{\"id\":\"m2\"}]}");

        page!.MediaId.Should().Be("m1");
    }
}
