using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using OrderDeck.App.Formatting;
using OrderDeck.App.Views;
using OrderDeck.App.Services;
using OrderDeck.Chat.Ingestors;
using OrderDeck.Core;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Services;
using OrderDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App;

public partial class App : Application
{
    public static AppHost Host { get; private set; } = null!;

    private ChatBridgeIngestor? _ingestor;
    private OverlayHost? _overlay;
    private HeartbeatHostedService? _heartbeat;
    private OrderDeck.App.Services.IntakeForm.IntakeFormSyncHostedService? _intakeSync;
    private OrderDeck.Chat.Ingestors.YouTube.YouTubeChatHostedService? _ytChat;

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

        // One-time legacy LiveDeck → OrderDeck data migration. Idempotent; runs before
        // AppHost (and therefore SQLite/SettingsStore) constructs path-bound services.
        try { AppDataMigrator.MigrateIfNeeded(); } catch { /* best-effort, fall through to fresh setup */ }

        Host = new AppHost();

        var logger = Host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("OrderDeck starting up");

        // Phase 4b: license bootstrap before showing main window.
        // Wrapped in Task.Run so the await chain runs off the WPF dispatcher —
        // otherwise GetResult() blocks the UI thread and any continuation that
        // captured the dispatcher SyncContext would deadlock.
        var licenseService = Host.Services.GetRequiredService<LicenseService>();
        try
        {
            Task.Run(() => licenseService.InitializeAsync()).GetAwaiter().GetResult();
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

        // Phase 5a — auto-prompt restore if local DB is empty AND cloud has backups
        try
        {
            var dbFile = AppPaths.DatabaseFile;
            var dbMissingOrTiny = !File.Exists(dbFile) || new FileInfo(dbFile).Length < 10240;
            if (dbMissingOrTiny)
            {
                var restoreService = Host.Services.GetRequiredService<RestoreService>();
                // Same Task.Run wrap — keep async I/O off the WPF dispatcher.
                var available = Task.Run(() => restoreService.ListAvailableAsync()).GetAwaiter().GetResult();
                if (available.Count > 0)
                {
                    var dlg = new Views.RestoreDialog(restoreService, available);
                    var ok = dlg.ShowDialog();
                    if (ok == true)
                    {
                        MessageBox.Show("Geri yükleme tamamlandı. Uygulama yeniden başlatılacak.",
                            "OrderDeck", MessageBoxButton.OK, MessageBoxImage.Information);
                        Shutdown();
                        return;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Restore auto-prompt failed (non-fatal)");
        }

        // Phase 5a — wire stream-end → cloud backup (fire-and-forget)
        var sessionService = Host.Services.GetRequiredService<StreamSessionService>();
        var backupService = Host.Services.GetRequiredService<BackupService>();
        sessionService.SessionEnded += (_, _) => backupService.QueueBackup("stream-end");

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

        // Phase 4f: intake form sync hosted service
        _intakeSync = Host.Services.GetServices<IHostedService>()
            .OfType<OrderDeck.App.Services.IntakeForm.IntakeFormSyncHostedService>()
            .FirstOrDefault();
        _ = _intakeSync?.StartAsync(CancellationToken.None);

        // Phase 5c: YouTube live-chat scraper hosted service. Idle until the
        // user fills in YouTubeChannelHandle in Settings.
        _ytChat = Host.Services.GetServices<IHostedService>()
            .OfType<OrderDeck.Chat.Ingestors.YouTube.YouTubeChatHostedService>()
            .FirstOrDefault();
        _ = _ytChat?.StartAsync(CancellationToken.None);

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // Same Task.Run wrap as OnStartup — keep StopAsync continuations off the
        // WPF dispatcher so GetResult() doesn't deadlock during shutdown.
        try { Task.Run(() => _ytChat?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _intakeSync?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _heartbeat?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _ingestor?.StopAsync(CancellationToken.None) ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        try { Task.Run(() => _overlay?.StopAsync() ?? Task.CompletedTask).GetAwaiter().GetResult(); } catch { /* ignore */ }
        Host.Dispose();
        base.OnExit(e);
    }
}
