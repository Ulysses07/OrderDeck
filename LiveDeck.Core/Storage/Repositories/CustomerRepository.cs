using System.Collections.Generic;
using System.Linq;
using Dapper;
using LiveDeck.Core.Customers;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class CustomerRepository
{
    private readonly IDbConnectionFactory _factory;
    public CustomerRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(Customer c)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Customer
              (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
               TotalOrders, CompletedOrders, CancelledOrders, TrustScore,
               IsBlacklisted, BlacklistReason, Notes,
               TotalLabelsPrinted, TotalAmount, BlacklistedAt)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @TotalOrders, @CompletedOrders, @CancelledOrders, @TrustScore,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt)",
            new
            {
                c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
                c.TotalOrders, c.CompletedOrders, c.CancelledOrders, c.TrustScore,
                IsBlacklisted = c.IsBlacklisted ? 1 : 0,
                c.BlacklistReason, c.Notes,
                c.TotalLabelsPrinted, c.TotalAmount, c.BlacklistedAt
            });
    }

    public Customer? FindByPlatformAndUsername(string platform, string username)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Platform=@platform AND Username=@username",
            new { platform, username });
        return row is null ? null : Map(row);
    }

    public Customer? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Customer WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public void IncrementLabelStats(string id, int labelDelta, decimal amountDelta, long lastSeenAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Customer
              SET TotalLabelsPrinted = TotalLabelsPrinted + @labelDelta,
                  TotalAmount        = TotalAmount + @amountDelta,
                  LastSeenAt         = @lastSeenAt
              WHERE Id = @id",
            new { id, labelDelta, amountDelta, lastSeenAt });
    }

    /// <summary>Sets or clears the blacklist flag, with optional reason and timestamp.</summary>
    public void UpdateBlacklist(string id, bool isBlacklisted, string? reason, long? blacklistedAt)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE Customer
              SET IsBlacklisted   = @flag,
                  BlacklistReason = @reason,
                  BlacklistedAt   = @blacklistedAt
              WHERE Id = @id",
            new
            {
                id,
                flag = isBlacklisted ? 1 : 0,
                reason,
                blacklistedAt
            });
    }

    /// <summary>Returns all currently-blacklisted customers, newest first.</summary>
    public IReadOnlyList<Customer> GetBlacklisted()
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Customer
              WHERE IsBlacklisted = 1
              ORDER BY COALESCE(BlacklistedAt, 0) DESC").ToList();
        return rows.Select(Map).ToList();
    }

    private static Customer Map(Row r) => new(
        r.Id, r.Platform, r.Username, r.DisplayName, r.AvatarUrl,
        r.FirstSeenAt, r.LastSeenAt,
        r.TotalOrders, r.CompletedOrders, r.CancelledOrders, r.TrustScore,
        r.IsBlacklisted == 1, r.BlacklistReason, r.Notes,
        r.TotalLabelsPrinted, r.TotalAmount, r.BlacklistedAt);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? AvatarUrl { get; init; }
        public long FirstSeenAt { get; init; }
        public long LastSeenAt { get; init; }
        public int TotalOrders { get; init; }
        public int CompletedOrders { get; init; }
        public int CancelledOrders { get; init; }
        public int TrustScore { get; init; }
        public int IsBlacklisted { get; init; }
        public string? BlacklistReason { get; init; }
        public string? Notes { get; init; }
        public int TotalLabelsPrinted { get; init; }
        public decimal TotalAmount { get; init; }
        public long? BlacklistedAt { get; init; }
    }
}
