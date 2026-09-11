using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public sealed class PaymentJobMigrationTests
{
    /// <summary>Gömülü migration script'lerini <paramref name="maxVersion"/>'a
    /// KADAR yükler — 033 dünyası kurulup veri ekildikten sonra tam koşu 034'ün
    /// taşıma SQL'ini gerçek veri üstünde sınar.</summary>
    private static IReadOnlyList<(int Version, string Sql)> EmbeddedScriptsUpTo(int maxVersion)
    {
        var asm = typeof(MigrationRunner).Assembly;
        const string prefix = "OrderDeck.Core.Storage.Migrations.";
        var list = new List<(int Version, string Sql)>();
        foreach (var name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
                !name.EndsWith(".sql", StringComparison.Ordinal))
                continue;
            var file = name.Substring(prefix.Length);
            var version = int.Parse(
                file.Substring(0, file.IndexOf('_')), CultureInfo.InvariantCulture);
            if (version > maxVersion) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((version, reader.ReadToEnd()));
        }
        return list.OrderBy(t => t.Version).ToList();
    }

    private static void SeedPending(
        InMemorySqlite fx, string customerId, Guid key, string total,
        long createdAt, long? resolvedAt)
    {
        using var conn = fx.Open();
        conn.Execute(
            @"INSERT INTO PendingBalanceApply
              (IdempotencyKey, CustomerId, ProductTotal, CreatedAt, ResolvedAt)
              VALUES (@k, @c, @t, @at, @r)",
            new { k = key.ToString("N"), c = customerId, t = total, at = createdAt, r = resolvedAt });
    }

    [Fact]
    public void Migration034_CozulmemisEnYeniKayit_LegacyIsineTasinir()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(33)).Run();

        var eskiKey = Guid.NewGuid();
        var yeniKey = Guid.NewGuid();
        var cozulmusKey = Guid.NewGuid();
        var digerKey = Guid.NewGuid();
        SeedPending(fx, "c1", eskiKey, "100.5", createdAt: 100, resolvedAt: null);
        SeedPending(fx, "c1", yeniKey, "250.75", createdAt: 200, resolvedAt: null); // en yeni → taşınır
        SeedPending(fx, "c1", cozulmusKey, "50", createdAt: 300, resolvedAt: 301);  // çözülmüş → atılır
        SeedPending(fx, "c2", digerKey, "10", createdAt: 150, resolvedAt: null);

        new MigrationRunner(fx).Run(); // kalan migration'lar (034 dahil)

        using var conn = fx.Open();
        var rows = conn.Query<(string CustomerId, string ScopeKey, string ProductTotal,
                string? ApplyKey, string State, long CreatedAt)>(
            @"SELECT CustomerId, ScopeKey, ProductTotal, ApplyKey, State, CreatedAt
              FROM PaymentJob ORDER BY CustomerId").ToList();

        rows.Should().HaveCount(2); // müşteri başına EN YENİ çözülmemiş satır
        var c1 = rows[0];
        c1.CustomerId.Should().Be("c1");
        c1.ScopeKey.Should().Be("legacy");
        c1.ProductTotal.Should().Be("250.75");
        c1.ApplyKey.Should().Be(yeniKey.ToString("N"));
        c1.State.Should().Be("apply_uncertain");
        c1.CreatedAt.Should().Be(200);
        rows[1].CustomerId.Should().Be("c2");
        rows[1].ApplyKey.Should().Be(digerKey.ToString("N"));

        // Eski tablo düşmüş olmalı.
        var eskiTablo = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PendingBalanceApply'");
        eskiTablo.Should().Be(0);

        var version = conn.ExecuteScalar<long>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(34);
    }

    [Fact]
    public void Migration034_UniqueScopeIndeksi_AyniKapsamdaIkinciSatiriReddeder()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx).Run();

        using var conn = fx.Open();
        const string insert =
            @"INSERT INTO PaymentJob
              (Id, CustomerId, ScopeKey, ProductTotal, Revision, State, CreatedAt, UpdatedAt)
              VALUES (@id, 'c1', 'session:s1', '10', 0, 'created', 1, 1)";
        conn.Execute(insert, new { id = Guid.NewGuid().ToString("N") });
        var act = () => conn.Execute(insert, new { id = Guid.NewGuid().ToString("N") });
        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>()
           .Which.SqliteErrorCode.Should().Be(19); // SQLITE_CONSTRAINT
    }
}
