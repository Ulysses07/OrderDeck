using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.App.Startup;
using OrderDeck.Core.Sessions;
using OrderDeck.Licensing.Backup;

namespace OrderDeck.Tests.App;

/// <summary>
/// Açılış sırasının kuralları. STA GEREKMİYOR: StartupFlow ne pencere ne
/// servis tanıyor, ikisi de arayüz arkasında.
///
/// NEDEN BU TESTLER: bu sıra bugüne kadar hiç test edilemedi ve iki kez
/// sessizce yanlış davrandı — biri gerileme, biri doğuştan:
/// (1) <c>StartupUri</c> ana pencereyi OnStartup'tan SONRA türetiyordu; hata
///     CI'da tesadüfen görünene kadar sessizce durdu (10c53a8).
/// (2) Geri yükleme yolu, eklendiği 422cbc0'dan beri kullanıcıya "Uygulama
///     yeniden başlatılacak" deyip yalnızca <c>Shutdown()</c> çağırıyordu —
///     yani bozulmadı, baştan yanlış davranıyordu.
/// Kurallar artık burada kilitli.
/// </summary>
public class StartupFlowTests
{
    private static StreamSession Session(string id = "s1") =>
        new(id, "Akşam yayını", 1_700_000_000, null, new[] { "youtube" }, null);

    private static StartupFlow Build(FakeStartupGates gates, FakeStartupEnvironment env)
    {
        // Gate ve ortam çağrıları TEK zaman çizgisinde görünsün diye iki sahte
        // aynı listeye yazıyor; sıra iddiası ancak böyle kurulabiliyor.
        env.Calls = gates.Calls;
        return new StartupFlow(gates, env, NullLogger<StartupFlow>.Instance);
    }

    [Fact]
    public async Task Licensed_and_clean_start_mounts_the_shell()
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment();

        await Build(gates, env).RunAsync();

        Assert.Equal(1, gates.BootCount);
        Assert.False(gates.LoginShown);
        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
        Assert.False(env.RestartRequested);
    }

    [Fact]
    public async Task Every_gate_runs_in_the_documented_order()
    {
        // Bu testin TEK işi sıra. Bayrak iddiaları blokların yerini
        // değiştirmeyi yakalamıyordu: sihirbaz bloğu geri yükleme bloğunun
        // önüne alındığında diğer testlerin hepsi yeşil kalıyor.
        // Beklenen dizi App.xaml.cs'teki eski düz akıştan alındı (o kod
        // a428bd2'de silindi; sıranın tek kaydı artık burası):
        // lisans → geri yükleme → sihirbaz → oturum kurtarma →
        // arka plan servisleri → shell.
        var gates = new FakeStartupGates
        {
            RestoreResult = RestoreOutcome.Skipped,
            RecoveryResult = SessionRecoveryChoice.Continue
        };
        var env = new FakeStartupEnvironment
        {
            HasLicense = false,               // giriş gate'i açılsın
            DatabaseMissing = true,           // geri yükleme gate'i açılsın
            Backups = new[] { new BackupMetadata(Guid.NewGuid(), 4096, DateTimeOffset.UtcNow, false, "PC") },
            FirstRunCompleted = false,        // sihirbaz açılsın
            ActiveSession = Session()         // oturum kurtarma açılsın
        };

        await Build(gates, env).RunAsync();

        Assert.Equal(
            new[]
            {
                "gate.boot",
                "env.license.init",
                "env.license.has",
                "gate.login(isStartupGate:True)",
                "env.db.isMissingOrTiny",
                "env.backups.list",
                "gate.restore",
                "env.firstRun.hasCompleted",
                "gate.firstRun",
                "env.session.getActive",
                "gate.sessionRecovery",
                "env.services.start",
                "env.shell.mount"
            },
            env.Calls);
    }

    [Fact]
    public async Task Boot_gate_wraps_license_initialization_exactly_once()
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment();

        await Build(gates, env).RunAsync();

        Assert.Equal(1, gates.BootCount);
        // Lisans işi gate AÇILDIKTAN sonra başlamalı — ShowBootAsync'in
        // Func<Task> almasının tek sebebi bu.
        Assert.Equal(new[] { "gate.boot", "env.license.init" }, env.Calls.Take(2));
    }

    [Fact]
    public async Task Cancelled_login_shuts_down_without_mounting_the_shell()
    {
        var gates = new FakeStartupGates { LoginResult = false };
        var env = new FakeStartupEnvironment { HasLicense = false };

        await Build(gates, env).RunAsync();

        Assert.True(gates.LoginShown);
        // Açılış yolunda giriş HER ZAMAN gate modunda açılır; isStartupGate:false
        // yalnız çalışma anındaki hesap değiştirme içindir.
        Assert.True(gates.LoginIsStartupGate);
        Assert.True(env.ShutdownRequested);
        Assert.False(env.ShellMounted);
    }

    [Fact]
    public async Task Successful_login_continues_to_the_shell()
    {
        var gates = new FakeStartupGates { LoginResult = true };
        var env = new FakeStartupEnvironment { HasLicense = false };

        await Build(gates, env).RunAsync();

        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
    }

    [Fact]
    public async Task Completed_restore_restarts_instead_of_mounting_the_shell()
    {
        var gates = new FakeStartupGates { RestoreResult = RestoreOutcome.Restored };
        var env = new FakeStartupEnvironment
        {
            DatabaseMissing = true,
            Backups = new[] { new BackupMetadata(Guid.NewGuid(), 4096, DateTimeOffset.UtcNow, false, "PC") }
        };

        await Build(gates, env).RunAsync();

        Assert.True(env.RestartRequested);
        Assert.False(env.ShellMounted);
    }

    [Fact]
    public async Task Skipped_restore_continues_to_the_shell()
    {
        var gates = new FakeStartupGates { RestoreResult = RestoreOutcome.Skipped };
        var env = new FakeStartupEnvironment
        {
            DatabaseMissing = true,
            Backups = new[] { new BackupMetadata(Guid.NewGuid(), 4096, DateTimeOffset.UtcNow, false, "PC") }
        };

        await Build(gates, env).RunAsync();

        Assert.False(env.RestartRequested);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Restore_gate_is_skipped_when_there_are_no_backups()
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { DatabaseMissing = true, Backups = Array.Empty<BackupMetadata>() };

        await Build(gates, env).RunAsync();

        Assert.False(gates.RestoreShown);
        Assert.True(env.ShellMounted);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task First_run_gate_follows_the_persisted_flag(bool completed, bool expectShown)
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { FirstRunCompleted = completed };

        await Build(gates, env).RunAsync();

        Assert.Equal(expectShown, gates.FirstRunShown);
    }

    [Fact]
    public async Task Session_recovery_exit_shuts_down()
    {
        var gates = new FakeStartupGates { RecoveryResult = SessionRecoveryChoice.Exit };
        var env = new FakeStartupEnvironment { ActiveSession = Session() };

        await Build(gates, env).RunAsync();

        Assert.True(env.ShutdownRequested);
        Assert.False(env.ShellMounted);
        Assert.Null(env.EndedSessionId);
    }

    [Fact]
    public async Task Session_recovery_end_closes_the_session_and_continues()
    {
        var gates = new FakeStartupGates { RecoveryResult = SessionRecoveryChoice.EndSession };
        var env = new FakeStartupEnvironment { ActiveSession = Session("abc") };

        await Build(gates, env).RunAsync();

        Assert.Equal("abc", env.EndedSessionId);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Session_recovery_continue_leaves_the_session_open()
    {
        var gates = new FakeStartupGates { RecoveryResult = SessionRecoveryChoice.Continue };
        var env = new FakeStartupEnvironment { ActiveSession = Session("abc") };

        await Build(gates, env).RunAsync();

        Assert.Null(env.EndedSessionId);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Background_service_failure_leaves_the_shell_unmounted()
    {
        // Port çakışması: bugün MessageBox + Shutdown(). Ortam false döndürüp
        // kapatmayı kendi üstleniyor; akışın tek işi shell'i KURMAMAK.
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { BackgroundServicesStart = false };

        await Build(gates, env).RunAsync();

        Assert.False(env.ShellMounted);
    }

    [Fact]
    public async Task License_initialization_failure_does_not_stop_startup()
    {
        // Bugünkü davranış: hata loglanır, akış devam eder (çevrimdışı
        // makinede uygulama yine de açılmalı).
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment { LicenseInitThrows = true };

        await Build(gates, env).RunAsync();

        // Gate'in try/finally sözleşmesi: iş patlasa da açılış ekranı
        // kapanır. Kapanmasaydı çevrimdışı makine "Hazırlanıyor…"da asılı
        // kalırdı — arkadaki shell'e bir daha ulaşılamazdı.
        Assert.True(gates.BootClosed);
        Assert.True(env.ShellMounted);
    }

    [Fact]
    public async Task Restore_gate_failure_does_not_stop_startup()
    {
        // Bulut erişilemiyorsa operatör yine de yayın yapabilmeli: hata
        // yutulur, sonraki adımlar koşar, shell yine kurulur.
        var gates = new FakeStartupGates { RestoreThrows = true };
        var env = new FakeStartupEnvironment
        {
            DatabaseMissing = true,
            FirstRunCompleted = false,
            ActiveSession = Session(),
            Backups = new[] { new BackupMetadata(Guid.NewGuid(), 4096, DateTimeOffset.UtcNow, false, "PC") }
        };

        await Build(gates, env).RunAsync();

        Assert.True(gates.RestoreShown);
        Assert.True(gates.FirstRunShown);
        Assert.True(gates.RecoveryShown);
        Assert.True(env.ShellMounted);
        Assert.False(env.RestartRequested);
        Assert.False(env.ShutdownRequested);
    }

    [Fact]
    public async Task First_run_wizard_failure_does_not_stop_startup()
    {
        var gates = new FakeStartupGates { FirstRunThrows = true };
        var env = new FakeStartupEnvironment { FirstRunCompleted = false, ActiveSession = Session() };

        await Build(gates, env).RunAsync();

        Assert.True(gates.FirstRunShown);
        Assert.True(gates.RecoveryShown);
        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
    }

    [Fact]
    public async Task Session_recovery_failure_does_not_stop_startup()
    {
        // Kurtarma sorusu patlarsa oturum AÇIK bırakılır — kapatma kararı
        // yalnız operatörün açık seçimiyle verilir.
        var gates = new FakeStartupGates { RecoveryThrows = true };
        var env = new FakeStartupEnvironment { ActiveSession = Session("abc") };

        await Build(gates, env).RunAsync();

        Assert.True(gates.RecoveryShown);
        Assert.Null(env.EndedSessionId);
        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
    }

    // ── Sahteler ──────────────────────────────────────────────────────

    // Her iki sahte de AYNI Calls listesine yazar (bkz. Build). Bayrak
    // tutmak yetmiyor: bloklar yer değiştirdiğinde bayraklar aynı kalıyor,
    // sıra iddiası ise kırılıyor.
    private sealed class FakeStartupGates : IStartupGates
    {
        public List<string> Calls { get; } = new();

        public int BootCount;
        public bool BootClosed;
        public bool LoginShown, RestoreShown, FirstRunShown, RecoveryShown;
        public bool? LoginIsStartupGate;

        public bool LoginResult = true;
        public RestoreOutcome RestoreResult = RestoreOutcome.Skipped;
        public SessionRecoveryChoice RecoveryResult = SessionRecoveryChoice.Continue;

        public bool RestoreThrows, FirstRunThrows, RecoveryThrows;

        public async Task ShowBootAsync(Func<Task> work)
        {
            BootCount++;
            Calls.Add("gate.boot");
            // Üretim gerçeklemesinin ZORUNLU kalıbı (bkz. IStartupGates
            // sözleşmesi): iş ne yaparsa yapsın gate kapanır, hata olduğu
            // gibi yukarı çıkar. Kapanış işaretleniyor ki lisans hatası
            // testi "iş patladı ama gate kapandı"yı iddia edebilsin.
            try { await work(); }
            finally { BootClosed = true; }
        }

        public Task<bool> ShowLoginAsync(bool isStartupGate)
        {
            LoginShown = true;
            LoginIsStartupGate = isStartupGate;
            Calls.Add($"gate.login(isStartupGate:{isStartupGate})");
            return Task.FromResult(LoginResult);
        }

        public Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups)
        {
            RestoreShown = true;
            Calls.Add("gate.restore");
            return RestoreThrows
                ? Task.FromException<RestoreOutcome>(new InvalidOperationException("yedek listesi bozuk"))
                : Task.FromResult(RestoreResult);
        }

        public Task ShowFirstRunAsync()
        {
            FirstRunShown = true;
            Calls.Add("gate.firstRun");
            return FirstRunThrows
                ? Task.FromException(new InvalidOperationException("sihirbaz açılamadı"))
                : Task.CompletedTask;
        }

        public Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session)
        {
            RecoveryShown = true;
            Calls.Add("gate.sessionRecovery");
            return RecoveryThrows
                ? Task.FromException<SessionRecoveryChoice>(new InvalidOperationException("kurtarma sorusu açılamadı"))
                : Task.FromResult(RecoveryResult);
        }
    }

    private sealed class FakeStartupEnvironment : IStartupEnvironment
    {
        /// <summary>Build() burayı gates.Calls ile aynı listeye bağlar.</summary>
        public List<string> Calls { get; set; } = new();

        public bool HasLicenseValue { get; set; } = true;
        public bool FirstRunCompleted { get; set; } = true;
        public bool DatabaseMissing { get; set; }
        public bool LicenseInitThrows { get; set; }
        public bool BackgroundServicesStart { get; set; } = true;
        public IReadOnlyList<BackupMetadata> Backups { get; set; } = Array.Empty<BackupMetadata>();
        public StreamSession? ActiveSession { get; set; }

        /// <summary>Testlerin okunaklı kalması için: env.HasLicense = false.</summary>
        public bool HasLicense
        {
            get { Calls.Add("env.license.has"); return HasLicenseValue; }
            set => HasLicenseValue = value;
        }

        public bool ShellMounted, ShutdownRequested, RestartRequested;
        public string? EndedSessionId;

        public Task InitializeLicenseAsync()
        {
            Calls.Add("env.license.init");
            return LicenseInitThrows
                ? Task.FromException(new InvalidOperationException("lisans sunucusuna ulaşılamadı"))
                : Task.CompletedTask;
        }

        public bool IsDatabaseMissingOrTiny()
        {
            Calls.Add("env.db.isMissingOrTiny");
            return DatabaseMissing;
        }

        public Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync()
        {
            Calls.Add("env.backups.list");
            return Task.FromResult(Backups);
        }

        public bool HasCompletedFirstRun()
        {
            Calls.Add("env.firstRun.hasCompleted");
            return FirstRunCompleted;
        }

        public StreamSession? GetActiveSession()
        {
            Calls.Add("env.session.getActive");
            return ActiveSession;
        }

        public void EndSession(string sessionId)
        {
            Calls.Add("env.session.end");
            EndedSessionId = sessionId;
        }

        public Task<bool> StartBackgroundServicesAsync()
        {
            Calls.Add("env.services.start");
            return Task.FromResult(BackgroundServicesStart);
        }

        public void MountShell()
        {
            Calls.Add("env.shell.mount");
            ShellMounted = true;
        }

        public void RequestShutdown()
        {
            Calls.Add("env.shutdown");
            ShutdownRequested = true;
        }

        public void RequestRestart()
        {
            Calls.Add("env.restart");
            RestartRequested = true;
        }
    }
}
