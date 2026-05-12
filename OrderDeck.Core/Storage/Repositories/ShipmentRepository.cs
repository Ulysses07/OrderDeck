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

    public void Insert(Shipment s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Shipment
              (Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount)
              VALUES
              (@Id, @CustomerId, @Status, @CreatedAt, @HeldAt, @ShippedAt, @CumulativeAmount)",
            new
            {
                s.Id,
                s.CustomerId,
                Status = s.Status.ToString(),
                s.CreatedAt,
                s.HeldAt,
                s.ShippedAt,
                CumulativeAmount = (double)s.CumulativeAmount
            });
    }

    public Shipment? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount
              FROM Shipment WHERE Id=@id",
            new { id });
        return row is null ? null : Map(row);
    }

    /// <summary>
    /// Müşterinin açık Shipment'ı (Pending veya Held). Shipped/RecipientPays
    /// kapalı sayılır — yeni alım yeni Shipment açar.
    /// </summary>
    public Shipment? GetOpenByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount
              FROM Shipment
              WHERE CustomerId=@customerId AND Status IN ('Pending', 'Held')
              ORDER BY CreatedAt DESC
              LIMIT 1",
            new { customerId });
        return row is null ? null : Map(row);
    }

    /// <summary>
    /// Mobile Panel "Bekleyen Kargolar" / "Alıcı Ödemeli" tab'ları (PR-D).
    /// FIFO sıralama — en eski bekleyen üstte.
    /// </summary>
    public IReadOnlyList<Shipment> GetByStatus(ShipmentStatus status)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, CustomerId, Status, CreatedAt, HeldAt, ShippedAt, CumulativeAmount
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
    public void Update(Shipment s)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Shipment SET
                Status=@Status, HeldAt=@HeldAt, ShippedAt=@ShippedAt,
                CumulativeAmount=@CumulativeAmount
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
            (decimal)r.CumulativeAmount);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Status { get; init; } = "Pending";
        public long CreatedAt { get; init; }
        public long? HeldAt { get; init; }
        public long? ShippedAt { get; init; }
        public double CumulativeAmount { get; init; }
    }
}
