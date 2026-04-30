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

    public PaymentRequestResult OpenWhatsApp(Customer customer, decimal totalAmount, DateTime streamDate)
    {
        if (!PhoneNormalizer.IsValidTr(customer.Phone))
            return PaymentRequestResult.PhoneRequired;

        var settings = _settingsStore.Load();
        var ctx = new PaymentContext(
            DisplayName: customer.DisplayName ?? customer.Username,
            TotalAmount: totalAmount,
            StreamDate: streamDate,
            Iban: settings.Payment.Iban,
            AccountHolder: settings.Payment.AccountHolder,
            Papara: settings.Payment.Papara);

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
}
