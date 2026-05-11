using System.Windows;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Payments;

namespace OrderDeck.App.Views;

public partial class ShipmentDirectiveDialog : Window
{
    private readonly ShipmentDirectiveDialogViewModel _vm;

    public ShipmentDirectiveDialog(ShipmentDirectiveDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    public ShipmentDirective? Result => _vm.ChosenDirective;

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDirective = null;
        DialogResult = false;
    }

    private void OnHold(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDirective = ShipmentDirective.Hold;
        DialogResult = true;
    }

    private void OnRecipientPays(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDirective = ShipmentDirective.RecipientPays;
        DialogResult = true;
    }
}
