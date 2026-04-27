using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;

namespace LiveDeck.Core.Storage;

/// <summary>
/// Applies embedded <c>Storage/Migrations/*.sql</c> scripts in lexical order. The current schema
/// version is stored in the <c>_meta</c> table; scripts are skipped if already applied.
/// </summary>
public sealed class MigrationRunner
{
    private const string MigrationPrefix = "LiveDeck.Core.Storage.Migrations.";

    private readonly IDbConnectionFactory _factory;

    public MigrationRunner(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Run()
    {
        using var conn = _factory.Open();

        var scripts = LoadEmbeddedScripts();

        foreach (var (name, sql) in scripts)
        {
            conn.Execute(sql);
        }
    }

    private static IEnumerable<(string Name, string Sql)> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix) && n.EndsWith(".sql"))
            .OrderBy(n => n);

        foreach (var name in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            yield return (name, reader.ReadToEnd());
        }
    }
}
