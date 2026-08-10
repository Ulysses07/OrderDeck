using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App.Views;

public partial class MainShellView : UserControl
{
    public MainShellView()
    {
        InitializeComponent();
        // App.Host yalnız uygulama açılışında kurulur; kompozisyon testi bu
        // kontrolü DI olmadan örnekliyor (amaç XAML'ın çözülebildiğini
        // kanıtlamak). Üretimde App.Host her zaman dolu — davranış değişmez.
        if (App.Host is not null)
            DataContext = App.Host.Services.GetRequiredService<MainShellViewModel>();
        Loaded += OnLoaded;
        // Window-bubbled ESC handler — cancels backup-selection mode without
        // requiring focus on a particular control. We attach in Loaded so the
        // ancestor window is materialised.
        Loaded += AttachWindowEscHandler;
    }

    private void AttachWindowEscHandler(object sender, RoutedEventArgs e)
    {
        var win = Window.GetWindow(this);
        if (win is null) return;
        // Use PreviewKeyDown so we get the key before TextBoxes consume it for
        // their own purposes (typing ESC into a text input still cancels mode,
        // matching most apps' behaviour).
        win.PreviewKeyDown -= OnWindowPreviewKeyDown;
        win.PreviewKeyDown += OnWindowPreviewKeyDown;
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Gate açıkken shell'in kısayolları susar. ShellHost.IsEnabled=false
        // yalnız odak/tıklamayı keser; bu handler Window'a bağlı olduğu için
        // ondan etkilenmiyor — ESC arkadaki çekmeceyi kapatırdı.
        if (App.Host?.Services.GetRequiredService<Services.Gates.AppGateStack>().IsOpen == true)
            return;

        // Ctrl+K → ürün kodu kutusuna odaklan. ESC kontrolünden ÖNCE olmalı:
        // aşağıdaki erken dönüş Escape dışındaki her tuşu eler.
        if (e.Key == Key.K && (Keyboard.Modifiers & ModifierKeys.Control) != 0)
        {
            ProductBar.FocusCode();
            e.Handled = true;
            return;
        }

        if (e.Key != Key.Escape) return;

        // ESC en üstteki KATMANI kapatır: çekmece → sayfa → yedek seçim modu.
        // Operatörün ESC'ye bastığında kastettiği şey her zaman ekranın en
        // üstündeki katmandır.
        //
        // Çekmece sayfadan ÖNCE deneniyor. Bugün sayfadan çekmece açan bir
        // akış yok (Faz 3 planı yazılırken tarandı), yani sıra pratikte
        // çakışmıyor; ileride eklenirse doğru davranış kendiliğinden gelsin.
        if (App.Host is not null)
        {
            if (App.Host.Services.GetRequiredService<Services.Drawers.DrawerStack>().CloseTop())
            {
                e.Handled = true;
                return;
            }

            if (App.Host.Services.GetRequiredService<Services.Pages.PageStack>().Back())
            {
                e.Handled = true;
                return;
            }
        }

        if (DataContext is not MainShellViewModel vm) return;
        if (!vm.IsInBackupSelectionMode) return;

        vm.CancelBackupSelectionCommand.Execute(null);
        e.Handled = true;
    }

    /// <summary>
    /// Mockup'ın iki kırılımı. WPF'te medya sorgusu yok; pencere boyutunu
    /// ViewModel'e bayrak olarak bildiriyoruz ki stiller DataTrigger ile
    /// tepki versin. Eşikler mockup'taki @media değerleriyle birebir.
    /// </summary>
    private const double CompactWidth = 1360;
    private const double ShortHeight = 850;

    private void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;
        vm.IsCompact = e.NewSize.Width < CompactWidth;
        vm.IsShort = e.NewSize.Height < ShortHeight;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Phase 4c: trial just started this session — show banner once
        var licenseService = global::OrderDeck.App.App.Host.Services
            .GetRequiredService<global::OrderDeck.Licensing.Services.LicenseService>();
        if (licenseService.JustStartedTrial)
        {
            System.Windows.MessageBox.Show(
                "Deneme süresi başladı. 14 gün boyunca Instagram chat ile tüm özellikleri ücretsiz kullanabilirsiniz.",
                "OrderDeck — Deneme süresi",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Information);

            // Flag'i hemen düş — bir sonraki Loaded'da göstermesin
            licenseService.AcknowledgeTrialStartBanner();
        }
    }
}
