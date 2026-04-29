# LiveDeck Phase 2a — Multi-Platform + Admin UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add TikTok chat ingestion + three admin dialogs (Settings, Yayın Geçmişi, Kara Liste) accessible from a ⋮ menu in the existing single-screen UI.

**Architecture:** Browser extension gains a TikTok content script (flat ExtensionMessage format, same bridge port). C# side renames the platform-agnostic ingestor and adds `Customer.BlacklistedAt` (migration 003) plus management UI. Three new modal dialogs hang off a context menu button in the top bar.

**Tech Stack:** .NET 10 WPF (existing), Dapper, ClosedXML (existing), CommunityToolkit.Mvvm. New: `System.Drawing.Text.InstalledFontCollection` for the font dropdown, `System.Drawing.Printing.PrinterSettings.InstalledPrinters` for the printer dropdown.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-2a state:** P1b commit `5a9f05b` + spec commit `fb88422`. 39/39 tests passing. Manual smoke OK.

---

## Task Index

**Data layer (1-2):** Schema migration + repo/service blacklist methods
**Chat ingestion (3-4):** Rename Instagram→ChatBridge ingestor + TikTok content script port
**UI wrappers (5):** ChatMessageViewModel + LabelViewModel for blacklist binding
**Admin dialogs (6-9):** Settings + Stream History + Blacklist + Add-to-Blacklist popover
**Shell wiring (10-11):** ⋮ menu button + right-click context + AppHost registrations
**Acceptance (12):** Manual smoke test

---

## Data Layer

### Task 1: Migration 003 + Customer.BlacklistedAt + repository support

**Files:**
- Create: `LiveDeck.Core/Storage/Migrations/003_blacklist_timestamp.sql`
- Modify: `LiveDeck.Core/Customers/Customer.cs`
- Modify: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Modify: `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`
- Modify: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`

- [ ] **Step 1: Migration SQL**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Migrations/003_blacklist_timestamp.sql`:

```sql
-- Phase 2a: track when a customer was blacklisted.
ALTER TABLE Customer ADD COLUMN BlacklistedAt INTEGER;

UPDATE _meta SET SchemaVersion = 3 WHERE Id = 1;
```

- [ ] **Step 2: Add `BlacklistedAt` to Customer record**

Replace the entire content of `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Customers/Customer.cs`:

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
    int TotalOrders,
    int CompletedOrders,
    int CancelledOrders,
    int TrustScore,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes,
    int TotalLabelsPrinted,
    decimal TotalAmount,
    long? BlacklistedAt);
```

- [ ] **Step 3: Update MigrationRunnerTests for version 3**

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
    public void Run_creates_label_aggregates_blacklist_at_version_3()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();
        tables.Should().Contain(new[] { "Customer", "Label", "Settings", "StreamSession", "_meta" });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(3);

        var customerColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Customer')").AsList();
        customerColumns.Should().Contain(new[] { "TotalLabelsPrinted", "TotalAmount", "BlacklistedAt" });
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
        version.Should().Be(3);
    }
}
```

- [ ] **Step 4: Update CustomerRepository**

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
```

- [ ] **Step 5: Update CustomerRepositoryTests**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class CustomerRepositoryTests
{
    private static Customer NewCustomer(string id = "c1") =>
        new(id, "instagram", "@ayse_y", "Ayşe", null,
            FirstSeenAt: 1000, LastSeenAt: 1000,
            TotalOrders: 0, CompletedOrders: 0, CancelledOrders: 0,
            TrustScore: 100, IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null);

    [Fact]
    public void Insert_then_FindByPlatformAndUsername_returns_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(NewCustomer());

        var found = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        found.Should().NotBeNull();
        found!.Id.Should().Be("c1");
        found.IsBlacklisted.Should().BeFalse();
        found.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void FindByPlatformAndUsername_returns_null_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.FindByPlatformAndUsername("instagram", "@nonexistent").Should().BeNull();
    }

    [Fact]
    public void IncrementLabelStats_adds_count_and_amount_and_lastSeen()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());

        repo.IncrementLabelStats("c1", labelDelta: 2, amountDelta: 250m, lastSeenAt: 5000);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y");
        fresh!.TotalLabelsPrinted.Should().Be(2);
        fresh.TotalAmount.Should().Be(250m);
        fresh.LastSeenAt.Should().Be(5000);
    }

    [Fact]
    public void UpdateBlacklist_sets_flag_reason_and_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());

        repo.UpdateBlacklist("c1", isBlacklisted: true, reason: "Ödemedi", blacklistedAt: 9000);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        fresh.IsBlacklisted.Should().BeTrue();
        fresh.BlacklistReason.Should().Be("Ödemedi");
        fresh.BlacklistedAt.Should().Be(9000);
    }

    [Fact]
    public void UpdateBlacklist_can_clear_flag_and_reason()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        repo.Insert(NewCustomer());
        repo.UpdateBlacklist("c1", isBlacklisted: true, reason: "test", blacklistedAt: 9000);

        repo.UpdateBlacklist("c1", isBlacklisted: false, reason: null, blacklistedAt: null);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        fresh.IsBlacklisted.Should().BeFalse();
        fresh.BlacklistReason.Should().BeNull();
        fresh.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void GetBlacklisted_returns_only_blacklisted_newest_first()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);

        repo.Insert(NewCustomer("c1"));
        repo.Insert(NewCustomer("c2") with { Username = "@b" });
        repo.Insert(NewCustomer("c3") with { Username = "@c" });

        repo.UpdateBlacklist("c1", true, "r1", 1000);
        repo.UpdateBlacklist("c3", true, "r3", 3000);

        var list = repo.GetBlacklisted();
        list.Should().HaveCount(2);
        list[0].Id.Should().Be("c3");   // newest first
        list[1].Id.Should().Be("c1");
    }
}
```

- [ ] **Step 6: Build + run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -3
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~Customer|FullyQualifiedName~Migration" 2>&1 | tail -3
```
Expected: build clean (or single error in CustomerService.GetOrCreate which Task 2 fixes), tests for migration + customer repo all pass.

If `CustomerService.GetOrCreate` errors due to the new `BlacklistedAt` parameter, that's expected — Task 2 fixes it. Don't try to fix here.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Migrations/003_blacklist_timestamp.sql LiveDeck.Core/Customers/Customer.cs LiveDeck.Core/Storage/Repositories/CustomerRepository.cs LiveDeck.Tests/Storage/MigrationRunnerTests.cs LiveDeck.Tests/Storage/CustomerRepositoryTests.cs
git commit -m "feat(core): add Customer.BlacklistedAt (migration 003) + repository methods"
```

---

### Task 2: CustomerService blacklist methods (TDD)

**Files:**
- Modify: `LiveDeck.Core/Customers/CustomerService.cs`
- Modify: `LiveDeck.Tests/Customers/CustomerServiceTests.cs`

- [ ] **Step 1: Update CustomerServiceTests**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Customers/CustomerServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Tests.TestHelpers;
using Moq;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class CustomerServiceTests
{
    [Fact]
    public void GetOrCreate_creates_customer_with_zero_aggregates()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = new CustomerService(repo, clock);

        var customer = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        customer.TotalLabelsPrinted.Should().Be(0);
        customer.TotalAmount.Should().Be(0m);
        customer.IsBlacklisted.Should().BeFalse();
        customer.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void GetOrCreate_returns_existing_on_second_call()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1234L);
        var svc = new CustomerService(repo, clock);

        var first  = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);
        var second = svc.GetOrCreate("instagram", "@ayse_y", "Ayşe", null);

        second.Id.Should().Be(first.Id);
    }

    [Fact]
    public void RecordPrintedLabels_bumps_aggregates()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 5000L);
        var svc = new CustomerService(repo, clock);
        var c = svc.GetOrCreate("instagram", "@a", null, null);

        svc.RecordPrintedLabels(c.Id, labelCount: 3, amount: 450m);

        var fresh = repo.FindByPlatformAndUsername("instagram", "@a")!;
        fresh.TotalLabelsPrinted.Should().Be(3);
        fresh.TotalAmount.Should().Be(450m);
    }

    [Fact]
    public void AddToBlacklist_flips_flag_with_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 7000L);
        var svc = new CustomerService(repo, clock);
        var c = svc.GetOrCreate("instagram", "@bad", null, null);

        svc.AddToBlacklist(c.Id, "Ödemedi 3 kez");

        var fresh = repo.GetById(c.Id)!;
        fresh.IsBlacklisted.Should().BeTrue();
        fresh.BlacklistReason.Should().Be("Ödemedi 3 kez");
        fresh.BlacklistedAt.Should().Be(7000L);
    }

    [Fact]
    public void RemoveFromBlacklist_clears_flag_reason_timestamp()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 7000L);
        var svc = new CustomerService(repo, clock);
        var c = svc.GetOrCreate("instagram", "@bad", null, null);
        svc.AddToBlacklist(c.Id, "test");

        svc.RemoveFromBlacklist(c.Id);

        var fresh = repo.GetById(c.Id)!;
        fresh.IsBlacklisted.Should().BeFalse();
        fresh.BlacklistReason.Should().BeNull();
        fresh.BlacklistedAt.Should().BeNull();
    }

    [Fact]
    public void EnsureBlacklistedManual_creates_then_blacklists_when_missing()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 9000L);
        var svc = new CustomerService(repo, clock);

        var c = svc.EnsureBlacklistedManual("tiktok", "@spammer", "Spam");

        c.Platform.Should().Be("tiktok");
        c.Username.Should().Be("@spammer");
        c.IsBlacklisted.Should().BeTrue();
        c.BlacklistReason.Should().Be("Spam");
        c.BlacklistedAt.Should().Be(9000L);
    }

    [Fact]
    public void EnsureBlacklistedManual_blacklists_existing_customer()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CustomerRepository(db);
        var clock = Mock.Of<IClock>(c => c.UnixNow() == 9000L);
        var svc = new CustomerService(repo, clock);
        var existing = svc.GetOrCreate("instagram", "@a", null, null);

        var blacklisted = svc.EnsureBlacklistedManual("instagram", "@a", "Reason");

        blacklisted.Id.Should().Be(existing.Id);
        blacklisted.IsBlacklisted.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Update CustomerService**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Customers/CustomerService.cs`:

```csharp
using System;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Customers;

public sealed class CustomerService
{
    private readonly CustomerRepository _repo;
    private readonly IClock _clock;

    public CustomerService(CustomerRepository repo, IClock clock)
    {
        _repo = repo;
        _clock = clock;
    }

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
            TotalOrders: 0,
            CompletedOrders: 0,
            CancelledOrders: 0,
            TrustScore: 100,
            IsBlacklisted: false,
            BlacklistReason: null,
            Notes: null,
            TotalLabelsPrinted: 0,
            TotalAmount: 0m,
            BlacklistedAt: null);
        _repo.Insert(customer);
        return customer;
    }

    public void RecordPrintedLabels(string customerId, int labelCount, decimal amount)
    {
        _repo.IncrementLabelStats(customerId, labelCount, amount, _clock.UnixNow());
    }

    /// <summary>Marks the customer as blacklisted with optional reason.</summary>
    public void AddToBlacklist(string customerId, string? reason)
    {
        _repo.UpdateBlacklist(customerId, isBlacklisted: true, reason, blacklistedAt: _clock.UnixNow());
    }

    /// <summary>Clears the blacklist flag, reason, and timestamp.</summary>
    public void RemoveFromBlacklist(string customerId)
    {
        _repo.UpdateBlacklist(customerId, isBlacklisted: false, reason: null, blacklistedAt: null);
    }

    /// <summary>
    /// Creates the customer if missing, then blacklists. Returns the post-blacklist record.
    /// Used by the manual "+ Manuel Ekle" flow in the Blacklist dialog.
    /// </summary>
    public Customer EnsureBlacklistedManual(string platform, string username, string? reason)
    {
        var c = GetOrCreate(platform, username, displayName: null, avatarUrl: null);
        AddToBlacklist(c.Id, reason);
        return _repo.GetById(c.Id)!;
    }
}
```

- [ ] **Step 3: Run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~CustomerServiceTests" 2>&1 | tail -3
```
Expected: 7/7 PASS.

- [ ] **Step 4: Run full test suite for regressions**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: all tests pass (~46-48 total now).

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Customers/CustomerService.cs LiveDeck.Tests/Customers/CustomerServiceTests.cs
git commit -m "feat(core): add CustomerService.AddToBlacklist/RemoveFromBlacklist/EnsureBlacklistedManual"
```

---

## Chat Ingestion

### Task 3: Rename InstagramIngestor → ChatBridgeIngestor

**Files:**
- Delete: `LiveDeck.Chat/Ingestors/InstagramIngestor.cs`
- Create: `LiveDeck.Chat/Ingestors/ChatBridgeIngestor.cs`
- Modify: `LiveDeck.App/AppHost.cs` (registration)
- Modify: `LiveDeck.App/App.xaml.cs` (resolution + start/stop call sites)

The Phase 1 `InstagramIngestor` is just a wrapper that starts/stops `ExtensionBridgeServer`. Now that we add TikTok (which flows through the same bridge), the Instagram-specific name is misleading. Rename to `ChatBridgeIngestor` and drop the per-platform marker — platform is per-message, not per-ingestor.

- [ ] **Step 1: Delete InstagramIngestor**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -f LiveDeck.Chat/Ingestors/InstagramIngestor.cs
```

- [ ] **Step 2: Create ChatBridgeIngestor**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Chat/Ingestors/ChatBridgeIngestor.cs`:

```csharp
using System.Threading;
using System.Threading.Tasks;
using LiveDeck.Chat.Bridge;
using LiveDeck.Core.Chat;
using Microsoft.Extensions.Logging;

namespace LiveDeck.Chat.Ingestors;

/// <summary>
/// Platform-agnostic ingestor that starts/stops the <see cref="ExtensionBridgeServer"/>.
/// All decoded chat events flow through the same bridge regardless of platform.
/// </summary>
public sealed class ChatBridgeIngestor : IChatIngestor
{
    private readonly ExtensionBridgeServer _bridge;
    private readonly ILogger<ChatBridgeIngestor> _log;

    /// <summary>Always "all" — the bridge multiplexes platforms.</summary>
    public string Platform => "all";

    public ChatBridgeIngestor(ExtensionBridgeServer bridge, ILogger<ChatBridgeIngestor> log)
    {
        _bridge = bridge;
        _log = log;
    }

    public Task StartAsync(CancellationToken ct)
    {
        _log.LogInformation("ChatBridgeIngestor starting (bridge port {Port})", _bridge.Port);
        return _bridge.StartAsync(ct);
    }

    public Task StopAsync(CancellationToken ct)
    {
        _log.LogInformation("ChatBridgeIngestor stopping");
        return _bridge.StopAsync(ct);
    }
}
```

- [ ] **Step 3: Update AppHost registration**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs`. Find the line:

```csharp
        services.AddSingleton<InstagramIngestor>();
```

Replace with:

```csharp
        services.AddSingleton<ChatBridgeIngestor>();
```

The `using LiveDeck.Chat.Ingestors;` directive at the top of AppHost.cs already covers both names.

- [ ] **Step 4: Update App.xaml.cs**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/App.xaml.cs`. Replace ALL occurrences of `InstagramIngestor` with `ChatBridgeIngestor`. Specifically:

```csharp
private InstagramIngestor? _ingestor;
```

becomes

```csharp
private ChatBridgeIngestor? _ingestor;
```

And

```csharp
_ingestor = Host.Services.GetRequiredService<InstagramIngestor>();
```

becomes

```csharp
_ingestor = Host.Services.GetRequiredService<ChatBridgeIngestor>();
```

- [ ] **Step 5: Build full solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -3
```
Expected: 0 warnings, 0 errors.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add -A
git commit -m "refactor(chat): rename InstagramIngestor to ChatBridgeIngestor (multi-platform)"
```

---

### Task 4: Port TikTok content script + manifest update

**Files:**
- Create: `Extension/content-tiktok.js`
- Modify: `Extension/manifest.json`
- Modify: `Extension/popup.html`

The UniCast extension at `C:/Users/burak/Downloads/UniCast/UniCast/Extension/content-tiktok.js` already has a working TikTok DOM scraper. Copy + adapt to LiveDeck's flat `ExtensionMessage` shape (mirror the P1 Task 25 conversion that was done for Instagram).

- [ ] **Step 1: Read the source first**

Read `C:/Users/burak/Downloads/UniCast/UniCast/Extension/content-tiktok.js` to understand the DOM observer pattern + payload format. Compare to the existing `Extension/content-instagram.js` (already adapted to flat format in P1 Task 25) so the same conversion can be applied to TikTok.

- [ ] **Step 2: Copy file**

```bash
cp /c/Users/burak/Downloads/UniCast/UniCast/Extension/content-tiktok.js \
   /c/Users/burak/source/repos/LiveDeck/Extension/content-tiktok.js
```

- [ ] **Step 3: Apply LiveDeck adaptations**

Open `C:/Users/burak/source/repos/LiveDeck/Extension/content-tiktok.js`. Apply the same adaptations P1 Task 25 applied to `content-instagram.js`:

1. **WebSocket port + path:** any `ws://localhost:NNNN/...` (UniCast port — likely 9876 with path `/tiktok` or `/comments`) → `ws://localhost:4748/extension`
2. **Constants:** `UNICAST_WS_PORT = 9876` → `LIVEDECK_WS_PORT = 4748` (or whatever the const is named)
3. **Window namespace:** `window.__unicastBridge` → `window.__livedeckBridge`
4. **Console prefixes:** `[UniCast]` → `[LiveDeck]`
5. **Payload shape:** wherever UniCast sends a nested envelope like `{ type: 'comment', data: { id, username, text, timestamp, platform } }`, change the `socket.send(JSON.stringify(...))` call to emit a flat `ExtensionMessage`:

```javascript
socket.send(JSON.stringify({
  type: 'chat',
  platform: 'tiktok',
  username: username,
  displayName: username,         // TikTok DOM doesn't surface a separate display name
  avatarUrl: null,
  text: text,
  externalId: id,
  timestamp: Math.floor(Date.now() / 1000)
}));
```

The key invariant: outgoing JSON must match `ExtensionMessage` exactly (camelCase, flat, top-level `type: 'chat'`, `platform: 'tiktok'`). The C# `ExtensionBridgeServer` parses with `PropertyNamingPolicy.CamelCase` and `PropertyNameCaseInsensitive`.

- [ ] **Step 4: Update manifest.json**

Open `C:/Users/burak/source/repos/LiveDeck/Extension/manifest.json`. Modify these three entries (preserve everything else):

- Bump `version` to `"1.1.0"`
- Change `description` to `"Forwards Instagram + TikTok Live chat to LiveDeck"`
- Add `"*://*.tiktok.com/*"` to `host_permissions` array
- Append a second `content_scripts` entry for TikTok

After edits, the file should look like:

```json
{
  "manifest_version": 3,
  "name": "LiveDeck Chat Bridge",
  "version": "1.1.0",
  "description": "Forwards Instagram + TikTok Live chat to LiveDeck",
  "background": { "service_worker": "background.js" },
  "permissions": ["storage"],
  "host_permissions": [
    "*://*.instagram.com/*",
    "*://*.tiktok.com/*"
  ],
  "content_scripts": [
    {
      "matches": ["*://*.instagram.com/*"],
      "js": ["content-instagram.js"],
      "run_at": "document_idle"
    },
    {
      "matches": ["*://*.tiktok.com/*"],
      "js": ["content-tiktok.js"],
      "run_at": "document_idle"
    }
  ],
  "action": { "default_popup": "popup.html" }
}
```

If the existing manifest has an `"icons"` block, preserve it.

- [ ] **Step 5: Update popup.html copy**

Open `C:/Users/burak/source/repos/LiveDeck/Extension/popup.html`. Find any text mentioning only Instagram (e.g., title, usage instructions). Update to mention both platforms, e.g., "Instagram + TikTok Live chat'i LiveDeck'e iletir."

- [ ] **Step 6: JSON syntax check**

```bash
cd /c/Users/burak/source/repos/LiveDeck/Extension
python -c "import json; json.load(open('manifest.json'))" 2>&1 || node -e "JSON.parse(require('fs').readFileSync('manifest.json'))" 2>&1 || echo "Validate JSON by eye"
```
Expected: no output / no error.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add Extension/content-tiktok.js Extension/manifest.json Extension/popup.html
git commit -m "feat(ext): port TikTok content script with flat ExtensionMessage format"
```

---

## UI Wrappers + Settings ViewModel

### Task 5: ChatMessageViewModel + LabelViewModel (blacklist-aware UI wrappers)

**Files:**
- Create: `LiveDeck.App/ViewModels/ChatMessageViewModel.cs`
- Create: `LiveDeck.App/ViewModels/LabelViewModel.cs`

These two thin wrappers expose `IsSenderBlacklisted` for UI binding (red-highlight). The pure-domain `ChatMessage` and `Label` records stay clean — UI concerns live in App-layer wrappers.

- [ ] **Step 1: ChatMessageViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/ChatMessageViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Chat;

namespace LiveDeck.App.ViewModels;

/// <summary>
/// UI-side wrapper around <see cref="ChatMessage"/> that adds <see cref="IsSenderBlacklisted"/>
/// for red-highlight binding in the live chat panel.
/// </summary>
public sealed partial class ChatMessageViewModel : ObservableObject
{
    public ChatMessage Message { get; }

    [ObservableProperty] private bool _isSenderBlacklisted;

    public string Platform => Message.Platform;
    public string Username => Message.Username;
    public string Text     => Message.Text;
    public string Display  => Message.DisplayName ?? Message.Username;

    public ChatMessageViewModel(ChatMessage message, bool isSenderBlacklisted)
    {
        Message = message;
        IsSenderBlacklisted = isSenderBlacklisted;
    }
}
```

- [ ] **Step 2: LabelViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/LabelViewModel.cs`:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using LiveDeck.Core.Sales;

namespace LiveDeck.App.ViewModels;

/// <summary>
/// UI-side wrapper around <see cref="Label"/> for the print-queue panel. Exposes
/// <see cref="IsCustomerBlacklisted"/> so queued rows from blacklisted users get the
/// same red highlight as their chat messages.
/// </summary>
public sealed partial class LabelViewModel : ObservableObject
{
    public Label Label { get; }

    [ObservableProperty] private bool _isCustomerBlacklisted;

    public string Username    => Label.Username;
    public string MessageText => Label.MessageText;
    public decimal Price      => Label.Price;
    public string Id          => Label.Id;
    public string CustomerId  => Label.CustomerId;

    public LabelViewModel(Label label, bool isCustomerBlacklisted)
    {
        Label = label;
        IsCustomerBlacklisted = isCustomerBlacklisted;
    }
}
```

- [ ] **Step 3: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0/0.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/ChatMessageViewModel.cs LiveDeck.App/ViewModels/LabelViewModel.cs
git commit -m "feat(app): add ChatMessageViewModel + LabelViewModel wrappers for blacklist binding"
```

---

### Task 6: SettingsViewModel (drop dead settings, add print + OBS fields)

**Files:**
- Modify: `LiveDeck.Core/Settings/AppSettings.cs` (drop dead parser fields)
- Modify: `LiveDeck.Tests/Settings/SettingsStoreTests.cs` (drop dead-field assertions)
- Create: `LiveDeck.App/ViewModels/SettingsViewModel.cs`

The Settings dialog VM exposes editable bindings + Save/Test commands. We also drop `ParserHighConfidence` and `ParserLowConfidence` from `AppSettings` (P1b removed the engine; these are dead).

- [ ] **Step 1: Drop dead settings from AppSettings**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Settings/AppSettings.cs`:

```csharp
namespace LiveDeck.Core.Settings;

public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";

    // Printing
    public string? PrinterName { get; set; }
    public int LabelWidthMm  { get; set; } = 60;
    public int LabelHeightMm { get; set; } = 30;
    public int LabelGapMm    { get; set; } = 5;
    public string LabelFontFamily { get; set; } = "Arial";
    public int   LabelUserFontSize  { get; set; } = 14;
    public int   LabelMessageFontSize { get; set; } = 12;
}
```

- [ ] **Step 2: Update SettingsStoreTests**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Settings/SettingsStoreTests.cs`:

```csharp
using System.IO;
using FluentAssertions;
using LiveDeck.Core.Settings;
using Xunit;

namespace LiveDeck.Tests.Settings;

public class SettingsStoreTests
{
    private string CreateTempPath() =>
        Path.Combine(Path.GetTempPath(), $"livedeck-test-{System.Guid.NewGuid():N}.json");

    [Fact]
    public void Load_returns_defaults_when_file_missing()
    {
        var store = new SettingsStore(CreateTempPath());
        var settings = store.Load();

        settings.OverlayPort.Should().Be(4747);
        settings.LabelWidthMm.Should().Be(60);
        settings.LabelHeightMm.Should().Be(30);
        settings.LabelFontFamily.Should().Be("Arial");
        settings.PrinterName.Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_round_trips()
    {
        var path = CreateTempPath();
        var store = new SettingsStore(path);
        var original = new AppSettings
        {
            OverlayPort = 5000,
            ChatTheme = "neon",
            PrinterName = "Zebra ZD220",
            LabelWidthMm = 75,
            LabelHeightMm = 40,
            LabelGapMm = 3,
            LabelFontFamily = "Segoe UI",
            LabelUserFontSize = 16,
            LabelMessageFontSize = 13
        };

        store.Save(original);
        var reloaded = store.Load();

        reloaded.OverlayPort.Should().Be(5000);
        reloaded.ChatTheme.Should().Be("neon");
        reloaded.PrinterName.Should().Be("Zebra ZD220");
        reloaded.LabelWidthMm.Should().Be(75);
        reloaded.LabelHeightMm.Should().Be(40);
        reloaded.LabelGapMm.Should().Be(3);
        reloaded.LabelFontFamily.Should().Be("Segoe UI");
        reloaded.LabelUserFontSize.Should().Be(16);
        reloaded.LabelMessageFontSize.Should().Be(13);

        File.Delete(path);
    }
}
```

- [ ] **Step 3: SettingsViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/SettingsViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Drawing.Printing;
using System.Drawing.Text;
using System.Linq;
using System.Runtime.Versioning;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Settings;
using LiveDeck.Labeling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.App.ViewModels;

[SupportedOSPlatform("windows")]
public sealed partial class SettingsViewModel : ViewModelBase
{
    public const string DefaultPrinterSentinel = "(Windows varsayılanı)";

    private readonly AppSettings _liveSettings;
    private readonly SettingsStore _store;
    private readonly int _originalOverlayPort;

    public ObservableCollection<string> AvailablePrinters { get; } = new();
    public ObservableCollection<string> AvailableFonts    { get; } = new();
    public ObservableCollection<string> AvailableThemes   { get; } = new() { "minimal" };

    [ObservableProperty] private string _selectedPrinter = DefaultPrinterSentinel;
    [ObservableProperty] private int    _labelWidthMm;
    [ObservableProperty] private int    _labelHeightMm;
    [ObservableProperty] private int    _labelGapMm;
    [ObservableProperty] private string _labelFontFamily = "Arial";
    [ObservableProperty] private int    _labelUserFontSize;
    [ObservableProperty] private int    _labelMessageFontSize;

    [ObservableProperty] private int    _overlayPort;
    [ObservableProperty] private string _chatTheme = "minimal";

    [ObservableProperty] private string? _validationError;

    /// <summary>True iff Save was called and OverlayPort changed (caller checks for restart prompt).</summary>
    public bool OverlayPortChanged { get; private set; }

    /// <summary>True iff Save committed changes; dialog uses to set DialogResult.</summary>
    public bool Saved { get; private set; }

    public SettingsViewModel(AppSettings settings, SettingsStore store)
    {
        _liveSettings = settings;
        _store = store;
        _originalOverlayPort = settings.OverlayPort;

        LoadFromSettings();
        LoadInstalledPrinters();
        LoadInstalledFonts();
    }

    private void LoadFromSettings()
    {
        SelectedPrinter      = _liveSettings.PrinterName ?? DefaultPrinterSentinel;
        LabelWidthMm         = _liveSettings.LabelWidthMm;
        LabelHeightMm        = _liveSettings.LabelHeightMm;
        LabelGapMm           = _liveSettings.LabelGapMm;
        LabelFontFamily      = _liveSettings.LabelFontFamily;
        LabelUserFontSize    = _liveSettings.LabelUserFontSize;
        LabelMessageFontSize = _liveSettings.LabelMessageFontSize;
        OverlayPort          = _liveSettings.OverlayPort;
        ChatTheme            = _liveSettings.ChatTheme;
    }

    private void LoadInstalledPrinters()
    {
        AvailablePrinters.Clear();
        AvailablePrinters.Add(DefaultPrinterSentinel);
        foreach (string p in PrinterSettings.InstalledPrinters)
            AvailablePrinters.Add(p);
    }

    private void LoadInstalledFonts()
    {
        AvailableFonts.Clear();
        using var fonts = new InstalledFontCollection();
        foreach (var f in fonts.Families.OrderBy(f => f.Name))
            AvailableFonts.Add(f.Name);
    }

    private bool Validate()
    {
        if (OverlayPort < 1024 || OverlayPort > 65535)
        { ValidationError = "Port 1024-65535 arasında olmalı."; return false; }

        if (LabelWidthMm < 10 || LabelWidthMm > 200)
        { ValidationError = "Etiket genişliği 10-200 mm arasında olmalı."; return false; }

        if (LabelHeightMm < 10 || LabelHeightMm > 200)
        { ValidationError = "Etiket yüksekliği 10-200 mm arasında olmalı."; return false; }

        if (LabelGapMm < 0 || LabelGapMm > 50)
        { ValidationError = "Etiket aralığı 0-50 mm arasında olmalı."; return false; }

        if (LabelUserFontSize < 6 || LabelUserFontSize > 72 ||
            LabelMessageFontSize < 6 || LabelMessageFontSize > 72)
        { ValidationError = "Font boyutu 6-72 pt arasında olmalı."; return false; }

        ValidationError = null;
        return true;
    }

    [RelayCommand]
    private void Save()
    {
        if (!Validate()) return;

        // Mutate the live AppSettings instance so dependents (LabelPrinter, OverlayHost)
        // pick up the new values immediately. AppSettings is a class with public setters.
        _liveSettings.PrinterName          = SelectedPrinter == DefaultPrinterSentinel ? null : SelectedPrinter;
        _liveSettings.LabelWidthMm         = LabelWidthMm;
        _liveSettings.LabelHeightMm        = LabelHeightMm;
        _liveSettings.LabelGapMm           = LabelGapMm;
        _liveSettings.LabelFontFamily      = LabelFontFamily;
        _liveSettings.LabelUserFontSize    = LabelUserFontSize;
        _liveSettings.LabelMessageFontSize = LabelMessageFontSize;
        _liveSettings.OverlayPort          = OverlayPort;
        _liveSettings.ChatTheme            = ChatTheme;

        _store.Save(_liveSettings);

        OverlayPortChanged = (OverlayPort != _originalOverlayPort);
        Saved = true;
    }

    [RelayCommand]
    private void TestPrint()
    {
        if (!Validate()) return;

        // Build a temporary AppSettings snapshot using the IN-FORM (unsaved) values.
        var temp = new AppSettings
        {
            PrinterName          = SelectedPrinter == DefaultPrinterSentinel ? null : SelectedPrinter,
            LabelWidthMm         = LabelWidthMm,
            LabelHeightMm        = LabelHeightMm,
            LabelGapMm           = LabelGapMm,
            LabelFontFamily      = LabelFontFamily,
            LabelUserFontSize    = LabelUserFontSize,
            LabelMessageFontSize = LabelMessageFontSize,
            OverlayPort          = OverlayPort,
            ChatTheme            = ChatTheme
        };

        var sample = new Label(
            Id: "test", SessionId: "test", CustomerId: "test",
            Platform: "instagram", Username: "@test",
            MessageText: "Test mesajı",
            Code: null, Price: 100m,
            AddedAt: 0, PrintedAt: null);

        try
        {
            var printer = new LabelPrinter(temp, NullLogger<LabelPrinter>.Instance);
            printer.Print(new[] { sample });
            MessageBox.Show("Test etiketi yazıcıya gönderildi.",
                "Test başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Yazdırma başarısız: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
```

- [ ] **Step 4: Build + run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -3
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~Settings" 2>&1 | tail -3
```
Expected: build clean, settings tests pass (2/2).

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Settings/AppSettings.cs LiveDeck.Tests/Settings/SettingsStoreTests.cs LiveDeck.App/ViewModels/SettingsViewModel.cs
git commit -m "feat(app): add SettingsViewModel + drop dead parser settings"
```

---

## Admin Dialogs

### Task 7: SettingsDialog (tabbed XAML)

**Files:**
- Create: `LiveDeck.App/Views/SettingsDialog.xaml` + `.xaml.cs`

- [ ] **Step 1: SettingsDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/SettingsDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.SettingsDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Ayarlar" Width="540" Height="540"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TabControl Grid.Row="0" Background="#FF1A1A1A" Foreground="White">
            <!-- Yazıcı tab -->
            <TabItem Header="Yazıcı">
                <Grid Margin="12">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="160"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>

                    <TextBlock Grid.Row="0" Grid.Column="0" Text="Yazıcı:" Margin="0,8,8,8"/>
                    <ComboBox  Grid.Row="0" Grid.Column="1"
                               ItemsSource="{Binding AvailablePrinters}"
                               SelectedItem="{Binding SelectedPrinter, UpdateSourceTrigger=PropertyChanged}"
                               Margin="0,4"/>

                    <TextBlock Grid.Row="1" Grid.Column="0" Text="Etiket genişliği (mm):" Margin="0,8,8,8"/>
                    <TextBox   Grid.Row="1" Grid.Column="1"
                               Text="{Binding LabelWidthMm, UpdateSourceTrigger=PropertyChanged}"
                               Padding="6" Margin="0,4"/>

                    <TextBlock Grid.Row="2" Grid.Column="0" Text="Etiket yüksekliği (mm):" Margin="0,8,8,8"/>
                    <TextBox   Grid.Row="2" Grid.Column="1"
                               Text="{Binding LabelHeightMm, UpdateSourceTrigger=PropertyChanged}"
                               Padding="6" Margin="0,4"/>

                    <TextBlock Grid.Row="3" Grid.Column="0" Text="Etiket aralığı (mm):" Margin="0,8,8,8"/>
                    <TextBox   Grid.Row="3" Grid.Column="1"
                               Text="{Binding LabelGapMm, UpdateSourceTrigger=PropertyChanged}"
                               Padding="6" Margin="0,4"/>

                    <TextBlock Grid.Row="4" Grid.Column="0" Text="Yazı tipi:" Margin="0,8,8,8"/>
                    <ComboBox  Grid.Row="4" Grid.Column="1"
                               ItemsSource="{Binding AvailableFonts}"
                               SelectedItem="{Binding LabelFontFamily, UpdateSourceTrigger=PropertyChanged}"
                               Margin="0,4"/>

                    <TextBlock Grid.Row="5" Grid.Column="0" Text="Kullanıcı font (pt):" Margin="0,8,8,8"/>
                    <TextBox   Grid.Row="5" Grid.Column="1"
                               Text="{Binding LabelUserFontSize, UpdateSourceTrigger=PropertyChanged}"
                               Padding="6" Margin="0,4"/>

                    <TextBlock Grid.Row="6" Grid.Column="0" Text="Mesaj font (pt):" Margin="0,8,8,8"/>
                    <TextBox   Grid.Row="6" Grid.Column="1"
                               Text="{Binding LabelMessageFontSize, UpdateSourceTrigger=PropertyChanged}"
                               Padding="6" Margin="0,4"/>

                    <Button    Grid.Row="7" Grid.Column="1"
                               Content="Test Etiketi Bas"
                               Command="{Binding TestPrintCommand}"
                               Padding="14,6" Margin="0,16,0,0"
                               HorizontalAlignment="Left"/>
                </Grid>
            </TabItem>

            <!-- OBS tab -->
            <TabItem Header="OBS">
                <Grid Margin="12">
                    <Grid.ColumnDefinitions>
                        <ColumnDefinition Width="160"/>
                        <ColumnDefinition Width="*"/>
                    </Grid.ColumnDefinitions>
                    <Grid.RowDefinitions>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                        <RowDefinition Height="Auto"/>
                    </Grid.RowDefinitions>

                    <TextBlock Grid.Row="0" Grid.Column="0" Text="Overlay portu:" Margin="0,8,8,8"/>
                    <TextBox   Grid.Row="0" Grid.Column="1"
                               Text="{Binding OverlayPort, UpdateSourceTrigger=PropertyChanged}"
                               Padding="6" Margin="0,4"/>

                    <TextBlock Grid.Row="1" Grid.Column="1"
                               Text="Port değişikliği için uygulamayı yeniden başlatman gerekir."
                               Foreground="#FFAAAAAA" FontStyle="Italic"
                               Margin="0,4,0,12" TextWrapping="Wrap"/>

                    <TextBlock Grid.Row="2" Grid.Column="0" Text="Chat teması:" Margin="0,8,8,8"/>
                    <ComboBox  Grid.Row="2" Grid.Column="1"
                               ItemsSource="{Binding AvailableThemes}"
                               SelectedItem="{Binding ChatTheme, UpdateSourceTrigger=PropertyChanged}"
                               Margin="0,4"/>
                </Grid>
            </TabItem>
        </TabControl>

        <!-- Validation error -->
        <TextBlock Grid.Row="1" Text="{Binding ValidationError}"
                   Foreground="#FFFF6666" Margin="0,8,0,0"
                   Visibility="{Binding ValidationError, Converter={StaticResource NullToCollapsedConverter}}"/>

        <!-- Buttons -->
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="İptal" Click="OnCancel" Padding="14,6" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Kaydet" Click="OnSave"  Padding="14,6" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 2: SettingsDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/SettingsDialog.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class SettingsDialog : Window
{
    private readonly SettingsViewModel _vm;

    public SettingsDialog()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<SettingsViewModel>();
        DataContext = _vm;
    }

    private void OnCancel(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        _vm.SaveCommand.Execute(null);
        if (!_vm.Saved) return;   // validation failed, dialog stays open

        if (_vm.OverlayPortChanged)
        {
            MessageBox.Show(
                "Overlay portu değiştirildi. Bu değişiklik için uygulamayı kapatıp yeniden açmanız gerekir.",
                "Yeniden başlatma gerekir",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }

        DialogResult = true;
        Close();
    }
}
```

- [ ] **Step 3: NullToCollapsedConverter (tiny helper)**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Converters/NullToCollapsedConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>Null/empty string → Collapsed; non-empty → Visible. Used by validation banners.</summary>
public sealed class NullToCollapsedConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is null || (value is string s && string.IsNullOrWhiteSpace(s))
            ? Visibility.Collapsed
            : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 4: Register the converter in App.xaml**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/App.xaml`. Replace its content with:

```xml
<Application x:Class="LiveDeck.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:converters="clr-namespace:LiveDeck.App.Converters"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <converters:NullToCollapsedConverter x:Key="NullToCollapsedConverter"/>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 5: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors. (Warnings/missing AppHost registration is OK — Task 11 fixes that.)

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/SettingsDialog.xaml LiveDeck.App/Views/SettingsDialog.xaml.cs LiveDeck.App/Converters/NullToCollapsedConverter.cs LiveDeck.App/App.xaml
git commit -m "feat(app): add SettingsDialog (tabbed) + NullToCollapsedConverter"
```

---

### Task 8: SessionRepository.GetAllEnded + StreamHistoryViewModel + Dialog

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/SessionRepository.cs` (+ `GetAllEnded`)
- Create: `LiveDeck.App/ViewModels/StreamHistoryViewModel.cs`
- Create: `LiveDeck.App/Views/StreamHistoryDialog.xaml` + `.xaml.cs`
- Modify: `LiveDeck.Tests/Storage/SessionRepositoryTests.cs`

- [ ] **Step 1: Add SessionRepository test**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/SessionRepositoryTests.cs`. ADD this test (alongside existing tests):

```csharp
    [Fact]
    public void GetAllEnded_returns_only_ended_sessions_newest_first()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new SessionRepository(db);

        repo.Insert(new StreamSession("s1", null, 1000, EndedAt: 1500, new[] { "instagram" }, null));
        repo.Insert(new StreamSession("s2", null, 2000, EndedAt: null,  new[] { "instagram" }, null));
        repo.Insert(new StreamSession("s3", null, 3000, EndedAt: 3500, new[] { "tiktok" },    null));

        var ended = repo.GetAllEnded(limit: 10);

        ended.Should().HaveCount(2);
        ended[0].Id.Should().Be("s3");
        ended[1].Id.Should().Be("s1");
    }
```

- [ ] **Step 2: Implement GetAllEnded in SessionRepository**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/SessionRepository.cs`. Add this method to the class (next to `GetActive` / `GetById`):

```csharp
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
```

If the existing class doesn't already have `using System.Collections.Generic;` and `using System.Linq;` at the top, add them.

- [ ] **Step 3: Run repo test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~SessionRepositoryTests"
```
Expected: 3/3 PASS (existing 2 + new 1).

- [ ] **Step 4: StreamHistoryViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/StreamHistoryViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class StreamHistoryViewModel : ViewModelBase
{
    private readonly SessionRepository _sessions;
    private readonly LabelRepository _labels;

    public ObservableCollection<StreamHistoryRow> Sessions { get; } = new();

    public StreamHistoryViewModel(SessionRepository sessions, LabelRepository labels)
    {
        _sessions = sessions;
        _labels = labels;
        Reload();
    }

    private void Reload()
    {
        Sessions.Clear();
        foreach (var s in _sessions.GetAllEnded(limit: 365))
        {
            var totals = _labels.GetSessionTotals(s.Id);
            var endedAt = s.EndedAt ?? s.StartedAt;
            var seconds = endedAt - s.StartedAt;

            Sessions.Add(new StreamHistoryRow(
                SessionId:    s.Id,
                StartedLabel: DateTimeOffset.FromUnixTimeSeconds(s.StartedAt).LocalDateTime
                                .ToString("yyyy-MM-dd HH:mm"),
                Duration:     FormatDuration(seconds),
                LabelCount:   totals.PrintedCount,
                TotalAmount:  totals.TotalAmount,
                Platforms:    string.Join(", ", s.Platforms)));
        }
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}s {ts.Minutes}d"
            : $"{ts.Minutes}d {ts.Seconds}s";
    }
}

/// <summary>Flattened row for the history DataGrid.</summary>
public sealed record StreamHistoryRow(
    string SessionId,
    string StartedLabel,
    string Duration,
    int LabelCount,
    decimal TotalAmount,
    string Platforms);
```

- [ ] **Step 5: StreamHistoryDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/StreamHistoryDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.StreamHistoryDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Yayın Geçmişi" Width="780" Height="540"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Yayın Geçmişi" FontSize="20" FontWeight="Bold"
                   Margin="0,0,0,12"/>

        <DataGrid Grid.Row="1"
                  x:Name="HistoryGrid"
                  ItemsSource="{Binding Sessions}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  HeadersVisibility="Column"
                  MouseDoubleClick="OnRowDoubleClick"
                  Background="#FF1A1A1A"
                  Foreground="White"
                  BorderBrush="#FF333333"
                  RowBackground="#FF1A1A1A"
                  AlternatingRowBackground="#FF222222">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Tarih"    Binding="{Binding StartedLabel}" Width="2*"/>
                <DataGridTextColumn Header="Süre"     Binding="{Binding Duration}"     Width="*"/>
                <DataGridTextColumn Header="Etiket"   Binding="{Binding LabelCount}"   Width="*"/>
                <DataGridTextColumn Header="Ciro"
                                    Binding="{Binding TotalAmount, StringFormat={}{0:N2} TL}"
                                    Width="*"/>
                <DataGridTextColumn Header="Platform" Binding="{Binding Platforms}"    Width="2*"/>
            </DataGrid.Columns>
        </DataGrid>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,12,0,0">
            <Button Content="Kapat" Click="OnClose" Padding="14,6" IsCancel="True" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 6: StreamHistoryDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/StreamHistoryDialog.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class StreamHistoryDialog : Window
{
    public StreamHistoryDialog()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<StreamHistoryViewModel>();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (HistoryGrid.SelectedItem is StreamHistoryRow row)
        {
            var report = App.Host.Services.GetRequiredService<StreamReportDialog>();
            report.LoadReport(row.SessionId);
            report.Owner = this;
            report.ShowDialog();
        }
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 7: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors (DI registration not yet done — Task 11 will fix; that's a runtime issue, not compile).

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/SessionRepository.cs LiveDeck.Tests/Storage/SessionRepositoryTests.cs LiveDeck.App/ViewModels/StreamHistoryViewModel.cs LiveDeck.App/Views/StreamHistoryDialog.xaml LiveDeck.App/Views/StreamHistoryDialog.xaml.cs
git commit -m "feat(app): add Stream History dialog (list + double-click → report)"
```

---

### Task 9: BlacklistViewModel + BlacklistDialog + AddToBlacklistDialog

**Files:**
- Create: `LiveDeck.App/ViewModels/BlacklistViewModel.cs`
- Create: `LiveDeck.App/Views/BlacklistDialog.xaml` + `.xaml.cs`
- Create: `LiveDeck.App/Views/AddToBlacklistDialog.xaml` + `.xaml.cs`

`AddToBlacklistDialog` is a small popover (Username readonly + Sebep + Platform dropdown for manual add). Reused from both the management dialog ("+ Manuel Ekle") and from MainShell's right-click ("Kara Listeye Al…" — the username pre-filled).

- [ ] **Step 1: BlacklistViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/BlacklistViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;

namespace LiveDeck.App.ViewModels;

public sealed partial class BlacklistViewModel : ViewModelBase
{
    private readonly CustomerRepository _repo;
    private readonly CustomerService _customers;

    public ObservableCollection<Customer> Items { get; } = new();

    [ObservableProperty] private Customer? _selected;
    [ObservableProperty] private int _totalCount;

    public BlacklistViewModel(CustomerRepository repo, CustomerService customers)
    {
        _repo = repo;
        _customers = customers;
        Reload();
    }

    public void Reload()
    {
        Items.Clear();
        foreach (var c in _repo.GetBlacklisted()) Items.Add(c);
        TotalCount = Items.Count;
    }

    [RelayCommand]
    private void RemoveSelected()
    {
        if (Selected is null) return;
        var confirm = MessageBox.Show(
            $"{Selected.Username} kara listeden çıkarılacak. Emin misin?",
            "Onayla", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        _customers.RemoveFromBlacklist(Selected.Id);
        Reload();
    }

    [RelayCommand]
    private void AddManual()
    {
        var dialog = new Views.AddToBlacklistDialog
        {
            Mode = Views.AddToBlacklistDialog.DialogMode.Manual
        };
        dialog.Owner = Application.Current?.Windows.Count > 0
            ? Application.Current?.Windows[Application.Current.Windows.Count - 1]
            : null;
        if (dialog.ShowDialog() != true) return;

        _customers.EnsureBlacklistedManual(
            dialog.PlatformText ?? "instagram",
            dialog.UsernameText ?? "",
            dialog.ReasonText);
        Reload();
    }
}
```

- [ ] **Step 2: AddToBlacklistDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/AddToBlacklistDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.AddToBlacklistDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Kara Listeye Al" Width="420" Height="240"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>
        <Grid.ColumnDefinitions>
            <ColumnDefinition Width="100"/>
            <ColumnDefinition Width="*"/>
        </Grid.ColumnDefinitions>

        <TextBlock Grid.Row="0" Grid.Column="0" Text="Platform:"  Margin="0,8,8,8"/>
        <ComboBox  Grid.Row="0" Grid.Column="1"
                   x:Name="PlatformBox"
                   Margin="0,4">
            <ComboBoxItem Content="instagram"/>
            <ComboBoxItem Content="tiktok"/>
        </ComboBox>

        <TextBlock Grid.Row="1" Grid.Column="0" Text="Kullanıcı:" Margin="0,8,8,8"/>
        <TextBox   Grid.Row="1" Grid.Column="1"
                   x:Name="UsernameBox" Padding="6" Margin="0,4"/>

        <TextBlock Grid.Row="2" Grid.Column="0" Text="Sebep:"     Margin="0,8,8,8"/>
        <TextBox   Grid.Row="2" Grid.Column="1"
                   x:Name="ReasonBox" Padding="6" Margin="0,4"/>

        <StackPanel Grid.Row="4" Grid.ColumnSpan="2" Orientation="Horizontal"
                    HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="İptal" Click="OnCancel" Padding="14,6" Margin="0,0,8,0" IsCancel="True"/>
            <Button Content="Ekle"  Click="OnSave"   Padding="14,6" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: AddToBlacklistDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/AddToBlacklistDialog.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;

namespace LiveDeck.App.Views;

public partial class AddToBlacklistDialog : Window
{
    public enum DialogMode { Prefilled, Manual }

    public string? PlatformText { get; set; }
    public string? UsernameText { get; set; }
    public string? ReasonText   { get; set; }
    public DialogMode Mode { get; set; } = DialogMode.Manual;

    public AddToBlacklistDialog()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // Pre-fill from properties (caller can set before ShowDialog)
        UsernameBox.Text = UsernameText ?? "";
        ReasonBox.Text   = ReasonText ?? "";

        // Select platform in ComboBox
        var target = PlatformText ?? "instagram";
        foreach (ComboBoxItem item in PlatformBox.Items)
        {
            if ((item.Content as string) == target)
            {
                PlatformBox.SelectedItem = item;
                break;
            }
        }
        if (PlatformBox.SelectedItem is null && PlatformBox.Items.Count > 0)
            PlatformBox.SelectedIndex = 0;

        // In "Prefilled" mode the username comes from a chat row — lock it.
        UsernameBox.IsReadOnly = (Mode == DialogMode.Prefilled);
        PlatformBox.IsEnabled  = (Mode == DialogMode.Manual);
    }

    private void OnCancel(object sender, RoutedEventArgs e) { DialogResult = false; }

    private void OnSave(object sender, RoutedEventArgs e)
    {
        var username = UsernameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(username))
        {
            MessageBox.Show("Kullanıcı adı boş olamaz.",
                "Eksik bilgi", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        UsernameText = username;
        ReasonText   = string.IsNullOrWhiteSpace(ReasonBox.Text) ? null : ReasonBox.Text.Trim();
        PlatformText = (PlatformBox.SelectedItem as ComboBoxItem)?.Content as string ?? "instagram";

        DialogResult = true;
    }
}
```

- [ ] **Step 4: BlacklistDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/BlacklistDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.BlacklistDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Kara Liste" Width="700" Height="500"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="16">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <DockPanel Grid.Row="0" Margin="0,0,0,12">
            <TextBlock Text="Kara Liste" FontSize="20" FontWeight="Bold"
                       VerticalAlignment="Center"/>
            <TextBlock Text="{Binding TotalCount, StringFormat=Toplam: {0} kullanıcı}"
                       Foreground="#FFAAAAAA" Margin="16,0,0,0"
                       VerticalAlignment="Center"/>
        </DockPanel>

        <DataGrid Grid.Row="1"
                  ItemsSource="{Binding Items}"
                  SelectedItem="{Binding Selected}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  HeadersVisibility="Column"
                  Background="#FF1A1A1A"
                  Foreground="White"
                  BorderBrush="#FF333333"
                  RowBackground="#FF1A1A1A"
                  AlternatingRowBackground="#FF222222">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Platform"  Binding="{Binding Platform}"        Width="*"/>
                <DataGridTextColumn Header="Kullanıcı" Binding="{Binding Username}"        Width="2*"/>
                <DataGridTextColumn Header="Sebep"     Binding="{Binding BlacklistReason}" Width="3*"/>
                <DataGridTextColumn Header="Tarih"
                                    Binding="{Binding BlacklistedAt, Converter={StaticResource UnixToDateConverter}}"
                                    Width="2*"/>
            </DataGrid.Columns>
        </DataGrid>

        <StackPanel Grid.Row="2" Orientation="Horizontal" Margin="0,12,0,0">
            <Button Content="+ Manuel Ekle"   Command="{Binding AddManualCommand}"      Padding="14,6"/>
            <Button Content="Kara Listeden Çıkar"
                    Command="{Binding RemoveSelectedCommand}"
                    Padding="14,6" Margin="8,0,0,0"
                    Foreground="#FFFF6666"/>
            <Button Content="Kapat" Click="OnClose" Padding="14,6"
                    HorizontalAlignment="Right" Margin="0,0,0,0"
                    DockPanel.Dock="Right" IsCancel="True" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 5: BlacklistDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/BlacklistDialog.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class BlacklistDialog : Window
{
    public BlacklistDialog()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<BlacklistViewModel>();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 6: UnixToDateConverter (used by Blacklist DataGrid)**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Converters/UnixToDateConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows.Data;

namespace LiveDeck.App.Converters;

/// <summary>UNIX-seconds long? → "yyyy-MM-dd HH:mm" local string. Null → "".</summary>
public sealed class UnixToDateConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is long unix && unix > 0)
            return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("yyyy-MM-dd HH:mm");
        return "";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

- [ ] **Step 7: Register UnixToDateConverter in App.xaml**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/App.xaml`. Add the new converter resource alongside `NullToCollapsedConverter`:

```xml
<Application x:Class="LiveDeck.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             xmlns:converters="clr-namespace:LiveDeck.App.Converters"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
            <converters:NullToCollapsedConverter x:Key="NullToCollapsedConverter"/>
            <converters:UnixToDateConverter      x:Key="UnixToDateConverter"/>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

- [ ] **Step 8: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors.

- [ ] **Step 9: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/BlacklistViewModel.cs LiveDeck.App/Views/BlacklistDialog.xaml LiveDeck.App/Views/BlacklistDialog.xaml.cs LiveDeck.App/Views/AddToBlacklistDialog.xaml LiveDeck.App/Views/AddToBlacklistDialog.xaml.cs LiveDeck.App/Converters/UnixToDateConverter.cs LiveDeck.App/App.xaml
git commit -m "feat(app): add Blacklist + AddToBlacklist dialogs with management commands"
```

---

## Shell Wiring

### Task 10: MainShellViewModel + MainShellView updates (⋮ menu, right-click, blacklist binding)

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs`
- Modify: `LiveDeck.App/Views/MainShellView.xaml` + `.xaml.cs`
- Create: `LiveDeck.App/Converters/BlacklistToBrushConverter.cs`

This task is the largest UI change in Faz 2a:
1. Replace `ChatMessages` `ObservableCollection<ChatMessage>` with `ObservableCollection<ChatMessageViewModel>`
2. Replace `PrintQueue` with `ObservableCollection<LabelViewModel>`
3. Add `OpenSettingsCommand`, `OpenStreamHistoryCommand`, `OpenBlacklistCommand`
4. Add `AddToBlacklistFromUsernameCommand` (used by both right-click menus)
5. XAML: ⋮ button + ContextMenu in top bar
6. XAML: ListBox.ItemContainerStyle background binding to `IsSenderBlacklisted` / `IsCustomerBlacklisted` via converter
7. XAML: ContextMenu on chat list AND queue list with "Kara Listeye Al…" / "Kara Listeden Çıkar"

- [ ] **Step 1: BlacklistToBrushConverter**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Converters/BlacklistToBrushConverter.cs`:

```csharp
using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace LiveDeck.App.Converters;

/// <summary>true → soft red (rgba 80%); false → Transparent. Used for ListBoxItem backgrounds.</summary>
public sealed class BlacklistToBrushConverter : IValueConverter
{
    private static readonly Brush BlacklistedBrush =
        new SolidColorBrush(Color.FromArgb(0x80, 0xFF, 0x66, 0x66));

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? BlacklistedBrush : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
```

Add it to `App.xaml`'s ResourceDictionary (alongside the others). After this step, `App.xaml` should have THREE converters registered:

```xml
<converters:NullToCollapsedConverter   x:Key="NullToCollapsedConverter"/>
<converters:UnixToDateConverter        x:Key="UnixToDateConverter"/>
<converters:BlacklistToBrushConverter  x:Key="BlacklistToBrushConverter"/>
```

- [ ] **Step 2: Replace MainShellViewModel**

Replace the entire content of `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`:

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
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _busSubscription;

    private const int MaxChatMessages = 200;

    public ObservableCollection<ChatMessageViewModel> ChatMessages { get; } = new();
    public ObservableCollection<LabelViewModel>       PrintQueue   { get; } = new();

    [ObservableProperty] private string _activeCode = "";
    [ObservableProperty] private string _activePriceText = "0";
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";

    public MainShellViewModel(
        IChatBus bus,
        LabelService labels,
        StreamSessionService sessions,
        LabelPrinter printer,
        CustomerService customers,
        CustomerRepository customerRepo)
    {
        _labels = labels;
        _sessions = sessions;
        _printer = printer;
        _customers = customers;
        _customerRepo = customerRepo;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _busSubscription = bus.Subscribe(OnChatMessage);

        UpdateStreamStatusLabel();
        ReloadQueueFromActiveSession();
    }

    private void OnChatMessage(ChatMessage m)
    {
        _dispatcher.BeginInvoke(() =>
        {
            var customer = _customerRepo.FindByPlatformAndUsername(m.Platform, m.Username);
            var blacklisted = customer?.IsBlacklisted ?? false;
            ChatMessages.Add(new ChatMessageViewModel(m, blacklisted));
            while (ChatMessages.Count > MaxChatMessages) ChatMessages.RemoveAt(0);
        });
    }

    private void UpdateStreamStatusLabel()
    {
        var session = _sessions.GetActive();
        StreamStatusLabel = session is null
            ? "Yayın aktif değil"
            : $"Yayın aktif (başlangıç: {DateTimeOffset.FromUnixTimeSeconds(session.StartedAt):HH:mm})";
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
        ReloadQueueFromActiveSession();
    }

    [RelayCommand] private void EndStream()
    {
        var session = _sessions.GetActive();
        if (session is null) return;

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
        // After save, refresh chat panel highlights in case blacklists were touched (rare but safe)
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
        // After dialog closes, blacklist may have changed → refresh chat highlights
        RefreshHighlights();
    }

    /// <summary>Right-click handler from chat panel: pre-fills username + platform.</summary>
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

    /// <summary>Right-click handler from queue panel.</summary>
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
        // Re-evaluate IsSenderBlacklisted for every visible chat row + queue row.
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

    public void Dispose() => _busSubscription.Dispose();
}
```

- [ ] **Step 3: Replace MainShellView.xaml**

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

            <!-- ⋮ menu button -->
            <Button   Grid.Column="8" Content="⋮"
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

        <!-- Status -->
        <TextBlock Grid.Row="1" Text="{Binding StreamStatusLabel}"
                   Foreground="#FFAAAAAA" Margin="0,0,0,12"/>

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

- [ ] **Step 4: Replace MainShellView.xaml.cs**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class MainShellView : UserControl
{
    public MainShellView()
    {
        InitializeComponent();
        DataContext = App.Host.Services.GetRequiredService<MainShellViewModel>();
    }

    private void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainShellViewModel vm
            && ChatList.SelectedItem is ChatMessageViewModel msgVm)
        {
            vm.AddChatToQueue(msgVm);
        }
    }

    private void OnMenuClick(object sender, RoutedEventArgs e)
    {
        // Open the ContextMenu attached to the ⋮ button
        if (MenuButton.ContextMenu is { } cm)
        {
            cm.PlacementTarget = MenuButton;
            cm.IsOpen = true;
        }
    }
}
```

- [ ] **Step 5: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors. (DI registration of MainShellViewModel needs the new params — Task 11 fixes that. May get a runtime DI exception but compile should be clean.)

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs LiveDeck.App/Views/MainShellView.xaml LiveDeck.App/Views/MainShellView.xaml.cs LiveDeck.App/Converters/BlacklistToBrushConverter.cs LiveDeck.App/App.xaml
git commit -m "feat(app): add ⋮ menu, right-click blacklist, red-highlight bindings to MainShell"
```

---

### Task 11: AppHost — register Faz 2a services + dialogs

**Files:**
- Modify: `LiveDeck.App/AppHost.cs`

After all the new VMs/dialogs/services, AppHost needs to register them. This is purely additive.

- [ ] **Step 1: Replace AppHost.cs entirely**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs`:

```csharp
using System;
using System.IO;
using LiveDeck.Chat.Bridge;
using LiveDeck.Chat.Ingestors;
using LiveDeck.Core;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Labeling;
using LiveDeck.Overlay;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Extensions.Logging;

namespace LiveDeck.App;

public sealed class AppHost : IDisposable
{
    public IServiceProvider Services { get; }
    private readonly Serilog.ILogger _serilog;

    public AppHost()
    {
        AppPaths.EnsureDirectoriesExist();

        _serilog = new LoggerConfiguration()
            .MinimumLevel.Information()
            .Enrich.FromLogContext()
            .WriteTo.Console()
            .WriteTo.File(
                Path.Combine(AppPaths.LogsFolder, "log-.txt"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] {SourceContext} {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        var services = new ServiceCollection();
        services.AddSingleton<ILoggerFactory>(_ => new SerilogLoggerFactory(_serilog, dispose: false));
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));

        // Settings + time
        services.AddSingleton(new SettingsStore(AppPaths.SettingsFile));
        services.AddSingleton(sp => sp.GetRequiredService<SettingsStore>().Load());
        services.AddSingleton<IClock, SystemClock>();

        // Storage
        services.AddSingleton<IDbConnectionFactory>(_ => new SqliteConnectionFactory(AppPaths.DatabaseFile));
        services.AddSingleton<MigrationRunner>();
        services.AddSingleton<SessionRepository>();
        services.AddSingleton<CustomerRepository>();
        services.AddSingleton<LabelRepository>();

        // Domain
        services.AddSingleton<StreamSessionService>();
        services.AddSingleton<CustomerService>();
        services.AddSingleton<LabelService>();

        // Chat plumbing
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 200));
        services.AddSingleton(sp => new ExtensionBridgeServer(
            sp.GetRequiredService<IChatBus>(),
            port: 4748,
            log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>()));
        services.AddSingleton<ChatBridgeIngestor>();

        // Overlay
        services.AddSingleton(sp => new OverlayHost(
            sp.GetRequiredService<IChatBus>(),
            port: sp.GetRequiredService<AppSettings>().OverlayPort,
            log: sp.GetRequiredService<ILogger<OverlayHost>>()));

        // Printing
        services.AddSingleton(sp => new LabelPrinter(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<LabelPrinter>>()));

        // ViewModels
        services.AddSingleton<ViewModels.MainShellViewModel>();
        services.AddTransient<ViewModels.StreamReportViewModel>();
        services.AddTransient<ViewModels.SettingsViewModel>();
        services.AddTransient<ViewModels.StreamHistoryViewModel>();
        services.AddTransient<ViewModels.BlacklistViewModel>();

        // Dialogs (transient — fresh instance per open)
        services.AddTransient<Views.StreamReportDialog>();
        services.AddTransient<Views.SettingsDialog>();
        services.AddTransient<Views.StreamHistoryDialog>();
        services.AddTransient<Views.BlacklistDialog>();

        Services = services.BuildServiceProvider();

        // Apply migrations once at boot
        Services.GetRequiredService<MigrationRunner>().Run();
    }

    public void Dispose()
    {
        if (Services is IDisposable disposable) disposable.Dispose();
        Serilog.Log.CloseAndFlush();
    }
}
```

- [ ] **Step 2: Build full solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
```
Expected: `Build succeeded.` 0 warnings, 0 errors.

- [ ] **Step 3: Run full test suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -5
```
Expected: ALL tests pass (~46-48 tests; was 39 in P1b + new repo/service tests).

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs
git commit -m "feat(app): rewire AppHost for Faz 2a (TikTok + admin dialogs + blacklist)"
```

---

## Acceptance

### Task 12: Manual smoke test — Faz 2a end-to-end

**No new files. Manual user verification.**

- [ ] **Step 1: Run app**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

Expected: window opens, single screen, top bar has Code+Price+Yayın Başlat+Yayını Bitir+⋮ buttons.

- [ ] **Step 2: ⋮ menu**

Click ⋮. Dropdown should open with three items: Ayarlar / Yayın Geçmişi / Kara Liste. Each opens a modal.

- [ ] **Step 3: Settings — Yazıcı tab**

Open Settings. Verify:
- "Yazıcı" combo lists Windows printers + "(Windows varsayılanı)" at top
- Width/Height/Gap fields are editable, validation kicks in when out of range
- Font combo lists installed system fonts (alphabetical)
- "Test Etiketi Bas" button: triggers a single test label print with sample data

Modify a field, click Kaydet. Reopen Settings → field should be persisted.

Modify OverlayPort to a different value (e.g., 4848), click Kaydet. Verify "Yeniden başlatma gerekir" info box pops up. Cancel app restart, OS port stays at old value at runtime (just settings.json updated).

- [ ] **Step 4: Settings — OBS tab**

Switch to OBS tab. Verify port + theme dropdown render correctly. Validation: enter port=99 → Kaydet should refuse (validation error visible).

- [ ] **Step 5: Yayın Geçmişi**

Open Yayın Geçmişi. If you've completed any P1b yayınlar, they should be listed (Tarih DESC).

Double-click a row → Stream Report dialog should open with that yayın's data. Excel export should work as in P1b.

- [ ] **Step 6: Kara Liste — manual add**

Open Kara Liste → "+ Manuel Ekle". Fill: Platform=tiktok, Kullanıcı=@test_blacklist, Sebep=test. Click Ekle.

Verify the row appears in the management dialog. Sebep + Tarih populated.

Right-click → "Kara Listeden Çıkar" → confirm → row disappears.

- [ ] **Step 7: Kara Liste — chat right-click**

Start a yayın. Wait for a chat message to come in (Instagram or TikTok via extension). Right-click on a chat row → "Kara Listeye Al…" → reason popover. Verify Username pre-filled, Platform pre-filled, Username field readonly. Type a reason, Ekle.

Confirm:
- Same chat row now has soft-red background
- Future messages from same user also red
- Open Kara Liste dialog → user is listed there

- [ ] **Step 8: TikTok ingestor**

In Chrome, ensure LiveDeck Chat Bridge extension is loaded (or reload — manifest changed). Open a TikTok Live page. Verify chat messages from TikTok flow into LiveDeck's left panel with Platform=tiktok.

Double-click a TikTok message → it should land in the print queue with current price.

- [ ] **Step 9: Stream Report still works**

End the yayın. Verify Stream Report dialog opens with totals (printed labels include both Instagram + TikTok — not platform-filtered).

- [ ] **Step 10: Tag milestone**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git tag -a phase-2a -m "Phase 2a — multi-platform + admin UX (TikTok, Settings, History, Blacklist)"
```

---

## Plan self-review

Cross-checked against `docs/superpowers/specs/2026-04-28-phase-2a-multiplatform-admin-ux-design.md` Section 1.2 + 3.x:

| Spec requirement | Task(s) |
|---|---|
| TikTok ingestor — content script port | Task 4 |
| TikTok ingestor — manifest update | Task 4 |
| InstagramIngestor → ChatBridgeIngestor rename | Task 3 |
| Settings UI — tabbed dialog | Tasks 6, 7 |
| Settings UI — printer dropdown (InstalledPrinters) | Task 6 |
| Settings UI — font dropdown (InstalledFontCollection) | Task 6 |
| Settings UI — Test Etiketi Bas | Task 6 |
| Settings UI — drop ParserHighConfidence/LowConfidence | Task 6 |
| Settings UI — OverlayPort restart warning | Task 7 (dialog code-behind) |
| Settings UI — Save/Cancel + validation | Tasks 6, 7 |
| Yayın Geçmişi — list dialog | Task 8 |
| Yayın Geçmişi — `SessionRepository.GetAllEnded` | Task 8 |
| Yayın Geçmişi — double-click → StreamReportDialog | Task 8 (code-behind) |
| Kara Liste — `Customer.BlacklistedAt` migration | Task 1 |
| Kara Liste — `CustomerRepository.UpdateBlacklist` / `GetBlacklisted` | Task 1 |
| Kara Liste — `CustomerService.AddToBlacklist` / `RemoveFromBlacklist` / `EnsureBlacklistedManual` | Task 2 |
| Kara Liste — visual red highlight (chat + queue) | Tasks 5, 10 |
| Kara Liste — right-click chat → Kara Listeye Al | Task 10 |
| Kara Liste — right-click queue → Kara Listeye Al | Task 10 |
| Kara Liste — yönetim dialog (list + remove + manual add) | Task 9 |
| Kara Liste — `AddToBlacklistDialog` popover | Task 9 |
| ⋮ menu in top bar | Task 10 |
| AppHost re-registration | Task 11 |
| Manual smoke acceptance | Task 12 |

**Placeholder scan:** No "TBD" / "TODO" / "fill in later" markers in any task. Every code step has full code; every shell step has the exact command.

**Type consistency:**
- `ChatBridgeIngestor` (Task 3) used in Task 11's AppHost.
- `Customer` constructor with 17 positional members (Task 1) used in Tasks 2, 8, all customer-facing code.
- `LabelViewModel` / `ChatMessageViewModel` (Task 5) used in Task 10's MainShellViewModel.
- `AddToBlacklistDialog.PlatformText / UsernameText / ReasonText` properties (Task 9) used by Task 10's right-click handlers.
- `SettingsViewModel.Saved` / `OverlayPortChanged` flags (Task 6) used by Task 7's dialog code-behind.
- `StreamHistoryRow` record (Task 8) — same shape used in DataGrid binding and double-click handler.

**Spec coverage gaps:** none. Every requirement maps to a task.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-2a-multiplatform-admin-ux.md`.

Two execution options:

**1. Subagent-Driven (recommended)** — fresh subagent per task with two-stage review.
**2. Inline Execution** — batch with checkpoints in this session.

**Which approach?**

