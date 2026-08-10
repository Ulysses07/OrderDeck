using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using Microsoft.Win32;
using Velopack;
using OrderDeck.App.Formatting;
using OrderDeck.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App;

public partial class App : Application
{
    /// <summary>Velopack feed (delta/otomatik güncelleme). Statik dosya host'u:
    /// Setup.exe + full/delta .nupkg + releases.win.json burada durur (Caddy serve).</summary>
    public const string VelopackFeedUrl = "https://orderdeckapp.com/downloads/velopack";

    /// <summary>
    /// Özel WPF entry point. VelopackApp.Build().Run() install/update/uninstall
    /// hook'larını WPF'ten ÖNCE işler ve gerektiğinde hızlıca çıkar (WPF yükü
    /// yüklenmeden). Normal açılışta no-op'tur, sonra WPF uygulaması başlar.
    /// WithFirstRun: ilk kurulumda WebView2 evergreen runtime yoksa sessiz kurar.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build()
            .OnAfterInstallFastCallback(_ => TryInstallWebView2())
            .Run();

        _startedFromEntryPoint = true;

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    /// <summary>
    /// Uygulama GERÇEKTEN buradan mı başlatıldı? Tema testleri App'i yalnız
    /// App.xaml'in kaynak sözlüğü için örnekliyor (Application süreç başına
    /// tek olduğu ve BAML kökü bu tipi istediği için başka türlüsü mümkün
    /// değil) ve hiç <c>Run()</c> çağırmıyor. Ama bir test dispatcher
    /// kuyruğunu pompalarsa (ThemeTestHost.Pump) WPF'in bekleyen açılış işi
    /// koşuyor ve <see cref="OnStartup"/> tetikleniyor: lisans sunucusuna
    /// gerçek istekler, hosted service'ler, modal sihirbaz. CI'da bu 3
    /// dakikalık kilitlenmeye ve test host çökmesine yol açtı — hangi WPF
    /// testinin App'i ilk yarattığına bağlı olduğu için flaky'ydi.
    /// </summary>
    private static bool _startedFromEntryPoint;

    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Gerçek giriş noktasından gelinmediyse hiçbir şey başlatılmaz;
        // gerekçe _startedFromEntryPoint'in üstünde.
        if (!_startedFromEntryPoint) return;

        // Wire global exception handlers FIRST so they catch any crash that
        // happens during the rest of OnStartup (e.g. corrupt settings file
        // throwing during AppHost construction). Operator's mid-stream WPF
        // crash with no MessageBox = blank desktop + lost session — these
        // handlers turn that into a recoverable error dialog.
        DispatcherUnhandledException += App_DispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += AppDomain_UnhandledException;
        TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

        // Lock culture to tr-TR so number/date/currency formatting is consistent regardless
        // of the OS locale. WPF Binding StringFormat and C# default formats both pick this up.
        var tr = TrFormats.TR;
        Thread.CurrentThread.CurrentCulture = tr;
        Thread.CurrentThread.CurrentUICulture = tr;
        CultureInfo.DefaultThreadCurrentCulture = tr;
        CultureInfo.DefaultThreadCurrentUICulture = tr;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(tr.IetfLanguageTag)));

        // One-time legacy LiveDeck → OrderDeck data migration. Idempotent; runs before
        // AppHost (and therefore SQLite/SettingsStore) constructs path-bound services.
        try { AppDataMigrator.MigrateIfNeeded(); } catch { /* best-effort, fall through to fresh setup */ }

        Host = new AppHost();

        var logger = Host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("OrderDeck starting up");

        // Otomatik güncelleme (Velopack) — non-blocking. Feed'den delta iner,
        // kullanıcı onaylayınca yeniden başlatıp uygular. Sadece Velopack ile
        // kurulmuş sürümde çalışır (dev/dev-run'da IsInstalled=false → no-op).
        // Best-effort: hata → sessiz geç, akışı bozma.
        _ = Task.Run(async () =>
        {
            try
            {
                var mgr = new UpdateManager(VelopackFeedUrl);
                if (!mgr.IsInstalled) return;

                var newVersion = await mgr.CheckForUpdatesAsync();
                if (newVersion is null) return;

                await mgr.DownloadUpdatesAsync(newVersion);

                Dispatcher.Invoke(() =>
                {
                    var ver = newVersion.TargetFullRelease.Version;
                    var choice = MessageBox.Show(
                        $"Yeni sürüm hazır: {ver}\n\nŞimdi yeniden başlatıp güncellensin mi?\n"
                        + "(Hayır dersen bir sonraki açılışta uygulanır.)",
                        "OrderDeck — Güncelleme", MessageBoxButton.YesNo, MessageBoxImage.Information);
                    if (choice == MessageBoxResult.Yes)
                        mgr.ApplyUpdatesAndRestart(newVersion);
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Velopack update check failed (non-fatal)");
            }
        });

        base.OnStartup(e);

        // AÇILIŞ SIRASI TERSİNE DÖNDÜ (Faz 4a). Pencere artık İLK açılıyor;
        // lisans/geri yükleme/sihirbaz kontrolleri onun içinde tam ekran
        // gate olarak koşuyor. Eskiden üç ShowDialog() pencereden önce
        // gelirdi ve operatör görev çubuğunda ikonsuz, alt+tab'de bulunamayan
        // pencerelerle uğraşırdı.
        var root = Host.Services.GetRequiredService<Views.AppRootView>();
        var main = new MainWindow(root);
        MainWindow = main;
        main.Show();

        // Bilerek await EDİLMİYOR: OnStartup'ın dönmesi gerekiyor ki
        // dispatcher mesaj döngüsü koşsun ve gate'ler çizilsin.
        _ = RunStartupAsync(logger);
    }

    private static async Task RunStartupAsync(ILogger<App> logger)
    {
        try
        {
            await Host.Services.GetRequiredService<Startup.StartupFlow>().RunAsync();
        }
        catch (Exception ex)
        {
            // Buraya düşmek akışın kendi try/catch'lerinin kaçırdığı bir
            // hata demek; sessizce boş gate ekranında kalmaktansa söyle.
            logger.LogError(ex, "Startup flow failed");
            MessageBox.Show(
                $"OrderDeck açılamadı:\n\n{ex.Message}",
                "OrderDeck — Başlatma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            Current.Shutdown();
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // WebView2 evergreen runtime — Velopack ilk kurulumda çağırır
    // (Inno Setup'ın [Run] bootstrapper adımının karşılığı). Win10 22H2 /
    // Win11 önyüklü gelir; eski makineler için sessiz kurulum fallback'i.
    // ──────────────────────────────────────────────────────────────────

    private static void TryInstallWebView2()
    {
        try
        {
            if (IsWebView2Installed()) return;
            var bootstrap = Path.Combine(AppContext.BaseDirectory, "MicrosoftEdgeWebview2Setup.exe");
            if (!File.Exists(bootstrap)) return;
            var psi = new ProcessStartInfo(bootstrap, "/silent /install") { UseShellExecute = false };
            Process.Start(psi)?.WaitForExit();
        }
        catch { /* best-effort; app WebView2 yokken yine de açılmayı dener */ }
    }

    private static bool IsWebView2Installed()
    {
        // EdgeUpdate client key (system HKLM + per-user HKCU). pv boş/0.0.0.0 = yok.
        const string clients = @"Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";
        return HasPv(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\" + clients)
            || HasPv(Registry.CurrentUser, @"Software\" + clients);
    }

    private static bool HasPv(RegistryKey root, string subKey)
    {
        using var key = root.OpenSubKey(subKey);
        var pv = key?.GetValue("pv") as string;
        return !string.IsNullOrEmpty(pv) && pv != "0.0.0.0";
    }

    // ──────────────────────────────────────────────────────────────────
    // Global crash handlers
    // ──────────────────────────────────────────────────────────────────

    private void App_DispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var logger = Host?.Services.GetService<ILogger<App>>();
        logger?.LogError(e.Exception, "Unhandled UI dispatcher exception");

        // e.Handled = true keeps the app alive so the operator's mid-stream
        // session/queue state is preserved. Critical UI-thread exceptions
        // (XAML binding null refs, command click null derefs, etc.) used to
        // tear the process down silently.
        MessageBox.Show(
            "Uygulamada beklenmeyen bir hata oluştu — uygulama kapanmadı, çalışmaya devam edebilirsin.\n\n" +
            $"Hata: {e.Exception.GetType().Name}\n{e.Exception.Message}\n\n" +
            "Detaylı log AppData/OrderDeck/logs altında. Sorun tekrar ederse logları paylaş.",
            "OrderDeck — Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    private void AppDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        // Non-UI thread crash. By the time we're here the CLR may already be
        // tearing down the process (IsTerminating=true) so MessageBox is
        // unreliable — just persist what we can.
        var logger = Host?.Services.GetService<ILogger<App>>();
        logger?.LogCritical(
            e.ExceptionObject as Exception,
            "Unhandled domain exception (terminating={IsTerminating})",
            e.IsTerminating);
    }

    private void TaskScheduler_UnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        // Fire-and-forget Task that threw and was never awaited. Default in
        // modern .NET is non-fatal but logging it surfaces background issues
        // (failed backups, broken WS broadcasts) instead of hiding them.
        var logger = Host?.Services.GetService<ILogger<App>>();
        logger?.LogWarning(e.Exception, "Unobserved task exception");
        e.SetObserved();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Arka plan servislerini başlatan sınıf durdurmayı da yapıyor
        // (Faz 4a); Task.Run sarmalayıcıları oraya taşındı.
        Host.Services.GetRequiredService<Startup.WpfStartupEnvironment>()
            .StopBackgroundServices();
        Host.Dispose();
        base.OnExit(e);
    }
}
