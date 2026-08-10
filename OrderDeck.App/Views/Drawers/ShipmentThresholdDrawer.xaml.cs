using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Sales;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Kümülatif kargo eşiği sorusu, çekmece içinde. Eski
/// <c>ShipmentThresholdDialog</c> penceresinin yerini alır.
///
/// Karar pencere sürümünde code-behind'ın <c>Result</c> özelliğinde
/// duruyordu; artık ViewModel'de (<see
/// cref="ShipmentThresholdDialogViewModel.ChosenDecision"/>). Sebep: zinciri
/// süren <see cref="DekontEkleViewModel"/> view örneğini hiç görmüyor —
/// ViewModel'i o yaratıp fabrikaya veriyor, sonucu da oradan okuyor. Kardeş
/// çekmece (<see cref="ShipmentDirectiveDrawer"/>) ile aynı sözleşme.
/// </summary>
public partial class ShipmentThresholdDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly ShipmentThresholdDialogViewModel _vm;

    public ShipmentThresholdDrawer(Drawer drawer, ShipmentThresholdDialogViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        _vm = vm;
        DataContext = vm;
    }

    private void OnShipNow(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDecision = ShipmentDecision.ShipNow;
        _drawer.Close(true);
    }

    private void OnHold(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDecision = ShipmentDecision.Hold;
        _drawer.Close(true);
    }
}
