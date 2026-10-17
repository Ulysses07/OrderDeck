using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
/// <c>disable</c> turunun 2. adımı (spec 2026-10-13 §8.5-2, §6.5-1, M15, M19; W2-11) —
/// <see cref="SalesFeedRepository.ApplyModeOff"/>: tek işlemde yerel kip 0 ve görülen an, kendi yayınları yerel
/// otoriteye, uzak satırlar ve canlı yayın başlıkları silinir, toplamlar yalnız yerel etiketlerden, Off'ta başka kopyanın
/// olacak kargoya verilmemiş dosyalar silinir (kendi, kaynaksız ve kargoya verilmiş dosyalar kalır). Ödeme akışındaki
/// müşteri hiçbir şey yazdırmaz. İdempotent. Gerçek kurguda (dosya, WAL, havuz).
/// </summary>
public sealed class DisableRoundLocalStepTests : IDisposable
{
    private const long Now = 1_791_000_000L;
    private const long StartedAt = 1_790_980_000L;
    private const long EndedAt = 1_790_985_000L;
    private const long ReleasedAt = 1_790_990_000L;
    private const long ModeChangedAt = 1_790_995_000L;

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"od-disable-{Guid.NewGuid():N}.db");
    private readonly SqliteConnectionFactory _db;
    private readonly Guid _replica;
    private readonly Guid _otherReplica = Guid.NewGuid();

    public DisableRoundLocalStepTests()
    {
        _db = new SqliteConnectionFactory(_path);
        new MigrationRunner(_db).Run();
        _replica = new ReplicaIdentityRepository(_db).GetReplicaId()!.Value;
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var f in new[] { _path, _path + "-wal", _path + "-shm" })
            if (File.Exists(f)) File.Delete(f);
    }

    private static string NewId() => Guid.NewGuid().ToString("N");

    private void Exec(string sql, object? param = null)
    {
        using var c = _db.Open();
        c.Execute(sql, param);
    }

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

    /// <summary>Bu bilgisayarın yayını (onaylı).</summary>
    private string OwnSession(long? endedAt)
    {
        var id = NewId();
        new SessionRepository(_db).Insert(new StreamSession(id, "Kendi yayını", StartedAt, endedAt, ["tiktok"], null,
            SyncedAt: StartedAt + 10));
        return id;
    }

    /// <summary>Mod On'da devri görülmüş kendi yayını (etiketleri eklendikten sonra: sunucu otoritesinde yerel ekleme
    /// düşer).</summary>
    private void ToServer(string sessionId) =>
        Exec("UPDATE StreamSession SET ReleasedAt = @r, Authority = 'server' WHERE Id = @sessionId",
            new { r = ReleasedAt, sessionId });

    private string OwnLabel(string sessionId, string customerId, decimal price)
    {
        var id = NewId();
        new LabelRepository(_db).Insert(new Label(id, sessionId, customerId, "tiktok", "alici", "yorum", "A1", price,
            AddedAt: StartedAt + 60, PrintedAt: StartedAt + 120, DisplayName: "Takma Ad", SyncedAt: StartedAt + 200));
        return id;
    }

    /// <summary>Başka bilgisayarın akıştan inmiş yayını ve basılmış etiketi (mod On döneminin aynası).</summary>
    private string RemoteSale(string customerId, decimal price, SalesFeedRepository? feed = null)
    {
        var session = new SalesFeedSession(NewId(), "Uzak yayın", StartedAt, EndedAt, ["tiktok"], null, "PC-B", ReleasedAt);
        var order = new SalesFeedOrder(NewId(), session.Id, customerId, "tiktok", "alici", "Takma Ad", "aldım", "B1",
            price, StartedAt + 60, StartedAt + 120, null, null, false, false, false, null, null, 2, null, null, null,
            null, null, null);
        (feed ?? new SalesFeedRepository(_db)).ApplyItem(
            new SalesFeedItem(SalesFeedItemKind.Order, 1, null, order, null), [session], [],
            new SalesFeedApplyOptions(ApplyShipments: true, CountCatchUpOverwrite: false), Now);
        return order.Id;
    }

    /// <summary>Kargo aynasının satırı (mod On'da akış gibi kilit altında yazılır: dosya tetikleyicisi ABORT etmez).</summary>
    private string Shipment(string customerId, ShipmentStatus status, string? origin)
    {
        var id = NewId();
        using var scope = SyncApplyScope.Begin(_db);
        scope.Execute(
            @"INSERT INTO Shipment (Id, CustomerId, Status, CreatedAt, CumulativeAmount, SyncedAt, OriginReplicaId)
              VALUES (@id, @customerId, @status, @at, 40, @at, @origin)",
            new { id, customerId, status = status.ToString(), at = StartedAt, origin });
        scope.Commit();
        return id;
    }

    private void ModeOn() => Exec("UPDATE SalesSyncState SET Mode = 1, ModeSeenAt = 1, FirstCatchUpAt = 2 WHERE Id = 1");

    [Fact]
    public void Ikinci_adim_kipi_kapatir_kendi_yayinlarini_yerele_dondurur_uzak_satirlari_ve_baskasinin_acik_dosyalarini_siler()
    {
        var customer = Customer();
        var released = OwnSession(EndedAt);
        var live = OwnSession(endedAt: null);
        var ownLabel = OwnLabel(released, customer, 40m);
        var liveLabel = OwnLabel(live, customer, 25m);
        ToServer(released);
        ModeOn();
        var remoteLabel = RemoteSale(customer, 150m);
        Exec("INSERT INTO RemoteLiveSession (Id, Title, StartedAt, OwnerMachineName, State) VALUES (@id, 'Canlı', 1, 'PC-B', 'live')",
            new { id = NewId() });
        Exec("INSERT INTO SalesFeedFailure (Kind, ItemId, ChangeSeq, Attempts, FirstFailedAt) VALUES ('order', 'x', 9, 2, 1)");
        var own = Shipment(customer, ShipmentStatus.Held, _replica.ToString("N"));
        var ownDashed = Shipment(customer, ShipmentStatus.Pending, _replica.ToString("D").ToUpperInvariant());
        var legacy = Shipment(customer, ShipmentStatus.Pending, origin: null);
        var foreign = Shipment(customer, ShipmentStatus.Held, _otherReplica.ToString("N"));
        var foreignShipped = Shipment(customer, ShipmentStatus.Shipped, _otherReplica.ToString("N"));
        new CustomerRepository(_db).GetById(customer)!.TotalAmount.Should().Be(215m, "ön koşul: uzak etiket toplamda");
        var before = new LabelRepository(_db).GetById(ownLabel)!;
        var revisionBefore = Scalar<long>("SELECT Revision FROM Label WHERE Id = @ownLabel", new { ownLabel });

        var result = new SalesFeedRepository(_db).ApplyModeOff(ModeChangedAt, _replica);

        result.Should().BeEquivalentTo(new
        {
            Busy = false, SessionsToLocal = 1, Sessions = 1, Labels = 1, LiveSessions = 1, Shipments = 1, FeedFailures = 1,
        });
        var state = new SalesSyncStateRepository(_db).Get();
        state.Mode.Should().Be(0);
        state.ModeSeenAt.Should().Be(ModeChangedAt, "görülen an: sales/state'in modeChangedAt'i");
        new SalesSyncStateRepository(_db).GetSeenMode().Should().Be(0, "bekleyen komut yok: tur bitti");
        var sessions = new SessionRepository(_db);
        sessions.GetById(released)!.Authority.Should().Be(StreamSessionAuthority.Local, "Off'ta kendi yayınını push'la yazar");
        sessions.GetById(live)!.Authority.Should().Be(StreamSessionAuthority.Local);
        sessions.GetActive()!.Id.Should().Be(live, "canlı yayın kesilmez");
        Scalar<int>("SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote'").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM Label WHERE Id = @remoteLabel", new { remoteLabel }).Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM RemoteLiveSession").Should().Be(0);
        Scalar<int>("SELECT COUNT(*) FROM SalesFeedFailure").Should().Be(0);
        new CustomerRepository(_db).GetById(customer)!.TotalAmount.Should().Be(65m, "toplam yalnız kendi etiketlerinden");
        using (var write = DbWrite.Begin(_db))
            CustomerAggregateSql.RecomputeAll(write).ChangedRows.Should().Be(0, "toplamlar yalnız yerel türetmeyle aynı");
        Scalar<string>("SELECT group_concat(Id) FROM (SELECT Id FROM Shipment ORDER BY Id)").Split(',')
            .Should().BeEquivalentTo(new[] { own, ownDashed, legacy, foreignShipped },
                "kendi, kaynaksız (eski ikili) ve kargoya verilmiş dosyalar kalır; Off'ta başka kopyanın olacak açık dosya gider");
        var after = new LabelRepository(_db).GetById(ownLabel)!;
        after.SyncedAt.Should().Be(before.SyncedAt, "kendi etiketi kuyruğa girmez");
        Scalar<long>("SELECT Revision FROM Label WHERE Id = @ownLabel", new { ownLabel }).Should().Be(revisionBefore);
        sessions.GetById(released)!.SyncedAt.Should().NotBeNull("otorite dönüşü oturumu kuyruğa almaz");
        new LabelRepository(_db).GetById(liveLabel).Should().NotBeNull();

        var again = new SalesFeedRepository(_db).ApplyModeOff(modeSeenAt: null, _replica);

        again.Should().BeEquivalentTo(new
        {
            Busy = false, SessionsToLocal = 0, Sessions = 0, Labels = 0, LiveSessions = 0, Shipments = 0, FeedFailures = 0,
        }, "idempotent");
        new SalesSyncStateRepository(_db).Get().ModeSeenAt.Should().Be(ModeChangedAt, "boş görülen an eskisini silmez");
    }

    [Fact]
    public void Kip_kapandiktan_sonra_kalan_dosya_ve_kendi_yayini_yerel_yoldan_yazilir()
    {
        var customer = Customer();
        var released = OwnSession(EndedAt);
        var label = OwnLabel(released, customer, 40m);
        ToServer(released);
        ModeOn();
        var own = Shipment(customer, ShipmentStatus.Held, _replica.ToString("N"));

        new SalesFeedRepository(_db).ApplyModeOff(ModeChangedAt, _replica);

        var shipments = new ShipmentRepository(_db);
        var file = shipments.GetById(own)!;
        shipments.Update(file with { Status = ShipmentStatus.Shipped, ShippedAt = Now });
        shipments.GetById(own)!.Status.Should().Be(ShipmentStatus.Shipped, "mod 0'da dosya tetikleyicisi ABORT etmez");
        shipments.GetById(own)!.SyncedAt.Should().BeNull("Off'ta dosyanın tek yazıcısı bu kopya: shipments/sync ile gider");
        new LabelRepository(_db).MarkCancelled([label], Now, "musteri-vazgecti");
        new LabelRepository(_db).GetById(label)!.SyncedAt.Should().BeNull("iptal kuyruğa girer; yayını artık yerel");
    }

    [Fact]
    public async Task Uzak_etiketin_musterisi_odeme_akisindaysa_hicbir_sey_yazilmaz_yerel_kip_1_kalir()
    {
        var busy = new CustomerBusySet();
        var feed = new SalesFeedRepository(_db, busy);
        var customer = Customer();
        var released = OwnSession(EndedAt);
        ToServer(released);
        ModeOn();
        RemoteSale(customer, 150m, feed);
        var foreign = Shipment(customer, ShipmentStatus.Held, _otherReplica.ToString("N"));
        using var lease = await busy.EnterAsync(customer);

        feed.ApplyModeOff(ModeChangedAt, _replica).Busy.Should().BeTrue();

        new SalesSyncStateRepository(_db).GetMode().Should().Be(1, "U13: ödeme isteği okurken altından etiket çekilmez");
        new SessionRepository(_db).GetById(released)!.Authority.Should().Be(StreamSessionAuthority.Server);
        Scalar<int>("SELECT COUNT(*) FROM StreamSession WHERE Origin = 'remote'").Should().Be(1);
        new ShipmentRepository(_db).GetById(foreign).Should().NotBeNull();
        lease.Dispose();

        feed.ApplyModeOff(ModeChangedAt, _replica).Labels.Should().Be(1);
        new SalesSyncStateRepository(_db).GetMode().Should().Be(0);
    }
}
