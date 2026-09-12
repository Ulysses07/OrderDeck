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

/// <summary>
/// N03-g göçü (036): mevcut satırların numaralanması ve tetikleyicilerin
/// sayacı ilerletmesi. Bu dosya göçün KENDİSİNİ sınıyor — repo API'si üzerinden
/// davranış testleri <c>CustomerRepositoryTests</c>'te.
/// </summary>
public sealed class CustomerSyncSeqMigrationTests
{
    /// <summary>Gömülü script'leri <paramref name="maxVersion"/>'a KADAR yükler —
    /// 035 dünyası kurulup veri ekildikten sonra 036'nın geri doldurma SQL'i
    /// gerçek satırlar üstünde sınanabiliyor.</summary>
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

    private static void SeedCustomer(InMemorySqlite fx, string id, long lastSeenAt)
    {
        using var conn = fx.Open();
        conn.Execute(
            @"INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl,
                FirstSeenAt, LastSeenAt, IsBlacklisted, BlacklistReason, Notes,
                TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone)
              VALUES (@id, 'instagram', @id, NULL, NULL, @at, @at, 0, NULL, NULL, 0, 0, NULL, NULL, NULL)",
            new { id, at = lastSeenAt });
    }

    [Fact]
    public void Migration036_MevcutSatirlar_BenzersizArtanSyncSeq_Alir()
    {
        using var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(35)).Run();

        // Aynı saniyeye düşen satırlar dahil — eski imlecin kaybettiği desen.
        SeedCustomer(fx, "c1", 1000);
        SeedCustomer(fx, "c2", 1000);
        SeedCustomer(fx, "c3", 2000);

        new MigrationRunner(fx).Run();

        using var conn = fx.Open();
        var seqs = conn.Query<long>("SELECT SyncSeq FROM Customer ORDER BY SyncSeq").ToList();
        seqs.Should().OnlyHaveUniqueItems("imleç eşitlik bozucusuz çalışabilmeli");
        seqs.Should().AllSatisfy(s => s.Should().BeGreaterThan(0));
        conn.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id='c3'")
            .Should().Be(seqs.Max(), "en yeni LastSeenAt en büyük sırayı almalı");
    }

    [Fact]
    public void Migration036_GeriDoldurmaSonrasi_YeniSatir_EnBuyuktenBuyukSeqAlir()
    {
        // Tetikleyici MAX(SyncSeq)+1 yazıyor; geri doldurulan satırların üstüne
        // çıkmazsa yeni kayıtlar imlecin ALTINDA doğar ve hiç senkronlanmaz.
        using var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(35)).Run();
        SeedCustomer(fx, "c1", 1000);
        SeedCustomer(fx, "c2", 2000);

        new MigrationRunner(fx).Run();

        using var conn = fx.Open();
        var backfilledMax = conn.ExecuteScalar<long>("SELECT MAX(SyncSeq) FROM Customer");
        SeedCustomer(fx, "c3", 500); // iş zamanı GERİDE, sıra ileride olmalı
        conn.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id='c3'")
            .Should().BeGreaterThan(backfilledMax);
    }

    [Fact]
    public void Migration036_Tetikleyiciler_035_AramaTetikleyicisiyle_Catismaz()
    {
        // 035 SearchKey/PhoneKey yazıyor, 036 SyncSeq — hiçbiri öbürünün
        // "UPDATE OF" listesinde değil, yani özyineleme yok. İkisi de aynı
        // UPDATE'te güncellenebilmeli.
        using var fx = new InMemorySqlite();
        new MigrationRunner(fx).Run();
        SeedCustomer(fx, "c1", 1000);

        using var conn = fx.Open();
        var before = conn.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id='c1'");
        conn.Execute("UPDATE Customer SET FullName='Ayşe Yılmaz' WHERE Id='c1'");

        var after = conn.QueryFirst<(long SyncSeq, string SearchKey)>(
            "SELECT SyncSeq, SearchKey FROM Customer WHERE Id='c1'");
        after.SyncSeq.Should().BeGreaterThan(before);
        after.SearchKey.Should().Contain("yilmaz", "035 tetikleyicisi hâlâ çalışmalı");
    }
}
