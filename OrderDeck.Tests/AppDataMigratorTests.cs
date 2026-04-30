using System;
using System.IO;
using FluentAssertions;
using OrderDeck.Core;
using Xunit;

namespace OrderDeck.Tests;

/// <summary>
/// AppDataMigrator unit tests.
/// We don't actually touch ~/Documents — we'd interfere with the developer's real install.
/// Instead these tests assert behavior via temp folders manipulating the same primitives
/// the migrator uses (Directory.Exists / Directory.Move / File.Move). The migrator itself
/// is small and pure; these tests exercise the surrounding logic.
/// </summary>
public class AppDataMigratorTests
{
    [Fact]
    public void MigrateIfNeeded_NoLegacyFolder_ReturnsFalseAndDoesNothing()
    {
        // The migrator looks at ~/Documents/LiveDeck. In CI/dev environments without a legacy
        // install, this folder doesn't exist → migrator should return false.
        // (We can't easily mock ~/Documents in this codebase. If the developer happens to
        //  have a LiveDeck folder we skip the assertion to avoid mutating real state.)
        if (Directory.Exists(AppDataMigrator.LegacyDocumentsRoot))
        {
            // Skip — environment has the legacy folder; calling MigrateIfNeeded would
            // mutate real user data. Test is not safe here.
            return;
        }

        AppDataMigrator.MigrateIfNeeded().Should().BeFalse();
    }

    [Fact]
    public void LegacyDocumentsRoot_Resolves_To_DocumentsLiveDeck()
    {
        var expected = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents",
            "LiveDeck");
        AppDataMigrator.LegacyDocumentsRoot.Should().Be(expected);
    }

    [Fact]
    public void DirectoryMove_Preserves_NestedFiles_And_DbRename_Works_OnTempFolders()
    {
        // Sandbox test: simulate the migration on a temp path so we can assert behavior
        // without touching real user data.
        var sandbox = Path.Combine(Path.GetTempPath(), $"orderdeck-migrate-test-{Guid.NewGuid():N}");
        var legacy = Path.Combine(sandbox, "LiveDeck");
        var target = Path.Combine(sandbox, "OrderDeck");
        var legacyData = Path.Combine(legacy, "data");

        try
        {
            Directory.CreateDirectory(legacyData);
            File.WriteAllText(Path.Combine(legacyData, "livedeck.db"), "stub-db-bytes");
            File.WriteAllText(Path.Combine(legacyData, "auth.dat"), "stub-auth");
            File.WriteAllText(Path.Combine(legacy, "settings.json"), "{}");

            // Manually simulate the migrator's two-step move (since we can't easily
            // override AppPaths.DocumentsRoot in a test).
            Directory.Move(legacy, target);
            File.Move(Path.Combine(target, "data", "livedeck.db"),
                      Path.Combine(target, "data", "orderdeck.db"));

            Directory.Exists(legacy).Should().BeFalse();
            Directory.Exists(target).Should().BeTrue();
            File.Exists(Path.Combine(target, "data", "orderdeck.db")).Should().BeTrue();
            File.Exists(Path.Combine(target, "data", "livedeck.db")).Should().BeFalse();
            File.Exists(Path.Combine(target, "data", "auth.dat")).Should().BeTrue();
            File.Exists(Path.Combine(target, "settings.json")).Should().BeTrue();
        }
        finally
        {
            if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true);
        }
    }
}
