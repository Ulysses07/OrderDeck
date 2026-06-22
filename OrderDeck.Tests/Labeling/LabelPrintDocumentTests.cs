using FluentAssertions;
using OrderDeck.Core.Sales;
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

    [Fact]
    public void BuildLines_gift_mode_shows_keyword_and_HEDIYE_not_price()
    {
        // Çekiliş kazanan etiketi: ad + çekiliş kodu (keyword) + "HEDİYE", fiyat YOK.
        var lines = LabelPrintDocument.BuildLines("Ayşe Yılmaz", "KAZAN", price: 0m, isGift: true);

        lines.Should().HaveCount(2);
        lines[0].Text.Should().Be("Ayşe Yılmaz");
        lines[1].Text.Should().Contain("KAZAN").And.Contain("HEDİYE");
        lines[1].Text.Should().NotContain("TL");
    }

    // ── ResolveDisplayLabel: YouTube channel ID fix ──────────────────────

    private static Label MakeLabel(string username, string? displayName) =>
        new(
            Id: "L1", SessionId: "S1", CustomerId: "C1",
            Platform: "youtube", Username: username,
            MessageText: "msg", Code: null, Price: 100m,
            AddedAt: 1000L, PrintedAt: null,
            DisplayName: displayName);

    // ── BuildFittedSecondLine: uzun mesajda fiyat ASLA kesilmez ──────────
    // Gerçek Graphics ile ölçer; sonuç verilen genişlikte kalır, fiyat tam.

    [Fact]
    public void BuildFittedSecondLine_long_message_truncates_but_keeps_price()
    {
        using var bmp = new System.Drawing.Bitmap(10, 10);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        using var font = new System.Drawing.Font("Arial", 12f);
        const float maxWidth = 150f;
        var longMsg = "çok uzun bir aldım mesajı kırmızı kazak XL beden acele lütfen";

        var line = LabelPrintDocument.BuildFittedSecondLine(g, longMsg, 250m, isGift: false, font, maxWidth);

        line.Should().EndWith("250 TL");                 // fiyat kesilmedi
        line.Should().Contain("…");                       // mesaj kısaldı
        g.MeasureString(line, font).Width.Should().BeLessThanOrEqualTo(maxWidth + 8f);
    }

    [Fact]
    public void BuildFittedSecondLine_short_message_kept_whole_with_price()
    {
        using var bmp = new System.Drawing.Bitmap(10, 10);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        using var font = new System.Drawing.Font("Arial", 12f);

        var line = LabelPrintDocument.BuildFittedSecondLine(g, "kısa", 100m, isGift: false, font, 600f);

        line.Should().Be("kısa  100 TL");
        line.Should().NotContain("…");
    }

    [Fact]
    public void BuildFittedSecondLine_gift_mode_ends_with_HEDIYE_no_TL()
    {
        using var bmp = new System.Drawing.Bitmap(10, 10);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        using var font = new System.Drawing.Font("Arial", 12f);

        var line = LabelPrintDocument.BuildFittedSecondLine(g, "KAZAN", 0m, isGift: true, font, 600f);

        line.Should().EndWith("HEDİYE");
        line.Should().NotContain("TL");
    }

    [Fact]
    public void ResolveDisplayLabel_uses_DisplayName_when_set()
    {
        var label = MakeLabel("UCxxx_youtube_channel_id", "Ayşe Yılmaz");
        LabelPrintDocument.ResolveDisplayLabel(label).Should().Be("Ayşe Yılmaz");
    }

    [Fact]
    public void ResolveDisplayLabel_falls_back_to_Username_when_DisplayName_null()
    {
        var label = MakeLabel("@ayse_y", displayName: null);
        LabelPrintDocument.ResolveDisplayLabel(label).Should().Be("@ayse_y");
    }

    [Fact]
    public void ResolveDisplayLabel_falls_back_to_Username_when_DisplayName_empty()
    {
        var label = MakeLabel("@ayse_y", displayName: "   ");
        LabelPrintDocument.ResolveDisplayLabel(label).Should().Be("@ayse_y");
    }

    [Fact]
    public void ResolveDisplayLabel_trims_DisplayName_whitespace()
    {
        var label = MakeLabel("UCxxx", "  Ayşe Yılmaz  ");
        LabelPrintDocument.ResolveDisplayLabel(label).Should().Be("Ayşe Yılmaz");
    }
}
