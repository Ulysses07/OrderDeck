using System.Collections.Generic;
using System.Linq;
using Dapper;
using LiveDeck.Core.Sales;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class OrderRepository
{
    private readonly IDbConnectionFactory _factory;
    public OrderRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(OrderItem o)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO OrderItem
              (Id, SessionId, ActiveCodeId, CustomerId, Code, Size, Quantity,
               UnitPrice, TotalPrice, Confidence, Status, OriginalMessageText,
               CapturedAt, StatusUpdatedAt, LabelPrintedAt, Notes)
              VALUES
              (@Id, @SessionId, @ActiveCodeId, @CustomerId, @Code, @Size, @Quantity,
               @UnitPrice, @TotalPrice, @Confidence, @Status, @OriginalMessageText,
               @CapturedAt, @StatusUpdatedAt, @LabelPrintedAt, @Notes)",
            o);
    }

    public void UpdateStatus(string id, string status, long statusUpdatedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE OrderItem SET Status=@status, StatusUpdatedAt=@updatedAt WHERE Id=@id",
            new { id, status, updatedAt = statusUpdatedAt });
    }

    public IReadOnlyList<OrderItem> GetBySession(string sessionId)
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
            @"SELECT Id, SessionId, ActiveCodeId, CustomerId, Code, Size, Quantity,
                     UnitPrice, TotalPrice, Confidence, Status, OriginalMessageText,
                     CapturedAt, StatusUpdatedAt, LabelPrintedAt, Notes
              FROM OrderItem
              WHERE SessionId=@sessionId
              ORDER BY CapturedAt DESC",
            new { sessionId }).Select(Map).ToList();
    }

    public IReadOnlyList<OrderItem> GetBySessionAndStatus(string sessionId, string status)
    {
        using var conn = _factory.Open();
        return conn.Query<Row>(
            @"SELECT Id, SessionId, ActiveCodeId, CustomerId, Code, Size, Quantity,
                     UnitPrice, TotalPrice, Confidence, Status, OriginalMessageText,
                     CapturedAt, StatusUpdatedAt, LabelPrintedAt, Notes
              FROM OrderItem
              WHERE SessionId=@sessionId AND Status=@status
              ORDER BY CapturedAt DESC",
            new { sessionId, status }).Select(Map).ToList();
    }

    private static OrderItem Map(Row r) => new(
        r.Id, r.SessionId, r.ActiveCodeId, r.CustomerId, r.Code, r.Size, r.Quantity,
        r.UnitPrice, r.TotalPrice, r.Confidence, r.Status, r.OriginalMessageText,
        r.CapturedAt, r.StatusUpdatedAt, r.LabelPrintedAt, r.Notes);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string ActiveCodeId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Code { get; init; } = "";
        public string Size { get; init; } = "";
        public int Quantity { get; init; }
        public decimal UnitPrice { get; init; }
        public decimal TotalPrice { get; init; }
        public int Confidence { get; init; }
        public string Status { get; init; } = "";
        public string OriginalMessageText { get; init; } = "";
        public long CapturedAt { get; init; }
        public long StatusUpdatedAt { get; init; }
        public long? LabelPrintedAt { get; init; }
        public string? Notes { get; init; }
    }
}
