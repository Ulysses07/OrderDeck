using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class YouTubeVideoIdExtractorTests
{
    [Theory]
    [InlineData("dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/watch?v=dQw4w9WgXcQ&t=10s", "dQw4w9WgXcQ")]
    [InlineData("https://youtu.be/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ", "dQw4w9WgXcQ")]
    [InlineData("https://www.youtube.com/live/dQw4w9WgXcQ?feature=share", "dQw4w9WgXcQ")]
    public void Extracts_video_id_from_common_inputs(string input, string expected)
    {
        YouTubeVideoIdExtractor.TryExtract(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("@somehandle")]
    [InlineData("https://www.youtube.com/@orderdeck")]
    [InlineData("https://www.youtube.com/@orderdeck/live")]  // handle, not a video URL
    [InlineData("notavideourl")]
    [InlineData("dQw4")]  // too short
    public void Returns_null_when_no_video_id_present(string? input)
    {
        YouTubeVideoIdExtractor.TryExtract(input).Should().BeNull();
    }
}
