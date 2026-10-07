using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Göç 045: birim damgaları, SyncApplyGuard, kimlik anahtarı. Göçün KENDİSİNİ
/// sınar; yazan yolların damgaları CustomerStampedWritersTests'te.
/// </summary>
public sealed class CustomerUnitStampsMigrationTests
{
    private static readonly string[] StampColumns =
    {
        "FullNameChangedAt", "DisplayNameChangedAt", "GroupIdChangedAt", "AddressChangedAt",
        "RecipientPaysChangedAt", "PhoneChangedAt", "EmailChangedAt", "TcknChangedAt",
        "WhatsAppConsentChangedAt", "SmsConsentChangedAt", "BlacklistChangedAt", "NotesChangedAt",
    };

    /// <summary>CustomerSyncSeqMigrationTests'teki yardımcının aynısı: 044 dünyası
    /// kurulup veri ekildikten sonra 045 gerçek satırlar üstünde sınanır.</summary>
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
            var version = int.Parse(file.Substring(0, file.IndexOf('_')), CultureInfo.InvariantCulture);
            if (version > maxVersion) continue;
            using var stream = asm.GetManifestResourceStream(name)!;
            using var reader = new StreamReader(stream);
            list.Add((version, reader.ReadToEnd()));
        }
        return list.OrderBy(t => t.Version).ToList();
    }

    private static (InMemorySqlite Db, CustomerRepository Repo) Fresh()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return (db, new CustomerRepository(db));
    }

    private static long? Stamp(InMemorySqlite db, string id, string column)
    {
        using var c = db.Open();
        return c.ExecuteScalar<long?>($"SELECT {column} FROM Customer WHERE Id = @id", new { id });
    }

    private static long NowMs() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    private static Customer Chat(string id, string username) => new(
        id, "tiktok", username, DisplayName: "takma", AvatarUrl: null,
        FirstSeenAt: 1, LastSeenAt: 1, IsBlacklisted: false, BlacklistReason: null, Notes: null,
        TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null, Address: null, Phone: null);

    [Fact]
    public void Mevcut_satirlar_damgalanmaz_kimlik_anahtari_doldurulur()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedScriptsUpTo(44)).Run();
        using (var c = db.Open())
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt,
                            Address, City, Notes, IsBlacklisted, WhatsAppConsent, RecipientPaysActive)
                        VALUES ('old1', 'instagram', '  Ayse.KAYA ', 'takma', 1, 5,
                                'adres', 'İzmir', 'not', 1, 1, 1)");

        new MigrationRunner(db).Run();

        foreach (var col in StampColumns)
            Stamp(db, "old1", col).Should().BeNull(
                $"{col}: LastSeenAt iş zamanıdır, düzenleme anı değil — göç damga UYDURMAZ");
        using var conn = db.Open();
        conn.ExecuteScalar<string>("SELECT IdentityKey FROM Customer WHERE Id = 'old1'")
            .Should().Be("ayse.kaya");
        conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(45);
    }

    [Fact]
    public void Goc_hicbir_imleci_silmez()
    {
        // U15: önceki sürüme dönüşte o sürüm kendi imleçleriyle kaldığı yerden sürer; bu sürüm
        // biçim-2 gönderimini kendi imleciyle yapar (satırı yok → ilk açılışta tam gönderim).
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedScriptsUpTo(44)).Run();
        var lisans = $"lisans-{Guid.NewGuid():N}";
        var cursors = new SyncCursorRepository(db);
        cursors.Upsert("customer-projection-out", lisans, seq: 99);
        cursors.Upsert("shopper-ingest-in", lisans, updatedAt: DateTimeOffset.UtcNow, lastId: Guid.NewGuid());
        cursors.Upsert("intake-form-in", lisans, updatedAt: DateTimeOffset.UtcNow, lastId: Guid.NewGuid());

        new MigrationRunner(db).Run();

        cursors.Get("customer-projection-out", lisans)!.Seq.Should().Be(99, "önceki sürümün gönderim imleci");
        cursors.Get("shopper-ingest-in", lisans).Should().NotBeNull("önceki sürümün ingest imleci");
        cursors.Get("intake-form-in", lisans).Should().NotBeNull(
            "form imleci korunur — eski formlar yeniden oynatılmaz");
        cursors.Get("customer-projection-out-v2", lisans).Should().BeNull(
            "bu sürüm ilk açılışta tüm müşterileri biçim 2 ile gönderir");
    }

    [Fact]
    public void Sohbetten_acilan_satir_yalniz_takma_adi_damgalar()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        var before = NowMs();
        repo.Insert(Chat("c1", "ayse"));
        var after = NowMs();

        Stamp(db, "c1", "DisplayNameChangedAt").Should().BeInRange(before - 1000, after + 1000);
        foreach (var col in StampColumns.Where(c => c != "DisplayNameChangedAt"))
            Stamp(db, "c1", col).Should().BeNull(
                $"{col} boş birim — damgalansaydı başka bilgisayarın girdiği değeri boşla ezerdi");
    }

    [Fact]
    public void Dolu_adres_blogu_tek_damga_alir_false_bayrak_damgalanmaz()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse") with { Address = "Atatürk Cd. 1", City = "İzmir" });

        Stamp(db, "c1", "AddressChangedAt").Should().NotBeNull();
        Stamp(db, "c1", "RecipientPaysChangedAt").Should().BeNull("false bayrak boş birimdir");
        Stamp(db, "c1", "WhatsAppConsentChangedAt").Should().BeNull();
        Stamp(db, "c1", "BlacklistChangedAt").Should().BeNull();
    }

    [Fact]
    public void Not_degisince_yalniz_not_damgalanir_ve_SyncSeq_ilerler()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        var seq = repo.GetById("c1")!.SyncSeq;

        repo.UpdateNotes("c1", "kargo kapıya");

        Stamp(db, "c1", "NotesChangedAt").Should().NotBeNull();
        Stamp(db, "c1", "PhoneChangedAt").Should().BeNull();
        repo.GetById("c1")!.SyncSeq.Should().BeGreaterThan(seq);
    }

    [Fact]
    public void Ayni_degeri_yeniden_yazmak_damgayi_degistirmez()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        repo.UpdateNotes("c1", "not");
        var first = Stamp(db, "c1", "NotesChangedAt");

        repo.UpdateNotes("c1", "not");

        Stamp(db, "c1", "NotesChangedAt").Should().Be(first);
    }

    [Fact]
    public void Acikca_yazilan_damga_tetikleyicice_ezilmez()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        using (var c = db.Open())
            c.Execute("UPDATE Customer SET Address = 'x', AddressChangedAt = 42 WHERE Id = 'c1'");

        Stamp(db, "c1", "AddressChangedAt").Should().Be(42);
    }

    [Fact]
    public void Ileri_damgali_birimde_duzenleme_damgayi_bir_ilerletir()
    {
        // Saati ileri bir bilgisayardan inmiş damga yerelde dururken yapılan
        // düzenleme, gördüğü değeri YENMELİ (U1) — yoksa sunucu "eski" der.
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        var future = NowMs() + 3_600_000;
        using (var c = db.Open())
            c.Execute("UPDATE Customer SET Notes = 'ileri', NotesChangedAt = @future WHERE Id = 'c1'",
                new { future });

        repo.UpdateNotes("c1", "şimdi");

        Stamp(db, "c1", "NotesChangedAt").Should().Be(future + 1);
    }

    [Fact]
    public void Kara_liste_blogu_tek_damga_alir()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));

        repo.UpdateBlacklist("c1", isBlacklisted: true, "ödemedi", blacklistedAt: 9000);

        Stamp(db, "c1", "BlacklistChangedAt").Should().NotBeNull();
        Stamp(db, "c1", "NotesChangedAt").Should().BeNull();
    }

    [Fact]
    public void Kilit_altindaki_guncelleme_damgalamaz_ve_SyncSeq_ilerletmez()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        var seq = repo.GetById("c1")!.SyncSeq;

        using (var scope = SyncApplyScope.Begin(db))
        {
            scope.Execute("UPDATE Customer SET Notes = 'sunucu', Email = 'a@example.test' WHERE Id = 'c1'");
            scope.Commit();
        }

        Stamp(db, "c1", "NotesChangedAt").Should().BeNull();
        Stamp(db, "c1", "EmailChangedAt").Should().BeNull();
        repo.GetById("c1")!.SyncSeq.Should().Be(seq, "sunucudan inen veri yankı olarak geri gönderilmez (U2)");
        using var conn = db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
    }

    [Fact]
    public void Kilit_altindaki_ekleme_damgalamaz_ama_SyncSeq_benzersiz_numaralanir()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));

        using (var scope = SyncApplyScope.Begin(db))
        {
            scope.Execute(
                @"INSERT INTO Customer (Id, Platform, Username, FullName, FirstSeenAt, LastSeenAt)
                  VALUES ('s1', 'tiktok', 'mehmet', 'Mehmet', 1, 1)");
            scope.Commit();
        }

        Stamp(db, "s1", "FullNameChangedAt").Should().BeNull();
        using var conn = db.Open();
        conn.Query<long>("SELECT SyncSeq FROM Customer").Should().OnlyHaveUniqueItems(
            "036/F07: imleç eşitlik bozucusuz çalışır — eklemede numara her zaman verilir");
    }

    [Fact]
    public void Kilit_ayni_islemde_ikinci_kez_acilamaz_ve_geri_alinan_islemde_kalmaz()
    {
        var (db, _) = Fresh();
        using var _d = db;
        using (var scope = SyncApplyScope.Begin(db))
        {
            var again = () => scope.Execute("INSERT INTO SyncApplyGuard (Id) VALUES (1)");
            again.Should().Throw<SqliteException>();
            // Commit yok → Dispose işlemi (kilit satırı dahil) geri alır.
        }

        using var conn = db.Open();
        conn.ExecuteScalar<int>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
    }

    [Fact]
    public void Kalmis_kilit_satiri_temizlenir_damgalama_geri_gelir()
    {
        // U15: kapsamlar kilit satırını commit'ten ÖNCE siler; görünen her satır bir
        // hatanın artığıdır ve bütün damgalamayı/gönderimi sessizce kapatır.
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        using (var c = db.Open())
            c.Execute("INSERT INTO SyncApplyGuard (Id) VALUES (1)");

        repo.UpdateNotes("c1", "kilitliyken");
        Stamp(db, "c1", "NotesChangedAt").Should().BeNull("kalmış kilit damgalamayı kapatır — bu yüzden temizlenir");

        SyncApplyScope.ClearStale(db).Should().Be(1);
        SyncApplyScope.ClearStale(db).Should().Be(0);
        repo.UpdateNotes("c1", "temizlendikten sonra");
        Stamp(db, "c1", "NotesChangedAt").Should().NotBeNull();
    }

    [Fact]
    public void Mezar_tasina_kimlik_anahtari_eklenir_yonlendirme_ve_akis_hatasi_tablolari_kurulur()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedScriptsUpTo(44)).Run();
        using (var c = db.Open())
            c.Execute("INSERT INTO CustomerPurgeTombstone (Platform, Username, PurgedAt) VALUES ('instagram', 'ŞEYMA', 5000)");

        new MigrationRunner(db).Run();

        using var conn = db.Open();
        conn.ExecuteScalar<string>("SELECT IdentityKey FROM CustomerPurgeTombstone").Should().Be("şeyma",
            "U16: NOCASE 'ŞEYMA' ile 'şeyma'yı eşlemez, kimlik anahtarı eşler");
        conn.Execute("INSERT INTO CustomerRedirect (FromId, ToId, At) VALUES ('a', 'b', 1)");
        var twice = () => conn.Execute("INSERT INTO CustomerRedirect (FromId, ToId, At) VALUES ('a', 'c', 2)");
        twice.Should().Throw<SqliteException>("bir Id tek yere yönlenir");
        conn.Execute("INSERT INTO CustomerFeedFailure (ItemId, ChangeSeq, Attempts, FirstFailedAt) VALUES ('x', 1, 1, 1)");
        conn.ExecuteScalar<long?>("SELECT SkippedAt FROM CustomerFeedFailure").Should().BeNull();
    }

    [Fact]
    public void Etiket_sayaci_damgayi_ve_SyncSeqi_degistirmez()
    {
        var (db, repo) = Fresh();
        using var _d = db;
        repo.Insert(Chat("c1", "ayse"));
        var seq = repo.GetById("c1")!.SyncSeq;
        var stamp = Stamp(db, "c1", "DisplayNameChangedAt");

        repo.IncrementLabelStats("c1", 1, 250m, lastSeenAt: 2000);

        repo.GetById("c1")!.SyncSeq.Should().Be(seq, "etiket basımı sıcak yol, projeksiyonu değiştirmiyor");
        Stamp(db, "c1", "DisplayNameChangedAt").Should().Be(stamp);
    }
}
