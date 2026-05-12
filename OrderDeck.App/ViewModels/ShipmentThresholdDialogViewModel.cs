using System.Globalization;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// PR-C/2: Kümülatif kargo eşik aşıldığında vendor'a sunulan modal'ın
/// view model'i. Read-only display — vendor tek karar verir:
/// "Evet, kargolansın" → ShipNow, "Beklemeye devam" → Hold (yeni HeldAt
/// set edilmez, mevcut state korunur).
/// </summary>
public sealed class ShipmentThresholdDialogViewModel
{
    public ShipmentThresholdDialogViewModel(
        ShipmentDecisionContext context,
        string customerDisplay,
        decimal freeShippingThreshold)
    {
        Context = context;
        CustomerDisplay = customerDisplay;
        FreeShippingThreshold = freeShippingThreshold;
    }

    public ShipmentDecisionContext Context { get; }
    public string CustomerDisplay { get; }
    public decimal FreeShippingThreshold { get; }

    public decimal CumulativeAmount => Context.Shipment?.CumulativeAmount ?? 0m;

    public string CumulativeAmountText =>
        CumulativeAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " TL";

    public string ThresholdText =>
        FreeShippingThreshold.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " TL";
}
