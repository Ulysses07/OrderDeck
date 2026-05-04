using FluentAssertions;
using OrderDeck.Core.Settings;
using OrderDeck.Labeling;
using Xunit;

namespace OrderDeck.Tests.Labeling;

public class LabelPrintDocumentTests
{
    [Fact]
    public void MmToHundredths_converts_60mm_to_correct_imaging_units()
    {
        // PrintDocument page units are 1/100 inch. 1 inch = 25.4 mm.
        // 60mm = 60 / 25.4 inch = ~2.362 inch = ~236 hundredths.
        var hundredths = LabelPrintDocument.MmToHundredths(60);
        hundredths.Should().BeInRange(235, 237);
    }

    [Fact]
    public void MmToHundredths_converts_30mm_correctly()
    {
        var hundredths = LabelPrintDocument.MmToHundredths(30);
        hundredths.Should().BeInRange(117, 119);
    }

    [Theory]
    [InlineData("instagram", "IG")]
    [InlineData("tiktok",    "TT")]
    [InlineData("facebook",  "FB")]
    [InlineData("youtube",   "YT")]
    [InlineData("Instagram", "IG")] // case-insensitive
    [InlineData("",          "??")]
    [InlineData("unknown",   "??")]
    public void PlatformAbbreviation_returns_two_letter_code(string platform, string expected)
    {
        LabelPrintDocument.PlatformAbbreviation(platform).Should().Be(expected);
    }

    [Fact]
    public void BuildLines_splits_username_and_message_with_price()
    {
        var lines = LabelPrintDocument.BuildLines("@ayse_y", "MAVI XL aldım", price: 100m);

        lines.Should().HaveCount(2);
        lines[0].Text.Should().Be("@ayse_y");
        lines[0].IsBold.Should().BeTrue();

        lines[1].Text.Should().Contain("MAVI XL aldım");
        lines[1].Text.Should().Contain("100");
    }

    [Fact]
    public void BuildLines_formats_decimal_price_without_trailing_zeros()
    {
        var lines = LabelPrintDocument.BuildLines("@a", "x", 100m);
        lines[1].Text.Should().Contain("100");
        lines[1].Text.Should().NotContain("100.00");
    }

    [Fact]
    public void BuildLines_keeps_decimal_when_meaningful()
    {
        var lines = LabelPrintDocument.BuildLines("@a", "x", 99.50m);
        lines[1].Text.Should().Contain("99.5").And.Contain("TL");
    }
}
