using System.Windows;
using OrderDeck.App.Views;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private readonly CustomerRepository _customers;

    public WpfDialogService(CustomerRepository customers) => _customers = customers;

    public bool ShowPhoneEntryDialog(string customerId)
    {
        var dlg = new PhoneEntryDialog(_customers, customerId)
        {
            Owner = Application.Current.MainWindow
        };
        return dlg.ShowDialog() == true;
    }

    public void ShowError(string message)
        => MessageBox.Show(message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
}
