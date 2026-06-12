using System.Text.Json;
using FluentAssertions;
using OrderDeck.Chat.Ingestors.YouTube;
using Xunit;

namespace OrderDeck.Tests.Chat.YouTube;

/// <summary>
/// innertube liveChat "runs" → görüntü metni dönüşümü. Asıl regresyon: standart
/// Unicode emoji'ler (😀🔥) shortcode metni (:fire:) olarak geliyordu. Standart
/// emoji'de emojiId gerçek karakteri taşır → onu basmalı; custom/membership
/// emojisinde Unicode yok → shortcode fallback kalmalı.
/// </summary>
public class YouTubeEmojiRunsTests
{
    private static string Extract(string messageJson)
    {
        // ExtractRunsText bir "renderer" alıp içindeki `field` ("message")
        // property'sini okuyor; gerçek liveChatTextMessageRenderer şeklini taklit et.
        using var doc = JsonDocument.Parse($$"""{ "message": {{messageJson}} }""");
        return YouTubeLiveChatScraper.ExtractRunsText(doc.RootElement, "message");
    }

    [Fact]
    public void Standard_emoji_renders_as_unicode_character()
    {
        // 🔥 standart emoji: emojiId = gerçek karakter, isCustomEmoji yok.
        var msg = """
        { "runs": [ { "emoji": {
            "emojiId": "🔥",
            "shortcuts": [":fire:"],
            "isCustomEmoji": false
        } } ] }
        """;
        Extract(msg).Should().Be("🔥");
    }

    [Fact]
    public void Standard_emoji_without_isCustomEmoji_flag_still_uses_unicode()
    {
        // YouTube standart emoji run'ında isCustomEmoji alanını hiç göndermeyebilir.
        var msg = """
        { "runs": [ { "emoji": { "emojiId": "😀", "shortcuts": [":grinning:"] } } ] }
        """;
        Extract(msg).Should().Be("😀");
    }

    [Fact]
    public void Custom_channel_emoji_falls_back_to_shortcode()
    {
        // Membership/kanal emojisi: opak emojiId + isCustomEmoji:true, Unicode yok.
        var msg = """
        { "runs": [ { "emoji": {
            "emojiId": "UCabc123/xyz789",
            "shortcuts": [":_kalp:"],
            "isCustomEmoji": true
        } } ] }
        """;
        Extract(msg).Should().Be(":_kalp:");
    }

    [Fact]
    public void Mixed_text_and_emoji_are_concatenated_in_order()
    {
        var msg = """
        { "runs": [
            { "text": "harika " },
            { "emoji": { "emojiId": "🔥", "shortcuts": [":fire:"] } },
            { "text": " al" }
        ] }
        """;
        Extract(msg).Should().Be("harika 🔥 al");
    }

    [Fact]
    public void Custom_emoji_with_only_shortcuts_uses_shortcode()
    {
        // emojiId yok, sadece shortcuts var → fallback shortcode.
        var msg = """
        { "runs": [ { "emoji": { "shortcuts": [":_party:"], "isCustomEmoji": true } } ] }
        """;
        Extract(msg).Should().Be(":_party:");
    }

    [Fact]
    public void Plain_text_runs_pass_through()
    {
        Extract("""{ "runs": [ { "text": "iki tane alıyorum" } ] }""")
            .Should().Be("iki tane alıyorum");
    }

    [Fact]
    public void SimpleText_field_is_returned_directly()
    {
        Extract("""{ "simpleText": "Kanal Adı" }""").Should().Be("Kanal Adı");
    }

    [Fact]
    public void Missing_runs_and_simpleText_yields_empty()
    {
        Extract("""{ "foo": "bar" }""").Should().BeEmpty();
    }
}
