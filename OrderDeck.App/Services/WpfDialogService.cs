using System.Windows;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.Views;
using OrderDeck.App.Views.Drawers;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.Services;

public sealed class WpfDialogService : IDialogService
{
    private readonly CustomerRepository _customers;
    private readonly IDrawerService _drawers;

    public WpfDialogService(CustomerRepository customers, IDrawerService drawers)
    {
        _customers = customers;
        _drawers = drawers;
    }

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

    public void ShowInfo(string message)
        => MessageBox.Show(message, "Bilgi", MessageBoxButton.OK, MessageBoxImage.Information);

    public void Show(string message, string title, DialogSeverity severity = DialogSeverity.Info)
        => MessageBox.Show(message, title, MessageBoxButton.OK, Icon(severity));

    // Onay kutusu her zaman Question: kabuktaki sekiz onayın hepsi bu ikonu
    // kullanıyordu, severity parametresi tek bir çağrıya bile hizmet etmezdi.
    public bool Confirm(string message, string title)
        => MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question)
           == MessageBoxResult.Yes;

    public Task<bool> ConfirmAsync(string message, string title)
        => _drawers.ShowAsync(title, d => MessageDrawer.ForConfirm(d, message));

    public Task ShowAsync(string message, string title, DialogSeverity severity = DialogSeverity.Info)
        => _drawers.ShowAsync(title, d => MessageDrawer.ForNotice(d, message, severity));

    private static MessageBoxImage Icon(DialogSeverity severity) => severity switch
    {
        DialogSeverity.Error   => MessageBoxImage.Error,
        DialogSeverity.Warning => MessageBoxImage.Warning,
        _                      => MessageBoxImage.Information,
    };
}
