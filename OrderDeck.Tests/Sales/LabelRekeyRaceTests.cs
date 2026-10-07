using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Moq;
using OrderDeck.Core.Chat;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>U12: etiket ekleme ile yerel taşımanın yarışı. İki bilgisayar aynı
/// yayında yorum okurken push yanıtı o an yorum yazanın satırını taşır.</summary>
public sealed class LabelRekeyRaceTests
{
    private static ChatMessage Msg(string username) =>
        new(Guid.NewGuid().ToString("N"), "tiktok", null, username, "Örnek Müşteri", null, "MAVI XL aldım", 1000,
            Array.Empty<string>());

    private static LabelService Service(IDbConnectionFactory db)
    {
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        var labels = new LabelRepository(db);
        var customers = new CustomerService(new CustomerRepository(db), new SessionRepository(db), labels, clock);
        return new LabelService(labels, customers, db, clock);
    }

    [Fact]
    public void Add_musteriyi_ve_etiketi_tek_islemde_yazar()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "tiktok" }, null));
        using (var c = db.Open())
            c.Execute("CREATE TRIGGER etiket_bozuk BEFORE INSERT ON Label BEGIN SELECT RAISE(ABORT, 'disk dolu'); END");

        var act = () => Service(db).Add("s1", Msg("yeni_kisi"), 10m, null);

        act.Should().Throw<SqliteException>();
        new CustomerRepository(db).FindByPlatformAndUsername("tiktok", "yeni_kisi").Should().BeNull(
            "müşteri satırı etiketle aynı işlemde — biri düşerse ikisi de geri alınır; taşıma araya giremez");
    }

    [Fact]
    public void Add_YouTube_benimsemesi_paketin_icinde_ikinci_baglanti_acmaz()
    {
        // U17: GetOrCreate'in bütün depo çağrıları (arama, ekleme, grup benimseme, grup kara
        // listesi, son okuma) paketin bağlantısından. Birini unutmak testte WriteScopeGuard
        // hatası, üretimde kendi yazma kilidini 10 sn bekleyip SQLITE_BUSY = kayıp satış.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "youtube" }, null));
        var customers = new CustomerRepository(db);
        customers.Insert(new Customer(Guid.NewGuid().ToString("N"), "youtube", "ornekkanal", "Örnek Müşteri", null,
            1, 1, true, "ödemedi", null, 0, 0m, 500, null, null, GroupId: "grup-1"));
        var msg = new ChatMessage(Guid.NewGuid().ToString("N"), "youtube", null, "UC" + Guid.NewGuid().ToString("N"),
            "@ornekkanal", null, "MAVI XL aldım", 1000, Array.Empty<string>());

        var label = Service(db).Add("s1", msg, 10m, null);

        var adopted = customers.GetById(label.CustomerId)!;
        adopted.GroupId.Should().Be("grup-1", "kanal satırı formdaki @handle'ın grubuna benimsendi");
        adopted.IsBlacklisted.Should().BeTrue("grup kara listesi aynı pakette yayıldı");
    }

    [Fact]
    public void Add_tasinmis_musterinin_adiyla_gelen_yorumu_asil_kayda_yazar()
    {
        // Taşıma önce koştu: sohbet kopyanın birebir adını bulamaz, kimlik anahtarıyla
        // asıl kaydı bulur (U7) — yeni satır açılmaz, etiket asıl kayda gider.
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "tiktok" }, null));
        var customers = new CustomerRepository(db);
        var copy = Guid.NewGuid().ToString("N");
        var canonical = Guid.NewGuid().ToString("N");
        customers.Insert(new Customer(copy, "tiktok", "ayse", null, null, 1, 1, false, null, null, 0, 0m, null, null, null));
        customers.Insert(new Customer(canonical, "tiktok", "Ayse", null, null, 1, 1, false, null, null, 0, 0m, null, null, null));
        new CustomerSyncRepository(db).RekeyToLocal(copy, canonical, pushedThroughSeq: long.MaxValue, nowUnix: 1_791_000_000)
            .Should().Be(RekeyResult.Rekeyed);

        var label = Service(db).Add("s1", Msg("ayse"), 10m, null);

        label.CustomerId.Should().Be(canonical);
        using var c = db.Open();
        c.ExecuteScalar<int>("SELECT COUNT(*) FROM Customer").Should().Be(1);
    }

    [Fact]
    public async Task Add_ile_tasima_yarisinda_satis_kaybolmaz()
    {
        // Paylaşımlı bellek-DB eşzamanlı yazımda SQLITE_LOCKED verebildiği için geçici DOSYA (WAL — prod gibi).
        var path = Path.Combine(Path.GetTempPath(), $"od-race-{Guid.NewGuid():N}.db");
        var db = new SqliteConnectionFactory(path);
        try
        {
            new MigrationRunner(db).Run();
            new SessionRepository(db).Insert(new StreamSession("s1", null, 1000, null, new[] { "tiktok" }, null));
            var customers = new CustomerRepository(db);
            var sync = new CustomerSyncRepository(db);
            var service = Service(db);

            for (var i = 0; i < 15; i++)
            {
                var copy = Guid.NewGuid().ToString("N");
                var canonical = Guid.NewGuid().ToString("N");
                customers.Insert(new Customer(copy, "tiktok", $"ayse{i}", null, null, 1, 1, false, null, null, 0, 0m, null, null, null));
                customers.Insert(new Customer(canonical, "tiktok", $"Ayse{i}", null, null, 1, 1, false, null, null, 0, 0m, null, null, null));

                var username = $"ayse{i}";
                var add = Task.Run(() => service.Add("s1", Msg(username), 10m, null));
                var rekey = Task.Run(() => sync.RekeyToLocal(copy, canonical, pushedThroughSeq: long.MaxValue, nowUnix: 1_791_000_000));
                await Task.WhenAll(add, rekey);                       // FK hatası burada patlardı
                var label = await add;

                using var c = db.Open();
                c.ExecuteScalar<string>("SELECT CustomerId FROM Label WHERE Id = @id", new { id = label.Id })
                    .Should().Be(canonical, $"tur {i}: etiket hangi sırada yazılırsa yazılsın asıl kayıtta");
            }
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            foreach (var f in new[] { path, path + "-wal", path + "-shm" })
                if (File.Exists(f)) File.Delete(f);
        }
    }
}
