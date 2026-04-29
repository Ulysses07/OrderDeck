using System;
using System.IO;

namespace LiveDeck.Core;

/// <summary>
/// Centralised filesystem paths used by LiveDeck. All paths are derived from the user's
/// Documents folder so they are roaming-friendly and easy to back up.
/// </summary>
public static class AppPaths
{
    public static string DocumentsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents",
        "LiveDeck");

    public static string DataFolder => Path.Combine(DocumentsRoot, "data");
    public static string LogsFolder => Path.Combine(DocumentsRoot, "Logs");
    public static string ReportsFolder => Path.Combine(DocumentsRoot, "Reports");
    public static string BackupsFolder => Path.Combine(DocumentsRoot, "Backups");

    public static string DatabaseFile => Path.Combine(DataFolder, "livedeck.db");
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
