using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dapper;
using LiveDeck.Core.Sales;

namespace LiveDeck.Core.Storage.Repositories;

public sealed class ActiveCodeRepository
{
    private readonly IDbConnectionFactory _factory;

    public ActiveCodeRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(ActiveCode code)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO ActiveCode
              (Id, SessionId, Code, Sizes, Price, ImageUrl, Aliases, StartedAt, EndedAt)
              VALUES (@Id, @SessionId, @Code, @Sizes, @Price, @ImageUrl, @Aliases, @StartedAt, @EndedAt)",
            new
            {
                code.Id,
                code.SessionId,
                code.Code,
                Sizes = JsonSerializer.Serialize(code.Sizes),
                code.Price,
                code.ImageUrl,
                Aliases = JsonSerializer.Serialize(code.Aliases),
                code.StartedAt,
                code.EndedAt
            });
    }

    public void Update(ActiveCode code)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"UPDATE ActiveCode
              SET Code=@Code, Sizes=@Sizes, Price=@Price, ImageUrl=@ImageUrl, Aliases=@Aliases
              WHERE Id=@Id",
            new
            {
                code.Id,
                code.Code,
                Sizes = JsonSerializer.Serialize(code.Sizes),
                code.Price,
                code.ImageUrl,
                Aliases = JsonSerializer.Serialize(code.Aliases)
            });
    }

    public void End(string id, long endedAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE ActiveCode SET EndedAt=@endedAt WHERE Id=@id", new { id, endedAt });
    }

    public IReadOnlyList<ActiveCode> GetActiveBySession(string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<Row>(
            @"SELECT Id, SessionId, Code, Sizes, Price, ImageUrl, Aliases, StartedAt, EndedAt
              FROM ActiveCode
              WHERE SessionId=@sessionId AND EndedAt IS NULL
              ORDER BY StartedAt",
            new { sessionId }).ToList();

        return rows.Select(MapRow).ToList();
    }

    public ActiveCode? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT Id, SessionId, Code, Sizes, Price, ImageUrl, Aliases, StartedAt, EndedAt
              FROM ActiveCode WHERE Id=@id",
            new { id });
        return row is null ? null : MapRow(row);
    }

    private static ActiveCode MapRow(Row r) => new(
        r.Id,
        r.SessionId,
        r.Code,
        JsonSerializer.Deserialize<List<string>>(r.Sizes ?? "[]") ?? new List<string>(),
        r.Price,
        r.ImageUrl,
        JsonSerializer.Deserialize<List<string>>(r.Aliases ?? "[]") ?? new List<string>(),
        r.StartedAt,
        r.EndedAt);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Code { get; init; } = "";
        public string? Sizes { get; init; }
        public decimal Price { get; init; }
        public string? ImageUrl { get; init; }
        public string? Aliases { get; init; }
        public long StartedAt { get; init; }
        public long? EndedAt { get; init; }
    }
}
