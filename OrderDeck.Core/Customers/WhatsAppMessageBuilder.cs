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
        //
        // E3b bakiye placeholder'ları (2026-06):
        //   {bakiye}      — bu mesajda düşülen bakiye (örn. "100,00")
        //                   AppliedBalance == 0 ise boş string
        //   {net_tutar}   — bakiye düşüldükten sonra ödenecek tutar (== TotalAmount)
        //   {toplam_oncesi}— bakiye düşülmeden önceki toplam (TotalBeforeBalance)
        // Bu placeholder'lar geriye uyumlu — eski template'lerde yer almıyorsa
        // sessiz geçer.
        var bakiyeText = ctx.AppliedBalance > 0
            ? ctx.AppliedBalance.ToString("N2", Tr)
            : "";
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
            .Replace("{kargo}", ctx.ShippingNote)
            .Replace("{bakiye}", bakiyeText)
            .Replace("{net_tutar}", ctx.TotalAmount.ToString("N2", Tr))
            .Replace("{toplam_oncesi}",
                (ctx.TotalBeforeBalance > 0 ? ctx.TotalBeforeBalance : ctx.TotalAmount)
                .ToString("N2", Tr));
    }

    /// <summary>
    /// Kümülatif kargo PR-E (2026-05-12): ücretsiz kargo eşiği aşıldı
    /// "tebrikler" şablonu için placeholder substitusyonu.
    /// Placeholder: {ad}, {kumulatif_tutar}, {tarih}.
    /// </summary>
    public string BuildShippingWonMessage(string template, string displayName, decimal cumulativeAmount)
    {
        return template
            .Replace("{ad}", displayName)
            .Replace("{kumulatif_tutar}", cumulativeAmount.ToString("N2", Tr))
            .Replace("{tarih}", DateTime.Now.ToString("dd MMMM yyyy", Tr));
    }

    /// <summary>"+905551234567" + "Hello" → "https://wa.me/905551234567?text=Hello".</summary>
    public string BuildWaMeLink(string e164Phone, string message)
    {
        var phone = e164Phone.TrimStart('+');
        return $"https://wa.me/{phone}?text={Uri.EscapeDataString(message)}";
    }
}
