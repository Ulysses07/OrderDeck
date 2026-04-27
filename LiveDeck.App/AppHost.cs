using System;
using System.IO;
using LiveDeck.Chat.Bridge;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Core;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sales.Pipeline;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
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

        // Settings
        services.AddSingleton(new SettingsStore(AppPaths.SettingsFile));
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());

        // Time
        services.AddSingleton<IClock, SystemClock>();

        // Storage
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(AppPaths.DatabaseFile));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<ActiveCodeRepository>();
        services.AddSingleton<OrderRepository>();
        services.AddSingleton<CustomerRepository>();

        // Domain services
        services.AddSingleton<StreamSessionService>();
        services.AddSingleton<ActiveCodeService>();
        services.AddSingleton<CustomerService>();

        // Capture pipeline
        services.AddSingleton<MessageNormalizer>();
        services.AddSingleton<CodeMatcher>();
        services.AddSingleton<VariantExtractor>();
        services.AddSingleton<QuantityExtractor>();
        services.AddSingleton<IntentScorer>();
        services.AddSingleton<ConfidenceScorer>();
        services.AddSingleton<OrderCaptureEngine>();
        services.AddSingleton<OrderService>();

        // Chat plumbing
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 200));
        services.AddSingleton(sp =>
        {
            return new ExtensionBridgeServer(
                sp.GetRequiredService<IChatBus>(),
                port: 4748,
                log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>());
        });
        services.AddSingleton<InstagramIngestor>();

        // Overlay
        services.AddSingleton(sp =>
        {
            var settings = sp.GetRequiredService<AppSettings>();
            return new OverlayHost(
                sp.GetRequiredService<IChatBus>(),
                port: settings.OverlayPort,
                log: sp.GetRequiredService<ILogger<OverlayHost>>());
        });

        // ViewModels
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.ActiveCodesViewModel>();
        services.AddSingleton<ViewModels.OrderQueueViewModel>();
        services.AddSingleton<ViewModels.ChatPanelViewModel>();

        services.AddSingleton<Services.ClipboardService>();
        services.AddSingleton<Services.HotkeyService>();
        services.AddSingleton<Services.EtiketIntegration>();
        services.AddSingleton<LiveDeck.Labeling.ClipboardLabelFormatter>();

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
