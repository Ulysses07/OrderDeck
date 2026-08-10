using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Yayıncı toplu SMS ekranı. DataContext = BulkSmsViewModel.
///
/// Kardeşi <see cref="SupportRequestsPage"/> gibi, pencere sürümündeki
/// <c>Open()</c> metodu düştü: bakiye + kampanya geçmişini yükleyip
/// <c>ShowDialog()</c> çağırıyordu. Yükleme artık sayfayı açan
/// <c>MainShellViewModel</c>'de.
/// </summary>
public partial class BulkSmsPage : UserControl
{
    private BulkSmsPage(BulkSmsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static BulkSmsPage Create(BulkSmsViewModel vm) => new(vm);
}
