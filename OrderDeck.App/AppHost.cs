using System;
using System.IO;
using OrderDeck.App.Services;
using OrderDeck.App.Services.IntakeForm;
using OrderDeck.Chat.Bridge;
using OrderDeck.Chat.Ingestors;
using OrderDeck.Core;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Labeling;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Services;
using OrderDeck.Licensing.Storage;
using OrderDeck.Licensing.Trial;
using OrderDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;
using Serilog.Extensions.Logging;

namespace OrderDeck.App;

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

        // Giveaway (Phase 2b)
        services.AddSingleton<GiveawayRepository>();
        services.AddSingleton<GiveawayDrawer>();
        services.AddSingleton<GiveawayService>();
        services.AddSingleton<ViewModels.GiveawayBannerViewModel>();

        // Chat plumbing
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 200));
        services.AddSingleton(sp => new ExtensionBridgeServer(
            sp.GetRequiredService<IChatBus>(),
            port: 4748,
            log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>(),
            trialProbe: sp.GetRequiredService<LicenseService>()));
        services.AddSingleton<ChatBridgeIngestor>();

        // Overlay
        services.AddSingleton(sp => new OverlayHost(
            sp.GetRequiredService<IChatBus>(),
            sp.GetRequiredService<GiveawayService>(),
            port: sp.GetRequiredService<AppSettings>().OverlayPort,
            log: sp.GetRequiredService<ILogger<OverlayHost>>()));

        // Printing
        services.AddSingleton<LabelPrinter>(sp => new LabelPrinter(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<LabelPrinter>>()));
        services.AddSingleton<ILabelPrinter>(sp => sp.GetRequiredService<LabelPrinter>());

        // ViewModels
        services.AddSingleton<ViewModels.MainShellViewModel>();
        services.AddTransient<ViewModels.StreamReportViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.StreamHistoryViewModel>();
        services.AddTransient<ViewModels.BlacklistViewModel>();

        // Dialogs (transient — fresh instance per open)
        services.AddTransient<Views.StreamReportDialog>();
        services.AddTransient<Views.SettingsDialog>();
        services.AddTransient<Views.StreamHistoryDialog>();
        services.AddTransient<Views.BlacklistDialog>();

        // Customer center (Phase 3a)
        services.AddTransient<ViewModels.CustomerDetailViewModel>();
        services.AddTransient<ViewModels.CustomerSearchViewModel>();
        services.AddTransient<Views.CustomerDetailDialog>();
        services.AddTransient<Views.CustomerSearchDialog>();

        // Phase 4g — payment infrastructure
        services.AddSingleton<WhatsAppMessageBuilder>();
        services.AddSingleton<IUrlLauncher, ProcessUrlLauncher>();
        services.AddSingleton<PaymentRequestService>();
        services.AddSingleton<IDialogService, WpfDialogService>();

        // Shortcuts (Phase 3b-1)
        services.AddSingleton<OrderDeck.Core.Shortcuts.ShortcutRegistry>();
        services.AddSingleton<OrderDeck.App.Shortcuts.ShortcutBinder>();
        services.AddTransient<ViewModels.ShortcutsTabViewModel>();
        services.AddTransient<Views.ShortcutHelpDialog>();

        // Licensing (Phase 4b)
        var licensingOptions = BuildLicensingOptions();
        services.AddSingleton(Options.Create(licensingOptions));
        services.AddSingleton<IHardwareIdProvider, HardwareIdProvider>();
        services.AddSingleton<EncryptedStore>();
        services.AddSingleton(sp => new AuthStore(
            sp.GetRequiredService<EncryptedStore>(), AppPaths.AuthFile));
        services.AddSingleton(sp => new LicenseStateStore(
            sp.GetRequiredService<EncryptedStore>(), AppPaths.LicenseFile));
        services.AddHttpClient<LicenseApiClient>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            http.BaseAddress = new Uri(opt.ServerBaseUrl);
            http.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
        });
        services.AddSingleton<LoginService>();
        services.AddSingleton<LicenseService>();
        services.AddHostedService<HeartbeatHostedService>();

        // Licensing — Trial (Phase 4c)
        services.AddSingleton<HkcuTrialStorage>();
        services.AddSingleton<ProgramDataTrialStorage>(sp =>
        {
            var opt = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            return new ProgramDataTrialStorage(opt.TrialProgramDataPath,
                sp.GetRequiredService<ILogger<ProgramDataTrialStorage>>());
        });
        services.AddSingleton<LocalAppDataTrialStorage>(sp =>
            new LocalAppDataTrialStorage(
                sp.GetRequiredService<EncryptedStore>(),
                AppPaths.TrialFile));
        services.AddSingleton<ITrialStorage>(sp => new CompositeTrialStorage(
            sp.GetRequiredService<HkcuTrialStorage>(),
            sp.GetRequiredService<ProgramDataTrialStorage>(),
            sp.GetRequiredService<LocalAppDataTrialStorage>(),
            sp.GetRequiredService<ILogger<CompositeTrialStorage>>()));
        services.AddSingleton<TrialService>(sp => new TrialService(
            sp.GetRequiredService<ITrialStorage>(),
            sp.GetRequiredService<IHardwareIdProvider>(),
            sp.GetRequiredService<IOptions<LicensingOptions>>(),
            () => DateTimeOffset.UtcNow,
            sp.GetRequiredService<ILogger<TrialService>>()));

        // Intake form sync (Phase 4f)
        services.AddSingleton<IntakeFormSyncService>(sp => new IntakeFormSyncService(
            sp.GetRequiredService<LicenseApiClient>(),
            sp.GetRequiredService<CustomerRepository>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<IntakeFormSyncService>>()));
        services.AddHostedService<IntakeFormSyncHostedService>();

        // Intake form settings (Phase 4f Task 10)
        services.AddTransient<ViewModels.IntakeFormSettingsViewModel>();

        // Licensing dialogs (Phase 4b)
        services.AddTransient<ViewModels.LoginDialogViewModel>();
        services.AddTransient<Views.LoginDialog>();
        services.AddTransient<ViewModels.AccountDialogViewModel>();
        services.AddTransient<Views.AccountDialog>();

        Services = services.BuildServiceProvider();

        // Apply migrations once at boot
        Services.GetRequiredService<MigrationRunner>().Run();

        // If a previous run crashed mid-giveaway, mark phantom rows cancelled so the next
        // session starts clean (otherwise GetActiveBySession would surface stale rows).
        var orphans = Services.GetRequiredService<GiveawayRepository>()
            .CancelAllOrphaned(Services.GetRequiredService<IClock>().UnixNow());
        if (orphans > 0)
            Services.GetRequiredService<ILogger<AppHost>>()
                .LogWarning("Cancelled {Count} orphaned giveaway(s) from prior session", orphans);
    }

    private static LicensingOptions BuildLicensingOptions()
    {
        var opt = new LicensingOptions();
        var envBase = Environment.GetEnvironmentVariable("LIVEDECK_LICENSE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envBase)) opt.ServerBaseUrl = envBase.Trim();

        var envTrialDays = Environment.GetEnvironmentVariable("LIVEDECK_TRIAL_DURATION_DAYS");
        if (int.TryParse(envTrialDays, out var d) && d >= 0) opt.TrialDurationDays = d;

        var envTrialPath = Environment.GetEnvironmentVariable("LIVEDECK_TRIAL_PROGRAMDATA_PATH");
        if (!string.IsNullOrWhiteSpace(envTrialPath)) opt.TrialProgramDataPath = envTrialPath.Trim();

        var envTrialKey = Environment.GetEnvironmentVariable("LIVEDECK_TRIAL_REGISTRY_SUBKEY");
        if (!string.IsNullOrWhiteSpace(envTrialKey)) opt.TrialRegistrySubKey = envTrialKey.Trim();

        return opt;
    }

    public void Dispose()
    {
        if (Services is IDisposable disposable) disposable.Dispose();
        Serilog.Log.CloseAndFlush();
    }
}
