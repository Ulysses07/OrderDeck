using System;
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
               IsBlacklisted, BlacklistReason, Notes,
               TotalLabelsPrinted, TotalAmount, BlacklistedAt, Address, Phone)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt, @Address, @Phone)",
            new
            {
                c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
                IsBlacklisted = c.IsBlacklisted ? 1 : 0,
                c.BlacklistReason, c.Notes,
                c.TotalLabelsPrinted, c.TotalAmount, c.BlacklistedAt, c.Address, c.Phone
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

    /// <summary>Updates only the Notes column. Whitespace input normalizes to NULL.</summary>
    public void UpdateNotes(string customerId, string? notes)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Customer SET Notes=@notes WHERE Id=@id",
            new { id = customerId, notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim() });
    }

    /// <summary>Case-insensitive substring search on Username, ordered by LastSeenAt DESC.</summary>
    public IReadOnlyList<Customer> Search(string usernameContains, int limit = 50)
    {
        if (string.IsNullOrWhiteSpace(usernameContains))
            return System.Array.Empty<Customer>();

        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT * FROM Customer
              WHERE LOWER(Username) LIKE LOWER(@q)
              ORDER BY LastSeenAt DESC
              LIMIT @limit",
            new { q = "%" + usernameContains + "%", limit }).ToList();
        return rows.Select(Map).ToList();
    }

    private static Customer Map(Row r) => new(
        r.Id, r.Platform, r.Username, r.DisplayName, r.AvatarUrl,
        r.FirstSeenAt, r.LastSeenAt,
        r.IsBlacklisted == 1, r.BlacklistReason, r.Notes,
        r.TotalLabelsPrinted, r.TotalAmount, r.BlacklistedAt, r.Address, r.Phone);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public string? DisplayName { get; init; }
        public string? AvatarUrl { get; init; }
        public long FirstSeenAt { get; init; }
        public long LastSeenAt { get; init; }
        public int IsBlacklisted { get; init; }
        public string? BlacklistReason { get; init; }
        public string? Notes { get; init; }
        public int TotalLabelsPrinted { get; init; }
        public decimal TotalAmount { get; init; }
        public long? BlacklistedAt { get; init; }
        public string? Address { get; init; }
        public string? Phone { get; init; }
    }

    /// <summary>
    /// Upsert by (Platform, Username). Phase 4f intake form sync için.
    /// Mevcut müşteri varsa DisplayName, Address, LastSeenAt güncellenir;
    /// yoksa yeni satır insert edilir.
    /// </summary>
    public Customer UpsertFromIntakeForm(string username, string fullName, string address, string? phone, long nowUnix)
    {
        const string platform = "form";
        using var conn = _factory.Open();

        var existing = conn.QueryFirstOrDefault<Row>(@"
            SELECT Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                   IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                   BlacklistedAt, Address, Phone
            FROM Customer
            WHERE Platform = @platform AND Username = @username",
            new { platform, username });

        if (existing is not null)
        {
            conn.Execute(@"
                UPDATE Customer
                SET DisplayName = @fullName,
                    Address = @address,
                    Phone = @phone,
                    LastSeenAt = @nowUnix
                WHERE Id = @id",
                new { fullName, address, phone, nowUnix, id = existing.Id });
            var updated = Map(existing);
            return updated with { DisplayName = fullName, Address = address, Phone = phone, LastSeenAt = nowUnix };
        }

        var id = Guid.NewGuid().ToString("N");
        conn.Execute(@"
            INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                                  IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                                  BlacklistedAt, Address, Phone)
            VALUES (@id, @platform, @username, @fullName, NULL, @nowUnix, @nowUnix,
                    0, NULL, NULL, 0, 0, NULL, @address, @phone)",
            new { id, platform, username, fullName, nowUnix, address, phone });

        return new Customer(id, platform, username, fullName, null, nowUnix, nowUnix,
            false, null, null, 0, 0m, null, address, phone);
    }

    /// <summary>Phase 4g: WhatsApp E.164 telefonu güncelle. Geçersiz id no-op.</summary>
    public void UpdatePhone(string customerId, string e164Phone)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE Customer SET Phone=@phone WHERE Id=@id",
            new { phone = e164Phone, id = customerId });
    }
}
