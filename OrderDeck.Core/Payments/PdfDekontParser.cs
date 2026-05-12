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
        string RawText,
        string? RecipientIban = null);   // 2026-05-12: alıcı IBAN — Settings.Iban ile karşılaştırma için

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
        RawText: text,
        RecipientIban: ExtractRecipientIban(text));

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
    /// birkaç desen denenir, ilk eşleşen kullanılır.
    /// PDF text'i tek satır olabilir (newline'sız), o yüzden end delimiter
    /// olarak diğer field label'ları + IBAN/TR##/AÇIKLAMA kullanılır.</summary>
    internal static string? ExtractPayerName(string text)
    {
        // PDF tek satır olabilir; end-delimiter olarak bir sonraki alan/keyword
        // kullanılır. Tüm büyük harf + Türkçe karakterler + 5+ kelime uzun
        // şirket adları tolere edilir (örn. "EMAR GLOBAL TEKSTİL GIDA İNŞAAT
        // TURİZM YAZILIM VE TİCARET").
        var patterns = new[]
        {
            // Türkiye Finans format: "GÖNDERENİsim              : HARUN CEYLAN"
            // veya "GÖNDEREN ... İsim : NAME". GÖNDEREN section başlığı sonrası
            // ilk "İsim" sub-label'ı.
            @"G[ÖöO]NDEREN[^A-Za-zÇĞİıÖŞÜçğıöşü]{0,40}?[İıI]sim\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:ALICI|AÇIKLAMA|IBAN|Tutar|Para|Karşı|VKN|Vergi|TC[:\s]|$))",
            @"G[ÖöO]NDEREN\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:IBAN|TR\d{2}|AÇIKLAMA|ALICI|Hesap|TC[:\s]|Tarih|Tutar|Para|VKN|Sorgu|Fi[şs]|EFT|KATILIMCI|$))",
            @"M[ÜüU][şS]TER[İıI]\s+[ÜüU]NVANI?\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:IBAN|TR\d{2}|AÇIKLAMA|ALICI|$))",
            @"G[öo]nderen\s*[:\-]\s*([A-Za-zÇĞİıÖŞÜçğıöşü][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:IBAN|TR\d{2}|AÇIKLAMA|Hesap|TC[:\s]|Tarih|Tutar|Para|VKN|Sorgu|Fi[şs]|$))",
            @"Ad\s+Soyad[ıi]?\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:IBAN|TR\d{2}|Hesap|TC[:\s]|Tarih|Tutar|$))",
            @"Hesap\s+Sahibi\s*[:\-]\s*([A-ZÇĞİÖŞÜ][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:IBAN|TR\d{2}|TC[:\s]|Tarih|Tutar|$))",
            @"G[öo]nd(?:eren|erici)[:\s]+([A-Za-zÇĞİıÖŞÜçğıöşü][A-Za-zÇĞİıÖŞÜçğıöşü0-9\.\s/]+?)(?=\s*(?:IBAN|TR\d{2}|AÇIKLAMA|Hesap|TC[:\s]|Tarih|Tutar|$))"
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var name = match.Groups[1].Value.Trim();
                // Trailing kırpılma: bazı PDF'lerde "TİCARET Lİ" gibi
                // ortadan kesilmiş bir kelime kalır — son 1-2 harfli kelimeyi
                // korurken " Lİ" / " Ş" trail'lerini atma (anlamlı ekler).
                name = Regex.Replace(name, @"\s+", " ");
                if (name.Length >= 3 && name.Length <= 200) return name;
            }
        }
        return null;
    }

    /// <summary>Tutarı label'lar altından veya en büyük "X.YY TL" pattern'i
    /// olarak çekiyor. Hem TR format (1.234,56) hem US format (1,234.56)
    /// kabul edilir — bazı bankalar (örn. QNB) US format kullanıyor.</summary>
    internal static decimal? ExtractAmount(string text)
    {
        // Explicit label'lar (uppercase variants öncelikli — yeni bankalar
        // hep all-caps label kullanıyor: "EFT TUTARI", "İŞLEM TUTARI").
        var labeledPatterns = new[]
        {
            @"EFT\s+TUTAR[IıİiI]\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"\u0130[şS]LEM\s+TUTAR[IıİiI]\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"HAVALE\s+TUTAR[IıİiI]\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
            @"TUTAR\s*\(?\s*(?:TL|TRY)?\s*\)?\s*[:\-]\s*([\d\.,]+)\s*(?:TL|TRY)?",
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
                if (TryParseLocaleDecimal(raw, out var amount) && amount > 0) return amount;
            }
        }

        // Fallback: belge içindeki en büyük "X,YY TL" veya "X.YY TL" değeri.
        // Yanlış pozitif riski var (IBAN parçası vb.) ama explicit label
        // bulunamadıysa son çare.
        var fallback = Regex.Matches(text,
                @"([\d][\d\.,]{0,15}[\.,]\d{2})\s*(?:TL|TRY)\b")
            .Select(m => TryParseLocaleDecimal(m.Groups[1].Value, out var d) ? d : 0m)
            .Where(d => d > 0)
            .OrderByDescending(d => d)
            .FirstOrDefault();
        return fallback > 0 ? fallback : null;
    }

    /// <summary>Hem TR (1.234,56) hem US (1,234.56) format'larını parse eder.
    /// Heuristik: son ayraç (nokta veya virgül) ondalık kabul edilir, diğeri
    /// binlik separator olarak çıkarılır.</summary>
    private static bool TryParseLocaleDecimal(string raw, out decimal value)
    {
        raw = raw.Trim();
        var lastComma = raw.LastIndexOf(',');
        var lastDot = raw.LastIndexOf('.');

        string normalized;
        if (lastComma > lastDot)
        {
            // TR: nokta binlik, virgül ondalık → noktayı sil, virgülü noktaya
            normalized = raw.Replace(".", "").Replace(",", ".");
        }
        else if (lastDot > lastComma)
        {
            // US: virgül binlik, nokta ondalık → virgülü sil
            normalized = raw.Replace(",", "");
        }
        else
        {
            // Hiçbir ayraç yok veya tek bir karakter → olduğu gibi parse dene
            normalized = raw.Replace(",", ".");
        }

        return decimal.TryParse(normalized, NumberStyles.Number, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>Türkçe banka dekontlarında tarih genelde "DD.MM.YYYY HH:MM"
    /// veya "DD/MM/YYYY" şeklinde. Label varsa o, yoksa en eski tarih.
    /// Yıl boundary'si lookahead ile koruluyor (`(?!\d)`) çünkü PDF tek satır
    /// olunca yıl sonrası başka digit'lar gelir ve `\b` yeterli olmuyor.</summary>
    internal static DateTime? ExtractPaidAt(string text)
    {
        var labeledPatterns = new[]
        {
            @"(?:\u0130[şS]LEM\s+)?TAR[İıI]H[İıI]?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{4})",
            @"(?:\u0130[şs]lem\s+)?Tarih(?:i)?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{4})",
            @"Valor\s*Tarihi?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{4})",
            @"\u0130[şs]lem\s+Zaman[ıi]?\s*[:\-]\s*(\d{1,2}[\./]\d{1,2}[\./]\d{4})"
        };

        foreach (var pattern in labeledPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success && TryParseTurkishDate(match.Groups[1].Value, out var date))
            {
                return date;
            }
        }

        // Fallback: belgedeki ilk tarih. Yıl 4-digit fixed (range yerine) ve
        // word-boundary kaldırıldı. PDF tek satırında "2026404..." gibi
        // run-on'larda eski `\d{2,4}` + `\b` kombinasyonu fail ediyordu —
        // greedy backtrack hiçbir yıl tamamlamıyordu çünkü digit→digit
        // transition'da `\b` yok. Fixed 4-digit non-greedy bunu çözüyor.
        var anyDate = Regex.Match(text, @"(\d{1,2}[\./]\d{1,2}[\./]\d{4})");
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

    /// <summary>Banka referans no etiketleri — "Referans No", "İşlem No",
    /// "Dekont No", "Sorgu No", "Fiş No", "Onay No". Türk bankalarının
    /// büyük çoğunluğunda ref no pure numeric — alfanumerik versiyonu da
    /// fallback olarak (örn. Garanti'nin "GTI..." prefix'i).</summary>
    internal static string? ExtractReferansNo(string text)
    {
        // 1. Pass: dash-separated alphanumeric en spesifik (Türkiye Finans:
        // "20260508-99-XOGKX"). Numeric prefix + en az 1 dash. Numeric-only
        // pass'tan önce çalıştırılır çünkü dash'siz pattern bu format'ı
        // numeric kısmıyla yutardı.
        // Bilinen sonraki bölüm label'larından önce dur — PDF tek satırda
        // "20260508-99-XOGKXGÖNDEREN" gibi run-on'larda yumuşak boundary.
        const string RefNoStop = @"(?=G[ÖÖO]NDEREN|ALICI|[İıI]SIM|M[ÜÜU][şS]TER[İıI]|TUTAR|IBAN|AÇIKLAMA|VKN|Vergi|Düzenleme|Tarih|$)";
        var dashAlphanumericPatterns = new[]
        {
            @"Referans\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9]+(?:\-[A-Z0-9]+){1,4})" + RefNoStop,
            @"\u0130[şs]lem\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9]+(?:\-[A-Z0-9]+){1,4})" + RefNoStop,
            @"Onay\s+(?:No|Kodu|Numaras[ıi])\s*[:\-]?\s*([A-Z0-9]+(?:\-[A-Z0-9]+){1,4})" + RefNoStop
        };

        foreach (var pattern in dashAlphanumericPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                // En az 1 dash içermeli — pure numeric 2. pass'a düşsün
                if (value.Length >= 6 && value.Contains('-')) return value;
            }
        }

        // 2. Pass: numeric-only (PDF tek satır olunca "1503468325MÜŞTERİ"
        // gibi run-on'larda alfanumerik pattern label'ları yutabiliyor).
        var numericPatterns = new[]
        {
            @"Referans\s+(?:No|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})",
            @"\u0130[şs]lem\s+(?:No|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})",
            @"Dekont\s+(?:No|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})",
            @"Transfer\s+(?:No|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})",
            @"Onay\s+(?:No|Kodu|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})",
            @"Sorgu\s+(?:No|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})",
            @"Fi[şsŞS]\s+(?:No|Numaras[ıi])\s*[:\-]?\s*(\d{6,32})"
        };

        foreach (var pattern in numericPatterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                var value = match.Groups[1].Value.Trim();
                if (value.Length >= 6) return value;
            }
        }

        // 3. Pass: alfanumerik prefix (Garanti GTI..., Akbank AKB...).
        var alphanumericPatterns = new[]
        {
            @"Referans\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z]{2,5}\d{4,28})",
            @"\u0130[şs]lem\s+(?:No|Numaras[ıi])\s*[:\-]?\s*([A-Z]{2,5}\d{4,28})",
            @"Onay\s+(?:No|Kodu|Numaras[ıi])\s*[:\-]?\s*([A-Z]{2,5}\d{4,28})"
        };

        foreach (var pattern in alphanumericPatterns)
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

    /// <summary>
    /// 2026-05-12: Alıcı IBAN'ını yakalar. Vendor'un Settings.Payment.Iban'ı
    /// ile karşılaştırma yaparak müşterinin yanlış hesaba transfer yapıp
    /// yapmadığını kontrol etmek için kullanılır. Türkiye Finans, QNB ve
    /// genel bankacılık format'ları: "ALICI IBAN: TR...", "Alıcı IBAN: TR...",
    /// "IBAN/Hesap No : TR..." (ALICI section altında).
    /// </summary>
    internal static string? ExtractRecipientIban(string text)
    {
        // ALICI section başlangıcından sonraki ilk TR-IBAN'ı yakala.
        // Lazy `.*?` ile section içindeki text'i atla, IBAN'a kadar git.
        // Singleline mode gerek değil (text zaten tek satır PDF'lerden gelir).
        var patterns = new[]
        {
            // Türkiye Finans: "ALICI ... IBAN/Hesap No : TR..."
            // QNB: "ALICI IBAN: TR..."
            // Generic ALICI section + ilk IBAN
            @"ALICI.{0,200}?IBAN(?:/Hesap\s*No)?\s*[:\-]\s*(TR\d{2}[\s\d]{20,30})",
            // Title-case Alıcı + section ile IBAN
            @"Al[ıi]c[ıi]\s*[:\-].{0,200}?IBAN(?:/Hesap\s*No)?\s*[:\-]\s*(TR\d{2}[\s\d]{20,30})",
            // Inline "Alıcı : NAME ... IBAN: TR..."
            @"Al[ıi]c[ıi]\s*[:\-].{0,200}?\b(TR\d{2}[\s\d]{20,30})",
        };

        foreach (var pattern in patterns)
        {
            var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            if (match.Success)
            {
                // Whitespace çıkar, normalize et
                var iban = System.Text.RegularExpressions.Regex.Replace(
                    match.Groups[1].Value, @"\s+", "");
                if (iban.Length == 26) return iban;
            }
        }
        return null;
    }
}
