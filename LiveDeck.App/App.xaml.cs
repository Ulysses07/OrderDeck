using System.Globalization;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using LiveDeck.App.Formatting;
using LiveDeck.App.Views;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Services;
using LiveDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    private ChatBridgeIngestor? _ingestor;
    private OverlayHost? _overlay;
    private HeartbeatHostedService? _heartbeat;

    protected override void OnStartup(StartupEventArgs e)
    {
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

        Host = new AppHost();

        var logger = Host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("LiveDeck starting up");

        // Phase 4b: license bootstrap before showing main window
        var licenseService = Host.Services.GetRequiredService<LicenseService>();
        try
        {
            licenseService.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "License initialization failed");
        }

        if (licenseService.CurrentStatus == LicenseStatus.NoLicense)
        {
            var loginDlg = Host.Services.GetRequiredService<LoginDialog>();
            var ok = loginDlg.ShowDialog();
            if (ok != true)
            {
                Shutdown();
                return;
            }
        }

        _overlay  = Host.Services.GetRequiredService<OverlayHost>();
        _ingestor = Host.Services.GetRequiredService<ChatBridgeIngestor>();

        // Fire-and-forget — bridge & overlay should always be running
        _ = _overlay.StartAsync();
        _ = _ingestor.StartAsync(CancellationToken.None);

        // Heartbeat manual lifecycle (no IHost builder)
        _heartbeat = Host.Services.GetServices<IHostedService>()
            .OfType<HeartbeatHostedService>()
            .FirstOrDefault();
        _ = _heartbeat?.StartAsync(CancellationToken.None);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _heartbeat?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { _ingestor?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { _overlay?.StopAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
        Host.Dispose();
        base.OnExit(e);
    }
}
