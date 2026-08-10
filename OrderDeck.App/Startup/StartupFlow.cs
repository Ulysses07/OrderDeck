using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Startup;

/// <summary>
/// Açılış sırası. Eskiden <c>App.OnStartup</c> içinde düz akış hâlinde duran
/// ve hiç test edilemeyen mantığın tamamı burada; <c>App.OnStartup</c> artık
/// yalnızca pencereyi açıp <see cref="RunAsync"/>'i tetikliyor.
///
/// SIRA DEĞİŞMEDİ: lisans → giriş → geri yükleme → sihirbaz → oturum
/// kurtarma → arka plan servisleri. Tek eklenen son adım
/// <see cref="IStartupEnvironment.MountShell"/>: pencere artık en başta
/// açıldığı için shell'in ne zaman kurulacağına burası karar veriyor.
///
/// Üç blok (geri yükleme, sihirbaz, oturum kurtarma) hatayı yutup devam
/// ediyor — bugünkü davranışın aynısı. Gerekçe: hiçbiri uygulamanın
/// açılmasını engelleyecek kadar kritik değil; bulut erişilemiyorsa
/// operatör yine de yayın yapabilmeli.
/// </summary>
public sealed class StartupFlow
{
    private readonly IStartupGates _gates;
    private readonly IStartupEnvironment _env;
    private readonly ILogger<StartupFlow> _log;

    public StartupFlow(IStartupGates gates, IStartupEnvironment env, ILogger<StartupFlow> log)
    {
        _gates = gates;
        _env = env;
        _log = log;
    }

    public async Task RunAsync()
    {
        // ── Lisans ────────────────────────────────────────────────────
        try
        {
            await _gates.ShowBootAsync(_env.InitializeLicenseAsync);
        }
        catch (Exception ex)
        {
            // Çevrimdışı makinede uygulama yine de açılmalı; durum
            // OfflineGrace'e düşer.
            _log.LogError(ex, "License initialization failed");
        }

        if (!_env.HasLicense)
        {
            var licensed = await _gates.ShowLoginAsync(isStartupGate: true);
            if (!licensed)
            {
                _env.RequestShutdown();
                return;
            }
        }

        // ── Geri yükleme ──────────────────────────────────────────────
        try
        {
            if (_env.IsDatabaseMissingOrTiny())
            {
                var backups = await _env.ListBackupsAsync();
                if (backups.Count > 0 &&
                    await _gates.ShowRestoreAsync(backups) == RestoreOutcome.Restored)
                {
                    // Yazılan DB'nin süreç boyunca açık tutulan bağlantılarla
                    // tutarlı olmasının tek yolu yeniden başlamak.
                    //
                    // BİLİNÇLİ DAVRANIŞ DEĞİŞİKLİĞİ (Faz 4a tasarımı §5): eskiden
                    // burada MessageBox + Shutdown() vardı, yani tasarım gereği
                    // uygulamayı elle açmak gerekiyordu; MessageBox zaten kalkmak
                    // zorunda olduğu için yerine otomatik yeniden başlatma kondu.
                    // Yeni süreç kalkmazsa RequestRestart yine de açıklama gösteriyor.
                    //
                    // Bu blok üretimde bugüne kadar HİÇ koşmadı: koşulu veren
                    // IsDatabaseMissingOrTiny dosyaya migration'dan sonra baktığı
                    // için hep false dönüyordu. Ölçüm artık açılışın en başında
                    // yapılıyor (BootDatabaseState).
                    _env.RequestRestart();
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Restore auto-prompt failed (non-fatal)");
        }

        // ── İlk açılış sihirbazı ──────────────────────────────────────
        try
        {
            if (!_env.HasCompletedFirstRun())
                await _gates.ShowFirstRunAsync();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "First-run wizard failed (non-fatal)");
        }

        // ── Yarım kalmış yayın ────────────────────────────────────────
        try
        {
            var active = _env.GetActiveSession();
            if (active is not null)
            {
                var choice = await _gates.ShowSessionRecoveryAsync(active);
                if (choice == SessionRecoveryChoice.Exit)
                {
                    _env.RequestShutdown();
                    return;
                }
                if (choice == SessionRecoveryChoice.EndSession)
                    _env.EndSession(active.Id);
                // Continue → oturum açık kalıyor; MainShellViewModel
                // ReloadQueueFromActiveSession ile kuyruğu geri yüklüyor.
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Session recovery prompt failed (non-fatal)");
        }

        // ── Arka plan servisleri ve shell ─────────────────────────────
        if (!await _env.StartBackgroundServicesAsync())
            return;

        _env.MountShell();
    }
}
