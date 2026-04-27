# LiveDeck Phase 1b — Manual Label Workflow Pivot Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Pivot the LiveDeck UX from automatic order-capture to a manual double-click-to-print workflow. The user sets a Code/Price live, sees one merged chat stream, double-clicks any message to queue an etiket, then prints all queued etiketler directly to a thermal printer.

**Architecture:** Single-screen WPF window (no sidebar tabs). Left = live chat (read-only ListBox, double-click adds), Right = print queue (selectable ListBox with row-level prices, no totals visible). Top bar = Code/Price text inputs + Yayın Başlat/Bitir. Direct printing via `System.Drawing.Printing.PrintDocument` (printer-independent). Stream-end opens a modal report (totals + top customers + Excel export) — only place where ciro is visible.

**Tech Stack:** .NET 10 WPF, CommunityToolkit.Mvvm, SQLite + Dapper, Serilog. New: `System.Drawing.Common` for printing, `ClosedXML` for Excel export.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-pivot state:** Phase 1 commit `cee69e3` (39/39 tasks done). Tests: 85/85.

---

## Scope

This pivot removes the auto-capture pipeline and replaces order-status workflow with a flat "Label" model. After this plan executes, the app provides:

- Single shell screen (no sidebar)
- Live merged chat (Instagram via existing extension; future TikTok/Facebook unchanged)
- Manual double-click-to-print queue with per-line prices
- Direct thermal printing
- Customer aggregate (TotalLabelsPrinted, TotalAmount) for end-of-stream report
- Stream report modal (parolasız) with Excel export

Out of scope (kept from Phase 1, no changes): Solution scaffolding, DI/Serilog, AppPaths, Settings, IClock, SQLite plumbing, MigrationRunner, ChatBus, ExtensionBridgeServer, browser extension, OBS overlay (chat HTML), StreamSession entity/service.

Out of scope (will be deleted): OrderCaptureEngine pipeline (6 stages), 50 Türkçe fixtures, OrderService, OrderRepository, OrderItem entity, ActiveCode entity/service/repository, ActiveCodes UI, OrderQueue UI with status tabs, ClipboardLabelFormatter, EtiketIntegration, HotkeyService, JoinConverter, StatusTabConverter, OrderCaptureWiring.

Out of scope (deferred to Phase 2): Çekiliş, sipariş bildirim toast overlay, multi-platform ingestor expansion.

---

## File Structure

After this plan completes, the relevant directories look like:

```
LiveDeck.Core/
├── Sales/
│   ├── Label.cs                              # NEW — record (replaces OrderItem)
│   └── LabelService.cs                       # NEW — Add(chat, price, code) + MarkPrinted(ids)
│   └── (deleted: OrderCaptureEngine.cs, OrderItem.cs, OrderStatus.cs, OrderService.cs,
│                  ActiveCode.cs, ActiveCodeService.cs, Pipeline/*)
├── Customers/
│   └── Customer.cs                           # MODIFIED — TotalLabelsPrinted, TotalAmount
│   └── CustomerService.cs                    # MODIFIED — UpdateAggregatesOnPrint
├── Storage/
│   ├── Migrations/
│   │   ├── 001_initial.sql                   # MODIFIED — drop OrderItem, add Label
│   │   └── 002_pivot_to_labels.sql           # NEW — migrates existing DBs
│   └── Repositories/
│       ├── LabelRepository.cs                # NEW
│       ├── CustomerRepository.cs             # MODIFIED — UpdateAggregates signature
│       └── (deleted: ActiveCodeRepository.cs, OrderRepository.cs)

LiveDeck.Labeling/
├── ClipboardLabelFormatter.cs                # KEPT (still exists for v3 use; no consumers in P1b)
├── LabelPrinter.cs                           # NEW — PrintDocument-based direct printing
└── LabelPrintDocument.cs                     # NEW — internal layout (60×30mm default)

LiveDeck.App/
├── ViewModels/
│   ├── MainShellViewModel.cs                 # NEW (replaces MainViewModel + 3 panel VMs)
│   └── StreamReportViewModel.cs              # NEW
│   └── (deleted: MainViewModel.cs, ActiveCodesViewModel.cs, OrderQueueViewModel.cs,
│                  ChatPanelViewModel.cs)
├── Views/
│   ├── MainShellView.xaml + .cs              # NEW — single-screen layout
│   ├── StreamReportDialog.xaml + .cs         # NEW
│   └── (deleted: ActiveCodesView, OrderQueueView, ChatPanelView, EditCodeDialog)
├── Converters/
│   └── (deleted: JoinConverter.cs, StatusTabConverter.cs)
├── Services/
│   └── (deleted: OrderCaptureWiring.cs, HotkeyService.cs, ClipboardService.cs, EtiketIntegration.cs)
├── MainWindow.xaml + .cs                     # MODIFIED — hosts MainShellView, no sidebar
├── App.xaml                                  # MODIFIED — remove deleted converters
├── App.xaml.cs                               # UNCHANGED (still starts ingestor + overlay)
└── AppHost.cs                                # MODIFIED — re-register service set

LiveDeck.Tests/
├── Sales/
│   ├── LabelServiceTests.cs                  # NEW
│   └── (deleted: MessageNormalizerTests, CodeMatcherTests, VariantExtractorTests,
│                  QuantityExtractorTests, IntentScorerTests, ConfidenceScorerTests,
│                  OrderCaptureEngineTests, OrderServiceTests, EngineFixturesTests, Fixtures/)
├── Storage/
│   ├── LabelRepositoryTests.cs               # NEW
│   ├── ActiveCodeRepositoryTests.cs          # DELETED
│   └── OrderRepositoryTests.cs               # DELETED
├── Labeling/
│   └── LabelPrintDocumentTests.cs            # NEW (layout math only — no real printing)
│   └── (deleted: ClipboardLabelFormatterTests.cs only if formatter is removed —
│                  see Task 4 decision: keeping the formatter, keeping its test)
```

**Key boundary rules unchanged from Phase 1:**
- `LiveDeck.Core` has zero UI/network dependencies
- `LiveDeck.Tests` references Core, Chat, Labeling

---

## Task Index

**Cleanup (Tasks 1-3):** Delete the auto-capture stack
**Schema migration (Tasks 4-6):** Drop OrderItem/ActiveCode, add Label, evolve Customer
**New domain (Tasks 7-10):** Label entity + repository + service + customer aggregate updates
**Direct printing (Tasks 11-13):** LabelPrintDocument + LabelPrinter + simple settings
**WPF rewrite (Tasks 14-18):** MainShellViewModel + MainShellView + double-click handler + StreamReportDialog
**Wiring (Tasks 19-20):** AppHost re-registration + final solution build/sanity

---

## Cleanup Phase

### Task 1: Delete OrderCaptureEngine pipeline + tests + fixtures

**Files to delete:**
- `LiveDeck.Core/Sales/Pipeline/MessageNormalizer.cs`
- `LiveDeck.Core/Sales/Pipeline/CodeMatcher.cs`
- `LiveDeck.Core/Sales/Pipeline/VariantExtractor.cs`
- `LiveDeck.Core/Sales/Pipeline/QuantityExtractor.cs`
- `LiveDeck.Core/Sales/Pipeline/IntentScorer.cs`
- `LiveDeck.Core/Sales/Pipeline/ConfidenceScorer.cs`
- `LiveDeck.Core/Sales/Pipeline/CaptureResult.cs`
- `LiveDeck.Core/Sales/OrderCaptureEngine.cs`
- `LiveDeck.Core/Sales/OrderService.cs`
- `LiveDeck.Tests/Sales/MessageNormalizerTests.cs`
- `LiveDeck.Tests/Sales/CodeMatcherTests.cs`
- `LiveDeck.Tests/Sales/VariantExtractorTests.cs`
- `LiveDeck.Tests/Sales/QuantityExtractorTests.cs`
- `LiveDeck.Tests/Sales/IntentScorerTests.cs`
- `LiveDeck.Tests/Sales/ConfidenceScorerTests.cs`
- `LiveDeck.Tests/Sales/OrderCaptureEngineTests.cs`
- `LiveDeck.Tests/Sales/OrderServiceTests.cs`
- `LiveDeck.Tests/Sales/EngineFixturesTests.cs`
- `LiveDeck.Tests/Sales/Fixtures/tr_chat_samples.json`
- `LiveDeck.Tests/Sales/Fixtures/` (empty directory)
- `LiveDeck.App/Services/OrderCaptureWiring.cs`

- [ ] **Step 1: Delete the pipeline + engine + service files**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -rf LiveDeck.Core/Sales/Pipeline
rm -f  LiveDeck.Core/Sales/OrderCaptureEngine.cs
rm -f  LiveDeck.Core/Sales/OrderService.cs
rm -f  LiveDeck.App/Services/OrderCaptureWiring.cs
```

- [ ] **Step 2: Delete the tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -f  LiveDeck.Tests/Sales/MessageNormalizerTests.cs
rm -f  LiveDeck.Tests/Sales/CodeMatcherTests.cs
rm -f  LiveDeck.Tests/Sales/VariantExtractorTests.cs
rm -f  LiveDeck.Tests/Sales/QuantityExtractorTests.cs
rm -f  LiveDeck.Tests/Sales/IntentScorerTests.cs
rm -f  LiveDeck.Tests/Sales/ConfidenceScorerTests.cs
rm -f  LiveDeck.Tests/Sales/OrderCaptureEngineTests.cs
rm -f  LiveDeck.Tests/Sales/OrderServiceTests.cs
rm -f  LiveDeck.Tests/Sales/EngineFixturesTests.cs
rm -rf LiveDeck.Tests/Sales/Fixtures
```

- [ ] **Step 3: Remove the fixture-copy rule from `LiveDeck.Tests.csproj`**

Open `LiveDeck.Tests/LiveDeck.Tests.csproj`. Find and DELETE this `<ItemGroup>`:

```xml
  <ItemGroup>
    <None Update="Sales\Fixtures\tr_chat_samples.json">
      <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    </None>
  </ItemGroup>
```

Do NOT delete other ItemGroups (PackageReferences, project refs).

- [ ] **Step 4: Remove OrderCaptureWiring registration + force-resolve from AppHost.cs**

Open `LiveDeck.App/AppHost.cs`. DELETE these two lines wherever they appear:

```csharp
        services.AddSingleton<Services.OrderCaptureWiring>();
```

```csharp
        // Force-create singletons so they start running even before any window opens
        _ = Services.GetRequiredService<Services.OrderCaptureWiring>();
```

DO NOT delete the rest of the host yet — that comes in Tasks 2 and 19.

- [ ] **Step 5: Build to confirm what else is broken**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -30
```

Expected: build FAILS with errors like "Type 'OrderService' not found" — referenced from `OrderQueueViewModel`, `MainViewModel`, `App.xaml.cs` etc. These are removed/replaced in subsequent tasks. **Do NOT try to fix the build here.** The intermediate broken state is expected.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add -A
git commit -m "refactor(core): delete auto-capture pipeline and fixtures (pivot to manual workflow)"
```

The commit will not pass `dotnet build` — that's intentional. Tasks 2-3 finish the cleanup and Tasks 7-18 fill in the new functionality.

---

### Task 2: Delete ActiveCode + OrderItem stack

**Files to delete:**
- `LiveDeck.Core/Sales/ActiveCode.cs`
- `LiveDeck.Core/Sales/ActiveCodeService.cs`
- `LiveDeck.Core/Sales/OrderItem.cs`
- `LiveDeck.Core/Sales/OrderStatus.cs`
- `LiveDeck.Core/Storage/Repositories/ActiveCodeRepository.cs`
- `LiveDeck.Core/Storage/Repositories/OrderRepository.cs`
- `LiveDeck.Tests/Storage/ActiveCodeRepositoryTests.cs`
- `LiveDeck.Tests/Storage/OrderRepositoryTests.cs`

- [ ] **Step 1: Delete the entities and services**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -f LiveDeck.Core/Sales/ActiveCode.cs
rm -f LiveDeck.Core/Sales/ActiveCodeService.cs
rm -f LiveDeck.Core/Sales/OrderItem.cs
rm -f LiveDeck.Core/Sales/OrderStatus.cs
rm -f LiveDeck.Core/Storage/Repositories/ActiveCodeRepository.cs
rm -f LiveDeck.Core/Storage/Repositories/OrderRepository.cs
rm -f LiveDeck.Tests/Storage/ActiveCodeRepositoryTests.cs
rm -f LiveDeck.Tests/Storage/OrderRepositoryTests.cs
```

- [ ] **Step 2: Remove ActiveCode/OrderItem registrations from AppHost.cs**

Open `LiveDeck.App/AppHost.cs`. DELETE these lines wherever they appear:

```csharp
        services.AddSingleton<ActiveCodeRepository>();
        services.AddSingleton<OrderRepository>();
        services.AddSingleton<ActiveCodeService>();
```

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add -A
git commit -m "refactor(core): delete ActiveCode and OrderItem stacks (replaced by Label model)"
```

Build still broken — expected.

---

### Task 3: Delete WPF panels + dialogs + converters + obsolete services

**Files to delete:**
- `LiveDeck.App/ViewModels/MainViewModel.cs`
- `LiveDeck.App/ViewModels/ActiveCodesViewModel.cs`
- `LiveDeck.App/ViewModels/OrderQueueViewModel.cs`
- `LiveDeck.App/ViewModels/ChatPanelViewModel.cs`
- `LiveDeck.App/Views/ActiveCodesView.xaml` + `.xaml.cs`
- `LiveDeck.App/Views/OrderQueueView.xaml` + `.xaml.cs`
- `LiveDeck.App/Views/ChatPanelView.xaml` + `.xaml.cs`
- `LiveDeck.App/Views/EditCodeDialog.xaml` + `.xaml.cs`
- `LiveDeck.App/Converters/JoinConverter.cs`
- `LiveDeck.App/Converters/StatusTabConverter.cs`
- `LiveDeck.App/Services/ClipboardService.cs`
- `LiveDeck.App/Services/HotkeyService.cs`
- `LiveDeck.App/Services/EtiketIntegration.cs`

**Kept:**
- `LiveDeck.App/ViewModels/ViewModelBase.cs` — re-used as base for MainShellViewModel
- `LiveDeck.Labeling/ClipboardLabelFormatter.cs` + its test — kept as a utility, no consumer in P1b but deletion is irreversible and the formatter is harmless. Future v3 may reuse it.

- [ ] **Step 1: Delete the WPF files**

```bash
cd /c/Users/burak/source/repos/LiveDeck
rm -f LiveDeck.App/ViewModels/MainViewModel.cs
rm -f LiveDeck.App/ViewModels/ActiveCodesViewModel.cs
rm -f LiveDeck.App/ViewModels/OrderQueueViewModel.cs
rm -f LiveDeck.App/ViewModels/ChatPanelViewModel.cs
rm -f LiveDeck.App/Views/ActiveCodesView.xaml
rm -f LiveDeck.App/Views/ActiveCodesView.xaml.cs
rm -f LiveDeck.App/Views/OrderQueueView.xaml
rm -f LiveDeck.App/Views/OrderQueueView.xaml.cs
rm -f LiveDeck.App/Views/ChatPanelView.xaml
rm -f LiveDeck.App/Views/ChatPanelView.xaml.cs
rm -f LiveDeck.App/Views/EditCodeDialog.xaml
rm -f LiveDeck.App/Views/EditCodeDialog.xaml.cs
rm -f LiveDeck.App/Converters/JoinConverter.cs
rm -f LiveDeck.App/Converters/StatusTabConverter.cs
rm -f LiveDeck.App/Services/ClipboardService.cs
rm -f LiveDeck.App/Services/HotkeyService.cs
rm -f LiveDeck.App/Services/EtiketIntegration.cs
```

- [ ] **Step 2: Reset MainWindow.xaml to placeholder**

Replace the entire content of `LiveDeck.App/MainWindow.xaml` with:

```xml
<Window x:Class="LiveDeck.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="LiveDeck" Height="800" Width="1280"
        Background="#FF1A1A1A" Foreground="White">
    <Grid>
        <TextBlock Text="LiveDeck"
                   HorizontalAlignment="Center" VerticalAlignment="Center"
                   FontSize="32" Foreground="#FF555555"/>
    </Grid>
</Window>
```

- [ ] **Step 3: Reset MainWindow.xaml.cs to minimal**

Replace the entire content of `LiveDeck.App/MainWindow.xaml.cs` with:

```csharp
using System.Windows;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
```

- [ ] **Step 4: Reset App.xaml resources (remove deleted converter declarations)**

Replace the entire content of `LiveDeck.App/App.xaml` with:

```xml
<Application x:Class="LiveDeck.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

(MainShellViewModel + style additions land in Task 14 onward.)

- [ ] **Step 5: Remove ViewModel + obsolete service registrations from AppHost.cs**

Open `LiveDeck.App/AppHost.cs`. DELETE these lines (anywhere they appear):

```csharp
        services.AddSingleton<ViewModels.MainViewModel>();
        services.AddSingleton<ViewModels.ActiveCodesViewModel>();
        services.AddSingleton<ViewModels.OrderQueueViewModel>();
        services.AddSingleton<ViewModels.ChatPanelViewModel>();
        services.AddSingleton<Services.ClipboardService>();
        services.AddSingleton<Services.HotkeyService>();
        services.AddSingleton<Services.EtiketIntegration>();
```

The `LiveDeck.Labeling.ClipboardLabelFormatter` registration in AppHost is also obsolete (no consumer in P1b). DELETE that too:

```csharp
        services.AddSingleton<LiveDeck.Labeling.ClipboardLabelFormatter>();
```

- [ ] **Step 6: Build to confirm cleanup is complete**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Expected: build still has some errors related to `Customer` (TotalLabelsPrinted not yet added) and missing `LabelService`/`LabelRepository`. Those are filled in Tasks 4-10. **Do NOT try to make the build green here** — the cleanup is done.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add -A
git commit -m "refactor(app): delete sidebar UI, ViewModels, dialogs, hotkey/clipboard/etiket services"
```

---

## Schema Migration Phase

### Task 4: Add migration `002_pivot_to_labels.sql`

**Files:**
- Create: `LiveDeck.Core/Storage/Migrations/002_pivot_to_labels.sql`

**Strategy:** keep `001_initial.sql` untouched (history-preserving). Add a new migration `002` that:
1. Drops the `OrderItem`, `ActiveCode`, `Giveaway`, `GiveawayParticipant` tables (last two are pre-Phase-2 placeholders never used by P1b)
2. Adds `Customer.TotalLabelsPrinted INTEGER NOT NULL DEFAULT 0` and `Customer.TotalAmount REAL NOT NULL DEFAULT 0` columns
3. Creates the new `Label` table
4. Bumps `_meta.SchemaVersion` to 2

Existing fingerprint tracking columns on `Customer` (TotalOrders, CompletedOrders, CancelledOrders, TrustScore, IsBlacklisted, BlacklistReason) are KEPT — Phase 2 will reuse them for blacklist/giveaway logic. `TotalOrders` simply stops being incremented; the new aggregates use the new columns.

- [ ] **Step 1: Author the migration**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Migrations/002_pivot_to_labels.sql`:

```sql
-- Phase 1b pivot: drop OrderItem/ActiveCode/Giveaway, introduce Label, evolve Customer.
-- Idempotent: re-applying does nothing.

PRAGMA foreign_keys = OFF;

DROP TABLE IF EXISTS GiveawayParticipant;
DROP TABLE IF EXISTS Giveaway;
DROP TABLE IF EXISTS OrderItem;
DROP TABLE IF EXISTS ActiveCode;

-- Add aggregate columns to Customer (idempotent via ALTER ... IF NOT EXISTS pattern is
-- not supported in SQLite; use a guard via PRAGMA table_info instead).
-- SQLite does not support conditional column additions natively; this migration assumes
-- a clean slate or a Phase 1 DB. If the column already exists, the ALTER will fail and
-- abort the migration. The MigrationRunner is responsible for skipping already-applied
-- scripts via _meta.SchemaVersion.

ALTER TABLE Customer ADD COLUMN TotalLabelsPrinted INTEGER NOT NULL DEFAULT 0;
ALTER TABLE Customer ADD COLUMN TotalAmount        REAL    NOT NULL DEFAULT 0;

CREATE TABLE IF NOT EXISTS Label (
    Id           TEXT PRIMARY KEY,
    SessionId    TEXT NOT NULL,
    CustomerId   TEXT NOT NULL,
    Platform     TEXT NOT NULL,
    Username     TEXT NOT NULL,
    MessageText  TEXT NOT NULL,
    Code         TEXT,
    Price        REAL NOT NULL,
    AddedAt      INTEGER NOT NULL,
    PrintedAt    INTEGER,
    FOREIGN KEY (SessionId)  REFERENCES StreamSession(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE INDEX IF NOT EXISTS IX_Label_Session_PrintedAt ON Label(SessionId, PrintedAt);
CREATE INDEX IF NOT EXISTS IX_Label_Customer          ON Label(CustomerId);

PRAGMA foreign_keys = ON;

UPDATE _meta SET SchemaVersion = 2 WHERE Id = 1;
```

- [ ] **Step 2: Update MigrationRunner to skip already-applied migrations (idempotency)**

The current `MigrationRunner.Run()` (Phase 1, Task 9) re-executes ALL embedded scripts every time. With ALTER TABLE in `002`, that fails on a Customer that already has the new columns. Make it version-aware.

Open `LiveDeck.Core/Storage/MigrationRunner.cs`. Replace the entire file content with:

```csharp
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Dapper;

namespace LiveDeck.Core.Storage;

/// <summary>
/// Applies embedded `Storage/Migrations/NNN_*.sql` scripts in lexical order, skipping
/// scripts whose number is already recorded in `_meta.SchemaVersion`. Each script must
/// end with `UPDATE _meta SET SchemaVersion = N WHERE Id = 1;` to advance the counter.
/// </summary>
public sealed class MigrationRunner
{
    private const string MigrationPrefix = "LiveDeck.Core.Storage.Migrations.";

    private readonly IDbConnectionFactory _factory;

    public MigrationRunner(IDbConnectionFactory factory)
    {
        _factory = factory;
    }

    public void Run()
    {
        using var conn = _factory.Open();

        // Bootstrap _meta if missing — run only the first script (which always creates _meta)
        // when the table doesn't exist yet.
        var hasMeta = conn.ExecuteScalar<long>(
            "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='_meta'") > 0;

        int currentVersion = hasMeta
            ? conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1")
            : 0;

        foreach (var (version, sql) in LoadEmbeddedScripts())
        {
            if (version <= currentVersion) continue;
            conn.Execute(sql);
            currentVersion = version;
        }
    }

    /// <summary>
    /// Yields (version, sql) pairs sorted by lexical filename. Filename pattern is
    /// `NNN_description.sql` where NNN is the integer version. Files that don't start
    /// with three digits are skipped.
    /// </summary>
    private static IEnumerable<(int Version, string Sql)> LoadEmbeddedScripts()
    {
        var assembly = typeof(MigrationRunner).Assembly;
        var resourceNames = assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(MigrationPrefix) && n.EndsWith(".sql"))
            .OrderBy(n => n);

        foreach (var name in resourceNames)
        {
            var leaf = name.Substring(MigrationPrefix.Length);   // "001_initial.sql"
            if (leaf.Length < 4 || !int.TryParse(leaf.Substring(0, 3), out var version))
                continue;

            using var stream = assembly.GetManifestResourceStream(name);
            if (stream is null) continue;
            using var reader = new StreamReader(stream);
            yield return (version, reader.ReadToEnd());
        }
    }
}
```

- [ ] **Step 3: Update MigrationRunnerTests to verify version-aware behaviour**

Open `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`. Replace the entire content with:

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
    public void Run_creates_label_and_customer_aggregates_at_version_2()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();

        using var conn = db.Open();
        var tables = conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name").AsList();

        // P1b shape: Label exists, OrderItem/ActiveCode/Giveaway gone.
        tables.Should().Contain(new[]
        {
            "Customer", "Label", "Settings", "StreamSession", "_meta"
        });
        tables.Should().NotContain(new[]
        {
            "ActiveCode", "OrderItem", "Giveaway", "GiveawayParticipant"
        });

        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(2);

        // New aggregate columns exist on Customer.
        var customerColumns = conn.Query<string>(
            "SELECT name FROM pragma_table_info('Customer')").AsList();
        customerColumns.Should().Contain(new[] { "TotalLabelsPrinted", "TotalAmount" });
    }

    [Fact]
    public void Run_is_idempotent()
    {
        using var db = new InMemorySqlite();
        var runner = new MigrationRunner(db);

        runner.Run();
        runner.Run();   // second call must not throw or duplicate columns

        using var conn = db.Open();
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(2);
    }
}
```

- [ ] **Step 4: Run migration tests → expect FAIL (the new SQL hasn't shipped yet)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests" 2>&1 | tail -10
```

Expected: tests fail because `001_initial.sql` still creates `OrderItem` etc. and the new ALTER hasn't run. **This is OK at this step.**

- [ ] **Step 5: Build the Core project to confirm SQL is embedded**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core
```

Expected: `Build succeeded.` 0/0. (`<EmbeddedResource Include="Storage\Migrations\*.sql" />` from Phase 1 Task 8 picks up the new file automatically.)

- [ ] **Step 6: Commit (build green, migration tests intentionally red)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Migrations/002_pivot_to_labels.sql LiveDeck.Core/Storage/MigrationRunner.cs LiveDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(core): add 002 migration adding Label table and Customer aggregates"
```

Migration tests pass after Task 5 simplifies `001_initial.sql` (or, if Task 5 chooses not to touch `001`, after the schema is correctly applied via the version gate).

---

### Task 5: Trim `001_initial.sql` to remove pre-pivot tables

**Files:**
- Modify: `LiveDeck.Core/Storage/Migrations/001_initial.sql`

**Why:** `002_pivot_to_labels.sql` does `DROP TABLE IF EXISTS OrderItem`, but in fresh deployments running `001` then `002` we'd still create OrderItem then immediately drop it — wasteful. More importantly, the new `MigrationRunnerTests` assert `OrderItem` is NOT in the tables list at version 2, but `001` would still create it temporarily.

The cleanest fix: edit `001_initial.sql` to NOT create OrderItem/ActiveCode/Giveaway/GiveawayParticipant at all. Existing developers' DBs (which already have those tables from Phase 1) will get them dropped by `002`. Fresh DBs will skip them entirely.

- [ ] **Step 1: Replace `001_initial.sql` content**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Migrations/001_initial.sql` and replace its entire content with:

```sql
-- LiveDeck initial schema (P1b-flattened). All timestamps are unix seconds (INTEGER).
-- This migration is idempotent: re-applying it on a populated DB does nothing.

CREATE TABLE IF NOT EXISTS _meta (
    Id INTEGER PRIMARY KEY CHECK (Id = 1),
    SchemaVersion INTEGER NOT NULL
);

INSERT OR IGNORE INTO _meta (Id, SchemaVersion) VALUES (1, 0);

CREATE TABLE IF NOT EXISTS StreamSession (
    Id           TEXT PRIMARY KEY,
    Title        TEXT,
    StartedAt    INTEGER NOT NULL,
    EndedAt      INTEGER,
    Platforms    TEXT NOT NULL DEFAULT '[]',
    Notes        TEXT
);

CREATE INDEX IF NOT EXISTS IX_StreamSession_StartedAt ON StreamSession(StartedAt DESC);

CREATE TABLE IF NOT EXISTS Customer (
    Id                TEXT PRIMARY KEY,
    Platform          TEXT NOT NULL,
    Username          TEXT NOT NULL,
    DisplayName       TEXT,
    AvatarUrl         TEXT,
    FirstSeenAt       INTEGER NOT NULL,
    LastSeenAt        INTEGER NOT NULL,
    TotalOrders       INTEGER NOT NULL DEFAULT 0,
    CompletedOrders   INTEGER NOT NULL DEFAULT 0,
    CancelledOrders   INTEGER NOT NULL DEFAULT 0,
    TrustScore        INTEGER NOT NULL DEFAULT 100,
    IsBlacklisted     INTEGER NOT NULL DEFAULT 0,
    BlacklistReason   TEXT,
    Notes             TEXT
);

CREATE UNIQUE INDEX IF NOT EXISTS UX_Customer_Platform_Username ON Customer(Platform, Username);
CREATE INDEX IF NOT EXISTS IX_Customer_Blacklisted ON Customer(IsBlacklisted);
CREATE INDEX IF NOT EXISTS IX_Customer_TrustScore ON Customer(TrustScore DESC);

CREATE TABLE IF NOT EXISTS Settings (
    Key   TEXT PRIMARY KEY,
    Value TEXT NOT NULL
);

UPDATE _meta SET SchemaVersion = 1 WHERE Id = 1;
```

This drops the CREATE statements for OrderItem, ActiveCode, Giveaway, GiveawayParticipant — they're never created on fresh DBs. On existing dev DBs they're dropped by `002`.

- [ ] **Step 2: Build Core**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core
```
Expected: 0/0.

- [ ] **Step 3: Run migration tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~MigrationRunnerTests"
```
Expected: 2/2 PASS.

If the idempotency test fails because `002`'s ALTER TABLE complains "duplicate column name", the version gate in MigrationRunner from Task 4 should prevent re-running `002`. Verify the gate logic.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Migrations/001_initial.sql
git commit -m "refactor(core): trim 001_initial to P1b-relevant tables only"
```

---

### Task 6: Delete dev-local `livedeck.db` (forces fresh schema for the developer)

**No files committed — this is a developer-side cleanup.**

- [ ] **Step 1: Delete the existing dev DB so Task 9 starts on a fresh schema**

```bash
rm -f /c/Users/burak/Documents/LiveDeck/data/livedeck.db
rm -f /c/Users/burak/Documents/LiveDeck/data/livedeck.db-shm
rm -f /c/Users/burak/Documents/LiveDeck/data/livedeck.db-wal
```

Subsequent app launches will trigger MigrationRunner which now applies `001` then `002` from scratch.

- [ ] **Step 2: No commit needed**

(`*.db` is gitignored. There's nothing to commit.)

---

## New Domain Phase

### Task 7: Label entity + Customer aggregate fields

**Files:**
- Create: `LiveDeck.Core/Sales/Label.cs`
- Modify: `LiveDeck.Core/Customers/Customer.cs`

- [ ] **Step 1: Label record**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/Label.cs`:

```csharp
namespace LiveDeck.Core.Sales;

/// <summary>
/// A queued or printed label. Created when the user double-clicks a chat message in the
/// MainShell; persisted to SQLite. PrintedAt = null means it's still in the queue.
/// </summary>
public sealed record Label(
    string Id,
    string SessionId,
    string CustomerId,
    string Platform,
    string Username,
    string MessageText,
    string? Code,
    decimal Price,
    long AddedAt,
    long? PrintedAt);
```

- [ ] **Step 2: Add aggregate fields to Customer record**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Customers/Customer.cs`. Replace the entire file content with:

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
    decimal TotalAmount);
```

The two new positional members go at the end so existing call sites that don't care about them break loudly (compile error) — we want that, because `CustomerService.GetOrCreate` and `CustomerRepository.Insert/Map` need to be updated.

- [ ] **Step 3: Build Core (will fail until repository + service are updated in Tasks 8-10)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core 2>&1 | tail -10
```

Expected: errors at `CustomerRepository.Insert/Map` and `CustomerService.GetOrCreate` due to missing constructor args. Fixed in Task 8.

- [ ] **Step 4: Commit (red build is intentional — fixed in Task 8)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/Label.cs LiveDeck.Core/Customers/Customer.cs
git commit -m "feat(core): add Label record and Customer aggregate fields (TotalLabelsPrinted, TotalAmount)"
```

---

### Task 8: Update CustomerRepository to handle new aggregate columns

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Modify: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`

- [ ] **Step 1: Update CustomerRepositoryTests**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`. Replace its content with:

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
            TotalLabelsPrinted: 0, TotalAmount: 0m);

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
        found.TotalLabelsPrinted.Should().Be(0);
        found.TotalAmount.Should().Be(0m);
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
}
```

- [ ] **Step 2: Update CustomerRepository**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`. Replace the entire file content with:

```csharp
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
               TotalLabelsPrinted, TotalAmount)
              VALUES
              (@Id, @Platform, @Username, @DisplayName, @AvatarUrl, @FirstSeenAt, @LastSeenAt,
               @TotalOrders, @CompletedOrders, @CancelledOrders, @TrustScore,
               @IsBlacklisted, @BlacklistReason, @Notes,
               @TotalLabelsPrinted, @TotalAmount)",
            new
            {
                c.Id, c.Platform, c.Username, c.DisplayName, c.AvatarUrl,
                c.FirstSeenAt, c.LastSeenAt,
                c.TotalOrders, c.CompletedOrders, c.CancelledOrders, c.TrustScore,
                IsBlacklisted = c.IsBlacklisted ? 1 : 0,
                c.BlacklistReason, c.Notes,
                c.TotalLabelsPrinted, c.TotalAmount
            });
    }

    public Customer? FindByPlatformAndUsername(string platform, string username)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            @"SELECT * FROM Customer
              WHERE Platform=@platform AND Username=@username",
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

    /// <summary>
    /// Atomically bumps TotalLabelsPrinted by <paramref name="labelDelta"/>, TotalAmount
    /// by <paramref name="amountDelta"/>, and refreshes LastSeenAt.
    /// </summary>
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

    private static Customer Map(Row r) => new(
        r.Id, r.Platform, r.Username, r.DisplayName, r.AvatarUrl,
        r.FirstSeenAt, r.LastSeenAt,
        r.TotalOrders, r.CompletedOrders, r.CancelledOrders, r.TrustScore,
        r.IsBlacklisted == 1, r.BlacklistReason, r.Notes,
        r.TotalLabelsPrinted, r.TotalAmount);

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
    }
}
```

`UpdateAggregates` (the legacy method from Phase 1) is removed. P1b only needs `IncrementLabelStats`. If anyone else was calling `UpdateAggregates`, the compile error will surface — verify in step 4.

- [ ] **Step 3: Update CustomerService.GetOrCreate to pass the new fields**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Customers/CustomerService.cs`. Replace its content with:

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
            TotalAmount: 0m);
        _repo.Insert(customer);
        return customer;
    }

    /// <summary>
    /// Increments label aggregate counters when one or more labels are printed.
    /// </summary>
    public void RecordPrintedLabels(string customerId, int labelCount, decimal amount)
    {
        _repo.IncrementLabelStats(customerId, labelCount, amount, _clock.UnixNow());
    }
}
```

- [ ] **Step 4: Update CustomerServiceTests for the new constructor**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Customers/CustomerServiceTests.cs`. Replace content with:

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
        customer.FirstSeenAt.Should().Be(1234L);
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
        fresh.LastSeenAt.Should().Be(5000L);
    }
}
```

- [ ] **Step 5: Run all customer tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~Customer"
```

Expected: 6/6 PASS (3 service + 3 repo).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/CustomerRepository.cs LiveDeck.Core/Customers/CustomerService.cs LiveDeck.Tests/Storage/CustomerRepositoryTests.cs LiveDeck.Tests/Customers/CustomerServiceTests.cs
git commit -m "feat(core): teach Customer repository/service about label aggregates"
```

---

### Task 9: LabelRepository (Dapper, TDD)

**Files:**
- Create: `LiveDeck.Core/Storage/Repositories/LabelRepository.cs`
- Create: `LiveDeck.Tests/Storage/LabelRepositoryTests.cs`

- [ ] **Step 1: Failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/LabelRepositoryTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Tests.Storage;

public class LabelRepositoryTests
{
    private static (InMemorySqlite Db, LabelRepository Repo, string SessionId, string CustomerId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100, 0, 0, 0, 100,
                false, null, null, 0, 0m));

        return (db, new LabelRepository(db), "s1", "c1");
    }

    private static Label MakeLabel(string id, string sessionId, string customerId,
        decimal price = 100m, long? printedAt = null) =>
        new(id, sessionId, customerId, "instagram", "@a", "Mavi XL aldım", "MAVI",
            price, AddedAt: 200, PrintedAt: printedAt);

    [Fact]
    public void Insert_then_GetUnprinted_returns_inserted_label()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;

        repo.Insert(MakeLabel("l1", sid, cid));

        var unprinted = repo.GetUnprintedBySession(sid);
        unprinted.Should().HaveCount(1);
        unprinted[0].MessageText.Should().Be("Mavi XL aldım");
    }

    [Fact]
    public void Delete_removes_label_from_unprinted()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(MakeLabel("l1", sid, cid));

        repo.Delete("l1");

        repo.GetUnprintedBySession(sid).Should().BeEmpty();
    }

    [Fact]
    public void MarkPrinted_excludes_from_unprinted_and_sets_PrintedAt()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(MakeLabel("l1", sid, cid));

        repo.MarkPrinted(new[] { "l1" }, printedAt: 999);

        repo.GetUnprintedBySession(sid).Should().BeEmpty();
        var totals = repo.GetSessionTotals(sid);
        totals.PrintedCount.Should().Be(1);
    }

    [Fact]
    public void GetSessionTotals_aggregates_printed_only()
    {
        var (db, repo, sid, cid) = Fx();
        using var _ = db;
        repo.Insert(MakeLabel("l1", sid, cid, price: 100m, printedAt: 500));
        repo.Insert(MakeLabel("l2", sid, cid, price: 150m, printedAt: 600));
        repo.Insert(MakeLabel("l3", sid, cid, price: 200m, printedAt: null));   // queued, not printed

        var t = repo.GetSessionTotals(sid);

        t.PrintedCount.Should().Be(2);
        t.TotalAmount.Should().Be(250m);
        t.UniqueCustomers.Should().Be(1);
    }

    [Fact]
    public void GetTopCustomersBySession_orders_by_amount_desc()
    {
        var (db, repo, sid, _) = Fx();
        using var _2 = db;

        var customers = new CustomerRepository(db);
        customers.Insert(new Customer("c2", "instagram", "@b", null, null,
            100, 100, 0, 0, 0, 100, false, null, null, 0, 0m));
        customers.Insert(new Customer("c3", "instagram", "@c", null, null,
            100, 100, 0, 0, 0, 100, false, null, null, 0, 0m));

        repo.Insert(MakeLabel("l1", sid, "c1", price: 100m, printedAt: 500));
        repo.Insert(MakeLabel("l2", sid, "c1", price: 100m, printedAt: 500));
        repo.Insert(MakeLabel("l3", sid, "c2", price: 500m, printedAt: 500));   // top
        repo.Insert(MakeLabel("l4", sid, "c3", price: 50m,  printedAt: 500));

        var top = repo.GetTopCustomersBySession(sid, limit: 5);

        top.Should().HaveCount(3);
        top[0].Username.Should().Be("@b");          // 500 TL
        top[0].LabelCount.Should().Be(1);
        top[0].TotalAmount.Should().Be(500m);
        top[1].Username.Should().Be("@a");          // 200 TL × 2 labels
        top[1].LabelCount.Should().Be(2);
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelRepositoryTests"
```
Expected: FAIL — `LabelRepository` not found.

- [ ] **Step 3: Implement LabelRepository**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/LabelRepository.cs`:

```csharp
using System.Collections.Generic;
using System.Linq;
using Dapper;
using LiveDeck.Core.Sales;

namespace LiveDeck.Core.Storage.Repositories;

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
        var rows = conn.Query<TopCustomer>(
            @"SELECT Username,
                     Platform,
                     COUNT(*)   AS LabelCount,
                     SUM(Price) AS TotalAmount
              FROM Label
              WHERE SessionId=@sessionId AND PrintedAt IS NOT NULL
              GROUP BY CustomerId, Username, Platform
              ORDER BY SUM(Price) DESC
              LIMIT @limit",
            new { sessionId, limit }).ToList();
        return rows;
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
}

public sealed record SessionTotals(int PrintedCount, decimal TotalAmount, int UniqueCustomers);
public sealed record TopCustomer(string Username, string Platform, int LabelCount, decimal TotalAmount);
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelRepositoryTests"
```
Expected: 5/5 PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/LabelRepository.cs LiveDeck.Tests/Storage/LabelRepositoryTests.cs
git commit -m "feat(core): add LabelRepository with CRUD + session totals + top customers"
```

---

### Task 10: LabelService (TDD)

**Files:**
- Create: `LiveDeck.Core/Sales/LabelService.cs`
- Create: `LiveDeck.Tests/Sales/LabelServiceTests.cs`

LabelService is the small façade the WPF layer will call: `Add(chatMessage, price, code)` creates a label and ensures the customer exists; `MarkPrintedAndRecord(ids)` flips PrintedAt and bumps customer aggregates.

- [ ] **Step 1: Failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Sales/LabelServiceTests.cs`:

```csharp
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

public class LabelServiceTests
{
    private static (LabelService Svc, LabelRepository Labels, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = Mock.Of<IClock>(c => c.UnixNow() == 1000L);
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 1000, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var customerSvc = new CustomerService(customerRepo, clock);
        var labelRepo = new LabelRepository(db);

        var svc = new LabelService(labelRepo, customerSvc, clock);
        return (svc, labelRepo, customerRepo, db, "s1");
    }

    private static ChatMessage Msg(string username = "@ayse_y", string text = "MAVI XL aldım") =>
        new(System.Guid.NewGuid().ToString("N"),
            "instagram", null, username, "Ayşe", null, text, 1000,
            System.Array.Empty<string>());

    [Fact]
    public void Add_creates_customer_and_unprinted_label()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        var label = svc.Add(sid, Msg(), price: 199m, code: "MAVI");

        label.Price.Should().Be(199m);
        label.PrintedAt.Should().BeNull();

        labels.GetUnprintedBySession(sid).Should().HaveCount(1);
        customers.FindByPlatformAndUsername("instagram", "@ayse_y").Should().NotBeNull();
    }

    [Fact]
    public void Add_called_twice_for_same_user_creates_two_labels_one_customer()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;

        svc.Add(sid, Msg(), 100m, "MAVI");
        svc.Add(sid, Msg(), 150m, "MAVI");

        labels.GetUnprintedBySession(sid).Should().HaveCount(2);
    }

    [Fact]
    public void MarkPrintedAndRecord_marks_labels_and_bumps_customer_aggregates()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var l1 = svc.Add(sid, Msg(), 100m, null);
        var l2 = svc.Add(sid, Msg(), 150m, null);

        svc.MarkPrintedAndRecord(new[] { l1.Id, l2.Id });

        labels.GetUnprintedBySession(sid).Should().BeEmpty();
        var c = customers.FindByPlatformAndUsername("instagram", "@ayse_y")!;
        c.TotalLabelsPrinted.Should().Be(2);
        c.TotalAmount.Should().Be(250m);
    }

    [Fact]
    public void MarkPrintedAndRecord_aggregates_per_customer()
    {
        var (svc, labels, customers, db, sid) = Fx();
        using var _ = db;
        var aLabel = svc.Add(sid, Msg("@a", "Mavi"), 100m, null);
        var bLabel = svc.Add(sid, Msg("@b", "Mavi"), 150m, null);

        svc.MarkPrintedAndRecord(new[] { aLabel.Id, bLabel.Id });

        customers.FindByPlatformAndUsername("instagram", "@a")!.TotalAmount.Should().Be(100m);
        customers.FindByPlatformAndUsername("instagram", "@b")!.TotalAmount.Should().Be(150m);
    }
}
```

- [ ] **Step 2: RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelServiceTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement LabelService**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/LabelService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;

namespace LiveDeck.Core.Sales;

public sealed class LabelService
{
    private readonly LabelRepository _labels;
    private readonly CustomerService _customers;
    private readonly IClock _clock;

    public LabelService(LabelRepository labels, CustomerService customers, IClock clock)
    {
        _labels = labels;
        _customers = customers;
        _clock = clock;
    }

    /// <summary>
    /// Queues a new (unprinted) label for the given chat message + price snapshot.
    /// Auto-creates the Customer on first encounter.
    /// </summary>
    public Label Add(string sessionId, ChatMessage message, decimal price, string? code)
    {
        var customer = _customers.GetOrCreate(
            message.Platform, message.Username, message.DisplayName, message.AvatarUrl);

        var label = new Label(
            Id: Guid.NewGuid().ToString("N"),
            SessionId: sessionId,
            CustomerId: customer.Id,
            Platform: message.Platform,
            Username: message.Username,
            MessageText: message.Text,
            Code: code,
            Price: price,
            AddedAt: _clock.UnixNow(),
            PrintedAt: null);

        _labels.Insert(label);
        return label;
    }

    public void Delete(string labelId) => _labels.Delete(labelId);

    public IReadOnlyList<Label> GetQueue(string sessionId) =>
        _labels.GetUnprintedBySession(sessionId);

    /// <summary>
    /// Marks the given labels as printed and increments customer aggregates per-customer.
    /// </summary>
    public void MarkPrintedAndRecord(IReadOnlyList<string> labelIds)
    {
        if (labelIds.Count == 0) return;

        // Read the labels we're about to mark — needed to compute per-customer aggregates.
        // We read them from the queue (unprinted) before flipping PrintedAt.
        // Note: GetUnprintedBySession is per-session; here we don't have a session, so we
        // accept any unprinted label whose Id matches.
        // For simplicity we resolve via repo's already-existing lookups in the WPF layer
        // or, more cleanly, do it in two passes: first read queue, then mark.
        // Since the WPF layer holds the queue list in memory, we expose Add+MarkPrinted
        // here and let the caller do the per-customer rollup. To keep this service
        // self-contained, we resolve the labels via a helper SELECT.
        var groupedAmounts = new Dictionary<string, (int Count, decimal Amount)>();

        foreach (var id in labelIds)
        {
            var lbl = LookupLabel(id);
            if (lbl is null) continue;
            if (groupedAmounts.TryGetValue(lbl.CustomerId, out var agg))
                groupedAmounts[lbl.CustomerId] = (agg.Count + 1, agg.Amount + lbl.Price);
            else
                groupedAmounts[lbl.CustomerId] = (1, lbl.Price);
        }

        _labels.MarkPrinted(labelIds, _clock.UnixNow());

        foreach (var (customerId, agg) in groupedAmounts)
            _customers.RecordPrintedLabels(customerId, agg.Count, agg.Amount);
    }

    private Label? LookupLabel(string id)
    {
        // Inline single-row read; LabelRepository didn't expose GetById to keep its surface
        // small. For one-shot reads in this service, do an ad-hoc query via the same
        // connection factory the repo uses.
        // To avoid duplicating Dapper SQL here, we add a GetById method to LabelRepository
        // in the same task. (Implementation pulled out below — see addendum.)
        return _labels.GetById(id);
    }
}
```

- [ ] **Step 4: Add LabelRepository.GetById**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/LabelRepository.cs`. Inside the class, add this method below `Delete`:

```csharp
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
```

- [ ] **Step 5: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelServiceTests"
```
Expected: 4/4 PASS.

- [ ] **Step 6: Run full test suite to confirm no regressions**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: all tests pass (the migration test, customer tests, session/chat tests, label repo tests, label service tests).

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/LabelService.cs LiveDeck.Core/Storage/Repositories/LabelRepository.cs LiveDeck.Tests/Sales/LabelServiceTests.cs
git commit -m "feat(core): add LabelService for queue-add + mark-printed-and-record flow"
```

---

## Direct Printing Phase

### Task 11: Add `System.Drawing.Common` to LiveDeck.Labeling + extend AppSettings

**Files:**
- Modify: `LiveDeck.Labeling/LiveDeck.Labeling.csproj`
- Modify: `LiveDeck.Core/Settings/AppSettings.cs`

`System.Drawing.Common` provides `PrintDocument` for direct printing on Windows. .NET 10 still ships it but it requires an explicit PackageReference (was framework-included only until .NET 6).

- [ ] **Step 1: Add the package**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.Labeling package System.Drawing.Common --version 9.0.0
```

- [ ] **Step 2: Extend AppSettings with print options**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Settings/AppSettings.cs`. Replace the entire file content with:

```csharp
namespace LiveDeck.Core.Settings;

/// <summary>
/// Application settings persisted to settings.json under the user's Documents folder.
/// All defaults are chosen so the app works out-of-the-box for a new user.
/// </summary>
public sealed class AppSettings
{
    public int OverlayPort { get; set; } = 4747;
    public string ChatTheme { get; set; } = "minimal";
    public int ParserHighConfidence { get; set; } = 80;
    public int ParserLowConfidence { get; set; } = 50;

    // Printing
    public string? PrinterName { get; set; }            // null = use Windows default printer
    public int LabelWidthMm  { get; set; } = 60;
    public int LabelHeightMm { get; set; } = 30;
    public int LabelGapMm    { get; set; } = 5;
    public string LabelFontFamily { get; set; } = "Arial";
    public int   LabelUserFontSize  { get; set; } = 14;
    public int   LabelMessageFontSize { get; set; } = 12;
}
```

The `CaptureOrderHotkey`, `EtiketIntegrationEnabled`, `EtiketWindowTitle` properties from Phase 1 are dropped (no consumers in P1b).

- [ ] **Step 3: Update SettingsStore round-trip test**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Settings/SettingsStoreTests.cs`. Replace its content with:

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
            ParserHighConfidence = 75,
            ParserLowConfidence = 40,
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
        reloaded.ParserHighConfidence.Should().Be(75);
        reloaded.ParserLowConfidence.Should().Be(40);
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

- [ ] **Step 4: Build + test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -3
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~SettingsStoreTests" 2>&1 | tail -3
```
Expected: settings tests pass. Full solution build still has errors (no MainShellViewModel yet). Just confirm the settings layer compiles.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Labeling/LiveDeck.Labeling.csproj LiveDeck.Core/Settings/AppSettings.cs LiveDeck.Tests/Settings/SettingsStoreTests.cs
git commit -m "feat(core): add print settings, drop hotkey/etiket settings, add System.Drawing.Common"
```

---

### Task 12: LabelPrintDocument (layout) — TDD on layout math

**Files:**
- Create: `LiveDeck.Labeling/LabelPrintDocument.cs`
- Create: `LiveDeck.Tests/Labeling/LabelPrintDocumentTests.cs`

We test the layout math (mm→pixel conversion, font sizing decisions) without invoking real printer drivers. The actual `PrintDocument` call is delegated to a thin wrapper in Task 13.

- [ ] **Step 1: Failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Labeling/LabelPrintDocumentTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.Core.Settings;
using LiveDeck.Labeling;
using Xunit;

namespace LiveDeck.Tests.Labeling;

public class LabelPrintDocumentTests
{
    [Fact]
    public void MmToHundredths_converts_60mm_to_correct_imaging_units()
    {
        // PrintDocument page units are 1/100 inch. 1 inch = 25.4 mm.
        // 60mm = 60 / 25.4 inch = ~2.362 inch = ~236 hundredths.
        var hundredths = LabelPrintDocument.MmToHundredths(60);
        hundredths.Should().BeInRange(235, 237);
    }

    [Fact]
    public void MmToHundredths_converts_30mm_correctly()
    {
        var hundredths = LabelPrintDocument.MmToHundredths(30);
        hundredths.Should().BeInRange(117, 119);
    }

    [Fact]
    public void BuildLines_splits_username_and_message_with_price()
    {
        var settings = new AppSettings { LabelFontFamily = "Arial" };
        var lines = LabelPrintDocument.BuildLines("@ayse_y", "MAVI XL aldım", price: 100m);

        lines.Should().HaveCount(2);
        lines[0].Text.Should().Be("@ayse_y");
        lines[0].IsBold.Should().BeTrue();

        lines[1].Text.Should().Contain("MAVI XL aldım");
        lines[1].Text.Should().Contain("100");
    }

    [Fact]
    public void BuildLines_formats_decimal_price_without_trailing_zeros()
    {
        var lines = LabelPrintDocument.BuildLines("@a", "x", 100m);
        lines[1].Text.Should().Contain("100");
        lines[1].Text.Should().NotContain("100.00");
    }

    [Fact]
    public void BuildLines_keeps_two_decimals_when_meaningful()
    {
        var lines = LabelPrintDocument.BuildLines("@a", "x", 99.50m);
        lines[1].Text.Should().Contain("99.5").And.Contain("TL");
    }
}
```

- [ ] **Step 2: RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelPrintDocumentTests"
```
Expected: FAIL.

- [ ] **Step 3: Implement LabelPrintDocument**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Labeling/LabelPrintDocument.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Printing;
using System.Globalization;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Settings;

namespace LiveDeck.Labeling;

/// <summary>
/// Builds a <see cref="PrintDocument"/> that lays out a batch of <see cref="Label"/>s onto
/// thermal-printer-sized pages. The layout is pure math (no actual printing) so it can be
/// unit-tested without driver access. Actual print kick-off lives in <see cref="LabelPrinter"/>.
/// </summary>
public static class LabelPrintDocument
{
    public sealed record Line(string Text, bool IsBold);

    /// <summary>
    /// Converts millimetres to the 1/100-inch units that <see cref="PrintDocument"/> uses
    /// when <c>OriginAtMargins=false</c> and <c>PrinterSettings.DefaultPageSettings</c>
    /// has hundredths-of-an-inch resolution.
    /// </summary>
    public static int MmToHundredths(int mm) => (int)Math.Round(mm * 100.0 / 25.4);

    /// <summary>
    /// Builds the two text lines printed on a label: top = @username (bold), bottom =
    /// message + price (regular).
    /// </summary>
    public static IReadOnlyList<Line> BuildLines(string username, string messageText, decimal price)
    {
        var formattedPrice = FormatPrice(price);
        return new[]
        {
            new Line(username, IsBold: true),
            new Line($"{messageText}  {formattedPrice} TL", IsBold: false)
        };
    }

    private static string FormatPrice(decimal price)
    {
        // Drop trailing zeros so 100.00 → "100", 99.50 → "99.5".
        var s = price.ToString("0.##", CultureInfo.InvariantCulture);
        return s;
    }

    /// <summary>
    /// Builds a fresh <see cref="PrintDocument"/> that, when Print() is called, lays out
    /// the supplied labels one per page.
    /// </summary>
    public static PrintDocument Build(IReadOnlyList<Label> labels, AppSettings settings,
        string? printerName)
    {
        var doc = new PrintDocument
        {
            DocumentName = "LiveDeck Labels"
        };
        if (!string.IsNullOrWhiteSpace(printerName))
            doc.PrinterSettings.PrinterName = printerName;

        var widthHundredths  = MmToHundredths(settings.LabelWidthMm);
        var heightHundredths = MmToHundredths(settings.LabelHeightMm);
        var gapHundredths    = MmToHundredths(settings.LabelGapMm);

        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.DefaultPageSettings.PaperSize =
            new PaperSize("LabelTH", widthHundredths, heightHundredths);

        int index = 0;

        doc.PrintPage += (sender, e) =>
        {
            if (index >= labels.Count)
            {
                e.HasMorePages = false;
                return;
            }

            var label = labels[index];
            var lines = BuildLines(label.Username, label.MessageText, label.Price);

            using var userFont = new Font(settings.LabelFontFamily,
                settings.LabelUserFontSize, FontStyle.Bold);
            using var messageFont = new Font(settings.LabelFontFamily,
                settings.LabelMessageFontSize, FontStyle.Regular);

            float x = e.PageBounds.Left;
            float pageWidth = e.PageBounds.Width;

            // Username line — top half, centered horizontally
            var userSize = e.Graphics!.MeasureString(lines[0].Text, userFont);
            float userY = (heightHundredths * 0.15f);
            float userX = (pageWidth - userSize.Width) / 2;
            e.Graphics.DrawString(lines[0].Text, userFont, Brushes.Black, userX, userY);

            // Message line — bottom half, centered horizontally
            var msgSize = e.Graphics.MeasureString(lines[1].Text, messageFont);
            float msgY = heightHundredths * 0.55f;
            float msgX = (pageWidth - msgSize.Width) / 2;
            e.Graphics.DrawString(lines[1].Text, messageFont, Brushes.Black, msgX, msgY);

            index++;
            e.HasMorePages = index < labels.Count;
        };

        return doc;
    }
}
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LabelPrintDocumentTests"
```
Expected: 5/5 PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Labeling/LabelPrintDocument.cs LiveDeck.Tests/Labeling/LabelPrintDocumentTests.cs
git commit -m "feat(labeling): add LabelPrintDocument layout (mm→hundredths, line builder)"
```

---

### Task 13: LabelPrinter (kick-off wrapper)

**Files:**
- Create: `LiveDeck.Labeling/LabelPrinter.cs`

The printer is a thin wrapper around `PrintDocument.Print()`. It's deliberately not unit-tested — invoking real printer drivers under test would be flaky. Manual verification happens at the end of Task 20 when the user smoke-tests the app.

- [ ] **Step 1: Implement LabelPrinter**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Labeling/LabelPrinter.cs`:

```csharp
using System.Collections.Generic;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Settings;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LiveDeck.Labeling;

/// <summary>
/// Sends a batch of labels to the configured printer via Windows printing subsystem.
/// Printer-independent — works with any Windows-driver-backed printer.
/// </summary>
public sealed class LabelPrinter
{
    private readonly AppSettings _settings;
    private readonly ILogger<LabelPrinter> _log;

    public LabelPrinter(AppSettings settings, ILogger<LabelPrinter>? log = null)
    {
        _settings = settings;
        _log = log ?? NullLogger<LabelPrinter>.Instance;
    }

    /// <summary>
    /// Prints the given labels in order. Throws if there is no labels to print, or if the
    /// configured printer is missing.
    /// </summary>
    public void Print(IReadOnlyList<Label> labels)
    {
        if (labels.Count == 0)
        {
            _log.LogInformation("Print called with empty label batch — no-op");
            return;
        }

        using var doc = LabelPrintDocument.Build(labels, _settings, _settings.PrinterName);
        _log.LogInformation("Printing {Count} label(s) on '{Printer}'",
            labels.Count, doc.PrinterSettings.PrinterName);

        doc.Print();
    }
}
```

- [ ] **Step 2: Build the Labeling project**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Labeling 2>&1 | tail -3
```
Expected: 0/0.

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Labeling/LabelPrinter.cs
git commit -m "feat(labeling): add LabelPrinter kick-off wrapper"
```

---

## WPF Rewrite Phase

### Task 14: MainShellViewModel — single-screen orchestrator

**Files:**
- Create: `LiveDeck.App/ViewModels/MainShellViewModel.cs`

This VM owns the live chat list, the print queue, the current Code/Price inputs, and the stream lifecycle (Start/End). No tabs, no navigation. The View binds to it directly.

- [ ] **Step 1: Implement MainShellViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core.Chat;
using LiveDeck.Core.Sales;
using LiveDeck.Core.Sessions;
using LiveDeck.Labeling;

namespace LiveDeck.App.ViewModels;

public sealed partial class MainShellViewModel : ViewModelBase, IDisposable
{
    private readonly LabelService _labels;
    private readonly StreamSessionService _sessions;
    private readonly LabelPrinter _printer;
    private readonly Dispatcher _dispatcher;
    private readonly IDisposable _busSubscription;

    private const int MaxChatMessages = 200;

    /// <summary>Live merged chat (read-only in the UI; double-click adds to print queue).</summary>
    public ObservableCollection<ChatMessage> ChatMessages { get; } = new();

    /// <summary>Etiketler waiting to be printed.</summary>
    public ObservableCollection<Label> PrintQueue { get; } = new();

    [ObservableProperty] private string _activeCode = "";
    [ObservableProperty] private string _activePriceText = "0";
    [ObservableProperty] private string _streamStatusLabel = "Yayın aktif değil";

    public MainShellViewModel(
        IChatBus bus,
        LabelService labels,
        StreamSessionService sessions,
        LabelPrinter printer)
    {
        _labels = labels;
        _sessions = sessions;
        _printer = printer;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _busSubscription = bus.Subscribe(OnChatMessage);

        UpdateStreamStatusLabel();
        ReloadQueueFromActiveSession();
    }

    private void OnChatMessage(ChatMessage m)
    {
        _dispatcher.BeginInvoke(() =>
        {
            ChatMessages.Add(m);
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
        foreach (var l in _labels.GetQueue(session.Id)) PrintQueue.Add(l);
    }

    [RelayCommand] private void StartStream()
    {
        if (_sessions.GetActive() is not null)
        {
            MessageBox.Show("Zaten aktif bir yayın var. Önce mevcut yayını bitir.",
                "Yayın aktif", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _sessions.Start("Yeni Yayın", new[] { "instagram" });
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

        // Auto-print any unprinted labels before closing the session (user choice from
        // brainstorming: "B — otomatik yazdır → sonra rapor").
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

        // Open the report dialog. Resolution via App.Host so we don't wire it through DI here.
        var dialog = App.Host.Services
            .GetRequiredService<Views.StreamReportDialog>();
        dialog.LoadReport(session.Id);
        dialog.Owner = Application.Current?.MainWindow;
        dialog.ShowDialog();
    }

    /// <summary>
    /// Adds the chat message to the print queue with the *current* ActivePrice and ActiveCode.
    /// Called from MainShellView's chat ListBox MouseDoubleClick handler.
    /// </summary>
    public void AddChatToQueue(ChatMessage message)
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

        var label = _labels.Add(session.Id, message, price,
            string.IsNullOrWhiteSpace(ActiveCode) ? null : ActiveCode.Trim());
        PrintQueue.Add(label);
    }

    [RelayCommand]
    private void RemoveSelectedFromQueue(Label? selected)
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

        foreach (var label in PrintQueue.ToList()) _labels.Delete(label.Id);
        PrintQueue.Clear();
    }

    [RelayCommand]
    private void Print()
    {
        if (PrintQueue.Count == 0) return;

        var snapshot = PrintQueue.ToList();
        _printer.Print(snapshot);
        _labels.MarkPrintedAndRecord(snapshot.Select(l => l.Id).ToList());
        PrintQueue.Clear();
    }

    private static bool TryParsePrice(string text, out decimal price)
    {
        return decimal.TryParse(text, NumberStyles.Any, CultureInfo.InvariantCulture, out price)
            || decimal.TryParse(text, NumberStyles.Any, new CultureInfo("tr-TR"), out price);
    }

    public void Dispose() => _busSubscription.Dispose();
}
```

The `App.Host.Services.GetRequiredService<...>()` calls assume `App.Host` is the static AppHost instance from Phase 1. That stays.

- [ ] **Step 2: Add the Microsoft.Extensions.DependencyInjection using import**

The MainShellViewModel above uses `GetRequiredService` which is in `Microsoft.Extensions.DependencyInjection`. Add to the top of the file:

```csharp
using Microsoft.Extensions.DependencyInjection;
```

(Add it next to the existing usings — alphabetical order is fine.)

- [ ] **Step 3: Build the App project (will fail until MainShellView + StreamReportDialog exist — Tasks 15-17)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -10
```

Expected: errors about missing `Views.StreamReportDialog`. Fixed in Task 17.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/MainShellViewModel.cs
git commit -m "feat(app): add MainShellViewModel orchestrating chat, queue, stream lifecycle"
```

---

### Task 15: MainShellView (single-screen layout)

**Files:**
- Create: `LiveDeck.App/Views/MainShellView.xaml` + `.xaml.cs`

- [ ] **Step 1: MainShellView.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml`:

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

        <!-- Top bar: Code/Price + Start/End -->
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
            </Grid.ColumnDefinitions>

            <TextBlock Grid.Column="0" Text="Kod:" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBox  Grid.Column="1" Text="{Binding ActiveCode, UpdateSourceTrigger=PropertyChanged}"
                      Padding="6" FontSize="14"/>

            <TextBlock Grid.Column="3" Text="Fiyat:" VerticalAlignment="Center" Margin="0,0,8,0"/>
            <TextBox  Grid.Column="4" Text="{Binding ActivePriceText, UpdateSourceTrigger=PropertyChanged}"
                      Padding="6" FontSize="14" />

            <Button   Grid.Column="6" Content="Yayın Başlat"
                      Command="{Binding StartStreamCommand}" Padding="14,6" Margin="0,0,8,0"/>
            <Button   Grid.Column="7" Content="Yayını Bitir"
                      Command="{Binding EndStreamCommand}"   Padding="14,6"/>
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
                    <ListBox.ItemTemplate>
                        <DataTemplate>
                            <StackPanel Orientation="Horizontal" Margin="0,4">
                                <TextBlock Text="{Binding Platform}" FontWeight="Bold"
                                           Foreground="#FFFFD166" Width="80"/>
                                <TextBlock Text="{Binding Username}" FontWeight="Bold"
                                           Width="160" TextTrimming="CharacterEllipsis"/>
                                <TextBlock Text="{Binding Text}" TextWrapping="Wrap"/>
                            </StackPanel>
                        </DataTemplate>
                    </ListBox.ItemTemplate>
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

- [ ] **Step 2: MainShellView.xaml.cs (handles double-click)**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml.cs`:

```csharp
using System.Windows.Controls;
using System.Windows.Input;
using LiveDeck.App.ViewModels;
using LiveDeck.Core.Chat;
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
            && ChatList.SelectedItem is ChatMessage message)
        {
            vm.AddChatToQueue(message);
        }
    }
}
```

- [ ] **Step 3: Replace MainWindow.xaml to host MainShellView**

Replace the entire content of `LiveDeck.App/MainWindow.xaml` with:

```xml
<Window x:Class="LiveDeck.App.MainWindow"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:views="clr-namespace:LiveDeck.App.Views"
        Title="LiveDeck" Height="800" Width="1280"
        Background="#FF1A1A1A" Foreground="White">
    <views:MainShellView/>
</Window>
```

`MainWindow.xaml.cs` stays as the minimal version from Task 3 (no DataContext lookup needed — the inner UserControl handles that).

- [ ] **Step 4: Build the App (still missing StreamReportDialog — Task 17)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -10
```

Expected: error about missing `Views.StreamReportDialog`. Fixed in Task 17.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/Views/MainShellView.xaml LiveDeck.App/Views/MainShellView.xaml.cs LiveDeck.App/MainWindow.xaml
git commit -m "feat(app): add MainShellView single-screen layout with double-click queueing"
```

---

### Task 16: Add ClosedXML for Excel export

**Files:**
- Modify: `LiveDeck.App/LiveDeck.App.csproj`

The stream report dialog needs to export to `.xlsx`. ClosedXML is the standard library — pure managed, no Office dependency.

- [ ] **Step 1: Add ClosedXML**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet add LiveDeck.App package ClosedXML --version 0.105.0
```

- [ ] **Step 2: Build**

```bash
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: still errors about missing StreamReportDialog (next task). Just confirm ClosedXML resolved (no NU1605 / NU1101).

- [ ] **Step 3: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/LiveDeck.App.csproj
git commit -m "chore(app): add ClosedXML for stream report Excel export"
```

---

### Task 17: StreamReportViewModel + StreamReportDialog

**Files:**
- Create: `LiveDeck.App/ViewModels/StreamReportViewModel.cs`
- Create: `LiveDeck.App/Views/StreamReportDialog.xaml` + `.xaml.cs`

The report shows totals + top customers + an Excel export button. **Parolasız** per user decision.

- [ ] **Step 1: StreamReportViewModel**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/StreamReportViewModel.cs`:

```csharp
using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using ClosedXML.Excel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Core;
using LiveDeck.Core.Sessions;
using LiveDeck.Core.Storage.Repositories;
using Microsoft.Win32;

namespace LiveDeck.App.ViewModels;

public sealed partial class StreamReportViewModel : ViewModelBase
{
    private readonly LabelRepository _labels;
    private readonly SessionRepository _sessions;

    [ObservableProperty] private string _durationLabel = "—";
    [ObservableProperty] private int    _totalLabels;
    [ObservableProperty] private decimal _totalAmount;
    [ObservableProperty] private int    _uniqueCustomers;

    public ObservableCollection<TopCustomer> TopCustomers { get; } = new();

    private string? _sessionId;

    public StreamReportViewModel(LabelRepository labels, SessionRepository sessions)
    {
        _labels = labels;
        _sessions = sessions;
    }

    public void Load(string sessionId)
    {
        _sessionId = sessionId;

        // Get session for duration
        var totals = _labels.GetSessionTotals(sessionId);
        TotalLabels = totals.PrintedCount;
        TotalAmount = totals.TotalAmount;
        UniqueCustomers = totals.UniqueCustomers;

        TopCustomers.Clear();
        foreach (var c in _labels.GetTopCustomersBySession(sessionId, limit: 10))
            TopCustomers.Add(c);

        // Duration
        var session = LookupSession(sessionId);
        if (session is not null)
        {
            var endedAt = session.EndedAt ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var seconds = endedAt - session.StartedAt;
            DurationLabel = FormatDuration(seconds);
        }
    }

    private StreamSession? LookupSession(string sessionId)
    {
        // SessionRepository in P1 only exposes GetActive(). For the report we want a
        // specific session; rather than expanding the repo here we accept the active one
        // (which is what the UI flow always uses — we open the report immediately after
        // ending the session, so the session was just ended).
        // GetActive returns null after End(), so we re-query: in P1b we accept that the
        // duration is computed from session lookups added in step below.
        // Simplest: extend SessionRepository.GetById in step 4.
        return _sessions.GetById(sessionId);
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0) return "—";
        var ts = TimeSpan.FromSeconds(seconds);
        if (ts.TotalHours >= 1)
            return $"{(int)ts.TotalHours} saat {ts.Minutes} dakika";
        return $"{ts.Minutes} dakika {ts.Seconds} saniye";
    }

    [RelayCommand]
    private void ExportToExcel()
    {
        if (_sessionId is null) return;

        var dlg = new SaveFileDialog
        {
            Filter = "Excel Workbook|*.xlsx",
            FileName = $"livedeck-rapor-{DateTime.Now:yyyy-MM-dd-HHmm}.xlsx",
            InitialDirectory = AppPaths.ReportsFolder
        };
        if (dlg.ShowDialog() != true) return;

        try
        {
            using var wb = new XLWorkbook();
            var ws = wb.Worksheets.Add("Rapor");
            ws.Cell(1, 1).Value = "Yayın Raporu";
            ws.Cell(1, 1).Style.Font.Bold = true;

            ws.Cell(3, 1).Value = "Süre";        ws.Cell(3, 2).Value = DurationLabel;
            ws.Cell(4, 1).Value = "Toplam etiket"; ws.Cell(4, 2).Value = TotalLabels;
            ws.Cell(5, 1).Value = "Toplam ciro";  ws.Cell(5, 2).Value = TotalAmount;
            ws.Cell(6, 1).Value = "Tekil müşteri"; ws.Cell(6, 2).Value = UniqueCustomers;

            ws.Cell(8, 1).Value = "En çok alan müşteriler";
            ws.Cell(8, 1).Style.Font.Bold = true;

            ws.Cell(9, 1).Value = "Kullanıcı";
            ws.Cell(9, 2).Value = "Platform";
            ws.Cell(9, 3).Value = "Etiket";
            ws.Cell(9, 4).Value = "Tutar (TL)";
            ws.Range(9, 1, 9, 4).Style.Font.Bold = true;

            int row = 10;
            foreach (var c in TopCustomers)
            {
                ws.Cell(row, 1).Value = c.Username;
                ws.Cell(row, 2).Value = c.Platform;
                ws.Cell(row, 3).Value = c.LabelCount;
                ws.Cell(row, 4).Value = c.TotalAmount;
                row++;
            }

            ws.Columns().AdjustToContents();
            wb.SaveAs(dlg.FileName);

            MessageBox.Show($"Rapor kaydedildi:\n{dlg.FileName}",
                "Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Excel'e aktarma başarısız: {ex.Message}",
                "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
```

- [ ] **Step 2: Add `SessionRepository.GetById`**

Open `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/SessionRepository.cs`. Inside the class, add this method:

```csharp
    public StreamSession? GetById(string id)
    {
        using var conn = _factory.Open();
        var row = conn.QueryFirstOrDefault<Row>(
            "SELECT Id, Title, StartedAt, EndedAt, Platforms, Notes " +
            "FROM StreamSession WHERE Id=@id", new { id });
        return row is null ? null : Map(row);
    }
```

- [ ] **Step 3: StreamReportDialog.xaml**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/StreamReportDialog.xaml`:

```xml
<Window x:Class="LiveDeck.App.Views.StreamReportDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="Yayın Raporu" Width="600" Height="600"
        WindowStartupLocation="CenterOwner"
        Background="#FF1A1A1A" Foreground="White">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Yayın Raporu" FontSize="24" FontWeight="Bold"
                   Margin="0,0,0,16"/>

        <Grid Grid.Row="1" Margin="0,0,0,16">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="160"/>
                <ColumnDefinition Width="*"/>
            </Grid.ColumnDefinitions>
            <Grid.RowDefinitions>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
                <RowDefinition Height="Auto"/>
            </Grid.RowDefinitions>

            <TextBlock Grid.Row="0" Grid.Column="0" Text="Süre:"          Foreground="#FFAAAAAA"/>
            <TextBlock Grid.Row="0" Grid.Column="1" Text="{Binding DurationLabel}" FontWeight="Bold"/>

            <TextBlock Grid.Row="1" Grid.Column="0" Text="Toplam etiket:"  Foreground="#FFAAAAAA"/>
            <TextBlock Grid.Row="1" Grid.Column="1" Text="{Binding TotalLabels}" FontWeight="Bold"/>

            <TextBlock Grid.Row="2" Grid.Column="0" Text="Toplam ciro:"    Foreground="#FFAAAAAA"/>
            <TextBlock Grid.Row="2" Grid.Column="1"
                       Text="{Binding TotalAmount, StringFormat={}{0:N2} TL}"
                       FontWeight="Bold" Foreground="#FFFFD166"/>

            <TextBlock Grid.Row="3" Grid.Column="0" Text="Tekil müşteri:"  Foreground="#FFAAAAAA"/>
            <TextBlock Grid.Row="3" Grid.Column="1" Text="{Binding UniqueCustomers}" FontWeight="Bold"/>
        </Grid>

        <TextBlock Grid.Row="2" Text="En çok alan müşteriler" FontSize="16" FontWeight="Bold"
                   Margin="0,0,0,8"/>

        <DataGrid Grid.Row="3"
                  ItemsSource="{Binding TopCustomers}"
                  AutoGenerateColumns="False"
                  IsReadOnly="True"
                  HeadersVisibility="Column"
                  Background="#FF1A1A1A"
                  Foreground="White"
                  BorderBrush="#FF333333"
                  RowBackground="#FF1A1A1A"
                  AlternatingRowBackground="#FF222222">
            <DataGrid.Columns>
                <DataGridTextColumn Header="Kullanıcı" Binding="{Binding Username}"   Width="2*"/>
                <DataGridTextColumn Header="Platform"  Binding="{Binding Platform}"   Width="*"/>
                <DataGridTextColumn Header="Etiket"    Binding="{Binding LabelCount}" Width="*"/>
                <DataGridTextColumn Header="Tutar"
                                    Binding="{Binding TotalAmount, StringFormat={}{0:N2} TL}"
                                    Width="*"/>
            </DataGrid.Columns>
        </DataGrid>

        <StackPanel Grid.Row="4" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button Content="Excel'e Aktar" Command="{Binding ExportToExcelCommand}" Padding="14,6"/>
            <Button Content="Kapat"          Click="OnClose" Padding="14,6" Margin="8,0,0,0"
                    IsCancel="True" IsDefault="True"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 4: StreamReportDialog.xaml.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/StreamReportDialog.xaml.cs`:

```csharp
using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App.Views;

public partial class StreamReportDialog : Window
{
    private readonly StreamReportViewModel _vm;

    public StreamReportDialog()
    {
        InitializeComponent();
        _vm = App.Host.Services.GetRequiredService<StreamReportViewModel>();
        DataContext = _vm;
    }

    public void LoadReport(string sessionId) => _vm.Load(sessionId);

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
```

- [ ] **Step 5: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -5
```

Expected: errors about missing service registrations (Task 19 fixes that), but no syntax errors in the new files.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/StreamReportViewModel.cs LiveDeck.App/Views/StreamReportDialog.xaml LiveDeck.App/Views/StreamReportDialog.xaml.cs LiveDeck.Core/Storage/Repositories/SessionRepository.cs
git commit -m "feat(app): add StreamReport dialog with totals + top customers + Excel export"
```

---

### Task 18: Restore reports folder + finalize App.xaml

**Files:**
- Modify: `LiveDeck.App/App.xaml` (no app-level styles needed for now — keep clean)

The reports save to `AppPaths.ReportsFolder` which is created in `AppPaths.EnsureDirectoriesExist()` (already called by AppHost ctor in Phase 1). No changes needed there.

App.xaml is minimal — no converters, no sidebar styles. The Phase 1 sidebar style was deleted in Task 3. The empty resource dictionary is fine for now.

- [ ] **Step 1: Confirm App.xaml content**

Read `LiveDeck.App/App.xaml`. It should match this (from Task 3):

```xml
<Application x:Class="LiveDeck.App.App"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             StartupUri="MainWindow.xaml">
    <Application.Resources>
        <ResourceDictionary>
        </ResourceDictionary>
    </Application.Resources>
</Application>
```

If anything differs, fix it.

- [ ] **Step 2: No commit needed if no changes**

---

## Final Wiring Phase

### Task 19: AppHost — register the P1b service set

**Files:**
- Replace contents: `LiveDeck.App/AppHost.cs`

After all the deletions and additions, AppHost should register exactly the services P1b needs.

- [ ] **Step 1: Replace AppHost.cs**

Replace the entire content of `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs` with:

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

        // Chat plumbing (unchanged from P1)
        services.AddSingleton<IChatBus>(_ => new ChatBus(ringBufferSize: 200));
        services.AddSingleton(sp => new ExtensionBridgeServer(
            sp.GetRequiredService<IChatBus>(),
            port: 4748,
            log: sp.GetRequiredService<ILogger<ExtensionBridgeServer>>()));
        services.AddSingleton<InstagramIngestor>();

        // Overlay (unchanged from P1)
        services.AddSingleton(sp => new OverlayHost(
            sp.GetRequiredService<IChatBus>(),
            port: sp.GetRequiredService<AppSettings>().OverlayPort,
            log: sp.GetRequiredService<ILogger<OverlayHost>>()));

        // Printing
        services.AddSingleton(sp => new LabelPrinter(
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<ILogger<LabelPrinter>>()));

        // ViewModels + dialogs
        services.AddSingleton<ViewModels.MainShellViewModel>();
        services.AddTransient<ViewModels.StreamReportViewModel>();
        services.AddTransient<Views.StreamReportDialog>();

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

The dialog and its VM are `Transient` — each `EndStream` call resolves a fresh instance.

- [ ] **Step 2: Build the full solution**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
```

Expected: `Build succeeded.` 0 warnings, 0 errors.

If you get errors:
- "Type or namespace 'X' could not be found" → check that the `using` is present at the top of AppHost (System.Drawing.Common pulls in `LabelPrinter` via LiveDeck.Labeling).
- "OrderRepository / ActiveCodeRepository not registered" → make sure those lines were deleted in Task 2.
- "MainViewModel not found" → verify Task 3 deleted MainViewModel and that no XAML still references it.

- [ ] **Step 3: Run the test suite to confirm no regressions**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -5
```

Expected: ALL tests pass. Approximate count: ~25-30 (P1's 85 minus the deleted pipeline/order/activecode tests).

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/AppHost.cs
git commit -m "feat(app): rewire AppHost for P1b (label workflow)"
```

---

### Task 20: Final acceptance — manual smoke

**No new files. Manual user verification.**

- [ ] **Step 1: Run the WPF app**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

Expected: window opens, single screen layout — Code/Price inputs at top, "Yayın aktif değil" status, two empty list boxes side by side.

- [ ] **Step 2: Start a stream**

Click "Yayın Başlat". Status changes to "Yayın aktif (başlangıç: HH:mm)".

- [ ] **Step 3: Set Code + Price**

Type `MAVI` into Kod, `100` into Fiyat.

- [ ] **Step 4: Connect the browser extension and open Instagram Live**

Make sure the LiveDeck Extension is loaded in Chrome (from Phase 1 Task 25). Open an Instagram Live page or test page that emits chat events.

Verify in the left "Canlı Chat" pane that messages start appearing.

- [ ] **Step 5: Double-click a chat row**

Pick any chat row, double-click. Verify it appears in the right "Yazdırılacak Etiketler" pane with the current price (100 TL) and the original message text.

Try changing the price to `150` and double-click another message — confirm the new entry shows 150 TL.

Try double-clicking the SAME message twice — confirm it produces two queue entries.

- [ ] **Step 6: Print**

Make sure a printer is set up (Windows Printers and Scanners). Either set `PrinterName` in `Documents/LiveDeck/settings.json`, or leave null to use the Windows default printer.

Click "Yazdır". Confirm:
- The configured printer (or Windows default) prints the queued labels — one per page, with @username on top and message+price on bottom
- The right-side queue clears after printing
- The labels in the DB now have `PrintedAt` set:
  ```bash
  sqlite3 ~/Documents/LiveDeck/data/livedeck.db \
    "SELECT Username, Price, PrintedAt FROM Label ORDER BY AddedAt DESC LIMIT 10;"
  ```

If you don't have a real printer, install "Microsoft Print to PDF" — it's free and Windows-built-in. Selecting it as PrinterName lets you "print" to a PDF file and visually verify the layout.

- [ ] **Step 7: End the stream and verify the report**

Click "Yayını Bitir" → "Evet". The dialog opens with:
- Süre (e.g. "5 dakika")
- Toplam etiket (matches what you printed)
- Toplam ciro (sum of prices)
- Tekil müşteri (distinct usernames)
- Top customers DataGrid populated

Click "Excel'e Aktar" → save to a file → open in Excel/LibreOffice → verify columns match.

Click "Kapat" → dialog closes → status returns to "Yayın aktif değil".

- [ ] **Step 8: Verify staff hidden behavior**

Open the app fresh (or just look at the main window during the stream). Confirm:
- ✅ No total ciro visible anywhere on the main screen
- ✅ No total etiket count visible
- ✅ Per-line price visible in queue (decision B from brainstorming)
- ✅ Stream report only opens via "Yayını Bitir"
- ✅ No password prompt

- [ ] **Step 9: Optional — verify OBS overlay still works**

Open OBS → Browser Source → `http://localhost:4747/overlay/chat` 500×800. Confirm chat messages appear in OBS as before (Phase 1 Task 28 functionality preserved).

- [ ] **Step 10: Tag the milestone**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git tag -a phase-1b -m "Phase 1b — manual label workflow pivot complete"
```

---

## Plan self-review

Cross-checked against the brainstorming decisions:

| Decision | Implementation |
|---|---|
| No tabs, single screen | Tasks 14-15 (MainShellView replaces sidebar) |
| Top-bar Kod + Fiyat as live inputs | Task 15 XAML (`ActiveCode`, `ActivePriceText` bindings) |
| Price snapshot at queue-add time | Task 14 (`AddChatToQueue` reads `ActivePriceText` at the moment of click) |
| Double-click to queue | Task 15 (`MainShellView.xaml.cs::ChatList_OnDoubleClick`) |
| Direct printing (no etiket.exe) | Tasks 11-13 (`LabelPrinter` + `LabelPrintDocument`) |
| Same message multiple times | Task 14 design (each call to `AddChatToQueue` creates a new Label row) |
| Customer aggregates for end-of-stream report | Task 7 (`Customer.TotalLabelsPrinted/TotalAmount`) + Task 8 (`IncrementLabelStats`) + Task 10 (`MarkPrintedAndRecord`) |
| Per-line price visible, total ciro hidden | Task 15 XAML (each row shows price; status bar empty) |
| Stream report at "Yayını Bitir" — modal | Task 17 (`StreamReportDialog`) |
| No password on report | Task 17 (no auth gate in `LoadReport`) |
| Auto-print unprinted at end of stream | Task 14 `EndStream` calls `Print()` if `PrintQueue.Count > 0` |
| Excel export | Task 16 (ClosedXML) + Task 17 (`ExportToExcelCommand`) |

**Placeholder scan:** searched plan for "TBD", "TODO", "implement later" — none found.

**Type consistency:** verified `MainShellViewModel` constructor params (LabelService, StreamSessionService, LabelPrinter, IChatBus) match AppHost registrations. Verified `LabelService.Add` signature (sessionId, ChatMessage, decimal, string?) matches the call from `MainShellViewModel.AddChatToQueue`. Verified `LabelRepository.GetSessionTotals` returns `SessionTotals(int, decimal, int)` — used identically by `StreamReportViewModel.Load`.

**Spec coverage gaps:** none material. The only thing not in this plan is the Phase 2 Çekiliş feature, which was explicitly out of scope.

---

## Execution

Plan complete and saved to `docs/superpowers/plans/2026-04-27-phase-1b-manual-label-workflow.md`.

Two execution options:

**1. Subagent-Driven (recommended)** — fresh subagent per task, two-stage review between tasks.
**2. Inline Execution** — batch with checkpoints in this session.

**Which approach?**

