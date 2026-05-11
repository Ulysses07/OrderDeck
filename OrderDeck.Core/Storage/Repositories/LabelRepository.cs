using System.Collections.Generic;
using System.Linq;
using Dapper;
using OrderDeck.Core.Sales;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class LabelRepository
{
    private readonly IDbConnectionFactory _factory;
    public LabelRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(Label l)
    {
        using var conn = _factory.Open();
        // SQLite stores BOOLs as INTEGER — Dapper handles bool→0/1 conversion,
        // but we cast explicitly so the parameter type is unambiguous on
        // callers that pass an anonymous-typed projection.
        conn.Execute(
            @"INSERT INTO Label
              (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt, PrintedAt,
               IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee)
              VALUES
              (@Id, @SessionId, @CustomerId, @Platform, @Username, @DisplayName, @MessageText, @Code, @Price, @AddedAt, @PrintedAt,
               @IsBackupPromoted, @ParentLabelId, @IsTentativeBackup, @IsShippingFee)",
            new
            {
                l.Id, l.SessionId, l.CustomerId, l.Platform, l.Username, l.DisplayName, l.MessageText,
                l.Code, l.Price, l.AddedAt, l.PrintedAt,
                IsBackupPromoted = l.IsBackupPromoted ? 1 : 0,
                l.ParentLabelId,
                IsTentativeBackup = l.IsTentativeBackup ? 1 : 0,
                IsShippingFee = l.IsShippingFee ? 1 : 0
            });
    }

    public void Delete(string id)
    {
        using var conn = _factory.Open();
        conn.Execute("DELETE FROM Label WHERE Id=@id", new { id });
    }

    public Label? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee
              FROM Label WHERE Id=@id",
            new { id });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<Label> GetUnprintedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        // Cancelled labels are NOT eligible for re-print — exclude them from the
        // queue snapshot the same way they're excluded from revenue totals.
        // Tentative backup labels DO appear here on purpose: the operator
        // wants to print them physically alongside normal queue items so they
        // can stick the spare sticker on the goods.
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NULL AND CancelledAt IS NULL
              ORDER BY AddedAt",
            new { sessionId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>
    /// Tentative-backup labels for a given parent — used by the
    /// BackupTransferDialog after the parent is cancelled, and by the chip
    /// counter on the queue. Confirmed backups (IsTentativeBackup=0) are NOT
    /// returned because they're already real labels in their own right.
    /// </summary>
    public IReadOnlyList<Label> GetTentativeBackupsByParent(string parentLabelId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code,
                     Price, AddedAt, PrintedAt, CancelledAt, CancelReason, IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee
              FROM Label
              WHERE ParentLabelId=@parentLabelId AND IsTentativeBackup = 1 AND CancelledAt IS NULL
              ORDER BY AddedAt",
            new { parentLabelId }).ToList();
        return rows.Select(Map).ToList();
    }

    /// <summary>One round-trip count of tentative backups grouped by parent —
    /// drives the chip badge on the queue UI.</summary>
    public IReadOnlyDictionary<string, int> GetTentativeBackupCounts(IEnumerable<string> parentLabelIds)
    {
        var idArray = parentLabelIds.ToArray();
        if (idArray.Length == 0)
            return new Dictionary<string, int>();

        using var conn = _factory.Open();
        var rows = conn.Query<(string ParentLabelId, int Count)>(
            @"SELECT ParentLabelId, COUNT(*) AS Count
              FROM Label
              WHERE ParentLabelId IN @ids AND IsTentativeBackup = 1 AND CancelledAt IS NULL
              GROUP BY ParentLabelId",
            new { ids = idArray });
        return rows.ToDictionary(r => r.ParentLabelId, r => r.Count);
    }

    /// <summary>Flips IsTentativeBackup→0 for the given label ids. Used when
    /// the operator confirms a backup after the original buyer cancels.
    /// Returns the labels affected so callers can update customer aggregates.</summary>
    public void ConfirmTentativeBackups(IEnumerable<string> labelIds)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Label SET IsTentativeBackup = 0 WHERE Id IN @ids AND IsTentativeBackup = 1",
            new { ids = labelIds.ToArray() });
    }

    public void MarkCancelled(IEnumerable<string> ids, long cancelledAt, string reason)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Label SET CancelledAt=@cancelledAt, CancelReason=@reason WHERE Id IN @ids",
            new { cancelledAt, reason, ids = ids.ToArray() });
    }

    public void Uncancel(IEnumerable<string> ids)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Label SET CancelledAt=NULL, CancelReason=NULL WHERE Id IN @ids",
            new { ids = ids.ToArray() });
    }

    public void MarkPrinted(IEnumerable<string> ids, long printedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Label SET PrintedAt=@printedAt WHERE Id IN @ids",
            new { printedAt, ids = ids.ToArray() });
    }

    /// <summary>Updates the Price of a single label. Used by
    /// <c>LabelService.ConfirmBackup</c> when the operator negotiates a
    /// different number while promoting a tentative backup.</summary>
    public void UpdatePrice(string id, decimal price)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Label SET Price=@price WHERE Id=@id", new { id, price });
    }

    public SessionTotals GetSessionTotals(string sessionId)
    {
        using var conn = _factory.Open();
        // Cancelled labels are excluded from revenue + count; the dialog still
        // surfaces them visually in the customer detail view (with an iptal
        // badge) so the audit trail isn't lost.
        // Tentative backup labels are also excluded — they're physically
        // printed sticker stand-ins for "if the original cancels", not
        // realised sales. Only after the operator confirms a backup
        // (IsTentativeBackup→0) does it count toward revenue.
        var row = conn.QueryFirstOrDefault<TotalsRow>(
            @"SELECT
                COUNT(*)               AS PrintedCount,
                COALESCE(SUM(Price),0) AS TotalAmount,
                COUNT(DISTINCT CustomerId) AS UniqueCustomers
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NOT NULL
                AND CancelledAt IS NULL AND IsTentativeBackup = 0",
            new { sessionId });

        return new SessionTotals(
            row?.PrintedCount ?? 0,
            row?.TotalAmount ?? 0m,
            row?.UniqueCustomers ?? 0);
    }

    public IReadOnlyList<TopCustomer> GetTopCustomersBySession(string sessionId, int limit = 10)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<TopCustomerRow>(
            @"SELECT c.Username,
                     l.Platform,
                     COUNT(*)   AS LabelCount,
                     SUM(l.Price) AS TotalAmount
              FROM Label l
              JOIN Customer c ON c.Id = l.CustomerId
              WHERE l.SessionId=@sessionId AND l.PrintedAt IS NOT NULL
                AND l.CancelledAt IS NULL AND l.IsTentativeBackup = 0
              GROUP BY l.CustomerId
              ORDER BY SUM(l.Price) DESC
              LIMIT @limit",
            new { sessionId, limit }).ToList();
        return rows.Select(r => new TopCustomer(r.Username, r.Platform, r.LabelCount, r.TotalAmount)).ToList();
    }

    /// <summary>Returns the labels a customer added in a specific session, ordered
    /// oldest-first so the auctioneer sees them in the order they happened during
    /// the live. Cancelled rows are returned too — UI flags them visually.</summary>
    public IReadOnlyList<CustomerLabelRow> GetByCustomerAndSession(string customerId, string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, string SessionId, string MessageText, string? Code,
                                decimal Price, long AddedAt, long? PrintedAt,
                                long? CancelledAt, string? CancelReason)>(
            @"SELECT Id, SessionId, MessageText, Code, Price, AddedAt, PrintedAt,
                     CancelledAt, CancelReason
              FROM Label
              WHERE CustomerId=@customerId AND SessionId=@sessionId
              ORDER BY AddedAt",
            new { customerId, sessionId });

        return rows
            .Select(r => new CustomerLabelRow(
                r.Id, r.SessionId, r.MessageText, r.Code, r.Price, r.AddedAt,
                IsPrinted: r.PrintedAt is not null,
                CancelledAt: r.CancelledAt,
                CancelReason: r.CancelReason))
            .ToList();
    }

    /// <summary>
    /// Kargo PR C (2026-05-11): müşterinin verilen yayındaki ürün satış toplamı.
    /// Kargo ücreti label'larını (IsShippingFee=1), iptal edilenleri ve henüz
    /// onaylanmamış backup'ları (IsTentativeBackup=1) dışarda bırakır. Print
    /// durumuna bakmaz — operatör basmadan önce de toplam doğru olmalı çünkü
    /// dekont matcher yayın anında bunu kullanır.
    /// </summary>
    public decimal GetCustomerSessionLabelTotal(string customerId, string sessionId)
    {
        using var conn = _factory.Open();
        var total = conn.ExecuteScalar<decimal?>(
            @"SELECT COALESCE(SUM(Price), 0)
              FROM Label
              WHERE CustomerId=@customerId AND SessionId=@sessionId
                AND CancelledAt IS NULL
                AND IsTentativeBackup = 0
                AND IsShippingFee = 0",
            new { customerId, sessionId });
        return total ?? 0m;
    }

    /// <summary>Full lifetime label history (every session). Used by the customer
    /// detail dialog when there's no active session to scope to.</summary>
    public IReadOnlyList<CustomerLabelRow> GetByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, string SessionId, string MessageText, string? Code,
                                decimal Price, long AddedAt, long? PrintedAt,
                                long? CancelledAt, string? CancelReason)>(
            @"SELECT Id, SessionId, MessageText, Code, Price, AddedAt, PrintedAt,
                     CancelledAt, CancelReason
              FROM Label
              WHERE CustomerId=@customerId
              ORDER BY AddedAt DESC",
            new { customerId });

        return rows
            .Select(r => new CustomerLabelRow(
                r.Id, r.SessionId, r.MessageText, r.Code, r.Price, r.AddedAt,
                IsPrinted: r.PrintedAt is not null,
                CancelledAt: r.CancelledAt,
                CancelReason: r.CancelReason))
            .ToList();
    }

    private static Label Map(Row r) =>
        new(r.Id, r.SessionId, r.CustomerId, r.Platform, r.Username, r.MessageText,
            r.Code, r.Price, r.AddedAt, r.PrintedAt, r.CancelledAt, r.CancelReason,
            IsBackupPromoted: r.IsBackupPromoted != 0,
            ParentLabelId: r.ParentLabelId,
            IsTentativeBackup: r.IsTentativeBackup != 0,
            DisplayName: r.DisplayName,
            IsShippingFee: r.IsShippingFee != 0);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string MessageText { get; init; } = "";
        public string? Code { get; init; }
        public decimal Price { get; init; }
        public long AddedAt { get; init; }
        public long? PrintedAt { get; init; }
        public long? CancelledAt { get; init; }
        public string? CancelReason { get; init; }
        public int IsBackupPromoted { get; init; }
        public string? ParentLabelId { get; init; }
        public int IsTentativeBackup { get; init; }
        public int IsShippingFee { get; init; }
    }

    private sealed class TotalsRow
    {
        public int PrintedCount { get; init; }
        public decimal TotalAmount { get; init; }
        public int UniqueCustomers { get; init; }
    }

    private sealed class TopCustomerRow
    {
        public string Username { get; init; } = "";
        public string Platform { get; init; } = "";
        public int LabelCount { get; init; }
        public decimal TotalAmount { get; init; }
    }
}

public sealed record SessionTotals(int PrintedCount, decimal TotalAmount, int UniqueCustomers);
public sealed record TopCustomer(string Username, string Platform, int LabelCount, decimal TotalAmount);

/// <summary>UI projection of a Label for the customer detail dialog.</summary>
public sealed record CustomerLabelRow(
    string Id,
    string SessionId,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    bool IsPrinted,
    long? CancelledAt = null,
    string? CancelReason = null)
{
    public bool IsCancelled => CancelledAt.HasValue;
}
