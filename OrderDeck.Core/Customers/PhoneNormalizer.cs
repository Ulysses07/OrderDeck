using System.Linq;

namespace LiveDeck.Core.Customers;

/// <summary>
/// Phase 4g: TR mobil telefon numaralarını E.164 (+90...) formatına normalize eder.
/// Pure function — no side effects.
/// </summary>
public static class PhoneNormalizer
{
    /// <summary>
    /// "5551234567" / "05551234567" / "+90 555 123 45 67" → "+905551234567".
    /// Geçersiz/null/empty/yurt-dışı → null.
    /// </summary>
    public static string? NormalizeTr(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var digits = new string(input.Where(char.IsDigit).ToArray());

        // 12 digits starting with 90 → already has TR prefix
        if (digits.Length == 12 && digits.StartsWith("90"))
            return "+" + digits;

        // 11 digits starting with 0 → drop leading 0, prepend +90
        if (digits.Length == 11 && digits.StartsWith("0"))
            return "+90" + digits.Substring(1);

        // 10 digits → prepend +90
        if (digits.Length == 10)
            return "+90" + digits;

        return null;
    }

    /// <summary>E.164 TR format kontrolü: "+90" + 10 digit (toplam 13 karakter).</summary>
    public static bool IsValidTr(string? e164)
        => !string.IsNullOrEmpty(e164)
           && e164.StartsWith("+90")
           && e164.Length == 13
           && e164.Substring(1).All(char.IsDigit);
}
