using System.Windows;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        Host = new AppHost();

        var logger = Host.Services.GetService(typeof(ILogger<App>)) as ILogger<App>;
        logger?.LogInformation("LiveDeck starting up");

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Host.Dispose();
        base.OnExit(e);
    }
}
