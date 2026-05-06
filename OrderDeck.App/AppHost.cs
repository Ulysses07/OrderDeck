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
using OrderDeck.Licensing.Backup;
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
        // 500-message ring sized for the heaviest realistic load: ~30 msg/sec
        // peak across all four platforms (IG + TT + FB + YT) gives ~16 seconds
        // of scroll-back even at the worst spike, which is enough for the
        // auctioneer to catch a missed mention. 200 was sized for a single
        // platform Live and started dropping mid-product-shows.
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 500));
        // SpamFilter pulls its rule toggles from the live AppSettings via Func
        // so changes from the Settings dialog take effect immediately without
        // a service restart.
        services.AddSingleton<SpamFilter>(sp =>
            new SpamFilter(() => sp.GetRequiredService<AppSettings>().SpamFilter));
        services.AddSingleton(sp => new ExtensionBridgeServer(
            sp.GetRequiredService<IChatBus>(),
            port: 4748,
            log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>(),
            trialProbe: sp.GetRequiredService<LicenseService>(),
            spamFilter: sp.GetRequiredService<SpamFilter>()));
        services.AddSingleton<ChatBridgeIngestor>();

        // Phase 5c — YouTube Live chat scraper. Hosted service polls
        // youtube.com/{handle}/live and runs the InnerTube continuation API
        // when the channel goes live. Idle when AppSettings.YouTubeChannelHandle
        // is unset; honors trial-mode the same way the extension bridge does.
        services.AddHostedService(sp =>
            new OrderDeck.Chat.Ingestors.YouTube.YouTubeChatHostedService(
                () => sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<IChatBus>(),
                sp.GetRequiredService<ILoggerFactory>(),
                trialProbe: sp.GetRequiredService<LicenseService>(),
                spamFilter: sp.GetRequiredService<SpamFilter>()));

        // Phase 5d — YouTube OAuth + moderation. The data store sits in a
        // dedicated subfolder so we can wipe it on disconnect without
        // touching unrelated state.
        services.AddSingleton<Google.Apis.Util.Store.IDataStore>(_ =>
            new OrderDeck.Chat.YouTube.EncryptedYouTubeTokenStore(
                Path.Combine(AppPaths.DataFolder, "youtube-tokens")));
        services.AddSingleton<OrderDeck.Chat.YouTube.YouTubeOAuthService>(sp =>
            new OrderDeck.Chat.YouTube.YouTubeOAuthService(
                () => sp.GetRequiredService<AppSettings>(),
                sp.GetRequiredService<Google.Apis.Util.Store.IDataStore>(),
                sp.GetRequiredService<ILogger<OrderDeck.Chat.YouTube.YouTubeOAuthService>>()));
        services.AddSingleton<OrderDeck.Chat.YouTube.YouTubeModerationService>();

        // Overlay
        services.AddSingleton(sp => new OverlayHost(
            sp.GetRequiredService<IChatBus>(),
            sp.GetRequiredService<GiveawayService>(),
            port: sp.GetRequiredService<AppSettings>().OverlayPort,
            log: sp.GetRequiredService<ILogger<OverlayHost>>(),
            audioProvider: () =>
            {
                var s = sp.GetRequiredService<SettingsStore>().Load().GiveawayAnimation;
                return new GiveawayAudioSnapshot(s.Volume, s.MutedMode);
            }));

        // Printing
        services.AddSingleton<LabelPrinter>(sp => new LabelPrinter(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<LabelPrinter>>()));
        services.AddSingleton<ILabelPrinter>(sp => sp.GetRequiredService<LabelPrinter>());

        // Animation catalog client (Task 20)
        services.AddSingleton<Services.AnimationCatalogClient>(sp => new Services.AnimationCatalogClient(
            new System.Net.Http.HttpClient(),
            sp.GetRequiredService<AppSettings>().OverlayPort));

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
        // LicenseAuthHandler is a singleton so SetAuthToken on either the
        // LicenseApiClient or LoginService points at the same volatile token
        // field. Without singleton semantics each HttpClientFactory creation
        // would get a fresh handler with a stale token snapshot.
        services.AddSingleton<OrderDeck.Licensing.Api.LicenseAuthHandler>();
        services.AddHttpClient<LicenseApiClient>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            http.BaseAddress = new Uri(opt.ServerBaseUrl);
            http.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
        })
        .AddHttpMessageHandler(sp => sp.GetRequiredService<OrderDeck.Licensing.Api.LicenseAuthHandler>())
        .AddStandardResilienceHandler();  // retry on 5xx/network with exp. backoff; no retry on 4xx
        services.AddSingleton<LoginService>();
        services.AddSingleton<LicenseService>();
        // TokenRefresher must be a singleton so its single-flight gate is shared
        // across every HTTP path that triggers a 401 (BackupClient, LicenseApiClient).
        services.AddSingleton<TokenRefresher>();
        services.AddHostedService<HeartbeatHostedService>();

        // Phase 5a — cloud backup
        services.AddTransient<BearerAuthHandler>();
        services.AddHttpClient<IBackupClient, BackupClient>((sp, http) =>
        {
            var opt = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
            http.BaseAddress = new Uri(opt.ServerBaseUrl);
            http.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
        }).AddHttpMessageHandler<BearerAuthHandler>()
          .AddStandardResilienceHandler();  // same resilience profile for backup uploads
        services.AddSingleton<BackupService>(sp =>
            new BackupService(
                AppPaths.DatabaseFile,
                sp.GetRequiredService<IBackupClient>(),
                sp.GetRequiredService<ILogger<BackupService>>()));
        services.AddSingleton<RestoreService>(sp =>
            new RestoreService(
                AppPaths.DatabaseFile,
                sp.GetRequiredService<IBackupClient>(),
                sp.GetRequiredService<ILogger<RestoreService>>()));
        services.AddHostedService(sp =>
            new RestoreRecoveryService(
                AppPaths.DatabaseFile,
                sp.GetRequiredService<ILogger<RestoreRecoveryService>>()));

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

        // Wire LicenseApiClient → TokenRefresher.TryRefreshAsync as the on-401
        // callback. Done post-build because both services participate in a
        // construction cycle (LicenseApiClient ← TokenRefresher ← LicenseApiClient
        // via LicenseService) that DI can't resolve directly.
        var licenseApi = Services.GetRequiredService<LicenseApiClient>();
        var refresher = Services.GetRequiredService<TokenRefresher>();
        licenseApi.OnUnauthorized = ct => refresher.TryRefreshAsync(ct);

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

        // ORDERDECK_* takes precedence; LIVEDECK_* kept as backward-compat
        // for the rename (Phase 4b shipped under the LiveDeck name). Drop
        // the legacy fallback after one renewal cycle when no installs
        // could plausibly still have the old env vars set.
        var envBase = ReadEnv("ORDERDECK_LICENSE_BASE_URL", "LIVEDECK_LICENSE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envBase)) opt.ServerBaseUrl = envBase.Trim();

        var envTrialDays = ReadEnv("ORDERDECK_TRIAL_DURATION_DAYS", "LIVEDECK_TRIAL_DURATION_DAYS");
        if (int.TryParse(envTrialDays, out var d) && d >= 0) opt.TrialDurationDays = d;

        var envTrialPath = ReadEnv("ORDERDECK_TRIAL_PROGRAMDATA_PATH", "LIVEDECK_TRIAL_PROGRAMDATA_PATH");
        if (!string.IsNullOrWhiteSpace(envTrialPath)) opt.TrialProgramDataPath = envTrialPath.Trim();

        var envTrialKey = ReadEnv("ORDERDECK_TRIAL_REGISTRY_SUBKEY", "LIVEDECK_TRIAL_REGISTRY_SUBKEY");
        if (!string.IsNullOrWhiteSpace(envTrialKey)) opt.TrialRegistrySubKey = envTrialKey.Trim();

        return opt;
    }

    private static string? ReadEnv(string preferred, string legacyFallback)
    {
        var v = Environment.GetEnvironmentVariable(preferred);
        return !string.IsNullOrWhiteSpace(v) ? v : Environment.GetEnvironmentVariable(legacyFallback);
    }

    public void Dispose()
    {
        // Modern ServiceProvider only exposes async disposal when any registered
        // service implements IAsyncDisposable (e.g. ExtensionBridgeServer / Kestrel).
        // Calling sync Dispose() on it throws InvalidOperationException, so prefer
        // DisposeAsync and fall back to sync only when permitted.
        if (Services is IAsyncDisposable asyncDisposable)
        {
            System.Threading.Tasks.Task.Run(async () => await asyncDisposable.DisposeAsync())
                .GetAwaiter().GetResult();
        }
        else if (Services is IDisposable disposable)
        {
            disposable.Dispose();
        }
        Serilog.Log.CloseAndFlush();
    }
}
