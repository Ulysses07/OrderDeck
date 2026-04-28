using System;
using System.IO;
using LiveDeck.Chat.Bridge;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Core;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Labeling;
using LiveDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LiveDeck.App;

public sealed class AppHost : IDisposable
{
    public IServiceProvider Services { get; }
    private readonly Serilog.ILogger _serilog;

    public AppHost()
    {
        AppPaths.EnsureDirectoriesExist();

        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsFolder, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(_serilog, dispose: false));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Settings + time
        services.AddSingleton(new SettingsStore(AppPaths.SettingsFile));
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());
        services.AddSingleton<IClock, SystemClock>();

        // Storage
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(AppPaths.DatabaseFile));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<CustomerRepository>();
        services.AddSingleton<LabelRepository>();

        // Domain
        services.AddSingleton<StreamSessionService>();
        services.AddSingleton<CustomerService>();
        services.AddSingleton<LabelService>();

        // Chat plumbing (unchanged from P1)
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 200));
        services.AddSingleton(sp => new ExtensionBridgeServer(
            sp.GetRequiredService<IChatBus>(),
            port: 4748,
            log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>()));
        services.AddSingleton<ChatBridgeIngestor>();

        // Overlay (unchanged from P1)
        services.AddSingleton(sp => new OverlayHost(
            sp.GetRequiredService<IChatBus>(),
            port: sp.GetRequiredService<AppSettings>().OverlayPort,
            log: sp.GetRequiredService<ILogger<OverlayHost>>()));

        // Printing
        services.AddSingleton(sp => new LabelPrinter(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<LabelPrinter>>()));

        // ViewModels + dialogs
        services.AddSingleton<ViewModels.MainShellViewModel>();
        services.AddTransient<ViewModels.StreamReportViewModel>();
        services.AddTransient<Views.StreamReportDialog>();
        services.AddTransient<ViewModels.StreamHistoryViewModel>();
        services.AddTransient<Views.StreamHistoryDialog>();
        services.AddTransient<ViewModels.BlacklistViewModel>();
        services.AddTransient<Views.BlacklistDialog>();

        Services = services.BuildServiceProvider();

        // Apply migrations once at boot
        Services.GetRequiredService<MigrationRunner>().Run();
    }

    public void Dispose()
    {
        if (Services is IDisposable disposable) disposable.Dispose();
        Serilog.Log.CloseAndFlush();
    }
}
