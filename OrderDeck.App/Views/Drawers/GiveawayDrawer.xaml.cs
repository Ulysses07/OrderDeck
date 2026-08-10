using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using OrderDeck.App.Services;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Settings;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Yeni çekiliş formu, çekmece içinde. Eski <c>NewGiveawayDialog</c>
/// penceresinin yerini alır; <see cref="NewGiveawayDialogViewModel"/> ve
/// doğrulaması aynen korunur.
///
/// <c>ShowDialog() == true</c>'nun karşılığı <c>drawer.Close(true)</c>:
/// çağıran <see cref="Drawer.Completion"/>'ı bekler, sonucu (anahtar
/// kelime, süre, kazanan sayısı...) eskiden olduğu gibi <see cref="ViewModel"/>
/// üzerinden okur.
/// </summary>
public partial class GiveawayDrawer : UserControl
{
    private readonly Drawer _drawer;
    private bool _webReady;

    public NewGiveawayDialogViewModel ViewModel { get; }

    private GiveawayDrawer(Drawer drawer, AppSettings settings,
                           AnimationCatalogClient? catalogClient)
    {
        InitializeComponent();
        _drawer = drawer;
        ViewModel = new NewGiveawayDialogViewModel(settings, catalogClient);
        DataContext = ViewModel;
    }

    /// <summary>Çekmece fabrikası. <see cref="IDrawerService.ShowAsync"/>'e
    /// verilir; ctor'un private olması içeriksiz/çekmecesiz bir örneğin
    /// yaratılmasını imkânsız kılar (MessageDrawer ile aynı kalıp).</summary>
    public static GiveawayDrawer Create(Drawer drawer, AppSettings settings,
                                        AnimationCatalogClient? catalogClient = null)
        => new(drawer, settings, catalogClient);

    // ── Canlı animasyon önizlemesi (WebView2 → overlay preview sayfası) ──
    // Pencereden çekmeceye taşınırken davranış değişmedi; init hâlâ tembel
    // ve hatası yutuluyor — Edge çalışma zamanı olmayan makinede çekiliş
    // kurulumu önizleme yüzünden durmamalı.
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_webReady) return;   // çekmece yığında gezinirken Loaded tekrar gelebilir

        try
        {
            // Yüklenene kadar beyaz parlama olmasın. Renk paletten okunuyor;
            // spec §10 "sabit hex 0" — pencere sürümünde burada elle yazılmış
            // bir renk vardı.
            var surface = (System.Windows.Media.SolidColorBrush)FindResource("OD.Brush.Surface2");
            PreviewWeb.DefaultBackgroundColor = System.Drawing.Color.FromArgb(
                surface.Color.R, surface.Color.G, surface.Color.B);

            var udf = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OrderDeck", "WebView2");
            Directory.CreateDirectory(udf);
            var env = await CoreWebView2Environment.CreateAsync(null, udf);
            await PreviewWeb.EnsureCoreWebView2Async(env);
            // Overlay preview sayfası tam ekran için tasarlı (sahne 700px'e
            // kadar). Pencere sürümünde 380px sütuna 0.45 ile sığıyordu;
            // çekmece 400px ama önizleme kutusu 250px yerine 170px yüksek,
            // o yüzden daha da küçültüldü.
            PreviewWeb.ZoomFactor = 0.30;
            PreviewWeb.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
            PreviewWeb.CoreWebView2.NavigationCompleted += async (_, _) =>
            {
                try
                {
                    await PreviewWeb.CoreWebView2.ExecuteScriptAsync(
                        "document.documentElement.style.overflow='hidden';" +
                        "document.body.style.overflow='hidden';");
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

    private void OnAnimSelectionChanged(object sender, SelectionChangedEventArgs e)
        => NavigateToSelectedAnimation();

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

    /// <summary>
    /// Pencere sürümünde temizlik <c>Window.Closed</c>'daydı; çekmecenin
    /// böyle bir olayı yok — kapanınca içerik görsel ağaçtan düşüyor, yani
    /// karşılığı Unloaded. Yapılmazsa WebView2 süreci arkada dönmeye devam
    /// eder ve her çekiliş kurulumunda bir yenisi eklenir.
    /// </summary>
    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        try { PreviewWeb.CoreWebView2?.Navigate("about:blank"); } catch { /* ignore */ }
        try { PreviewWeb.Dispose(); } catch { /* ignore */ }
        _webReady = false;
    }

    private void OnWinnerInc(object sender, RoutedEventArgs e)
        => ViewModel.WinnerCount = Math.Min(50, ViewModel.WinnerCount + 1);

    private void OnWinnerDec(object sender, RoutedEventArgs e)
        => ViewModel.WinnerCount = Math.Max(1, ViewModel.WinnerCount - 1);

    private void OnCancel(object sender, RoutedEventArgs e) => _drawer.Close(false);

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate()) return;
        ViewModel.MarkSaved();
        _drawer.Close(true);
    }
}
