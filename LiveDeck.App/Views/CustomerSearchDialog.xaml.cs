using System.Windows;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Customers;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class CustomerSearchDialog : Window
{
    public CustomerSearchDialog(CustomerSearchViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
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
