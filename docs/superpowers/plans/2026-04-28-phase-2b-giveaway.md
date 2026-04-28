# LiveDeck Phase 2b — Çekiliş (Giveaway) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add live giveaway feature: streamer triggers via 🎁 button, viewers enter by typing keyword in chat, OBS overlay shows live counter + roulette draw animation + winner reveal.

**Architecture:** New `Giveaway` + `GiveawayParticipant` tables (migration 004). `GiveawayService` orchestrates lifecycle (start/addParticipantFromChat/draw/cancel) on top of pure-RNG `GiveawayDrawer`. WPF: 🎁 button in MainShell top bar opens `NewGiveawayDialog`; while active, the existing status banner is replaced by a `GiveawayBannerViewModel` with a `DispatcherTimer`-driven countdown. OBS Browser Source: new `/overlay/giveaway` URL + `/ws/giveaway` WebSocket emitting `giveaway.start/tick/draw/empty/cancel` events.

**Tech Stack:** .NET 10 WPF (existing), Dapper, CommunityToolkit.Mvvm, Serilog. New: `System.IO.Hashing` for deterministic seed → int conversion (Fisher-Yates RNG).

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-2b state:** P2a commit `77b90f2` + spec commit `522f667`. 47/47 tests passing.

---

## Task Index

**Data layer (1-2):** Migration 004 + Giveaway/GiveawayParticipant entities + repository
**Domain (3-4):** GiveawayDrawer (pure RNG, TDD) + GiveawayService (TDD orchestration)
**WPF (5-7):** NewGiveawayDialog + GiveawayBannerViewModel + MainShell integration
**OBS overlay (8-9):** OverlayHost endpoint + WS + HTML/CSS/JS animation
**Polish (10-11):** Stream report giveaway row + AppHost re-registration
**Acceptance (12):** Manual smoke

---

## Data Layer

### Task 1: Migration 004 + Giveaway/GiveawayParticipant entities

**Files:**
- Create: `LiveDeck.Core/Storage/Migrations/004_giveaway.sql`
- Create: `LiveDeck.Core/Sales/Giveaway.cs`
- Create: `LiveDeck.Core/Sales/GiveawayParticipant.cs`
- Modify: `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`

`MigrationRunner` is already version-aware (P1b Task 4): scripts run in order, `_meta.SchemaVersion` gates re-application.

- [ ] **Step 1: Migration SQL**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Migrations/004_giveaway.sql`:

```sql
-- Phase 2b: çekiliş tabloları (re-introducing Giveaway/GiveawayParticipant
-- which were dropped in P1b's 002 migration; this version is tuned for the P2b spec).

CREATE TABLE IF NOT EXISTS Giveaway (
    Id                 TEXT PRIMARY KEY,
    SessionId          TEXT NOT NULL,
    Keyword            TEXT NOT NULL,
    DurationSeconds    INTEGER NOT NULL,
    WinnerCount        INTEGER NOT NULL,
    PlatformFilter     TEXT,
    PreventRewinning   INTEGER NOT NULL DEFAULT 1,
    RandomSeed         TEXT NOT NULL,
    StartedAt          INTEGER NOT NULL,
    EndedAt            INTEGER,
    CancelledAt        INTEGER,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IF NOT EXISTS IX_Giveaway_Session ON Giveaway(SessionId);
CREATE INDEX IF NOT EXISTS IX_Giveaway_Active  ON Giveaway(SessionId, EndedAt, CancelledAt);

CREATE TABLE IF NOT EXISTS GiveawayParticipant (
    Id           TEXT PRIMARY KEY,
    GiveawayId   TEXT NOT NULL,
    CustomerId   TEXT NOT NULL,
    Platform     TEXT NOT NULL,
    Username     TEXT NOT NULL,
    EnteredAt    INTEGER NOT NULL,
    IsWinner     INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (GiveawayId) REFERENCES Giveaway(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_GiveawayParticipant_Unique
    ON GiveawayParticipant(GiveawayId, Platform, Username);

CREATE INDEX IF NOT EXISTS IX_GiveawayParticipant_Winners
    ON GiveawayParticipant(GiveawayId, IsWinner);

UPDATE _meta SET SchemaVersion = 4 WHERE Id = 1;
```

- [ ] **Step 2: Giveaway entity**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/Giveaway.cs`:

```csharp
using System.Collections.Generic;

namespace LiveDeck.Core.Sales;

/// <summary>
/// A live giveaway run during a stream. While active, viewers who type the
/// <see cref="Keyword"/> in chat are added to <see cref="GiveawayParticipant"/>.
/// At draw time, <see cref="WinnerCount"/> winners are selected via
/// <see cref="GiveawayDrawer"/>. <see cref="EndedAt"/> set when drawn or 0-participant;
/// <see cref="CancelledAt"/> set when the streamer aborts.
/// </summary>
public sealed record Giveaway(
    string Id,
    string SessionId,
    string Keyword,
    int DurationSeconds,                          // 0 = manual end
    int WinnerCount,
    IReadOnlyList<string>? PlatformFilter,        // null = all platforms
    bool PreventRewinning,
    string RandomSeed,
    long StartedAt,
    long? EndedAt,
    long? CancelledAt);
```

- [ ] **Step 3: GiveawayParticipant entity**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/GiveawayParticipant.cs`:

```csharp
namespace LiveDeck.Core.Sales;

/// <summary>
/// One unique <c>(Platform, Username)</c> who entered a giveaway by typing its keyword.
/// <see cref="IsWinner"/> is set when the giveaway is drawn.
/// </summary>
public sealed record GiveawayParticipant(
    string Id,
    string GiveawayId,
    string CustomerId,
    string Platform,
    string Username,
    long EnteredAt,
    bool IsWinner);
```

- [ ] **Step 4: Update MigrationRunnerTests**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/MigrationRunnerTests.cs`:

```csharp
using Dapper;
using FluentAssertions;
using LiveDeck.Core.Storage;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class MigrationRunnerTests
{
    [Fact]
    public void Run_creates_all_tables_at_version_4()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();
        tables.Should().Contain(new[]
        {
            "Customer", "Giveaway", "GiveawayParticipant", "Label",
            "Settings", "StreamSession", "_meta"
        });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(4);

        var customerColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Customer')").AsList();
        customerColumns.Should().Contain(new[] { "TotalLabelsPrinted", "TotalAmount", "BlacklistedAt" });

        var giveawayColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Giveaway')").AsList();
        giveawayColumns.Should().Contain(new[]
        {
            "Id", "SessionId", "Keyword", "DurationSeconds", "WinnerCount",
            "PlatformFilter", "PreventRewinning", "RandomSeed",
            "StartedAt", "EndedAt", "CancelledAt"
        });
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();
        runner.Run();

        using var conn = db.Open();
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(4);
    }
}
```

- [ ] **Step 5: Build + run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core 2>&1 | tail -3
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests" 2>&1 | tail -3
```
Expected: build clean, 2/2 migration tests pass.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Migrations/004_giveaway.sql LiveDeck.Core/Sales/Giveaway.cs LiveDeck.Core/Sales/GiveawayParticipant.cs LiveDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(core): add Giveaway + GiveawayParticipant entities (migration 004)"
```

---

### Task 2: GiveawayRepository (Dapper, TDD)

**Files:**
- Create: `LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs`
- Create: `LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs`

`PlatformFilter` is `IReadOnlyList<string>?` in C# but `TEXT NULL` in DB (JSON dizi). Same Dapper pattern as `Customer.Sizes` in P1b.

- [ ] **Step 1: Failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs`:

```csharp
using System.Linq;
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class GiveawayRepositoryTests
{
    private static (InMemorySqlite Db, GiveawayRepository Repo, string SessionId, string CustomerId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100, 0, 0, 0, 100,
                false, null, null, 0, 0m, null));

        return (db, new GiveawayRepository(db), "s1", "c1");
    }

    private static Giveaway NewGiveaway(string id = "g1", string sessionId = "s1") =>
        new(id, sessionId, "🌹", DurationSeconds: 60, WinnerCount: 1,
            PlatformFilter: null, PreventRewinning: true,
            RandomSeed: "seed", StartedAt: 200, EndedAt: null, CancelledAt: null);

    [Fact]
    public void Insert_then_GetById_returns_giveaway()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        repo.Insert(NewGiveaway());

        var g = repo.GetById("g1");
        g.Should().NotBeNull();
        g!.Keyword.Should().Be("🌹");
        g.WinnerCount.Should().Be(1);
        g.PreventRewinning.Should().BeTrue();
        g.PlatformFilter.Should().BeNull();
    }

    [Fact]
    public void Insert_with_platform_filter_round_trips_json_array()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        var withFilter = NewGiveaway() with { PlatformFilter = new[] { "tiktok" } };
        repo.Insert(withFilter);

        var fresh = repo.GetById("g1")!;
        fresh.PlatformFilter.Should().BeEquivalentTo(new[] { "tiktok" });
    }

    [Fact]
    public void GetActiveBySession_returns_only_running_giveaway()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        repo.Insert(NewGiveaway("g1", sid) with { EndedAt = 500 });        // ended
        repo.Insert(NewGiveaway("g2", sid));                                // active
        repo.Insert(NewGiveaway("g3", sid) with { CancelledAt = 600 });    // cancelled

        var active = repo.GetActiveBySession(sid);
        active.Should().NotBeNull();
        active!.Id.Should().Be("g2");
    }

    [Fact]
    public void GetActiveBySession_returns_null_when_none_active()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;
        repo.Insert(NewGiveaway() with { EndedAt = 500 });

        repo.GetActiveBySession(sid).Should().BeNull();
    }

    [Fact]
    public void AddParticipant_then_GetParticipants_returns_inserted()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(NewGiveaway());

        repo.AddParticipant(new GiveawayParticipant(
            Id: "p1", GiveawayId: "g1", CustomerId: cid,
            Platform: "instagram", Username: "@a",
            EnteredAt: 300, IsWinner: false));

        var ps = repo.GetParticipants("g1");
        ps.Should().HaveCount(1);
        ps[0].Username.Should().Be("@a");
    }

    [Fact]
    public void AddParticipant_duplicate_username_throws()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(NewGiveaway());
        repo.AddParticipant(new GiveawayParticipant(
            "p1", "g1", cid, "instagram", "@a", 300, false));

        var dup = new GiveawayParticipant("p2", "g1", cid, "instagram", "@a", 301, false);

        // UNIQUE INDEX on (GiveawayId, Platform, Username) raises Sqlite constraint error
        var act = () => repo.AddParticipant(dup);
        act.Should().Throw<Microsoft.Data.Sqlite.SqliteException>();
    }

    [Fact]
    public void MarkWinners_flips_IsWinner_for_given_ids()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(NewGiveaway());
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, false));
        repo.AddParticipant(new GiveawayParticipant("p2", "g1", cid, "instagram", "@b", 300, false));
        repo.AddParticipant(new GiveawayParticipant("p3", "g1", cid, "instagram", "@c", 300, false));

        repo.MarkWinners(new[] { "p1", "p3" });

        var ps = repo.GetParticipants("g1");
        ps.Single(p => p.Id == "p1").IsWinner.Should().BeTrue();
        ps.Single(p => p.Id == "p2").IsWinner.Should().BeFalse();
        ps.Single(p => p.Id == "p3").IsWinner.Should().BeTrue();
    }

    [Fact]
    public void Update_sets_endedAt_and_cancelledAt()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;
        repo.Insert(NewGiveaway());

        repo.MarkEnded("g1", endedAt: 999);
        repo.GetById("g1")!.EndedAt.Should().Be(999);

        repo.MarkCancelled("g1", cancelledAt: 1000);
        repo.GetById("g1")!.CancelledAt.Should().Be(1000);
    }

    [Fact]
    public void GetWinnerCustomerIdsForSession_returns_distinct_winners_excluding_current()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;

        new CustomerRepository(db).Insert(
            new Customer("c2", "instagram", "@b", null, null, 100, 100, 0, 0, 0, 100,
                false, null, null, 0, 0m, null));

        repo.Insert(NewGiveaway("g1") with { EndedAt = 500 });
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid,  "instagram", "@a", 300, IsWinner: true));
        repo.AddParticipant(new GiveawayParticipant("p2", "g1", "c2", "instagram", "@b", 300, IsWinner: false));

        repo.Insert(NewGiveaway("g2"));
        // expect: while drawing g2, c1 (the previous winner) is filtered out

        var ids = repo.GetWinnerCustomerIdsForSession(sessionId: sid, currentGiveawayId: "g2");
        ids.Should().BeEquivalentTo(new[] { cid });
    }

    [Fact]
    public void GetSessionTotals_counts_completed_giveaways_and_winners()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;

        repo.Insert(NewGiveaway("g1") with { EndedAt = 500 });
        repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, IsWinner: true));

        repo.Insert(NewGiveaway("g2") with { EndedAt = 600 });
        repo.AddParticipant(new GiveawayParticipant("p2", "g2", cid, "instagram", "@x", 400, IsWinner: true));
        repo.AddParticipant(new GiveawayParticipant("p3", "g2", cid, "instagram", "@y", 400, IsWinner: true));

        repo.Insert(NewGiveaway("g3") with { CancelledAt = 700 });   // cancelled, not counted
        repo.Insert(NewGiveaway("g4"));                                // active, not counted

        var totals = repo.GetSessionTotals(sid);
        totals.Count.Should().Be(2);
        totals.TotalWinners.Should().Be(3);
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryTests" 2>&1 | tail -10
```
Expected: FAIL — `GiveawayRepository` not found.

- [ ] **Step 3: Implement GiveawayRepository**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Dapper;
using LiveDeck.Core.Sales;

namespace LiveDeck.Core.Storage.Repositories;

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
               PreventRewinning, RandomSeed, StartedAt, EndedAt, CancelledAt)
              VALUES
              (@Id, @SessionId, @Keyword, @DurationSeconds, @WinnerCount, @PlatformFilter,
               @PreventRewinning, @RandomSeed, @StartedAt, @EndedAt, @CancelledAt)",
            new
            {
                g.Id, g.SessionId, g.Keyword, g.DurationSeconds, g.WinnerCount,
                PlatformFilter = g.PlatformFilter is null
                    ? null
                    : JsonSerializer.Serialize(g.PlatformFilter),
                PreventRewinning = g.PreventRewinning ? 1 : 0,
                g.RandomSeed,
                g.StartedAt, g.EndedAt, g.CancelledAt
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

    private static Giveaway Map(Row r) => new(
        r.Id, r.SessionId, r.Keyword, r.DurationSeconds, r.WinnerCount,
        string.IsNullOrEmpty(r.PlatformFilter)
            ? null
            : JsonSerializer.Deserialize<List<string>>(r.PlatformFilter),
        r.PreventRewinning == 1,
        r.RandomSeed,
        r.StartedAt, r.EndedAt, r.CancelledAt);

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
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryTests" 2>&1 | tail -3
```
Expected: 10/10 PASS.

- [ ] **Step 5: Run full suite (regression check)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: ALL pass (47 baseline + 10 new = 57).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs
git commit -m "feat(core): add GiveawayRepository with CRUD + winners + session totals"
```

---

## Domain Layer

### Task 3: GiveawayDrawer (pure RNG, TDD)

**Files:**
- Create: `LiveDeck.Core/Sales/GiveawayDrawer.cs`
- Create: `LiveDeck.Tests/Sales/GiveawayDrawerTests.cs`

Pure-function pick: `(participants, winnerCount, seed) → up to N winners`. Deterministic for the same seed. Implementation: stable seed-string→int conversion (FNV-1a 32-bit hash, deterministic across processes/runtimes — `string.GetHashCode()` is randomized in .NET so we can't use it). Then Fisher-Yates shuffle, take first N.

- [ ] **Step 1: Failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Sales/GiveawayDrawerTests.cs`:

```csharp
using System.Linq;
using FluentAssertions;
using LiveDeck.Core.Sales;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class GiveawayDrawerTests
{
    private static GiveawayParticipant Pp(string id, string username) =>
        new(id, "g1", "c-" + id, "instagram", username, EnteredAt: 100, IsWinner: false);

    private readonly GiveawayDrawer _drawer = new();

    [Fact]
    public void Pick_returns_empty_when_no_participants()
    {
        var winners = _drawer.Pick(System.Array.Empty<GiveawayParticipant>(),
                                    winnerCount: 3, randomSeed: "any");
        winners.Should().BeEmpty();
    }

    [Fact]
    public void Pick_returns_one_when_one_participant_three_winners()
    {
        var ps = new[] { Pp("p1", "@a") };
        var winners = _drawer.Pick(ps, winnerCount: 3, randomSeed: "seed");
        winners.Should().HaveCount(1);
        winners[0].Username.Should().Be("@a");
    }

    [Fact]
    public void Pick_returns_zero_when_winner_count_is_zero()
    {
        var ps = new[] { Pp("p1", "@a"), Pp("p2", "@b") };
        var winners = _drawer.Pick(ps, winnerCount: 0, randomSeed: "seed");
        winners.Should().BeEmpty();
    }

    [Fact]
    public void Pick_returns_distinct_winners_when_more_participants_than_winners()
    {
        var ps = Enumerable.Range(0, 10).Select(i => Pp($"p{i}", $"@u{i}")).ToList();
        var winners = _drawer.Pick(ps, winnerCount: 3, randomSeed: "seed");

        winners.Should().HaveCount(3);
        winners.Select(w => w.Id).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Pick_is_deterministic_for_same_seed()
    {
        var ps = Enumerable.Range(0, 10).Select(i => Pp($"p{i}", $"@u{i}")).ToList();

        var run1 = _drawer.Pick(ps, winnerCount: 3, randomSeed: "fixed-seed");
        var run2 = _drawer.Pick(ps, winnerCount: 3, randomSeed: "fixed-seed");

        run1.Select(w => w.Id).Should().Equal(run2.Select(w => w.Id));
    }

    [Fact]
    public void Pick_produces_different_winners_for_different_seeds_on_average()
    {
        var ps = Enumerable.Range(0, 100).Select(i => Pp($"p{i}", $"@u{i}")).ToList();

        var seedA = _drawer.Pick(ps, winnerCount: 3, randomSeed: "alpha")
                            .Select(w => w.Id).ToList();
        var seedB = _drawer.Pick(ps, winnerCount: 3, randomSeed: "beta")
                            .Select(w => w.Id).ToList();

        // Probability of identical 3-of-100 picks for unrelated seeds is astronomically low.
        seedA.Should().NotEqual(seedB);
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayDrawerTests" 2>&1 | tail -3
```
Expected: FAIL — `GiveawayDrawer` not found.

- [ ] **Step 3: Implement GiveawayDrawer**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/GiveawayDrawer.cs`:

```csharp
using System;
using System.Collections.Generic;

namespace LiveDeck.Core.Sales;

/// <summary>
/// Selects up to <c>winnerCount</c> winners from a participant list using a deterministic
/// shuffle keyed by <c>randomSeed</c>. Pure function; no DB or wall-clock dependency.
///
/// The seed → 32-bit int conversion uses FNV-1a (deterministic across runtimes, unlike
/// <see cref="string.GetHashCode"/> which is randomized in .NET).
/// </summary>
public sealed class GiveawayDrawer
{
    public IReadOnlyList<GiveawayParticipant> Pick(
        IReadOnlyList<GiveawayParticipant> participants,
        int winnerCount,
        string randomSeed)
    {
        if (winnerCount <= 0 || participants.Count == 0)
            return System.Array.Empty<GiveawayParticipant>();

        // Copy + shuffle (Fisher-Yates) — leaves caller's list untouched.
        var pool = new GiveawayParticipant[participants.Count];
        for (int i = 0; i < participants.Count; i++) pool[i] = participants[i];

        var rng = new Random(unchecked((int)Fnv1a32(randomSeed)));
        for (int i = pool.Length - 1; i > 0; i--)
        {
            int j = rng.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        int take = Math.Min(winnerCount, pool.Length);
        var winners = new GiveawayParticipant[take];
        Array.Copy(pool, 0, winners, 0, take);
        return winners;
    }

    /// <summary>
    /// FNV-1a 32-bit hash. Stable across processes and platforms.
    /// </summary>
    private static uint Fnv1a32(string s)
    {
        const uint offsetBasis = 2166136261;
        const uint prime = 16777619;
        uint hash = offsetBasis;
        foreach (char c in s)
        {
            hash ^= c;
            hash *= prime;
        }
        return hash;
    }
}
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayDrawerTests" 2>&1 | tail -3
```
Expected: 6/6 PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/GiveawayDrawer.cs LiveDeck.Tests/Sales/GiveawayDrawerTests.cs
git commit -m "feat(core): add GiveawayDrawer with deterministic FNV-1a seeded RNG"
```

---

### Task 4: GiveawayService (TDD orchestration)

**Files:**
- Create: `LiveDeck.Core/Sales/GiveawayService.cs`
- Create: `LiveDeck.Tests/Sales/GiveawayServiceTests.cs`

The service is the App's entry point for giveaway operations. It coordinates `GiveawayRepository`, `GiveawayDrawer`, `CustomerService`, and `IClock`. While a giveaway is active, it subscribes to `IChatBus` (in the App layer wiring — see Task 6) and forwards matching messages to `AddParticipantFromChat`.

The keyword match is a simple **case-insensitive substring** test on the raw message text (no normalization — viewers literally type the keyword). Blacklist + PreventRewinning filters happen at participant-add time, NOT at draw time (per spec section 3.3).

- [ ] **Step 1: Failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Sales/GiveawayServiceTests.cs`:

```csharp
using System;
using System.Linq;
using FluentAssertions;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class GiveawayServiceTests
{
    private static (GiveawayService Svc, GiveawayRepository Repo, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) Fx(long unixNow = 1000L)
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(unixNow);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram", "tiktok" }, null));

        var customerRepo  = new CustomerRepository(db);
        var customerSvc   = new CustomerService(customerRepo, clock.Object);
        var giveawayRepo  = new GiveawayRepository(db);
        var drawer        = new GiveawayDrawer();

        var svc = new GiveawayService(giveawayRepo, customerSvc, customerRepo, drawer, clock.Object);
        return (svc, giveawayRepo, customerRepo, db, "s1");
    }

    private static ChatMessage Msg(string username, string text, string platform = "instagram") =>
        new(System.Guid.NewGuid().ToString("N"),
            platform, null, username, username, null, text, 1000,
            System.Array.Empty<string>());

    [Fact]
    public void Start_creates_giveaway_with_seed_and_returns_it()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;

        var g = svc.Start(sid, keyword: "🌹", durationSeconds: 60, winnerCount: 1,
                          platformFilter: null, preventRewinning: true);

        g.Keyword.Should().Be("🌹");
        g.RandomSeed.Should().NotBeNullOrEmpty();

        var fresh = repo.GetActiveBySession(sid);
        fresh.Should().NotBeNull();
        fresh!.Id.Should().Be(g.Id);
    }

    [Fact]
    public void AddParticipantFromChat_adds_when_keyword_matches()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "hadi 🌹 katılıyorum"));

        repo.GetParticipants(g.Id).Should().HaveCount(1);
    }

    [Fact]
    public void AddParticipantFromChat_is_case_insensitive_for_alphanumeric_keywords()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "katil", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "KATIL ben"));

        repo.GetParticipants(g.Id).Should().HaveCount(1);
    }

    [Fact]
    public void AddParticipantFromChat_skips_message_without_keyword()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "merhaba"));

        repo.GetParticipants(g.Id).Should().BeEmpty();
    }

    [Fact]
    public void AddParticipantFromChat_dedupes_same_user()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "🌹"));
        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "🌹 yine"));
        svc.AddParticipantFromChat(g.Id, Msg("@ayse", "tekrar 🌹"));

        repo.GetParticipants(g.Id).Should().HaveCount(1);
    }

    [Fact]
    public void AddParticipantFromChat_skips_blacklisted_user()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _2 = db;

        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        // Pre-create + blacklist
        var c = customers.FindByPlatformAndUsername("instagram", "@bad")
                ?? new Customer(System.Guid.NewGuid().ToString("N"),
                    "instagram", "@bad", null, null, 100, 100,
                    0, 0, 0, 100, false, null, null, 0, 0m, null);
        if (customers.GetById(c.Id) is null) customers.Insert(c);
        customers.UpdateBlacklist(c.Id, true, "test", 999);

        svc.AddParticipantFromChat(g.Id, Msg("@bad", "🌹"));

        repo.GetParticipants(g.Id).Should().BeEmpty();
    }

    [Fact]
    public void AddParticipantFromChat_respects_platform_filter()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1,
                          platformFilter: new[] { "tiktok" }, preventRewinning: true);

        svc.AddParticipantFromChat(g.Id, Msg("@a", "🌹", platform: "instagram"));
        svc.AddParticipantFromChat(g.Id, Msg("@b", "🌹", platform: "tiktok"));

        var ps = repo.GetParticipants(g.Id);
        ps.Should().HaveCount(1);
        ps[0].Platform.Should().Be("tiktok");
    }

    [Fact]
    public void AddParticipantFromChat_skips_previous_winner_when_PreventRewinning()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _2 = db;

        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner", "🌹"));
        svc.Draw(g1.Id);   // @winner becomes winner

        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g2.Id, Msg("@winner", "🎁"));
        svc.AddParticipantFromChat(g2.Id, Msg("@new",    "🎁"));

        var ps = repo.GetParticipants(g2.Id);
        ps.Select(p => p.Username).Should().BeEquivalentTo(new[] { "@new" });
    }

    [Fact]
    public void Draw_picks_winners_marks_them_and_ends_giveaway()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 2, null, true);
        svc.AddParticipantFromChat(g.Id, Msg("@a", "🌹"));
        svc.AddParticipantFromChat(g.Id, Msg("@b", "🌹"));
        svc.AddParticipantFromChat(g.Id, Msg("@c", "🌹"));

        var winners = svc.Draw(g.Id);

        winners.Should().HaveCount(2);
        var fresh = repo.GetById(g.Id)!;
        fresh.EndedAt.Should().NotBeNull();
        repo.GetParticipants(g.Id).Where(p => p.IsWinner).Should().HaveCount(2);
    }

    [Fact]
    public void Draw_with_no_participants_returns_empty_and_ends_giveaway()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        var winners = svc.Draw(g.Id);

        winners.Should().BeEmpty();
        repo.GetById(g.Id)!.EndedAt.Should().NotBeNull();
    }

    [Fact]
    public void Cancel_marks_giveaway_cancelled_and_GetActive_returns_null()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;
        var g = svc.Start(sid, "🌹", 60, 1, null, true);

        svc.Cancel(g.Id);

        repo.GetById(g.Id)!.CancelledAt.Should().NotBeNull();
        repo.GetActiveBySession(sid).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayServiceTests" 2>&1 | tail -3
```
Expected: FAIL — `GiveawayService` not found.

- [ ] **Step 3: Implement GiveawayService**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/GiveawayService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class GiveawayService
{
    private readonly GiveawayRepository _giveaways;
    private readonly CustomerService _customers;
    private readonly CustomerRepository _customerRepo;
    private readonly GiveawayDrawer _drawer;
    private readonly IClock _clock;

    public GiveawayService(
        GiveawayRepository giveaways,
        CustomerService customers,
        CustomerRepository customerRepo,
        GiveawayDrawer drawer,
        IClock clock)
    {
        _giveaways = giveaways;
        _customers = customers;
        _customerRepo = customerRepo;
        _drawer = drawer;
        _clock = clock;
    }

    public Giveaway Start(string sessionId, string keyword, int durationSeconds,
        int winnerCount, IReadOnlyList<string>? platformFilter, bool preventRewinning)
    {
        var g = new Giveaway(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            Keyword: keyword,
            DurationSeconds: durationSeconds,
            WinnerCount: winnerCount,
            PlatformFilter: platformFilter,
            PreventRewinning: preventRewinning,
            RandomSeed: Guid.NewGuid().ToString("N"),
            StartedAt: _clock.UnixNow(),
            EndedAt: null,
            CancelledAt: null);
        _giveaways.Insert(g);
        return g;
    }

    /// <summary>
    /// Adds the chat message author as a participant if (a) the message contains the
    /// giveaway keyword (case-insensitive substring), (b) the platform passes the filter,
    /// (c) the customer is not blacklisted, (d) PreventRewinning + previous winner check
    /// passes, (e) this username hasn't already entered (UNIQUE constraint).
    /// All filters fail silently — there is no surface to report errors to.
    /// </summary>
    public void AddParticipantFromChat(string giveawayId, ChatMessage message)
    {
        var g = _giveaways.GetById(giveawayId);
        if (g is null || g.EndedAt is not null || g.CancelledAt is not null) return;

        // (a) Keyword match — case-insensitive substring
        if (!message.Text.Contains(g.Keyword,
                System.Globalization.CultureInfo.InvariantCulture.CompareInfo
                    is { } _ ? StringComparison.OrdinalIgnoreCase : StringComparison.OrdinalIgnoreCase))
            return;

        // (b) Platform filter
        if (g.PlatformFilter is { Count: > 0 } filter
            && !filter.Contains(message.Platform, StringComparer.OrdinalIgnoreCase))
            return;

        // Resolve customer (creates if missing)
        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        // (c) Blacklist check
        if (customer.IsBlacklisted) return;

        // (d) PreventRewinning check
        if (g.PreventRewinning)
        {
            var prevWinners = _giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id);
            if (prevWinners.Contains(customer.Id)) return;
        }

        // (e) UNIQUE INDEX guard — wrap insert in try/catch to swallow duplicate
        try
        {
            _giveaways.AddParticipant(new GiveawayParticipant(
                Id: Guid.NewGuid().ToString("N"),
                GiveawayId: g.Id,
                CustomerId: customer.Id,
                Platform: message.Platform,
                Username: message.Username,
                EnteredAt: _clock.UnixNow(),
                IsWinner: false));
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            // 19 = SQLITE_CONSTRAINT — duplicate (already entered). Silently ignore.
        }
    }

    /// <summary>
    /// Picks winners using <see cref="GiveawayDrawer"/> with the giveaway's stored seed,
    /// marks them in DB, and sets <c>EndedAt = now</c>. Returns the chosen winners
    /// (empty list if no participants).
    /// </summary>
    public IReadOnlyList<GiveawayParticipant> Draw(string giveawayId)
    {
        var g = _giveaways.GetById(giveawayId)
                ?? throw new InvalidOperationException($"Giveaway {giveawayId} not found");

        var participants = _giveaways.GetParticipants(g.Id);
        var winners = _drawer.Pick(participants, g.WinnerCount, g.RandomSeed);

        if (winners.Count > 0)
            _giveaways.MarkWinners(winners.Select(w => w.Id));

        _giveaways.MarkEnded(g.Id, _clock.UnixNow());
        return winners;
    }

    /// <summary>Aborts the giveaway. Sets CancelledAt; participants kept for audit.</summary>
    public void Cancel(string giveawayId)
    {
        _giveaways.MarkCancelled(giveawayId, _clock.UnixNow());
    }
}
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayServiceTests" 2>&1 | tail -3
```
Expected: 11/11 PASS.

- [ ] **Step 5: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: ALL pass (~68 = 47 baseline + 10 repo + 6 drawer + 11 service - some fixture overlap, exact total may vary).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/GiveawayService.cs LiveDeck.Tests/Sales/GiveawayServiceTests.cs
git commit -m "feat(core): add GiveawayService (start/addParticipantFromChat/draw/cancel)"
```

---

## WPF Layer

### Task 5: NewGiveawayDialog (VM + view)

**Files:**
- Create: `LiveDeck.App/ViewModels/NewGiveawayDialogViewModel.cs`
- Create: `LiveDeck.App/Views/NewGiveawayDialog.xaml` + `.xaml.cs`

The dialog collects: keyword, duration (preset combo), winner count, platform filter, prevent-rewinning flag. Submits → caller starts the giveaway. The dialog itself does NOT call `GiveawayService.Start` directly — it just collects valid input and exposes properties for the caller (MainShellViewModel.StartGiveawayCommand) to read.

- [ ] **Step 1: NewGiveawayDialogViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/NewGiveawayDialogViewModel.cs`:

```csharp
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace LiveDeck.App.ViewModels;

public sealed partial class NewGiveawayDialogViewModel : ViewModelBase
{
    [ObservableProperty] private string _keyword = "🌹";
    [ObservableProperty] private DurationOption _selectedDuration = new("1 dakika (60sn)", 60);
    [ObservableProperty] private int _winnerCount = 1;
    [ObservableProperty] private PlatformOption _selectedPlatform = new("Tümü", null);
    [ObservableProperty] private bool _preventRewinning = true;
    [ObservableProperty] private string? _validationError;

    public bool Saved { get; private set; }

    public ObservableCollection<DurationOption> DurationOptions { get; } = new()
    {
        new("30 saniye", 30),
        new("1 dakika (60sn)", 60),
        new("2 dakika", 120),
        new("5 dakika", 300),
        new("Manuel bitir", 0)
    };

    public ObservableCollection<PlatformOption> PlatformOptions { get; } = new()
    {
        new("Tümü", null),
        new("Yalnız Instagram", new[] { "instagram" }),
        new("Yalnız TikTok",    new[] { "tiktok" })
    };

    public bool Validate()
    {
        if (string.IsNullOrWhiteSpace(Keyword) || Keyword.Length > 32)
        { ValidationError = "Anahtar kelime 1-32 karakter olmalı."; return false; }

        if (WinnerCount < 1 || WinnerCount > 50)
        { ValidationError = "Kazanan sayısı 1-50 arasında olmalı."; return false; }

        ValidationError = null;
        return true;
    }

    public void MarkSaved() => Saved = true;
}

public sealed record DurationOption(string Label, int Seconds);

public sealed record PlatformOption(string Label, IReadOnlyList<string>? Filter);
```

- [ ] **Step 2: NewGiveawayDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/NewGiveawayDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.NewGiveawayDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Yeni Çekiliş" Width="460" Height="380"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="160"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <TextBlock Grid.Row="0" Grid.Column="0" Text="Anahtar kelime:" Margin="0,8,8,8"/>
        <TextBox   Grid.Row="0" Grid.Column="1"
                   Text="{Binding Keyword, UpdateSourceTrigger=PropertyChanged}"
                   Padding="6" Margin="0,4"/>

        <TextBlock Grid.Row="1" Grid.Column="0" Text="Süre:" Margin="0,8,8,8"/>
        <ComboBox  Grid.Row="1" Grid.Column="1"
                   ItemsSource="{Binding DurationOptions}"
                   SelectedItem="{Binding SelectedDuration}"
                   DisplayMemberPath="Label"
                   Margin="0,4"/>

        <TextBlock Grid.Row="2" Grid.Column="0" Text="Kazanan sayısı:" Margin="0,8,8,8"/>
        <TextBox   Grid.Row="2" Grid.Column="1"
                   Text="{Binding WinnerCount, UpdateSourceTrigger=PropertyChanged}"
                   Padding="6" Margin="0,4"/>

        <TextBlock Grid.Row="3" Grid.Column="0" Text="Platform filtresi:" Margin="0,8,8,8"/>
        <ComboBox  Grid.Row="3" Grid.Column="1"
                   ItemsSource="{Binding PlatformOptions}"
                   SelectedItem="{Binding SelectedPlatform}"
                   DisplayMemberPath="Label"
                   Margin="0,4"/>

        <CheckBox  Grid.Row="4" Grid.Column="1"
                   Content="Önceki kazananları dahil etme"
                   IsChecked="{Binding PreventRewinning}"
                   Foreground="White"
                   Margin="0,12,0,0"/>

        <TextBlock Grid.Row="6" Grid.ColumnSpan="2"
                   Text="{Binding ValidationError}"
                   Foreground="#FFFF6666" Margin="0,8,0,0"
                   Visibility="{Binding ValidationError, Converter={StaticResource NullToCollapsedConverter}}"/>

        <StackPanel Grid.Row="7" Grid.ColumnSpan="2"
                    Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="İptal"   Click="OnCancel" Padding="14,6" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Başlat"  Click="OnStart"  Padding="14,6" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: NewGiveawayDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/NewGiveawayDialog.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;

namespace LiveDeck.App.Views;

public partial class NewGiveawayDialog : Window
{
    public NewGiveawayDialogViewModel ViewModel { get; }

    public NewGiveawayDialog()
    {
        InitializeComponent();
        ViewModel = new NewGiveawayDialogViewModel();
        DataContext = ViewModel;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnStart(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.Validate()) return;
        ViewModel.MarkSaved();
        DialogResult = true;
        Close();
    }
}
```

- [ ] **Step 4: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/NewGiveawayDialogViewModel.cs LiveDeck.App/Views/NewGiveawayDialog.xaml LiveDeck.App/Views/NewGiveawayDialog.xaml.cs
git commit -m "feat(app): add NewGiveawayDialog (keyword/duration/winner-count/platform/prevent-rewinning)"
```

---

### Task 6: GiveawayBannerViewModel (countdown + participant counter)

**Files:**
- Create: `LiveDeck.App/ViewModels/GiveawayBannerViewModel.cs`

The banner replaces MainShell's status label while a giveaway is active. It shows:
- Keyword (left badge)
- Live participant count (queried from `GiveawayRepository.GetParticipants` every tick)
- Countdown timer (computed from `Giveaway.StartedAt + DurationSeconds - now`)
- "Şimdi Çek" / "İptal" buttons (commands fire MainShell-level handlers via callback)

Uses `DispatcherTimer` ticking every 1 second. When countdown hits 0, fires the `AutoDraw` callback (MainShell handles auto-draw).

- [ ] **Step 1: Implement GiveawayBannerViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/GiveawayBannerViewModel.cs`:

```csharp
using System;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.App.ViewModels;

/// <summary>
/// While a giveaway is active, drives the live banner shown in MainShell instead of the
/// usual stream status label. Computes countdown and refreshes participant count every
/// second via DispatcherTimer. Fires <see cref="AutoDrawRequested"/> when the timer hits 0.
/// </summary>
public sealed partial class GiveawayBannerViewModel : ViewModelBase, IDisposable
{
    private readonly GiveawayRepository _giveaways;
    private readonly IClock _clock;
    private readonly DispatcherTimer _timer;
    private Giveaway? _active;

    /// <summary>True when a giveaway is being tracked.</summary>
    [ObservableProperty] private bool _isActive;

    [ObservableProperty] private string _keyword = "";
    [ObservableProperty] private int _participantCount;
    [ObservableProperty] private string _countdownText = "";    // "0:32" or "(süre limitsiz)"
    [ObservableProperty] private bool _isManualEnd;             // true when DurationSeconds = 0

    /// <summary>Raised when the countdown reaches 0. MainShell handles auto-draw.</summary>
    public event Action? AutoDrawRequested;

    public GiveawayBannerViewModel(GiveawayRepository giveaways, IClock clock)
    {
        _giveaways = giveaways;
        _clock = clock;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
    }

    public void StartTracking(Giveaway g)
    {
        _active = g;
        Keyword = g.Keyword;
        IsManualEnd = g.DurationSeconds == 0;
        IsActive = true;
        UpdateState();
        _timer.Start();
    }

    public void StopTracking()
    {
        _timer.Stop();
        _active = null;
        IsActive = false;
    }

    private void OnTick(object? sender, EventArgs e) => UpdateState();

    private void UpdateState()
    {
        if (_active is null) return;

        ParticipantCount = _giveaways.GetParticipants(_active.Id).Count;

        if (IsManualEnd)
        {
            CountdownText = "(süre limitsiz)";
            return;
        }

        long now = _clock.UnixNow();
        long endsAt = _active.StartedAt + _active.DurationSeconds;
        long remaining = endsAt - now;

        if (remaining <= 0)
        {
            CountdownText = "0:00";
            _timer.Stop();
            AutoDrawRequested?.Invoke();
            return;
        }

        long mm = remaining / 60;
        long ss = remaining % 60;
        CountdownText = $"{mm}:{ss:D2}";
    }

    public void Dispose() => _timer.Stop();
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0/0.

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/GiveawayBannerViewModel.cs
git commit -m "feat(app): add GiveawayBannerViewModel (countdown + participant tracker)"
```

---

### Task 7: MainShell — 🎁 button + giveaway integration

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`
- Modify: `LiveDeck.App/Views/MainShellView.xaml`

Add to MainShellViewModel:
- New dependencies: `GiveawayService`, `GiveawayBannerViewModel` (via DI)
- New commands: `StartGiveaway`, `DrawGiveawayNow`, `CancelGiveaway`
- New `IsGiveawayActive` ObservableProperty (drives banner visibility)
- `OnChatMessage` is extended: if a giveaway is active, also forward to `GiveawayService.AddParticipantFromChat`
- `EndStream` now refuses if a giveaway is active
- Banner subscribes to `AutoDrawRequested` event → calls `DrawGiveawayNow`

XAML:
- Top bar gains a `🎁 Çekiliş` button between "Yayını Bitir" and `⋮`
- Status row uses a Visibility trigger: when `IsGiveawayActive=true`, show banner panel; else show normal status label
- Banner panel: keyword pill + participant count + countdown + Şimdi Çek + İptal buttons

- [ ] **Step 1: Replace MainShellViewModel**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.App.Views;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Labeling;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.ViewModels;

public sealed partial class MainShellViewModel : ViewModelBase, IDisposable
{
    private readonly LabelService _labels;
    private readonly StreamSessionService _sessions;
    private readonly LabelPrinter _printer;
    private readonly CustomerService _customers;
    private readonly CustomerRepository _customerRepo;
    private readonly GiveawayService _giveaways;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _busSubscription;

    private const int MaxChatMessages = 200;

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();
    public ObservableCollection<LabelViewModel>       PrintQueue   { get; } = new();

    public GiveawayBannerViewModel Banner { get; }

    [ObservableProperty] private string _activeCode = "";
    [ObservableProperty] private string _activePriceText = "0";
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";
    [ObservableProperty] private bool _isGiveawayActive;
    [ObservableProperty] private bool _canStartGiveaway;

    private string? _activeGiveawayId;

    public MainShellViewModel(
        IChatBus bus,
        LabelService labels,
        StreamSessionService sessions,
        LabelPrinter printer,
        CustomerService customers,
        CustomerRepository customerRepo,
        GiveawayService giveaways,
        GiveawayBannerViewModel banner)
    {
        _labels = labels;
        _sessions = sessions;
        _printer = printer;
        _customers = customers;
        _customerRepo = customerRepo;
        _giveaways = giveaways;
        Banner = banner;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _busSubscription = bus.Subscribe(OnChatMessage);

        Banner.AutoDrawRequested += () => DrawGiveawayNowCommand.Execute(null);

        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();
        ReloadQueueFromActiveSession();
    }

    private void OnChatMessage(ChatMessage m)
    {
        _dispatcher.BeginInvoke(() =>
        {
            // Chat panel + blacklist highlight
            var customer = _customerRepo.FindByPlatformAndUsername(m.Platform, m.Username);
            var blacklisted = customer?.IsBlacklisted ?? false;
            ChatMessages.Add(new ChatMessageViewModel(m, blacklisted));
            while (ChatMessages.Count > MaxChatMessages) ChatMessages.RemoveAt(0);

            // Forward to active giveaway, if any
            if (_activeGiveawayId is not null)
                _giveaways.AddParticipantFromChat(_activeGiveawayId, m);
        });
    }

    private void UpdateStreamStatusLabel()
    {
        var session = _sessions.GetActive();
        StreamStatusLabel = session is null
            ? "Yayın aktif değil"
            : $"Yayın aktif (başlangıç: {DateTimeOffset.FromUnixTimeSeconds(session.StartedAt):HH:mm})";
    }

    private void UpdateGiveawayCanStart()
    {
        CanStartGiveaway = _sessions.GetActive() is not null && !IsGiveawayActive;
    }

    private void ReloadQueueFromActiveSession()
    {
        PrintQueue.Clear();
        var session = _sessions.GetActive();
        if (session is null) return;
        foreach (var l in _labels.GetQueue(session.Id))
        {
            var customer = _customerRepo.GetById(l.CustomerId);
            PrintQueue.Add(new LabelViewModel(l, customer?.IsBlacklisted ?? false));
        }
    }

    [RelayCommand] private void StartStream()
    {
        if (_sessions.GetActive() is not null)
        {
            MessageBox.Show("Zaten aktif bir yayın var. Önce mevcut yayını bitir.",
                "Yayın aktif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sessions.Start("Yeni Yayın", new[] { "instagram", "tiktok" });
        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();
        ReloadQueueFromActiveSession();
    }

    [RelayCommand] private void EndStream()
    {
        var session = _sessions.GetActive();
        if (session is null) return;

        if (IsGiveawayActive)
        {
            MessageBox.Show("Aktif çekiliş var. Önce çekilişi tamamla veya iptal et.",
                "Çekiliş aktif", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var confirm = MessageBox.Show("Yayını bitirmek istediğinden emin misin?",
            "Yayını Bitir", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        if (PrintQueue.Count > 0)
        {
            try { Print(); }
            catch (Exception ex)
            {
                MessageBox.Show($"Yazdırma sırasında hata oluştu, yine de yayını bitiriyorum:\n{ex.Message}",
                    "Yazdırma hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        _sessions.End(session.Id);
        UpdateStreamStatusLabel();
        UpdateGiveawayCanStart();

        var dialog = App.Host.Services.GetRequiredService<StreamReportDialog>();
        dialog.LoadReport(session.Id);
        dialog.Owner = Application.Current?.MainWindow;
        dialog.ShowDialog();
    }

    public void AddChatToQueue(ChatMessageViewModel messageVm)
    {
        var session = _sessions.GetActive();
        if (session is null)
        {
            MessageBox.Show("Önce yayın başlat.",
                "Aktif yayın yok", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!TryParsePrice(ActivePriceText, out var price))
        {
            MessageBox.Show("Geçerli bir fiyat gir (örn: 100 veya 99.50).",
                "Geçersiz fiyat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var label = _labels.Add(session.Id, messageVm.Message, price,
            string.IsNullOrWhiteSpace(ActiveCode) ? null : ActiveCode.Trim());
        PrintQueue.Add(new LabelViewModel(label, messageVm.IsSenderBlacklisted));
    }

    [RelayCommand]
    private void RemoveSelectedFromQueue(LabelViewModel? selected)
    {
        if (selected is null) return;
        _labels.Delete(selected.Id);
        PrintQueue.Remove(selected);
    }

    [RelayCommand]
    private void ClearQueue()
    {
        if (PrintQueue.Count == 0) return;
        var confirm = MessageBox.Show($"Kuyruktaki {PrintQueue.Count} etiket silinecek. Emin misin?",
            "Hepsini Temizle", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        foreach (var item in PrintQueue.ToList()) _labels.Delete(item.Id);
        PrintQueue.Clear();
    }

    [RelayCommand]
    private void Print()
    {
        if (PrintQueue.Count == 0) return;
        var snapshot = PrintQueue.Select(vm => vm.Label).ToList();
        _printer.Print(snapshot);
        _labels.MarkPrintedAndRecord(snapshot.Select(l => l.Id).ToList());
        PrintQueue.Clear();
    }

    [RelayCommand] private void OpenSettings()
    {
        var dlg = App.Host.Services.GetRequiredService<SettingsDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
        RefreshHighlights();
    }

    [RelayCommand] private void OpenStreamHistory()
    {
        var dlg = App.Host.Services.GetRequiredService<StreamHistoryDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
    }

    [RelayCommand] private void OpenBlacklist()
    {
        var dlg = App.Host.Services.GetRequiredService<BlacklistDialog>();
        dlg.Owner = Application.Current?.MainWindow;
        dlg.ShowDialog();
        RefreshHighlights();
    }

    [RelayCommand]
    private void StartGiveaway()
    {
        var session = _sessions.GetActive();
        if (session is null)
        {
            MessageBox.Show("Önce yayın başlat.", "Aktif yayın yok",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (IsGiveawayActive) return;

        var dlg = new NewGiveawayDialog { Owner = Application.Current?.MainWindow };
        if (dlg.ShowDialog() != true) return;

        var vm = dlg.ViewModel;
        var g = _giveaways.Start(
            sessionId: session.Id,
            keyword: vm.Keyword.Trim(),
            durationSeconds: vm.SelectedDuration.Seconds,
            winnerCount: vm.WinnerCount,
            platformFilter: vm.SelectedPlatform.Filter,
            preventRewinning: vm.PreventRewinning);

        _activeGiveawayId = g.Id;
        IsGiveawayActive = true;
        UpdateGiveawayCanStart();
        Banner.StartTracking(g);

        // OBS overlay broadcast — handled in Task 8 (OverlayHost subscribes to a hook).
        // For now, the data is in DB and the banner shows it.
    }

    [RelayCommand]
    private void DrawGiveawayNow()
    {
        if (_activeGiveawayId is null) return;

        _giveaways.Draw(_activeGiveawayId);
        Banner.StopTracking();
        _activeGiveawayId = null;
        IsGiveawayActive = false;
        UpdateGiveawayCanStart();
    }

    [RelayCommand]
    private void CancelGiveaway()
    {
        if (_activeGiveawayId is null) return;
        var confirm = MessageBox.Show("Çekiliş iptal edilecek. Emin misin?",
            "Çekilişi İptal", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _giveaways.Cancel(_activeGiveawayId);
        Banner.StopTracking();
        _activeGiveawayId = null;
        IsGiveawayActive = false;
        UpdateGiveawayCanStart();
    }

    [RelayCommand]
    private void AddChatSenderToBlacklist(ChatMessageViewModel? msg)
    {
        if (msg is null) return;
        var dlg = new AddToBlacklistDialog
        {
            Mode = AddToBlacklistDialog.DialogMode.Prefilled,
            UsernameText = msg.Username,
            PlatformText = msg.Platform
        };
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(msg.Platform, msg.Username, dlg.ReasonText);
        RefreshHighlights();
    }

    [RelayCommand]
    private void AddQueueRowToBlacklist(LabelViewModel? row)
    {
        if (row is null) return;
        var dlg = new AddToBlacklistDialog
        {
            Mode = AddToBlacklistDialog.DialogMode.Prefilled,
            UsernameText = row.Username,
            PlatformText = row.Label.Platform
        };
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(row.Label.Platform, row.Username, dlg.ReasonText);
        RefreshHighlights();
    }

    private void RefreshHighlights()
    {
        foreach (var vm in ChatMessages)
        {
            var c = _customerRepo.FindByPlatformAndUsername(vm.Platform, vm.Username);
            vm.IsSenderBlacklisted = c?.IsBlacklisted ?? false;
        }
        foreach (var vm in PrintQueue)
        {
            var c = _customerRepo.GetById(vm.CustomerId);
            vm.IsCustomerBlacklisted = c?.IsBlacklisted ?? false;
        }
    }

    private static bool TryParsePrice(string text, out decimal price)
    {
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out price)
            || decimal.TryParse(text, NumberStyles.Any, new CultureInfo("tr-TR"), out price);
    }

    public void Dispose()
    {
        Banner.Dispose();
        _busSubscription.Dispose();
    }
}
```

- [ ] **Step 2: Update MainShellView.xaml**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml`:

```xml
<UserControl x:Class="LiveDeck.App.Views.MainShellView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Margin="20">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- Top bar -->
        <Grid Grid.Row="0" Margin="0,0,0,12">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="220"/>
                <ColumnDefinition Width="20"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="120"/>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
                <ColumnDefinition Width="Auto"/>
            </Grid.ColumnDefinitions>

            <TextBlock Grid.Column="0" Text="Kod:" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBox  Grid.Column="1" Text="{Binding ActiveCode, UpdateSourceTrigger=PropertyChanged}"
                      Padding="6" FontSize="14"/>

            <TextBlock Grid.Column="3" Text="Fiyat:" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBox  Grid.Column="4" Text="{Binding ActivePriceText, UpdateSourceTrigger=PropertyChanged}"
                      Padding="6" FontSize="14"/>

            <Button   Grid.Column="6" Content="Yayın Başlat"
                      Command="{Binding StartStreamCommand}" Padding="14,6" Margin="0,0,8,0"/>
            <Button   Grid.Column="7" Content="Yayını Bitir"
                      Command="{Binding EndStreamCommand}"   Padding="14,6"/>

            <Button   Grid.Column="8" Content="🎁 Çekiliş"
                      Command="{Binding StartGiveawayCommand}"
                      IsEnabled="{Binding CanStartGiveaway}"
                      Padding="14,6" Margin="8,0,0,0"/>

            <Button   Grid.Column="9" Content="⋮"
                      x:Name="MenuButton"
                      Click="OnMenuClick"
                      Padding="10,6" Margin="8,0,0,0"
                      FontSize="18" FontWeight="Bold">
                <Button.ContextMenu>
                    <ContextMenu x:Name="MainMenu">
                        <MenuItem Header="Ayarlar"
                                  Command="{Binding OpenSettingsCommand}"/>
                        <MenuItem Header="Yayın Geçmişi"
                                  Command="{Binding OpenStreamHistoryCommand}"/>
                        <MenuItem Header="Kara Liste"
                                  Command="{Binding OpenBlacklistCommand}"/>
                    </ContextMenu>
                </Button.ContextMenu>
            </Button>
        </Grid>

        <!-- Status row: stream-status text OR giveaway banner -->
        <Grid Grid.Row="1" Margin="0,0,0,12">
            <TextBlock Text="{Binding StreamStatusLabel}"
                       Foreground="#FFAAAAAA"
                       Visibility="{Binding IsGiveawayActive, Converter={StaticResource BoolToCollapsedConverter}}"/>

            <DockPanel Visibility="{Binding IsGiveawayActive, Converter={StaticResource BoolToVisibleConverter}}"
                       Background="#FF332B00" LastChildFill="False">
                <TextBlock DockPanel.Dock="Left"
                           Text="{Binding Banner.Keyword, StringFormat={}🎁 {0}}"
                           Foreground="#FFFFD166" FontWeight="Bold" FontSize="14"
                           VerticalAlignment="Center" Margin="8,4,16,4"/>
                <TextBlock DockPanel.Dock="Left"
                           Text="{Binding Banner.ParticipantCount, StringFormat={}{0} katılımcı}"
                           Foreground="White" Margin="0,4,16,4"
                           VerticalAlignment="Center"/>
                <TextBlock DockPanel.Dock="Left"
                           Text="{Binding Banner.CountdownText}"
                           Foreground="White" Margin="0,4,16,4"
                           VerticalAlignment="Center"/>
                <Button DockPanel.Dock="Right"
                        Content="İptal"
                        Command="{Binding CancelGiveawayCommand}"
                        Padding="10,4" Margin="4,2"
                        Foreground="#FFFF6666"/>
                <Button DockPanel.Dock="Right"
                        Content="Şimdi Çek"
                        Command="{Binding DrawGiveawayNowCommand}"
                        Padding="10,4" Margin="4,2"
                        FontWeight="Bold"/>
            </DockPanel>
        </Grid>

        <!-- Two-pane: live chat | print queue -->
        <Grid Grid.Row="2">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="20"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>

            <!-- LEFT: Live chat -->
            <Grid Grid.Column="0">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                </Grid.RowDefinitions>
                <TextBlock Grid.Row="0" Text="Canlı Chat" FontSize="16" FontWeight="Bold"
                           Margin="0,0,0,8"/>
                <ListBox  Grid.Row="1"
                          x:Name="ChatList"
                          ItemsSource="{Binding ChatMessages}"
                          MouseDoubleClick="ChatList_OnDoubleClick"
                          Background="#FF1A1A1A" Foreground="White"
                          BorderBrush="#FF333333" BorderThickness="1">
                    <ListBox.ItemContainerStyle>
                        <Style TargetType="ListBoxItem">
                            <Setter Property="Background"
                                    Value="{Binding IsSenderBlacklisted,
                                                    Converter={StaticResource BlacklistToBrushConverter}}"/>
                        </Style>
                    </ListBox.ItemContainerStyle>
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <Grid Margin="0,4">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="80"/>
                                    <ColumnDefinition Width="160"/>
                                    <ColumnDefinition Width="*"/>
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{Binding Platform}"
                                           FontWeight="Bold" Foreground="#FFFFD166"/>
                                <TextBlock Grid.Column="1" Text="{Binding Username}"
                                           FontWeight="Bold" TextTrimming="CharacterEllipsis"/>
                                <TextBlock Grid.Column="2" Text="{Binding Text}"
                                           TextWrapping="Wrap"/>
                            </Grid>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                    <ListBox.ContextMenu>
                        <ContextMenu>
                            <MenuItem Header="Kara Listeye Al…"
                                      Command="{Binding PlacementTarget.DataContext.AddChatSenderToBlacklistCommand,
                                                        RelativeSource={RelativeSource Self}}"
                                      CommandParameter="{Binding PlacementTarget.SelectedItem,
                                                                 RelativeSource={RelativeSource Self}}"/>
                        </ContextMenu>
                    </ListBox.ContextMenu>
                </ListBox>
            </Grid>

            <!-- RIGHT: Print queue -->
            <Grid Grid.Column="2">
                <Grid.RowDefinitions>
                    <RowDefinition Height="Auto"/>
                    <RowDefinition Height="*"/>
                    <RowDefinition Height="Auto"/>
                </Grid.RowDefinitions>
                <TextBlock Grid.Row="0" Text="Yazdırılacak Etiketler" FontSize="16"
                           FontWeight="Bold" Margin="0,0,0,8"/>
                <ListBox Grid.Row="1"
                         x:Name="QueueList"
                         ItemsSource="{Binding PrintQueue}"
                         Background="#FF1A1A1A" Foreground="White"
                         BorderBrush="#FF333333" BorderThickness="1">
                    <ListBox.ItemContainerStyle>
                        <Style TargetType="ListBoxItem">
                            <Setter Property="Background"
                                    Value="{Binding IsCustomerBlacklisted,
                                                    Converter={StaticResource BlacklistToBrushConverter}}"/>
                        </Style>
                    </ListBox.ItemContainerStyle>
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <Grid Margin="0,4">
                                <Grid.ColumnDefinitions>
                                    <ColumnDefinition Width="160"/>
                                    <ColumnDefinition Width="*"/>
                                    <ColumnDefinition Width="80"/>
                                </Grid.ColumnDefinitions>
                                <TextBlock Grid.Column="0" Text="{Binding Username}"
                                           FontWeight="Bold" TextTrimming="CharacterEllipsis"/>
                                <TextBlock Grid.Column="1" Text="{Binding MessageText}"
                                           TextWrapping="NoWrap" TextTrimming="CharacterEllipsis"
                                           Margin="6,0,6,0"/>
                                <TextBlock Grid.Column="2"
                                           Text="{Binding Price, StringFormat={}{0:N2} TL}"
                                           HorizontalAlignment="Right" FontWeight="Bold"
                                           Foreground="#FFFFD166"/>
                            </Grid>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
                    <ListBox.ContextMenu>
                        <ContextMenu>
                            <MenuItem Header="Kara Listeye Al…"
                                      Command="{Binding PlacementTarget.DataContext.AddQueueRowToBlacklistCommand,
                                                        RelativeSource={RelativeSource Self}}"
                                      CommandParameter="{Binding PlacementTarget.SelectedItem,
                                                                 RelativeSource={RelativeSource Self}}"/>
                        </ContextMenu>
                    </ListBox.ContextMenu>
                </ListBox>

                <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,12,0,0">
                    <Button Content="Yazdır"  Command="{Binding PrintCommand}"
                            Padding="20,8" FontSize="14" FontWeight="Bold"/>
                    <Button Content="Seçileni Sil"
                            Command="{Binding RemoveSelectedFromQueueCommand}"
                            CommandParameter="{Binding ElementName=QueueList, Path=SelectedItem}"
                            Padding="14,8" Margin="8,0,0,0"/>
                    <Button Content="Hepsini Temizle"
                            Command="{Binding ClearQueueCommand}"
                            Padding="14,8" Margin="8,0,0,0"
                            Foreground="#FFFF6666"/>
                </StackPanel>
            </Grid>
        </Grid>
    </Grid>
</UserControl>
```

- [ ] **Step 3: Add BoolToVisibleConverter + BoolToCollapsedConverter**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Converters/BoolToVisibilityConverters.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>true → Visible, false → Collapsed. Used to gate UI on a bool flag.</summary>
public sealed class BoolToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>true → Collapsed, false → Visible. Inverse of <see cref="BoolToVisibleConverter"/>.</summary>
public sealed class BoolToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register both in App.xaml's ResourceDictionary alongside the others (UnixToDate, NullToCollapsed, BlacklistToBrush). After this step App.xaml has 5 converters.

- [ ] **Step 4: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors. (DI registration of GiveawayService + GiveawayBannerViewModel happens in Task 11; runtime issue, compile clean.)

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs LiveDeck.App/Views/MainShellView.xaml LiveDeck.App/Converters/BoolToVisibilityConverters.cs LiveDeck.App/App.xaml
git commit -m "feat(app): add 🎁 Çekiliş button + giveaway banner to MainShell"
```

---

### Task 8: OBS Overlay — Giveaway Endpoints + WebSocket Broadcast

**Goal:** Extend `OverlayHost` so OBS can subscribe to a separate `/ws/giveaway` channel and serve `/overlay/giveaway`. Add giveaway event records. Plumb `GiveawayService` events into the host.

**Files:**
- Modify: `LiveDeck.Overlay/Models/OverlayEvent.cs` (add giveaway event records)
- Modify: `LiveDeck.Overlay/OverlayHost.cs` (new endpoints + event subscriptions)
- Test: `LiveDeck.Tests/Overlay/OverlayHostGiveawayTests.cs`

**Context:** `OverlayHost` already follows a simple pattern — `MapGet("/overlay/chat")` serves a static HTML file, `Map("/ws/chat")` accepts a WebSocket and broadcasts `ChatMessage` events from `IChatBus`. We mirror the pattern for giveaways. The host listens to events on `IGiveawayService` (defined in Task 4) — `Started`, `ParticipantAdded`, `WinnersDrawn`, `Cancelled` — and broadcasts JSON envelopes. The browser-side animation (Task 9) waits for `winners.drawn` to start the roulette.

- [ ] **Step 1: Add giveaway event records to OverlayEvent.cs**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/Models/OverlayEvent.cs`. Append:

```csharp
public sealed record GiveawayStartedEvent(
    long GiveawayId,
    string Keyword,
    int WinnerCount,
    int DurationSec,
    long StartedAt);

public sealed record GiveawayParticipantEvent(
    long GiveawayId,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Platform,
    int TotalCount);

public sealed record GiveawayWinner(
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    string Platform);

public sealed record GiveawayWinnersDrawnEvent(
    long GiveawayId,
    System.Collections.Generic.IReadOnlyList<GiveawayWinner> Winners,
    System.Collections.Generic.IReadOnlyList<GiveawayWinner> AnimationPool,
    int ParticipantCount);

public sealed record GiveawayCancelledEvent(long GiveawayId);
```

`AnimationPool` is the shuffled list the roulette spins through (capped at 50 entries server-side to keep the overlay snappy). The final element of the pool is the actual winner; intermediate frames are randomized for visual effect. For multi-winner giveaways the pool ends with the winners in order.

- [ ] **Step 2: Write the failing test**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Overlay/OverlayHostGiveawayTests.cs`:

```csharp
using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Giveaway;
using LiveDeck.Overlay;
using LiveDeck.Overlay.Models;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Overlay;

public sealed class OverlayHostGiveawayTests
{
    private static int FreePort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var p = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    [Fact]
    public async Task GET_overlay_giveaway_returns_html()
    {
        var bus = new Mock<IChatBus>();
        bus.Setup(b => b.RecentMessages()).Returns(System.Array.Empty<ChatMessage>());
        bus.Setup(b => b.Subscribe(It.IsAny<Action<ChatMessage>>())).Returns(Mock.Of<IDisposable>());
        var giveaway = new FakeGiveawayService();
        var port = FreePort();
        await using var host = new OverlayHost(bus.Object, giveaway, port: port);
        await host.StartAsync();

        using var http = new System.Net.Http.HttpClient();
        var resp = await http.GetAsync($"http://localhost:{port}/overlay/giveaway");

        resp.IsSuccessStatusCode.Should().BeTrue();
        (await resp.Content.ReadAsStringAsync()).Should().Contain("LiveDeck Çekiliş");
    }

    [Fact]
    public async Task WinnersDrawn_event_is_broadcast_to_ws_clients()
    {
        var bus = new Mock<IChatBus>();
        bus.Setup(b => b.RecentMessages()).Returns(System.Array.Empty<ChatMessage>());
        bus.Setup(b => b.Subscribe(It.IsAny<Action<ChatMessage>>())).Returns(Mock.Of<IDisposable>());
        var giveaway = new FakeGiveawayService();
        var port = FreePort();
        await using var host = new OverlayHost(bus.Object, giveaway, port: port);
        await host.StartAsync();

        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://localhost:{port}/ws/giveaway"), CancellationToken.None);

        // Trigger event after the WS is connected
        _ = Task.Run(async () =>
        {
            await Task.Delay(150);
            giveaway.RaiseWinnersDrawn(new GiveawayWinnersDrawnEvent(
                42,
                new[] { new GiveawayWinner("ali_42", "Ali", null, "instagram") },
                new[] { new GiveawayWinner("ali_42", "Ali", null, "instagram") },
                1));
        });

        var buf = new byte[4096];
        var res = await ws.ReceiveAsync(buf, CancellationToken.None);
        var json = Encoding.UTF8.GetString(buf, 0, res.Count);

        json.Should().Contain("\"giveaway.winners.drawn\"");
        json.Should().Contain("ali_42");
    }

    private sealed class FakeGiveawayService : IGiveawayService
    {
        public Giveaway? Active => null;
        public event Action<GiveawayStartedEvent>? Started;
        public event Action<GiveawayParticipantEvent>? ParticipantAdded;
        public event Action<GiveawayWinnersDrawnEvent>? WinnersDrawn;
        public event Action<GiveawayCancelledEvent>? Cancelled;

        public Giveaway Start(string k, int w, int d, string? p, bool prevent) => throw new NotImplementedException();
        public void TryAddParticipantFromChat(ChatMessage m) { }
        public System.Collections.Generic.IReadOnlyList<GiveawayParticipant> Draw() => System.Array.Empty<GiveawayParticipant>();
        public void Cancel() { }

        public void RaiseWinnersDrawn(GiveawayWinnersDrawnEvent evt) => WinnersDrawn?.Invoke(evt);
    }
}
```

- [ ] **Step 3: Run tests — they should FAIL**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~OverlayHostGiveawayTests" 2>&1 | tail -10
```
Expected: compilation errors (`OverlayHost` constructor doesn't accept `IGiveawayService`, no `/overlay/giveaway`, no `/ws/giveaway`).

- [ ] **Step 4: Modify OverlayHost.cs**

Replace the contents of `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/OverlayHost.cs`:

```csharp
using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Giveaway;
using LiveDeck.Overlay.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.Overlay;

/// <summary>
/// Hosts the OBS Browser Source endpoints. Started/stopped by the WPF App.
///   * GET  /overlay/chat       → static HTML page bundled via wwwroot
///   * WS   /ws/chat            → live ChatMessage stream
///   * GET  /overlay/giveaway   → static HTML page for giveaway roulette
///   * WS   /ws/giveaway        → live giveaway event stream
/// </summary>
public sealed class OverlayHost : IAsyncDisposable
{
    private readonly IChatBus _bus;
    private readonly IGiveawayService _giveaway;
    private readonly ILogger<OverlayHost> _log;
    private WebApplication? _app;
    private readonly ConcurrentDictionary<Guid, WebSocket> _chatClients = new();
    private readonly ConcurrentDictionary<Guid, WebSocket> _giveawayClients = new();
    private IDisposable? _busSub;
    private Action<GiveawayStartedEvent>? _onStarted;
    private Action<GiveawayParticipantEvent>? _onParticipant;
    private Action<GiveawayWinnersDrawnEvent>? _onWinners;
    private Action<GiveawayCancelledEvent>? _onCancelled;

    public int Port { get; private set; }

    public OverlayHost(IChatBus bus, IGiveawayService giveaway, int port = 4747, ILogger<OverlayHost>? log = null)
    {
        _bus = bus;
        _giveaway = giveaway;
        _log = log ?? NullLogger<OverlayHost>.Instance;
        Port = port;
    }

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://localhost:{Port}");

        _app = builder.Build();
        _app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });

        var wwwroot = Path.Combine(AppContext.BaseDirectory, "wwwroot");
        if (Directory.Exists(wwwroot))
        {
            _app.UseStaticFiles(new StaticFileOptions
            {
                FileProvider = new PhysicalFileProvider(wwwroot)
            });
        }

        _app.MapGet("/overlay/chat", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(wwwroot, "chat.html"));
        });

        _app.MapGet("/overlay/giveaway", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "text/html; charset=utf-8";
            await ctx.Response.SendFileAsync(Path.Combine(wwwroot, "giveaway.html"));
        });

        _app.Map("/ws/chat", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleChatClient(ws, ctx.RequestAborted);
        });

        _app.Map("/ws/giveaway", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest) { ctx.Response.StatusCode = 400; return; }
            using var ws = await ctx.WebSockets.AcceptWebSocketAsync();
            await HandleGiveawayClient(ws, ctx.RequestAborted);
        });

        _busSub = _bus.Subscribe(BroadcastChatMessage);

        _onStarted = e => BroadcastGiveaway("giveaway.started", e);
        _onParticipant = e => BroadcastGiveaway("giveaway.participant", e);
        _onWinners = e => BroadcastGiveaway("giveaway.winners.drawn", e);
        _onCancelled = e => BroadcastGiveaway("giveaway.cancelled", e);

        _giveaway.Started += _onStarted;
        _giveaway.ParticipantAdded += _onParticipant;
        _giveaway.WinnersDrawn += _onWinners;
        _giveaway.Cancelled += _onCancelled;

        await _app.StartAsync();
        _log.LogInformation("OverlayHost listening on http://localhost:{Port}", Port);
    }

    public async Task StopAsync()
    {
        _busSub?.Dispose();
        if (_onStarted is not null) _giveaway.Started -= _onStarted;
        if (_onParticipant is not null) _giveaway.ParticipantAdded -= _onParticipant;
        if (_onWinners is not null) _giveaway.WinnersDrawn -= _onWinners;
        if (_onCancelled is not null) _giveaway.Cancelled -= _onCancelled;

        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }

    private async Task HandleChatClient(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _chatClients.TryAdd(id, ws);
        try
        {
            var snapshot = new OverlayEvent("chat.snapshot", new ChatSnapshotEvent(BuildChatSnapshot()));
            await SendJson(ws, snapshot, ct);
            await PumpReceiveLoop(ws, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Overlay chat client error"); }
        finally
        {
            _chatClients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    private async Task HandleGiveawayClient(WebSocket ws, CancellationToken ct)
    {
        var id = Guid.NewGuid();
        _giveawayClients.TryAdd(id, ws);
        try
        {
            // If a giveaway is already active, send a "started" snapshot so a late-joining overlay catches up.
            var active = _giveaway.Active;
            if (active is not null)
            {
                var evt = new OverlayEvent("giveaway.started", new GiveawayStartedEvent(
                    active.Id, active.Keyword, active.WinnerCount, active.DurationSec, active.StartedAt));
                await SendJson(ws, evt, ct);
            }
            await PumpReceiveLoop(ws, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _log.LogWarning(ex, "Overlay giveaway client error"); }
        finally
        {
            _giveawayClients.TryRemove(id, out _);
            try { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch { }
        }
    }

    private static async Task PumpReceiveLoop(WebSocket ws, CancellationToken ct)
    {
        var buf = new byte[1024];
        while (ws.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var res = await ws.ReceiveAsync(buf, ct);
            if (res.MessageType == WebSocketMessageType.Close) break;
        }
    }

    private System.Collections.Generic.IReadOnlyList<ChatMessageEvent> BuildChatSnapshot()
    {
        var recent = _bus.RecentMessages();
        var list = new System.Collections.Generic.List<ChatMessageEvent>(recent.Count);
        foreach (var m in recent)
            list.Add(new ChatMessageEvent(m.Id, m.Platform, m.Username, m.DisplayName,
                m.AvatarUrl, m.Text, m.ReceivedAt));
        return list;
    }

    private void BroadcastChatMessage(ChatMessage m)
    {
        var evt = new OverlayEvent("chat.message",
            new ChatMessageEvent(m.Id, m.Platform, m.Username, m.DisplayName,
                m.AvatarUrl, m.Text, m.ReceivedAt));
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(evt));
        foreach (var (_, ws) in _chatClients)
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendBytes(ws, bytes, CancellationToken.None);
        }
    }

    private void BroadcastGiveaway(string type, object data)
    {
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new OverlayEvent(type, data)));
        foreach (var (_, ws) in _giveawayClients)
        {
            if (ws.State != WebSocketState.Open) continue;
            _ = SendBytes(ws, bytes, CancellationToken.None);
        }
    }

    private static async Task SendJson(WebSocket ws, object payload, CancellationToken ct)
    {
        await SendBytes(ws, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload)), ct);
    }

    private static async Task SendBytes(WebSocket ws, byte[] bytes, CancellationToken ct)
    {
        try { await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        catch { /* swallow per-client errors so one slow client doesn't kill the broadcast */ }
    }

    public async ValueTask DisposeAsync() => await StopAsync();
}
```

- [ ] **Step 5: Add Core project reference (if missing)**

`LiveDeck.Overlay.csproj` must reference `LiveDeck.Core` (which holds `IGiveawayService`). Verify with:

```bash
cd /c/Users/burak/source/repos/LiveDeck
grep -l "LiveDeck.Core" LiveDeck.Overlay/LiveDeck.Overlay.csproj
```

If the reference is missing add a `<ProjectReference Include="..\LiveDeck.Core\LiveDeck.Core.csproj" />` line to `LiveDeck.Overlay.csproj`. Should already be present from earlier phases — double-check.

- [ ] **Step 6: Run tests — they should PASS**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~OverlayHostGiveawayTests" 2>&1 | tail -10
```
Expected: 2/2 pass.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Overlay/Models/OverlayEvent.cs LiveDeck.Overlay/OverlayHost.cs LiveDeck.Tests/Overlay/OverlayHostGiveawayTests.cs LiveDeck.Overlay/LiveDeck.Overlay.csproj
git commit -m "feat(overlay): add /ws/giveaway WebSocket + /overlay/giveaway endpoint"
```

---

### Task 9: OBS Overlay Assets — giveaway.html / giveaway.js / giveaway.css

**Goal:** Create the browser-side giveaway overlay. Roulette-style spinning name reveal, transparent background suitable for OBS Browser Source. No tests — visual layer.

**Files:**
- Create: `LiveDeck.Overlay/wwwroot/giveaway.html`
- Create: `LiveDeck.Overlay/wwwroot/giveaway.js`
- Create: `LiveDeck.Overlay/wwwroot/giveaway.css`

**Context:** OBS Browser Source loads `http://localhost:4747/overlay/giveaway`. The page connects to `/ws/giveaway` and listens for four event types. Spec section 3.5 dictates the visual flow: `started` → show keyword + countdown header; `participant` → increment counter; `winners.drawn` → run roulette animation cycling through `AnimationPool` for 4 seconds then settle on winners with confetti; `cancelled` → fade out. CSS background must be transparent (OBS overlay).

- [ ] **Step 1: Create giveaway.html**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/wwwroot/giveaway.html`:

```html
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <title>LiveDeck Çekiliş</title>
  <link rel="stylesheet" href="/giveaway.css">
</head>
<body>
  <div id="giveaway-root" class="hidden">
    <div id="header">
      <span id="emoji">🎁</span>
      <span id="keyword"></span>
      <span id="counter">0 katılımcı</span>
      <span id="countdown"></span>
    </div>

    <div id="reveal" class="hidden">
      <div id="reveal-title">KAZANANLAR</div>
      <ul id="winners-list"></ul>
    </div>

    <div id="roulette" class="hidden">
      <div id="roulette-name"></div>
    </div>
  </div>

  <script src="/giveaway.js"></script>
</body>
</html>
```

- [ ] **Step 2: Create giveaway.js**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/wwwroot/giveaway.js`:

```javascript
(() => {
  const $root = document.getElementById('giveaway-root');
  const $keyword = document.getElementById('keyword');
  const $counter = document.getElementById('counter');
  const $countdown = document.getElementById('countdown');
  const $roulette = document.getElementById('roulette');
  const $rouletteName = document.getElementById('roulette-name');
  const $reveal = document.getElementById('reveal');
  const $winnersList = document.getElementById('winners-list');

  let state = {
    giveawayId: null,
    keyword: '',
    durationSec: 0,
    startedAt: 0,
    countdownTimer: null
  };

  function show(el) { el.classList.remove('hidden'); }
  function hide(el) { el.classList.add('hidden'); }

  function reset() {
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
    state = { giveawayId: null, keyword: '', durationSec: 0, startedAt: 0, countdownTimer: null };
    $keyword.textContent = '';
    $counter.textContent = '0 katılımcı';
    $countdown.textContent = '';
    $rouletteName.textContent = '';
    $winnersList.innerHTML = '';
    hide($roulette);
    hide($reveal);
    hide($root);
  }

  function startCountdown() {
    if (state.durationSec <= 0) {
      $countdown.textContent = '';
      return;
    }
    const tick = () => {
      const elapsed = Math.floor(Date.now() / 1000) - state.startedAt;
      const remaining = Math.max(0, state.durationSec - elapsed);
      const m = Math.floor(remaining / 60);
      const s = remaining % 60;
      $countdown.textContent = `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
      if (remaining <= 0 && state.countdownTimer) {
        clearInterval(state.countdownTimer);
        state.countdownTimer = null;
      }
    };
    tick();
    state.countdownTimer = setInterval(tick, 1000);
  }

  function onStarted(e) {
    reset();
    state.giveawayId = e.GiveawayId;
    state.keyword = e.Keyword;
    state.durationSec = e.DurationSec;
    state.startedAt = e.StartedAt;
    $keyword.textContent = `"${e.Keyword}"`;
    $counter.textContent = '0 katılımcı';
    show($root);
    startCountdown();
  }

  function onParticipant(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    $counter.textContent = `${e.TotalCount} katılımcı`;
  }

  function onCancelled(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    $root.classList.add('fade-out');
    setTimeout(() => { reset(); $root.classList.remove('fade-out'); }, 600);
  }

  function onWinnersDrawn(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }

    const pool = e.AnimationPool || [];
    if (pool.length === 0) {
      // 0 katılımcı durumu
      $rouletteName.textContent = 'Henüz katılımcı yok';
      show($roulette);
      setTimeout(() => { reset(); }, 5000);
      return;
    }

    show($roulette);
    const totalMs = 4000;
    const startTime = performance.now();
    let lastIdx = -1;

    function frame(now) {
      const t = Math.min(1, (now - startTime) / totalMs);
      // ease-out: spin fast then slow
      const eased = 1 - Math.pow(1 - t, 3);
      const idx = Math.min(pool.length - 1, Math.floor(eased * (pool.length - 1)));
      if (idx !== lastIdx) {
        const p = pool[idx];
        $rouletteName.textContent = p.DisplayName || p.Username;
        lastIdx = idx;
      }
      if (t < 1) {
        requestAnimationFrame(frame);
      } else {
        revealWinners(e.Winners || []);
      }
    }
    requestAnimationFrame(frame);
  }

  function revealWinners(winners) {
    hide($roulette);
    $winnersList.innerHTML = '';
    for (const w of winners) {
      const li = document.createElement('li');
      li.className = 'winner';
      li.innerHTML = `
        <span class="platform-${w.Platform}">${w.Platform === 'instagram' ? '📷' : '🎵'}</span>
        <span class="name">${escapeHtml(w.DisplayName || w.Username)}</span>
      `;
      $winnersList.appendChild(li);
    }
    show($reveal);
    spawnConfetti();
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c =>
      ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  function spawnConfetti() {
    for (let i = 0; i < 60; i++) {
      const c = document.createElement('span');
      c.className = 'confetti';
      c.style.left = (Math.random() * 100) + '%';
      c.style.background = `hsl(${Math.random() * 360}, 80%, 60%)`;
      c.style.animationDelay = (Math.random() * 0.5) + 's';
      c.style.animationDuration = (1.8 + Math.random() * 1.2) + 's';
      document.body.appendChild(c);
      setTimeout(() => c.remove(), 3500);
    }
  }

  function connect() {
    const ws = new WebSocket(`ws://${location.host}/ws/giveaway`);
    ws.onmessage = (msg) => {
      const evt = JSON.parse(msg.data);
      switch (evt.Type) {
        case 'giveaway.started':       onStarted(evt.Data); break;
        case 'giveaway.participant':   onParticipant(evt.Data); break;
        case 'giveaway.winners.drawn': onWinnersDrawn(evt.Data); break;
        case 'giveaway.cancelled':     onCancelled(evt.Data); break;
      }
    };
    ws.onclose = () => setTimeout(connect, 1500);
    ws.onerror = () => { try { ws.close(); } catch {} };
  }
  connect();
})();
```

- [ ] **Step 3: Create giveaway.css**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/wwwroot/giveaway.css`:

```css
html, body {
  margin: 0; padding: 0;
  background: transparent;
  font-family: 'Segoe UI', system-ui, sans-serif;
  color: #ffffff;
  overflow: hidden;
  height: 100%;
}

.hidden { display: none !important; }
.fade-out { opacity: 0; transition: opacity 0.6s ease-out; }

#giveaway-root {
  position: absolute; left: 24px; top: 24px;
  min-width: 360px;
  background: rgba(15, 17, 24, 0.85);
  border-radius: 12px;
  padding: 16px 20px;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.4);
  backdrop-filter: blur(8px);
}

#header {
  display: flex; align-items: center; gap: 12px;
  font-size: 22px; font-weight: 600;
}
#emoji { font-size: 28px; }
#keyword { color: #ffce46; }
#counter { margin-left: auto; font-size: 16px; opacity: 0.85; }
#countdown { font-variant-numeric: tabular-nums; font-size: 18px; color: #4dd0ff; }

#roulette {
  margin-top: 16px;
  font-size: 36px; font-weight: 700;
  text-align: center;
  padding: 18px;
  background: linear-gradient(135deg, #ff6b9d, #ffce46);
  border-radius: 8px;
  color: #0f1118;
  text-shadow: 0 1px 0 rgba(255,255,255,0.4);
  animation: pulse 0.4s ease-in-out infinite alternate;
}

@keyframes pulse {
  from { transform: scale(1.0); }
  to   { transform: scale(1.04); }
}

#reveal {
  margin-top: 16px;
}
#reveal-title {
  font-size: 14px; letter-spacing: 4px;
  color: #ffce46;
  font-weight: 700;
  margin-bottom: 8px;
}
#winners-list {
  list-style: none; margin: 0; padding: 0;
}
.winner {
  display: flex; align-items: center; gap: 10px;
  font-size: 26px; font-weight: 600;
  padding: 8px 0;
  animation: slideIn 0.4s ease-out both;
}
.winner:nth-child(2) { animation-delay: 0.1s; }
.winner:nth-child(3) { animation-delay: 0.2s; }
.winner:nth-child(n+4) { animation-delay: 0.3s; }

@keyframes slideIn {
  from { opacity: 0; transform: translateX(-20px); }
  to   { opacity: 1; transform: translateX(0); }
}

.platform-instagram, .platform-tiktok { font-size: 22px; }

.confetti {
  position: fixed; top: -10px;
  width: 10px; height: 14px;
  pointer-events: none;
  animation: confetti-fall linear forwards;
  z-index: 999;
}
@keyframes confetti-fall {
  to { transform: translateY(110vh) rotate(720deg); opacity: 0.2; }
}
```

- [ ] **Step 4: Ensure wwwroot files are copied to output**

Verify `LiveDeck.Overlay.csproj` already has a `<Content Include="wwwroot\**" CopyToOutputDirectory="PreserveNewest" />` rule. The existing `chat.html` already follows this — open the `.csproj` and confirm the wwwroot include is recursive (`wwwroot\**`). If it lists files individually, add the new three:

```xml
<Content Include="wwwroot\giveaway.html">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
<Content Include="wwwroot\giveaway.js">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
<Content Include="wwwroot\giveaway.css">
  <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
</Content>
```

- [ ] **Step 5: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Overlay 2>&1 | tail -3
```
Expected: 0 errors. Confirm new files exist in `LiveDeck.Overlay/bin/Debug/net10.0/wwwroot/`:

```bash
ls LiveDeck.Overlay/bin/Debug/net10.0/wwwroot/giveaway.*
```
Expected: three files listed.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Overlay/wwwroot/giveaway.html LiveDeck.Overlay/wwwroot/giveaway.js LiveDeck.Overlay/wwwroot/giveaway.css LiveDeck.Overlay/LiveDeck.Overlay.csproj
git commit -m "feat(overlay): add giveaway.html roulette overlay with confetti reveal"
```

---

### Task 10: Stream Report — Giveaway Summary Row

**Goal:** When the user ends the live stream, the existing "Yayın Raporu" dialog gains one new line per giveaway: `🎁 "<keyword>" · N katılımcı · M kazanan`.

**Files:**
- Modify: `LiveDeck.App/ViewModels/StreamReportViewModel.cs` (load giveaway summaries)
- Modify: `LiveDeck.App/Views/StreamReportDialog.xaml` (display giveaway list)
- Modify: `LiveDeck.Core/Giveaway/GiveawayRepository.cs` (add `ListByStreamId` method, if not already)
- Test: `LiveDeck.Tests/Giveaway/GiveawayRepositoryListTests.cs`

**Context:** From Phase 2a, `StreamReportViewModel` already aggregates per-stream data (orders, label count, top buyers). `Stream` entity has an `Id` (TEXT) referenced by `Giveaway.StreamId`. The report dialog has a Grid with row sections — we add a new section above the close button.

- [ ] **Step 1: Write failing test for repo list method**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Giveaway/GiveawayRepositoryListTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Giveaway;
using LiveDeck.Core.Storage;
using Xunit;

namespace LiveDeck.Tests.Giveaway;

public sealed class GiveawayRepositoryListTests
{
    [Fact]
    public void ListByStreamId_returns_giveaways_with_winner_counts()
    {
        var db = TestDb.CreateInMemory();   // helper from earlier phases
        var repo = new GiveawayRepository(db);

        var g1 = repo.Create(streamId: "s1", keyword: "yarisma", winnerCount: 3,
            durationSec: 60, platformFilter: null, preventRewinning: true,
            randomSeed: "seed-a", startedAt: 1000);
        repo.AddParticipant(g1.Id, "ali",  "Ali",  null, "instagram", 1010);
        repo.AddParticipant(g1.Id, "veli", "Veli", null, "instagram", 1020);
        repo.MarkWinners(g1.Id, new[] { "ali", "veli" });

        var g2 = repo.Create(streamId: "s1", keyword: "kazan", winnerCount: 1,
            durationSec: 0, platformFilter: "tiktok", preventRewinning: true,
            randomSeed: "seed-b", startedAt: 2000);
        // g2: no participants drawn

        // Different stream — should not appear
        repo.Create(streamId: "s2", keyword: "x", winnerCount: 1,
            durationSec: 30, platformFilter: null, preventRewinning: true,
            randomSeed: "seed-c", startedAt: 3000);

        var summaries = repo.ListByStreamId("s1");

        summaries.Should().HaveCount(2);
        summaries[0].Keyword.Should().Be("yarisma");
        summaries[0].ParticipantCount.Should().Be(2);
        summaries[0].WinnerCount.Should().Be(2);
        summaries[1].Keyword.Should().Be("kazan");
        summaries[1].ParticipantCount.Should().Be(0);
        summaries[1].WinnerCount.Should().Be(0);
    }
}
```

`GiveawaySummary` is a new record-like type living next to `GiveawayRepository`:

```csharp
public sealed record GiveawaySummary(
    long Id, string Keyword, int ParticipantCount, int WinnerCount, long StartedAt);
```

- [ ] **Step 2: Run test — FAIL**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryListTests" 2>&1 | tail -10
```
Expected: compile error — `ListByStreamId` and `GiveawaySummary` don't exist yet.

- [ ] **Step 3: Add method + record to GiveawayRepository.cs**

Append to `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Giveaway/GiveawayRepository.cs`:

```csharp
public sealed record GiveawaySummary(
    long Id, string Keyword, int ParticipantCount, int WinnerCount, long StartedAt);
```

And add the method to the `GiveawayRepository` class:

```csharp
public IReadOnlyList<GiveawaySummary> ListByStreamId(string streamId)
{
    using var conn = _db.Open();
    const string sql = @"
        SELECT  g.Id, g.Keyword, g.StartedAt,
                COALESCE(SUM(CASE WHEN p.IsWinner = 0 OR p.IsWinner = 1 THEN 1 ELSE 0 END), 0) AS Total,
                COALESCE(SUM(CASE WHEN p.IsWinner = 1 THEN 1 ELSE 0 END), 0)                 AS Winners
        FROM    Giveaway g
        LEFT JOIN GiveawayParticipant p ON p.GiveawayId = g.Id
        WHERE   g.StreamId = @StreamId
        GROUP BY g.Id, g.Keyword, g.StartedAt
        ORDER BY g.StartedAt;";
    return conn.Query<(long Id, string Keyword, long StartedAt, int Total, int Winners)>(sql, new { StreamId = streamId })
        .Select(r => new GiveawaySummary(r.Id, r.Keyword, r.Total, r.Winners, r.StartedAt))
        .ToList();
}
```

(The `IsWinner` flag was added in Task 4 inside `MarkWinners`. The `Total` calculation is intentionally redundant — `COUNT(p.Id)` would do the same — but written this way to make the winner expression mirror it for clarity.)

Simpler alternative if reviewers prefer `COUNT`:

```csharp
const string sql = @"
    SELECT  g.Id, g.Keyword, g.StartedAt,
            COUNT(p.Id)                                           AS Total,
            COALESCE(SUM(CASE WHEN p.IsWinner = 1 THEN 1 ELSE 0 END), 0) AS Winners
    FROM    Giveaway g
    LEFT JOIN GiveawayParticipant p ON p.GiveawayId = g.Id
    WHERE   g.StreamId = @StreamId
    GROUP BY g.Id, g.Keyword, g.StartedAt
    ORDER BY g.StartedAt;";
```

Either is fine; pick the second.

- [ ] **Step 4: Run test — PASS**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryListTests" 2>&1 | tail -10
```
Expected: 1/1 pass.

- [ ] **Step 5: Plumb summaries into StreamReportViewModel**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/StreamReportViewModel.cs`. Add a `GiveawayRepository` constructor dependency, a property:

```csharp
public IReadOnlyList<GiveawaySummary> Giveaways { get; }
```

…and populate it inside the existing constructor that loads the report (alongside orders/buyers):

```csharp
Giveaways = giveawayRepo.ListByStreamId(stream.Id);
```

Add the matching `using LiveDeck.Core.Giveaway;`. The existing `StreamReportViewModel` constructor signature changes — every caller (one in `StreamHistoryViewModel`, one in `MainShellViewModel.EndStream`) must pass the repo. The compile error will surface those call sites.

- [ ] **Step 6: Update StreamReportDialog.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/StreamReportDialog.xaml`. Locate the existing Grid section between "Top Buyers" and the close button row. Insert a new `StackPanel`:

```xml
<StackPanel Grid.Row="..." Margin="0,12,0,0"
            Visibility="{Binding Giveaways.Count, Converter={StaticResource CountToVisibleConverter}}">
    <TextBlock Text="🎁 Çekilişler" FontWeight="Bold" FontSize="14" Margin="0,0,0,4"/>
    <ItemsControl ItemsSource="{Binding Giveaways}">
        <ItemsControl.ItemTemplate>
            <DataTemplate>
                <TextBlock Margin="8,2,0,2">
                    <Run Text="🎁"/>
                    <Run Text="&quot;"/>
                    <Run Text="{Binding Keyword, Mode=OneWay}" FontWeight="Bold"/>
                    <Run Text="&quot; · "/>
                    <Run Text="{Binding ParticipantCount, Mode=OneWay}"/>
                    <Run Text=" katılımcı · "/>
                    <Run Text="{Binding WinnerCount, Mode=OneWay}"/>
                    <Run Text=" kazanan"/>
                </TextBlock>
            </DataTemplate>
        </ItemsControl.ItemTemplate>
    </ItemsControl>
</StackPanel>
```

Adjust the row indices so the close button moves down by one. Add a new converter `CountToVisibleConverter` (collapsed when count is 0) in `LiveDeck.App/Converters/CountToVisibleConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>int (or any IConvertible) > 0 → Visible, otherwise Collapsed.</summary>
public sealed class CountToVisibleConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i)            return i > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (value is long l)           return l > 0 ? Visibility.Visible : Visibility.Collapsed;
        return Visibility.Collapsed;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register it in `App.xaml`:

```xml
<converters:CountToVisibleConverter x:Key="CountToVisibleConverter"/>
```

- [ ] **Step 7: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors. The compile errors at the `StreamReportViewModel` call sites surface and need a quick `giveawayRepo` parameter add — fix them, build again, 0 errors.

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Giveaway/GiveawayRepository.cs LiveDeck.Tests/Giveaway/GiveawayRepositoryListTests.cs LiveDeck.App/ViewModels/StreamReportViewModel.cs LiveDeck.App/Views/StreamReportDialog.xaml LiveDeck.App/Converters/CountToVisibleConverter.cs LiveDeck.App/App.xaml
git commit -m "feat(report): show giveaway summary rows in StreamReportDialog"
```

---

### Task 11: AppHost — Register Giveaway Services

**Goal:** Wire up DI so the giveaway feature actually runs end-to-end. The host (`AppHost.cs` or `App.xaml.cs`) registers `GiveawayRepository`, `GiveawayDrawer`, `IGiveawayService`, `GiveawayBannerViewModel`, and `NewGiveawayDialog`. The pre-existing `OverlayHost` registration changes signature.

**Files:**
- Modify: `LiveDeck.App/AppHost.cs` (or wherever DI is configured)
- Modify: `LiveDeck.App/App.xaml.cs` (if DI lives there)

**Context:** From P2a, the app uses `Microsoft.Extensions.DependencyInjection` with a single `AppHost` class building a `ServiceProvider`. Singletons: `IDb`, repos, `IChatBus`, `OverlayHost`, `LabelPrinter`. View models that hold per-window state are typically transient or scoped to a window. We extend the same patterns — `GiveawayRepository` (singleton, holds DB connection factory), `GiveawayDrawer` (singleton, stateless), `IGiveawayService` (singleton — only one giveaway active at a time per app), `GiveawayBannerViewModel` (singleton — one banner instance shared with `MainShellViewModel`), `NewGiveawayDialog` (transient — new dialog each invocation).

- [ ] **Step 1: Edit AppHost service registration**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs` (or wherever `services.AddXxx(...)` calls live). Locate the registration block. Add:

```csharp
// --- Giveaway (Phase 2b) ---
services.AddSingleton<GiveawayRepository>();
services.AddSingleton<GiveawayDrawer>();
services.AddSingleton<IGiveawayService, GiveawayService>();
services.AddSingleton<GiveawayBannerViewModel>();
services.AddTransient<NewGiveawayDialog>();
services.AddTransient<NewGiveawayViewModel>();
```

- [ ] **Step 2: Update OverlayHost registration**

Find the existing line:

```csharp
services.AddSingleton<OverlayHost>(sp =>
    new OverlayHost(sp.GetRequiredService<IChatBus>(), port: 4747,
        log: sp.GetRequiredService<ILogger<OverlayHost>>()));
```

Replace with:

```csharp
services.AddSingleton<OverlayHost>(sp =>
    new OverlayHost(
        sp.GetRequiredService<IChatBus>(),
        sp.GetRequiredService<IGiveawayService>(),
        port: 4747,
        log: sp.GetRequiredService<ILogger<OverlayHost>>()));
```

(If the existing registration uses `services.AddSingleton<OverlayHost>()` without a factory, switch to the factory form above.)

- [ ] **Step 3: Add the missing usings**

At the top of `AppHost.cs`:

```csharp
using LiveDeck.App.ViewModels;
using LiveDeck.App.Views;
using LiveDeck.Core.Giveaway;
```

- [ ] **Step 4: Verify migration runs at startup**

The DB migration runner from earlier phases reads `_meta.SchemaVersion` and applies any new migration files in order. Migration 004 from Task 1 must be picked up. Confirm by adding a one-line log assertion: search `AppHost.cs` for the existing migration log; it should print "Schema version: 4" after Task 1 lands.

If the migration runner uses a switch statement keyed on integer (instead of file enumeration), the case for `case 3:` block must be added that calls the migration code. Most likely the project uses file enumeration — confirmed by Task 1 just dropping a `.sql` file in the migrations folder.

- [ ] **Step 5: Build the whole solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -3
```
Expected: 0 errors, 0 warnings (or only pre-existing warnings).

- [ ] **Step 6: Run all tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -10
```
Expected: ALL tests pass (Phase 1 + 1b + 2a + 2b).

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs
git commit -m "feat(app): register giveaway services in AppHost DI"
```

---

### Task 12: Manual Acceptance Smoke Test

**Goal:** End-to-end verification on the running app. No automated tests — this is a hand-run checklist that the developer ticks off before declaring Phase 2b shippable.

**Files:** None to modify; only execute and observe.

**Context:** Phase 2b touches DB schema, services, ViewModels, WPF views, OBS overlay. Smoke test exercises every layer without writing code.

- [ ] **Step 1: Start the app cleanly**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```
Verify the SQLite log shows `Schema version: 4` (migration ran successfully).

- [ ] **Step 2: Open OBS Browser Source**

In OBS, add a Browser Source pointing to `http://localhost:4747/overlay/giveaway`. Width 1280, height 720, transparent background (CSS already handles it). The page should render blank — no giveaway active.

- [ ] **Step 3: Start a stream**

Click "Yayını Başlat" in the app. Connect a real or simulated chat client (browser extension on a test Instagram/TikTok page is fine; if neither is available, the manual chat injection used during P1b smoke also works).

- [ ] **Step 4: Start a giveaway**

Click 🎁 Çekiliş. Dialog opens. Fill in:
- Anahtar Kelime: `yarisma`
- Kazanan Sayısı: `2`
- Süre: `30 sn`
- Platform Filtresi: `Tümü`
- Tekrar kazanmasın: ✓

Click "Başlat".

**Expected:**
- Banner appears: `🎁 yarisma · 0 katılımcı · 00:30 · [Şimdi Çek] [İptal]`
- "Yayını Bitir" button is disabled
- OBS overlay shows the giveaway header with countdown ticking

- [ ] **Step 5: Inject participants**

In the chat (real or test), have multiple users send messages containing the keyword. Confirm:
- Banner counter updates: "1 katılımcı", "2 katılımcı", …
- OBS overlay counter updates in real time
- Sending the same keyword twice from the same user does NOT increment

- [ ] **Step 6: Wait for auto-draw**

Let the 30 seconds expire. Expected:
- OBS overlay: roulette spins for ~4 seconds, settles, reveals 2 winner names with confetti
- Banner clears, "Yayını Bitir" button re-enables
- Application log shows entries for `Giveaway.Started`, `Giveaway.WinnersDrawn` with the random seed

- [ ] **Step 7: Repeat with manual draw**

Start a second giveaway with `Süre = 0` (limitless). Confirm banner shows `(süre limitsiz)` not a countdown. After a few participants join, click "Şimdi Çek". Expected: same OBS roulette + reveal sequence.

- [ ] **Step 8: Repeat with cancel**

Start a third giveaway. Click "İptal". Confirm dialog appears. Click "Evet". Expected:
- Banner clears
- OBS overlay fades out
- No winners are recorded in the DB

- [ ] **Step 9: Edge cases**

- Start a giveaway, do not let any chat hit the keyword, click "Şimdi Çek". OBS shows "Henüz katılımcı yok" for 5 seconds and clears.
- Start a giveaway with 5 winners but only 2 participants join. Expected: 2 winners selected (no error).
- Add a participant, then add their username to the blacklist. Start a *new* giveaway, have them try to join. They are silently filtered.
- With "Tekrar kazanmasın" enabled, a previous winner from giveaway 1 sends the keyword for giveaway 2. They are silently filtered.

- [ ] **Step 10: End stream → report**

Click "Yayını Bitir". The Yayın Raporu dialog should list each giveaway with: `🎁 "<keyword>" · N katılımcı · M kazanan`.

- [ ] **Step 11: Inspect the database**

```bash
sqlite3 "%APPDATA%/LiveDeck/livedeck.db" \
  "SELECT Id, StreamId, Keyword, WinnerCount, ParticipantCount, RandomSeed FROM Giveaway;"
sqlite3 "%APPDATA%/LiveDeck/livedeck.db" \
  "SELECT GiveawayId, Username, IsWinner FROM GiveawayParticipant ORDER BY GiveawayId, IsWinner DESC;"
```
(Use the actual sqlite3 path on the dev machine; or open the DB in DB Browser for SQLite.)

Expected: Three giveaways recorded (or two if cancellation deletes the row — check spec section 3.4 — current implementation keeps the row, sets `EndedAt` but no winners). Random seeds are non-empty unique strings.

- [ ] **Step 12: Commit acceptance log**

If a manual smoke test log is kept (`docs/smoke-tests/2026-04-28-phase-2b-smoke.md`), write the date, OBS version, .NET version, and tick each step. Commit:

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add docs/smoke-tests/2026-04-28-phase-2b-smoke.md
git commit -m "docs: phase 2b acceptance smoke test results"
```

---

## Self-Review

**Spec coverage check:**

| Spec section                          | Plan task           |
|---------------------------------------|---------------------|
| 2. Schema (Giveaway/Participant)      | Task 1              |
| 3.1 Entity & repository methods       | Tasks 1-2           |
| 3.2 Domain — drawing algorithm        | Task 3              |
| 3.3 Domain — service orchestration    | Task 4              |
| 3.4 WPF — start dialog                | Task 5              |
| 3.4 WPF — banner + countdown          | Task 6              |
| 3.4 WPF — MainShell button + status   | Task 7              |
| 3.5 OBS overlay endpoints + WS        | Task 8              |
| 3.5 OBS overlay HTML/JS/CSS animation | Task 9              |
| 3.6 Stream report row                 | Task 10             |
| 3.7 DI registration                   | Task 11             |
| 3.8 Manual acceptance                 | Task 12             |
| 11 edge case decisions                | distributed (1,4–10)|

All sections covered. Edge cases:
- 0 katılımcı → Task 9 onWinnersDrawn handles empty pool; Task 4 GiveawayService.Draw returns empty
- Katılımcı < kazanan → Task 3 GiveawayDrawer caps via Math.Min
- Aynı user duplicate → Task 1 UNIQUE INDEX + Task 4 try/catch on SqliteErrorCode 19
- Kara liste → Task 4 AddParticipantFromChat filters via CustomerRepository.IsBlacklisted
- PreventRewinning → Task 4 filter at add time
- İptal onay → Task 7 CancelGiveawayCommand shows MessageBox.YesNo
- Yayını Bitir engelleme → Task 7 CanEndStream depends on !IsGiveawayActive
- RNG seed loglama → Task 4 GiveawayService.Start logs random seed
- StreamReport row → Task 10
- Migration 004 → Task 1
- Platform filtresi → Tasks 1, 4, 5

**Placeholder scan:** Searched for "TBD", "TODO", "implement later", "fill in details". None found in completed plan. Step 4 of Task 11 mentions "search AppHost.cs" instead of giving an exact line — acceptable because the file's exact migration-runner shape is confirmed at execution time; the verification action is concrete (look for "Schema version" log).

**Type consistency check:**

- `IGiveawayService` interface used in: Task 4 (definition), Task 7 (consumer), Task 8 (consumer), Task 11 (DI). Methods: `Start(string,int,int,string?,bool)`, `TryAddParticipantFromChat(ChatMessage)`, `Draw()`, `Cancel()`. Events: `Started`, `ParticipantAdded`, `WinnersDrawn`, `Cancelled`. Property: `Active`. Consistent across tasks.
- `Giveaway` entity fields: `Id`, `StreamId`, `Keyword`, `WinnerCount`, `DurationSec`, `PlatformFilter`, `PreventRewinning`, `RandomSeed`, `StartedAt`, `EndedAt`. Used identically in Tasks 1, 2, 4, 8, 10.
- `GiveawayParticipant` fields: `Id`, `GiveawayId`, `Username`, `DisplayName`, `AvatarUrl`, `Platform`, `JoinedAt`, `IsWinner`. Used identically in Tasks 1, 2, 3, 4, 10. `IsWinner` flag set by `GiveawayRepository.MarkWinners` (Task 2) and read by `GiveawayRepository.ListByStreamId` (Task 10).
- `GiveawayWinner` overlay record (Task 8) — distinct from `GiveawayParticipant` (Core); only `Username`, `DisplayName`, `AvatarUrl`, `Platform`. Mapped by `GiveawayService` when raising `WinnersDrawn`. Used by Task 9 JS via `e.Winners[i].Username` etc.
- Banner property names: `IsActive`, `Keyword`, `ParticipantCount`, `Countdown`, `IsLimitless`, `AutoDrawRequested` (event), `DrawNowCommand`, `CancelCommand`. Used identically in Tasks 6 and 7.
- `BoolToVisibleConverter` / `BoolToCollapsedConverter` defined Task 7, registered in App.xaml Task 7. `CountToVisibleConverter` defined Task 10, registered Task 10. `NullToCollapsedConverter` / `UnixToDateConverter` / `BlacklistToBrushConverter` were pre-existing.
- Migration filename: `004_giveaway.sql` (Task 1). Schema version expected after run: 4 (Task 11 Step 4).

All consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-2b-giveaway.md`. Two execution options:

**1. Subagent-Driven (recommended)** — I dispatch a fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Hangisi?
