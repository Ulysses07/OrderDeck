using System.Linq;

namespace OrderDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Phase 4g: TR mobil telefonu E.164 formatına normalize eder.
/// Port from OrderDeck.Core.Customers.PhoneNormalizer (cross-project shared lib YAGNI).
/// </summary>
public static class PhoneNormalizer
{
    public static string? NormalizeTr(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var digits = new string(input.Where(char.IsDigit).ToArray());
        if (digits.Length == 12 && digits.StartsWith("90")) return "+" + digits;
        if (digits.Length == 11 && digits.StartsWith("0")) return "+90" + digits.Substring(1);
        if (digits.Length == 10) return "+90" + digits;
        return null;
    }

    public static bool IsValidTr(string? e164)
        => !string.IsNullOrEmpty(e164)
           && e164.StartsWith("+90")
           && e164.Length == 13
           && e164.Substring(1).All(char.IsDigit);
}
