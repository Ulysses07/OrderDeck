using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// TR telefon numarası normalize edici. Çıktı: E.164 format `+90XXXXXXXXXX`.
/// Boşluk, tire, parantez temizlenir. Prefix değişimleri:
///   0XXXXXXXXXX  → +90XXXXXXXXXX
///   90XXXXXXXXXX → +90XXXXXXXXXX
///   XXXXXXXXXX   → +90XXXXXXXXXX (10 hane gönderilirse)
/// Diğer ülke kodları (örn. +1) reddedilir.
/// </summary>
public static class PhoneNormalizer
{
    // Allowed chars: digits, +, spaces, hyphens, parentheses
    private static readonly Regex ValidChars = new(@"^[\d\s\-\+\(\)]+$", RegexOptions.Compiled);
    private static readonly Regex StripNonDigit = new(@"[^\d]", RegexOptions.Compiled);
    private static readonly Regex StripNonDigitOrPlus = new(@"[^\d+]", RegexOptions.Compiled);

    public static string Normalize(string raw)
    {
        if (TryNormalize(raw, out var result))
            return result!;
        throw new ArgumentException("Geçersiz TR telefon numarası.", nameof(raw));
    }

    public static bool TryNormalize(string? raw, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Reject input containing letters or other non-phone characters
        if (!ValidChars.IsMatch(raw)) return false;

        var cleaned = StripNonDigitOrPlus.Replace(raw, "");

        if (cleaned.StartsWith("+"))
        {
            if (!cleaned.StartsWith("+90")) return false;
            cleaned = cleaned[3..];
        }
        else if (cleaned.StartsWith("90") && cleaned.Length == 12)
        {
            cleaned = cleaned[2..];
        }
        else if (cleaned.StartsWith("0") && cleaned.Length == 11)
        {
            cleaned = cleaned[1..];
        }

        if (cleaned.Length != 10) return false;
        if (!cleaned.All(char.IsDigit)) return false;

        result = "+90" + cleaned;
        return true;
    }
}
