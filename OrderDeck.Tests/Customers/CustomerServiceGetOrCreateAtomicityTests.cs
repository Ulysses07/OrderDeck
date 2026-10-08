using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Customers;

/// <summary>
/// U12 / C8 incelemesi: paket verilmeden çağrılan <see cref="CustomerService.GetOrCreate"/>
/// (çekiliş katılımı, elle kara liste) yazan dalını kendi IMMEDIATE işleminde koşar — arama
/// işlemin İÇİNDE yinelenir, yani aynı kullanıcı adını eşzamanlı ekleyen başka bir yazıcı
/// (akıştan inen satır, ikinci sohbet kaynağı) UNIQUE çakışması üretmez. Bilinen müşteri yolu
/// yazma kilidi almaz: çekiliş katılımı arayüz iş parçacığında koşar.
/// </summary>
public sealed class CustomerServiceGetOrCreateAtomicityTests
{
    private static CustomerService Service(IDbConnectionFactory db) =>
        new(new CustomerRepository(db), new SessionRepository(db), new LabelRepository(db),
            Mock.Of<IClock>(c => c.UnixNow() == 1000L));

    private static async Task WithFileDb(Func<SqliteConnectionFactory, string, Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"od-getorcreate-{Guid.NewGuid():N}.db");
        var db = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(db).Run();
            await body(db, path);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }

    [Fact]
    public void Yeni_musteri_ve_YouTube_benimsemesi_tek_islemde()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new CustomerRepository(db).Insert(new Customer(Guid.NewGuid().ToString("N"), "youtube", "ornekkanal",
            "Örnek Müşteri", null, 1, 1, false, null, null, 0, 0m, null, null, null, GroupId: "grup-1"));
        using (var c = db.Open())
            c.Execute(@"CREATE TRIGGER grup_bozuk BEFORE UPDATE OF GroupId ON Customer
                        BEGIN SELECT RAISE(ABORT, 'disk dolu'); END");
        var channel = "UC" + Guid.NewGuid().ToString("N");

        var act = () => Service(db).GetOrCreate("youtube", channel, "@ornekkanal", null);

        act.Should().Throw<SqliteException>();
        new CustomerRepository(db).FindByPlatformAndUsername("youtube", channel).Should().BeNull(
            "satır ve benimseme aynı işlemde — yarım kalan satır grubu dışında, kara listesiz kalmaz");
    }

    [Fact]
    public Task Bilinen_musteri_yolu_yazma_kilidi_beklemez() => WithFileDb((db, path) =>
    {
        var known = Guid.NewGuid().ToString("N");
        new CustomerRepository(db).Insert(new Customer(known, "tiktok", "bilinen", null, null, 1, 1,
            false, null, null, 0, 0m, null, null, null));

        // Başka bir yazıcı (ör. akış turu) yazma kilidini tutuyor.
        using var blocker = new SqliteConnection($"Data Source={path}");
        blocker.Open();
        blocker.Execute("BEGIN IMMEDIATE");
        try
        {
            var sw = Stopwatch.StartNew();
            var c = Service(db).GetOrCreate("tiktok", "bilinen", null, null);

            c.Id.Should().Be(known);
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5),
                "bilinen müşteri okumayla döner — arayüz iş parçacığı kilit beklemez (WAL)");
        }
        finally
        {
            blocker.Execute("ROLLBACK");
        }
        return Task.CompletedTask;
    });

    [Fact]
    public Task Ayni_yeni_kullanici_adini_eszamanli_iki_cagri_tek_satirda_bulusur() => WithFileDb(async (db, _) =>
    {
        var service = Service(db);
        for (var i = 0; i < 15; i++)
        {
            var username = $"yeni_kisi_{i}";
            using var barrier = new Barrier(2);
            Customer Run()
            {
                barrier.SignalAndWait(TimeSpan.FromSeconds(30)).Should().BeTrue();
                return service.GetOrCreate("tiktok", username, null, null);   // UNIQUE çakışması burada patlardı
            }

            var results = await Task.WhenAll(Task.Run(Run), Task.Run(Run));

            results[0].Id.Should().Be(results[1].Id, $"tur {i}: iki çağrı aynı satırı görür");
            using var c = db.Open();
            c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer WHERE Username = @username", new { username })
                .Should().Be(1);
        }
    });
}
