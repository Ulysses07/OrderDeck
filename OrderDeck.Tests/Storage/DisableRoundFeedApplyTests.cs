using System;
using System.IO;
using Dapper;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// <c>disable</c> turunun son akışı (spec 2026-10-13 §8.5-1, §6.5-1; W2-11) — <see cref="SalesFeedApplyOptions.OwnRowsOnly"/>:
/// kendi oturum ve etiketlerine başka bilgisayarın komut sonuçları iner (yerelde olmayan kendi etiketi de, kendi
/// oturumuna), uzak oturum ve etiket ne eklenir ne tazelenir; dosyalar her zamanki gibi uygulanır. Mod On dönemini hiç
/// görmemiş kopyada (yerel kip 0) yerel tabloya bu yüzden uzak etiket hiç inmez.
/// </summary>
public sealed class DisableRoundFeedApplyTests : IDisposable
{
    private const long Now = 1_791_000_000L;
    private const long StartedAt = 1_790_980_000L;
    private const long EndedAt = 1_790_985_000L;
    private const long ReleasedAt = 1_790_990_000L;

    private static readonly SalesFeedApplyOptions Round =
        new(ApplyShipments: true, CountCatchUpOverwrite: false, OwnRowsOnly: true);

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"od-disable-feed-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _db;
    private readonly SalesFeedRepository _feed;

    public DisableRoundFeedApplyTests()
    {
        _db = new SqliteConnectionFactory(_path);
        new MigrationRunner(_db).Run();
        _feed = new SalesFeedRepository(_db);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private T Scalar<T>(string sql, object? param = null)
    {
        using var c = _db.Open();
        return c.ExecuteScalar<T>(sql, param)!;
    }

    private string Customer()
    {
        var id = NewId();
        new CustomerRepository(_db).Insert(new Customer(id, "tiktok", "alici_" + id[..8], "Takma Ad", null, 100, 200,
            false, null, null, 0, 0m, null, null, null));
        return id;
    }

    private string OwnSession()
    {
        var id = NewId();
        new SessionRepository(_db).Insert(new StreamSession(id, "Kendi yayını", StartedAt, EndedAt, ["tiktok"], null,
            SyncedAt: StartedAt + 10));
        return id;
    }

    private string OwnLabel(string sessionId, string customerId)
    {
        var id = NewId();
        new LabelRepository(_db).Insert(new Label(id, sessionId, customerId, "tiktok", "alici", "yorum", "A1", 40m,
            AddedAt: StartedAt + 60, PrintedAt: StartedAt + 120, DisplayName: "Takma Ad", SyncedAt: StartedAt + 200));
        return id;
    }

    private static SalesFeedSession Session(string id) =>
        new(id, "Yayın", StartedAt, EndedAt, ["tiktok"], null, "PC-B", ReleasedAt);

    private static SalesFeedOrder Order(string id, string sessionId, string customerId, long? cancelledAt = null) =>
        new(id, sessionId, customerId, "tiktok", "alici", "Takma Ad", "yorum", "A1", 40m, StartedAt + 60,
            StartedAt + 120, cancelledAt, cancelledAt is null ? null : "musteri-vazgecti", false, false, false, null,
            null, 5, null, null, null, null, null, null);

    private SalesFeedApplyResult Apply(SalesFeedItem item, SalesFeedSession? side = null, string? draftFor = null,
        SalesFeedApplyOptions? options = null)
        => _feed.ApplyItem(item, side is null ? [] : [side],
            draftFor is null ? [] : [new SalesFeedCustomerDraft(draftFor, "tiktok", "alici", "Takma Ad", null)],
            options ?? Round, Now);

    private static SalesFeedItem OrderItem(SalesFeedOrder o) => new(SalesFeedItemKind.Order, 2, null, o, null);

    [Fact]
    public void Uzak_oturum_ve_etiket_disable_turunda_inmez()
    {
        var customer = Customer();
        var remote = Session(NewId());

        Apply(new SalesFeedItem(SalesFeedItemKind.Session, 1, remote, null, null)).Status
            .Should().Be(SalesFeedApplyStatus.Ignored);
        Apply(OrderItem(Order(NewId(), remote.Id, customer)), side: remote, draftFor: customer).Status
            .Should().Be(SalesFeedApplyStatus.Ignored);

        Scalar<int>("SELECT COUNT(*) FROM StreamSession").Should().Be(0, "uzak oturum yan listeden de eklenmez");
        Scalar<int>("SELECT COUNT(*) FROM Label").Should().Be(0, "Off'ta yerel tabloda uzak etiket yok (§6.5-1)");
    }

    [Fact]
    public void Daha_once_inmis_uzak_etiket_tazelenmez()
    {
        var customer = Customer();
        var remote = Session(NewId());
        var id = NewId();
        _feed.ApplyItem(OrderItem(Order(id, remote.Id, customer)), [remote], [],
            new SalesFeedApplyOptions(ApplyShipments: true, CountCatchUpOverwrite: false), Now);

        Apply(OrderItem(Order(id, remote.Id, customer, cancelledAt: Now - 5)), side: remote).Status
            .Should().Be(SalesFeedApplyStatus.Ignored);

        new LabelRepository(_db).GetById(id)!.CancelledAt.Should().BeNull("2. adım siler; tazelemenin anlamı yok");
    }

    [Fact]
    public void Kendi_etiketine_baska_bilgisayarin_komutu_iner_kendi_oturumunun_devri_saklanir()
    {
        var customer = Customer();
        var own = OwnSession();
        var label = OwnLabel(own, customer);

        Apply(new SalesFeedItem(SalesFeedItemKind.Session, 1, Session(own), null, null)).Status
            .Should().Be(SalesFeedApplyStatus.Applied);
        var result = Apply(OrderItem(Order(label, own, customer, cancelledAt: Now - 5)), side: Session(own));

        result.Status.Should().Be(SalesFeedApplyStatus.Applied);
        var row = new LabelRepository(_db).GetById(label)!;
        row.CancelledAt.Should().Be(Now - 5, "mod On döneminde başka bilgisayarın iptali kendi etiketine iner");
        row.SyncedAt.Should().NotBeNull("inen değer kuyruğa girmez");
        var session = new SessionRepository(_db).GetById(own)!;
        session.ReleasedAt.Should().Be(ReleasedAt);
        session.Authority.Should().Be(StreamSessionAuthority.Local, "yerel kip 0: devir yalnız saklanır");
    }

    [Fact]
    public void Kendi_oturumunda_yerelde_olmayan_etiket_eklenir()
    {
        var customer = Customer();
        var own = OwnSession();
        var id = NewId();                                 // geri yüklenmiş veritabanı: yedekten sonra yazılmış etiket

        Apply(OrderItem(Order(id, own, customer)), side: Session(own)).Status.Should().Be(SalesFeedApplyStatus.Applied);

        new LabelRepository(_db).GetById(id)!.SessionId.Should().Be(own);
    }

    // ── yerel kip 0'da dosya: bu kopya yazıcıdır ──────────────────────────

    private string OwnReplica => new ReplicaIdentityRepository(_db).GetReplicaId()!.Value.ToString("N");

    private (string Id, string Customer) OwnSyncedShipment(int serverVersion = 4)
    {
        var customer = Customer();
        var id = NewId();
        new ShipmentRepository(_db).Insert(new Shipment(id, customer, ShipmentStatus.Pending, StartedAt, null, null, 60m,
            SyncedAt: StartedAt + 10));
        Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 3, null, null,
            ServerShipment(id, customer, OwnReplica, serverVersion))).Status.Should().Be(SalesFeedApplyStatus.Applied,
            "ön koşul: sürüm bilinir");
        return (id, customer);
    }

    private static SalesFeedShipment ServerShipment(string id, string customer, string origin, int version,
        ShipmentStatus status = ShipmentStatus.Pending, decimal amount = 60m) =>
        new(id, customer, status, amount, StartedAt, null, status == ShipmentStatus.Shipped ? Now - 9 : null, version,
            origin);

    private (string Status, double Amount, long? HeldAt, long? SyncedAt, long Revision) ShipmentRow(string id)
    {
        using var c = _db.Open();
        return c.QuerySingle<(string, double, long?, long?, long)>(
            "SELECT Status, CumulativeAmount, HeldAt, SyncedAt, Revision FROM Shipment WHERE Id = @id", new { id });
    }

    [Fact]
    public void Kip_0da_gonderilmemis_yerel_dosya_akisi_durdurur_ezilmez_onaysiz_kalir()
    {
        var (id, customer) = OwnSyncedShipment(serverVersion: 4);
        // Tur sürerken dekont: ikinci etiket dosyayı 140'a çıkardı, "Beklet" kararı (gönderilmedi).
        new ShipmentRepository(_db).Update(new Shipment(id, customer, ShipmentStatus.Held, StartedAt, Now - 30, null, 140m));

        var result = Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 4, null, null,
            ServerShipment(id, customer, OwnReplica, version: 4)));

        result.Status.Should().Be(SalesFeedApplyStatus.Stalled, "U5'in dosya karşılığı: önce gönderilmeli");
        var row = ShipmentRow(id);
        row.Status.Should().Be("Held", "gönderilmemiş karar ezilmez");
        row.Amount.Should().Be(140d);
        row.HeldAt.Should().Be(Now - 30);
        row.SyncedAt.Should().BeNull("hâlâ gönderim kuyruğunda");
    }

    [Fact]
    public void Kip_0da_baska_kopyanin_acik_dosyasi_eklenmez_yerelde_varsa_kalkar_kargolanmisi_eklenir()
    {
        var customer = Customer();
        var foreign = Guid.NewGuid().ToString("N");
        var newForeign = NewId();

        Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 3, null, null,
            ServerShipment(newForeign, customer, foreign, version: 2, status: ShipmentStatus.Held))).Status
            .Should().Be(SalesFeedApplyStatus.Ignored, "Off'ta başka kopyanın olacak açık dosyası");
        new ShipmentRepository(_db).GetById(newForeign).Should().BeNull("dekont onu bu arada seçemez");
        new ShipmentRepository(_db).GetOpenByCustomer(customer).Should().BeNull();

        // Yerelde duran dosya (bu kopyanın açtığı) disable'da başka kopyaya geçti (§4.10): hemen kalkar.
        var (moved, owner) = OwnSyncedShipment();
        var removed = Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 5, null, null,
            ServerShipment(moved, owner, foreign, version: 6)));
        removed.Status.Should().Be(SalesFeedApplyStatus.Applied);
        removed.ShipmentIds.Should().Equal(moved);
        new ShipmentRepository(_db).GetById(moved).Should().BeNull("2. adımın kuralı öğe anında");

        var shipped = NewId();
        Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 6, null, null,
            ServerShipment(shipped, customer, foreign, version: 3, status: ShipmentStatus.Shipped))).Status
            .Should().Be(SalesFeedApplyStatus.Applied, "kargoya verilmiş dosya her kipte kalır");
    }

    [Fact]
    public void Kip_1de_onaysiz_yerel_dosyayi_sunucu_yazar()
    {
        var (id, customer) = OwnSyncedShipment(serverVersion: 4);
        new ShipmentRepository(_db).Update(new Shipment(id, customer, ShipmentStatus.Held, StartedAt, Now - 30, null, 140m));
        using (var c = _db.Open()) c.Execute("UPDATE SalesSyncState SET Mode = 1 WHERE Id = 1");

        Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 4, null, null,
            ServerShipment(id, customer, Guid.NewGuid().ToString("N"), version: 5))).Status
            .Should().Be(SalesFeedApplyStatus.Applied, "§8.4-3: kip 1'de dosya sunucunun; durmak akışı sonsuza kilitlerdi");

        var row = ShipmentRow(id);
        row.Status.Should().Be("Pending");
        row.SyncedAt.Should().Be(Now);
    }

    [Fact]
    public void Dosya_ogesi_disable_turunda_uygulanir()
    {
        var customer = Customer();
        var file = new SalesFeedShipment(NewId(), customer, ShipmentStatus.Shipped, 90m, StartedAt, null, Now - 9, 4,
            Guid.NewGuid().ToString("N"));

        Apply(new SalesFeedItem(SalesFeedItemKind.Shipment, 3, null, null, file)).Status
            .Should().Be(SalesFeedApplyStatus.Applied);

        new ShipmentRepository(_db).GetById(file.Id)!.Status.Should().Be(ShipmentStatus.Shipped);
    }
}
