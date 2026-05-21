using System.Windows;
using System.Windows.Input;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Customers;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

public partial class CustomerSearchDialog : Window
{
    public CustomerSearchDialog(CustomerSearchViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        // Initial populate so the operator sees recent customers (including
        // newly-registered shoppers) without having to type anything.
        vm.RefreshSearch();
    }

    private void ResultsList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not Customer selected) return;
        var detail = App.Host.Services.GetRequiredService<CustomerDetailDialog>();
        detail.Owner = this;
        detail.Open(selected.Id);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
