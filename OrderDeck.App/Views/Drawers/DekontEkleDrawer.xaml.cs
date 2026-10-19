using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// "Gelen Ödemeler" formu, çekmece içinde (2026-10-19'a kadar "Yeni Dekont"; eski <c>DekontEkleDialog</c>
/// penceresinin yerini almıştı).
///
/// Pencere sürümünde kaydetme ZİNCİRİ burada, code-behind'daydı:
/// <c>TrySave</c> → (kargo yönergesi modal'ı) → <c>CommitWithDirective</c> →
/// (kargo eşiği modal'ı) → <c>ApplyShipmentDecision</c>. O akış
/// <see cref="DekontEkleViewModel.SaveAsync"/>'e taşındı (spec §9, Faz 2b/4);
/// geriye yalnız "hangi çekmecedeyim" bilgisi kaldı. PDF dosya seçici dekont
/// kaydıyla birlikte kalktı.
/// </summary>
public partial class DekontEkleDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly DekontEkleViewModel _vm;

    public DekontEkleDrawer(Drawer drawer, DekontEkleViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        _vm = vm;
        DataContext = vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        // false: doğrulama hatası (ErrorMessage bağlı) ya da operatör kargo
        // yönergesinden vazgeçti — çekmece açık kalsın, form düzeltilebilsin.
        // Zincir zaten sürüyorsa (çift tıklama) da false: kapatmayı ilk
        // tıklamanın zinciri yapar.
        if (await _vm.SaveAsync()) _drawer.Close(true);
    }
}
