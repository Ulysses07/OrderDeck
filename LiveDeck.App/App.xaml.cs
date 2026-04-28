using System.Globalization;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using LiveDeck.App.Formatting;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    private ChatBridgeIngestor? _ingestor;
    private OverlayHost? _overlay;

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

        _overlay  = Host.Services.GetRequiredService<OverlayHost>();
        _ingestor = Host.Services.GetRequiredService<ChatBridgeIngestor>();

        // Fire-and-forget — bridge & overlay should always be running
        _ = _overlay.StartAsync();
        _ = _ingestor.StartAsync(CancellationToken.None);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _ingestor?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { _overlay?.StopAsync().GetAwaiter().GetResult(); } catch { /* ignore */ }
        Host.Dispose();
        base.OnExit(e);
    }
}
