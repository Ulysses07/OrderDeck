using System.Collections.Generic;
using System.Linq;
using Dapper;
using OrderDeck.Core.Sales;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Shipment CRUD + state transitions. Bu repo sadece persistence yapar;
/// kümülatif kargo iş kuralları (yeni Shipment ne zaman açılır, Label hangi
/// Shipment'a attach edilir, threshold check, vb.) ShipmentService'te (PR-C)
/// yer alacak.
/// </summary>
public sealed class ShipmentRepository
{
    private readonly IDbConnectionFactory _factory;
    public ShipmentRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>U12: CustomerId yönlendirmeden yazımla aynı ifadede çözülür — taşınmış Id'yle
    /// açılan kargo silinmiş Id'de öksüz kalmaz (Shipment'ta FK yok; hata değil sessiz kayıp
    /// olurdu). Çağıranın elindeki nesnenin <c>CustomerId</c>'si bayat kalabilir; sonraki
    /// işlemler (<c>AttachLabels</c>, <c>ApplyDecision</c>) kargonun Id'siyle yürür.
    ///
    /// <para>Göç 049 (spec 2026-10-13 §3.2, §7.4): <c>OriginReplicaId</c> bu veritabanının kopya kimliğidir
    /// (<c>_meta.ReplicaId</c>, aynı ifadede okunur) — yerelde açılan dosyanın mod Off'taki tek yazıcısı bu
    /// kopyadır.</para></summary>
    public void Insert(Shipment s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Shipment
              (Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, OriginReplicaId)
              VALUES
              (@Id, " + CustomerIdSql.Resolve("@CustomerId") + @", @Status, @CreatedAt, @HeldAt, @ShippedAt,
               @CumulativeAmount, @SyncedAt, (SELECT ReplicaId FROM _meta WHERE Id = 1))",
            new
            {
                s.Id,
                s.CustomerId,
                Status = s.Status.ToString(),
                s.CreatedAt,
                s.HeldAt,
                s.ShippedAt,
                CumulativeAmount = (double)s.CumulativeAmount,
                s.SyncedAt
            });
    }

    /// <param name="write">Doluysa çağıranın paketinde (kargo kararı tek işlem —
    /// <c>ShipmentService.ApplyDecision</c>). Boşsa kendi bağlantısı — davranış değişmez.</param>
    public Shipment? GetById(string id, DbWrite? write = null)
    {
        var row = _factory.QueryFirstOrDefault<Row>(write,
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, Revision, ServerVersion
              FROM Shipment WHERE Id=@id",
            new { id });
        return row is null ? null : Map(row);
    }

    /// <summary>Kargo yazımlarını tek işleme bağlayan paket (kargo kararı: hedef dosya + kişinin
    /// öbür açık dosyaları birlikte ya da hiç).</summary>
    internal DbWrite BeginWrite() => DbWrite.Begin(_factory);

    /// <summary>
    /// Müşterinin açık Shipment'ı (Pending veya Held). Shipped/RecipientPays
    /// kapalı sayılır — yeni alım yeni Shipment açar.
    ///
    /// <para>U12: müşteri Id'si yönlendirmeden çözülür. Yerel taşıma kargoları birleştirmez
    /// (bilinçli kabul): kişi iki açık dosyayla kalabilir — en yenisi seçilir, eşitlikte Id
    /// (her bilgisayar aynı dosyayı seçer) ve yeni etiketler ona bağlanır. Eşik ve kargo kararı
    /// kişinin bütün açık dosyalarını tek havuz sayar (<see cref="GetAllOpenByCustomer"/>,
    /// <c>ShipmentService</c>); fazla dosya bir sonraki kararda kapanır. Müşteri penceresi
    /// bunu <see cref="CountOpenByCustomer"/> ile bildirir.</para>
    /// </summary>
    public Shipment? GetOpenByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, Revision, ServerVersion
              FROM Shipment
              WHERE CustomerId = " + CustomerIdSql.Resolve("@customerId") + @" AND Status IN ('Pending', 'Held')
              ORDER BY CreatedAt DESC, Id DESC
              LIMIT 1",
            new { customerId });
        return row is null ? null : Map(row);
    }

    /// <summary>Müşterinin BÜTÜN açık (Pending/Held) kargo dosyaları, en yenisi başta
    /// (<see cref="GetOpenByCustomer"/> ile aynı sıra); Id yönlendirmeden çözülür (U12).
    /// <c>ShipmentService</c> kararları bunları tek havuz sayar.</summary>
    /// <param name="write"><inheritdoc cref="GetById" path="/param[@name='write']"/></param>
    public IReadOnlyList<Shipment> GetAllOpenByCustomer(string customerId, DbWrite? write = null)
        => _factory.Query<Row>(write,
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, Revision, ServerVersion
              FROM Shipment
              WHERE CustomerId = " + CustomerIdSql.Resolve("@customerId") + @" AND Status IN ('Pending', 'Held')
              ORDER BY CreatedAt DESC, Id DESC",
            new { customerId }).Select(Map).ToList();

    /// <summary>Müşterinin açık (Pending/Held) kargo dosyası sayısı; Id yönlendirmeden çözülür
    /// (U12). Birden fazlası yerel taşımadan kalır (bkz. <see cref="GetOpenByCustomer"/>).</summary>
    public int CountOpenByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM Shipment WHERE CustomerId = " + CustomerIdSql.Resolve("@customerId")
            + " AND Status IN ('Pending', 'Held')",
            new { customerId });
    }

    /// <summary>
    /// Mobile Panel "Bekleyen Kargolar" / "Alıcı Ödemeli" tab'ları (PR-D).
    /// FIFO sıralama — en eski bekleyen üstte.
    /// </summary>
    public IReadOnlyList<Shipment> GetByStatus(ShipmentStatus status)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, Revision, ServerVersion
              FROM Shipment
              WHERE Status=@status
              ORDER BY CreatedAt",
            new { status = status.ToString() }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Status + timestamp + cumulative güncelleme. ShipmentService state
    /// transition'larda kullanır; full row update tek sorgu.
    /// </summary>
    /// <param name="write"><inheritdoc cref="GetById" path="/param[@name='write']"/></param>
    public void Update(Shipment s, DbWrite? write = null)
    {
        // Lokal state değişti → SyncedAt'i NULL'a düşür ki bir sonraki sync
        // tick'inde tekrar push edilsin (Payment outbox pattern ile aynı).
        _factory.Execute(write,
            @"UPDATE Shipment SET
                Status=@Status, HeldAt=@HeldAt, ShippedAt=@ShippedAt,
                CumulativeAmount=@CumulativeAmount,
                SyncedAt=NULL, Revision = Revision + 1
              WHERE Id=@Id",
            new
            {
                s.Id,
                Status = s.Status.ToString(),
                s.HeldAt,
                s.ShippedAt,
                CumulativeAmount = (double)s.CumulativeAmount
            });
    }

    /// <summary>
    /// PR-D outbox query: henüz LicenseServer'a push edilmemiş Shipment'lar.
    /// Sync service tick'inde batch alıp push'lar, dönerse MarkSynced.
    /// </summary>
    public IReadOnlyList<Shipment> GetUnsynced(int limit = 50)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount, SyncedAt, Revision, ServerVersion
              FROM Shipment
              WHERE SyncedAt IS NULL
              ORDER BY CreatedAt
              LIMIT @limit",
            new { limit }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>Push başarılı sonrası SyncedAt'i set eder. UpdatedAt local'de
    /// yok — sync timestamp'i ayrıca tutulur.
    /// F05: compare-and-set — push uçuştayken Revision değiştiyse 0 satır
    /// etkiler, satır bekleyen kalır (bkz. <see cref="LabelRepository.MarkSynced"/>).</summary>
    public void MarkSynced(string id, long syncedAt, long revision)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Shipment SET SyncedAt=@syncedAt WHERE Id=@id AND Revision=@revision",
            new { id, syncedAt, revision });
    }

    /// <summary>
    /// v2 <c>shipments/sync</c> yanıtının dosya satırı, mod Off (spec 2026-10-13 §7.1, M19): sunucunun güncel hâli yerel
    /// aynaya yazılır ve gönderim onaylanır. Kargoya verilmiş dosya sunucuda tek yönlü olduğu için bayat yerel karar
    /// (ör. başka bilgisayarın kargoladığı dosya burada Held) sunucunun Shipped'ine döner; sonraki dekont yeni dosya açar.
    ///
    /// <para>Tek <see cref="SyncApplyScope"/> işleminde: TR_Shipment_Enqueue kuyruğa almaz (Revision değişmez, yankı
    /// yok). F05: satır gönderilen <paramref name="revision"/>'da değilse (uçuşta yerelde değişti) hiçbir şey yazılmaz
    /// — güncel hâli sonraki turda gider, sunucu yeniden yanıtlar. Karar alanları, tutar, kaynak kopya ve sürüm yereldekiyle
    /// aynıysa yalnız onay yazılır (bugünkü <see cref="MarkSynced"/>). Sürüm geri gitmez. CustomerId'ye dokunulmaz.</para>
    /// </summary>
    /// <returns>Yazımın sonucu.</returns>
    public ShipmentPushAck ApplyPushResponse(string id, long revision, SalesFeedShipment server, long syncedAt)
    {
        using var scope = SyncApplyScope.Begin(_factory);
        var conn = scope.Connection;
        var tx = scope.Transaction;
        var local = conn.QuerySingleOrDefault<MirrorRow>(
            @"SELECT Status, HeldAt, ShippedAt, CumulativeAmount, OriginReplicaId, ServerVersion, Revision
              FROM Shipment WHERE Id = @id",
            new { id }, tx);
        if (local is null || local.Revision != revision)
            return ShipmentPushAck.ChangedInFlight;           // işlem yazmadan geri alınır

        var status = server.Status.ToString();
        var same = string.Equals(local.Status, status, System.StringComparison.Ordinal)
                   && local.HeldAt == server.HeldAt
                   && local.ShippedAt == server.ShippedAt
                   && (decimal)local.CumulativeAmount == server.CumulativeAmount
                   && SameReplica(local.OriginReplicaId, server.OriginReplicaId)
                   && local.ServerVersion == server.Version;
        if (same || (local.ServerVersion is { } known && server.Version < known))
        {
            conn.Execute("UPDATE Shipment SET SyncedAt = @syncedAt WHERE Id = @id AND Revision = @revision",
                new { id, syncedAt, revision }, tx);
            scope.Commit();
            return ShipmentPushAck.Acked;
        }

        conn.Execute(MirrorPushResponseSql, new
        {
            id,
            revision,
            syncedAt,
            Status = status,
            server.HeldAt,
            server.ShippedAt,
            CumulativeAmount = (double)server.CumulativeAmount,
            server.OriginReplicaId,
            server.Version,
        }, tx);
        scope.Commit();
        return ShipmentPushAck.Mirrored;
    }

    /// <summary>Sunucunun dosya satırı yerel aynaya, onayla birlikte (F05: gönderilen Revision). Kilit altında:
    /// TR_Shipment_Enqueue kuyruğa almaz.</summary>
    private const string MirrorPushResponseSql = @"
        UPDATE Shipment SET Status = @Status, HeldAt = @HeldAt, ShippedAt = @ShippedAt,
               CumulativeAmount = @CumulativeAmount, OriginReplicaId = @OriginReplicaId, ServerVersion = @Version,
               SyncedAt = @syncedAt
         WHERE Id = @id AND Revision = @revision";

    private static bool SameReplica(string? a, string? b) =>
        a is null || b is null
            ? a is null && b is null
            : string.Equals(a, b, System.StringComparison.OrdinalIgnoreCase)
              || (System.Guid.TryParse(a, out var ga) && System.Guid.TryParse(b, out var gb) && ga == gb);

    /// <summary>
    /// Label'ı bir Shipment'a bağla. ShipmentService attach sırasında
    /// kullanır; CumulativeAmount güncellemesi caller sorumluluğunda
    /// (genelde aynı transaction içinde Update çağrılır).
    /// </summary>
    public void AttachLabel(string shipmentId, string labelId)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Label SET ShipmentId=@shipmentId WHERE Id=@labelId",
            new { shipmentId, labelId });
    }

    /// <summary>Shipment'a bağlı Label.Id listesi. PR-C/D detail view'ları için.</summary>
    public IReadOnlyList<string> GetLabelIds(string shipmentId)
    {
        using var conn = _factory.Open();
        return conn.Query<string>(
            "SELECT Id FROM Label WHERE ShipmentId=@shipmentId ORDER BY AddedAt",
            new { shipmentId }).ToList();
    }

    private static Shipment Map(Row r) =>
        new(r.Id, r.CustomerId,
            System.Enum.Parse<ShipmentStatus>(r.Status),
            r.CreatedAt, r.HeldAt, r.ShippedAt,
            (decimal)r.CumulativeAmount,
            r.SyncedAt,
            r.Revision,
            r.ServerVersion);

    private sealed class MirrorRow
    {
        public string Status { get; init; } = "";
        public long? HeldAt { get; init; }
        public long? ShippedAt { get; init; }
        public double CumulativeAmount { get; init; }
        public string? OriginReplicaId { get; init; }
        public long? ServerVersion { get; init; }
        public long Revision { get; init; }
    }

    private sealed class Row
    {
        public long? ServerVersion { get; init; }
        public string Id { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Status { get; init; } = "Pending";
        public long CreatedAt { get; init; }
        public long? HeldAt { get; init; }
        public long? ShippedAt { get; init; }
        public double CumulativeAmount { get; init; }
        public long? SyncedAt { get; init; }
        public long Revision { get; init; }
    }
}

/// <summary><see cref="ShipmentRepository.ApplyPushResponse"/>'un sonucu.</summary>
public enum ShipmentPushAck
{
    /// <summary>Sunucunun satırı yereldekiyle aynıydı: yalnız onay yazıldı.</summary>
    Acked,

    /// <summary>Sunucunun satırı yerel aynaya yazıldı ve onaylandı.</summary>
    Mirrored,

    /// <summary>Satır uçuşta yerelde değişti (ya da silindi): hiçbir şey yazılmadı, sonraki turda yeniden gider.</summary>
    ChangedInFlight,
}
