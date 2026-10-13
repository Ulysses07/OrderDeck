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
        var lines = LabelPrintDocument.BuildLines("Örnek Müşteri", "KAZAN", price: 0m, isGift: true);

        lines.Should().HaveCount(2);
        lines[0].Text.Should().Be("Örnek Müşteri");
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
        var label = MakeLabel("UCxxx_youtube_channel_id", "Örnek Müşteri");
        LabelPrintDocument.ResolveDisplayLabel(label).Should().Be("Örnek Müşteri");
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
        var label = MakeLabel("UCxxx", "  Örnek Müşteri  ");
        LabelPrintDocument.ResolveDisplayLabel(label).Should().Be("Örnek Müşteri");
    }

    // ── "DÜZELTME" işareti (K5, 2026-10-12) ────────────────────────────
    // Fiyatı düzeltilmiş etiketin yeniden baskısında küçük kutulu işaret: eski (yanlış fiyatlı) kâğıt da
    // pakette durabilir. Sayfa gerçek yazıcı olmadan bir bit eşleme çizilir; işaretli ve işaretsiz baskı
    // karşılaştırılır.

    private static System.Drawing.Bitmap RenderFirstPage(Label label, IReadOnlySet<string>? corrections,
        AppSettings? settings = null, IReadOnlySet<string>? recipientPays = null)
    {
        settings ??= new AppSettings();
        using var doc = LabelPrintDocument.Build(new[] { label }, settings, printerName: null,
            recipientPaysLabelIds: recipientPays, correctionLabelIds: corrections);
        var w = LabelPrintDocument.MmToHundredths(settings.LabelWidthMm);
        var h = LabelPrintDocument.MmToHundredths(settings.LabelHeightMm);
        var bmp = new System.Drawing.Bitmap(w, h);
        using var g = System.Drawing.Graphics.FromImage(bmp);
        g.Clear(System.Drawing.Color.White);
        var bounds = new System.Drawing.Rectangle(0, 0, w, h);
        var args = new System.Drawing.Printing.PrintPageEventArgs(g, bounds, bounds,
            new System.Drawing.Printing.PageSettings());
        typeof(System.Drawing.Printing.PrintDocument)
            .GetMethod("OnPrintPage", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(doc, new object[] { args });
        return bmp;
    }

    [Fact]
    public void Correction_mark_is_drawn_only_for_corrected_labels_in_the_lower_right()
    {
        var label = new Label("l1", "s1", "c1", "instagram", "@ayse", "aldım", "A12", 205m, 1000L, 1000L);

        using var plain = RenderFirstPage(label, null);
        using var marked = RenderFirstPage(label, new HashSet<string> { "l1" });
        using var other = RenderFirstPage(label, new HashSet<string> { "baska" });

        var diff = new List<(int X, int Y)>();
        for (var y = 0; y < plain.Height; y++)
            for (var x = 0; x < plain.Width; x++)
            {
                plain.GetPixel(x, y).Should().Be(other.GetPixel(x, y), "işaret yalnız düzeltilen etikete");
                if (plain.GetPixel(x, y) != marked.GetPixel(x, y)) diff.Add((x, y));
            }

        diff.Should().NotBeEmpty("düzeltilen etiketin kâğıdında işaret var");
        diff.Should().OnlyContain(p => p.X > plain.Width / 2 && p.Y > plain.Height / 2,
            "işaret sağ-alt köşede — ad, mesaj ve fiyatla çakışmaz");
    }

    [Theory]
    [InlineData(12, false)]
    [InlineData(12, true)]
    [InlineData(14, true)]
    [InlineData(16, true)]
    [InlineData(16, false)]
    public void Correction_mark_never_covers_the_printed_text(int messageFontSize, bool recipientPays)
    {
        // Mesaj yazısı büyüyünce (ayarda 6–72) ve ALICI ÖDEMELİ şeridi varken sabit konumlu işaret fiyatın
        // ("… 1205 TL") üstüne biniyordu.
        var settings = new AppSettings { LabelMessageFontSize = messageFontSize };
        var label = new Label("l1", "s1", "c1", "instagram", "@ayse", "kırmızı olanı alıyorum lütfen ayırın",
            "A12", 1205m, 1000L, 1000L);
        var rp = recipientPays ? new HashSet<string> { "l1" } : null;

        using var plain = RenderFirstPage(label, null, settings, rp);
        using var marked = RenderFirstPage(label, new HashSet<string> { "l1" }, settings, rp);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < plain.Height; y++)
            for (var x = 0; x < plain.Width; x++)
                if (plain.GetPixel(x, y) != marked.GetPixel(x, y))
                {
                    minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                    maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                }
        maxX.Should().BeGreaterThan(-1, "işaret basıldı");

        var white = System.Drawing.Color.White.ToArgb();
        var covered = 0;
        for (var y = Math.Max(0, minY - 1); y <= Math.Min(plain.Height - 1, maxY + 1); y++)
            for (var x = Math.Max(0, minX - 1); x <= Math.Min(plain.Width - 1, maxX + 1); x++)
                if (plain.GetPixel(x, y).ToArgb() != white) covered++;
        covered.Should().Be(0, "işaretin kutusu ad, mesaj/fiyat ya da şerit mürekkebine değmez");
    }
}
