using System.Diagnostics;
using System.Runtime.CompilerServices;
using OrderDeck.Core;
using OrderDeck.Core.Storage;

namespace OrderDeck.Tests;

/// <summary>
/// Test sürecinin AppPaths kökü geçici klasöre çevrilir (Bölüm C, Task C0). AppHost kuran
/// testler (LicensingDiTests, BootDatabaseStateTests, CatalogSyncServiceTests) aksi hâlde
/// geliştiricinin gerçek veritabanını göç ettirir, ayar ve günlük dosyalarına yazar.
/// </summary>
internal static class TestAssemblyInit
{
    /// <summary>Aşağıdaki klasörlerin ortak öneki (<see cref="RedirectAppPaths"/> ve
    /// <see cref="CleanupStaleTestDirectories"/> aynı deseni paylaşır).</summary>
    private const string DirPrefix = "orderdeck-tests-";

    [ModuleInitializer]
    internal static void RedirectAppPaths()
    {
        // Süreç başına bir klasör: paralel test sınıfları aynı kökü paylaşır (bugünkü gerçek
        // klasörde de öyleydi; MigrationRunner BEGIN IMMEDIATE ile eşzamanlı açılışa dayanıklı).
        var root = Path.Combine(Path.GetTempPath(), $"{DirPrefix}{Environment.ProcessId}", "OrderDeck");
        AppContext.SetData(AppPaths.DocumentsRootOverrideKey, root);
    }

    /// <summary>
    /// U17 kilit sırası denetimleri test sürecinde HER derlemede açık. CI testleri Release koşar;
    /// denetim yalnız DEBUG'a bağlı kalsaydı orada hiç çalışmaz, ihlal eden yol yeşil geçerdi.
    /// </summary>
    [ModuleInitializer]
    internal static void EnableWriteScopeChecks()
        => AppContext.SetSwitch(WriteScopeGuard.ChecksSwitch, true);

    /// <summary>
    /// Son inceleme M-5: <see cref="RedirectAppPaths"/>'in açtığı <c>%TEMP%\orderdeck-tests-&lt;pid&gt;</c>
    /// klasörü hiçbir yerde silinmiyor — her test süreci (yerel koşu, CI) kendi klasörünü bırakır,
    /// onlarcası birikir. Bu modül başlatıcı KARDEŞ klasörleri tarar; sahibi artık koşmayan (süreç
    /// kimliği yaşamıyor) klasörü siler. En iyi gayret: tarama ya da tek bir klasörün silinmesi
    /// başarısız olsa da (erişim, başka bir süreç açık dosya tutuyor…) test koşusu asla düşmez.
    /// Yalnız <c>%TEMP%\orderdeck-tests-*</c> desenine uyan klasörlere dokunur — TEMP'teki başka
    /// hiçbir şeye.
    /// </summary>
    [ModuleInitializer]
    internal static void CleanupStaleTestDirectories()
    {
        try
        {
            var currentPid = Environment.ProcessId;
            foreach (var dir in Directory.EnumerateDirectories(Path.GetTempPath(), $"{DirPrefix}*"))
            {
                try
                {
                    var pidText = Path.GetFileName(dir)[DirPrefix.Length..];
                    if (!int.TryParse(pidText, out var pid) || pid == currentPid) continue;
                    if (IsProcessRunning(pid)) continue;
                    Directory.Delete(dir, recursive: true);
                }
                catch
                {
                    // Bu klasör silinemedi (kilitli, erişim…) — sıradakine geç, bu süreç çalışır.
                }
            }
        }
        catch
        {
            // Taramanın kendisi başarısız olsa da (ör. TEMP erişilemez) testler sürer.
        }
    }

    /// <summary>Bilinmeyen bir hatada (ör. erişim) "emin değilken sil" yerine "emin değilken
    /// dokunma" — yanlışlıkla hâlâ kullanılan bir klasörü silmemek, birikmeye göz yummaktan
    /// daha güvenli.</summary>
    private static bool IsProcessRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;      // o kimlikte süreç yok — klasör sahipsiz
        }
        catch
        {
            return true;
        }
    }
}
