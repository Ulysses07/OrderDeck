using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Göç 046: göç öncesi grup numaraları üyelerinden türetilir — aynı formları işlemiş iki
/// bilgisayar aynı kişi için aynı numarayı taşır. Her test 044 dünyası kurar (v0.9.8'in şeması,
/// <see cref="EmbeddedMigrationScripts.UpTo"/>), v0.9.8'in yazdığı gibi satır eker, sonra bu
/// sürümün tam göçünü koşar.
/// </summary>
public sealed class LegacyGroupIdMigrationTests
{
    private sealed record Row(string Platform, string Username, string? Phone, string? GroupId);

    /// <summary>Bir bilgisayarın v0.9.8 veritabanı: satır Id'leri de grup numaraları da o
    /// bilgisayarın kendi rastgele değerleri.</summary>
    private static InMemorySqlite Pc(params Row[] rows)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedMigrationScripts.UpTo(44)).Run();
        using var c = db.Open();
        foreach (var r in rows)
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt, Phone, GroupId)
                        VALUES (@id, @Platform, @Username, @Username, 1, 1, @Phone, @GroupId)",
                new { id = Guid.NewGuid().ToString("N"), r.Platform, r.Username, r.Phone, r.GroupId });
        return db;
    }

    private static string? GroupOf(InMemorySqlite db, string platform, string username)
    {
        using var c = db.Open();
        return c.ExecuteScalar<string?>(
            "SELECT GroupId FROM Customer WHERE Platform = @platform AND Username = @username",
            new { platform, username });
    }

    private static string NewGroup() => Guid.NewGuid().ToString("N");

    [Fact]
    public void Ayni_formu_isleyen_iki_bilgisayar_ayni_grup_numarasini_alir()
    {
        var phone = TestPhone.NewE164();
        string gA = NewGroup(), gB = NewGroup();
        using var a = Pc(new Row("instagram", "kisi_a", phone, gA), new Row("tiktok", "kisi_a_tt", phone, gA));
        using var b = Pc(new Row("instagram", "kisi_a", phone, gB), new Row("tiktok", "kisi_a_tt", phone, gB));

        new MigrationRunner(a).Run();
        new MigrationRunner(b).Run();

        var groupA = GroupOf(a, "instagram", "kisi_a");
        groupA.Should().NotBeNull().And.NotBe(gA);
        GroupOf(a, "tiktok", "kisi_a_tt").Should().Be(groupA, "kimse gruptan çıkmaz");
        GroupOf(b, "instagram", "kisi_a").Should().Be(groupA, "aynı üyeler → her bilgisayarda aynı numara");
        GroupOf(b, "tiktok", "kisi_a_tt").Should().Be(groupA);
        groupA.Should().MatchRegex("^[0-9a-f]{32}$", "form gruplarıyla aynı biçim (Guid \"N\")");
    }

    [Fact]
    public void Bilgisayara_ozgu_telefonsuz_sohbet_uyesi_numarayi_degistirmez()
    {
        // A'da elle eklenmiş bir sohbet satırı var, alfabede form kimliklerinden önce geliyor
        // ("facebook" < "instagram"); B'de yok. Çapa telefonlu üyelerden seçilir.
        var phone = TestPhone.NewE164();
        string gA = NewGroup(), gB = NewGroup();
        using var a = Pc(new Row("instagram", "kisi_b", phone, gA), new Row("facebook", "aaa_sohbet", null, gA));
        using var b = Pc(new Row("instagram", "kisi_b", phone, gB));

        new MigrationRunner(a).Run();
        new MigrationRunner(b).Run();

        GroupOf(a, "instagram", "kisi_b").Should().Be(GroupOf(b, "instagram", "kisi_b"));
        GroupOf(a, "facebook", "aaa_sohbet").Should().Be(GroupOf(a, "instagram", "kisi_b"),
            "bilgisayara özgü üye kendi grubunda kalır");
    }

    [Fact]
    public void Grup_bolumlemesi_korunur_grupsuz_satira_dokunulmaz()
    {
        string g1 = NewGroup(), g2 = NewGroup();
        using var db = Pc(
            new Row("instagram", "kisi_c", TestPhone.NewE164(), g1),
            new Row("youtube", "kisi_c_yt", null, g1),
            new Row("instagram", "kisi_d", TestPhone.NewE164(), g2),
            new Row("tiktok", "kisi_d_tt", null, g2),
            new Row("instagram", "grupsuz", null, null));

        new MigrationRunner(db).Run();

        var c = GroupOf(db, "instagram", "kisi_c");
        var d = GroupOf(db, "instagram", "kisi_d");
        c.Should().NotBe(d);
        GroupOf(db, "youtube", "kisi_c_yt").Should().Be(c);
        GroupOf(db, "tiktok", "kisi_d_tt").Should().Be(d);
        GroupOf(db, "instagram", "grupsuz").Should().BeNull();
    }

    [Fact]
    public void Damga_yazilmaz_sira_numarasi_ilerlemez_kilit_satiri_kalmaz()
    {
        using var db = Pc(new Row("instagram", "kisi_e", TestPhone.NewE164(), NewGroup()),
                          new Row("tiktok", "kisi_e_tt", null, null));
        Dictionary<string, long> SyncSeqs()
        {
            using var c = db.Open();
            return c.Query<(string Id, long Seq)>("SELECT Id, SyncSeq FROM Customer")
                .ToDictionary(r => r.Id, r => r.Seq);
        }
        var before = SyncSeqs();

        new MigrationRunner(db).Run();

        using var conn = db.Open();
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM Customer WHERE GroupIdChangedAt IS NOT NULL")
            .Should().Be(0, "göç damga uydurmaz (045 kural 1) — numara her bilgisayarda zaten aynı");
        SyncSeqs().Should().BeEquivalentTo(before,
            "ilk açılışın biçim-2 tam gönderimi satırları zaten götürür; göç sıra numarası tüketmez");
        conn.ExecuteScalar<long>("SELECT COUNT(*) FROM SyncApplyGuard").Should().Be(0);
        conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1").Should().Be(46);
    }

    [Fact]
    public void Damgali_gruba_dokunulmaz()
    {
        // 045 dünyasında (yeni sürümün açtığı) damgalı grup — form Id'li ya da elle birleştirilmiş.
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedMigrationScripts.UpTo(45)).Run();
        var stamped = NewGroup();
        using (var c = db.Open())
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt,
                                              Phone, GroupId, GroupIdChangedAt)
                        VALUES (@id, 'instagram', 'kisi_f', 'kisi_f', 'kisi_f', 1, 1, @phone, @stamped, 1791000000000)",
                new { id = Guid.NewGuid().ToString("N"), phone = TestPhone.NewE164(), stamped });

        new MigrationRunner(db).Run();

        GroupOf(db, "instagram", "kisi_f").Should().Be(stamped);
    }

    [Fact]
    public void Ayni_kimligin_iki_kopyasi_iki_grupta_ise_gruplar_birlesir()
    {
        // Yerelde aynı kimlik iki satırda (harf farkı) ve iki ayrı grupta: aynı kişi — PR-3'ün kopya
        // taşıması da onları tek satıra indirir. Çapa aynı → numara aynı.
        string g1 = NewGroup(), g2 = NewGroup();
        var phone = TestPhone.NewE164();
        using var db = Pc(new Row("instagram", "Kisi_G", phone, g1), new Row("instagram", "kisi_g", phone, g2),
                          new Row("tiktok", "kisi_g_tt", null, g2));

        new MigrationRunner(db).Run();

        GroupOf(db, "instagram", "Kisi_G").Should().Be(GroupOf(db, "instagram", "kisi_g"));
        GroupOf(db, "tiktok", "kisi_g_tt").Should().Be(GroupOf(db, "instagram", "kisi_g"));
    }

    [Fact]
    public void Numara_turetimi_sabit()
    {
        // Göç bir kez koşar; türetim sonradan değişirse sonra güncellenen bilgisayar öncekilerden ayrışır.
        // Altın değer: sha256("orderdeck-legacy-group|instagram|ornek")[:16 bayt].
        CustomerIdentity.LegacyGroupIdOf("instagram|ornek").Should().Be("66b3d49591df71904df4a1df0187c86e");
        CustomerIdentity.LegacyGroupIdOf("instagram|ornek")
            .Should().NotBe(CustomerIdentity.LegacyGroupIdOf("tiktok|ornek"));
    }

    [Fact]
    public void SQL_numarasi_CSharp_turetimiyle_ayni()
    {
        // Sözleşme SQL'de (çapa ayrıştırma) ve C#'ta: ikisi birlikte sabit.
        using var db = Pc(new Row("instagram", "kisi_h", TestPhone.NewE164(), NewGroup()));

        new MigrationRunner(db).Run();

        GroupOf(db, "instagram", "kisi_h").Should().Be(CustomerIdentity.LegacyGroupIdOf("instagram|kisi_h"));
    }

    [Fact]
    public void At_yazimi_bir_bilgisayarda_grupta_olsa_da_numara_ayni()
    {
        // A'da operatör "@kisi_i" satırına telefon girdi, form "kisi_i" aynı telefonla geldi → v0.9.8 telefonla
        // ikisini aynı gruba koydu. B'de yalnız form satırı var. '@' her harften önce sıralanır; çapada
        // sayılsaydı A "@kisi_i"ye, B "kisi_i"ye bağlanırdı.
        var phone = TestPhone.NewE164();
        string gA = NewGroup(), gB = NewGroup();
        using var a = Pc(new Row("instagram", "@kisi_i", phone, gA), new Row("instagram", "kisi_i", phone, gA));
        using var b = Pc(new Row("instagram", "kisi_i", phone, gB));

        new MigrationRunner(a).Run();
        new MigrationRunner(b).Run();

        GroupOf(a, "instagram", "@kisi_i").Should().Be(GroupOf(b, "instagram", "kisi_i"));
        GroupOf(a, "instagram", "kisi_i").Should().Be(GroupOf(b, "instagram", "kisi_i"));
        GroupOf(b, "instagram", "kisi_i").Should().Be(CustomerIdentity.LegacyGroupIdOf("instagram|kisi_i"));
    }

    [Fact]
    public void Damgali_ve_damgasiz_uyesi_olan_gruba_dokunulmaz()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db, EmbeddedMigrationScripts.UpTo(45)).Run();
        var group = NewGroup();
        using (var c = db.Open())
        {
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt,
                                              Phone, GroupId, GroupIdChangedAt)
                        VALUES (@id, 'instagram', 'kisi_j', 'kisi_j', 'kisi_j', 1, 1, @phone, @group, 1791000000000)",
                new { id = Guid.NewGuid().ToString("N"), phone = TestPhone.NewE164(), group });
            c.Execute(@"INSERT INTO Customer (Id, Platform, Username, IdentityKey, DisplayName, FirstSeenAt, LastSeenAt, GroupId)
                        VALUES (@id, 'tiktok', 'kisi_j_tt', 'kisi_j_tt', 'kisi_j_tt', 1, 1, @group)",
                new { id = Guid.NewGuid().ToString("N"), group });
            c.Execute("INSERT INTO SyncApplyGuard (Id) VALUES (1)");   // damgayı silerken yeniden basılmasın
            c.Execute("UPDATE Customer SET GroupIdChangedAt = NULL WHERE Username = 'kisi_j_tt'");
            c.Execute("DELETE FROM SyncApplyGuard");
        }

        new MigrationRunner(db).Run();

        GroupOf(db, "instagram", "kisi_j").Should().Be(group);
        GroupOf(db, "tiktok", "kisi_j_tt").Should().Be(group);
    }

    [Fact]
    public void Anahtarsiz_uye_grubuyla_birlikte_tasinir()
    {
        var g = NewGroup();
        using var db = Pc(new Row("instagram", "kisi_l", TestPhone.NewE164(), g), new Row("tiktok", "   ", null, g));

        new MigrationRunner(db).Run();

        GroupOf(db, "tiktok", "   ").Should().Be(GroupOf(db, "instagram", "kisi_l")).And.NotBe(g);
    }
}
