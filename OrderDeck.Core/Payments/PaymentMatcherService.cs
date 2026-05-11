using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Payments;

/// <summary>
/// Kargo PR C: dekont tutarını müşterinin ürün toplamı + kargo ücreti
/// beklentisi ile karşılaştırır. Sonuç vendor'a ShipmentDirectiveDialog
/// üzerinden sunulur.
///
/// Karşılaştırma şu mantığı izler:
/// <code>
/// productTotal = müşterinin bu yayındaki ürün label toplamı
/// settings.Shipping kapalı → dekont == productTotal mı? (kargo yok)
/// productTotal >= threshold → ücretsiz kargo, dekont == productTotal mı?
/// productTotal < threshold → expected = productTotal + ShippingFee
///   dekont == expected → kargo dahil ✓
///   dekont == productTotal → kargo eksik, vendor karar versin (Hold / RecipientPays)
///   diğer → ne fazla ne tam, belirsiz
/// </code>
/// </summary>
public sealed class PaymentMatcherService
{
    private readonly LabelRepository _labels;
    private readonly System.Func<AppSettings> _settings;

    public PaymentMatcherService(LabelRepository labels, System.Func<AppSettings> settings)
    {
        _labels = labels;
        _settings = settings;
    }

    public enum MatchOutcome
    {
        /// <summary>Dekont tutarı beklenen toplamla bire bir eşleşiyor.
        /// Vendor'a soru sorulmaz, ShipmentDirective=Normal.</summary>
        Match,

        /// <summary>Dekont sadece ürün toplamı kadar, kargo ücreti eksik.
        /// Vendor karar versin: Hold veya RecipientPays.</summary>
        ShippingShortage,

        /// <summary>Dekont tutarı ne tam eşleşiyor ne de tam ürün toplamı —
        /// belirsiz, vendor manuel kontrol etsin. Şu an dialog açılmaz; mobile
        /// onay/red akışı normal devam eder (vendor mobile'da görür).</summary>
        Mismatch
    }

    public sealed record MatchResult(
        MatchOutcome Outcome,
        decimal ProductTotal,
        decimal ExpectedAmount,
        decimal DekontAmount,
        decimal? ShippingFee);

    public MatchResult Match(string customerId, string sessionId, decimal dekontAmount)
    {
        var productTotal = _labels.GetCustomerSessionLabelTotal(customerId, sessionId);
        var shipping = _settings().Shipping;

        if (!shipping.IsEnabled)
        {
            // Feature kapalı — basit eşleşme.
            var outcome = dekontAmount == productTotal ? MatchOutcome.Match : MatchOutcome.Mismatch;
            return new(outcome, productTotal, productTotal, dekontAmount, null);
        }

        var threshold = shipping.FreeShippingThreshold!.Value;
        var fee = shipping.ShippingFee!.Value;

        if (productTotal >= threshold)
        {
            // Ücretsiz kargo bölgesi — kargo ücreti beklenmiyor.
            var outcome = dekontAmount == productTotal ? MatchOutcome.Match : MatchOutcome.Mismatch;
            return new(outcome, productTotal, productTotal, dekontAmount, fee);
        }

        // Eşik altı — kargo ücreti beklentisi var.
        var expectedWithFee = productTotal + fee;

        if (dekontAmount == expectedWithFee)
            return new(MatchOutcome.Match, productTotal, expectedWithFee, dekontAmount, fee);

        if (dekontAmount == productTotal)
            return new(MatchOutcome.ShippingShortage, productTotal, expectedWithFee, dekontAmount, fee);

        return new(MatchOutcome.Mismatch, productTotal, expectedWithFee, dekontAmount, fee);
    }
}
