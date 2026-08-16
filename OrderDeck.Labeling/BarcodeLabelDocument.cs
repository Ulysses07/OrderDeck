using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace OrderDeck.Labeling;

/// <summary>
/// Barkodlu ürün etiketi. Müşteri etiketinden (<see cref="LabelPrintDocument"/>)
/// AYRI: yükü de düzeni de farklı, ortak soyutlama ikisini de bulandırırdı.
///
/// <para><b>Neden vektör:</b> barkod raster görüntü olarak basılsaydı
/// yazıcının 203 dpi ızgarası ile görüntünün pikselleri hizalanmaz, çizgi
/// kalınlıkları bir nokta oynar ve okuma oranı düşerdi. Dikdörtgen olarak
/// çizince sürücü ızgaraya kendisi oturtuyor.</para>
///
/// <para><b>Modül genişliği:</b> 60 mm etikete 0.4 mm modülle basıyoruz
/// (10 hane ≈ 44 mm). Standardın izin verdiği asgari 0.25 mm'ye inmiyoruz:
/// 203 dpi yazıcının nokta boyu 0.125 mm, yani 0.25 mm modül tam iki nokta —
/// bir noktalık sapma çizgiyi %50 bozar. 0.4 mm'de sapma payı var.</para>
///
/// <para><b>Platform:</b> <see cref="LabelPrintDocument"/> ile aynı kalıp —
/// windows işareti sınıfta değil, yalnız System.Drawing'e dokunan üyelerde.
/// <see cref="EncodeWithQuietZone"/> ve <see cref="MmToHundredths"/> saf
/// hesap; ileride sunucu tarafında da çağrılabilsinler diye platformsuz
/// bırakıldılar (ZXing'in çekirdek paketi de platformsuz).</para>
/// </summary>
public static class BarcodeLabelDocument
{
    /// <summary>Standardın istediği asgari 10 modül.</summary>
    public const int QuietZoneModules = 10;

    /// <summary>Modül genişliği (mm). Gerekçe sınıf doc'unda.</summary>
    public const float ModuleWidthMm = 0.4f;

    /// <summary>Çizgi yüksekliği (mm).</summary>
    public const float BarHeightMm = 12f;

    /// <summary>
    /// Milimetreyi <see cref="PrintDocument"/>'ın kullandığı 1/100 inch
    /// birimine çevirir. <see cref="LabelPrintDocument.MmToHundredths"/> ile
    /// birebir aynı olmak zorunda: iki belge aynı kâğıda basıyor.
    /// </summary>
    public static int MmToHundredths(int mm) => (int)Math.Round(mm * 100.0 / 25.4);

    /// <summary>
    /// Code128 modül dizisi + iki uçta sessiz bölge.
    ///
    /// <para>ZXing'in <c>encode</c>'u sessiz bölge VERMİYOR — yalnız çizgi
    /// desenini döndürüyor. Eklemeseydik okuyucu barkodun nerede bittiğini
    /// anlayamaz, etiketin kenarındaki mürekkebi veri sanardı.</para>
    /// </summary>
    public static bool[] EncodeWithQuietZone(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Barkod yükü boş olamaz.", nameof(payload));

        var bars = new ZXing.OneD.Code128Writer().encode(payload);

        var result = new bool[bars.Length + QuietZoneModules * 2];
        for (var i = 0; i < bars.Length; i++)
            result[QuietZoneModules + i] = bars[i];
        return result;
    }

    /// <summary>Tek etikette basılacak içerik.</summary>
    public sealed record Label(string Barcode, string ProductName, string VariantName);

    /// <summary>
    /// Her etiketi <paramref name="copies"/> kez basan bir belge kurar.
    /// Sayfa başına tek etiket — rulo yazıcıda "sayfa" zaten bir etiket.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static PrintDocument Build(
        IReadOnlyList<Label> labels, int copies,
        string? printerName, int widthMm, int heightMm, string fontFamily)
    {
        if (labels.Count == 0)
            throw new ArgumentException("Basılacak etiket yok.", nameof(labels));
        if (copies <= 0)
            throw new ArgumentOutOfRangeException(nameof(copies));

        var queue = new List<Label>(labels.Count * copies);
        foreach (var label in labels)
            for (var i = 0; i < copies; i++)
                queue.Add(label);

        var doc = new PrintDocument();
        // Boş isim varsayılan yazıcıyı seçtirir; atarsak sürücü "böyle bir
        // yazıcı yok" diye patlar. LabelPrinter ile aynı davranış.
        if (!string.IsNullOrWhiteSpace(printerName))
            doc.PrinterSettings.PrinterName = printerName;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.DefaultPageSettings.PaperSize = new PaperSize(
            "LabelBarcode", MmToHundredths(widthMm), MmToHundredths(heightMm));

        var index = 0;
        doc.PrintPage += (_, e) =>
        {
            DrawLabel(e.Graphics!, queue[index], widthMm, heightMm, fontFamily);
            index++;
            e.HasMorePages = index < queue.Count;
        };
        return doc;
    }

    [SupportedOSPlatform("windows")]
    private static void DrawLabel(
        Graphics g, Label label, int widthMm, int heightMm, string fontFamily)
    {
        // Grafik birimi milimetreye çevriliyor: bütün ölçüler etiketin
        // fiziksel boyutuyla aynı dilde olsun, dpi hesabı tek yerde kalsın.
        g.PageUnit = GraphicsUnit.Millimeter;

        var modules = EncodeWithQuietZone(label.Barcode);
        var barcodeWidth = modules.Length * ModuleWidthMm;
        var left = Math.Max(0f, (widthMm - barcodeWidth) / 2f);

        using var nameFont = new Font(fontFamily, 3f, FontStyle.Bold);
        using var variantFont = new Font(fontFamily, 2.5f);
        using var codeFont = new Font(fontFamily, 2.5f);
        using var black = new SolidBrush(Color.Black);

        var y = 1.5f;
        g.DrawString(
            TruncateToWidth(g, label.ProductName, nameFont, widthMm - 2f),
            nameFont, black, 1f, y);
        y += 4f;

        if (label.VariantName.Length > 0)
        {
            g.DrawString(
                TruncateToWidth(g, label.VariantName, variantFont, widthMm - 2f),
                variantFont, black, 1f, y);
            y += 3.5f;
        }

        for (var i = 0; i < modules.Length; i++)
            if (modules[i])
                g.FillRectangle(black, left + i * ModuleWidthMm, y, ModuleWidthMm, BarHeightMm);

        y += BarHeightMm + 1f;

        // İnsan tarafından okunabilir satır: okuyucu çalışmazsa operatör
        // numarayı elle yazabilsin.
        var size = g.MeasureString(label.Barcode, codeFont);
        g.DrawString(label.Barcode, codeFont, black, (widthMm - size.Width) / 2f, y);
    }

    /// <summary>
    /// Sığmayan metni "…" ile kırpar. <see cref="LabelPrintDocument"/>'teki
    /// kardeşiyle aynı davranış; ayrı duruyorlar çünkü o sınıf iç kullanım
    /// için özel ve iki belge birbirine bağlanmasın isteniyor.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static string TruncateToWidth(Graphics g, string text, Font font, float maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;

        for (var len = text.Length - 1; len > 0; len--)
        {
            var candidate = text[..len] + "…";
            if (g.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }
        return "…";
    }
}
