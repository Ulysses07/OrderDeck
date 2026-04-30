using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using System.Runtime.Versioning;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Settings;

namespace OrderDeck.Labeling;

/// <summary>
/// Builds a <see cref="PrintDocument"/> that lays out a batch of <see cref="Label"/>s onto
/// thermal-printer-sized pages. The layout is pure math (no actual printing) so it can be
/// unit-tested without driver access. Actual print kick-off lives in <see cref="LabelPrinter"/>.
/// </summary>
public static class LabelPrintDocument
{
    public sealed record Line(string Text, bool IsBold);

    /// <summary>
    /// Converts millimetres to the 1/100-inch units that <see cref="PrintDocument"/> uses.
    /// </summary>
    public static int MmToHundredths(int mm) => (int)Math.Round(mm * 100.0 / 25.4);

    /// <summary>
    /// Builds the two text lines printed on a label: top = @username (bold), bottom =
    /// message + price (regular).
    /// </summary>
    public static IReadOnlyList<Line> BuildLines(string username, string messageText, decimal price)
    {
        var formattedPrice = FormatPrice(price);
        return new[]
        {
            new Line(username, IsBold: true),
            new Line($"{messageText}  {formattedPrice} TL", IsBold: false)
        };
    }

    private static string FormatPrice(decimal price)
    {
        // Drop trailing zeros so 100.00 → "100", 99.50 → "99.5".
        return price.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a fresh <see cref="PrintDocument"/> that, when Print() is called, lays out
    /// the supplied labels one per page.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PrintDocument Build(IReadOnlyList<Label> labels, AppSettings settings,
        string? printerName)
    {
        var doc = new PrintDocument
        {
            DocumentName = "OrderDeck Labels"
        };
        if (!string.IsNullOrWhiteSpace(printerName))
            doc.PrinterSettings.PrinterName = printerName;

        var widthHundredths  = MmToHundredths(settings.LabelWidthMm);
        var heightHundredths = MmToHundredths(settings.LabelHeightMm);

        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.DefaultPageSettings.PaperSize =
            new PaperSize("LabelTH", widthHundredths, heightHundredths);

        int index = 0;

        doc.PrintPage += (sender, e) =>
        {
            if (index >= labels.Count)
            {
                e.HasMorePages = false;
                return;
            }

            var label = labels[index];
            var lines = BuildLines(label.Username, label.MessageText, label.Price);

            using var userFont = new Font(settings.LabelFontFamily,
                settings.LabelUserFontSize, FontStyle.Bold);
            using var messageFont = new Font(settings.LabelFontFamily,
                settings.LabelMessageFontSize, FontStyle.Regular);

            float pageWidth = e.PageBounds.Width;

            // Username line — top half, centered horizontally
            var userSize = e.Graphics!.MeasureString(lines[0].Text, userFont);
            float userY = heightHundredths * 0.15f;
            float userX = (pageWidth - userSize.Width) / 2;
            e.Graphics.DrawString(lines[0].Text, userFont, Brushes.Black, userX, userY);

            // Message line — bottom half, centered horizontally
            var msgSize = e.Graphics.MeasureString(lines[1].Text, messageFont);
            float msgY = heightHundredths * 0.55f;
            float msgX = (pageWidth - msgSize.Width) / 2;
            e.Graphics.DrawString(lines[1].Text, messageFont, Brushes.Black, msgX, msgY);

            index++;
            e.HasMorePages = index < labels.Count;
        };

        return doc;
    }
}
