namespace OrderDeck.Shared.Sales;

/// <summary>
/// Kargo ücreti ve ücretsiz kargo eşiğinin <b>tek tanımı</b> — masaüstü ve sunucu aynı kodu çağırır (gerekçe:
/// <see cref="LabelRules"/>). Metinler ve neyin sorulacağı çağırandadır.
/// </summary>
public static class ShippingRules
{
    /// <summary>Kargo ayarı etkin mi: eşik ve ücret ikisi de dolu ve &gt; 0. Yarım, sıfır ya da negatif ayar
    /// kapalıdır.</summary>
    public static bool IsEnabled(decimal? freeShippingThreshold, decimal? shippingFee)
        => freeShippingThreshold is > 0 && shippingFee is > 0;

    /// <summary>Tutar ücretsiz kargo eşiğine ulaştı mı; tam eşik ulaşmıştır. Yalnız etkin ayarın
    /// (<see cref="IsEnabled"/>) eşiğiyle sorulur.</summary>
    public static bool ReachesThreshold(decimal amount, decimal freeShippingThreshold)
        => amount >= freeShippingThreshold;

    /// <summary>Müşteriden istenecek kargo: alıcı ödemeliyse, ayar kapalıysa ya da ürün toplamı eşik ve üstündeyse 0;
    /// değilse ücret.</summary>
    public static decimal FeeToRequest(
        decimal? freeShippingThreshold, decimal? shippingFee, decimal productTotal, bool recipientPays)
    {
        if (recipientPays || !IsEnabled(freeShippingThreshold, shippingFee)) return 0m;
        return ReachesThreshold(productTotal, freeShippingThreshold!.Value) ? 0m : shippingFee!.Value;
    }
}
