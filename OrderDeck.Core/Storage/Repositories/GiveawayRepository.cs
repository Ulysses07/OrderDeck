using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dapper;
using OrderDeck.Core.Sales;

namespace OrderDeck.Core.Storage.Repositories;

public sealed class GiveawayRepository
{
    private readonly IDbConnectionFactory _factory;
    public GiveawayRepository(IDbConnectionFactory factory) => _factory = factory;

    public void Insert(Giveaway g)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO Giveaway
              (Id, SessionId, Keyword, DurationSeconds, WinnerCount, PlatformFilter,
               PreventRewinning, RandomSeed, StartedAt, EndedAt, CancelledAt, AnimationId)
              VALUES
              (@Id, @SessionId, @Keyword, @DurationSeconds, @WinnerCount, @PlatformFilter,
               @PreventRewinning, @RandomSeed, @StartedAt, @EndedAt, @CancelledAt, @AnimationId)",
            new
            {
                g.Id, g.SessionId, g.Keyword, g.DurationSeconds, g.WinnerCount,
                PlatformFilter = g.PlatformFilter is null
                    ? null
                    : JsonSerializer.Serialize(g.PlatformFilter),
                PreventRewinning = g.PreventRewinning ? 1 : 0,
                g.RandomSeed,
                g.StartedAt, g.EndedAt, g.CancelledAt, g.AnimationId
            });
    }

    public Giveaway? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT * FROM Giveaway WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }

    public Giveaway? GetActiveBySession(string sessionId)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT * FROM Giveaway
              WHERE SessionId=@sessionId
                AND EndedAt IS NULL
                AND CancelledAt IS NULL
              ORDER BY StartedAt DESC
              LIMIT 1",
            new { sessionId });
        return row is null ? null : Map(row);
    }

    public void MarkEnded(string id, long endedAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Giveaway SET EndedAt=@endedAt WHERE Id=@id",
            new { id, endedAt });
    }

    public void MarkCancelled(string id, long cancelledAt)
    {
        using var conn = _factory.Open();
        conn.Execute("UPDATE Giveaway SET CancelledAt=@cancelledAt WHERE Id=@id",
            new { id, cancelledAt });
    }

    public void AddParticipant(GiveawayParticipant p)
    {
        using var conn = _factory.Open();
        conn.Execute(
            @"INSERT INTO GiveawayParticipant
              (Id, GiveawayId, CustomerId, Platform, Username, EnteredAt, IsWinner)
              VALUES
              (@Id, @GiveawayId, @CustomerId, @Platform, @Username, @EnteredAt, @IsWinner)",
            new
            {
                p.Id, p.GiveawayId, p.CustomerId, p.Platform, p.Username,
                p.EnteredAt, IsWinner = p.IsWinner ? 1 : 0
            });
    }

    public int GetParticipantCount(string giveawayId)
    {
        using var conn = _factory.Open();
        return conn.ExecuteScalar<int>(
            "SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId=@giveawayId",
            new { giveawayId });
    }

    public IReadOnlyList<GiveawayParticipant> GetParticipants(string giveawayId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<ParticipantRow>(
            @"SELECT Id, GiveawayId, CustomerId, Platform, Username, EnteredAt, IsWinner
              FROM GiveawayParticipant
              WHERE GiveawayId=@giveawayId
              ORDER BY EnteredAt",
            new { giveawayId }).ToList();
        return rows.Select(MapParticipant).ToList();
    }

    public void MarkWinners(IEnumerable<string> participantIds)
    {
        using var conn = _factory.Open();
        conn.Execute(
            "UPDATE GiveawayParticipant SET IsWinner=1 WHERE Id IN @ids",
            new { ids = participantIds.ToArray() });
    }

    /// <summary>
    /// Returns customer ids of winners from previous (NOT cancelled) giveaways in the
    /// same session, excluding the current giveaway. Used by PreventRewinning logic.
    /// </summary>
    public IReadOnlyList<string> GetWinnerCustomerIdsForSession(string sessionId, string currentGiveawayId)
    {
        using var conn = _factory.Open();
        return conn.Query<string>(
            @"SELECT DISTINCT gp.CustomerId
              FROM GiveawayParticipant gp
              INNER JOIN Giveaway g ON g.Id = gp.GiveawayId
              WHERE g.SessionId    = @sessionId
                AND g.Id           != @currentGiveawayId
                AND g.CancelledAt IS NULL
                AND gp.IsWinner    = 1",
            new { sessionId, currentGiveawayId }).ToList();
    }

    public GiveawaySessionTotals GetSessionTotals(string sessionId)
    {
        using var conn = _factory.Open();
        var count = conn.ExecuteScalar<int>(
            @"SELECT COUNT(*) FROM Giveaway
              WHERE SessionId = @sessionId
                AND EndedAt IS NOT NULL
                AND CancelledAt IS NULL",
            new { sessionId });

        var winnerTotal = conn.ExecuteScalar<int>(
            @"SELECT COUNT(*)
              FROM GiveawayParticipant gp
              INNER JOIN Giveaway g ON g.Id = gp.GiveawayId
              WHERE g.SessionId    = @sessionId
                AND g.EndedAt IS NOT NULL
                AND g.CancelledAt IS NULL
                AND gp.IsWinner    = 1",
            new { sessionId });

        return new GiveawaySessionTotals(count, winnerTotal);
    }

    /// <summary>
    /// Marks every still-running giveaway (no EndedAt, no CancelledAt) as cancelled with the
    /// given timestamp. Called once at app boot to clean up phantom rows from a previous
    /// crash. Returns the number of rows cleaned up.
    /// </summary>
    public int CancelAllOrphaned(long cancelledAt)
    {
        using var conn = _factory.Open();
        return conn.Execute(
            @"UPDATE Giveaway
              SET    CancelledAt = @cancelledAt
              WHERE  EndedAt IS NULL
                AND  CancelledAt IS NULL",
            new { cancelledAt });
    }

    /// <summary>
    /// Returns one summary row per completed (EndedAt set, not cancelled) giveaway in the
    /// session, ordered by start time. Used by the stream-end report.
    /// </summary>
    public IReadOnlyList<GiveawaySummary> ListSummariesBySession(string sessionId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string Id, string Keyword, int Total, int Winners, long StartedAt)>(
            @"SELECT  g.Id, g.Keyword,
                      COUNT(p.Id)                                                  AS Total,
                      COALESCE(SUM(CASE WHEN p.IsWinner = 1 THEN 1 ELSE 0 END), 0) AS Winners,
                      g.StartedAt
              FROM    Giveaway g
              LEFT JOIN GiveawayParticipant p ON p.GiveawayId = g.Id
              WHERE   g.SessionId  = @sessionId
                AND   g.EndedAt   IS NOT NULL
                AND   g.CancelledAt IS NULL
              GROUP BY g.Id, g.Keyword, g.StartedAt
              ORDER BY g.StartedAt;",
            new { sessionId });
        return rows
            .Select(r => new GiveawaySummary(r.Id, r.Keyword, r.Total, r.Winners, r.StartedAt))
            .ToList();
    }

    /// <summary>Returns the customer's giveaway participation history (joined with Giveaway),
    /// most recent first. Includes cancelled giveaways for audit visibility.</summary>
    public IReadOnlyList<CustomerGiveawayRow> GetParticipationsByCustomer(string customerId)
    {
        using var conn = _factory.Open();
        var rows = conn.Query<(string GiveawayId, string Keyword, long EnteredAt,
                                int IsWinner, long? GiveawayEndedAt, long? GiveawayCancelledAt)>(
            @"SELECT  gp.GiveawayId, g.Keyword, gp.EnteredAt, gp.IsWinner,
                      g.EndedAt    AS GiveawayEndedAt,
                      g.CancelledAt AS GiveawayCancelledAt
              FROM    GiveawayParticipant gp
              INNER JOIN Giveaway g ON g.Id = gp.GiveawayId
              WHERE   gp.CustomerId = @customerId
              ORDER BY gp.EnteredAt DESC",
            new { customerId });

        return rows
            .Select(r => new CustomerGiveawayRow(
                r.GiveawayId, r.Keyword, r.EnteredAt,
                IsWinner: r.IsWinner == 1,
                r.GiveawayEndedAt, r.GiveawayCancelledAt))
            .ToList();
    }

    private static Giveaway Map(Row r) => new(
        r.Id, r.SessionId, r.Keyword, r.DurationSeconds, r.WinnerCount,
        string.IsNullOrEmpty(r.PlatformFilter)
            ? null
            : JsonSerializer.Deserialize<List<string>>(r.PlatformFilter),
        r.PreventRewinning == 1,
        r.RandomSeed,
        r.StartedAt, r.EndedAt, r.CancelledAt, r.AnimationId);

    private static GiveawayParticipant MapParticipant(ParticipantRow r) =>
        new(r.Id, r.GiveawayId, r.CustomerId, r.Platform, r.Username, r.EnteredAt, r.IsWinner == 1);

    private sealed class Row
    {
        public string Id { get; init; } = "";
        public string SessionId { get; init; } = "";
        public string Keyword { get; init; } = "";
        public int DurationSeconds { get; init; }
        public int WinnerCount { get; init; }
        public string? PlatformFilter { get; init; }
        public int PreventRewinning { get; init; }
        public string RandomSeed { get; init; } = "";
        public long StartedAt { get; init; }
        public long? EndedAt { get; init; }
        public long? CancelledAt { get; init; }
        public string AnimationId { get; init; } = "wheel";
    }

    private sealed class ParticipantRow
    {
        public string Id { get; init; } = "";
        public string GiveawayId { get; init; } = "";
        public string CustomerId { get; init; } = "";
        public string Platform { get; init; } = "";
        public string Username { get; init; } = "";
        public long EnteredAt { get; init; }
        public int IsWinner { get; init; }
    }
}

public sealed record GiveawaySessionTotals(int Count, int TotalWinners);

/// <summary>One row per completed giveaway, used by the stream-end report.</summary>
public sealed record GiveawaySummary(
    string Id, string Keyword, int ParticipantCount, int WinnerCount, long StartedAt);

/// <summary>UI projection of a customer's giveaway participation row.</summary>
public sealed record CustomerGiveawayRow(
    string GiveawayId,
    string Keyword,
    long EnteredAt,
    bool IsWinner,
    long? GiveawayEndedAt,
    long? GiveawayCancelledAt);
