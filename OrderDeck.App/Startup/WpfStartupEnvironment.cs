using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OrderDeck.App.Services;
using OrderDeck.App.Shortcuts;
using OrderDeck.Chat.Ingestors;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Settings;
using OrderDeck.Licensing;
using OrderDeck.Licensing.Backup;
using OrderDeck.Licensing.Services;
using OrderDeck.Overlay;

namespace OrderDeck.App.Startup;

/// <summary>
/// <see cref="IStartupEnvironment"/>'ın gerçek uygulaması. Arka plan
/// servislerinin ömrü (başlat + durdur) buraya taşındı: eskiden App'in
/// dört alanı tutuyordu, başlatma OnStartup'ta durdurma OnExit'teydi.
/// İkisi aynı sınıfta olunca "başlattığını durdur" gözle görülür oluyor.
/// </summary>
public sealed class WpfStartupEnvironment : IStartupEnvironment
{
    private readonly LicenseService _license;
    private readonly RestoreService _restore;
    private readonly SettingsStore _settings;
    private readonly StreamSessionService _sessions;
    private readonly BackupService _backups;
    private readonly BootDatabaseState _bootDb;
    private readonly Views.AppRootView _root;
    private readonly IServiceProvider _services;
    private readonly ILogger<WpfStartupEnvironment> _log;

    // Overlay ve köprü artık ALAN DEĞİL: durdurmayı defter üstlendiği için
    // ikisine de yalnız başlatma metodunun içinde ihtiyaç var.
    private readonly HostedServiceLifecycle _lifecycle;

    public WpfStartupEnvironment(
        LicenseService license,
        RestoreService restore,
        SettingsStore settings,
        StreamSessionService sessions,
        BackupService backups,
        BootDatabaseState bootDb,
        Views.AppRootView root,
        IServiceProvider services,
        ILogger<WpfStartupEnvironment> log)
    {
        _license = license;
        _restore = restore;
        _settings = settings;
        _sessions = sessions;
        _backups = backups;
        _bootDb = bootDb;
        _root = root;
        _services = services;
        _log = log;
        _lifecycle = new HostedServiceLifecycle(log);

        // Yayın bitti → bulut yedeği (fire-and-forget). BURADA, ctor'da:
        // akış oturum kurtarmada "Yayını bitir" seçilirse EndSession'ı
        // arka plan servisleri kalkmadan ÖNCE çağırıyor ve
        // StreamSessionService.End() olayı senkron yükseltiyor
        // (OrderDeck.Core/Sessions/StreamSessionService.cs:34-39). Kablolama
        // daha geç bir noktada olsaydı o yoldaki yedek sessizce düşerdi.
        _sessions.SessionEnded += (_, _) => _backups.QueueBackup("stream-end");
    }

    // Task.Run sarmalayıcısı DÜŞTÜ: eskiden GetAwaiter().GetResult() UI
    // thread'ini bloklamasın diye gerekiyordu, artık gerçekten await
    // ediliyor.
    public Task InitializeLicenseAsync() => _license.InitializeAsync();

    public bool HasLicense => _license.CurrentStatus != LicenseStatus.NoLicense;

    /// <summary>
    /// DOSYAYA BAKMIYOR, bilerek. Bu metot eskiden
    /// <c>AppPaths.DatabaseFile</c>'ı stat'liyordu ve hiçbir zaman true
    /// dönmedi: <c>AppHost</c> ctor'u bu sınıftan da akıştan da önce koşuyor
    /// ve sonunda <c>MigrationRunner.Run()</c> dosyayı yaratıp şemayı
    /// kuruyor, boş şema bile 10 KB eşiğinin çok üstünde kalıyor. Yani
    /// "bilgisayarını değiştiren operatöre bulut yedeğini öner" yolu ölü
    /// koddu. Ölçüm artık <see cref="BootDatabaseState.Capture"/> ile
    /// migration'dan önce yapılıyor; burası o yakalanmış cevabı okuyor.
    /// </summary>
    public bool IsDatabaseMissingOrTiny() => _bootDb.IsMissingOrTiny;

    public async Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync() =>
        await _restore.ListAvailableAsync();

    public bool HasCompletedFirstRun() => _settings.Load().HasCompletedFirstRun;

    public StreamSession? GetActiveSession() => _sessions.GetActive();

    public void EndSession(string sessionId) => _sessions.End(sessionId);

    public async Task<bool> StartBackgroundServicesAsync()
    {
        var overlay = _services.GetRequiredService<OverlayHost>();
        var ingestor = _services.GetRequiredService<ChatBridgeIngestor>();

        // Deftere başlatmadan ÖNCE yazılıyorlar: başlatma yarıda patlarsa
        // (port çakışması dalı dahil) geriye yarım dinleyici kalmış olabilir
        // ve OnExit yine de durdurmayı denemek zorunda.
        _lifecycle.Track(nameof(OverlayHost), _ => overlay.StopAsync());

        try
        {
            await overlay.StartAsync();
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            _log.LogError(ex, "All overlay port candidates already in use");
            MessageBox.Show(
                "Overlay portlarının tümü kullanımda (4747, 4757-4760).\n\n" +
                "Büyük ihtimalle başka bir OrderDeck çalışıyor. Görev Yöneticisi'nden " +
                "OrderDeck.App'i kapatıp tekrar dene.\n\n" +
                $"Detay: {ex.Message}",
                "OrderDeck — Port Çakışması", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestShutdown();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Overlay startup failed");
            MessageBox.Show(
                $"Overlay başlatılamadı:\n\n{ex.Message}\n\nUygulama kapatılıyor.",
                "OrderDeck — Başlatma Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestShutdown();
            return false;
        }

        if (overlay.FellBackFromPreferredPort)
        {
            _log.LogWarning("Overlay running on fallback port {Port} (4747 was busy)", overlay.Port);
            MessageBox.Show(
                $"Overlay portu 4747 başka uygulama kullanıyor; otomatik olarak {overlay.Port}'e geçildi.\n\n" +
                "OBS Browser Source URL'lerini güncelle:\n" +
                $"  http://localhost:{overlay.Port}/overlay/chat\n" +
                $"  http://localhost:{overlay.Port}/overlay/giveaway\n\n" +
                "Bu durum genelde başka bir OrderDeck instance veya farklı bir uygulama " +
                "tarafından 4747'nin tutulduğunda olur.",
                "OrderDeck — Yedek Port Kullanılıyor",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        _lifecycle.Track(nameof(ChatBridgeIngestor), ct => ingestor.StopAsync(ct));

        try
        {
            await ingestor.StartAsync(CancellationToken.None);
        }
        catch (Exception ex) when (IsPortInUse(ex))
        {
            _log.LogError(ex, "Bridge port 4748 already in use");
            MessageBox.Show(
                "Chrome eklenti köprüsü portu (4748) zaten kullanımda.\n\n" +
                "Büyük ihtimalle başka bir OrderDeck çalışıyor. Görev Yöneticisi'nden " +
                "kapatıp tekrar dene.\n\n" +
                $"Detay: {ex.Message}",
                "OrderDeck — Port Çakışması", MessageBoxButton.OK, MessageBoxImage.Error);
            RequestShutdown();
            return false;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Bridge startup failed");
            MessageBox.Show(
                $"Chrome eklenti köprüsü başlatılamadı:\n\n{ex.Message}\n\n" +
                "Uygulama açık kalıyor — Instagram/TikTok chat çalışmayacak.",
                "OrderDeck — Köprü Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            // Köprüsüz devam — YouTube ve elle akışlar çalışıyor.
        }

        // WPF'te IHost builder yok; hosted service'ler elle başlatılmazsa ölü
        // örnek kalıyor (CLAUDE.md kuralı, PR #89). Sıra korunuyor: heartbeat
        // ve intake sync eskiden de önce geliyordu, kapanış da bunun tersi
        // olmak zorunda.
        var registered = _services.GetServices<IHostedService>().ToList();
        var ordered = new List<IHostedService>();
        AddFirst<HeartbeatHostedService>();
        AddFirst<Services.IntakeForm.IntakeFormSyncHostedService>();

        var alreadyOrdered = new HashSet<IHostedService>(ordered, ReferenceEqualityComparer.Instance);
        ordered.AddRange(registered.Where(s => !alreadyOrdered.Contains(s)));

        await _lifecycle.StartAllAsync(ordered);

        return true;

        void AddFirst<T>() where T : IHostedService
        {
            var svc = registered.OfType<T>().FirstOrDefault();
            if (svc is not null) ordered.Add(svc);
        }
    }

    /// <summary>
    /// App.OnExit'ten çağrılır. Task.Run sarmalayıcısı korunuyor: kapanış
    /// senkron ve UI thread'inde, doğrudan GetResult() dispatcher'la
    /// kilitlenebilir.
    ///
    /// Artık dört sabit alanı değil, <see cref="HostedServiceLifecycle"/>'ın
    /// deftere yazdığı HER ŞEYİ ters sırayla durduruyor.
    /// </summary>
    public void StopBackgroundServices()
    {
        try { Task.Run(() => _lifecycle.StopAllAsync()).GetAwaiter().GetResult(); }
        catch (Exception ex) { _log.LogWarning(ex, "Arka plan servisleri durdurulurken hata"); }
    }

    public void MountShell()
    {
        _root.MountShell(new Views.MainShellView());

        // Kısayollar eskiden MainWindow.Loaded'da bağlanıyordu; pencere artık
        // shell'den ÖNCE açıldığı için o an kısayolların hedefi yok.
        // Sessizce dönmüyoruz: bu dala düşmek Ctrl+Shift+S/E, Ctrl+G ve F2-F5'in
        // tamamının ölü doğması demek ve ekranda hiçbir izi olmaz — operatör
        // kısayolun çalışmadığını ancak canlı yayında fark eder. Üretimde
        // ulaşılamaz (App.OnStartup kökü MainWindow'a koyup Show() ediyor),
        // bu yüzden ulaşılırsa varsayım bozulmuş demektir; RunStartupAsync'in
        // catch'i hatayı gösterip kapatır.
        var window = Window.GetWindow(_root) ?? Application.Current?.MainWindow
            ?? throw new InvalidOperationException(
                "Shell kuruldu ama AppRootView bir Window'un içinde değil; kısayollar bağlanamaz.");

        var binder = _services.GetRequiredService<ShortcutBinder>();
        binder.Apply(window);
        SilenceShortcutsWhileGateIsOpen(
            window, _services.GetRequiredService<Services.Gates.AppGateStack>(), binder.Apply);
    }

    /// <summary>
    /// Gate açıkken <see cref="Window.InputBindings"/> boşaltılır, kapanınca
    /// <paramref name="apply"/> ile yeniden kurulur.
    ///
    /// NEDEN KOLEKSİYONU BOŞALTMAK: KeyBinding'ler Window'un üzerinde duruyor,
    /// yani AppRootView'un <c>ShellHost.IsEnabled=false</c>'u onlara ulaşmıyor.
    /// Çalışma anında hesap değiştirmek için açılan LoginGate'in üstünden
    /// Ctrl+Shift+S/E yayın başlatıp bitirebilir, Ctrl+G çekiliş açabilir,
    /// F2-F5 arkada sayfa yığabilirdi.
    ///
    /// <c>e.Handled=true</c> ile çözülemez: shell'in tuş dinleyicisi Window'a
    /// PreviewKeyDown (tünelleme) ile bağlı, orada işaretlemek gate'in kendi
    /// metin kutularına tuş gitmesini de keser.
    ///
    /// Yeniden kurmak güvenli: <see cref="ShortcutBinder"/> durumsuz, her
    /// <c>Apply</c> koleksiyonu temizleyip registry'den yeniden inşa ediyor.
    /// </summary>
    internal static void SilenceShortcutsWhileGateIsOpen(
        Window window, Services.Gates.AppGateStack gates, Action<Window> apply)
    {
        gates.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not nameof(Services.Gates.AppGateStack.IsOpen)) return;
            if (gates.IsOpen) window.InputBindings.Clear();
            else apply(window);
        };
    }

    public void RequestShutdown() => Application.Current.Shutdown();

    public void RequestRestart()
    {
        // Geri yükleme yeni bir DB dosyası yazıyor; süreç boyunca açık
        // tutulan SQLite bağlantılarıyla tutarlı olmasının tek yolu yeni
        // süreç. Faz 4a'ya kadar burada MessageBox + Shutdown() vardı, yani
        // tasarım gereği operatör uygulamayı elle açacaktı — ama pratikte
        // buraya hiç gelinmiyordu: geri yükleme koşulu migration'dan SONRA
        // ölçüldüğü için her zaman false dönüyordu (bkz.
        // IsDatabaseMissingOrTiny). Koşul düzeltildi; bu metot ilk kez
        // gerçekten koşabilir hâlde.
        var exe = Environment.ProcessPath;
        var relaunched = false;
        if (exe is not null)
        {
            try
            {
                Process.Start(new ProcessStartInfo(exe) { UseShellExecute = true });
                relaunched = true;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Restart could not be launched; closing only");
            }
        }

        // Yeni süreç kalkmadıysa SÖYLE: bu satıra gelen operatör "Yeniden
        // Başlat"a basmış ve uygulamanın geri gelmesini bekliyor. Sessiz
        // kapanış, geri yükleme başarısız olmuş gibi görünür.
        if (!relaunched)
        {
            _log.LogWarning("Restart unavailable (ProcessPath={Exe}); manual relaunch needed", exe);
            MessageBox.Show(
                "Yedek geri yüklendi, ama OrderDeck kendini yeniden başlatamadı.\n\n" +
                "Uygulama şimdi kapanıyor; geri yüklenen verilerle çalışmak için " +
                "OrderDeck'i elle tekrar aç.",
                "OrderDeck — Yeniden Başlatılamadı",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        Application.Current.Shutdown();
    }

    /// <summary>Kestrel/HttpListener'ın "port zaten bağlı" için attığı dağınık
    /// istisna biçimlerini tek bir boolean'a indirger. Kestrel alttaki
    /// SocketException'ı IOException içine sarar; HttpListener HRESULT
    /// 0x80004005 ile HttpListenerException atar.</summary>
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
}
