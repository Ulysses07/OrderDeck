using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Dekont giriş formu, çekmece içinde. Eski <c>DekontEkleDialog</c>
/// penceresinin yerini alır.
///
/// Pencere sürümünde kaydetme ZİNCİRİ burada, code-behind'daydı:
/// <c>TrySave</c> → (kargo yönergesi modal'ı) → <c>CommitWithDirective</c> →
/// (kargo eşiği modal'ı) → <c>ApplyShipmentDecision</c>. O akış
/// <see cref="DekontEkleViewModel.SaveAsync"/>'e taşındı (spec §9, Faz 2b/4);
/// geriye yalnız "hangi çekmecedeyim" bilgisi kaldı. Dosya seçici hâlâ
/// burada: <see cref="OpenFileDialog"/> bir işletim sistemi penceresi,
/// spec'in kaldırdığı uygulama içi pop-up'lardan değil.
/// </summary>
public partial class DekontEkleDrawer : UserControl
{
    private const int MaxPdfBytes = 20 * 1024 * 1024;

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
        if (await _vm.SaveAsync()) _drawer.Close(true);
    }

    private void OnPickPdf(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Dekont PDF'i seç",
            Filter = "PDF dosyaları (*.pdf)|*.pdf",
            CheckFileExists = true
        };
        if (picker.ShowDialog(Window.GetWindow(this)) != true) return;

        // PDF okuma hataları ErrorMessage'a yazılıyor: pencere sürümünde üç
        // ayrı MessageBox vardı, çekmecede hata için zaten sabit bir satır
        // var (spec §6.3 "satır içi onay" ile aynı gerekçe).
        try
        {
            var bytes = File.ReadAllBytes(picker.FileName);
            if (bytes.Length > MaxPdfBytes)
            {
                _vm.ErrorMessage =
                    "PDF dosyası 20 MB'tan büyük. Bu boyutta bir dekont olası değil — "
                    + "yanlış dosya seçmiş olabilir misin?";
                return;
            }
            _vm.ErrorMessage = _vm.TryFillFromPdf(bytes);
        }
        catch (System.Exception ex)
        {
            _vm.ErrorMessage = "PDF okunurken hata: " + ex.Message;
        }
    }
}
