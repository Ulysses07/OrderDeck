using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OrderDeck.App.Shortcuts;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace OrderDeck.App;

public partial class MainWindow : Window
{
    // Başlık çubuğunu koyu yaptırır (Windows 10 20H1+). WPF'in pencere
    // çerçevesi kabuğun malı; XAML'den boyanamıyor, tek yol bu DWM çağrısı.
    // Değer 20 = DWMWA_USE_IMMERSIVE_DARK_MODE. Windows 10 1809'da geçici
    // olarak 19'du; o yapılarda çağrı sessizce başarısız olur ve başlık
    // açık kalır — kabul edilebilir, uygulama etkilenmiyor.
    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attr, ref int value, int size);

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        var on = 1;
        // Dönüş değeri bilerek yok sayılıyor: desteklemeyen yapıda
        // E_INVALIDARG döner, yapacak bir şey yok.
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref on, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var binder = App.Host.Services.GetRequiredService<ShortcutBinder>();
        binder.Apply(this);
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
