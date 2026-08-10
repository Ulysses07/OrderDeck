using System.Windows.Controls;
using OrderDeck.App.ViewModels;

// System.Windows.Controls.Page ile ad çakışması var (bu dosya ikisini de
// görüyor). Takma ad, çakışmayı using yönergesinde bir kez çözüp gövdeyi
// tam nitelikli isimlerden kurtarıyor.
using ShellPage = OrderDeck.App.Services.Pages.Page;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Hesap sayfası (eski <c>AccountDialog</c> penceresi).
///
/// Page ALAN tek PR-1 sayfası: ViewModel kendi kapanışını istiyor
/// (<c>RequestClose</c> — çıkış yaptıktan ya da giriş penceresi kapandıktan
/// sonra). Pencere sürümünde karşılığı <c>DialogResult = true; Close();</c>
/// idi; sonucu okuyan kimse olmadığı için (Faz 3 planı, DialogResult
/// denetimi) yalnız kapanış kaldı.
/// </summary>
public partial class AccountPage : UserControl
{
    private AccountPage(ShellPage page, AccountDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, _) => page.Close();
    }

    public static AccountPage Create(ShellPage page, AccountDialogViewModel vm)
        => new(page, vm);
}
