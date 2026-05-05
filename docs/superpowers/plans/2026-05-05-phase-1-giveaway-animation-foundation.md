# Phase 1 — Giveaway Animation Plugin Foundation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Refactor the single-style giveaway overlay into a plugin host that loads animations dynamically, with the existing wheel migrated to be the first plugin. Server, settings, and operator UI gain the scaffolding to pick a default animation. No new visual styles ship in this phase — Phase 2 adds them.

**Architecture:** ES-module plugins under `OrderDeck.Overlay/wwwroot/animations/<id>/index.js` with a `manifest.json` index. `giveaway.js` becomes a thin host that dynamically imports the chosen plugin. Server adds `Giveaway.AnimationId` (default `"wheel"` for backward compat), `AppSettings.GiveawayAnimation`, and pushes the chosen id + audio settings in `giveaway.started`. WPF adds an `AnimationPickerControl` reused inside a new Settings tab.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions + Moq + InMemorySqlite, WPF (CommunityToolkit.Mvvm), vanilla JS ES modules.

**Spec:** [`docs/superpowers/specs/2026-05-05-giveaway-animations-pluggable-design.md`](../specs/2026-05-05-giveaway-animations-pluggable-design.md)

---

## File Structure

### New files

- `OrderDeck.Core/Storage/Migrations/013_giveaway_animation.sql` — DB migration
- `OrderDeck.Core/Sales/AnimationCatalog.cs` — server-side known-animations registry (Phase 1 ships only `"wheel"`)
- `OrderDeck.Overlay/wwwroot/animations/manifest.json` — overlay-side manifest
- `OrderDeck.Overlay/wwwroot/animations/wheel/index.js` — wheel plugin (1:1 port)
- `OrderDeck.Overlay/wwwroot/animations/wheel/style.css` — wheel-scoped styles (split from giveaway.css)
- `OrderDeck.Overlay/wwwroot/animations/wheel/README.md` — plugin doc placeholder
- `OrderDeck.Overlay/wwwroot/animations/wheel/thumbnail.svg` — Phase 1 placeholder (real WebP comes Phase 2 alongside the gallery polish)
- `OrderDeck.Overlay/wwwroot/audio-controller.js` — shared AudioController utility
- `OrderDeck.App/Controls/AnimationPickerControl.xaml` (+ `.cs`) — reusable gallery
- `OrderDeck.App/ViewModels/AnimationPickerViewModel.cs` — picker view-model
- `OrderDeck.App/ViewModels/AnimationCatalogClient.cs` — fetches manifest.json from the local OverlayHost for the picker UI
- `OrderDeck.Tests/ViewModels/AnimationPickerViewModelTests.cs`
- `OrderDeck.Tests/Sales/GiveawayServiceAnimationTests.cs`
- `OrderDeck.Tests/Settings/SettingsStoreGiveawayAnimationTests.cs`
- `OrderDeck.Tests/Storage/GiveawayRepositoryAnimationTests.cs`

### Modified files

- `OrderDeck.Core/Sales/Giveaway.cs` — add `AnimationId` field
- `OrderDeck.Core/Storage/Repositories/GiveawayRepository.cs` — INSERT/SELECT cover the new column
- `OrderDeck.Core/Sales/GiveawayService.cs` — `Start` gains `animationId`, fallback logic, fires event with id
- `OrderDeck.Core/Sales/GiveawayEvents.cs` — `GiveawayStartedEvent` gains `AnimationId`, `AudioVolume`, `AudioMuted`
- `OrderDeck.Core/Settings/AppSettings.cs` — add `GiveawayAnimationSettings GiveawayAnimation`
- `OrderDeck.Overlay/OverlayHost.cs` — broadcast new event fields; takes a callback for current audio settings
- `OrderDeck.Overlay/wwwroot/giveaway.js` — refactor into plugin host (kept thin)
- `OrderDeck.Overlay/wwwroot/giveaway.html` — replace fixed wheel DOM with `<div id="plugin-stage">`
- `OrderDeck.Overlay/wwwroot/giveaway.css` — strip wheel-specific CSS (moves into `animations/wheel/style.css`)
- `OrderDeck.App/Views/SettingsDialog.xaml` — new TabItem "Çekiliş Animasyonu"
- `OrderDeck.App/ViewModels/SettingsViewModel.cs` — new picker bindings + audio controls
- `OrderDeck.App/ViewModels/MainShellViewModel.cs` — pass settings default into `GiveawayService.Start`
- `OrderDeck.App/AppHost.cs` — wire `OverlayHost` audio-settings callback
- `OrderDeck.Tests/App/MainShellTestHarness.cs` — pass new optional animation parameter so existing harness still compiles

---

## Conventions used in this plan

- Tests use xUnit + FluentAssertions; existing pattern is `Sut_Method_Behaviour` with `_` separators (see `GiveawayServiceTests` for reference).
- Every C# task ends with a build + targeted test run before commit.
- JS / HTML / CSS / XAML changes don't have automated unit tests in this repo; the plan adds **manual smoke checklist** items at the end. The host refactor (Tasks 13-17) commits in one logical PR with the smoke items run by the operator.
- Each task is one commit. Use the existing message style (`type(scope): subject` + Co-Authored-By).
- Run the relevant focused tests after each task with `dotnet test --filter "FullyQualifiedName~<class>"`.

---

I've split the implementation into **task groups** for readability. Each task's checklist still expands to bite-sized steps when executed.

## Group A — Server domain & storage (Tasks 1-4)

### Task 1: Add `AnimationId` to `Giveaway` record

**Files:**
- Modify: `OrderDeck.Core/Sales/Giveaway.cs`

- [ ] **Step 1: Read the current record.** It ends with positional params `StartedAt, EndedAt, CancelledAt`.

- [ ] **Step 2: Add `AnimationId` as the new last positional parameter with no default.** A non-default reference type is correct because every code path constructs Giveaway through `GiveawayService.Start`, which we update in Task 5; intermediate compile errors are the safety net.

```csharp
public sealed record Giveaway(
    string Id,
    string SessionId,
    string Keyword,
    int DurationSeconds,
    int WinnerCount,
    IReadOnlyList<string>? PlatformFilter,
    bool PreventRewinning,
    string RandomSeed,
    long StartedAt,
    long? EndedAt,
    long? CancelledAt,
    string AnimationId);
```

- [ ] **Step 3: Build the solution.** `dotnet build OrderDeck.sln --nologo`. Compile errors are EXPECTED in `GiveawayService.Start`, `GiveawayRepository.Insert`, `GiveawayRepository.Map`, plus any test fixtures. The next tasks fix them in order.

- [ ] **Step 4: Commit.**

```bash
git add OrderDeck.Core/Sales/Giveaway.cs
git commit -m "feat(giveaway): add AnimationId field to Giveaway record"
```

---

### Task 2: Migration `013_giveaway_animation.sql`

**Files:**
- Create: `OrderDeck.Core/Storage/Migrations/013_giveaway_animation.sql`
- Modify: `OrderDeck.Core/OrderDeck.Core.csproj` (verify migrations are embedded — existing `<EmbeddedResource>` line covers `Storage/Migrations/*.sql`; no edit needed if pattern matches)

- [ ] **Step 1: Create the SQL.** Default `'wheel'` so existing rows backfill cleanly.

```sql
-- Phase 1 (animation library): per-giveaway animation id.
-- Default 'wheel' means existing rows + future rows where the operator
-- doesn't override fall back to the original spinning wheel — zero
-- regression for existing giveaways.

ALTER TABLE Giveaway ADD COLUMN AnimationId TEXT NOT NULL DEFAULT 'wheel';

UPDATE _meta SET SchemaVersion = 13 WHERE Id = 1;
```

- [ ] **Step 2: Verify the file is picked up.** `MigrationRunner` reads embedded resources from `OrderDeck.Core.Storage.Migrations.*`; it sorts lexically and skips versions ≤ current. Existing migrations 001-012 establish the pattern. Confirm the csproj already has:

```xml
<ItemGroup>
  <EmbeddedResource Include="Storage\Migrations\*.sql" />
</ItemGroup>
```

If not present (search the csproj), add it. Otherwise no edit needed.

- [ ] **Step 3: Commit.**

```bash
git add OrderDeck.Core/Storage/Migrations/013_giveaway_animation.sql
git commit -m "feat(db): migration 013 — Giveaway.AnimationId column (default 'wheel')"
```

---

### Task 3: `GiveawayRepository` reads + writes `AnimationId`

**Files:**
- Test: `OrderDeck.Tests/Storage/GiveawayRepositoryAnimationTests.cs` (new)
- Modify: `OrderDeck.Core/Storage/Repositories/GiveawayRepository.cs`

- [ ] **Step 1: Write the failing test.**

```csharp
using FluentAssertions;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class GiveawayRepositoryAnimationTests
{
    [Fact]
    public void Insert_then_GetById_round_trips_AnimationId()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", "Live", 100, null, new[] { "instagram" }, null));

        var repo = new GiveawayRepository(db);
        var g = new Giveaway(
            Id: "g1", SessionId: "s1", Keyword: "kazan",
            DurationSeconds: 60, WinnerCount: 1, PlatformFilter: null,
            PreventRewinning: true, RandomSeed: "seed",
            StartedAt: 100, EndedAt: null, CancelledAt: null,
            AnimationId: "slot-machine");

        repo.Insert(g);
        var loaded = repo.GetById("g1");

        loaded.Should().NotBeNull();
        loaded!.AnimationId.Should().Be("slot-machine");
    }

    [Fact]
    public void Migration_backfills_existing_rows_with_wheel_default()
    {
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        new SessionRepository(db).Insert(
            new StreamSession("s1", "Live", 100, null, new[] { "instagram" }, null));

        // Insert via raw SQL omitting AnimationId, simulating a row written
        // by code older than migration 013. The DEFAULT 'wheel' must apply.
        using (var conn = db.Open())
        {
            conn.Execute(
                @"INSERT INTO Giveaway (Id,SessionId,Keyword,DurationSeconds,WinnerCount,
                                        PreventRewinning,RandomSeed,StartedAt)
                  VALUES ('g-old','s1','kazan',60,1,1,'seed',100)");
        }

        var loaded = new GiveawayRepository(db).GetById("g-old");
        loaded!.AnimationId.Should().Be("wheel");
    }
}
```

- [ ] **Step 2: Run the test, expect compile or column-name failures.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~GiveawayRepositoryAnimationTests"`
Expected: build error in `Insert` until we update the SQL.

- [ ] **Step 3: Update `GiveawayRepository.Insert` SQL + parameter object.**

```csharp
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
            g.StartedAt, g.EndedAt, g.CancelledAt,
            g.AnimationId
        });
}
```

- [ ] **Step 4: Update the private `Row` class + `Map` method.** Add `AnimationId` property defaulted to `"wheel"` so partial-column SELECT statements (e.g., `SELECT *` runs against an old row right after migration where the value lands via DEFAULT) get something safe even if Dapper ever sees a null. Add it to the constructor in `Map`.

```csharp
// inside private sealed class Row { ... }
public string AnimationId { get; init; } = "wheel";

// inside Map(...)
private static Giveaway Map(Row r) => new(
    r.Id, r.SessionId, r.Keyword, r.DurationSeconds, r.WinnerCount,
    string.IsNullOrEmpty(r.PlatformFilter)
        ? null
        : JsonSerializer.Deserialize<List<string>>(r.PlatformFilter),
    r.PreventRewinning == 1,
    r.RandomSeed,
    r.StartedAt, r.EndedAt, r.CancelledAt,
    r.AnimationId);
```

- [ ] **Step 5: Run the test, expect PASS.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~GiveawayRepositoryAnimationTests"`
Expected: 2 tests pass.

- [ ] **Step 6: Commit.**

```bash
git add OrderDeck.Core/Storage/Repositories/GiveawayRepository.cs \
        OrderDeck.Tests/Storage/GiveawayRepositoryAnimationTests.cs
git commit -m "feat(db): persist Giveaway.AnimationId via repository"
```

---

### Task 4: Server-side `AnimationCatalog`

**Files:**
- Create: `OrderDeck.Core/Sales/AnimationCatalog.cs`
- Test: extend `OrderDeck.Tests/Sales/GiveawayServiceAnimationTests.cs` (created later in Task 6) — for now just write a small inline test in this file

- [ ] **Step 1: Create the catalog.** Phase 1 ships only the wheel; Phase 2 adds entries here as it adds plugins.

```csharp
using System.Collections.Generic;
using System.Linq;

namespace OrderDeck.Core.Sales;

/// <summary>
/// Server-side known-animations registry. The list is the source of truth
/// the validation in <see cref="GiveawayService.Start"/> uses to reject
/// unknown ids and fall back to the wheel.
///
/// Phase 1 ships only "wheel". Phase 2 adds slot-machine, bingo, card-draw.
/// Phase 3 adds the remaining six.
///
/// IMPORTANT: every id added here must have a matching folder under
/// OrderDeck.Overlay/wwwroot/animations/&lt;id&gt;/ AND a matching entry in
/// OrderDeck.Overlay/wwwroot/animations/manifest.json.
/// </summary>
public static class AnimationCatalog
{
    public const string DefaultId = "wheel";

    public static IReadOnlyList<string> KnownIds { get; } = new[] { "wheel" };

    public static bool IsKnown(string id) =>
        !string.IsNullOrWhiteSpace(id) && KnownIds.Contains(id);
}
```

- [ ] **Step 2: Write a unit test.**

```csharp
// OrderDeck.Tests/Sales/AnimationCatalogTests.cs
using FluentAssertions;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class AnimationCatalogTests
{
    [Fact]
    public void DefaultId_is_wheel()
    {
        AnimationCatalog.DefaultId.Should().Be("wheel");
    }

    [Fact]
    public void IsKnown_recognises_wheel()
    {
        AnimationCatalog.IsKnown("wheel").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("slot-machine")]   // not yet shipped in Phase 1
    [InlineData(null)]
    public void IsKnown_rejects_unknown_or_empty(string? id)
    {
        AnimationCatalog.IsKnown(id!).Should().BeFalse();
    }
}
```

- [ ] **Step 3: Run the tests, expect PASS.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~AnimationCatalogTests"`

- [ ] **Step 4: Commit.**

```bash
git add OrderDeck.Core/Sales/AnimationCatalog.cs \
        OrderDeck.Tests/Sales/AnimationCatalogTests.cs
git commit -m "feat(giveaway): add server-side AnimationCatalog (wheel-only for Phase 1)"
```

---

## Group B — Server service & events (Tasks 5-6)

### Task 5: `GiveawayService.Start` accepts `animationId`

**Files:**
- Test: `OrderDeck.Tests/Sales/GiveawayServiceAnimationTests.cs` (new)
- Modify: `OrderDeck.Core/Sales/GiveawayService.cs`
- Modify: `OrderDeck.Core/Sales/GiveawayEvents.cs` (add `AnimationId` to `GiveawayStartedEvent`)

- [ ] **Step 1: Write the failing tests.**

```csharp
using FluentAssertions;
using Moq;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class GiveawayServiceAnimationTests
{
    private static (GiveawayService svc, GiveawayRepository repo, string sessionId) Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        var sessions = new SessionRepository(db);
        var customers = new CustomerRepository(db);
        var labels = new LabelRepository(db);
        var giveaways = new GiveawayRepository(db);

        var customerSvc = new CustomerService(customers, sessions, labels, clock.Object);
        var drawer = new GiveawayDrawer();
        var svc = new GiveawayService(giveaways, customerSvc, drawer, clock.Object);

        var sessionId = "s-anim";
        sessions.Insert(new StreamSession(sessionId, "Live", 100, null, new[] { "instagram" }, null));
        return (svc, giveaways, sessionId);
    }

    [Fact]
    public void Start_with_explicit_animationId_persists_it()
    {
        var (svc, repo, sessionId) = Build();

        var g = svc.Start(sessionId, "kazan", 60, 1, null, true,
            animationId: "wheel");

        repo.GetById(g.Id)!.AnimationId.Should().Be("wheel");
    }

    [Fact]
    public void Start_with_null_animationId_falls_back_to_default()
    {
        var (svc, repo, sessionId) = Build();

        var g = svc.Start(sessionId, "kazan", 60, 1, null, true,
            animationId: null);

        g.AnimationId.Should().Be(AnimationCatalog.DefaultId);
        repo.GetById(g.Id)!.AnimationId.Should().Be(AnimationCatalog.DefaultId);
    }

    [Fact]
    public void Start_with_unknown_animationId_falls_back_to_default()
    {
        var (svc, repo, sessionId) = Build();

        var g = svc.Start(sessionId, "kazan", 60, 1, null, true,
            animationId: "does-not-exist");

        g.AnimationId.Should().Be(AnimationCatalog.DefaultId);
    }

    [Fact]
    public void Start_event_payload_contains_resolved_animationId()
    {
        var (svc, _, sessionId) = Build();
        GiveawayStartedEvent? captured = null;
        svc.Started += e => captured = e;

        svc.Start(sessionId, "kazan", 60, 1, null, true, animationId: "wheel");

        captured.Should().NotBeNull();
        captured!.AnimationId.Should().Be("wheel");
    }
}
```

- [ ] **Step 2: Run them, expect compile failure** (`Start` doesn't take `animationId`, event lacks `AnimationId`).

- [ ] **Step 3: Extend `GiveawayStartedEvent` with `AnimationId`.**

```csharp
// OrderDeck.Core/Sales/GiveawayEvents.cs
public sealed record GiveawayStartedEvent(
    string GiveawayId,
    string Keyword,
    int WinnerCount,
    int DurationSeconds,
    long StartedAt,
    string AnimationId);
```

- [ ] **Step 4: Update `GiveawayService.Start` signature + body.**

```csharp
public Giveaway Start(string sessionId, string keyword, int durationSeconds,
    int winnerCount, IReadOnlyList<string>? platformFilter, bool preventRewinning,
    string? animationId = null)
{
    // Resolve + validate. Unknown id falls back; empty/null falls back.
    var resolvedAnimationId = AnimationCatalog.IsKnown(animationId ?? "")
        ? animationId!
        : AnimationCatalog.DefaultId;

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
        CancelledAt: null,
        AnimationId: resolvedAnimationId);
    _giveaways.Insert(g);
    Active = g;

    _activePreviousWinners = preventRewinning
        ? new HashSet<string>(_giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id))
        : null;

    Started?.Invoke(new GiveawayStartedEvent(
        g.Id, g.Keyword, g.WinnerCount, g.DurationSeconds, g.StartedAt, g.AnimationId));
    return g;
}
```

- [ ] **Step 5: Run tests, expect PASS.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~GiveawayServiceAnimationTests"`
Expected: 4 tests pass.

- [ ] **Step 6: Run the full giveaway test suite to catch regressions.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~Giveaway"`
Expected: all giveaway tests pass (existing ones + new ones).

- [ ] **Step 7: Commit.**

```bash
git add OrderDeck.Core/Sales/GiveawayService.cs \
        OrderDeck.Core/Sales/GiveawayEvents.cs \
        OrderDeck.Tests/Sales/GiveawayServiceAnimationTests.cs
git commit -m "feat(giveaway): GiveawayService.Start accepts animationId with fallback"
```

---

### Task 6: Update existing call sites that broke

**Files:**
- Modify: any call site of `GiveawayService.Start` that's now stale (compile errors will reveal them — likely `MainShellViewModel`, `MainShellTestHarness`, possibly other ViewModels)

- [ ] **Step 1: Build to enumerate breakage.** `dotnet build OrderDeck.sln --nologo`. Compile errors list every call site.

- [ ] **Step 2: At each call site, pass `animationId: null` for now.** This is the "use default" code path. Tasks 12-13 plumb the real settings value through.

- [ ] **Step 3: Build green.** `dotnet build OrderDeck.sln --nologo`.

- [ ] **Step 4: Run the full test suite.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName!~MainShell&FullyQualifiedName!~GiveawayBannerViewModel"`
Expected: all pass (this is the same filter CI uses).

- [ ] **Step 5: Commit.**

```bash
git add -u
git commit -m "refactor(giveaway): pass animationId=null at existing call sites (default applies)"
```

---

## Group C — Settings shape & store (Tasks 7-8)

### Task 7: `AppSettings.GiveawayAnimation` block

**Files:**
- Modify: `OrderDeck.Core/Settings/AppSettings.cs`
- Test: `OrderDeck.Tests/Settings/SettingsStoreGiveawayAnimationTests.cs` (new)

- [ ] **Step 1: Write the failing round-trip test.**

```csharp
using System.IO;
using FluentAssertions;
using OrderDeck.Core.Settings;
using Xunit;

namespace OrderDeck.Tests.Settings;

public class SettingsStoreGiveawayAnimationTests
{
    [Fact]
    public void Save_then_Load_round_trips_GiveawayAnimation_block()
    {
        var path = Path.Combine(Path.GetTempPath(),
            $"orderdeck-anim-{System.Guid.NewGuid():N}.json");
        var store = new SettingsStore(path);
        try
        {
            var settings = new AppSettings();
            settings.GiveawayAnimation.DefaultId = "wheel";
            settings.GiveawayAnimation.Volume = 0.5;
            settings.GiveawayAnimation.MutedMode = true;

            store.Save(settings);
            var loaded = store.Load();

            loaded.GiveawayAnimation.DefaultId.Should().Be("wheel");
            loaded.GiveawayAnimation.Volume.Should().Be(0.5);
            loaded.GiveawayAnimation.MutedMode.Should().BeTrue();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Fresh_AppSettings_has_default_animation_block()
    {
        var s = new AppSettings();

        s.GiveawayAnimation.Should().NotBeNull();
        s.GiveawayAnimation.DefaultId.Should().Be("wheel");
        s.GiveawayAnimation.Volume.Should().BeApproximately(0.7, 0.001);
        s.GiveawayAnimation.MutedMode.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run the test, expect compile failure** (no `GiveawayAnimation` property).

- [ ] **Step 3: Add the new class + property to `AppSettings.cs`.**

```csharp
// add at the bottom of AppSettings.cs
public sealed class GiveawayAnimationSettings
{
    /// <summary>Plugin id from OrderDeck.Overlay/wwwroot/animations/manifest.json.</summary>
    public string DefaultId { get; set; } = "wheel";

    /// <summary>0.0 - 1.0 master volume. Plugins route audio via AudioController which respects this.</summary>
    public double Volume { get; set; } = 0.7;

    /// <summary>When true, all plugin audio is silenced regardless of Volume.</summary>
    public bool MutedMode { get; set; } = false;
}
```

And on `AppSettings`:

```csharp
public GiveawayAnimationSettings GiveawayAnimation { get; set; } = new();
```

- [ ] **Step 4: Run the test, expect PASS.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~SettingsStoreGiveawayAnimationTests"`

- [ ] **Step 5: Commit.**

```bash
git add OrderDeck.Core/Settings/AppSettings.cs \
        OrderDeck.Tests/Settings/SettingsStoreGiveawayAnimationTests.cs
git commit -m "feat(settings): add GiveawayAnimation block (DefaultId, Volume, MutedMode)"
```

---

### Task 8: Audio settings reach the started-event payload

**Files:**
- Modify: `OrderDeck.Core/Sales/GiveawayEvents.cs`
- Modify: `OrderDeck.Core/Sales/GiveawayService.cs`
- Modify: `OrderDeck.Tests/Sales/GiveawayServiceAnimationTests.cs` (extend)

The cleanest split: `GiveawayService` does NOT know about settings. `OverlayHost` fans the event out and adds audio fields at broadcast time. Therefore the **service event** stays domain-only (`GiveawayStartedEvent` already gained `AnimationId` in Task 5). The **wire payload** to JS gains audio fields, which we'll do in Task 11 inside `OverlayHost`.

- [ ] **Step 1: No code change here.** This task is a deliberate no-op confirming the layering decision: audio is overlay-host-broadcast-time concern, not domain. Update spec doc only if needed; the spec already reflects this — see `## Server-side changes` → "Overlay event payload" which lists `AudioVolume` + `AudioMuted` as additions to the wire-level `giveaway.started` (not the C# domain event).

- [ ] **Step 2: Add a one-line comment on `GiveawayStartedEvent` to lock the decision in code.**

```csharp
// OrderDeck.Core/Sales/GiveawayEvents.cs
/// <summary>
/// Domain event fired when a giveaway starts. Note: audio settings are NOT
/// here — they're added by OverlayHost at broadcast time, since they're a
/// presentation concern, not a domain event.
/// </summary>
public sealed record GiveawayStartedEvent(
    string GiveawayId,
    string Keyword,
    int WinnerCount,
    int DurationSeconds,
    long StartedAt,
    string AnimationId);
```

- [ ] **Step 3: Commit (docs only).**

```bash
git add OrderDeck.Core/Sales/GiveawayEvents.cs
git commit -m "docs(giveaway): clarify domain event excludes audio (overlay-broadcast concern)"
```

---

## Group D — Overlay broadcast (Tasks 9-11)

### Task 9: `OverlayHost` takes an audio-settings provider

**Files:**
- Modify: `OrderDeck.Overlay/OverlayHost.cs`
- Modify: any call site that constructs `OverlayHost` (likely `OrderDeck.App/AppHost.cs`)

- [ ] **Step 1: Add a delegate parameter to the ctor.**

```csharp
public OverlayHost(
    IChatBus bus,
    GiveawayService giveaway,
    int port = 4747,
    ILogger<OverlayHost>? log = null,
    Func<GiveawayAudioSnapshot>? audioProvider = null)
{
    _bus = bus;
    _giveaway = giveaway;
    _log = log ?? NullLogger<OverlayHost>.Instance;
    Port = port;
    _audioProvider = audioProvider ?? (() => new GiveawayAudioSnapshot(0.7, false));
}
```

`GiveawayAudioSnapshot` is a tiny record co-located with OverlayHost.cs:

```csharp
public sealed record GiveawayAudioSnapshot(double Volume, bool Muted);
```

(Co-located because it's an overlay-internal contract; it doesn't belong in `OrderDeck.Core`.)

- [ ] **Step 2: Build.**

`dotnet build OrderDeck.sln --nologo`. Should be green; the new param is optional.

- [ ] **Step 3: Wire `AppHost` to provide the snapshot from the live `SettingsStore`.** Locate where `OverlayHost` is constructed in `AppHost.cs`. Pass:

```csharp
audioProvider: () =>
{
    var s = settingsStore.Load().GiveawayAnimation;
    return new GiveawayAudioSnapshot(s.Volume, s.MutedMode);
}
```

(If the AppHost already keeps settings in memory, prefer that over a fresh `Load()`. The existing pattern will be obvious when you read AppHost.cs.)

- [ ] **Step 4: Build green.**

- [ ] **Step 5: Commit.**

```bash
git add OrderDeck.Overlay/OverlayHost.cs OrderDeck.App/AppHost.cs
git commit -m "feat(overlay): inject audio settings provider into OverlayHost"
```

---

### Task 10: Broadcast wire payload includes `AnimationId`/`AudioVolume`/`AudioMuted`

**Files:**
- Modify: `OrderDeck.Overlay/OverlayHost.cs`

The current code reuses `GiveawayStartedEvent` directly as the wire payload (`BroadcastGiveaway("giveaway.started", e)` where `e` is `GiveawayStartedEvent`). To add wire-only fields without polluting the domain event, introduce a `GiveawayStartedWirePayload` record local to `OverlayHost` and remap.

- [ ] **Step 1: Define the wire payload (private nested record).**

```csharp
// inside OverlayHost.cs, near the top of the class
private sealed record GiveawayStartedWirePayload(
    string GiveawayId,
    string Keyword,
    int WinnerCount,
    int DurationSeconds,
    long StartedAt,
    string AnimationId,
    double AudioVolume,
    bool AudioMuted);
```

- [ ] **Step 2: Update the `_onStarted` handler to remap.**

```csharp
_onStarted = e =>
{
    var audio = _audioProvider();
    var wire = new GiveawayStartedWirePayload(
        e.GiveawayId, e.Keyword, e.WinnerCount, e.DurationSeconds,
        e.StartedAt, e.AnimationId, audio.Volume, audio.Muted);
    BroadcastGiveaway("giveaway.started", wire);
};
```

- [ ] **Step 3: Update the late-join snapshot path (around line 159 today).** When a fresh overlay client connects and a giveaway is active, the host sends a synthetic `giveaway.started`. Remap that one too:

```csharp
var audio = _audioProvider();
var startedEvt = new OverlayEvent("giveaway.started",
    new GiveawayStartedWirePayload(
        active.Id, active.Keyword, active.WinnerCount, active.DurationSeconds,
        active.StartedAt, active.AnimationId,
        audio.Volume, audio.Muted));
await SendJson(ws, startedEvt, ct);
```

- [ ] **Step 4: Write an integration-style assertion.** Existing `OverlayHost` tests (if any — check `OrderDeck.Tests` and `OrderDeck.Overlay.Tests` if it exists) cover this path; if not, scope a small one inside `OrderDeck.Tests/Sales/GiveawayServiceAnimationTests.cs` that verifies the domain event, since the wire-level test would need a WebSocket client and is heavier than this Phase warrants. Lean on the manual smoke at the end of the plan for end-to-end coverage of the wire payload.

- [ ] **Step 5: Build + run.** `dotnet build OrderDeck.sln --nologo`.

- [ ] **Step 6: Commit.**

```bash
git add OrderDeck.Overlay/OverlayHost.cs
git commit -m "feat(overlay): wire payload carries AnimationId + audio settings"
```

---

### Task 11: Manual smoke (server-only) — confirm payload reaches a WebSocket client

- [ ] **Step 1: Run the WPF app locally, start a stream session, start a giveaway with default animation.**

- [ ] **Step 2: Open a WebSocket client (e.g. browser devtools `new WebSocket('ws://localhost:4747/ws/giveaway')`) and confirm the `giveaway.started` event payload contains:**
  - `AnimationId: "wheel"`
  - `AudioVolume: 0.7` (or whatever Settings shows)
  - `AudioMuted: false`

Do NOT proceed past Task 11 until this manual check is confirmed; everything downstream (Group E, F) depends on the wire shape being correct.

- [ ] **Step 3: Document the smoke result inline in the PR description** so reviewers can re-run. No code change.

---

## Group E — Overlay plugin host & wheel migration (Tasks 12-17)

These tasks rework `wwwroot/`. There is no JS test infrastructure in this repo; all verification is the manual smoke checklist at the end. Keep these as **one logical PR** (or one merge commit) to ease rollback if something is wrong.

### Task 12: Create `audio-controller.js`

**Files:**
- Create: `OrderDeck.Overlay/wwwroot/audio-controller.js`

- [ ] **Step 1: Write the module.**

```js
// audio-controller.js — shared by all animation plugins. Owns the
// HTMLAudioElement cache so a plugin can call audio.play('tick.mp3')
// without rebuilding elements per-call. Routes through master volume +
// muted toggle from Settings (passed in at construction time, can be
// updated later via setVolume/setMuted).

export class AudioController {
  /**
   * @param {string} basePath  Folder that holds the plugin's audio files,
   *                           e.g. './animations/wheel/audio/'.
   * @param {number} volume    Master volume 0-1.
   * @param {boolean} muted    Hard mute switch.
   */
  constructor(basePath, volume, muted) {
    this.basePath = basePath.endsWith('/') ? basePath : basePath + '/';
    this._volume = clamp01(volume);
    this._muted = !!muted;
    /** @type {Map<string, HTMLAudioElement>} */
    this._cache = new Map();
  }

  setVolume(v) {
    this._volume = clamp01(v);
    for (const a of this._cache.values()) a.volume = this._effective();
  }

  setMuted(b) {
    this._muted = !!b;
    for (const a of this._cache.values()) a.muted = this._muted;
  }

  /** Plays a clip. Filename is relative to `basePath`. */
  play(filename) {
    const a = this._get(filename);
    a.currentTime = 0;
    // Best-effort; browsers may block autoplay before user gesture.
    a.play().catch(() => {});
  }

  stop(filename) {
    const a = this._cache.get(filename);
    if (a) { a.pause(); a.currentTime = 0; }
  }

  /** Tear-down — call from plugin reset(). */
  disposeAll() {
    for (const a of this._cache.values()) { a.pause(); a.src = ''; }
    this._cache.clear();
  }

  _get(filename) {
    let a = this._cache.get(filename);
    if (!a) {
      a = new Audio(this.basePath + filename);
      a.preload = 'auto';
      a.volume = this._effective();
      a.muted = this._muted;
      this._cache.set(filename, a);
    }
    return a;
  }

  _effective() {
    return this._muted ? 0 : this._volume;
  }
}

function clamp01(v) { return Math.max(0, Math.min(1, +v || 0)); }
```

- [ ] **Step 2: Commit.**

```bash
git add OrderDeck.Overlay/wwwroot/audio-controller.js
git commit -m "feat(overlay): add AudioController utility for plugin sound packs"
```

---

### Task 13: Create `animations/manifest.json`

**Files:**
- Create: `OrderDeck.Overlay/wwwroot/animations/manifest.json`

- [ ] **Step 1: Write the manifest with one entry.**

```json
{
  "version": 1,
  "animations": [
    {
      "id": "wheel",
      "name": "Çark",
      "description": "Klasik dönen çark animasyonu",
      "category": "klasik",
      "thumbnail": "wheel/thumbnail.svg"
    }
  ]
}
```

- [ ] **Step 2: Commit.**

```bash
git add OrderDeck.Overlay/wwwroot/animations/manifest.json
git commit -m "feat(overlay): scaffold animations/manifest.json with wheel entry"
```

---

### Task 14: Wheel plugin — `animations/wheel/index.js`

**Files:**
- Create: `OrderDeck.Overlay/wwwroot/animations/wheel/index.js`

This is a **1:1 logical port** of the current `drawWheel` + `targetRotation` + `spinOnce` + `onWinnersDrawn` block (lines 90-273 of the existing `giveaway.js`), restructured behind the plugin interface defined in the spec.

- [ ] **Step 1: Write the module.** Verbatim translation; behaviour byte-equivalent (same easing, same durations, same slice palette). The host now owns container + audio injection; the plugin only renders.

```js
// animations/wheel/index.js — original spinning wheel, refactored as the
// first plugin under the new pluggable host. Behaviour identical to
// pre-refactor giveaway.js (see git history before commit XXX).

const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

export default {
  id: 'wheel',
  name: 'Çark',
  description: 'Klasik dönen çark animasyonu',
  category: 'klasik',
  thumbnail: './thumbnail.svg',

  // Internals (set by init)
  _container: null,
  _audio: null,
  _canvas: null,
  _name: null,
  _root: null,

  async init(container, audio) {
    this._container = container;
    this._audio = audio;
    container.innerHTML = `
      <div class="wheel-plugin hidden">
        <div class="wheel-arrow"></div>
        <canvas class="wheel-canvas" width="520" height="520"></canvas>
        <div class="wheel-name"></div>
      </div>`;
    this._root = container.querySelector('.wheel-plugin');
    this._canvas = container.querySelector('.wheel-canvas');
    this._name = container.querySelector('.wheel-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }

    this._show();
    this._draw(pool, 0);

    for (let i = 0; i < winners.length; i++) {
      const w = winners[i];
      let idx = pool.findIndex(p =>
        p.Username === w.Username && p.Platform === w.Platform);
      if (idx < 0) idx = 0;

      this._root.classList.remove('landed');
      const dur = i === 0 ? 4500 : 2800;
      await this._spin(pool, idx, dur);
      await new Promise(r => setTimeout(r, 900));
    }
  },

  reset() {
    if (this._root) this._root.classList.add('hidden');
    if (this._audio) this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  _show() { this._root.classList.remove('hidden'); },

  _draw(participants, rotation) {
    const ctx = this._canvas.getContext('2d');
    const W = this._canvas.width;
    const cx = W / 2, cy = W / 2;
    const outerR = W / 2 - 8;
    const innerR = 36;

    ctx.clearRect(0, 0, W, W);
    if (participants.length === 0) return;

    const slice = (Math.PI * 2) / participants.length;

    for (let i = 0; i < participants.length; i++) {
      const start = rotation + i * slice - Math.PI / 2;
      const end = start + slice;
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.arc(cx, cy, outerR, start, end);
      ctx.closePath();
      ctx.fillStyle = SLICE_COLORS[i % SLICE_COLORS.length];
      ctx.fill();
      ctx.strokeStyle = 'rgba(0,0,0,0.25)';
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    const labelArcMin = 14 * Math.PI / 180;
    if (slice >= labelArcMin) {
      ctx.font = 'bold 16px "Segoe UI", system-ui, sans-serif';
      ctx.fillStyle = '#fff';
      ctx.textAlign = 'right';
      ctx.textBaseline = 'middle';
      for (let i = 0; i < participants.length; i++) {
        const angle = rotation + i * slice + slice / 2 - Math.PI / 2;
        ctx.save();
        ctx.translate(cx, cy);
        ctx.rotate(angle);
        const text = (participants[i].DisplayName || participants[i].Username || '').slice(0, 14);
        ctx.shadowColor = 'rgba(0,0,0,0.65)';
        ctx.shadowBlur = 4;
        ctx.fillText(text, outerR - 14, 0);
        ctx.restore();
      }
    }

    ctx.beginPath();
    ctx.arc(cx, cy, innerR, 0, Math.PI * 2);
    ctx.fillStyle = '#0f1118';
    ctx.fill();
    ctx.strokeStyle = '#ffce46';
    ctx.lineWidth = 3;
    ctx.stroke();

    ctx.font = 'bold 22px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = '#ffce46';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.shadowColor = 'transparent';
    ctx.fillText('🎁', cx, cy);
  },

  _targetRotation(participantCount, winnerIndex, extraTurns) {
    const slice = (Math.PI * 2) / participantCount;
    const jitter = (Math.random() - 0.5) * slice * 0.7;
    const baseAngle = -winnerIndex * slice - slice / 2 + jitter;
    return baseAngle + extraTurns * Math.PI * 2;
  },

  _spin(pool, winnerIndex, durationMs) {
    return new Promise(resolve => {
      const target = this._targetRotation(
        pool.length, winnerIndex, 5 + Math.floor(Math.random() * 3));
      const start = performance.now();
      let lastHighlightIdx = -1;

      const frame = (now) => {
        const t = Math.min(1, (now - start) / durationMs);
        const eased = 1 - Math.pow(1 - t, 3);
        const rotation = target * eased;

        this._draw(pool, rotation);

        const slice = (Math.PI * 2) / pool.length;
        const normalised = ((-rotation - slice / 2) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2);
        const idx = Math.floor(normalised / slice) % pool.length;
        if (idx !== lastHighlightIdx) {
          const p = pool[idx];
          this._name.textContent = p.DisplayName || p.Username || '';
          lastHighlightIdx = idx;
        }

        if (t < 1) {
          requestAnimationFrame(frame);
        } else {
          this._name.textContent =
            pool[winnerIndex].DisplayName || pool[winnerIndex].Username || '';
          this._root.classList.add('landed');
          resolve();
        }
      };
      requestAnimationFrame(frame);
    });
  }
};
```

- [ ] **Step 2: Create `animations/wheel/style.css`** — copy the existing `#wheel`, `#wheel-canvas`, `#wheel-arrow`, `#wheel-name`, `.landed` blocks from `giveaway.css` and rename selectors from `#wheel-*` to `.wheel-plugin .wheel-*` (BEM-ish scoping). The host loads this CSS dynamically in Task 16.

- [ ] **Step 3: Create `animations/wheel/thumbnail.svg`** — minimal placeholder so manifest reference resolves. Real preview asset arrives in Phase 2 alongside the gallery polish:

```svg
<svg xmlns="http://www.w3.org/2000/svg" width="200" height="150" viewBox="0 0 200 150">
  <rect width="200" height="150" fill="#1a1a1a"/>
  <circle cx="100" cy="75" r="55" fill="none" stroke="#ffce46" stroke-width="3"/>
  <text x="100" y="80" text-anchor="middle" font-family="sans-serif"
        font-size="20" fill="#ffce46">Çark</text>
</svg>
```

- [ ] **Step 4: Create `animations/wheel/README.md`** — one-paragraph plugin description.

```markdown
# Wheel — `wheel`

Original spinning-wheel animation, kept as the system's default.
Behaviour: ease-out cubic spin (4500 ms first winner / 2800 ms each
subsequent), 16-colour slice palette, top-arrow reveal.

Audio: none in Phase 1 (sound design lands in Phase 2 alongside the
sibling animations).
```

- [ ] **Step 5: Commit.**

```bash
git add OrderDeck.Overlay/wwwroot/animations/wheel/
git commit -m "feat(overlay): wheel plugin (1:1 port of legacy spinning wheel)"
```

---

### Task 15: Refactor `giveaway.js` into the plugin host

**Files:**
- Modify: `OrderDeck.Overlay/wwwroot/giveaway.js` (significant rewrite)

- [ ] **Step 1: Replace the entire file.** The host now: connects WS, owns countdown/header/reveal/confetti, dynamically imports the chosen plugin, hands it a container + AudioController, and routes `winners.drawn` to the plugin's `runFor`.

```js
// giveaway.js — pluggable animation host. The host owns the WebSocket
// connection, header/countdown/reveal UI, and confetti. Each animation
// plugin (under animations/<id>/index.js) renders the spin/draw/reveal
// inside a host-provided container.
//
// On `giveaway.started` the host:
//   1. Reads AnimationId + AudioVolume + AudioMuted from the event.
//   2. Dynamically imports `./animations/<AnimationId>/index.js`.
//      Falls back to `wheel` if import fails.
//   3. Loads the plugin's optional `style.css` link tag.
//   4. Constructs an AudioController scoped to the plugin's audio folder.
//   5. Calls plugin.init(container, audio).
//
// On `giveaway.winners.drawn` the host calls plugin.runFor(winners, pool)
// then reveals the winner list and spawns confetti (host-owned).

import { AudioController } from './audio-controller.js';

(() => {
  const $root = document.getElementById('giveaway-root');
  const $keyword = document.getElementById('keyword');
  const $counter = document.getElementById('counter');
  const $countdown = document.getElementById('countdown');
  const $stage = document.getElementById('plugin-stage');
  const $reveal = document.getElementById('reveal');
  const $winnersList = document.getElementById('winners-list');

  let state = {
    giveawayId: null,
    keyword: '',
    durationSeconds: 0,
    startedAt: 0,
    countdownTimer: null,
    plugin: null,
    pluginStyleEl: null
  };

  function show(el) { el.classList.remove('hidden'); }
  function hide(el) { el.classList.add('hidden'); }

  function reset() {
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
    if (state.plugin) { try { state.plugin.reset(); } catch {} }
    if (state.pluginStyleEl) { state.pluginStyleEl.remove(); }
    state = {
      giveawayId: null, keyword: '', durationSeconds: 0, startedAt: 0,
      countdownTimer: null, plugin: null, pluginStyleEl: null
    };
    $keyword.textContent = '';
    $counter.textContent = '0 katılımcı';
    $countdown.textContent = '';
    $stage.innerHTML = '';
    $winnersList.innerHTML = '';
    hide($reveal);
    hide($root);
  }

  function startCountdown() {
    if (state.durationSeconds <= 0) { $countdown.textContent = ''; return; }
    const tick = () => {
      const elapsed = Math.floor(Date.now() / 1000) - state.startedAt;
      const remaining = Math.max(0, state.durationSeconds - elapsed);
      const m = Math.floor(remaining / 60);
      const s = remaining % 60;
      $countdown.textContent = `${m.toString().padStart(2,'0')}:${s.toString().padStart(2,'0')}`;
      if (remaining <= 0 && state.countdownTimer) {
        clearInterval(state.countdownTimer); state.countdownTimer = null;
      }
    };
    tick();
    state.countdownTimer = setInterval(tick, 1000);
  }

  async function loadPlugin(animationId, audioVolume, audioMuted) {
    const id = animationId || 'wheel';
    let module;
    try {
      module = await import(`./animations/${id}/index.js`);
    } catch (err) {
      console.warn(`[giveaway-host] failed to load plugin '${id}', falling back to wheel`, err);
      module = await import('./animations/wheel/index.js');
    }
    const plugin = module.default;

    // Inject the plugin's optional style.css.
    const styleUrl = `./animations/${plugin.id}/style.css`;
    state.pluginStyleEl = document.createElement('link');
    state.pluginStyleEl.rel = 'stylesheet';
    state.pluginStyleEl.href = styleUrl;
    document.head.appendChild(state.pluginStyleEl);

    const audio = new AudioController(
      `./animations/${plugin.id}/audio/`,
      typeof audioVolume === 'number' ? audioVolume : 0.7,
      !!audioMuted);

    await plugin.init($stage, audio);
    state.plugin = plugin;
    return plugin;
  }

  async function onStarted(e) {
    reset();
    state.giveawayId = e.GiveawayId;
    state.keyword = e.Keyword;
    state.durationSeconds = e.DurationSeconds;
    state.startedAt = e.StartedAt;
    $keyword.textContent = `"${e.Keyword}"`;
    $counter.textContent = '0 katılımcı';
    show($root);
    startCountdown();

    await loadPlugin(e.AnimationId, e.AudioVolume, e.AudioMuted);
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

  async function onWinnersDrawn(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
    if (!state.plugin) {
      console.warn('[giveaway-host] winners drawn before plugin loaded');
      return;
    }

    const pool = e.AnimationPool || [];
    const winners = e.Winners || [];

    await state.plugin.runFor(winners, pool);
    revealWinners(winners);
  }

  function revealWinners(winners) {
    $stage.innerHTML = '';
    $winnersList.innerHTML = '';
    const PLATFORM_EMOJI = { instagram: '📷', tiktok: '🎵', facebook: '👥', youtube: '▶️' };
    for (const w of winners) {
      const li = document.createElement('li');
      li.className = 'winner';
      const emoji = PLATFORM_EMOJI[w.Platform] || '💬';
      li.innerHTML = `
        <span class="platform-${w.Platform}">${emoji}</span>
        <span class="name">${escapeHtml(w.DisplayName || w.Username)}</span>`;
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

- [ ] **Step 2: Commit.**

```bash
git add OrderDeck.Overlay/wwwroot/giveaway.js
git commit -m "refactor(overlay): giveaway.js becomes a thin plugin host"
```

---

### Task 16: Update `giveaway.html` for plugin stage

**Files:**
- Modify: `OrderDeck.Overlay/wwwroot/giveaway.html`
- Modify: `OrderDeck.Overlay/wwwroot/giveaway.css` (strip `#wheel*` rules — moved to `animations/wheel/style.css`)

- [ ] **Step 1: Replace the wheel-specific DOM with a generic stage.**

```html
<!doctype html>
<html lang="tr">
<head>
  <meta charset="utf-8">
  <title>OrderDeck Çekiliş</title>
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

    <!-- Plugin stage. Each animation renders into this div via its
         init(container, audio) call from giveaway.js (the host). -->
    <div id="plugin-stage"></div>
  </div>

  <script type="module" src="/giveaway.js"></script>
</body>
</html>
```

Note `type="module"` on the script tag — required for `import` to work.

- [ ] **Step 2: Trim `giveaway.css`.** Remove every selector targeting `#wheel`, `#wheel-canvas`, `#wheel-arrow`, `#wheel-name`, `.landed`. They moved to `animations/wheel/style.css` in Task 14. Keep all header / countdown / reveal / confetti rules.

- [ ] **Step 3: Commit.**

```bash
git add OrderDeck.Overlay/wwwroot/giveaway.html OrderDeck.Overlay/wwwroot/giveaway.css
git commit -m "refactor(overlay): giveaway.html exposes a generic plugin stage"
```

---

### Task 17: Manual smoke — overlay end-to-end

- [ ] **Step 1: Run the WPF app, load OBS Browser Source pointing at `http://localhost:4747/overlay/giveaway`.**

- [ ] **Step 2: Run a giveaway with 5 participants, 1 winner.**

- [ ] **Step 3: Verify visually that the wheel spins identically to before** — same speed, same colours, same landing snap, same winner reveal + confetti.

- [ ] **Step 4: Verify devtools (F12) network tab shows:**
  - `giveaway.js` loaded once
  - `audio-controller.js` loaded once when started
  - `animations/wheel/index.js` loaded once when started
  - `animations/wheel/style.css` loaded once when started
  - No 404s on `animations/<unknown>/...` paths

- [ ] **Step 5: Verify the WebSocket frame for `giveaway.started` includes `AnimationId: "wheel"`, `AudioVolume`, `AudioMuted`.**

If any step fails, fix forward — do not proceed to Group F until smoke is green.

---

## Group F — WPF picker & settings tab (Tasks 18-21)

### Task 18: `AnimationPickerViewModel`

**Files:**
- Create: `OrderDeck.App/ViewModels/AnimationPickerViewModel.cs`
- Create: `OrderDeck.App/Services/AnimationCatalogClient.cs`
- Create: `OrderDeck.Tests/ViewModels/AnimationPickerViewModelTests.cs`

The picker reads `manifest.json` from the in-process `OverlayHost`. We expose a small client that wraps the HTTP fetch.

- [ ] **Step 1: Write the failing test.**

```csharp
using System.Collections.Generic;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class AnimationPickerViewModelTests
{
    private static IReadOnlyList<AnimationCatalogEntry> Two() => new[]
    {
        new AnimationCatalogEntry("wheel", "Çark", "Klasik", "klasik", "wheel/thumbnail.svg"),
        new AnimationCatalogEntry("slot-machine", "Slot", "Kazino", "klasik", "slot-machine/thumbnail.svg"),
    };

    [Fact]
    public void Loaded_animations_match_seeded_catalog()
    {
        var vm = new AnimationPickerViewModel();
        vm.LoadAnimations(Two());
        vm.Animations.Should().HaveCount(2);
        vm.Animations[0].Id.Should().Be("wheel");
    }

    [Fact]
    public void SelectedId_change_fires_PropertyChanged()
    {
        var vm = new AnimationPickerViewModel();
        vm.LoadAnimations(Two());
        vm.SelectedId = "wheel";

        var changes = 0;
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedId)) changes++; };

        vm.SelectedId = "slot-machine";

        changes.Should().Be(1);
        vm.SelectedId.Should().Be("slot-machine");
    }

    [Fact]
    public void Setting_unknown_id_does_not_change_selection()
    {
        var vm = new AnimationPickerViewModel();
        vm.LoadAnimations(Two());
        vm.SelectedId = "wheel";

        vm.SelectedId = "phantom";

        vm.SelectedId.Should().Be("wheel");
    }
}
```

- [ ] **Step 2: Run, expect compile failure.**

- [ ] **Step 3: Create `AnimationCatalogEntry` + `AnimationCatalogClient`.**

```csharp
// OrderDeck.App/Services/AnimationCatalogClient.cs
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

namespace OrderDeck.App.Services;

public sealed record AnimationCatalogEntry(
    string Id, string Name, string Description, string Category, string Thumbnail);

public sealed class AnimationCatalogClient
{
    private readonly HttpClient _http;
    private readonly string _manifestUrl;

    public AnimationCatalogClient(HttpClient http, int overlayPort)
    {
        _http = http;
        _manifestUrl = $"http://localhost:{overlayPort}/animations/manifest.json";
    }

    public async Task<IReadOnlyList<AnimationCatalogEntry>> LoadAsync()
    {
        var json = await _http.GetStringAsync(_manifestUrl);
        var doc = JsonDocument.Parse(json);
        var list = new List<AnimationCatalogEntry>();
        foreach (var el in doc.RootElement.GetProperty("animations").EnumerateArray())
        {
            list.Add(new AnimationCatalogEntry(
                el.GetProperty("id").GetString() ?? "",
                el.GetProperty("name").GetString() ?? "",
                el.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "",
                el.TryGetProperty("category", out var c) ? c.GetString() ?? "" : "",
                el.TryGetProperty("thumbnail", out var t) ? t.GetString() ?? "" : ""));
        }
        return list;
    }
}
```

- [ ] **Step 4: Create `AnimationPickerViewModel`.**

```csharp
// OrderDeck.App/ViewModels/AnimationPickerViewModel.cs
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services;

namespace OrderDeck.App.ViewModels;

public sealed partial class AnimationPickerViewModel : ViewModelBase
{
    public ObservableCollection<AnimationCatalogEntry> Animations { get; } = new();

    private string _selectedId = "wheel";
    public string SelectedId
    {
        get => _selectedId;
        set
        {
            // Reject ids not in the catalog so the operator can never persist
            // an invalid selection. Empty list = bootstrap (accept any).
            if (Animations.Count > 0 && !Animations.Any(a => a.Id == value)) return;
            if (_selectedId == value) return;
            _selectedId = value;
            OnPropertyChanged();
        }
    }

    public void LoadAnimations(IReadOnlyList<AnimationCatalogEntry> entries)
    {
        Animations.Clear();
        foreach (var e in entries) Animations.Add(e);
        // Re-validate current selection against the new list.
        if (Animations.Count > 0 && !Animations.Any(a => a.Id == _selectedId))
        {
            _selectedId = Animations[0].Id;
            OnPropertyChanged(nameof(SelectedId));
        }
    }
}
```

- [ ] **Step 5: Run tests, expect PASS.**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~AnimationPickerViewModelTests"`

- [ ] **Step 6: Commit.**

```bash
git add OrderDeck.App/Services/AnimationCatalogClient.cs \
        OrderDeck.App/ViewModels/AnimationPickerViewModel.cs \
        OrderDeck.Tests/ViewModels/AnimationPickerViewModelTests.cs
git commit -m "feat(app): AnimationPickerViewModel + manifest catalog client"
```

---

### Task 19: `AnimationPickerControl` XAML

**Files:**
- Create: `OrderDeck.App/Controls/AnimationPickerControl.xaml` (+ `.cs`)

- [ ] **Step 1: Write the user control.**

```xml
<UserControl x:Class="OrderDeck.App.Controls.AnimationPickerControl"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
             Background="Transparent" Foreground="White">
  <ItemsControl ItemsSource="{Binding Animations}">
    <ItemsControl.ItemsPanel>
      <ItemsPanelTemplate>
        <UniformGrid Columns="3"/>
      </ItemsPanelTemplate>
    </ItemsControl.ItemsPanel>
    <ItemsControl.ItemTemplate>
      <DataTemplate>
        <Border Margin="6" Padding="6" CornerRadius="6" BorderThickness="2"
                Background="#FF1F2937">
          <Border.Style>
            <Style TargetType="Border">
              <Setter Property="BorderBrush" Value="Transparent"/>
              <Style.Triggers>
                <DataTrigger Binding="{Binding Id}"
                             Value="{Binding DataContext.SelectedId,
                                            RelativeSource={RelativeSource AncestorType=UserControl}}">
                  <Setter Property="BorderBrush" Value="#FFFFCE46"/>
                </DataTrigger>
              </Style.Triggers>
            </Style>
          </Border.Style>
          <StackPanel>
            <Image Source="{Binding ThumbnailUri}"
                   Width="180" Height="120" Stretch="UniformToFill"/>
            <TextBlock Text="{Binding Name}" FontWeight="Bold"
                       Margin="0,6,0,2" Foreground="White"/>
            <TextBlock Text="{Binding Description}" FontSize="11"
                       Foreground="#FFB0B0B0" TextWrapping="Wrap"/>
            <Button Content="Seç" Margin="0,6,0,0"
                    CommandParameter="{Binding Id}"
                    Click="SelectButton_Click"/>
          </StackPanel>
        </Border>
      </DataTemplate>
    </ItemsControl.ItemTemplate>
  </ItemsControl>
</UserControl>
```

- [ ] **Step 2: Code-behind.**

```csharp
// OrderDeck.App/Controls/AnimationPickerControl.xaml.cs
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Controls;

public partial class AnimationPickerControl : UserControl
{
    public AnimationPickerControl()
    {
        InitializeComponent();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnimationPickerViewModel vm
            && sender is Button { CommandParameter: string id })
        {
            vm.SelectedId = id;
        }
    }
}
```

- [ ] **Step 3: Add the helper computed `ThumbnailUri` property to `AnimationCatalogEntry`.** Image source needs an absolute URL pointing at the OverlayHost. Update the record (or add an extension):

```csharp
public sealed record AnimationCatalogEntry(
    string Id, string Name, string Description, string Category, string Thumbnail)
{
    public string? OverlayBase { get; init; }
    public string ThumbnailUri =>
        $"{OverlayBase ?? ""}/animations/{Thumbnail}".TrimStart('/');
}
```

Update `AnimationCatalogClient.LoadAsync` to set `OverlayBase = $"http://localhost:{overlayPort}"` on each entry.

- [ ] **Step 4: Build to verify XAML compiles.** `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo`.

- [ ] **Step 5: Commit.**

```bash
git add OrderDeck.App/Controls/AnimationPickerControl.xaml \
        OrderDeck.App/Controls/AnimationPickerControl.xaml.cs \
        OrderDeck.App/Services/AnimationCatalogClient.cs
git commit -m "feat(app): AnimationPickerControl reusable gallery"
```

---

### Task 20: Settings tab "Çekiliş Animasyonu"

**Files:**
- Modify: `OrderDeck.App/Views/SettingsDialog.xaml`
- Modify: `OrderDeck.App/ViewModels/SettingsViewModel.cs`
- Modify: `OrderDeck.App/AppHost.cs` (wire `AnimationCatalogClient` into the SettingsViewModel construction)

- [ ] **Step 1: Add bindings to `SettingsViewModel`.**

```csharp
// inside SettingsViewModel, alongside existing properties
[ObservableProperty] private AnimationPickerViewModel _animationPicker = new();

[ObservableProperty] private double _animationVolume;
[ObservableProperty] private bool _animationMuted;

// In Load (or constructor where the existing settings are read):
_animationVolume = settings.GiveawayAnimation.Volume;
_animationMuted = settings.GiveawayAnimation.MutedMode;
AnimationPicker.SelectedId = settings.GiveawayAnimation.DefaultId;

// In Save (or wherever the existing Save command persists):
settings.GiveawayAnimation.DefaultId = AnimationPicker.SelectedId;
settings.GiveawayAnimation.Volume = AnimationVolume;
settings.GiveawayAnimation.MutedMode = AnimationMuted;
```

Constructor takes an `AnimationCatalogClient`:

```csharp
public SettingsViewModel(SettingsStore store, AnimationCatalogClient catalogClient, /* existing deps */)
{
    _store = store;
    _ = LoadCatalogAsync(catalogClient);
    // ...
}

private async Task LoadCatalogAsync(AnimationCatalogClient client)
{
    try
    {
        var entries = await client.LoadAsync();
        AnimationPicker.LoadAnimations(entries);
        // Re-apply persisted selection now that catalog is in.
        AnimationPicker.SelectedId = _store.Load().GiveawayAnimation.DefaultId;
    }
    catch (Exception ex)
    {
        _log?.LogWarning(ex, "Failed to load animation catalog");
    }
}
```

(If the existing constructor isn't async-friendly, fire-and-forget with the explicit `_ =` discard as shown.)

- [ ] **Step 2: Add the new tab in `SettingsDialog.xaml`** at the end of the existing `<TabControl>`.

```xml
<TabItem Header="Çekiliş Animasyonu">
  <Grid Margin="12">
    <Grid.RowDefinitions>
      <RowDefinition Height="*"/>
      <RowDefinition Height="Auto"/>
      <RowDefinition Height="Auto"/>
    </Grid.RowDefinitions>

    <controls:AnimationPickerControl Grid.Row="0"
                                     DataContext="{Binding AnimationPicker}"/>

    <StackPanel Grid.Row="1" Orientation="Horizontal" Margin="0,12,0,4">
      <TextBlock Text="Ses seviyesi:" Margin="0,4,8,0"/>
      <Slider Minimum="0" Maximum="1"
              Value="{Binding AnimationVolume, UpdateSourceTrigger=PropertyChanged}"
              Width="200" VerticalAlignment="Center"/>
      <TextBlock Text="{Binding AnimationVolume, StringFormat={}{0:P0}}"
                 Margin="8,4,0,0" Width="40"/>
    </StackPanel>

    <CheckBox Grid.Row="2" Margin="0,8,0,0"
              Content="Sessiz mod (tüm animasyonlar sessiz)"
              IsChecked="{Binding AnimationMuted}"/>
  </Grid>
</TabItem>
```

The `controls:` namespace alias is already declared at the top of the file: `xmlns:controls="clr-namespace:OrderDeck.App.Controls"`.

- [ ] **Step 3: Wire `AnimationCatalogClient` in `AppHost.cs`** when constructing `SettingsViewModel`. Inject a shared `HttpClient` and the overlay port from `AppSettings.OverlayPort`.

- [ ] **Step 4: Build.** `dotnet build OrderDeck.App/OrderDeck.App.csproj --nologo`.

- [ ] **Step 5: Manual smoke** — open Settings, see the new tab, see the Çark card highlighted, slider + checkbox respond. Save Settings, reopen — selection persists.

- [ ] **Step 6: Commit.**

```bash
git add OrderDeck.App/Views/SettingsDialog.xaml \
        OrderDeck.App/ViewModels/SettingsViewModel.cs \
        OrderDeck.App/AppHost.cs
git commit -m "feat(app): Settings tab — Çekiliş Animasyonu picker + audio controls"
```

---

### Task 21: `MainShellViewModel` passes settings default into `Start`

**Files:**
- Modify: `OrderDeck.App/ViewModels/MainShellViewModel.cs`
- Modify: `OrderDeck.Tests/App/MainShellTestHarness.cs` (if the harness mocks anything that broke)

- [ ] **Step 1: Locate `MainShellViewModel.StartGiveaway` (around line 540 today).** It currently calls `_giveaways.Start(...)` without an `animationId`. After Task 6 the call passes `animationId: null`. Now resolve the actual default:

```csharp
var settings = _settingsStore.Load();
var animationId = settings.GiveawayAnimation.DefaultId;
var g = _giveaways.Start(
    sessionId: session.Id,
    keyword: vm.Keyword,
    durationSeconds: vm.DurationSeconds,
    winnerCount: vm.WinnerCount,
    platformFilter: vm.SelectedPlatform.Filter,
    preventRewinning: vm.PreventRewinning,
    animationId: animationId);
```

(Use the existing `_settingsStore` field if present; if not, inject it through the constructor — match the style used by other settings-touching code in MainShellViewModel.)

- [ ] **Step 2: Build green, run filtered tests** (the same filter CI uses):

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName!~MainShell&FullyQualifiedName!~GiveawayBannerViewModel"
```

- [ ] **Step 3: Commit.**

```bash
git add OrderDeck.App/ViewModels/MainShellViewModel.cs OrderDeck.Tests/App/MainShellTestHarness.cs
git commit -m "feat(app): start giveaways with the operator's default animation"
```

---

## Group G — End-to-end smoke (Task 22)

### Task 22: Manual end-to-end smoke

This is the final verification before the PR is opened against `master`. Add a checklist file so future Phase-N migrations have a template.

**Files:**
- Create: `docs/manual-smoke/giveaway-animation-phase-1.md`

- [ ] **Step 1: Write the checklist.**

```markdown
# Phase 1 — Giveaway Animation Plugin Foundation Smoke

Verify the plugin host doesn't regress the existing wheel and that the
new selection scaffolding works end-to-end.

## App boot
- [ ] OrderDeck.App launches; no exceptions in the output.
- [ ] Settings dialog opens; "Çekiliş Animasyonu" tab is visible.
- [ ] Tab loads the Çark card with thumbnail.
- [ ] Volume slider defaults to 70%, "Sessiz mod" unchecked.

## Persistence
- [ ] Move slider to 30%, check "Sessiz mod", click Save, close dialog.
- [ ] Reopen Settings → values persisted exactly.
- [ ] Restore defaults (70% / unchecked) before the live test.

## Wheel parity
- [ ] Start a stream session.
- [ ] Open OBS Browser Source at http://localhost:4747/overlay/giveaway.
- [ ] Run a giveaway with the keyword "kazan", 60-second duration,
      1 winner, instagram filter.
- [ ] Type "kazan" from 5 different chat usernames over 30 seconds.
- [ ] At draw time the wheel spins exactly as before:
      4500 ms first winner, ease-out cubic, slice palette identical,
      winner snap, confetti reveal.

## Multi-winner
- [ ] Run another giveaway with 3 winners, 8 participants.
- [ ] Each winner gets its own 2800 ms spin after the first.

## Cancel
- [ ] Start a giveaway, cancel from the operator UI.
- [ ] Overlay fades out; on next start, plugin loads fresh (no stale DOM).

## Wire payload
- [ ] DevTools → Network → WS → `giveaway.started` frame contains
      `AnimationId: "wheel"`, `AudioVolume: 0.7`, `AudioMuted: false`.

## Backward compat
- [ ] Existing Giveaway rows in the DB show `AnimationId: 'wheel'`
      after migration 013 runs (sqlite3 query).
- [ ] Re-run `MigrationRunner` — idempotent (no errors, no duplicate
      column attempts).

If any item fails, do NOT merge to master; fix forward in the PR branch.
```

- [ ] **Step 2: Run the checklist; fix anything red.**

- [ ] **Step 3: Commit + open PR.**

```bash
git add docs/manual-smoke/giveaway-animation-phase-1.md
git commit -m "docs(smoke): Phase 1 giveaway animation foundation checklist"
git push -u origin feat/giveaway-animation-phase-1
```

Open a PR against `master`. CI runs build-test (filtered) — should be green. After merge, `license-server-deploy` is unaffected (no LicenseServer changes); the WPF + Overlay changes ship to operators on next App release.

---

## Self-review summary

- **Spec coverage:**
  - Plugin architecture (Tasks 12-16) ✓
  - Selection UX scaffolding (Tasks 18-20) ✓ — full per-giveaway override is Phase 2
  - Server data flow (Tasks 1-10) ✓
  - Backward compat (Task 2 default + Task 14 1:1 port) ✓
  - Smoke (Task 22) ✓
- **Out of Phase 1, into Phase 2:** WebView2 preview window, real WebP thumbnails, override dropdown in `NewGiveawayDialog`, slot-machine + bingo + card-draw plugins, sound packs.
- **Out of Phase 1, into Phase 3:** the remaining six animations, accessibility audit pass, manifest auto-generation script.

## Total task count

22 tasks across 7 groups. Targeted runtime estimate: ~30-40 hours focused work, matching the spec's Phase 1 estimate.
