using System.Windows;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.Views;

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
