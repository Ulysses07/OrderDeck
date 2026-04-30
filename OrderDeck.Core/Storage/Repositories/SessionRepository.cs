using System.Linq;
using System.Text.Json;
using Dapper;
using OrderDeck.Core.Sessions;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class SessionRepository
{
    private readonly IDbConnectionFactory _factory;
    public SessionRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(StreamSession session)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO StreamSession(Id, Title, StartedAt, EndedAt, Platforms, Notes)
              VALUES(@Id, @Title, @StartedAt, @EndedAt, @Platforms, @Notes)",
            new
            {
                session.Id,
                session.Title,
                session.StartedAt,
                session.EndedAt,
                Platforms = JsonSerializer.Serialize(session.Platforms),
                session.Notes
            });
    }

    public void End(string id, long endedAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE StreamSession SET EndedAt=@endedAt WHERE Id=@id",
            new { id, endedAt });
    }

    public StreamSession? GetActive()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes " +
            "FROM StreamSession WHERE EndedAt IS NULL ORDER BY StartedAt DESC LIMIT 1");
        return row is null ? null : Map(row);
    }

    public StreamSession? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes " +
            "FROM StreamSession WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    /// <summary>Phase 4g: en son tamamlanmış (EndedAt dolu) session'ı döndürür.</summary>
    public StreamSession? GetLatestEnded()
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes
              FROM StreamSession
              WHERE EndedAt IS NOT NULL
              ORDER BY EndedAt DESC
              LIMIT 1");
        return row is null ? null : Map(row);
    }

    public System.Collections.Generic.IReadOnlyList<Sessions.StreamSession> GetAllEnded(int limit)
    {
        using var conn = _factory.Open();
        var rows = Dapper.SqlMapper.Query<Row>(conn,
            @"SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes
              FROM StreamSession
              WHERE EndedAt IS NOT NULL
              ORDER BY StartedAt DESC
              LIMIT @limit",
            new { limit }).ToList();
        return rows.Select(Map).ToList();
    }

    private static StreamSession Map(Row r) => new(
        r.Id, r.Title, r.StartedAt, r.EndedAt,
        JsonSerializer.Deserialize<string[]>(r.Platforms ?? "[]") ?? System.Array.Empty<string>(),
        r.Notes);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string? Title { get; init; }
        public long StartedAt { get; init; }
        public long? EndedAt { get; init; }
        public string? Platforms { get; init; }
        public string? Notes { get; init; }
    }
}
