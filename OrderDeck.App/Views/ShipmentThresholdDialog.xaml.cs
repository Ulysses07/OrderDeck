using System.Windows;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Views;

public partial class ShipmentThresholdDialog : Window
{
    public ShipmentDecision? Result { get; private set; }

    public ShipmentThresholdDialog(ShipmentThresholdDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void OnShipNow(object sender, RoutedEventArgs e)
    {
        Result = ShipmentDecision.ShipNow;
        DialogResult = true;
    }

    private void OnHold(object sender, RoutedEventArgs e)
    {
        Result = ShipmentDecision.Hold;
        DialogResult = true;
    }
}
