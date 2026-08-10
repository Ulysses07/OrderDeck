using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// CancelLabelDialog'un çekmece hâli. Sonuç <see cref="SelectedReasonCode"/>'da
/// duruyor; çekmece sonuç taşımadığı için (bkz. <see cref="Drawer"/> notu)
/// çağıran fabrikadan dönen örneği tutup okur.
/// </summary>
public partial class CancelLabelDrawer : UserControl
{
    private readonly Drawer _drawer;

    private CancelLabelDrawer(Drawer drawer)
    {
        InitializeComponent();
        _drawer = drawer;
    }

    public static CancelLabelDrawer Create(Drawer drawer) => new(drawer);

    /// <summary>Seçilen sebep kodu (CancelReasonCodes.* ya da operatörün yazdığı
    /// metinle "custom:..."). Onayda dolar, iptalde null kalır.</summary>
    public string? SelectedReasonCode { get; private set; }

    private void OnConfirm(object sender, RoutedEventArgs e)
    {
        if (ReasonCustom.IsChecked == true)
        {
            var text = CustomReasonText.Text?.Trim();
            if (string.IsNullOrEmpty(text))
            {
                WarningText.Text = "Özel sebep boş bırakılamaz.";
                WarningText.Visibility = Visibility.Visible;
                CustomReasonText.Focus();
                return;
            }
            SelectedReasonCode = CancelReasonCodes.CustomPrefix + text;
        }
        else if (ReasonWrongProduct.IsChecked == true)
            SelectedReasonCode = CancelReasonCodes.WrongProduct;
        else if (ReasonDuplicate.IsChecked == true)
            SelectedReasonCode = CancelReasonCodes.Duplicate;
        else if (ReasonOutOfStock.IsChecked == true)
            SelectedReasonCode = CancelReasonCodes.OutOfStock;
        else
            SelectedReasonCode = CancelReasonCodes.Customer;

        _drawer.Close(true);
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
