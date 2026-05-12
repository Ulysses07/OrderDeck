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
    /// Builds the two text lines printed on a label: top = display name (bold), bottom =
    /// message + price (regular). The Y-marker for backup-promoted labels is drawn
    /// separately by <see cref="Build"/> (corner badge), not embedded in these lines.
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

    /// <summary>
    /// Etikette gösterilecek "kişi adı"nı çözer. YouTube label'larında
    /// <see cref="Label.Username"/> stabil channel ID (UCxxx...) — operatör
    /// için anlamsız. <see cref="Label.DisplayName"/> varsa onu kullan;
    /// yoksa Username'e düş. Bu davranış sticker üzerindeki "isim"in her
    /// platformda okunabilir olmasını garantiler.
    /// </summary>
    public static string ResolveDisplayLabel(Label label)
    {
        if (!string.IsNullOrWhiteSpace(label.DisplayName))
            return label.DisplayName!.Trim();
        return label.Username ?? string.Empty;
    }

    private static string FormatPrice(decimal price)
    {
        // Drop trailing zeros so 100.00 → "100", 99.50 → "99.5".
        return price.ToString("0.##", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Two-letter platform code for the corner stamp on the printed label.
    /// Thermal printers can render plain ASCII reliably across drivers, so we
    /// avoid emoji here — the abbreviations carry over from the in-app chat
    /// header conventions (IG/TT/FB/YT).
    /// </summary>
    public static string PlatformAbbreviation(string platform) => platform?.ToLowerInvariant() switch
    {
        "instagram" => "IG",
        "tiktok"    => "TT",
        "facebook"  => "FB",
        "youtube"   => "YT",
        _           => "??",
    };

    /// <summary>
    /// Builds a fresh <see cref="PrintDocument"/> that, when Print() is called, lays out
    /// the supplied labels one per page. Kargo PR F: <paramref name="recipientPaysLabelIds"/>
    /// set'inde olan label'lar etikette "ALICI ÖDEMELİ" kırmızı yazı + kargo ücreti
    /// (settings.Shipping.ShippingFee, varsa) render eder.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PrintDocument Build(IReadOnlyList<Label> labels, AppSettings settings,
        string? printerName, IReadOnlySet<string>? recipientPaysLabelIds = null)
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
            // YouTube label'larında Username = stabil channel ID (UCxxx...) —
            // operatöre anlamsız. DisplayName varsa onu yaz.
            var displayLabel = ResolveDisplayLabel(label);
            var lines = BuildLines(displayLabel, label.MessageText, label.Price);

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

            // Platform code badge — small, top-LEFT corner. Same visual
            // language as the Y badge (top-right) so the operator can scan
            // both corners at a glance: left = where the order came from,
            // right = whether it's a backup.
            using (var platformFont = new Font(settings.LabelFontFamily,
                settings.LabelUserFontSize * 0.65f, FontStyle.Bold))
            {
                var platformText = PlatformAbbreviation(label.Platform);
                var platformSize = e.Graphics.MeasureString(platformText, platformFont);
                float padX = 4f, padY = 2f;
                float boxW = platformSize.Width + padX * 2;
                float boxH = platformSize.Height + padY * 2;
                float boxX = 6f;
                float boxY = 6f;
                using var boxPen = new Pen(Brushes.Black, 1.5f);
                e.Graphics.DrawRectangle(boxPen, boxX, boxY, boxW, boxH);
                e.Graphics.DrawString(platformText, platformFont, Brushes.Black,
                    boxX + padX, boxY + padY);
            }

            // Kargo PR F: "ALICI ÖDEMELİ" — bottom-center red strip when this
            // label's customer is in RecipientPays mode. Vendor depo personeli
            // etiketi tek bakışta görüp kargo şirketine "alıcıdan tahsil et"
            // notuyla teslim etmeli.
            if (recipientPaysLabelIds is not null && recipientPaysLabelIds.Contains(label.Id))
            {
                using var rpFont = new Font(settings.LabelFontFamily,
                    settings.LabelMessageFontSize * 0.95f, FontStyle.Bold);
                string rpText = "ALICI ÖDEMELİ";
                if (settings.Shipping.ShippingFee is { } fee && fee > 0)
                    rpText += $"  {fee:0.##} TL";
                var rpSize = e.Graphics.MeasureString(rpText, rpFont);
                float rpY = heightHundredths * 0.82f;
                float rpX = (pageWidth - rpSize.Width) / 2;
                using var rpBrush = new SolidBrush(Color.Red);
                e.Graphics.DrawString(rpText, rpFont, rpBrush, rpX, rpY);
            }

            // "Y" badge for backup-promoted labels — small, top-right corner.
            // Box-stroke + bold "Y" so it's spottable from a metre away on a
            // 60×40 mm thermal sticker without competing with the username.
            if (label.IsBackupPromoted)
            {
                using var badgeFont = new Font(settings.LabelFontFamily,
                    settings.LabelUserFontSize * 0.65f, FontStyle.Bold);
                const string badgeText = "Y";
                var badgeSize = e.Graphics.MeasureString(badgeText, badgeFont);
                float padX = 4f, padY = 2f;
                float boxW = badgeSize.Width + padX * 2;
                float boxH = badgeSize.Height + padY * 2;
                float boxX = pageWidth - boxW - 6f;
                float boxY = 6f;
                using var boxPen = new Pen(Brushes.Black, 1.5f);
                e.Graphics.DrawRectangle(boxPen, boxX, boxY, boxW, boxH);
                e.Graphics.DrawString(badgeText, badgeFont, Brushes.Black,
                    boxX + padX, boxY + padY);
            }

            index++;
            e.HasMorePages = index < labels.Count;
        };

        return doc;
    }
}
