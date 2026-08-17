using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
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

    // Dikey düzen ölçüleri. Sabit olarak duruyorlar çünkü asgari etiket
    // yüksekliği bunların TOPLAMI; çizimde gömülü sayı kalsaydı doğrulama
    // ile düzen ayrı ayrı değişebilir ve kapı sessizce yanlış olurdu.
    private const float TopMarginMm = 1.5f;
    private const float NameLineMm = 4f;
    private const float VariantLineMm = 3.5f;
    private const float BarToCodeGapMm = 1f;
    private const float CodeLineMm = 3f;
    private const float BottomMarginMm = 1f;

    /// <summary>
    /// Düzenin dikeyde ihtiyaç duyduğu asgari etiket yüksekliği (mm).
    ///
    /// <para>Varyant satırı VAR sayılıyor: barkodu olan şey varyanttır, satır
    /// pratikte hep dolu. Yok sayıp 3.5 mm kısmak, satırın çıktığı anda
    /// kırpılan bir etiket üretirdi — kapının tek işi bunu engellemek.</para>
    /// </summary>
    public const float MinLabelHeightMm =
        TopMarginMm + NameLineMm + VariantLineMm + BarHeightMm
        + BarToCodeGapMm + CodeLineMm + BottomMarginMm;

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

    /// <summary>
    /// <see cref="EncodeWithQuietZone"/> + etikete sığma kontrolü.
    ///
    /// <para><b>Neden ayrı bir kapı:</b> sığmayan barkodu kırpmak (eski
    /// <c>Math.Max(0f, …)</c>) taşmayı çözmüyor, GİZLİYOR — kâğıda gözle
    /// kusursuz görünen ama stop deseni ve checksum'ı kesilmiş bir barkod
    /// basılırdı. Etiket ürüne yapıştırılır, hata ancak yayının ortasında
    /// okuyucu ötmeyince ortaya çıkardı. Burada patlamak, kâğıt hiç
    /// hareket etmeden operatörü durduruyor.</para>
    ///
    /// <para><b>Kapasite:</b> 60 mm etikette 0.4 mm modülle 150 modül var,
    /// 20'si sessiz bölgeye gidiyor. Sayaçtan gelen 10 haneli sayı Code128-C
    /// ile çift çift sıkıştığı için 110 modül (≈44 mm) — bol bol sığar.
    /// <b>Elle</b> yazılan harfli barkod sıkışmaz (Code128-B, karakter başına
    /// 11 modül) ve 60 mm'ye ancak 8 karakter girer. Sunucu elle girilen
    /// barkoda 64 karaktere kadar izin verdiği için bu sınır gerçek.</para>
    ///
    /// <para>Saf hesap: System.Drawing'e dokunmuyor, platformsuz kalıyor.</para>
    /// </summary>
    public static bool[] EncodeForLabel(string payload, int labelWidthMm)
    {
        var modules = EncodeWithQuietZone(payload);
        var neededMm = modules.Length * ModuleWidthMm;
        if (neededMm > labelWidthMm)
            throw new ArgumentException(
                $"'{payload}' barkodu {labelWidthMm} mm etikete sığmıyor: " +
                $"{neededMm:0.#} mm gerekiyor. Ayarlar'dan etiket genişliğini " +
                "büyüt ya da bu varyanta daha kısa bir barkod ver.",
                nameof(payload));
        return modules;
    }

    /// <summary>
    /// Etiket yüksekliğinin düzeni taşıdığını doğrular.
    ///
    /// <para><b>Neden ayrı bir kapı:</b> genişlik taşması patlarken yükseklik
    /// taşması SESSİZDİ. Ayarlar 10 mm yüksekliğe izin veriyor (ölçü müşteri
    /// etiketiyle paylaşılıyor, orada 10 mm mantıklı olabilir) ama barkod
    /// düzeni <see cref="MinLabelHeightMm"/> mm istiyor. Kısa kâğıtta çizgilerin
    /// altı ve insan-okur satır yazıcı tarafından kırpılır: etiket gözle
    /// neredeyse doğru görünür, ürüne yapıştırılır, sonra okuyucu ötmez ve elle
    /// yazılacak numara da yoktur. Genişlikte reddedilen hata sınıfının aynısı.</para>
    ///
    /// <para><b>Neden ayarı kısıtlamıyoruz:</b> genişlik/yükseklik müşteri
    /// etiketiyle ORTAK. Küçük kâğıtla yalnız müşteri etiketi basan operatörün
    /// ayarını reddetmek çalışan bir akışı bozardı. Doğru yer basım anı: kâğıt
    /// hiç hareket etmeden, ne yapması gerektiğini söyleyen bir hata.</para>
    ///
    /// <para>Saf hesap: System.Drawing'e dokunmuyor, platformsuz kalıyor.</para>
    /// </summary>
    public static void EnsureLabelHeightFits(int labelHeightMm)
    {
        if (labelHeightMm < MinLabelHeightMm)
            throw new ArgumentException(
                $"{labelHeightMm} mm etiket barkod düzenine yetmiyor: en az " +
                $"{MinLabelHeightMm:0.#} mm gerekiyor. Ayarlar'dan etiket " +
                "yüksekliğini büyüt.",
                nameof(labelHeightMm));
    }

    /// <summary>Tek etikette basılacak içerik.</summary>
    public sealed record Label(string Barcode, string ProductName, string VariantName);

    /// <summary>
    /// Her etiketi <paramref name="copies"/> kez basan bir belge kurar.
    /// Sayfa başına tek etiket — rulo yazıcıda "sayfa" zaten bir etiket.
    ///
    /// <para><b>Kodlama burada, çizimde değil:</b> her barkod belge kurulurken
    /// bir kez kodlanıp doğrulanıyor. Böylece sığmayan bir yük <c>PrintPage</c>
    /// içinde — yani kâğıt çoktan sarılmışken, önceki etiketler basılmışken —
    /// değil, tek bir sayfa gitmeden patlıyor. Tekrarlar da aynı diziyi
    /// paylaşıyor: kopya başına yeniden kodlamanın anlamı yok.</para>
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
        // Yükseklik kapısı EN BAŞTA: yazıcı seçilmeden, sayfa boyutu
        // kurulmadan patlıyor. Yüke bağlı olmadığı için tek etiket bile
        // kodlanmadan bilinebiliyor.
        EnsureLabelHeightFits(heightMm);

        var queue = new List<(Label Label, bool[] Modules)>(labels.Count * copies);
        foreach (var label in labels)
        {
            var modules = EncodeForLabel(label.Barcode, widthMm);
            for (var i = 0; i < copies; i++)
                queue.Add((label, modules));
        }

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
            // Belge public ve ham PrintDocument dönüyor: bir PrintPreviewDialog
            // sayfayı ikinci kez isteyebilir. Kardeş LabelPrintDocument'teki
            // aynı koruma.
            if (index >= queue.Count)
            {
                e.HasMorePages = false;
                return;
            }

            var (label, modules) = queue[index];
            DrawLabel(e.Graphics!, label, modules, widthMm, fontFamily);
            index++;
            e.HasMorePages = index < queue.Count;
        };
        return doc;
    }

    [SupportedOSPlatform("windows")]
    private static void DrawLabel(
        Graphics g, Label label, bool[] modules, int widthMm, string fontFamily)
    {
        // Grafik birimi milimetreye çevriliyor: bütün ölçüler etiketin
        // fiziksel boyutuyla aynı dilde olsun, dpi hesabı tek yerde kalsın.
        g.PageUnit = GraphicsUnit.Millimeter;
        // Kenar yumuşatma barkodun düşmanı: çizgi kenarındaki gri piksel
        // okuyucunun eşiğine göre bazen çizgi bazen boşluk sayılır. Varsayılana
        // güvenmeyip açıkça kapatıyoruz.
        g.SmoothingMode = SmoothingMode.None;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        var barcodeWidth = modules.Length * ModuleWidthMm;
        // Build sığmayanı zaten reddetti; burada kırpma/negatife düşme yok.
        var left = (widthMm - barcodeWidth) / 2f;

        // GraphicsUnit.Millimeter ŞART: Font'un em boyutu varsayılan olarak
        // PUNTO, ve PageUnit'i milimetreye çevirmek onu değiştirmiyor —
        // yalnız çizim koordinatlarını çeviriyor. Birimsiz "3f" 1.31 mm'lik
        // bir yazı üretiyordu; insan-okur satır 1.10 mm'ye düşüyor, yani
        // "okuyucu çalışmazsa elle yaz" güvenlik ağı yok oluyordu.
        //
        // Punto satır yüksekliğinden türetiliyor (satır payı 1 mm): düzen
        // ölçüsü büyütülünce yazı da onunla büyüsün, ikisi ayrışmasın.
        using var nameFont = new Font(fontFamily, NameLineMm - 1f, FontStyle.Bold, GraphicsUnit.Millimeter);
        using var variantFont = new Font(fontFamily, VariantLineMm - 1f, FontStyle.Regular, GraphicsUnit.Millimeter);
        using var codeFont = new Font(fontFamily, CodeLineMm, FontStyle.Regular, GraphicsUnit.Millimeter);
        using var black = new SolidBrush(Color.Black);

        var y = TopMarginMm;
        g.DrawString(
            TruncateToWidth(g, label.ProductName, nameFont, widthMm - 2f),
            nameFont, black, 1f, y);
        y += NameLineMm;

        if (label.VariantName.Length > 0)
        {
            g.DrawString(
                TruncateToWidth(g, label.VariantName, variantFont, widthMm - 2f),
                variantFont, black, 1f, y);
            y += VariantLineMm;
        }

        // Bitişik dolu modüller TEK dikdörtgen: Code128'de 2-4 modül kalınlığında
        // çizgiler olağan. Modül modül çizseydik kalın çizginin içinde sürücünün
        // yeniden rasterleştirdiği dikişler kalırdı; okuyucu o dikişi ince bir
        // boşluk sanabilir.
        var m = 0;
        while (m < modules.Length)
        {
            if (!modules[m]) { m++; continue; }

            var run = 1;
            while (m + run < modules.Length && modules[m + run]) run++;

            g.FillRectangle(
                black, left + m * ModuleWidthMm, y, run * ModuleWidthMm, BarHeightMm);
            m += run;
        }

        y += BarHeightMm + BarToCodeGapMm;

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
