using System.IO.Compression;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;

namespace OrderDeck.LicenseServer.Services.Backup;

/// <summary>
/// Phase 5a admin viewer: per-request lifecycle wrapping a decrypted SQLite db on temp disk.
/// Owns temp directory + read-only SqliteConnection. Dispose deletes temp + closes connection.
/// </summary>
public sealed class BackupSession : IAsyncDisposable, IDisposable
{
    private readonly string _tempDir;
    private readonly SqliteConnection _conn;
    private readonly ILogger _log;
    private bool _disposed;

    private const int DefaultPageSize = 50;

    public BackupSession(string tempDir, SqliteConnection conn, ILogger log)
    {
        _tempDir = tempDir;
        _conn = conn;
        _log = log;
    }

    public async Task<BackupSummary> GetSummaryAsync(CancellationToken ct = default)
    {
        var totalSessions = await ScalarLong("SELECT COUNT(*) FROM StreamSession WHERE EndedAt IS NOT NULL", ct);
        var totalLabels = await ScalarLong("SELECT COUNT(*) FROM Label WHERE PrintedAt IS NOT NULL", ct);
        var totalCustomers = await ScalarLong("SELECT COUNT(*) FROM Customer", ct);
        var totalRevenue = await ScalarDecimal("SELECT COALESCE(SUM(Price), 0) FROM Label WHERE PrintedAt IS NOT NULL", ct);

        var avgPerSession = totalSessions == 0 ? 0m : totalRevenue / totalSessions;
        var avgPerCustomer = totalCustomers == 0 ? 0m : totalRevenue / totalCustomers;

        TopSession? topSession = null;
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT s.Title, s.StartedAt, SUM(l.Price) AS Total
                FROM StreamSession s JOIN Label l ON l.SessionId = s.Id
                WHERE l.PrintedAt IS NOT NULL
                GROUP BY s.Id ORDER BY Total DESC LIMIT 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                var title = r.IsDBNull(0) ? null : r.GetString(0);
                var started = r.GetInt64(1);
                var total = r.GetDecimal(2);
                topSession = new TopSession(title,
                    DateTimeOffset.FromUnixTimeSeconds(started),
                    total);
            }
        }

        TopCustomer? topCustomer = null;
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = @"
                SELECT c.Username, SUM(l.Price) AS Total, COUNT(*) AS LabelCount
                FROM Customer c JOIN Label l ON l.CustomerId = c.Id
                WHERE l.PrintedAt IS NOT NULL
                GROUP BY c.Id ORDER BY Total DESC LIMIT 1";
            await using var r = await cmd.ExecuteReaderAsync(ct);
            if (await r.ReadAsync(ct))
            {
                topCustomer = new TopCustomer(r.GetString(0), r.GetDecimal(1), r.GetInt32(2));
            }
        }

        return new BackupSummary(
            (int)totalSessions, (int)totalLabels, (int)totalCustomers,
            totalRevenue, avgPerSession, avgPerCustomer,
            topSession, topCustomer);
    }

    public async Task<PagedResult<CustomerRow>> GetCustomersAsync(int page, string? search, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var where = string.IsNullOrWhiteSpace(search)
            ? ""
            : "WHERE LOWER(Username) LIKE LOWER(@q) OR LOWER(COALESCE(DisplayName,'')) LIKE LOWER(@q)";

        var total = await ScalarLong($"SELECT COUNT(*) FROM Customer {where}", ct,
            search is null ? null : ("@q", $"%{search}%"));

        var rows = new List<CustomerRow>();
        await using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = $@"
                SELECT Id, Platform, Username, DisplayName, Address, Phone, TotalAmount, LastSeenAt
                FROM Customer {where}
                ORDER BY LastSeenAt DESC
                LIMIT @limit OFFSET @offset";
            cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
            cmd.Parameters.AddWithValue("@offset", offset);
            if (search is not null) cmd.Parameters.AddWithValue("@q", $"%{search}%");
            await using var r = await cmd.ExecuteReaderAsync(ct);
            while (await r.ReadAsync(ct))
            {
                rows.Add(new CustomerRow(
                    r.GetString(0), r.GetString(1), r.GetString(2),
                    r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetString(5),
                    r.GetDecimal(6), r.GetInt64(7)));
            }
        }
        return new PagedResult<CustomerRow>(rows, (int)total, page, DefaultPageSize);
    }

    public async Task<PagedResult<SessionRow>> GetSessionsAsync(int page, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var total = await ScalarLong("SELECT COUNT(*) FROM StreamSession", ct);

        var rows = new List<SessionRow>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT s.Id, s.Title, s.StartedAt, s.EndedAt,
                   (SELECT COUNT(*) FROM Label WHERE SessionId = s.Id AND PrintedAt IS NOT NULL) AS LabelCount,
                   COALESCE((SELECT SUM(Price) FROM Label WHERE SessionId = s.Id AND PrintedAt IS NOT NULL), 0) AS TotalAmount
            FROM StreamSession s
            ORDER BY s.StartedAt DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new SessionRow(
                r.GetString(0),
                r.IsDBNull(1) ? null : r.GetString(1),
                r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetInt32(4), r.GetDecimal(5)));
        }
        return new PagedResult<SessionRow>(rows, (int)total, page, DefaultPageSize);
    }

    public async Task<PagedResult<LabelRow>> GetLabelsAsync(int page, string? sessionId, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var where = sessionId is null ? "" : "WHERE l.SessionId = @sessionId";

        var total = await ScalarLong($"SELECT COUNT(*) FROM Label l {where}", ct,
            sessionId is null ? null : ("@sessionId", sessionId));

        var rows = new List<LabelRow>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = $@"
            SELECT l.Id, l.SessionId, c.Username, l.Code, l.Price, l.AddedAt, l.PrintedAt
            FROM Label l JOIN Customer c ON c.Id = l.CustomerId
            {where}
            ORDER BY l.AddedAt DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        if (sessionId is not null) cmd.Parameters.AddWithValue("@sessionId", sessionId);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new LabelRow(
                r.GetString(0), r.GetString(1), r.GetString(2),
                r.IsDBNull(3) ? null : r.GetString(3),
                r.GetDecimal(4), r.GetInt64(5),
                r.IsDBNull(6) ? null : r.GetInt64(6)));
        }
        return new PagedResult<LabelRow>(rows, (int)total, page, DefaultPageSize);
    }

    public async Task<PagedResult<GiveawayRow>> GetGiveawaysAsync(int page, CancellationToken ct = default)
    {
        var offset = Math.Max(0, (page - 1)) * DefaultPageSize;
        var total = await ScalarLong("SELECT COUNT(*) FROM Giveaway", ct);

        var rows = new List<GiveawayRow>();
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
            SELECT g.Id, g.Keyword, g.StartedAt, g.EndedAt,
                   (SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId = g.Id) AS ParticipantCount,
                   (SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId = g.Id AND IsWinner = 1) AS WinnerCount
            FROM Giveaway g
            ORDER BY g.StartedAt DESC
            LIMIT @limit OFFSET @offset";
        cmd.Parameters.AddWithValue("@limit", DefaultPageSize);
        cmd.Parameters.AddWithValue("@offset", offset);
        await using var r = await cmd.ExecuteReaderAsync(ct);
        while (await r.ReadAsync(ct))
        {
            rows.Add(new GiveawayRow(
                r.GetString(0), r.GetString(1),
                r.IsDBNull(2) ? null : r.GetInt64(2),
                r.IsDBNull(3) ? null : r.GetInt64(3),
                r.GetInt32(4), r.GetInt32(5)));
        }
        return new PagedResult<GiveawayRow>(rows, (int)total, page, DefaultPageSize);
    }

    private async Task<long> ScalarLong(string sql, CancellationToken ct, (string name, object value)? param = null)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        if (param.HasValue) cmd.Parameters.AddWithValue(param.Value.name, param.Value.value);
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? 0L : Convert.ToInt64(v);
    }

    private async Task<decimal> ScalarDecimal(string sql, CancellationToken ct)
    {
        await using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        var v = await cmd.ExecuteScalarAsync(ct);
        return v is null || v is DBNull ? 0m : Convert.ToDecimal(v);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _conn.DisposeAsync();
        // Microsoft.Data.Sqlite pools connections by default; on Windows the pooled
        // handle keeps the .db file locked, blocking recursive temp-dir delete. Drain pools.
        SqliteConnection.ClearPool(_conn);
        TryDeleteTempDir();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _conn.Dispose();
        SqliteConnection.ClearPool(_conn);
        TryDeleteTempDir();
    }

    private void TryDeleteTempDir()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { _log.LogWarning(ex, "Failed to delete backup temp dir {Dir}", _tempDir); }
    }
}
