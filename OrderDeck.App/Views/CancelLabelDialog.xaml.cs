using System.Windows;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Views;

public partial class CancelLabelDialog : Window
{
    public CancelLabelDialog()
    {
        InitializeComponent();
    }

    /// <summary>The chosen reason code (one of CancelReasonCodes.* or
    /// "custom:..." with the operator's free text). Set on Confirm; null on
    /// cancel.</summary>
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

        DialogResult = true;
        Close();
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
