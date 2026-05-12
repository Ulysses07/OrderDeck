using System;
using System.Globalization;

namespace OrderDeck.Core.Customers;

/// <summary>
/// Phase 4g: Settings template'ini PaymentContext ile substitute eder
/// ve wa.me deep-link inşa eder. TR culture decimal/tarih formatlama.
/// </summary>
public sealed class WhatsAppMessageBuilder
{
    private static readonly CultureInfo Tr = new("tr-TR");

    public string BuildMessage(string template, PaymentContext ctx)
    {
        // Kargo placeholder'ları (2026-05-12): {urun_toplami}, {kargo_ucreti},
        // {kargo}. Eski template'ler bu placeholder'ları içermez — sessiz geçer.
        return template
            .Replace("{ad}", ctx.DisplayName)
            .Replace("{tutar}", ctx.TotalAmount.ToString("N2", Tr))
            .Replace("{tarih}", ctx.StreamDate.ToString("dd MMMM yyyy", Tr))
            .Replace("{iban}", ctx.Iban ?? "")
            .Replace("{hesap_sahibi}", ctx.AccountHolder ?? "")
            .Replace("{papara}", ctx.Papara ?? "")
            .Replace("{urun_toplami}", ctx.ProductTotal.ToString("N2", Tr))
            .Replace("{kargo_ucreti}",
                ctx.ShippingFee.HasValue ? ctx.ShippingFee.Value.ToString("N2", Tr) : "—")
            .Replace("{kargo}", ctx.ShippingNote);
    }

    /// <summary>"+905551234567" + "Hello" → "https://wa.me/905551234567?text=Hello".</summary>
    public string BuildWaMeLink(string e164Phone, string message)
    {
        var phone = e164Phone.TrimStart('+');
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }
}
