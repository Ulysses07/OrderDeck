using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Sales;
using OrderDeck.Tests.Sales.Rules;
using OrderDeck.Tests.TestHelpers;
using Xunit;
using Xunit.Abstractions;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Türetilmiş müşteri toplamları (spec 2026-10-13 §6.1, §8.2-7; W1-03): <see cref="CustomerAggregateSql"/>
/// etiket sayısını ve ciroyu etiketlerden <see cref="LabelRules.Contribution"/> kuralıyla yeniden hesaplar.
/// Olay başına çağrılar <c>LabelServiceAggregateTests</c>'te, yeniden anahtarlama
/// <c>CustomerSyncRepositoryTests</c>'te.
/// </summary>
public sealed class CustomerAggregateSqlTests(ITestOutputHelper output) : IDisposable
{
    private readonly InMemorySqlite _db = Migrated();
    private int _label;

    private static InMemorySqlite Migrated()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        using var c = db.Open();
        c.Execute("INSERT INTO StreamSession (Id, StartedAt) VALUES ('s1', 1)");
        return db;
    }

    public void Dispose() => _db.Dispose();

    private string NewCustomer(int labels = 0, decimal amount = 0m, long lastSeenAt = 200)
    {
        var id = Guid.NewGuid().ToString("N");
        new CustomerRepository(_db).Insert(new Customer(id, "tiktok", $"alici{id[..8]}", "Takma", null,
            FirstSeenAt: 100, LastSeenAt: lastSeenAt, IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: labels, TotalAmount: amount, BlacklistedAt: null, Address: null, Phone: null));
        return id;
    }

    /// <summary>Etiketi kurala giren alanlarıyla doğrudan yazar (durum kurmak, akışı sınamak değil).</summary>
    private string Insert(string customerId, LabelFacts f)
    {
        var id = $"l{++_label:D6}";
        using var c = _db.Open();
        c.Execute(
            @"INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                                 PrintedAt, CancelledAt, CancelReason, IsTentativeBackup, IsShippingFee, ShipmentId)
              VALUES (@id, 's1', @customerId, 'tiktok', 'u', 'aldım', @code, @price, 1,
                      @printedAt, @cancelledAt, @reason, @tentative, @fee, @shipmentId)",
            new
            {
                id, customerId, code = f.Code, price = (double)f.Price,
                printedAt = f.Printed ? 1500L : (long?)null,
                cancelledAt = f.Cancelled ? 1600L : (long?)null,
                reason = f.Cancelled ? "customer" : null,
                tentative = f.Tentative ? 1 : 0,
                fee = f.ShippingFee ? 1 : 0,
                // Dosya kurala girmez; etiket yerelde bulunmayan bir dosyayı gösterebilir (FK yok).
                shipmentId = f.ShipmentId,
            });
        return id;
    }

    private string Printed(string customerId, decimal price)
        => Insert(customerId, new LabelFacts("x", "A1", price, Printed: true, Cancelled: false, Tentative: false,
            ShippingFee: false, ShipmentId: null));

    private (int Count, decimal Amount) Totals(string customerId)
    {
        var c = new CustomerRepository(_db).GetById(customerId)!;
        return (c.TotalLabelsPrinted, c.TotalAmount);
    }

    private void Drift(string customerId, int labels, decimal amount)
    {
        using var c = _db.Open();
        c.Execute("UPDATE Customer SET TotalLabelsPrinted = TotalLabelsPrinted + @labels, TotalAmount = TotalAmount + @amount WHERE Id = @customerId",
            new { customerId, labels, amount = (double)amount });
    }

    private AggregateRecomputeResult Recompute(params string[] ids)
    {
        using var write = DbWrite.Begin(_db);
        var r = CustomerAggregateSql.Recompute(ids, write);
        write.Commit();
        return r;
    }

    private AggregateRecomputeResult RecomputeAll()
    {
        using var write = DbWrite.Begin(_db);
        var r = CustomerAggregateSql.RecomputeAll(write);
        write.Commit();
        return r;
    }

    public static IEnumerable<object[]> ContributionNames()
        => LabelRulesVectors.Names("contribution").Select(n => new object[] { n });

    // ── eşlik: LabelRules.Contribution ─────────────────────────────────

    [Theory]
    [MemberData(nameof(ContributionNames))]
    public void Tek_etiketin_turetilen_toplami_Contribution_ile_ayni(string name)
    {
        var c = LabelRulesVectors.Contribution().Single(v => v.Name == name);
        var customer = NewCustomer(labels: 9, amount: 999m);           // kaymış sayaç: türetme onu ezer
        Insert(customer, c.Label);

        Recompute(customer);

        Totals(customer).Should().Be(LabelRules.Contribution(c.Label), $"vektör {name}");
        Totals(customer).Should().Be((c.ExpectedCount, c.ExpectedAmount), $"vektör {name}");
    }

    [Fact]
    public void Butun_vektorler_tek_musteride_Contribution_toplamini_verir()
    {
        var cases = LabelRulesVectors.Contribution();
        var customer = NewCustomer(labels: 3, amount: 12.5m);
        foreach (var c in cases) Insert(customer, c.Label);

        Recompute(customer);

        var expected = cases.Select(c => LabelRules.Contribution(c.Label))
            .Aggregate((Count: 0, Amount: 0m), (s, x) => (s.Count + x.Count, s.Amount + x.Amount));
        Totals(customer).Should().Be(expected);
    }

    // ── idempotentlik ve yalnız farklı satır ───────────────────────────

    [Fact]
    public void Iki_kez_yeniden_hesap_ikincide_sifir_fark()
    {
        var a = NewCustomer(labels: 4, amount: 10m);
        var b = NewCustomer();
        Printed(a, 0.1m);
        Printed(a, 0.2m);                    // ikili kayan noktada 0.1 + 0.2 ≠ 0.3: aynı ifade aynı sonucu vermeli
        Printed(b, 250m);

        var first = RecomputeAll();
        var second = RecomputeAll();
        var third = Recompute(a, b);

        first.ChangedRows.Should().Be(2);
        second.Should().Be(AggregateRecomputeResult.None, "aynı etiketlerden ikinci hesap yazmaz");
        third.Should().Be(AggregateRecomputeResult.None, "Id listesiyle hesap tam hesapla bit düzeyinde aynı");
        Totals(a).Should().Be((2, 0.3m));
    }

    [Fact]
    public void Yalniz_degeri_farkli_satir_yazilir_fark_toplami_mutlak_degerlerden()
    {
        var right = NewCustomer(labels: 1, amount: 100m);
        var high = NewCustomer(labels: 3, amount: 400m);
        var low = NewCustomer(labels: 0, amount: 0m);
        Printed(right, 100m);
        Printed(high, 100m);
        Printed(low, 50m);

        var r = RecomputeAll();

        r.ChangedRows.Should().Be(2);
        r.TotalAbsDiff.Should().Be(350m, "|400 − 100| + |0 − 50|");
        Totals(right).Should().Be((1, 100m));
        Totals(high).Should().Be((1, 100m));
        Totals(low).Should().Be((1, 50m));
    }

    [Fact]
    public void Id_listesi_yalniz_verilen_musterileri_hesaplar_bos_liste_yazmaz()
    {
        var a = NewCustomer(labels: 5, amount: 5m);
        var b = NewCustomer(labels: 5, amount: 5m);
        Printed(a, 70m);
        Printed(b, 80m);

        Recompute().Should().Be(AggregateRecomputeResult.None);
        Recompute(a, a).ChangedRows.Should().Be(1, "tekrarlanan Id bir kez");

        Totals(a).Should().Be((1, 70m));
        Totals(b).Should().Be((5, 5m), "listede olmayan müşteriye dokunulmaz");
    }

    [Fact]
    public void Tasinmis_Id_asil_kayda_cozulur()
    {
        var canonical = NewCustomer();
        var stale = Guid.NewGuid().ToString("N");
        Printed(canonical, 40m);
        using (var c = _db.Open())
            c.Execute("INSERT INTO CustomerRedirect (FromId, ToId, At) VALUES (@stale, @canonical, 1)", new { stale, canonical });

        Recompute(stale).ChangedRows.Should().Be(1);
        Totals(canonical).Should().Be((1, 40m));

        using (var write = DbWrite.Begin(_db))
        {
            CustomerAggregateSql.TouchLastSeen(new[] { stale }, 4242, write);
            write.Commit();
        }
        new CustomerRepository(_db).GetById(canonical)!.LastSeenAt.Should().Be(4242);
    }

    [Fact]
    public void Olmayan_musteri_Idsi_hicbir_sey_yazmaz()
        => Recompute(Guid.NewGuid().ToString("N")).Should().Be(AggregateRecomputeResult.None);

    [Fact]
    public void Yeniden_hesap_LastSeenAti_degistirmez_TouchLastSeen_yalniz_onu_yazar()
    {
        var a = NewCustomer(labels: 2, amount: 2m, lastSeenAt: 300);
        Printed(a, 15m);

        Recompute(a);
        new CustomerRepository(_db).GetById(a)!.LastSeenAt.Should().Be(300);

        using (var write = DbWrite.Begin(_db))
        {
            CustomerAggregateSql.TouchLastSeen(new[] { a }, 900, write);
            write.Commit();
        }
        var row = new CustomerRepository(_db).GetById(a)!;
        row.LastSeenAt.Should().Be(900);
        (row.TotalLabelsPrinted, row.TotalAmount).Should().Be((1, 15m));
    }

    // ── gönderim kuyruğu ve damgalar (acceptance 9) ────────────────────

    [Fact]
    public void Yeniden_hesap_ve_LastSeen_SyncSeqi_ve_045_damgalarini_degistirmez_musteri_yeniden_gonderilmez()
    {
        var a = NewCustomer(labels: 7, amount: 700m);
        Printed(a, 25m);
        var before = StampsAndSeq(a);
        var sync = new CustomerSyncRepository(_db);
        var cursor = sync.GetForPush(0, int.MaxValue).Max(r => r.SyncSeq);

        RecomputeAll().ChangedRows.Should().Be(1);
        Drift(a, 1, 1m);
        Recompute(a).ChangedRows.Should().Be(1);
        using (var write = DbWrite.Begin(_db))
        {
            CustomerAggregateSql.TouchLastSeen(new[] { a }, 5000, write);
            write.Commit();
        }

        StampsAndSeq(a).Should().Equal(before, "toplamlar ve LastSeenAt projeksiyonun birimi değil");
        sync.GetForPush(cursor, int.MaxValue).Should().BeEmpty("türetme müşteriyi yeniden göndermez");
    }

    private IReadOnlyList<object?> StampsAndSeq(string id)
    {
        using var c = _db.Open();
        var stampColumns = c.Query<string>("SELECT name FROM pragma_table_info('Customer') WHERE name LIKE '%ChangedAt'")
            .ToList();
        stampColumns.Should().NotBeEmpty();
        var row = (IDictionary<string, object?>)c.QuerySingle(
            $"SELECT SyncSeq, {string.Join(", ", stampColumns)} FROM Customer WHERE Id = @id", new { id });
        return row.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => kv.Value).ToList();
    }

    // ── açılışta bir kez tam hesap (§8.2-7) ────────────────────────────

    [Fact]
    public void Acilis_hesabi_bir_kez_kosar_kaymayi_duzeltir_damgayi_yazar()
    {
        var a = NewCustomer(labels: 3, amount: 333m);
        var b = NewCustomer();
        Printed(a, 100m);
        Printed(b, 20m);

        var first = CustomerAggregateSql.DeriveAllOnce(_db, nowUnix: 7000);

        first.Should().Be(new AggregateRecomputeResult(2, 253m), "|333 − 100| + |0 − 20|");
        Totals(a).Should().Be((1, 100m));
        Totals(b).Should().Be((1, 20m));
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().Be(7000);

        Drift(a, 1, 50m);                                   // yeni kayma: ikinci açılış artık dokunmaz
        CustomerAggregateSql.DeriveAllOnce(_db, nowUnix: 8000).Should().BeNull("damga yazıldı, ikinci açılış no-op");
        Totals(a).Should().Be((2, 150m));
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().Be(7000);
    }

    [Fact]
    public void Acilis_hesabi_kayma_yoksa_da_damgayi_yazar()
    {
        var a = NewCustomer(labels: 1, amount: 60m);
        Printed(a, 60m);

        CustomerAggregateSql.DeriveAllOnce(_db, nowUnix: 7000).Should().Be(AggregateRecomputeResult.None);
        new SalesSyncStateRepository(_db).Get().AggregatesDerivedAt.Should().Be(7000);
    }

    // ── prod boyu (acceptance 8) ───────────────────────────────────────

    [Fact]
    public void Prod_boyu_veritabaninda_acilis_hesabi_5_saniyenin_altinda()
    {
        var path = Path.Combine(Path.GetTempPath(), $"odagg-{Guid.NewGuid():N}.db");
        var factory = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(factory).Run();
            Seed(factory, customers: 20_000, sessions: 2_000, labels: 200_000);

            var sw = Stopwatch.StartNew();
            var r = CustomerAggregateSql.DeriveAllOnce(factory, nowUnix: 1_800_000_000);
            sw.Stop();
            output.WriteLine($"açılış hesabı: 20000 müşteri, 200000 etiket, {r!.ChangedRows} satır değişti, " +
                             $"fark {r.TotalAbsDiff}, {sw.ElapsedMilliseconds} ms");

            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "açılışta koşar — kullanıcı bekler");
            r.ChangedRows.Should().BeGreaterThan(0, "tohumdaki sayaçlar etiketlerle uyuşmuyor");
            using var c = factory.Open();
            c.ExecuteScalar<long>(@"
                SELECT COUNT(*) FROM Customer cu
                 WHERE cu.TotalLabelsPrinted <> (SELECT COUNT(*) FROM Label l WHERE l.CustomerId = cu.Id
                          AND l.PrintedAt IS NOT NULL AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0)")
                .Should().Be(0);
            using (var write = DbWrite.Begin(factory))
            {
                CustomerAggregateSql.RecomputeAll(write).Should().Be(AggregateRecomputeResult.None);
                write.Commit();
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    private static void Seed(IDbConnectionFactory factory, int customers, int sessions, int labels)
    {
        using var conn = factory.Open();
        using var tx = conn.BeginTransaction();
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @customers)
            INSERT INTO Customer (Id, Platform, Username, DisplayName, FirstSeenAt, LastSeenAt, TotalLabelsPrinted, TotalAmount)
            SELECT printf('c%05d', i), 'instagram', 'kullanici' || i, 'Takma ' || i, 1700000000, 1700000000,
                   i % 7, (i % 7) * 12.5
              FROM n", new { customers }, tx);
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @sessions)
            INSERT INTO StreamSession (Id, StartedAt, EndedAt) SELECT printf('s%04d', i), 1700000000 + i, 1700000100 + i FROM n",
            new { sessions }, tx);
        conn.Execute(@"
            WITH RECURSIVE n(i) AS (SELECT 0 UNION ALL SELECT i + 1 FROM n WHERE i + 1 < @labels)
            INSERT INTO Label (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt,
                               PrintedAt, CancelledAt, IsTentativeBackup, IsShippingFee)
            SELECT printf('l%06d', i), printf('s%04d', i % @sessions), printf('c%05d', (i * 13) % @customers),
                   'instagram', 'u', 'aldım', 'K' || (i % 300), (i % 40) * 12.5 + 0.1, 1700000000 + i,
                   CASE WHEN i % 4 <> 0 THEN 1700000500 + i END, CASE WHEN i % 17 = 0 THEN 1700000600 + i END,
                   CASE WHEN i % 58 = 0 THEN 1 ELSE 0 END, CASE WHEN i % 101 = 0 THEN 1 ELSE 0 END
              FROM n", new { labels, sessions, customers }, tx);
        tx.Commit();
    }
}
