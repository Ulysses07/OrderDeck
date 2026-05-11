using System.IO;
using System.Windows;
using Microsoft.Win32;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views;

public partial class DekontEkleDialog : Window
{
    private readonly DekontEkleViewModel _vm;

    public DekontEkleDialog(DekontEkleViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e) => DialogResult = false;

    private void OnPickPdf(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFileDialog
        {
            Title = "Dekont PDF'i seç",
            Filter = "PDF dosyaları (*.pdf)|*.pdf",
            CheckFileExists = true
        };
        if (picker.ShowDialog(this) != true) return;

        try
        {
            var bytes = File.ReadAllBytes(picker.FileName);
            if (bytes.Length > 20 * 1024 * 1024) // 20 MB
            {
                MessageBox.Show(
                    "PDF dosyası 20 MB'tan büyük. Bu boyutta bir dekont olası değil — yanlış dosya seçmiş olabilir misin?",
                    "Çok büyük dosya", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var error = _vm.TryFillFromPdf(bytes);
            if (error is not null)
            {
                MessageBox.Show(error, "PDF okunamadı", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (System.Exception ex)
        {
            MessageBox.Show($"PDF okunurken hata: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var result = _vm.TrySave();
        switch (result.Kind)
        {
            case DekontEkleViewModel.SaveResultKind.Saved:
                DialogResult = true;
                break;

            case DekontEkleViewModel.SaveResultKind.NeedsShipmentDecision:
                // Kargo PR D: vendor karar versin (Hold / RecipientPays / Vazgeç)
                var directive = AskShipmentDirective(result.Shortage!);
                if (directive is null)
                {
                    // Vazgeç — dialog açık kalır, vendor formu düzeltebilir
                    return;
                }
                var commit = _vm.CommitWithDirective(directive.Value);
                if (commit.Kind == DekontEkleViewModel.SaveResultKind.Saved)
                    DialogResult = true;
                // commit.Kind == Error: ErrorMessage binding ile UI'da görünür
                break;

            case DekontEkleViewModel.SaveResultKind.Error:
                // ErrorMessage VM tarafından set edildi, UI binding zaten gösteriyor
                break;
        }
    }

    private Core.Payments.ShipmentDirective? AskShipmentDirective(
        Core.Payments.PaymentMatcherService.MatchResult match)
    {
        var vm = new ShipmentDirectiveDialogViewModel(match);
        var dlg = new ShipmentDirectiveDialog(vm) { Owner = this };
        dlg.ShowDialog();
        return dlg.Result;
    }
}
