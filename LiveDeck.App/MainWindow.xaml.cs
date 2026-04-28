using System.ComponentModel;
using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // If a giveaway is active, refuse the close and tell the user to finish/cancel it
        // first — the regular EndStream path has the same gate.
        var vm = App.Host.Services.GetService<MainShellViewModel>();
        if (vm is not null && vm.IsGiveawayActive)
        {
            MessageBox.Show(
                "Aktif çekiliş var. Önce çekilişi tamamla veya iptal et.",
                "Çekiliş aktif",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
    }
}
