using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Settings;

namespace OrderDeck.App.Views;

public partial class NewGiveawayDialog : Window
{
    public NewGiveawayDialogViewModel ViewModel { get; }

    private bool _webReady;

    public NewGiveawayDialog()
    {
        InitializeComponent();
        ViewModel = new NewGiveawayDialogViewModel();
        DataContext = ViewModel;
    }

    public NewGiveawayDialog(AppSettings settings, AnimationCatalogClient? catalogClient = null)
    {
        InitializeComponent();
        ViewModel = new NewGiveawayDialogViewModel(settings, catalogClient);
        DataContext = ViewModel;
    }

    // ── Canlı animasyon önizlemesi (WebView2 → overlay preview sayfası) ──
    // Seçili animasyonun gerçek önizlemesini sağ panelde gösterir; seçim
    // değişince yeniden gezinir. WebView2/Edge runtime yoksa sessizce
    // fallback metni kalır. Pencere kapanınca about:blank ile durdurulur.
    private async void OnDialogLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            PreviewWeb.DefaultBackgroundColor = System.Drawing.Color.FromArgb(0x0D, 0x0E, 0x14);
            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrderDeck", "WebView2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await PreviewWeb.EnsureCoreWebView2Async(env);
            // Overlay preview sayfası tam-ekran için tasarlı (sahne 700px'e kadar);
            // küçük panele sığsın diye içeriği ölçekle + kaydırma çubuklarını gizle.
            PreviewWeb.ZoomFactor = 0.45;
            PreviewWeb.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            PreviewWeb.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await PreviewWeb.CoreWebView2.ExecuteScriptAsync(
                        "document.documentElement.style.overflow='hidden';document.body.style.overflow='hidden';");
                }
                catch { /* ignore */ }
            };
            _webReady = true;
            NavigateToSelectedAnimation();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Giveaway preview WebView2 init failed: {ex.Message}");
            // fallback metni görünür kalır
        }
    }

    private void OnAnimSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        NavigateToSelectedAnimation();

    private void NavigateToSelectedAnimation()
    {
        if (!_webReady || PreviewWeb.CoreWebView2 is null) return;
        if (AnimCombo.SelectedItem is not AnimationCatalogEntry entry
            || string.IsNullOrEmpty(entry.OverlayBase)) return;
        var url = $"{entry.OverlayBase}/overlay/preview?animation={Uri.EscapeDataString(entry.Id)}";
        try
        {
            PreviewWeb.CoreWebView2.Navigate(url);
            PreviewFallback.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Giveaway preview nav failed: {ex.Message}");
        }
    }

    private void OnDialogClosed(object? sender, EventArgs e)
    {
        try { PreviewWeb.CoreWebView2?.Navigate("about:blank"); } catch { /* ignore */ }
        try { PreviewWeb.Dispose(); } catch { /* ignore */ }
    }

    // Kazanan sayısı stepper'ı — VM'e komut eklemeden, ObservableProperty
    // setter'ı üzerinden 1-50 aralığında ayarlar (Validate ile aynı sınır).
    private void OnWinnerInc(object sender, RoutedEventArgs e) =>
        ViewModel.WinnerCount = System.Math.Min(50, ViewModel.WinnerCount + 1);

    private void OnWinnerDec(object sender, RoutedEventArgs e) =>
        ViewModel.WinnerCount = System.Math.Max(1, ViewModel.WinnerCount - 1);

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate()) return;
        ViewModel.MarkSaved();
        DialogResult = true;
        Close();
    }
}
