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
/// davranış testleri <c>CustomerRepositoryTests</c>'te. 035 dünyası
/// (<see cref="EmbeddedMigrationScripts.UpTo"/>) kurulup veri ekildikten sonra
/// 036'nın geri doldurma SQL'i gerçek satırlar üstünde sınanır.
/// </summary>
public sealed class CustomerSyncSeqMigrationTests
{
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
        new MigrationRunner(fx, EmbeddedMigrationScripts.UpTo(35)).Run();

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
        // Tam göç zincirinden sonra numarayı 045'in sayacı verir (036'nın MAX(SyncSeq)+1'inin
        // yerine; sayaç geri doldurulan en büyük numarayla tohumlanır). Yeni kayıt geri
        // doldurulan satırların üstüne çıkmazsa imlecin ALTINDA doğar ve hiç senkronlanmaz.
        using var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedMigrationScripts.UpTo(35)).Run();
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
        conn.Execute("UPDATE Customer SET FullName='Deneme Alıcı' WHERE Id='c1'");

        var after = conn.QueryFirst<(long SyncSeq, string SearchKey)>(
            "SELECT SyncSeq, SearchKey FROM Customer WHERE Id='c1'");
        after.SyncSeq.Should().BeGreaterThan(before);
        after.SearchKey.Should().Contain("alici", "035 tetikleyicisi hâlâ çalışmalı");
    }
}
