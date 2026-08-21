using System;
using System.IO;
using System.Linq;
using Dapper;
using OrderDeck.Core;
using OrderDeck.Core.Storage;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.Tests.Storage;

// GEÇİCİ: CI'daki "no such table: Giveaway" için teşhis. Sonuç okununca silinecek.
public sealed class BootDbDiagnostics
{
    private readonly ITestOutputHelper _out;
    public BootDbDiagnostics(ITestOutputHelper output) => _out = output;

    [Fact]
    public void Dump_state_of_the_real_app_database()
    {
        var db = AppPaths.DatabaseFile;
        _out.WriteLine($"PATH={db}");
        Dump("ONCE", db);

        try
        {
            AppPaths.EnsureDirectoriesExist();
            new MigrationRunner(new SqliteConnectionFactory(db)).Run();
            _out.WriteLine("RUN=ok");
        }
        catch (Exception ex)
        {
            _out.WriteLine("RUN=THREW " + ex);
        }

        Dump("SONRA", db);

        try
        {
            using var host = new global::OrderDeck.App.AppHost();
            _out.WriteLine("APPHOST=ok");
        }
        catch (Exception ex)
        {
            _out.WriteLine("APPHOST=THREW " + ex.Message);
        }

        Dump("APPHOST SONRASI", db);
    }

    private void Dump(string tag, string db)
    {
        _out.WriteLine($"--- {tag} ---");
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var p = db + suffix;
            _out.WriteLine($"  file '{Path.GetFileName(p)}' exists={File.Exists(p)} " +
                           $"size={(File.Exists(p) ? new FileInfo(p).Length : -1)}");
        }

        if (!File.Exists(db)) return;

        try
        {
            using var conn = new SqliteConnectionFactory(db).Open();
            _out.WriteLine("  journal=" + conn.ExecuteScalar<string>("PRAGMA journal_mode;"));

            var tables = conn.Query<string>(
                "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").ToList();
            _out.WriteLine("  tables=" + string.Join(",", tables));

            if (tables.Contains("_meta"))
                _out.WriteLine("  version=" +
                    conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1"));
        }
        catch (Exception ex)
        {
            _out.WriteLine("  DUMP THREW " + ex.Message);
        }
    }
}
