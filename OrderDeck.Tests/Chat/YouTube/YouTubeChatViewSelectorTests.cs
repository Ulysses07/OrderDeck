using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// Bootstrap HTML'inden doğru sohbet görünümünün ("Canlı sohbet" = tüm mesajlar)
/// continuation'ının seçilmesi. Top chat ("Popüler sohbet") yoğun/tekrar dolu
/// mezat chat'inde mesaj eler; bu yüzden seçili-olmayan/Canlı görünümü tercih
/// edilir, bulunamazsa eski davranışa (ilk continuation) düşülür.
/// </summary>
public class YouTubeChatViewSelectorTests
{
    private static string Html(string ytInitialDataJson) =>
        "<!doctype html><html><body><script nonce=\"x\">"
        + "window[\"ytInitialData\"] = " + ytInitialDataJson + ";</script></body></html>";

    [Fact]
    public void Picks_live_chat_view_over_selected_top_chat()
    {
        var html = Html("""
        {"contents":{"liveChatRenderer":{"header":{"liveChatHeaderRenderer":{
        "viewSelector":{"sortFilterSubMenuRenderer":{"subMenuItems":[
        {"title":"Popüler sohbet mesajları","selected":true,
         "continuation":{"reloadContinuationData":{"continuation":"TOP_TOKEN"}}},
        {"title":"Canlı sohbet","selected":false,
         "continuation":{"reloadContinuationData":{"continuation":"LIVE_TOKEN"}}}
        ]}}}},"continuations":[{"reloadContinuationData":{"continuation":"TOP_TOKEN"}}]}}}
        """);

        var (cont, view) = YouTubeLiveChatScraper.SelectChatContinuation(html);

        cont.Should().Be("LIVE_TOKEN");
        view.Should().Be("Canlı sohbet");
    }

    [Fact]
    public void Picks_live_chat_when_titles_are_english()
    {
        var html = Html("""
        {"x":{"sortFilterSubMenuRenderer":{"subMenuItems":[
        {"title":"Top chat","selected":true,
         "continuation":{"reloadContinuationData":{"continuation":"TOP_TOKEN"}}},
        {"title":"Live chat","selected":false,
         "continuation":{"reloadContinuationData":{"continuation":"LIVE_TOKEN"}}}
        ]}}}
        """);

        var (cont, view) = YouTubeLiveChatScraper.SelectChatContinuation(html);

        cont.Should().Be("LIVE_TOKEN");
        view.Should().Be("Live chat");
    }

    [Fact]
    public void Falls_back_to_first_continuation_when_no_view_selector()
    {
        // viewSelector yok — yalnız ham continuation var; eski davranışa düşmeli.
        var html = "<html><script>var z = {\"foo\":1};</script>"
                 + "<script>\"continuation\":\"ONLY_TOKEN\"</script></html>";

        var (cont, _) = YouTubeLiveChatScraper.SelectChatContinuation(html);

        cont.Should().Be("ONLY_TOKEN");
    }

    [Fact]
    public void Single_view_stream_still_yields_a_token()
    {
        // Sadece seçili Top chat var (split sunmayan yayın). Token kaybolmamalı.
        var html = Html("""
        {"a":{"sortFilterSubMenuRenderer":{"subMenuItems":[
        {"title":"Popüler sohbet mesajları","selected":true,
         "continuation":{"reloadContinuationData":{"continuation":"ONLY_TOKEN"}}}
        ]}}}
        """);

        var (cont, _) = YouTubeLiveChatScraper.SelectChatContinuation(html);

        cont.Should().Be("ONLY_TOKEN");
    }

    [Fact]
    public void Returns_null_when_no_continuation_anywhere()
    {
        var (cont, view) = YouTubeLiveChatScraper.SelectChatContinuation("<html>no data</html>");

        cont.Should().BeNull();
        view.Should().BeNull();
    }
}
