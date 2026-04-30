using System.Windows;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Views;

public partial class PhoneEntryDialog : Window
{
    public PhoneEntryDialog(CustomerRepository customers, string customerId)
    {
        InitializeComponent();
        var vm = new PhoneEntryDialogViewModel(customers, customerId, () =>
        {
            DialogResult = true;
            Close();
        });
        DataContext = vm;
    }
}
