using System;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Göç 045'in SyncSeq sayacı (SyncSeqCounter): numara silinmeye dayanıklı sayaçtan verilir.
/// 036'nın MAX(SyncSeq)+1'i en büyük satır silinince geri gidiyordu — PR-3'ün yeniden
/// anahtarlaması yeni gönderilmiş kopyayı (çoğu zaman en büyük satır) siler; sonraki numaralar
/// gönderim imlecinin altına düşer, o düzenlemeler hiç gönderilmez ve bekleyen sayacı 0 gösterirdi.
/// Silmeler burada düz SQL (yeniden anahtarlamanın silmesinin yerine); numaraları gerçek
/// tetikleyiciler verir.
/// </summary>
public sealed class CustomerSyncSeqCounterTests
{
    private static (InMemorySqlite Db, CustomerRepository Repo) Fresh()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return (db, new CustomerRepository(db));
    }

    private static Customer Chat(string id, string username) => new(
        id, "tiktok", username, DisplayName: "takma", AvatarUrl: null,
        FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: false, BlacklistReason: null, Notes: null,
        TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: null);

    private static long Seq(InMemorySqlite db, string id)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT SyncSeq FROM Customer WHERE Id = @id", new { id });
    }

    private static long MaxSeq(InMemorySqlite db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT COALESCE(MAX(SyncSeq), 0) FROM Customer");
    }

    private static long Counter(InMemorySqlite db)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long>("SELECT Value FROM SyncSeqCounter WHERE Id = 1");
    }

    private static void Delete(InMemorySqlite db, string id)
    {
        using var c = db.Open();
        c.Execute("DELETE FROM Customer WHERE Id = @id", new { id });
    }

    /// <summary>İki müşteri; en büyük SyncSeq'li satır (yeni gönderilmiş kopya gibi) silinir.
    /// Dönen değer silinen en büyük numara — gönderim imleci tam burada durur.</summary>
    private static long WithMaxRowDeleted(InMemorySqlite db, CustomerRepository repo)
    {
        repo.Insert(Chat("a", "ayse"));
        repo.Insert(Chat("kopya", "Ayse"));
        var oldMax = MaxSeq(db);
        Seq(db, "kopya").Should().Be(oldMax, "kurulum: kopya en büyük satır");
        Delete(db, "kopya");
        MaxSeq(db).Should().BeLessThan(oldMax, "kurulum: MAX+1 artık imlecin altına düşerdi");
        return oldMax;
    }

    [Fact]
    public void En_buyuk_satir_silindikten_sonra_baska_musterinin_duzenlemesi_imlecin_onune_gecer()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        var oldMax = WithMaxRowDeleted(db, repo);

        repo.UpdateNotes("a", "iade");

        Seq(db, "a").Should().BeGreaterThan(oldMax, "düzenleme gönderim imlecinin ÖNÜNE geçmeli — yoksa hiç gönderilmez");
    }

    [Fact]
    public void En_buyuk_satir_silindikten_sonra_eklenen_satir_imlecin_onune_gecer()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        var oldMax = WithMaxRowDeleted(db, repo);

        repo.Insert(Chat("yeni", "mehmet"));

        Seq(db, "yeni").Should().BeGreaterThan(oldMax);
    }

    [Fact]
    public void Acik_ilerletme_en_buyuk_satir_silindikten_sonra_imlecin_onune_gecer()
    {
        // Yeniden anahtarlamanın sırası: kopya silinir, asıl kaydın SyncSeq'i AÇIKÇA ilerletilir
        // (U2) — taşınan birimler gönderilsin. MAX+1 burada imlecin altında kalırdı.
        var (db, repo) = Fresh();
        using var _d = db;
        var oldMax = WithMaxRowDeleted(db, repo);

        using (var conn = db.Open())
        using (var tx = conn.BeginTransaction())
        {
            CustomerSyncSeq.Bump(conn, tx, "a");
            tx.Commit();
        }

        Seq(db, "a").Should().BeGreaterThan(oldMax, "iki ifadenin ikisi de koşmalı: sayaç + satır");
        Seq(db, "a").Should().Be(Counter(db));
    }

    [Fact]
    public void Acik_ilerletme_islemsiz_cagrilamaz()
    {
        // İki ifade işlemsiz (autocommit) koşarsa arasına başka yazıcı girer ve iki satır AYNI
        // numarayı alır (F07 benzersizliği bozulur).
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("a", "ayse"));
        using var conn = db.Open();

        var bump = () => CustomerSyncSeq.Bump(conn, null!, "a");

        bump.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Tek_ifadede_guncellenen_uc_satir_ayri_numara_alir()
    {
        // F07: imleç eşitlik bozucusuz çalışır — tek UPDATE'in satırları da benzersiz numara alır.
        var (db, repo) = Fresh();
        using var _d = db;
        foreach (var id in new[] { "g1", "g2", "g3" })
            repo.Insert(Chat(id, id) with { GroupId = "G" });
        var before = MaxSeq(db);

        repo.SetGroupBlacklist("G", isBlacklisted: true, "ödemedi", blacklistedAt: 9000);

        var seqs = new[] { "g1", "g2", "g3" }.Select(id => Seq(db, id)).ToList();
        seqs.Should().OnlyHaveUniqueItems().And.OnlyContain(s => s > before);
    }

    [Fact]
    public void Sema_44_veritabaninda_sayac_mevcut_en_buyukten_baslar()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedMigrationScripts.UpTo(44)).Run();
        using (var c = db.Open())
            foreach (var id in new[] { "old1", "old2", "old3" })
                c.Execute(@"INSERT INTO Customer (Id, Platform, Username, FirstSeenAt, LastSeenAt)
                            VALUES (@id, 'tiktok', @id, 1, 1)", new { id });
        var oldMax = MaxSeq(db);
        oldMax.Should().BeGreaterThan(0, "kurulum: 036 numaraları verdi");

        new MigrationRunner(db).Run();

        Counter(db).Should().Be(oldMax, "sayaç bugünkü en büyük numaradan devam eder");
        new CustomerRepository(db).Insert(Chat("yeni", "mehmet"));
        Seq(db, "yeni").Should().BeGreaterThan(oldMax);
    }

    [Fact]
    public void Silme_sayaci_geri_almaz()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        repo.Insert(Chat("c2", "mehmet"));
        var before = Counter(db);
        before.Should().Be(MaxSeq(db));

        using (var c = db.Open())
            c.Execute("DELETE FROM Customer");

        Counter(db).Should().Be(before, "silme verilmiş numarayı geri almaz");
        repo.Insert(Chat("c3", "zeynep"));
        Seq(db, "c3").Should().BeGreaterThan(before);
    }
}
