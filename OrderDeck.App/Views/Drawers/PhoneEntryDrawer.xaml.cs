using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Views.Drawers;

public partial class PhoneEntryDrawer : UserControl
{
    private readonly Drawer _drawer;

    private PhoneEntryDrawer(Drawer drawer, CustomerRepository customers, string customerId)
    {
        InitializeComponent();
        _drawer = drawer;
        // ViewModel kaydettiğinde çekmeceyi onaylı kapatır: eski pencerede
        // bu callback DialogResult=true + Close() idi, sözleşme aynı kaldı.
        DataContext = new PhoneEntryDialogViewModel(
            customers, customerId, () => _drawer.Close(true));
        Loaded += (_, _) => PhoneBox.Focus();
    }

    public static PhoneEntryDrawer Create(
        Drawer drawer, CustomerRepository customers, string customerId)
        => new(drawer, customers, customerId);

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
