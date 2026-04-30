using System;
using System.IO;

namespace OrderDeck.Core;

/// <summary>
/// One-time migration of legacy LiveDeck user data to OrderDeck location.
/// Idempotent — safe to call on every startup.
///
/// Source: ~/Documents/LiveDeck/  (legacy)
/// Target: ~/Documents/OrderDeck/ (current, see AppPaths)
///
/// Behaviour:
/// - If legacy folder exists AND target folder does NOT exist → rename legacy → target.
/// - Inside the migrated data folder, rename livedeck.db (and -shm/-wal sibling files) → orderdeck.db.
/// - If both folders exist (e.g. user manually created OrderDeck while LiveDeck was still around),
///   leave both untouched and emit no error. Migration only runs on a clean OrderDeck.
/// </summary>
public static class AppDataMigrator
{
    public static string LegacyDocumentsRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Documents",
        "LiveDeck");

    /// <summary>Returns true if a migration was performed.</summary>
    public static bool MigrateIfNeeded()
    {
        var legacy = LegacyDocumentsRoot;
        var target = AppPaths.DocumentsRoot;

        if (!Directory.Exists(legacy)) return false;
        if (Directory.Exists(target)) return false; // both exist — don't merge, leave alone

        // Move the entire folder (single filesystem rename on the same volume — atomic).
        Directory.Move(legacy, target);

        // Rename data files: livedeck.db → orderdeck.db (+ SQLite -shm/-wal siblings if present).
        var dataFolder = Path.Combine(target, "data");
        if (Directory.Exists(dataFolder))
        {
            RenameIfExists(Path.Combine(dataFolder, "livedeck.db"),     Path.Combine(dataFolder, "orderdeck.db"));
            RenameIfExists(Path.Combine(dataFolder, "livedeck.db-shm"), Path.Combine(dataFolder, "orderdeck.db-shm"));
            RenameIfExists(Path.Combine(dataFolder, "livedeck.db-wal"), Path.Combine(dataFolder, "orderdeck.db-wal"));
        }

        return true;
    }

    private static void RenameIfExists(string from, string to)
    {
        if (File.Exists(from) && !File.Exists(to))
            File.Move(from, to);
    }
}
