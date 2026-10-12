using System;
using System.Globalization;
using System.Linq;
using System.Text;

namespace OrderDeck.Core.Sales;

/// <summary><see cref="PriceInput.Parse"/> sonucunun türü.</summary>
public enum PriceInputStatus
{
    Ok,

    /// <summary>Kutu boş (ya da yalnız boşluk / ₺ / TL).</summary>
    Empty,

    /// <summary>Sayı olarak okunamadı ya da ayraçlar belirsiz ("1.2345", "1,50.00").</summary>
    Invalid,

    /// <summary>Eksi fiyat — satış etiketi eksi olamaz.</summary>
    Negative,
}

/// <summary>Fiyat kutusunun okunmuş hâli. <see cref="Value"/> yalnız <see cref="PriceInputStatus.Ok"/>'ta
/// anlamlı; 0 TL geçerli bir okumadır, onayı çağıranın işidir.</summary>
public readonly record struct PriceInputResult(PriceInputStatus Status, decimal Value)
{
    public bool IsOk => Status == PriceInputStatus.Ok;
}

/// <summary>
/// Operatörün yazdığı fiyatın TEK okuyucusu (FİYAT kutusu, yedek onayı, fiyat düzeltme).
///
/// <para><b>Neden ayrı:</b> eski okuma önce kültürden bağımsız biçimi deniyordu ve
/// <c>NumberStyles.Any</c> ayraçları "binlik" sayabiliyordu: "99,50" → 9950 TL, "1.500" → 1,5 TL.
/// Yedek onayı ise önce Türkçe biçimi deniyordu; aynı metin iki ekranda iki farklı fiyattı.</para>
///
/// <para><b>Kural:</b> boşluk, ₺ ve "TL" atılır. Hem nokta hem virgül varsa SONUNCUSU ondalıktır,
/// öbürü binlik (ondalıktan önce, 3'lü gruplar). Tek tür ayraç varsa: ardından tam 3 hane
/// (ve tekrarlayan 3'lü gruplar) → binlik; 1–2 hane → ondalık. Başka her şekil belirsizdir ve
/// reddedilir — tahmin edilen fiyat, yanlış fiyattan daha kötü değildir ama sessizdir. Kuruş en çok
/// iki hanedir. Eksi fiyat reddedilir (<see cref="PriceInputStatus.Negative"/>).</para>
/// </summary>
public static class PriceInput
{
    private static readonly CultureInfo Tr = CultureInfo.GetCultureInfo("tr-TR");

    public static PriceInputResult Parse(string? text)
    {
        if (text is null) return new(PriceInputStatus.Empty, 0m);

        var cleaned = Clean(text);
        if (cleaned.Length == 0) return new(PriceInputStatus.Empty, 0m);

        var negative = false;
        if (cleaned[0] is '-' or '−')
        {
            negative = true;
            cleaned = cleaned[1..];
            if (cleaned.Length == 0) return new(PriceInputStatus.Invalid, 0m);
        }

        if (!TryParseUnsigned(cleaned, out var value)) return new(PriceInputStatus.Invalid, 0m);
        if (negative && value > 0m) return new(PriceInputStatus.Negative, -value);
        return new(PriceInputStatus.Ok, value);
    }

    /// <summary>Kutuya geri yazılacak biçim: binlik ayraçsız, kuruş varsa virgülle ("1500", "99,5").
    /// <see cref="Parse"/> bu biçimi her zaman aynı değere okur.</summary>
    public static string Format(decimal value) => value.ToString("0.##", Tr);

    /// <summary>Boşluk (bölünmez boşluklar dahil), ₺ ve "TL" (harf duyarsız) atılır.</summary>
    private static string Clean(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (char.IsWhiteSpace(ch) || ch == '₺') continue;
            sb.Append(ch);
        }
        var s = sb.ToString();
        var idx = s.IndexOf("TL", StringComparison.OrdinalIgnoreCase);
        while (idx >= 0)
        {
            s = s.Remove(idx, 2);
            idx = s.IndexOf("TL", StringComparison.OrdinalIgnoreCase);
        }
        return s;
    }

    private static bool TryParseUnsigned(string s, out decimal value)
    {
        value = 0m;
        if (s.Any(c => !char.IsAsciiDigit(c) && c != '.' && c != ',')) return false;

        var lastDot = s.LastIndexOf('.');
        var lastComma = s.LastIndexOf(',');

        string integerPart;
        string fraction;
        if (lastDot >= 0 && lastComma >= 0)
        {
            // İki tür ayraç: sonuncusu ondalık, tek kez geçer; öbürü yalnız ondan önce binlik.
            var decimalSep = lastDot > lastComma ? '.' : ',';
            var thousandsSep = decimalSep == '.' ? ',' : '.';
            var decimalAt = Math.Max(lastDot, lastComma);
            if (s.Count(c => c == decimalSep) != 1) return false;

            fraction = s[(decimalAt + 1)..];
            if (!IsFraction(fraction)) return false;
            if (!TryGroups(s[..decimalAt], thousandsSep, out integerPart)) return false;
        }
        else if (lastDot >= 0 || lastComma >= 0)
        {
            var sep = lastDot >= 0 ? '.' : ',';
            var parts = s.Split(sep);
            if (parts.Length == 2 && parts[1].Length is 1 or 2)
            {
                // Tek ayraç + 1–2 hane → ondalık.
                if (parts[0].Length == 0) return false;
                integerPart = parts[0];
                fraction = parts[1];
            }
            else
            {
                // Ardından 3'lü gruplar → binlik; başka her şekil belirsiz.
                if (!TryGroups(s, sep, out integerPart)) return false;
                fraction = "";
            }
        }
        else
        {
            integerPart = s;
            fraction = "";
        }

        if (integerPart.Length == 0) return false;
        var normalized = fraction.Length == 0 ? integerPart : integerPart + "." + fraction;
        return decimal.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);
    }

    private static bool IsFraction(string f) => f.Length is 1 or 2 && f.All(char.IsAsciiDigit);

    /// <summary>"1.500.000" biçimi: ilk grup 1–3 hane, sonrakiler tam 3 hane. Ayraçsız sayı da kabul
    /// (iki tür ayraçlı metnin tam kısmı ayraçsız olabilir: "1500,50").</summary>
    private static bool TryGroups(string s, char sep, out string digits)
    {
        digits = "";
        var groups = s.Split(sep);
        if (groups.Length == 1)
        {
            digits = groups[0];
            return digits.Length > 0 && digits.All(char.IsAsciiDigit);
        }
        if (groups[0].Length is < 1 or > 3) return false;
        for (var i = 1; i < groups.Length; i++)
            if (groups[i].Length != 3) return false;
        digits = string.Concat(groups);
        return digits.All(char.IsAsciiDigit);
    }
}
