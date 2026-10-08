using System;
using System.IO;

namespace OrderDeck.Core;

/// <summary>
/// Centralised filesystem paths used by OrderDeck. All paths are derived from the user's
/// Documents folder so they are roaming-friendly and easy to back up.
/// </summary>
public static class AppPaths
{
    /// <summary>
    /// Süreç içi kök değiştirme anahtarı. Yalnız test derlemesi kurar
    /// (<c>OrderDeck.Tests/TestAssemblyInit.cs</c> — modül başlatıcısı, test derlemesinde
    /// herhangi bir kod koşmadan önce çalışır). Ortam değişkeni BİLEREK değil: kullanıcı
    /// ortamından sızıp üretimde veritabanını başka klasöre taşıyamaz.
    /// </summary>
    public const string DocumentsRootOverrideKey = "OrderDeck.DocumentsRootOverride";

    // Önbelleğe ALINMAZ (her erişimde okunur): statik bir ilk değer ataması modül
    // başlatıcısından önce koşarsa anahtar sessizce yok sayılır ve testler yeniden gerçek
    // veritabanına dokunurdu; böylece sıra sorusu hiç doğmaz.
    public static string DocumentsRoot =>
        AppContext.GetData(DocumentsRootOverrideKey) as string
        ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "OrderDeck");

    public static string DataFolder => Path.Combine(DocumentsRoot, "data");
    public static string LogsFolder => Path.Combine(DocumentsRoot, "Logs");
    public static string ReportsFolder => Path.Combine(DocumentsRoot, "Reports");
    public static string BackupsFolder => Path.Combine(DocumentsRoot, "Backups");

    public static string DatabaseFile => Path.Combine(DataFolder, "orderdeck.db");
    public static string SettingsFile => Path.Combine(DocumentsRoot, "settings.json");

    public static string AuthFile => Path.Combine(DataFolder, "auth.dat");
    public static string LicenseFile => Path.Combine(DataFolder, "license.dat");
    public static string TrialFile => Path.Combine(DataFolder, "trial.dat");

    public static void EnsureDirectoriesExist()
    {
        Directory.CreateDirectory(DocumentsRoot);
        Directory.CreateDirectory(DataFolder);
        Directory.CreateDirectory(LogsFolder);
        Directory.CreateDirectory(ReportsFolder);
        Directory.CreateDirectory(BackupsFolder);
    }
}
