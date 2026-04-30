using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;

namespace OrderDeck.Core.Storage;

/// <summary>
/// Applies embedded `Storage/Migrations/NNN_*.sql` scripts in lexical order, skipping
/// scripts whose number is already recorded in `_meta.SchemaVersion`. Each script must
/// end with `UPDATE _meta SET SchemaVersion = N WHERE Id = 1;` to advance the counter.
/// </summary>
public sealed class MigrationRunner
{
    private const string MigrationPrefix = "OrderDeck.Core.Storage.Migrations.";

    private readonly IDbConnectionFactory _factory;

    public MigrationRunner(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Run()
    {
        using var conn = _factory.Open();

        var hasMeta = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='_meta'") > 0;

        int currentVersion = hasMeta
            ? conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
            : 0;

        foreach (var (version, sql) in LoadEmbeddedScripts())
        {
            if (version <= currentVersion) continue;
            conn.Execute(sql);
            currentVersion = version;
        }
    }

    /// <summary>
    /// Yields (version, sql) pairs sorted by lexical filename. Filename pattern is
    /// `NNN_description.sql` where NNN is the integer version. Files that don't start
    /// with three digits are skipped.
    /// </summary>
    private static IEnumerable<(int Version, string Sql)> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix) && n.EndsWith(".sql"))
            .OrderBy(n => n);

        foreach (var name in resourceNames)
        {
            var leaf = name.Substring(MigrationPrefix.Length);
            if (leaf.Length < 4 || !int.TryParse(leaf.Substring(0, 3), out var version))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            yield return (version, reader.ReadToEnd());
        }
    }
}
