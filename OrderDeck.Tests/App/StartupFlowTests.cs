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
/// sessizce bozuldu (StartupUri'nin OnStartup'tan sonra koşması, restore
/// sonrası uygulamanın kapanıp açılmaması). Kurallar artık burada kilitli.
/// </summary>
public class StartupFlowTests
{
    private static StreamSession Session(string id = "s1") =>
        new(id, "Akşam yayını", 1_700_000_000, null, new[] { "youtube" }, null);

    private static StartupFlow Build(FakeStartupGates gates, FakeStartupEnvironment env) =>
        new(gates, env, NullLogger<StartupFlow>.Instance);

    [Fact]
    public async Task Licensed_and_clean_start_mounts_the_shell()
    {
        var gates = new FakeStartupGates();
        var env = new FakeStartupEnvironment();

        await Build(gates, env).RunAsync();

        Assert.True(gates.BootShown);
        Assert.False(gates.LoginShown);
        Assert.True(env.ShellMounted);
        Assert.False(env.ShutdownRequested);
        Assert.False(env.RestartRequested);
    }

    [Fact]
    public async Task Cancelled_login_shuts_down_without_mounting_the_shell()
    {
        var gates = new FakeStartupGates { LoginResult = false };
        var env = new FakeStartupEnvironment { HasLicense = false };

        await Build(gates, env).RunAsync();

        Assert.True(gates.LoginShown);
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
        var env = new FakeStartupEnvironment { HasCompletedFirstRun = completed };

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

        Assert.True(env.ShellMounted);
    }

    // ── Sahteler ──────────────────────────────────────────────────────

    private sealed class FakeStartupGates : IStartupGates
    {
        public bool BootShown, LoginShown, RestoreShown, FirstRunShown, RecoveryShown;
        public bool LoginResult = true;
        public RestoreOutcome RestoreResult = RestoreOutcome.Skipped;
        public SessionRecoveryChoice RecoveryResult = SessionRecoveryChoice.Continue;

        public async Task ShowBootAsync(Func<Task> work)
        {
            BootShown = true;
            await work();
        }

        public Task<bool> ShowLoginAsync(bool isStartupGate)
        {
            LoginShown = true;
            return Task.FromResult(LoginResult);
        }

        public Task<RestoreOutcome> ShowRestoreAsync(IReadOnlyList<BackupMetadata> backups)
        {
            RestoreShown = true;
            return Task.FromResult(RestoreResult);
        }

        public Task ShowFirstRunAsync()
        {
            FirstRunShown = true;
            return Task.CompletedTask;
        }

        public Task<SessionRecoveryChoice> ShowSessionRecoveryAsync(StreamSession session)
        {
            RecoveryShown = true;
            return Task.FromResult(RecoveryResult);
        }
    }

    private sealed class FakeStartupEnvironment : IStartupEnvironment
    {
        public bool HasLicense { get; set; } = true;
        public bool HasCompletedFirstRun { get; set; } = true;
        public bool DatabaseMissing { get; set; }
        public bool LicenseInitThrows { get; set; }
        public bool BackgroundServicesStart { get; set; } = true;
        public IReadOnlyList<BackupMetadata> Backups { get; set; } = Array.Empty<BackupMetadata>();
        public StreamSession? ActiveSession { get; set; }

        public bool ShellMounted, ShutdownRequested, RestartRequested;
        public string? EndedSessionId;

        public Task InitializeLicenseAsync() =>
            LicenseInitThrows
                ? Task.FromException(new InvalidOperationException("lisans sunucusuna ulaşılamadı"))
                : Task.CompletedTask;

        public bool IsDatabaseMissingOrTiny() => DatabaseMissing;

        public Task<IReadOnlyList<BackupMetadata>> ListBackupsAsync() => Task.FromResult(Backups);

        public StreamSession? GetActiveSession() => ActiveSession;

        public void EndSession(string sessionId) => EndedSessionId = sessionId;

        public Task<bool> StartBackgroundServicesAsync() => Task.FromResult(BackgroundServicesStart);

        public void MountShell() => ShellMounted = true;

        public void RequestShutdown() => ShutdownRequested = true;

        public void RequestRestart() => RestartRequested = true;
    }
}
