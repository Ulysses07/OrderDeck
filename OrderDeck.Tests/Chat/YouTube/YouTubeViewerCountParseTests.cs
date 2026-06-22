using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// updated_metadata yanıtından canlı izleyici sayısının çıkarılması.
/// Gerçek innertube cevabının daraltılmış örnekleriyle.
/// </summary>
public class YouTubeViewerCountParseTests
{
    [Fact]
    public void Parses_runs_with_turkish_thousands()
    {
        const string json = """
        {"actions":[{"updateViewershipAction":{"viewCount":{"videoViewCountRenderer":{
        "viewCount":{"runs":[{"text":"1.234"},{"text":" izleyici"}]},
        "isLive":true,"originalViewCount":"1234"}}}}]}
        """;
        YouTubeLiveChatScraper.ParseConcurrentViewers(json).Should().Be(1234);
    }

    [Fact]
    public void Parses_simpleText_english()
    {
        const string json = """
        {"x":{"videoViewCountRenderer":{"viewCount":{"simpleText":"1,234 watching now"},"isLive":true}}}
        """;
        YouTubeLiveChatScraper.ParseConcurrentViewers(json).Should().Be(1234);
    }

    [Fact]
    public void Falls_back_to_originalViewCount()
    {
        const string json = """
        {"videoViewCountRenderer":{"viewCount":{"runs":[{"text":"—"}]},"originalViewCount":"512"}}
        """;
        YouTubeLiveChatScraper.ParseConcurrentViewers(json).Should().Be(512);
    }

    [Fact]
    public void Returns_null_when_renderer_absent()
    {
        YouTubeLiveChatScraper.ParseConcurrentViewers("""{"actions":[]}""").Should().BeNull();
    }

    [Fact]
    public void Returns_null_on_malformed_json()
    {
        YouTubeLiveChatScraper.ParseConcurrentViewers("not json").Should().BeNull();
    }
}
