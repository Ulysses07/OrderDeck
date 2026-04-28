using System.Threading;
using System.Windows;
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
