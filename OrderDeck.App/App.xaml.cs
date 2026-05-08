using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
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

        // Stream session recovery — if the previous run crashed mid-broadcast
        // (or the operator closed the app without ending the session), the
        // StreamSession row is still active in DB. Without this prompt the
        // operator would silently start a fresh session on top of orphaned
        // data and the active giveaway / queue state would never resume.
        try
        {
            var activeSession = sessionService.GetActive();
            if (activeSession is not null)
            {
                var startedLocal = DateTimeOffset.FromUnixTimeSeconds(activeSession.StartedAt)
                    .LocalDateTime.ToString("dd MMM HH:mm", tr);
                var titleLine = string.IsNullOrWhiteSpace(activeSession.Title)
                    ? string.Empty
                    : $"Başlık: {activeSession.Title}\n";
                var msg = "Önceki yayın bitirilmemiş gözüküyor:\n\n" +
                          $"Başlangıç: {startedLocal}\n" +
                          titleLine +
                          "\nDevam ettir mi yoksa bitirip yeni yayına başla?\n\n" +
                          "Evet  = devam et (önceki katılımcılar/sipariş kuyruğu yüklenir)\n" +
                          "Hayır = önceki yayını kapat (rapor için DB'de kalır)\n" +
                          "İptal = uygulamayı kapat";
                var choice = MessageBox.Show(msg, "OrderDeck — Yayın Kurtarma",
                    MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel)
                {
                    Shutdown();
                    return;
                }
                if (choice == MessageBoxResult.No)
                {
                    sessionService.End(activeSession.Id);
                }
                // Yes = leave active; MainShellViewModel.ReloadQueueFromActiveSession
                // picks it up.
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Session recovery prompt failed (non-fatal)");
        }

        _overlay  = Host.Services.GetRequiredService<OverlayHost>();
        _ingestor = Host.Services.GetRequiredService<ChatBridgeIngestor>();

        // Awaited (was fire-and-forget) so port-in-use errors surface to the
        // operator instead of being swallowed silently — pinning a stale
        // OrderDeck instance to 4747 used to mean OBS overlays returned 404
        // for 10 minutes of debugging before anyone realized.
        try
        {
            Task.Run(() => _overlay.StartAsync()).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            logger.LogError(ex, "Overlay port {Port} already in use", _overlay.Port);
            MessageBox.Show(
                $"Overlay portu ({_overlay.Port}) zaten kullanımda.\n\n" +
                "Büyük ihtimalle başka bir OrderDeck çalışıyor. Görev Yöneticisi'nden " +
                "OrderDeck.App'i kapatıp tekrar dene.\n\n" +
                $"Detay: {ex.Message}",
                "OrderDeck — Port Çakışması", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Overlay startup failed");
            MessageBox.Show(
                $"Overlay başlatılamadı:\n\n{ex.Message}\n\nUygulama kapatılıyor.",
                "OrderDeck — Başlatma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }

        try
        {
            Task.Run(() => _ingestor.StartAsync(CancellationToken.None)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            logger.LogError(ex, "Bridge port 4748 already in use");
            MessageBox.Show(
                "Chrome eklenti köprüsü portu (4748) zaten kullanımda.\n\n" +
                "Büyük ihtimalle başka bir OrderDeck çalışıyor. Görev Yöneticisi'nden " +
                "kapatıp tekrar dene.\n\n" +
                $"Detay: {ex.Message}",
                "OrderDeck — Port Çakışması", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
            return;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Bridge startup failed");
            MessageBox.Show(
                $"Chrome eklenti köprüsü başlatılamadı:\n\n{ex.Message}\n\n" +
                "Uygulama açık kalıyor — Instagram/TikTok chat çalışmayacak.",
                "OrderDeck — Köprü Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Continue without bridge — YouTube + manual flows still work.
        }

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

    /// <summary>Maps the messy collection of "port already bound" exception
    /// shapes Kestrel/HttpListener can throw into a single boolean. Kestrel
    /// wraps the underlying SocketException in IOException; HttpListener
    /// throws HttpListenerException with HRESULT 0x80004005.</summary>
    private static bool IsPortInUse(Exception ex)
    {
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current is SocketException se && se.SocketErrorCode == SocketError.AddressAlreadyInUse)
                return true;
            if (current is HttpListenerException hle &&
                (hle.ErrorCode == 32 || hle.ErrorCode == 183 || hle.ErrorCode == unchecked((int)0x80004005)))
                return true;
            if (current is IOException io &&
                (io.Message.Contains("address", StringComparison.OrdinalIgnoreCase) ||
                 io.Message.Contains("in use", StringComparison.OrdinalIgnoreCase) ||
                 io.Message.Contains("conflicts", StringComparison.OrdinalIgnoreCase)))
                return true;
        }
        return false;
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
