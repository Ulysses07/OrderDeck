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

    /// <summary>Eşiğin hesaplandığı tutar: kişinin bütün açık dosyalarının toplamı (U12 —
    /// yerel taşımadan kalan fazla dosya dahil; tek dosyada dosyanın kendi tutarı).</summary>
    public decimal CumulativeAmount => Context.PooledAmount;

    public string CumulativeAmountText =>
        CumulativeAmount.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " TL";

    public string ThresholdText =>
        FreeShippingThreshold.ToString("N2", CultureInfo.GetCultureInfo("tr-TR")) + " TL";

    /// <summary>Vendor kararı — çekmece code-behind'ı set eder. Faz 2b'de
    /// pencerenin code-behind'ından buraya taşındı: zinciri süren
    /// <see cref="DekontEkleViewModel"/> view örneğini görmüyor, kararı
    /// ViewModel üzerinden okuyor. null = karar verilmeden kapandı (ESC).</summary>
    public ShipmentDecision? ChosenDecision { get; set; }
}
