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
        if (ResultsList.SelectedItem is not CustomerSearchViewModel.CustomerCard selected) return;
        var detail = App.Host.Services.GetRequiredService<CustomerDetailDialog>();
        detail.Owner = this;
        // Birleşik kartta telefonlu (birincil) satırın detayı açılır — iletişim
        // bilgisi orada.
        detail.Open(selected.Primary.Id);
    }

    private void ResultsList_OnSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is not CustomerSearchViewModel vm) return;
        vm.SelectedCards.Clear();
        foreach (var item in ResultsList.SelectedItems)
            if (item is CustomerSearchViewModel.CustomerCard card)
                vm.SelectedCards.Add(card);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
