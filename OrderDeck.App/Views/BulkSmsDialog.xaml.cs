using System.Windows;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

/// <summary>
/// Yayıncı toplu SMS ekranı. DataContext = BulkSmsViewModel (DI). Açılışta
/// bakiye + kampanya geçmişini yükler.
/// </summary>
public partial class BulkSmsDialog : Window
{
    private readonly BulkSmsViewModel _vm;

    public BulkSmsDialog(BulkSmsViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Bakiye + geçmişi yükler ve dialog'u modal açar.</summary>
    public void Open()
    {
        _ = _vm.LoadAsync();
        ShowDialog();
    }
}
