using OrderDeck.PdfParsing;

namespace OrderDeck.LicenseServer.Services.ShopperPayments;

/// <summary>
/// PdfDekontParser sonucundan parser güven seviyesi türetir. 5 anahtar alana
/// bakar: PayerName, Amount, PaidAt, ReferansNo, RecipientIban. Skor:
///   ≥ 4 → "High"
///   2-3 → "Medium"
///   0-1 → "Low"
/// Payment.ParserConfidence kolonu max 16 char olduğu için kısa string.
/// </summary>
public static class ParserConfidenceCalculator
{
    public static string Compute(PdfDekontParser.ParseResult result)
    {
        int score = 0;
        if (!string.IsNullOrWhiteSpace(result.PayerName)) score++;
        if (result.Amount.HasValue) score++;
        if (result.PaidAt.HasValue) score++;
        if (!string.IsNullOrWhiteSpace(result.ReferansNo)) score++;
        if (!string.IsNullOrWhiteSpace(result.RecipientIban)) score++;

        return score >= 4 ? "High" : score >= 2 ? "Medium" : "Low";
    }
}
