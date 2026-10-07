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
    [ModuleInitializer]
    internal static void RedirectAppPaths()
    {
        // Süreç başına bir klasör: paralel test sınıfları aynı kökü paylaşır (bugünkü gerçek
        // klasörde de öyleydi; MigrationRunner BEGIN IMMEDIATE ile eşzamanlı açılışa dayanıklı).
        var root = Path.Combine(Path.GetTempPath(), $"orderdeck-tests-{Environment.ProcessId}", "OrderDeck");
        AppContext.SetData(AppPaths.DocumentsRootOverrideKey, root);
    }

    /// <summary>
    /// U17 kilit sırası denetimleri test sürecinde HER derlemede açık. CI testleri Release koşar;
    /// denetim yalnız DEBUG'a bağlı kalsaydı orada hiç çalışmaz, ihlal eden yol yeşil geçerdi.
    /// </summary>
    [ModuleInitializer]
    internal static void EnableWriteScopeChecks()
        => AppContext.SetSwitch(WriteScopeGuard.ChecksSwitch, true);
}
