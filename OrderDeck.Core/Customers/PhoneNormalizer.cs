using System.Linq;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Phase 4g: TR mobil telefon numaralarını E.164 (+90...) formatına normalize eder.
/// Pure function — no side effects.
/// </summary>
public static class PhoneNormalizer
{
    /// <summary>
    /// "5551234567" / "05551234567" / "+90 555 123 45 67" → "+905551234567".
    /// Geçersiz/null/empty/yurt-dışı/mobil-olmayan → null.
    /// </summary>
    public static string? NormalizeTr(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var digits = new string(input.Where(char.IsDigit).ToArray());

        // Önce 10 haneli abone numarasını ayıkla, sonra TEK yerde doğrula.
        string? subscriber = null;
        if (digits.Length == 12 && digits.StartsWith("90")) subscriber = digits.Substring(2);
        else if (digits.Length == 11 && digits.StartsWith("0")) subscriber = digits.Substring(1);
        else if (digits.Length == 10) subscriber = digits;

        // TR mobil abone numarası daima 5 ile başlar. Bu kural olmadan
        // "0533466482" (9 hane + baştaki 0) 10 hane sayılıp "+900533466482"
        // üretiyordu; prod'da böyle bir kayıt var ve İYS'de geçersiz anahtar.
        if (subscriber is null || subscriber[0] != '5') return null;

        return "+90" + subscriber;
    }

    /// <summary>E.164 TR mobil kontrolü: "+90" + 10 digit, abone "5" ile başlar.</summary>
    public static bool IsValidTr(string? e164)
        => !string.IsNullOrEmpty(e164)
           && e164.StartsWith("+90")
           && e164.Length == 13
           && e164[3] == '5'
           && e164.Substring(1).All(char.IsDigit);
}
