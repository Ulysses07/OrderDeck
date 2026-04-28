using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsDialog()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<SettingsViewModel>();
        DataContext = _vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        if (!_vm.Saved) return;   // validation failed, dialog stays open

        if (_vm.OverlayPortChanged)
        {
            MessageBox.Show(
                "Overlay portu değiştirildi. Bu değişiklik için uygulamayı kapatıp yeniden açmanız gerekir.",
                "Yeniden başlatma gerekir",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DialogResult = true;
        Close();
    }
}
