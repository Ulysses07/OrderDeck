using System.Collections.Generic;
using System.Globalization;
using Dapper;
using OrderDeck.Core.Payments;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class PaymentRepository
{
    private readonly IDbConnectionFactory _factory;

    public PaymentRepository(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Insert(Payment p)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Payment
              (Id, PayerName, Amount, PaidAt, ReferansNo, PdfHash, Status,
               CreatedAt, UpdatedAt, SyncedAt, ApprovedAt, RejectedAt, RejectReason,
               ShipmentDirective)
              VALUES
              (@Id, @PayerName, @Amount, @PaidAt, @ReferansNo, @PdfHash, @Status,
               @CreatedAt, @UpdatedAt, @SyncedAt, @ApprovedAt, @RejectedAt, @RejectReason,
               @ShipmentDirective)",
            new
            {
                p.Id,
                p.PayerName,
                Amount = FormatAmount(p.Amount),
                p.PaidAt,
                p.ReferansNo,
                p.PdfHash,
                Status = p.Status.ToString(),
                p.CreatedAt,
                p.UpdatedAt,
                p.SyncedAt,
                p.ApprovedAt,
                p.RejectedAt,
                p.RejectReason,
                ShipmentDirective = p.ShipmentDirective.ToString()
            });
    }

    public Payment? FindById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Payment WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public Payment? FindByReferansNo(string referansNo)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Payment WHERE ReferansNo=@referansNo", new { referansNo });
        return row is null ? null : Map(row);
    }

    /// <summary>Outbox queue: SyncedAt NULL satırlar, en eski önce. Limit ile batch.</summary>
    public IReadOnlyList<Payment> GetUnsynced(int limit = 50)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Payment WHERE SyncedAt IS NULL
              ORDER BY CreatedAt ASC LIMIT @limit",
            new { limit });
        return rows.Select(Map).ToList();
    }

    public IReadOnlyList<Payment> ListByStatus(PaymentStatus status, int limit = 100)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Payment WHERE Status=@status
              ORDER BY CreatedAt DESC LIMIT @limit",
            new { status = status.ToString(), limit });
        return rows.Select(Map).ToList();
    }

    public void MarkSynced(string id, long syncedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Payment SET SyncedAt=@syncedAt, UpdatedAt=@syncedAt WHERE Id=@id",
            new { id, syncedAt });
    }

    /// <summary>Reverse sync (sonraki PR): server'dan gelen status değişimini lokal'e uygula.</summary>
    public void ApplyServerStatus(string id, PaymentStatus status, long? approvedAt,
        long? rejectedAt, string? rejectReason, long updatedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Payment SET
                Status=@status, ApprovedAt=@approvedAt, RejectedAt=@rejectedAt,
                RejectReason=@rejectReason, UpdatedAt=@updatedAt
              WHERE Id=@id",
            new
            {
                id,
                status = status.ToString(),
                approvedAt,
                rejectedAt,
                rejectReason,
                updatedAt
            });
    }

    public int CountUnsynced()
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>("SELECT COUNT(*) FROM Payment WHERE SyncedAt IS NULL");
    }

    private static string FormatAmount(decimal amount) =>
        amount.ToString("0.00", CultureInfo.InvariantCulture);

    private static decimal ParseAmount(string raw) =>
        decimal.Parse(raw, NumberStyles.Number, CultureInfo.InvariantCulture);

    private static Payment Map(Row r) => new(
        r.Id,
        r.PayerName,
        ParseAmount(r.Amount),
        r.PaidAt,
        r.ReferansNo,
        r.PdfHash,
        System.Enum.Parse<PaymentStatus>(r.Status),
        r.CreatedAt,
        r.UpdatedAt,
        r.SyncedAt,
        r.ApprovedAt,
        r.RejectedAt,
        r.RejectReason,
        System.Enum.Parse<ShipmentDirective>(r.ShipmentDirective));

    private sealed class Row
    {
        public string Id { get; set; } = "";
        public string PayerName { get; set; } = "";
        public string Amount { get; set; } = "0";
        public long PaidAt { get; set; }
        public string ReferansNo { get; set; } = "";
        public string? PdfHash { get; set; }
        public string Status { get; set; } = "Pending";
        public long CreatedAt { get; set; }
        public long UpdatedAt { get; set; }
        public long? SyncedAt { get; set; }
        public long? ApprovedAt { get; set; }
        public long? RejectedAt { get; set; }
        public string? RejectReason { get; set; }
        public string ShipmentDirective { get; set; } = "Normal";
    }
}
