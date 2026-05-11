using System.Windows;
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
