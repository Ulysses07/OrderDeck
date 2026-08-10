using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Kara liste sayfası (eski <c>BlacklistDialog</c> penceresi).
///
/// Fabrikası <see cref="Services.Pages.Page"/> ALMIYOR: sayfanın kendini
/// kapatması gerekmiyor, çıkış PageHost'un geri okunda. Kapanışı tetikleyen
/// sayfalar (örn. <see cref="AccountPage"/>) Page'i alır.
/// </summary>
public partial class BlacklistPage : UserControl
{
    private BlacklistPage(BlacklistViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    public static BlacklistPage Create(BlacklistViewModel vm) => new(vm);
}
