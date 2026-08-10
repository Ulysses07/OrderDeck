using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Payments;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Kargo yönergesi sorusu, çekmece içinde. Eski <c>ShipmentDirectiveDialog</c>
/// penceresinin yerini alır.
///
/// Sonuç eskiden olduğu gibi ViewModel'in <see
/// cref="ShipmentDirectiveDialogViewModel.ChosenDirective"/> alanından okunur;
/// çekmece sonucu taşımaz (bkz. <see cref="Drawer"/> baş notu). Zinciri süren
/// <see cref="DekontEkleViewModel"/> ViewModel'i kendi yaratıp buraya
/// verdiği için kararı geri okuyabiliyor.
/// </summary>
public partial class ShipmentDirectiveDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly ShipmentDirectiveDialogViewModel _vm;

    public ShipmentDirectiveDrawer(Drawer drawer, ShipmentDirectiveDialogViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        _vm = vm;
        DataContext = vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDirective = null;
        _drawer.Close(false);
    }

    private void OnHold(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDirective = ShipmentDirective.Hold;
        _drawer.Close(true);
    }

    private void OnRecipientPays(object sender, RoutedEventArgs e)
    {
        _vm.ChosenDirective = ShipmentDirective.RecipientPays;
        _drawer.Close(true);
    }
}
