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
        conn.Execute(
            @"INSERT INTO Label
              (Id, SessionId, CustomerId, Platform, Username, MessageText, Code, Price, AddedAt, PrintedAt)
              VALUES
              (@Id, @SessionId, @CustomerId, @Platform, @Username, @MessageText, @Code, @Price, @AddedAt, @PrintedAt)",
            l);
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
            @"SELECT Id, SessionId, CustomerId, Platform, Username, MessageText, Code,
                     Price, AddedAt, PrintedAt
              FROM Label WHERE Id=@id",
            new { id });
        return row is null ? null : Map(row);
    }

    public IReadOnlyList<Label> GetUnprintedBySession(string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, CustomerId, Platform, Username, MessageText, Code,
                     Price, AddedAt, PrintedAt
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NULL
              ORDER BY AddedAt",
            new { sessionId }).ToList();
        return rows.Select(Map).ToList();
    }

    public void MarkPrinted(IEnumerable<string> ids, long printedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Label SET PrintedAt=@printedAt WHERE Id IN @ids",
            new { printedAt, ids = ids.ToArray() });
    }

    public SessionTotals GetSessionTotals(string sessionId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<TotalsRow>(
            @"SELECT
                COUNT(*)               AS PrintedCount,
                COALESCE(SUM(Price),0) AS TotalAmount,
                COUNT(DISTINCT CustomerId) AS UniqueCustomers
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NOT NULL",
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
              GROUP BY l.CustomerId
              ORDER BY SUM(l.Price) DESC
              LIMIT @limit",
            new { sessionId, limit }).ToList();
        return rows.Select(r => new TopCustomer(r.Username, r.Platform, r.LabelCount, r.TotalAmount)).ToList();
    }

    /// <summary>Returns labels for a customer, most recent first. Empty list if none.</summary>
    public IReadOnlyList<CustomerLabelRow> GetByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, string SessionId, string MessageText, string? Code,
                                decimal Price, long AddedAt, long? PrintedAt)>(
            @"SELECT Id, SessionId, MessageText, Code, Price, AddedAt, PrintedAt
              FROM Label
              WHERE CustomerId=@customerId
              ORDER BY AddedAt DESC",
            new { customerId });

        return rows
            .Select(r => new CustomerLabelRow(
                r.Id, r.SessionId, r.MessageText, r.Code, r.Price, r.AddedAt,
                IsPrinted: r.PrintedAt is not null))
            .ToList();
    }

    private static Label Map(Row r) =>
        new(r.Id, r.SessionId, r.CustomerId, r.Platform, r.Username, r.MessageText,
            r.Code, r.Price, r.AddedAt, r.PrintedAt);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string MessageText { get; init; } = "";
        public string? Code { get; init; }
        public decimal Price { get; init; }
        public long AddedAt { get; init; }
        public long? PrintedAt { get; init; }
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
    bool IsPrinted);
