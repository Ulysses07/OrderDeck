using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Payments;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Kargo PR D: Matcher ShippingShortage tespit ettiğinde vendor'a sunulan
/// modal'ın ViewModel'ı. UI 2 ana buton gösterir (Beklet / Alıcı Ödemeli)
/// + Vazgeç. ChosenDirective ile sonuç DekontEkleViewModel'a aktarılır.
///
/// Bu VM tamamen read-only sunum + button state — XAML basitliği için ViewModel
/// minimal. Code-behind sadece DialogResult set ediyor (mevcut dialog pattern'i).
/// </summary>
public sealed partial class ShipmentDirectiveDialogViewModel : ObservableObject
{
    public ShipmentDirectiveDialogViewModel(PaymentMatcherService.MatchResult match)
    {
        PayerNamePlaceholder = "(müşteri seçildi)";
        ProductTotalText = $"{match.ProductTotal:0.##} TL";
        ShippingFeeText = match.ShippingFee.HasValue
            ? $"{match.ShippingFee.Value:0.##} TL"
            : "—";
        ExpectedAmountText = $"{match.ExpectedAmount:0.##} TL";
        DekontAmountText = $"{match.DekontAmount:0.##} TL";
        ShortfallText = $"{match.ExpectedAmount - match.DekontAmount:0.##} TL";
    }

    public string PayerNamePlaceholder { get; }
    public string ProductTotalText { get; }
    public string ShippingFeeText { get; }
    public string ExpectedAmountText { get; }
    public string DekontAmountText { get; }
    public string ShortfallText { get; }

    /// <summary>Vendor kararı — dialog code-behind tarafından set edilir.
    /// null kalırsa Vazgeç seçildi.</summary>
    public ShipmentDirective? ChosenDirective { get; set; }
}
