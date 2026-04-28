# Faz 3a — Müşteri Merkezi Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Müşteri detay görünümü (özet + etiket geçmişi + çekiliş geçmişi + notlar) + müşteri arama + schema cleanup (TrustScore + sipariş alanlarını kaldır).

**Architecture:** Mimari değişiklik yok. Mevcut Dapper repo + WPF modal dialog pattern'ini takip ediyoruz. Schema migration 005 ALTER TABLE DROP COLUMN ile 4 ölü alanı kaldırır. `Customer` record 17→13 alan; aralarda kalktığı için tüm caller'lar compile error verir, hepsi aynı commit'te düzeltilir.

**Tech Stack:** .NET 10 WPF, Dapper, CommunityToolkit.Mvvm, ClosedXML (mevcut). Yeni paket yok.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-3a state:** Faz 3d HEAD `155c061` + spec commit `ed354e7`. 83/83 tests passing.

**Spec reference:** `docs/superpowers/specs/2026-04-28-phase-3a-customer-center-design.md`

---

## Task Index

**Schema cleanup (1):** Migration 005 + MigrationRunnerTests
**Entity + repo + service + fixtures (2):** Customer record 13 alan + CustomerRepository SQL/Map + CustomerService.GetOrCreate + 4 test fixture file güncellemesi
**Repo metodları (3-5):** CustomerRepository.UpdateNotes/Search + LabelRepository.GetByCustomer + GiveawayRepository.GetParticipationsByCustomer
**ViewModels (6-7):** CustomerDetailViewModel + CustomerSearchViewModel
**Views (8-9):** CustomerDetailDialog + CustomerSearchDialog
**Erişim noktaları (10-12):** MainShell context menus + ⋮ menü + BlacklistDialog button column
**DI + accept (13):** AppHost + manual smoke

---

### Task 1: Migration 005 + MigrationRunnerTests

**Files:**
- Create: `LiveDeck.Core/Storage/Migrations/005_drop_legacy_customer_metrics.sql`
- Modify: `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`

**Context:** SQLite ≥ 3.35'in `ALTER TABLE DROP COLUMN`'ı destekler. Microsoft.Data.Sqlite 9 SQLite 3.45+ paketler. Test runner version-gated `MigrationRunner` kullanıyor; `_meta.SchemaVersion` 4 → 5.

- [ ] **Step 1: Create migration 005**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Migrations/005_drop_legacy_customer_metrics.sql`:

```sql
-- Phase 3a: drop unused TrustScore + order-tracking columns. P1b's pivot to
-- label workflow eliminated the order-completion lifecycle; these columns
-- were never updated and made the entity heavier than it needs to be.

ALTER TABLE Customer DROP COLUMN TrustScore;
ALTER TABLE Customer DROP COLUMN TotalOrders;
ALTER TABLE Customer DROP COLUMN CompletedOrders;
ALTER TABLE Customer DROP COLUMN CancelledOrders;

UPDATE _meta SET SchemaVersion = 5 WHERE Id = 1;
```

- [ ] **Step 2: Update MigrationRunnerTests**

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
    public void Run_creates_all_tables_at_version_5_with_dropped_legacy_columns()
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
        version.Should().Be(5);

        var customerColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Customer')").AsList();
        customerColumns.Should().Contain(new[]
            { "TotalLabelsPrinted", "TotalAmount", "BlacklistedAt", "Notes", "IsBlacklisted" });
        customerColumns.Should().NotContain(new[]
            { "TrustScore", "TotalOrders", "CompletedOrders", "CancelledOrders" });
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
        version.Should().Be(5);
    }
}
```

- [ ] **Step 3: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests" 2>&1 | tail -10
```

Expected: 1/2 fail (`Run_creates_all_tables_at_version_5_with_dropped_legacy_columns`). The "version=5" assertion fails because file isn't in output bin yet — `Content CopyToOutputDirectory` should pick it up on next build, but test runs against existing build.

- [ ] **Step 4: Run GREEN**

Re-build and re-test:

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core 2>&1 | tail -3
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests" 2>&1 | tail -3
```

Expected: 2/2 pass.

The full test suite will FAIL at this point because Task 2 hasn't yet trimmed the `Customer` ctor. That's expected — Tasks 1 and 2 land in separate commits but tests are red between them. We accept that here because Task 1 is the schema; Task 2 immediately follows.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Migrations/005_drop_legacy_customer_metrics.sql LiveDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(core): drop TrustScore + order-tracking columns (migration 005)"
```

---

### Task 2: Customer record 13 alan + cascade fixture updates

**Files:**
- Modify: `LiveDeck.Core/Customers/Customer.cs`
- Modify: `LiveDeck.Core/Customers/CustomerService.cs`
- Modify: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Modify: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`
- Modify: `LiveDeck.Tests/Sales/GiveawayServiceTests.cs`

**Context:** Bu task tek bir atomic commit'te tüm caller siteleri günceller. `Customer` record'undan `TotalOrders`, `CompletedOrders`, `CancelledOrders`, `TrustScore` alanlarını kaldırıyoruz. CompositeRepository INSERT/Map/Row sınıfından da bu kolonlar çıkar. CustomerService.GetOrCreate ctor çağrısı 13 alana iner. Mevcut test fixture'ları güncellenir; bazı testlerin tamamen değiştirilmesi gerekmez.

- [ ] **Step 1: Replace Customer record**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Customers/Customer.cs`:

```csharp
namespace LiveDeck.Core.Customers;

public sealed record Customer(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    long FirstSeenAt,
    long LastSeenAt,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes,
    int TotalLabelsPrinted,
    decimal TotalAmount,
    long? BlacklistedAt);
```

- [ ] **Step 2: Update CustomerRepository**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`:

```csharp
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
               TotalLabelsPrinted, TotalAmount, BlacklistedAt)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount, @BlacklistedAt)",
            new
            {
                c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
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
        public int IsBlacklisted { get; init; }
        public string? BlacklistReason { get; init; }
        public string? Notes { get; init; }
        public int TotalLabelsPrinted { get; init; }
        public decimal TotalAmount { get; init; }
        public long? BlacklistedAt { get; init; }
    }
}
```

- [ ] **Step 3: Update CustomerService.GetOrCreate**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Customers/CustomerService.cs`. Replace the `GetOrCreate` method body:

```csharp
public Customer GetOrCreate(string platform, string username,
    string? displayName, string? avatarUrl)
{
    var existing = _repo.FindByPlatformAndUsername(platform, username);
    if (existing is not null) return existing;

    var now = _clock.UnixNow();
    var customer = new Customer(
        Id: Guid.NewGuid().ToString("N"),
        Platform: platform,
        Username: username,
        DisplayName: displayName,
        AvatarUrl: avatarUrl,
        FirstSeenAt: now,
        LastSeenAt: now,
        IsBlacklisted: false,
        BlacklistReason: null,
        Notes: null,
        TotalLabelsPrinted: 0,
        TotalAmount: 0m,
        BlacklistedAt: null);
    _repo.Insert(customer);
    return customer;
}
```

- [ ] **Step 4: Update CustomerRepositoryTests fixture**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`. Find every `new Customer(...)` instantiation and remove the 4 dropped fields. The existing test seed (line ~16) becomes:

```csharp
new Customer("c-1", "instagram", "@ali", "Ali", null,
    FirstSeenAt: 100, LastSeenAt: 100,
    IsBlacklisted: false, BlacklistReason: null, Notes: null,
    TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null)
```

Apply the same trim to every other `new Customer(...)` occurrence in this file. The exact set of removals: drop `TotalOrders: 0`, `CompletedOrders: 0`, `CancelledOrders: 0`, `TrustScore: 100` (or whatever values were used).

- [ ] **Step 5: Update GiveawayServiceTests fixture**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Sales/GiveawayServiceTests.cs`. The `AddParticipantFromChat_skips_blacklisted_user` test creates a Customer manually:

Find:
```csharp
?? new Customer(System.Guid.NewGuid().ToString("N"),
    "instagram", "@bad", null, null, 100, 100,
    0, 0, 0, 100, false, null, null, 0, 0m, null);
```

Replace with:
```csharp
?? new Customer(System.Guid.NewGuid().ToString("N"),
    "instagram", "@bad", null, null, 100, 100,
    false, null, null, 0, 0m, null);
```

(13 positional args.)

- [ ] **Step 6: Build + run all tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 0 errors, 83/83 pass.

If any other compile error surfaces (e.g., fixture in `GiveawayServicePreventRewinningCacheTests.cs` references `Customer` directly), trim it the same way. Search for any remaining `TotalOrders|CompletedOrders|CancelledOrders|TrustScore` references:

```bash
cd /c/Users/burak/source/repos/LiveDeck
grep -rn "TotalOrders\|CompletedOrders\|CancelledOrders\|TrustScore" --include="*.cs"
```

Expected: 0 hits. If any remain, fix them and re-run tests.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Customers/Customer.cs LiveDeck.Core/Customers/CustomerService.cs LiveDeck.Core/Storage/Repositories/CustomerRepository.cs LiveDeck.Tests/Storage/CustomerRepositoryTests.cs LiveDeck.Tests/Sales/GiveawayServiceTests.cs
git commit -m "refactor(core): trim Customer record to 13 fields (drop TrustScore + order metrics)"
```

---

### Task 3: CustomerRepository.UpdateNotes + Search

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Modify: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`

**Context:** Detail dialog notes editing + search dialog'un ihtiyacı. TDD.

- [ ] **Step 1: Write failing tests**

Append to `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`, just before the closing `}` of the class:

```csharp
[Fact]
public void UpdateNotes_sets_notes_or_normalizes_whitespace_to_null()
{
    using var db = new InMemorySqlite();
    new MigrationRunner(db).Run();
    var repo = new CustomerRepository(db);
    var c = new Customer("c-1", "instagram", "@ali", "Ali", null,
        FirstSeenAt: 100, LastSeenAt: 100,
        IsBlacklisted: false, BlacklistReason: null, Notes: null,
        TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null);
    repo.Insert(c);

    repo.UpdateNotes("c-1", "VIP müşteri");
    repo.GetById("c-1")!.Notes.Should().Be("VIP müşteri");

    repo.UpdateNotes("c-1", "   ");
    repo.GetById("c-1")!.Notes.Should().BeNull();

    repo.UpdateNotes("c-1", null);
    repo.GetById("c-1")!.Notes.Should().BeNull();
}

[Fact]
public void Search_returns_matching_customers_ordered_by_last_seen()
{
    using var db = new InMemorySqlite();
    new MigrationRunner(db).Run();
    var repo = new CustomerRepository(db);

    repo.Insert(new Customer("c-1", "instagram", "@ali",     "Ali", null,
        100, 200, false, null, null, 0, 0m, null));
    repo.Insert(new Customer("c-2", "instagram", "@alican",  "Alican", null,
        100, 300, false, null, null, 0, 0m, null));
    repo.Insert(new Customer("c-3", "tiktok",    "@veli",    "Veli", null,
        100, 400, false, null, null, 0, 0m, null));

    var results = repo.Search("ali", limit: 50);
    results.Select(c => c.Id).Should().Equal(new[] { "c-2", "c-1" });   // by LastSeenAt DESC, only @ali* match

    repo.Search("ALI", limit: 50).Select(c => c.Id)
        .Should().Equal(new[] { "c-2", "c-1" });   // case-insensitive

    repo.Search("xyz", limit: 50).Should().BeEmpty();

    repo.Search("ali", limit: 1).Should().HaveCount(1);
}
```

Add `using System.Linq;` to the file's using block if not already present.

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~CustomerRepositoryTests" 2>&1 | tail -10
```

Expected: compile errors (`UpdateNotes`, `Search` not found).

- [ ] **Step 3: Implement UpdateNotes + Search**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`. Insert the following methods just before `private static Customer Map(Row r)`:

```csharp
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
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~CustomerRepositoryTests" 2>&1 | tail -3
```

Expected: all CustomerRepositoryTests pass.

- [ ] **Step 5: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 85/85 (83 baseline + 2 new).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/CustomerRepository.cs LiveDeck.Tests/Storage/CustomerRepositoryTests.cs
git commit -m "feat(core): add CustomerRepository.UpdateNotes + Search"
```

---

### Task 4: LabelRepository.GetByCustomer + CustomerLabelRow

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/LabelRepository.cs`
- Modify: `LiveDeck.Tests/Storage/LabelRepositoryTests.cs`

**Context:** Detail dialog'un etiket geçmişi tab'ı için. Yeni metod customer'a ait labels'i en yeniden eskiye verir. `CustomerLabelRow` UI projeksiyonu — `Label` ile aynı veriyi taşır ama `IsPrinted` bool olarak hesaplanmış (PrintedAt IS NOT NULL).

- [ ] **Step 1: Write failing test**

Append to `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/LabelRepositoryTests.cs`, just before the closing `}` of the class. Adjust signature/usings if needed:

```csharp
[Fact]
public void GetByCustomer_returns_labels_ordered_by_recent_for_only_that_customer()
{
    using var db = new InMemorySqlite();
    new MigrationRunner(db).Run();
    new SessionRepository(db).Insert(
        new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
    var repo = new LabelRepository(db);

    repo.Insert(new Label("l1", "s1", "c-A", "instagram", "@ali",  "kazak", "K01", 100m, AddedAt: 100, PrintedAt: 110));
    repo.Insert(new Label("l2", "s1", "c-A", "instagram", "@ali",  "ceket", "C02", 250m, AddedAt: 200, PrintedAt: null));
    repo.Insert(new Label("l3", "s1", "c-B", "instagram", "@veli", "etek",  null,  150m, AddedAt: 150, PrintedAt: 160));

    var rows = repo.GetByCustomer("c-A");
    rows.Should().HaveCount(2);
    rows[0].Id.Should().Be("l2");          // most recent
    rows[0].IsPrinted.Should().BeFalse();
    rows[1].Id.Should().Be("l1");
    rows[1].IsPrinted.Should().BeTrue();

    repo.GetByCustomer("c-X").Should().BeEmpty();
}
```

If the test file does not yet have `using LiveDeck.Core.Sessions;` or session insertion machinery, ensure imports are present. Existing tests in this file already create sessions before inserting labels.

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelRepositoryTests" 2>&1 | tail -10
```

Expected: compile error (`GetByCustomer` not found).

- [ ] **Step 3: Implement GetByCustomer + CustomerLabelRow**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/LabelRepository.cs`. Add the following method (just before any existing private helpers):

```csharp
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
```

Append to the same file (outside the class), or in a new section after the `LabelRepository` class declaration:

```csharp
/// <summary>UI projection of a Label for the customer detail dialog.</summary>
public sealed record CustomerLabelRow(
    string Id,
    string SessionId,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    bool IsPrinted);
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelRepositoryTests" 2>&1 | tail -3
```

Expected: all LabelRepositoryTests pass.

- [ ] **Step 5: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 86/86.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/LabelRepository.cs LiveDeck.Tests/Storage/LabelRepositoryTests.cs
git commit -m "feat(core): add LabelRepository.GetByCustomer + CustomerLabelRow projection"
```

---

### Task 5: GiveawayRepository.GetParticipationsByCustomer + CustomerGiveawayRow

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs`
- Modify: `LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs`

**Context:** Detail dialog'un çekiliş geçmişi tab'ı için. Cancelled giveaway'leri de döndürür (UI durum sütunu hesaplar).

- [ ] **Step 1: Write failing test**

Append to `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs`, just before the closing `}` of the class:

```csharp
[Fact]
public void GetParticipationsByCustomer_returns_history_with_status_flags()
{
    var (db, repo, _, cid) = Fx();
    using var _2 = db;

    // g1: ended, customer won
    repo.Insert(NewGiveaway("g1", "s1") with { EndedAt = 500 });
    repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, IsWinner: true));

    // g2: ended, customer participated but did not win
    repo.Insert(NewGiveaway("g2", "s1") with { EndedAt = 600 });
    repo.AddParticipant(new GiveawayParticipant("p2", "g2", cid, "instagram", "@a", 400, IsWinner: false));

    // g3: cancelled
    repo.Insert(NewGiveaway("g3", "s1") with { CancelledAt = 700 });
    repo.AddParticipant(new GiveawayParticipant("p3", "g3", cid, "instagram", "@a", 500, IsWinner: false));

    // g4: another customer's participation — must not appear
    new CustomerRepository(db).Insert(new Customer("c-2", "instagram", "@other", null, null,
        100, 100, false, null, null, 0, 0m, null));
    repo.Insert(NewGiveaway("g4", "s1") with { EndedAt = 800 });
    repo.AddParticipant(new GiveawayParticipant("p4", "g4", "c-2", "instagram", "@other", 600, IsWinner: false));

    var rows = repo.GetParticipationsByCustomer(cid);

    rows.Select(r => r.GiveawayId).Should().Equal(new[] { "g3", "g2", "g1" });   // EnteredAt DESC
    rows[0].GiveawayCancelledAt.Should().Be(700);
    rows[1].IsWinner.Should().BeFalse();
    rows[2].IsWinner.Should().BeTrue();
    rows[2].GiveawayEndedAt.Should().Be(500);
}
```

Add `using System.Linq;` if not already in the file.

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryTests" 2>&1 | tail -10
```

Expected: compile error (`GetParticipationsByCustomer`, `CustomerGiveawayRow` not found).

- [ ] **Step 3: Implement GetParticipationsByCustomer + CustomerGiveawayRow**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs`. Add the following method (just before any private helpers / DTOs):

```csharp
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
```

Append to the same file (outside the existing repository class, alongside `GiveawaySessionTotals` and `GiveawaySummary`):

```csharp
/// <summary>UI projection of a customer's giveaway participation row.</summary>
public sealed record CustomerGiveawayRow(
    string GiveawayId,
    string Keyword,
    long EnteredAt,
    bool IsWinner,
    long? GiveawayEndedAt,
    long? GiveawayCancelledAt);
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryTests" 2>&1 | tail -3
```

Expected: all GiveawayRepositoryTests pass.

- [ ] **Step 5: Full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 87/87.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs
git commit -m "feat(core): add GiveawayRepository.GetParticipationsByCustomer"
```

---

### Task 6: CustomerDetailViewModel

**Files:**
- Create: `LiveDeck.App/ViewModels/CustomerDetailViewModel.cs`

**Context:** Mevcut WPF VM pattern (CommunityToolkit.Mvvm `[ObservableProperty]` + `[RelayCommand]`). Tarih formatlama `TrFormats.DateTime`.

- [ ] **Step 1: Create file**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/CustomerDetailViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.App.Formatting;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class CustomerDetailViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;
    private readonly LabelRepository _labels;
    private readonly GiveawayRepository _giveaways;
    private string? _customerId;

    [ObservableProperty] private string _username = "";
    [ObservableProperty] private string _platform = "";
    [ObservableProperty] private string? _displayName;
    [ObservableProperty] private string _firstSeenLabel = "";
    [ObservableProperty] private string _lastSeenLabel  = "";
    [ObservableProperty] private int    _totalLabelsPrinted;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private bool   _isBlacklisted;
    [ObservableProperty] private string? _blacklistReason;
    [ObservableProperty] private string _blacklistedAtLabel = "";
    [ObservableProperty] private string _notesEdit = "";

    public ObservableCollection<CustomerLabelRow>    Labels    { get; } = new();
    public ObservableCollection<CustomerGiveawayRow> Giveaways { get; } = new();

    public CustomerDetailViewModel(
        CustomerRepository customers, LabelRepository labels, GiveawayRepository giveaways)
    {
        _customers = customers;
        _labels = labels;
        _giveaways = giveaways;
    }

    /// <summary>Loads customer summary + label/giveaway history. Returns false if customer not found.</summary>
    public bool Load(string customerId)
    {
        var c = _customers.GetById(customerId);
        if (c is null) return false;

        _customerId = customerId;
        Username = c.Username;
        Platform = c.Platform;
        DisplayName = c.DisplayName;
        FirstSeenLabel = TrFormats.DateTime(c.FirstSeenAt);
        LastSeenLabel  = TrFormats.DateTime(c.LastSeenAt);
        TotalLabelsPrinted = c.TotalLabelsPrinted;
        TotalAmount = c.TotalAmount;
        IsBlacklisted = c.IsBlacklisted;
        BlacklistReason = c.BlacklistReason;
        BlacklistedAtLabel = c.BlacklistedAt is long t ? TrFormats.DateTime(t) : "";
        NotesEdit = c.Notes ?? "";

        Labels.Clear();
        foreach (var l in _labels.GetByCustomer(customerId)) Labels.Add(l);

        Giveaways.Clear();
        foreach (var g in _giveaways.GetParticipationsByCustomer(customerId)) Giveaways.Add(g);

        return true;
    }

    [RelayCommand]
    private void SaveNotes()
    {
        if (_customerId is null) return;
        _customers.UpdateNotes(_customerId, NotesEdit);
    }
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors. (DI registration happens in Task 13; this file's references to `CustomerLabelRow` and `CustomerGiveawayRow` resolve from `LiveDeck.Core.Storage.Repositories`.)

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/CustomerDetailViewModel.cs
git commit -m "feat(app): add CustomerDetailViewModel (summary + history + notes)"
```

---

### Task 7: CustomerSearchViewModel

**Files:**
- Create: `LiveDeck.App/ViewModels/CustomerSearchViewModel.cs`

**Context:** Synchronous arama, 50 sonuç limit, OnQueryChanged-driven.

- [ ] **Step 1: Create file**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/CustomerSearchViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class CustomerSearchViewModel : ViewModelBase
{
    private readonly CustomerRepository _customers;

    [ObservableProperty] private string _query = "";

    public ObservableCollection<Customer> Results { get; } = new();

    public CustomerSearchViewModel(CustomerRepository customers)
    {
        _customers = customers;
    }

    partial void OnQueryChanged(string value)
    {
        Results.Clear();
        if (string.IsNullOrWhiteSpace(value)) return;
        foreach (var c in _customers.Search(value.Trim(), limit: 50))
            Results.Add(c);
    }
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/CustomerSearchViewModel.cs
git commit -m "feat(app): add CustomerSearchViewModel (live username substring search)"
```

---

### Task 8: CustomerDetailDialog (XAML + code-behind) + status converter

**Files:**
- Create: `LiveDeck.App/Views/CustomerDetailDialog.xaml`
- Create: `LiveDeck.App/Views/CustomerDetailDialog.xaml.cs`
- Create: `LiveDeck.App/Converters/GiveawayParticipationStatusConverter.cs`
- Modify: `LiveDeck.App/App.xaml`

**Context:** `Status` sütunu (`CustomerGiveawayRow` → "🏆 Kazandı" / "Katıldı" / "İptal edildi") için converter. `UnixToDateConverter` zaten App.xaml'da kayıtlı; `BoolToVisibleConverter`/`CountToVisibleConverter` Faz 2b/3d'den var.

- [ ] **Step 1: Create GiveawayParticipationStatusConverter**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Converters/GiveawayParticipationStatusConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows.Data;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.Converters;

/// <summary>Maps a CustomerGiveawayRow to a localized status text.
/// IsWinner → "🏆 Kazandı"; cancelled giveaway → "İptal edildi"; otherwise "Katıldı".</summary>
public sealed class GiveawayParticipationStatusConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not CustomerGiveawayRow row) return "";
        if (row.IsWinner) return "🏆 Kazandı";
        if (row.GiveawayCancelledAt is not null) return "İptal edildi";
        return "Katıldı";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 2: Register converter in App.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/App.xaml`. Add the new converter alongside the existing entries inside `<ResourceDictionary>`:

```xml
<converters:GiveawayParticipationStatusConverter x:Key="GiveawayParticipationStatusConverter"/>
```

- [ ] **Step 3: Create CustomerDetailDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/CustomerDetailDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.CustomerDetailDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Müşteri Detayı" Width="720" Height="600"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <!-- Summary card -->
        <Border Grid.Row="0" Background="#FF222222" Padding="16" CornerRadius="6" Margin="0,0,0,12">
            <Grid>
                <Grid.ColumnDefinitions>
                    <ColumnDefinition Width="*"/>
                    <ColumnDefinition Width="Auto"/>
                </Grid.ColumnDefinitions>

                <StackPanel Grid.Column="0">
                    <TextBlock Text="{Binding Username}" FontSize="22" FontWeight="Bold"
                               Foreground="#FFFFD166"/>
                    <TextBlock Text="{Binding Platform}" FontSize="14" Foreground="#FFAAAAAA"
                               Margin="0,2,0,0"/>
                    <TextBlock Text="{Binding DisplayName}" FontSize="14" Margin="0,4,0,0"
                               Visibility="{Binding DisplayName, Converter={StaticResource NullToCollapsedConverter}}"/>
                    <TextBlock Margin="0,8,0,0">
                        <Run Text="İlk görülme:"      Foreground="#FFAAAAAA"/>
                        <Run Text="{Binding FirstSeenLabel, Mode=OneWay}"/>
                    </TextBlock>
                    <TextBlock>
                        <Run Text="Son görülme:"      Foreground="#FFAAAAAA"/>
                        <Run Text="{Binding LastSeenLabel, Mode=OneWay}"/>
                    </TextBlock>
                </StackPanel>

                <StackPanel Grid.Column="1" HorizontalAlignment="Right">
                    <TextBlock>
                        <Run Text="📦"/>
                        <Run Text="{Binding TotalLabelsPrinted, Mode=OneWay}" FontWeight="Bold"/>
                        <Run Text="etiket"/>
                    </TextBlock>
                    <TextBlock>
                        <Run Text="💰"/>
                        <Run Text="{Binding TotalAmount, Mode=OneWay, StringFormat={}{0:N2} TL}"
                             FontWeight="Bold" Foreground="#FFFFD166"/>
                    </TextBlock>
                    <StackPanel Visibility="{Binding IsBlacklisted, Converter={StaticResource BoolToVisibleConverter}}"
                                Margin="0,8,0,0">
                        <TextBlock Text="🚫 Kara listede" Foreground="#FFFF6666" FontWeight="Bold"/>
                        <TextBlock Text="{Binding BlacklistReason}" Foreground="#FFAAAAAA"
                                   TextWrapping="Wrap" MaxWidth="200"/>
                        <TextBlock Text="{Binding BlacklistedAtLabel}" Foreground="#FFAAAAAA" FontSize="11"/>
                    </StackPanel>
                </StackPanel>
            </Grid>
        </Border>

        <!-- Tabs -->
        <TabControl Grid.Row="1" Background="Transparent">
            <TabItem Header="Etiket Geçmişi">
                <Grid>
                    <DataGrid ItemsSource="{Binding Labels}" AutoGenerateColumns="False" IsReadOnly="True"
                              HeadersVisibility="Column"
                              Background="#FF1A1A1A" Foreground="White"
                              BorderBrush="#FF333333"
                              RowBackground="#FF1A1A1A"
                              AlternatingRowBackground="#FF222222">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="Tarih"
                                                Binding="{Binding AddedAt, Converter={StaticResource UnixToDateConverter}}"
                                                Width="*"/>
                            <DataGridTextColumn Header="Kod"     Binding="{Binding Code}"        Width="80"/>
                            <DataGridTextColumn Header="Mesaj"   Binding="{Binding MessageText}" Width="3*"/>
                            <DataGridTextColumn Header="Fiyat"
                                                Binding="{Binding Price, StringFormat={}{0:N2} TL}"
                                                Width="100"/>
                            <DataGridCheckBoxColumn Header="Yazdırıldı" Binding="{Binding IsPrinted}" Width="80"/>
                        </DataGrid.Columns>
                    </DataGrid>
                    <TextBlock Text="Henüz etiket yok"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               Foreground="#FF666666" FontSize="14"
                               Visibility="{Binding Labels.Count, Converter={StaticResource CountToCollapsedConverter}}"/>
                </Grid>
            </TabItem>

            <TabItem Header="Çekiliş Geçmişi">
                <Grid>
                    <DataGrid ItemsSource="{Binding Giveaways}" AutoGenerateColumns="False" IsReadOnly="True"
                              HeadersVisibility="Column"
                              Background="#FF1A1A1A" Foreground="White"
                              BorderBrush="#FF333333"
                              RowBackground="#FF1A1A1A"
                              AlternatingRowBackground="#FF222222">
                        <DataGrid.Columns>
                            <DataGridTextColumn Header="Tarih"
                                                Binding="{Binding EnteredAt, Converter={StaticResource UnixToDateConverter}}"
                                                Width="*"/>
                            <DataGridTextColumn Header="Anahtar Kelime" Binding="{Binding Keyword}" Width="2*"/>
                            <DataGridTextColumn Header="Durum"
                                                Binding="{Binding ., Converter={StaticResource GiveawayParticipationStatusConverter}}"
                                                Width="*"/>
                        </DataGrid.Columns>
                    </DataGrid>
                    <TextBlock Text="Henüz çekilişe katılmadı"
                               HorizontalAlignment="Center" VerticalAlignment="Center"
                               Foreground="#FF666666" FontSize="14"
                               Visibility="{Binding Giveaways.Count, Converter={StaticResource CountToCollapsedConverter}}"/>
                </Grid>
            </TabItem>

            <TabItem Header="Notlar">
                <Grid Margin="0,8,0,0">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>
                    <TextBox Grid.Row="0"
                             Text="{Binding NotesEdit, UpdateSourceTrigger=PropertyChanged}"
                             AcceptsReturn="True" TextWrapping="Wrap" VerticalScrollBarVisibility="Auto"
                             Padding="8" FontSize="14"
                             Background="#FF222222" Foreground="White" BorderBrush="#FF333333"/>
                    <Button Grid.Row="1" Content="Kaydet" Command="{Binding SaveNotesCommand}"
                            Padding="14,6" Margin="0,8,0,0" HorizontalAlignment="Right"/>
                </Grid>
            </TabItem>
        </TabControl>

        <!-- Footer -->
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Kapat" Click="OnClose" Padding="14,6"
                    IsCancel="True" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

The XAML uses a converter `CountToCollapsedConverter` for the empty-state TextBlock — inverse of the existing `CountToVisibleConverter`. We need to add this.

- [ ] **Step 4: Add CountToCollapsedConverter**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Converters/CountToVisibleConverter.cs`. Append a second class to the same file:

```csharp
/// <summary>int (or long) > 0 → Collapsed, otherwise Visible. Inverse of <see cref="CountToVisibleConverter"/>.</summary>
public sealed class CountToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is int i)  return i > 0 ? Visibility.Collapsed : Visibility.Visible;
        if (value is long l) return l > 0 ? Visibility.Collapsed : Visibility.Visible;
        return Visibility.Visible;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Register it in `App.xaml` under `ResourceDictionary`:

```xml
<converters:CountToCollapsedConverter x:Key="CountToCollapsedConverter"/>
```

- [ ] **Step 5: Create CustomerDetailDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/CustomerDetailDialog.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;

namespace LiveDeck.App.Views;

public partial class CustomerDetailDialog : Window
{
    private readonly CustomerDetailViewModel _vm;

    public CustomerDetailDialog(CustomerDetailViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    /// <summary>Loads the customer and shows the dialog. Returns false if customer not found.</summary>
    public bool Open(string customerId)
    {
        if (!_vm.Load(customerId))
        {
            MessageBox.Show(
                "Müşteri kaydı bulunamadı.",
                "Müşteri yok",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }
        ShowDialog();
        return true;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
```

- [ ] **Step 6: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -5
```

Expected: 0 errors. (Dialog DI registration happens in Task 13.)

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/CustomerDetailDialog.xaml LiveDeck.App/Views/CustomerDetailDialog.xaml.cs LiveDeck.App/Converters/GiveawayParticipationStatusConverter.cs LiveDeck.App/Converters/CountToVisibleConverter.cs LiveDeck.App/App.xaml
git commit -m "feat(app): add CustomerDetailDialog (summary + tabs for labels/giveaways/notes)"
```

---

### Task 9: CustomerSearchDialog (XAML + code-behind)

**Files:**
- Create: `LiveDeck.App/Views/CustomerSearchDialog.xaml`
- Create: `LiveDeck.App/Views/CustomerSearchDialog.xaml.cs`

**Context:** Çift tıkla `CustomerDetailDialog` aç. Dialog kendisi `App.Host.Services.GetRequiredService<CustomerDetailDialog>()` ile detail dialog'unu açar.

- [ ] **Step 1: Create CustomerSearchDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/CustomerSearchDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.CustomerSearchDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Müşteriler" Width="480" Height="520"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBox Grid.Row="0"
                 Text="{Binding Query, UpdateSourceTrigger=PropertyChanged}"
                 Padding="6" FontSize="14"
                 Background="#FF222222" Foreground="White" BorderBrush="#FF333333"/>

        <Grid Grid.Row="1" Margin="0,12,0,0">
            <ListBox x:Name="ResultsList"
                     ItemsSource="{Binding Results}"
                     MouseDoubleClick="ResultsList_OnDoubleClick"
                     Background="#FF1A1A1A" Foreground="White" BorderBrush="#FF333333">
                <ListBox.ItemTemplate>
                    <DataTemplate>
                        <Grid Margin="0,4">
                            <Grid.ColumnDefinitions>
                                <ColumnDefinition Width="2*"/>
                                <ColumnDefinition Width="*"/>
                                <ColumnDefinition Width="Auto"/>
                            </Grid.ColumnDefinitions>
                            <TextBlock Grid.Column="0" Text="{Binding Username}" FontWeight="Bold"
                                       Foreground="#FFFFD166"/>
                            <TextBlock Grid.Column="1" Text="{Binding Platform}" Foreground="#FFAAAAAA"/>
                            <TextBlock Grid.Column="2"
                                       Text="{Binding LastSeenAt, Converter={StaticResource UnixToDateConverter}}"
                                       Foreground="#FF888888" FontSize="11"/>
                        </Grid>
                    </DataTemplate>
                </ListBox.ItemTemplate>
            </ListBox>
            <TextBlock Text="Sonuç yok"
                       HorizontalAlignment="Center" VerticalAlignment="Center"
                       Foreground="#FF666666" FontSize="14"
                       Visibility="{Binding Results.Count, Converter={StaticResource CountToCollapsedConverter}}"/>
        </Grid>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Kapat" Click="OnClose" Padding="14,6"
                    IsCancel="True" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: Create CustomerSearchDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/CustomerSearchDialog.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Customers;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class CustomerSearchDialog : Window
{
    public CustomerSearchDialog(CustomerSearchViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void ResultsList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is not Customer selected) return;
        var detail = App.Host.Services.GetRequiredService<CustomerDetailDialog>();
        detail.Owner = this;
        detail.Open(selected.Id);
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/CustomerSearchDialog.xaml LiveDeck.App/Views/CustomerSearchDialog.xaml.cs
git commit -m "feat(app): add CustomerSearchDialog (live username search + open detail)"
```

---

### Task 10: MainShell — chat + queue context menu items + ⋮ "Müşteriler" + commands

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`
- Modify: `LiveDeck.App/Views/MainShellView.xaml`

**Context:** Üç yeni RelayCommand + üç yeni MenuItem. `OpenCustomerDetailFromChat` chat'teki seçili kullanıcının `Customer` kaydını arar (yoksa MessageBox); `OpenCustomerDetailFromQueue` `LabelViewModel.CustomerId`'i kullanır; `OpenCustomerSearch` arama dialog'unu açar.

- [ ] **Step 1: Add commands to MainShellViewModel**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`. Locate the existing `[RelayCommand] private void AddQueueRowToBlacklist(...)` method. Just below it, add:

```csharp
[RelayCommand]
private void OpenCustomerDetailFromChat(ChatMessageViewModel? msg)
{
    if (msg is null) return;
    var customer = _customerRepo.FindByPlatformAndUsername(msg.Platform, msg.Username);
    if (customer is null)
    {
        MessageBox.Show("Bu kullanıcı henüz kayıtlı değil.", "Müşteri yok",
            MessageBoxButton.OK, MessageBoxImage.Information);
        return;
    }
    ShowCustomerDetail(customer.Id);
}

[RelayCommand]
private void OpenCustomerDetailFromQueue(LabelViewModel? row)
{
    if (row is null) return;
    ShowCustomerDetail(row.CustomerId);
}

[RelayCommand]
private void OpenCustomerSearch()
{
    var dlg = App.Host.Services.GetRequiredService<Views.CustomerSearchDialog>();
    dlg.Owner = Application.Current?.MainWindow;
    dlg.ShowDialog();
}

private static void ShowCustomerDetail(string customerId)
{
    var dlg = App.Host.Services.GetRequiredService<Views.CustomerDetailDialog>();
    dlg.Owner = Application.Current?.MainWindow;
    dlg.Open(customerId);
}
```

- [ ] **Step 2: Add MenuItems to MainShellView.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml`.

**(a) Chat ContextMenu:** Find the existing chat ListBox `ContextMenu` block (`<MenuItem Header="Kara Listeye Al…" ... AddChatSenderToBlacklistCommand ...>`). Insert a new MenuItem before it:

```xml
<MenuItem Header="Müşteri Detayı"
          Command="{Binding PlacementTarget.DataContext.OpenCustomerDetailFromChatCommand,
                            RelativeSource={RelativeSource Self}}"
          CommandParameter="{Binding PlacementTarget.SelectedItem,
                                     RelativeSource={RelativeSource Self}}"/>
```

**(b) Queue ContextMenu:** Find the existing queue ListBox `ContextMenu` (the one with `AddQueueRowToBlacklistCommand`). Insert a new MenuItem before it:

```xml
<MenuItem Header="Müşteri Detayı"
          Command="{Binding PlacementTarget.DataContext.OpenCustomerDetailFromQueueCommand,
                            RelativeSource={RelativeSource Self}}"
          CommandParameter="{Binding PlacementTarget.SelectedItem,
                                     RelativeSource={RelativeSource Self}}"/>
```

**(c) ⋮ menu:** Find the existing MenuButton ContextMenu (with "Ayarlar", "Yayın Geçmişi", "Kara Liste"). Add a new MenuItem after "Kara Liste":

```xml
<MenuItem Header="Müşteriler"
          Command="{Binding OpenCustomerSearchCommand}"/>
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs LiveDeck.App/Views/MainShellView.xaml
git commit -m "feat(app): wire customer detail context menus + ⋮ Müşteriler"
```

---

### Task 11: BlacklistDialog button column + VM command

**Files:**
- Modify: `LiveDeck.App/ViewModels/BlacklistViewModel.cs`
- Modify: `LiveDeck.App/Views/BlacklistDialog.xaml`

**Context:** Mevcut DataGrid'e "…" buton kolonu eklenir. Buton tıklayınca `BlacklistViewModel.OpenCustomerDetailCommand(customerId)` ile detail dialog açılır.

- [ ] **Step 1: Read BlacklistViewModel for context**

```bash
cd /c/Users/burak/source/repos/LiveDeck
head -40 LiveDeck.App/ViewModels/BlacklistViewModel.cs
```

Confirm the DataGrid items have `Customer.Id` accessible (`BlacklistRow` or directly `Customer`). For this plan we assume the bound row exposes the customer's `Id` as a `CustomerId` property; if it's `Id` instead, adjust the binding path in Step 3 accordingly.

- [ ] **Step 2: Add OpenCustomerDetail command to BlacklistViewModel**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/BlacklistViewModel.cs`. Add at the bottom of the class (just before the closing `}`):

```csharp
[RelayCommand]
private void OpenCustomerDetail(string? customerId)
{
    if (string.IsNullOrEmpty(customerId)) return;
    var dlg = App.Host.Services.GetRequiredService<Views.CustomerDetailDialog>();
    dlg.Owner = Application.Current?.MainWindow;
    dlg.Open(customerId);
}
```

Add the necessary `using` declarations at the top of the file if missing:

```csharp
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
```

- [ ] **Step 3: Add button column to BlacklistDialog.xaml**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/BlacklistDialog.xaml`. In the existing `<DataGrid.Columns>` block, append a new column after the "Tarih" column:

```xml
<DataGridTemplateColumn Header="" Width="40">
    <DataGridTemplateColumn.CellTemplate>
        <DataTemplate>
            <Button Content="…" Padding="4,0" FontWeight="Bold"
                    Command="{Binding DataContext.OpenCustomerDetailCommand,
                                      RelativeSource={RelativeSource AncestorType=Window}}"
                    CommandParameter="{Binding Id}"/>
        </DataTemplate>
    </DataGridTemplateColumn.CellTemplate>
</DataGridTemplateColumn>
```

The button binds its `CommandParameter` to `{Binding Id}` — this is the bound row's Customer.Id. If the data source is a different shape (e.g., `BlacklistRow` with `CustomerId`), change to `{Binding CustomerId}`.

- [ ] **Step 4: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/BlacklistViewModel.cs LiveDeck.App/Views/BlacklistDialog.xaml
git commit -m "feat(app): add ⋯ button to blacklist rows opening customer detail"
```

---

### Task 12: AppHost DI registration

**Files:**
- Modify: `LiveDeck.App/AppHost.cs`

**Context:** ViewModel ve Dialog transient olarak kayıt edilir (her açılış taze instance).

- [ ] **Step 1: Register new services**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs`. Locate the existing block of `services.AddTransient<Views....>()` registrations (StreamReportDialog, SettingsDialog, etc.). Add:

```csharp
// Customer center (Phase 3a)
services.AddTransient<ViewModels.CustomerDetailViewModel>();
services.AddTransient<ViewModels.CustomerSearchViewModel>();
services.AddTransient<Views.CustomerDetailDialog>();
services.AddTransient<Views.CustomerSearchDialog>();
```

- [ ] **Step 2: Build whole solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
```

Expected: 0 errors.

- [ ] **Step 3: Run full test suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 87/87 pass.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs
git commit -m "feat(app): register customer detail + search VMs and dialogs in DI"
```

---

### Task 13: Manual Acceptance Smoke

**Files:** None — execute and observe.

**Context:** 4 erişim noktası + notes persistence + summary content. 6 senaryo.

- [ ] **Step 1: Start app cleanly**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

Verify: log shows `Schema version: 5`. App opens without errors.

- [ ] **Step 2: Chat → Müşteri Detayı**

- Click "Yayın Başlat".
- Send a chat message from `@testuser` (real or simulated via browser extension test harness).
- In the chat ListBox, right-click on `@testuser`'s message → "Müşteri Detayı".

**Expected:** Dialog opens. Summary shows `@testuser`, instagram (or whichever platform), 0 etiket, 0,00 TL. Etiket Geçmişi tab empty with "Henüz etiket yok". Çekiliş Geçmişi tab empty with "Henüz çekilişe katılmadı".

- [ ] **Step 3: Add a label, then queue → Müşteri Detayı**

- Close the detail dialog.
- Double-click `@testuser`'s chat message to add it to the print queue (with a price like `100,50`).
- In the queue, right-click the new row → "Müşteri Detayı".

**Expected:** Same dialog opens, now showing 1 etiket in summary. Etiket Geçmişi tab has 1 row with the message text and `100,50 TL`. The "Yazdırıldı" column shows unchecked (label is queued, not printed).

- [ ] **Step 4: Add a giveaway win**

- Close detail dialog.
- Click "🎁 Çekiliş", start a 10-second giveaway with keyword `test`.
- Have `@testuser` send "test" in chat.
- Wait for the auto-draw or click "Şimdi Çek".
- Open `@testuser`'s detail dialog again (chat right-click).

**Expected:** Çekiliş Geçmişi tab now has 1 row, status "🏆 Kazandı" (if @testuser was the only/lucky participant) or "Katıldı".

- [ ] **Step 5: Notes persistence**

- In the Notlar tab, type "VIP, hızlı ödeme yapar".
- Click "Kaydet".
- Close the dialog.
- Re-open `@testuser`'s detail (any access path).

**Expected:** Notlar tab shows the saved text.

- [ ] **Step 6: ⋮ → Müşteriler**

- Close detail dialog.
- Click ⋮ → "Müşteriler".
- Type `test` in the search box.

**Expected:** Search dialog opens; results show `@testuser`. Double-click → detail dialog opens. Close both dialogs.

- [ ] **Step 7: Kara liste → "…" button**

- Click ⋮ → "Kara Liste".
- Add `@testuser` to blacklist with reason "Test sebep".
- Close the AddToBlacklist dialog. The kara liste DataGrid shows `@testuser`.
- Click the "…" button on the row.

**Expected:** Detail dialog opens; summary shows "🚫 Kara listede" with reason "Test sebep" and the timestamp.

- [ ] **Step 8: Commit smoke log (optional)**

If a manual smoke log is kept, record the date, OS locale, .NET version, and tick each step:

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add docs/smoke-tests/2026-04-28-phase-3a-smoke.md
git commit -m "docs: phase 3a customer center smoke test results"
```

---

## Self-Review

**Spec coverage check:**

| Spec section | Plan task |
|---|---|
| §2 Mimari etkisi (dosya tablosu) | Tasks 1-12 |
| §3.1 Migration 005 | Task 1 |
| §3.2 Customer entity 13 alan | Task 2 |
| §3.3 Repo metodları (UpdateNotes, Search) | Task 3 |
| §3.3 Repo metodları (GetByCustomer + CustomerLabelRow) | Task 4 |
| §3.3 Repo metodları (GetParticipationsByCustomer + CustomerGiveawayRow) | Task 5 |
| §3.4 CustomerDetailViewModel | Task 6 |
| §3.5 CustomerDetailDialog | Task 8 |
| §3.6 CustomerSearchViewModel + Dialog | Tasks 7 + 9 |
| §3.7 4 erişim noktası | Tasks 10 + 11 |
| §3.8 AppHost DI | Task 12 |
| §4 Hata yönetimi | Distributed (MessageBox in chat path Task 10; Open returns false in Task 8 dialog code-behind; SaveNotes guard in Task 6) |
| §5 Test stratejisi | Tasks 1, 3, 4, 5 add tests; Task 13 manual smoke |
| §6 YAGNI | Plan refrains: no avatar upload, no notes history, no Excel export, no async search, no detail-from-add-to-blacklist |
| §8 Kabul kriterleri | Task 13 |

All sections covered.

**Placeholder scan:** Searched for "TBD", "TODO", "implement later", "fill in", "appropriate". None found. Every code step has actual code.

**Type consistency check:**

- `Customer` 13-field signature defined Task 2; consumed by all subsequent tasks. Field names match: `IsBlacklisted`, `BlacklistReason`, `Notes`, `TotalLabelsPrinted`, `TotalAmount`, `BlacklistedAt`.
- `CustomerLabelRow` defined Task 4 (in `LabelRepository.cs`); consumed by Task 6 (`CustomerDetailViewModel.Labels`) and Task 8 XAML (DataGrid binds to `AddedAt`, `Code`, `MessageText`, `Price`, `IsPrinted`). All consistent.
- `CustomerGiveawayRow` defined Task 5; consumed by Task 6 (`Giveaways`), Task 8 (DataGrid + status converter binds `EnteredAt`, `Keyword`, `IsWinner`, `GiveawayCancelledAt`). Consistent.
- `CustomerRepository.UpdateNotes(string customerId, string? notes)` defined Task 3, consumed Task 6 (`SaveNotes`). Consistent.
- `CustomerRepository.Search(string usernameContains, int limit = 50)` defined Task 3, consumed Task 7. Consistent.
- `LabelRepository.GetByCustomer(string customerId)` defined Task 4, consumed Task 6. Consistent.
- `GiveawayRepository.GetParticipationsByCustomer(string customerId)` defined Task 5, consumed Task 6. Consistent.
- `CustomerDetailViewModel.Load(string customerId)` returns bool — Task 6 definition matches Task 8 dialog `Open()` consumer.
- `CustomerSearchViewModel.Query` / `Results` defined Task 7, bound by Task 9 XAML.
- `MainShellViewModel.OpenCustomerDetailFromChatCommand`, `OpenCustomerDetailFromQueueCommand`, `OpenCustomerSearchCommand` — defined Task 10, consumed by Task 10 XAML.
- `BlacklistViewModel.OpenCustomerDetailCommand` — defined Task 11, consumed by Task 11 XAML.
- `GiveawayParticipationStatusConverter` — defined Task 8, consumed Task 8 XAML.
- `CountToCollapsedConverter` — defined Task 8 (added to existing `CountToVisibleConverter.cs`), consumed Task 8 + 9 XAML.

All consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-3a-customer-center.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Hangisi?
