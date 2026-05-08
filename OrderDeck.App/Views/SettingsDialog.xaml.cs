using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using OrderDeck.App.ViewModels;
using OrderDeck.Core;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

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

    /// <summary>Opens %LOCALAPPDATA%/OrderDeck/logs in Explorer so the
    /// operator can reach the Serilog file sink without knowing the
    /// AppData path. Used when reporting issues / sharing crash logs.</summary>
    private void OnOpenLogs(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = AppPaths.LogsFolder;
            if (!Directory.Exists(folder))
                Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo
            {
                FileName = folder,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Log klasörü açılamadı:\n\n{ex.Message}\n\nManuel yol:\n{AppPaths.LogsFolder}",
                "Logları Aç", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
