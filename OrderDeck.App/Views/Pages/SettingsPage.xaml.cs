using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;
using OrderDeck.Core;

// System.Windows.Controls.Page ile ad çakışması var (bu dosya ikisini de
// görüyor). Takma ad, çakışmayı using yönergesinde bir kez çözüp gövdeyi
// tam nitelikli isimlerden kurtarıyor.
using ShellPage = OrderDeck.App.Services.Pages.Page;

namespace OrderDeck.App.Views.Pages;

/// <summary>
/// Ayarlar sayfası (eski <c>SettingsDialog</c> penceresi).
///
/// Pencere sürümünden iki davranış düştü:
/// <list type="bullet">
///   <item><c>OnCancel</c> — geri oku (ve ESC) zaten kaydetmeden çıkıyor.</item>
///   <item><c>DialogResult</c> — kimse okumuyordu, kapanış tek başına yeter.</item>
/// </list>
///
/// ViewModel'i DI'dan burada ÇEKMİYOR (pencere sürümü
/// <c>App.Host.Services.GetRequiredService</c> diyordu): sayfayı açan taraf
/// zaten servis sağlayıcısına erişiyor, örneği fabrikaya veriyor.
/// </summary>
public partial class SettingsPage : UserControl
{
    private readonly ShellPage _page;
    private readonly SettingsViewModel _vm;

    private SettingsPage(ShellPage page, SettingsViewModel vm)
    {
        InitializeComponent();
        _page = page;
        _vm = vm;
        DataContext = vm;
    }

    public static SettingsPage Create(ShellPage page, SettingsViewModel vm)
        => new(page, vm);

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        if (!_vm.Saved) return;   // doğrulama düştü, sayfa açık kalıyor

        if (_vm.OverlayPortChanged)
        {
            MessageBox.Show(
                "Overlay portu değiştirildi. Bu değişiklik için uygulamayı kapatıp yeniden açmanız gerekir.",
                "Yeniden başlatma gerekir",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _page.Close();
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
