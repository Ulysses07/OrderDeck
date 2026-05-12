using System;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Settings;

namespace OrderDeck.App.Services;

public enum PaymentRequestResult
{
    Opened,
    PhoneRequired,
    LaunchFailed
}

/// <summary>
/// Phase 4g: Customer + tutar + tarih → Settings template substitution + wa.me launch.
/// Phone null/invalid ise PhoneRequired (caller PhoneEntryDialog açar).
///
/// Kargo entegrasyon (2026-05-12): caller ürün toplamını geçer, service
/// AppSettings.Shipping + Customer.RecipientPaysActive flag'ine göre kargo
/// ücretini hesaplar, template'e ShippingNote + final TotalAmount yansıtır.
/// Mevcut caller signature'ları aynı kalır (productTotal eski "totalAmount"un
/// semantic karşılığı — caller zaten label sum geçiriyordu).
/// </summary>
public sealed class PaymentRequestService
{
    private readonly SettingsStore _settingsStore;
    private readonly WhatsAppMessageBuilder _messageBuilder;
    private readonly IUrlLauncher _launcher;

    public PaymentRequestService(
        SettingsStore settingsStore,
        WhatsAppMessageBuilder messageBuilder,
        IUrlLauncher launcher)
    {
        _settingsStore = settingsStore;
        _messageBuilder = messageBuilder;
        _launcher = launcher;
    }

    /// <param name="productTotal">Müşterinin ürün label'larının toplamı
    /// (kargo HARİÇ). Service kargo ücretini settings + customer flag'e göre
    /// otomatik ekler.</param>
    public PaymentRequestResult OpenWhatsApp(Customer customer, decimal productTotal, DateTime streamDate)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var (totalAmount, shippingFee, shippingNote) = ComputeShipping(customer, productTotal, settings);

        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara,
            ProductTotal: productTotal,
            ShippingFee: shippingFee,
            ShippingNote: shippingNote);

        var message = _messageBuilder.BuildMessage(settings.Payment.WhatsAppMessageTemplate, ctx);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>
    /// Kümülatif kargo PR-E (2026-05-12): Müşteri ücretsiz kargo eşiğini
    /// aştığında vendor "Evet kargolansın" dedikten sonra çağrılır. WhatsApp
    /// linki açar; phone yoksa PhoneRequired döner. Template'i AppSettings'ten
    /// okur; boşsa Opened ile fail-silent çıkar (mesaj atılmaz).
    /// </summary>
    public PaymentRequestResult OpenShippingWonWhatsApp(Customer customer, decimal cumulativeAmount)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var template = settings.Payment.ShippingWonTemplate;
        if (string.IsNullOrWhiteSpace(template))
            return PaymentRequestResult.Opened; // template kapalı, sessiz geç

        var message = _messageBuilder.BuildShippingWonMessage(
            template,
            customer.DisplayName ?? customer.Username,
            cumulativeAmount);
        var link = _messageBuilder.BuildWaMeLink(customer.Phone!, message);

        try
        {
            _launcher.Launch(link);
            return PaymentRequestResult.Opened;
        }
        catch
        {
            return PaymentRequestResult.LaunchFailed;
        }
    }

    /// <summary>
    /// Kargo karar matrisi:
    /// <list type="bullet">
    ///   <item>Customer.RecipientPaysActive → "Kargo: alıcı ödemeli" (kargo fee
    ///     müşteriden istenmez; kargo şirketi tahsil eder)</item>
    ///   <item>Shipping feature kapalı → ShippingNote boş, total = productTotal</item>
    ///   <item>productTotal &gt;= FreeShippingThreshold → "Ücretsiz kargo"</item>
    ///   <item>productTotal &lt; threshold → "Kargo: X TL", total = productTotal + fee</item>
    /// </list>
    /// </summary>
    internal static (decimal Total, decimal? Fee, string Note) ComputeShipping(
        Customer customer, decimal productTotal, AppSettings settings)
    {
        if (customer.RecipientPaysActive)
        {
            return (productTotal, null, "Kargo: alıcı ödemeli");
        }

        var shipping = settings.Shipping;
        if (!shipping.IsEnabled)
        {
            return (productTotal, null, "");
        }

        if (productTotal >= shipping.FreeShippingThreshold!.Value)
        {
            return (productTotal, null, "Ücretsiz kargo");
        }

        var fee = shipping.ShippingFee!.Value;
        var tr = System.Globalization.CultureInfo.GetCultureInfo("tr-TR");
        var note = $"Kargo: {fee.ToString("N2", tr)} TL";
        return (productTotal + fee, fee, note);
    }
}
