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

    // R4-04: eskiden müşteri başına yalnız EN YENİ çözülmemiş anahtar taşınıyor,
    // daha eskileri atılıyordu. Atılan anahtar sunucuda ledger PK olarak durur:
    // geçmişte fazla düşüm olduysa yerelde İZ KALMAZ ve PendingBalanceApply
    // hemen DROP edildiği için sonraki bir göç de kurtaramaz. Hepsi taşınmalı.
    [Fact]
    public void Migration034_CozulmemisTumKayitlar_AyriLegacyIslerineTasinir()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(33)).Run();

        var eskiKey = Guid.NewGuid();
        var yeniKey = Guid.NewGuid();
        var cozulmusKey = Guid.NewGuid();
        var digerKey = Guid.NewGuid();
        SeedPending(fx, "c1", eskiKey, "100.5", createdAt: 100, resolvedAt: null);
        SeedPending(fx, "c1", yeniKey, "250.75", createdAt: 200, resolvedAt: null);
        SeedPending(fx, "c1", cozulmusKey, "50", createdAt: 300, resolvedAt: 301);  // çözülmüş → atılır
        SeedPending(fx, "c2", digerKey, "10", createdAt: 150, resolvedAt: null);

        new MigrationRunner(fx).Run(); // kalan migration'lar (034 dahil)

        using var conn = fx.Open();
        var rows = conn.Query<(string CustomerId, string ScopeKey, string ProductTotal,
                string? ApplyKey, string State, long CreatedAt)>(
            @"SELECT CustomerId, ScopeKey, ProductTotal, ApplyKey, State, CreatedAt
              FROM PaymentJob ORDER BY CustomerId, CreatedAt").ToList();

        // Çözülmüş kayıt atılır; çözülmemişlerin HEPSİ kalır.
        rows.Should().HaveCount(3);

        var c1Eski = rows[0];
        c1Eski.CustomerId.Should().Be("c1");
        c1Eski.ScopeKey.Should().Be($"legacy:{eskiKey:N}");
        c1Eski.ProductTotal.Should().Be("100.5");
        c1Eski.ApplyKey.Should().Be(eskiKey.ToString("N"));
        c1Eski.State.Should().Be("apply_uncertain");
        c1Eski.CreatedAt.Should().Be(100);

        var c1Yeni = rows[1];
        c1Yeni.ScopeKey.Should().Be($"legacy:{yeniKey:N}");
        c1Yeni.ProductTotal.Should().Be("250.75");
        c1Yeni.ApplyKey.Should().Be(yeniKey.ToString("N"));
        c1Yeni.CreatedAt.Should().Be(200);

        rows[2].CustomerId.Should().Be("c2");
        rows[2].ApplyKey.Should().Be(digerKey.ToString("N"));

        // Eski tablo düşmüş olmalı.
        var eskiTablo = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='PendingBalanceApply'");
        eskiTablo.Should().Be(0);

        var version = conn.ExecuteScalar<long>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(47);
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

    // 047: göç öncesi teslim edilmiş istekler de "tekrar gönderilsin mi?" sorusunu tetiklemeli. Legacy
    // olmayan kapsamda ClosedAt'ı yalnız teslim yazar → DeliveredAt = ClosedAt; miras kapsamında kapanış
    // uzlaştırmadır, teslim sayılmaz.
    [Fact]
    public void Migration047_eski_teslimleri_teslim_kaydina_tasir()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(46)).Run();
        using (var seed = fx.Open())
            foreach (var (id, scope, closedAt) in new (string, string, long?)[]
                     {
                         ("teslim", "session:s1", 500), ("acik", "session:s2", null),
                         ("miras", "legacy:k:miras", 600), ("eski-miras", "legacy", 700),
                     })
                seed.Execute(
                    @"INSERT INTO PaymentJob (Id, CustomerId, ScopeKey, ProductTotal, Revision, State,
                                              CreatedAt, UpdatedAt, ClosedAt)
                      VALUES (@id, 'c1', @scope, '10', 0, 'no_balance', 1, 1, @closedAt)",
                    new { id, scope, closedAt });

        new MigrationRunner(fx).Run();

        using var conn = fx.Open();
        var rows = conn.Query<(string Id, long? DeliveredAt, long Deliveries, string? DeliveredNet, long SendAttempts)>(
                "SELECT Id, DeliveredAt, Deliveries, DeliveredNet, SendAttempts FROM PaymentJob")
            .ToDictionary(r => r.Id);
        rows["teslim"].Should().Be(("teslim", (long?)500, 1L, (string?)"10", 1L),
            "net göçte dondurulur; teslim bir gönderim denemesidir (SendAttempts ≥ Deliveries)");
        rows["acik"].DeliveredAt.Should().BeNull();
        rows["acik"].Deliveries.Should().Be(0);
        rows["acik"].SendAttempts.Should().Be(0, "sonucu yazılmamış deneme yok");
        rows["miras"].DeliveredAt.Should().BeNull("miras kapanışı uzlaştırmadır");
        rows["eski-miras"].DeliveredAt.Should().BeNull();
    }

    // Son inceleme RV3: göç öncesi teslimin neti okurken işin GÜNCEL alanlarından türetiliyordu. İş sonra
    // yeniden açılınca (panel iadesi → R6-02 dış iade benimsemesi; revizyon; geri alma) alanlar değişir ve
    // "tekrar gönderilsin mi?" sorusu müşteriden hiç istenmemiş tutarı gösterirdi (200 istenmişken 300).
    // Göç neti DONDURUR — kayıpsız: tutarlar TEXT ondalık, SQL aritmetiği REAL'e çevirip yuvarlardı.
    [Fact]
    public void Migration047_teslim_netini_kayipsiz_dondurur_yeniden_acilinca_degismez()
    {
        var fx = new InMemorySqlite();
        new MigrationRunner(fx, EmbeddedScriptsUpTo(46)).Run();
        var key = Guid.NewGuid();
        using (var seed = fx.Open())
            foreach (var (id, total, applied, applyKey) in new (string, string, string?, string?)[]
                     {
                         ("dusumlu", "300", "100", key.ToString("N")), ("kurus", "0.3", "0.1", null),
                         ("kesirli", "250.50", "0", null),
                     })
                seed.Execute(
                    @"INSERT INTO PaymentJob (Id, CustomerId, ScopeKey, ProductTotal, Revision, ApplyKey,
                                              AppliedAmount, State, CreatedAt, UpdatedAt, ClosedAt)
                      VALUES (@id, @id, 'session:s1', @total, 0, @applyKey, @applied, 'applied', 1, 1, 500)",
                    new { id, total, applied, applyKey });

        new MigrationRunner(fx).Run();

        using (var conn = fx.Open())
            conn.Query<(string Id, string? DeliveredNet)>("SELECT Id, DeliveredNet FROM PaymentJob")
                .ToDictionary(r => r.Id, r => r.DeliveredNet)
                .Should().BeEquivalentTo(new Dictionary<string, string?>
                {
                    ["dusumlu"] = "200", ["kurus"] = "0.2", ["kesirli"] = "250.50",
                }, "REAL aritmetiği 0.3 − 0.1'i 0.19999999999999998 yapardı");

        var repo = new OrderDeck.Core.Storage.Repositories.PaymentJobRepository(fx);
        repo.Get("dusumlu")!.LastRequestedNet.Should().Be(200m);
        repo.AdoptExternalReversal("dusumlu", key, expectedRevision: 0).Should().BeTrue("panel iadesi");
        var acik = repo.Get("dusumlu")!;
        acik.ClosedAt.Should().BeNull();
        acik.AppliedAmount.Should().BeNull();
        acik.LastRequestedNet.Should().Be(200m, "müşteriden istenen 300 − 100 — işin yeni alanları (300) değil");
        acik.PromptNet.Should().Be(200m);
    }
}
