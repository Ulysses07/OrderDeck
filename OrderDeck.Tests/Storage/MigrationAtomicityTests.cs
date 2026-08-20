using System;
using System.Collections.Generic;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Göç script'leri ya tamamen uygulanır ya hiç uygulanmaz.
///
/// Öncesinde her ifade kendi örtük transaction'ında koşuyordu: script ortada
/// patlarsa ilk ifadeler kalıcı oluyor ama SchemaVersion ilerlemiyordu. Sonraki
/// açılışta aynı script baştan koşuyor ve "table already exists" ile ölüyordu —
/// veritabanı yarı göç etmiş, uygulama da bir daha hiç açılamaz hâlde kalıyordu.
/// </summary>
public class MigrationAtomicityTests
{
    private static IEnumerable<(int Version, string Sql)> Bootstrap() => new[]
    {
        (1, @"CREATE TABLE _meta (Id INTEGER PRIMARY KEY, SchemaVersion INTEGER NOT NULL);
              INSERT INTO _meta (Id, SchemaVersion) VALUES (1, 1);")
    };

    [Fact]
    public void Failed_script_leaves_no_partial_schema_behind()
    {
        using var db = new InMemorySqlite();

        var scripts = new List<(int, string)>(Bootstrap())
        {
            (2, @"CREATE TABLE Onceki (Id TEXT PRIMARY KEY);
                  CREATE TABLE Bozuk (Id TEXT PRIMARY KEY);
                  BU SATIR SQL DEGIL;
                  UPDATE _meta SET SchemaVersion = 2 WHERE Id = 1;")
        };

        var act = () => new MigrationRunner(db, scripts).Run();

        act.Should().Throw<Exception>(because: "bozuk script sessizce yutulmamalı");

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table'").AsList();

        tables.Should().NotContain("Onceki",
            because: "patlayan script'in ilk ifadeleri de geri alınmalı");
        tables.Should().NotContain("Bozuk");
        conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
            .Should().Be(1);
    }

    [Fact]
    public void Fixed_script_applies_cleanly_after_a_failure()
    {
        using var db = new InMemorySqlite();

        var broken = new List<(int, string)>(Bootstrap())
        {
            (2, @"CREATE TABLE Urun (Id TEXT PRIMARY KEY);
                  BU SATIR SQL DEGIL;
                  UPDATE _meta SET SchemaVersion = 2 WHERE Id = 1;")
        };
        try { new MigrationRunner(db, broken).Run(); } catch (Exception) { /* beklenen */ }

        // Aynı sürüm, düzeltilmiş hâliyle yeniden koşuyor. Geri alma çalıştıysa
        // "table Urun already exists" almadan geçmeli.
        var fixedScripts = new List<(int, string)>(Bootstrap())
        {
            (2, @"CREATE TABLE Urun (Id TEXT PRIMARY KEY);
                  UPDATE _meta SET SchemaVersion = 2 WHERE Id = 1;")
        };
        new MigrationRunner(db, fixedScripts).Run();

        using var conn = db.Open();
        conn.Query<string>("SELECT name FROM sqlite_master WHERE type='table'")
            .Should().Contain("Urun");
        conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
            .Should().Be(2);
    }
}
