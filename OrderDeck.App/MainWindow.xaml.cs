using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using OrderDeck.App.Services.Sync;
using OrderDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

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

    /// <summary>D5: gönderilmemiş kayıt uyarısı soruldu ve cevaplandı — sonraki kapanış yeniden sormaz.</summary>
    private bool _flushHandled;

    /// <summary>D5: "gönder ve kapat" sürüyor.</summary>
    private bool _flushing;

    public MainWindow(Views.AppRootView root)
    {
        InitializeComponent();
        RootHost.Content = root;
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

    protected override void OnClosing(CancelEventArgs e)
    {
        // Shell kurulmadan kapatılıyorsa (gate ekranındayız) MainShellViewModel'i
        // ÇÖZME: singleton olduğu için GetService onu KURAR — repo/servis
        // zinciri ayağa kalkar, üstelik sorulan şeyin (aktif çekiliş) cevabı
        // shell yokken zaten hep false. Pencere gate'lerden önce açıldığı için
        // bu yol gerçek: operatör giriş ekranında X'e basabiliyor.
        var root = RootHost.Content as Views.AppRootView;
        if (root is null || !root.IsShellMounted)
        {
            base.OnClosing(e);
            return;
        }

        // "Gönder ve kapat" sürüyor: kapatma istekleri yok sayılır — gönderim en geç bütçe
        // dolunca pencereyi kendisi kapatır (yarıda kesilen kapanış Host'u gönderimin altından söker).
        if (_flushing)
        {
            e.Cancel = true;
            return;
        }

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

        // Faz 0 (D5): gönderilmemiş kayıt varsa sor. Bir kez: "gönder ve kapat"tan sonraki Close()
        // yeniden sormaz. Giriş ekranından kapatma yukarıda erken döner ve sormaz — doğru, o yolda
        // gönderilecek oturum verisi yok.
        if (!_flushHandled && vm is not null)
        {
            var choice = AskAboutUnsentRecords(vm);
            if (choice == CloseSyncChoice.Cancel)
            {
                e.Cancel = true;
                return;
            }
            _flushHandled = true;
            if (choice == CloseSyncChoice.FlushThenClose)
            {
                e.Cancel = true;
                FlushThenClose();
                return;
            }
        }
        base.OnClosing(e);
    }

    /// <summary>D5: uyarının hatası kapanışı engellemez — OnClosing'den çıkan bir istisna pencereyi
    /// kapanamaz bırakırdı (global yakalayıcı işi "işlendi" sayar). Sayım hatasını VM zaten yalıtır;
    /// bu, diyaloğun kendisi içindir.</summary>
    private static CloseSyncChoice AskAboutUnsentRecords(MainShellViewModel vm)
    {
        try
        {
            return vm.ConfirmCloseWithUnsentRecords();
        }
        catch (Exception ex)
        {
            App.Host.Services.GetService<ILogger<MainWindow>>()?.LogWarning(ex,
                "Kapanış uyarısı gösterilemedi; sorulmadan kapatılıyor");
            return CloseSyncChoice.Close;
        }
    }

    /// <summary>
    /// "Gönder ve kapat" (D5): pencere kilitlenir, gönderim arayüz iş parçacığı DIŞINDA koşar
    /// (<see cref="Task.Run(Func{Task})"/> — müşteri gönderiminin ilk <c>await</c>'e kadarki kısmı tur
    /// kilidini ve <c>CustomerBusySet</c>'i eşzamanlı bekleyebilir; burada koşsaydı pencere donardı),
    /// en çok <see cref="SyncFlushService.CloseBudget"/> sürer, sonra pencere kapanır. Dispatcher
    /// hiçbir yerde engellenmez (<c>.Result</c>/<c>.Wait()</c> yok). Gönderim fırlatmaz; servis
    /// çözülemezse de pencere kapanır — kayıtlar kaybolmaz, sonraki açılışta gider.
    /// </summary>
    private void FlushThenClose()
    {
        _flushing = true;
        IsEnabled = false;
        Title = "OrderDeck — gönderiliyor…";

        Task flush;
        try
        {
            var service = App.Host.Services.GetRequiredService<SyncFlushService>();
            flush = Task.Run(() => service.FlushAsync(SyncFlushService.CloseBudget));
        }
        catch (Exception ex)
        {
            App.Host.Services.GetService<ILogger<MainWindow>>()?.LogWarning(ex,
                "Kapanış gönderimi başlatılamadı; pencere gönderimsiz kapanıyor");
            flush = Task.CompletedTask;
        }

        flush.ContinueWith(_ => Dispatcher.BeginInvoke(() =>
        {
            _flushing = false;
            Close();
        }), TaskScheduler.Default);
    }
}
