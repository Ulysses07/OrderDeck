using System.Runtime.CompilerServices;
using OrderDeck.Core;

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
}
