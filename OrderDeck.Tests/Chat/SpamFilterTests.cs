using FluentAssertions;
using OrderDeck.Core.Chat;
using Xunit;

namespace OrderDeck.Tests.Chat;

public class SpamFilterTests
{
    private static SpamFilter Make(SpamFilterSettings s) => new(() => s);

    [Fact]
    public void Disabled_filter_passes_everything()
    {
        var s = new SpamFilterSettings
        {
            Enabled = false,
            DropLinks = true,
            DropProfanity = true,
            DropDuplicates = true,
            BlockedWords = new() { "kötü" }
        };
        var f = Make(s);

        f.ShouldDrop("https://spam.com",   100).Should().BeNull();
        f.ShouldDrop("kötü adam",          100).Should().BeNull();
        f.ShouldDrop("aynı mesaj",         100).Should().BeNull();
        f.ShouldDrop("aynı mesaj",         101).Should().BeNull();
    }

    [Fact]
    public void DropShortMessages_blocks_below_threshold_only()
    {
        var s = new SpamFilterSettings { DropShortMessages = true, MinMessageLength = 3 };
        var f = Make(s);

        f.ShouldDrop("ok",   100).Should().Be("short");
        f.ShouldDrop("aa",   100).Should().Be("short");
        f.ShouldDrop("evet", 100).Should().BeNull();
    }

    [Fact]
    public void DropLinks_catches_url_shapes()
    {
        var f = Make(new SpamFilterSettings { DropLinks = true });

        f.ShouldDrop("https://example.com check this", 100).Should().Be("link");
        f.ShouldDrop("www.x.com",                       100).Should().Be("link");
        f.ShouldDrop("bence shopify.com çok güzel",     100).Should().Be("link");
        f.ShouldDrop("siteleri çok beğendim",           100).Should().BeNull();
    }

    [Fact]
    public void DropAllCaps_requires_both_length_and_uppercase_ratio()
    {
        var f = Make(new SpamFilterSettings { DropAllCaps = true });

        f.ShouldDrop("ÇOK GÜZEL ÜRÜN BU", 100).Should().Be("allcaps");
        // Single uppercase word OK
        f.ShouldDrop("XL beden var mı",   100).Should().BeNull();
        // Short text doesn't trigger (might be a brand name)
        f.ShouldDrop("WOW",               100).Should().BeNull();
    }

    [Fact]
    public void DropProfanity_uses_whole_word_match()
    {
        var s = new SpamFilterSettings
        {
            DropProfanity = true,
            BlockedWords = new() { "kötü" }
        };
        var f = Make(s);

        f.ShouldDrop("bu çok kötü bir ürün",  100).Should().Be("profanity");
        f.ShouldDrop("KÖTÜ ürün",              100).Should().Be("profanity");
        // Substring shouldn't match — "köyüm" contains the same letters but
        // not the word.
        f.ShouldDrop("köyüm güzel",            100).Should().BeNull();
        f.ShouldDrop("hiç de fena değil",      100).Should().BeNull();
    }

    [Fact]
    public void DropDuplicates_within_30s_window_only()
    {
        var f = Make(new SpamFilterSettings { DropDuplicates = true });

        f.ShouldDrop("aynı yazı", 100).Should().BeNull();        // first time → ok
        f.ShouldDrop("aynı yazı", 110).Should().Be("duplicate"); // 10s later → drop
        f.ShouldDrop("aynı yazı", 200).Should().BeNull();        // 100s later → window expired, ok again
    }

    [Fact]
    public void DropDuplicates_is_case_insensitive_and_trim_aware()
    {
        var f = Make(new SpamFilterSettings { DropDuplicates = true });

        f.ShouldDrop("MERHABA", 100).Should().BeNull();
        f.ShouldDrop("merhaba", 105).Should().Be("duplicate");
        f.ShouldDrop("  merhaba  ", 110).Should().Be("duplicate");
    }

    [Fact]
    public void Empty_or_whitespace_message_passes_through()
    {
        var f = Make(new SpamFilterSettings { DropShortMessages = true, MinMessageLength = 5 });
        f.ShouldDrop("", 100).Should().BeNull();
        f.ShouldDrop("   ", 100).Should().BeNull();
    }

    [Fact]
    public void Multiple_rules_first_match_wins()
    {
        var s = new SpamFilterSettings
        {
            DropShortMessages = true, MinMessageLength = 5,
            DropLinks = true,
            DropDuplicates = true
        };
        var f = Make(s);

        // "go" hits short before anything else.
        f.ShouldDrop("go", 100).Should().Be("short");
    }
}
