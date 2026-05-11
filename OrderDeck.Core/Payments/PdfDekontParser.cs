using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace OrderDeck.Core.Payments;

/// <summary>
/// Banka dekontu PDF'lerinden ödeyen / tutar / referans no / tarih
/// alanlarını best-effort olarak çıkarır. Operatör DekontEkleDialog'da
/// formu pre-fill olarak görür, gerekirse düzeltir. Türkiye'deki belli
/// başlı bankaların (Ziraat, Halkbank, Vakıfbank, Garanti, Yapı Kredi,
/// Akbank, İş, QNB, DenizBank, TEB, Papara, Enpara) ortak Türkçe
/// alanlarına dayanan heuristik regex'lerle çalışır.
///
/// PDF içeriği sunucuya gitmez — yerel parse + hash (PdfHash field için).
/// PDF dosyası kalıcı saklanmaz; parse sonrası operatör kaydedince ham
/// PDF bellekten düşer. KVKK için uygun davranış (memory'deki kararla
/// uyumlu: PDF retention 0, yalnız metadata persist).
/// </summary>
public sealed class PdfDekontParser
{
    /// <summary>Sonuç. Her alan null olabilir — parser bulamadığında
    /// UI alanı boş bırakır, operatör elle doldurur.</summary>
    public sealed record ParseResult(
        string? PayerName,
        decimal? Amount,
        DateTime? PaidAt,
        string? ReferansNo,
        string PdfHash,
        string RawText);

    public ParseResult Parse(byte[] pdfBytes)
    {
        var text = ExtractText(pdfBytes);
        var hash = ComputeSha256(pdfBytes);
        return ParseFromText(text, hash);
    }

    /// <summary>Text already extracted (testler için). Production'da
    /// <see cref="Parse(byte[])"/> kullan — bu sadece extractor logic'i
    /// test edebilmek için public.</summary>
    public ParseResult ParseFromText(string text, string hash) => new(
        PayerName: ExtractPayerName(text),
        Amount: ExtractAmount(text),
        PaidAt: ExtractPaidAt(text),
        ReferansNo: ExtractReferansNo(text),
        PdfHash: hash,
        RawText: text);

    private static string ExtractText(byte[] pdfBytes)
    {
        using var stream = new MemoryStream(pdfBytes);
        using var document = PdfDocument.Open(stream);
        var sb = new StringBuilder();
        foreach (var page in document.GetPages())
        {
            sb.AppendLine(page.Text);
        }
        return sb.ToString();
    }

    private static string ComputeSha256(byte[] bytes)
    {
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    // ── Field extractors ────────────────────────────────────────────────

    /// <summary>Türkçe "Gönderen", "Ad Soyad", "Hesap Sahibi" gibi etiketler
    /// sonrası ad soyad'ı yakalar. Banka format farklarını tolere etmek için
    /// birkaç desen denenir, ilk eşleşen kullanılır.</summary>
    private static string? ExtractPayerName(string text)
    {
        var patterns = new[]
        {
            @"G[öo]nderen\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü\s\.]+?)(?:\s*(?:IBAN|Hesap|TC|TR\d|Tarih|Tutar)|\r?\n)",
            @"Ad\s+Soyad[ıi]?\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü\s\.]+?)(?:\s*(?:IBAN|Hesap|TC|TR\d|Tarih|Tutar)|\r?\n)",
            @"Hesap\s+Sahibi\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü\s\.]+?)(?:\s*(?:IBAN|TC|TR\d|Tarih|Tutar)|\r?\n)",
            @"G[öo]nd(?:eren|erici)[:\s]+([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü\s\.]+?)(?:\s*(?:IBAN|Hesap|TC|TR\d|Tarih|Tutar)|\r?\n)"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                // Süslü filterler: ardışık boşluk, tek harfli noktalama vs.
                name = Regex.Replace(name, @"\s+", " ");
                if (name.Length >= 3 && name.Length <= 100) return name;
            }
        }
        return null;
    }

    /// <summary>Tutarı "Tutar:" / "Miktar:" / "Tutar (TL):" etiketleri altından
    /// veya en büyük "X,YY TL" pattern'i olarak çekiyor. Türkçe ondalık
    /// ayraç virgül + binlik nokta.</summary>
    private static decimal? ExtractAmount(string text)
    {
        // Önce explicit label'lar
        var labeledPatterns = new[]
        {
            @"Tutar\s*\(?\s*(?:TL|TRY)?\s*\)?\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"Miktar\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"\u0130[şs]lem\s+Tutar[ıi]?\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"Gönder(?:ilen|ime)?\s*Tutar[ıi]?\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"Havale\s*Tutar[ıi]?\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?"
        };

        foreach (var pattern in labeledPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var raw = match.Groups[1].Value;
                if (TryParseTurkishDecimal(raw, out var amount) && amount > 0) return amount;
            }
        }

        // Fallback: belge içindeki en büyük "X,YY TL" değeri (genelde tutar).
        // Yanlış pozitif riski var (IBAN parçası vb.) ama explicit label
        // bulunamadıysa son çare.
        var fallback = Regex.Matches(text, @"([\d\.]{1,12},\d{2})\s*(?:TL|TRY)\b")
            .Select(m => TryParseTurkishDecimal(m.Groups[1].Value, out var d) ? d : 0m)
            .Where(d => d > 0)
            .OrderByDescending(d => d)
            .FirstOrDefault();
        return fallback > 0 ? fallback : null;
    }

    private static bool TryParseTurkishDecimal(string raw, out decimal value)
    {
        // "1.234,56" → invariant "1234.56"
        var normalized = raw.Trim().Replace(".", "").Replace(",", ".");
        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Türkçe banka dekontlarında tarih genelde "DD.MM.YYYY HH:MM"
    /// veya "DD/MM/YYYY" şeklinde. Label varsa o, yoksa en eski tarih.</summary>
    private static DateTime? ExtractPaidAt(string text)
    {
        var labeledPatterns = new[]
        {
            @"(?:\u0130[şs]lem\s+)?Tarih(?:i)?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{2,4})(?:\s+(\d{1,2}:\d{2}(?::\d{2})?))?",
            @"Valor\s*Tarihi?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{2,4})",
            @"\u0130[şs]lem\s+Zaman[ıi]?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{2,4})"
        };

        foreach (var pattern in labeledPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success && TryParseTurkishDate(match.Groups[1].Value, out var date))
            {
                return date;
            }
        }

        // Fallback: belgedeki ilk tarih
        var anyDate = Regex.Match(text, @"\b(\d{1,2}[\./]\d{1,2}[\./]\d{2,4})\b");
        if (anyDate.Success && TryParseTurkishDate(anyDate.Groups[1].Value, out var fallback))
        {
            return fallback;
        }
        return null;
    }

    private static bool TryParseTurkishDate(string raw, out DateTime date)
    {
        var formats = new[] { "dd.MM.yyyy", "d.M.yyyy", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yy", "d.M.yy", "dd/MM/yy", "d/M/yy" };
        return DateTime.TryParseExact(raw.Trim(), formats,
            CultureInfo.InvariantCulture, DateTimeStyles.None, out date);
    }

    /// <summary>"Referans No", "İşlem No", "Dekont No" etiketleri altından
    /// 6-20 karakter arası rakam/harf karışımı.</summary>
    private static string? ExtractReferansNo(string text)
    {
        var patterns = new[]
        {
            @"Referans\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9\-]{6,32})",
            @"\u0130[şs]lem\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9\-]{6,32})",
            @"Dekont\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9\-]{6,32})",
            @"Transfer\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9\-]{6,32})",
            @"Onay\s+(?:No|Kodu|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9\-]{6,32})"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length >= 6) return value;
            }
        }
        return null;
    }
}
