# Faz 3d — Polish + Türkçe UX Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Faz 2b code review'dan dönen 4 ertelenmiş madde + 4 Türkçe yerelleştirme cilasını tek bundle olarak ship et.

**Architecture:** Mimari değişiklik yok. Yeni proje yok. Tek yeni dosya `LiveDeck.App/Formatting/TrFormats.cs` (ortak `tr-TR` `CultureInfo` + iki helper). Kalan değişiklikler mevcut dosyalara lokalize yamalardır.

**Tech Stack:** .NET 10 WPF (existing), Dapper (existing), ClosedXML (existing). Yeni paket yok.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-3d state:** P2b HEAD `332e4c5` + spec commit `c7ccbde`. 74/74 tests passing.

**Spec reference:** `docs/superpowers/specs/2026-04-28-phase-3d-polish-design.md`

---

## Task Index

**Domain layer (1-3):** TR keyword + kazanan cache + repo participant count
**Overlay (4):** Late-join TotalCount snapshot + platform emoji map
**App (5-9):** Window guard + global tr-TR culture + TrFormats helper + StreamReport Excel format + StreamHistory tarih
**Acceptance (10):** Manuel smoke

---

### Task 1: TR-uyumlu keyword eşleşmesi

**Files:**
- Modify: `LiveDeck.Core/Sales/GiveawayService.cs`
- Test: `LiveDeck.Tests/Sales/GiveawayServiceTurkishKeywordTests.cs` (new)

**Context:** `GiveawayService.AddParticipantFromChat` şu an `message.Text.Contains(g.Keyword, StringComparison.OrdinalIgnoreCase)` kullanıyor. Bu Türkçe noktalı/noktasız 'I'/'i' farklarını yanlış eşliyor. Çözüm: `CultureInfo("tr-TR").CompareInfo.IndexOf` ile değiştir.

- [ ] **Step 1: Write failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Sales/GiveawayServiceTurkishKeywordTests.cs`:

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

public class GiveawayServiceTurkishKeywordTests
{
    private static (GiveawayService Svc, GiveawayRepository Repo, string SessionId, InMemorySqlite Db) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var customerSvc  = new CustomerService(customerRepo, clock.Object);
        var giveawayRepo = new GiveawayRepository(db);
        var drawer       = new GiveawayDrawer();

        return (new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object),
                giveawayRepo, "s1", db);
    }

    private static ChatMessage Msg(string username, string text) =>
        new(Guid.NewGuid().ToString("N"), "instagram", null, username, username, null, text, 1000,
            Array.Empty<string>());

    [Theory]
    [InlineData("istanbul", "İSTANBUL gel",     true)]   // dotless I in message → dotted i in keyword
    [InlineData("İstanbul", "istanbul gel",     true)]   // dotted i in keyword → dotless I in message
    [InlineData("ışık",     "IŞIK kapı",        true)]   // mixed Turkish dotless i
    [InlineData("kazan",    "kaybetti",         false)]  // unrelated word → no match
    [InlineData("🌹",       "alıyorum 🌹",       true)]   // emoji keyword still works
    public void AddParticipantFromChat_matches_keyword_with_turkish_culture(
        string keyword, string text, bool shouldAdd)
    {
        var (svc, repo, sid, db) = Fx();
        using var _ = db;
        var g = svc.Start(sid, keyword, durationSeconds: 60, winnerCount: 1,
                          platformFilter: null, preventRewinning: false);

        svc.AddParticipantFromChat(g.Id, Msg("@user", text));

        repo.GetParticipants(g.Id).Count.Should().Be(shouldAdd ? 1 : 0);
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayServiceTurkishKeywordTests" 2>&1 | tail -10
```
Expected: 2/5 fail. The mixed-i cases (`İstanbul`/`istanbul`, `ışık`/`IŞIK`) miss with OrdinalIgnoreCase.

- [ ] **Step 3: Switch to tr-TR CompareInfo**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/GiveawayService.cs`. Add a static field at the top of the class (just below `SqliteUniqueConstraintCode`):

```csharp
/// <summary>tr-TR CompareInfo for keyword matching (handles dotted/dotless i correctly).</summary>
private static readonly System.Globalization.CompareInfo TrCompare =
    new System.Globalization.CultureInfo("tr-TR").CompareInfo;
```

Replace the keyword-match block in `AddParticipantFromChat`:

```csharp
// (a) Keyword match — case-insensitive substring
if (!message.Text.Contains(g.Keyword, StringComparison.OrdinalIgnoreCase))
    return;
```

with:

```csharp
// (a) Keyword match — Turkish-aware case-insensitive substring
if (TrCompare.IndexOf(message.Text, g.Keyword, System.Globalization.CompareOptions.IgnoreCase) < 0)
    return;
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayServiceTurkishKeywordTests" 2>&1 | tail -3
```
Expected: 5/5 pass.

- [ ] **Step 5: Run full suite (regression)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: 79/79 (74 baseline + 5 new). The existing `AddParticipantFromChat_is_case_insensitive_for_alphanumeric_keywords` keeps passing — tr-TR culture also matches ASCII case-insensitively.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/GiveawayService.cs LiveDeck.Tests/Sales/GiveawayServiceTurkishKeywordTests.cs
git commit -m "fix(core): use tr-TR CompareInfo for giveaway keyword matching"
```

---

### Task 2: GiveawayRepository.GetParticipantCount

**Files:**
- Modify: `LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs`
- Test: `LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs` (extend)

**Context:** Task 4 (overlay late-join) needs a count without materializing the full participant list. Add a thin `GetParticipantCount` method.

- [ ] **Step 1: Write failing test**

Append to `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs`, just before the closing `}` of the class:

```csharp
[Fact]
public void GetParticipantCount_returns_count_for_giveaway()
{
    var (db, repo, _, cid) = Fx();
    using var _2 = db;
    repo.Insert(NewGiveaway());
    repo.AddParticipant(new GiveawayParticipant("p1", "g1", cid, "instagram", "@a", 300, false));
    repo.AddParticipant(new GiveawayParticipant("p2", "g1", cid, "instagram", "@b", 301, false));

    repo.GetParticipantCount("g1").Should().Be(2);
    repo.GetParticipantCount("g-nonexistent").Should().Be(0);
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryTests" 2>&1 | tail -5
```
Expected: compile error — `GetParticipantCount` not found.

- [ ] **Step 3: Add GetParticipantCount method**

In `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs`, locate the `GetParticipants` method and add immediately above it:

```csharp
public int GetParticipantCount(string giveawayId)
{
    using var conn = _factory.Open();
    return conn.ExecuteScalar<int>(
        "SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId=@giveawayId",
        new { giveawayId });
}
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayRepositoryTests" 2>&1 | tail -3
```
Expected: 12/12 pass (11 prev + 1 new).

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Storage/Repositories/GiveawayRepository.cs LiveDeck.Tests/Storage/GiveawayRepositoryTests.cs
git commit -m "feat(core): add GiveawayRepository.GetParticipantCount"
```

---

### Task 3: PreventRewinning kazanan cache

**Files:**
- Modify: `LiveDeck.Core/Sales/GiveawayService.cs`
- Test: `LiveDeck.Tests/Sales/GiveawayServicePreventRewinningCacheTests.cs` (new)

**Context:** Şu an PreventRewinning aktifse `AddParticipantFromChat` her chat mesajı için DB JOIN sorgusu yapıyor. Aktif çekilişin lifetime'ı boyunca kazanan customer-id set'i değişmediği için bir kez `Start`'ta cache'lenebilir.

Note: Bu task `GiveawayServiceTests` mevcut Mock framework olmadan çalışıyor (real `GiveawayRepository` + `InMemorySqlite`). Cache hit verification için hibrit yaklaşım: gerçek repo'yu kullan, çağrı sayısını test edebilmek için fixture içine bir `CallCountingGiveawayRepository` wrapper koy.

Daha basit ve güvenilir alternatif: cache davranışını gözlemlenebilir bir invariant ile test et — `Start` sonrası DB'den past winner kaydını sil, sonra `AddParticipantFromChat` o kullanıcıyı hâlâ filtrelemeli (cache'lenmiş set kullanıyor demek). Bu plan ikinciyi kullanır — test daha basit, mock'suz.

- [ ] **Step 1: Write failing test**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/Sales/GiveawayServicePreventRewinningCacheTests.cs`:

```csharp
using System;
using Dapper;
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

public class GiveawayServicePreventRewinningCacheTests
{
    private static (GiveawayService Svc, GiveawayRepository Repo, CustomerRepository Customers,
                    InMemorySqlite Db, string SessionId) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();

        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(1000L);

        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));

        var customerRepo = new CustomerRepository(db);
        var customerSvc  = new CustomerService(customerRepo, clock.Object);
        var giveawayRepo = new GiveawayRepository(db);
        var drawer       = new GiveawayDrawer();

        return (new GiveawayService(giveawayRepo, customerSvc, drawer, clock.Object),
                giveawayRepo, customerRepo, db, "s1");
    }

    private static ChatMessage Msg(string username, string text) =>
        new(Guid.NewGuid().ToString("N"), "instagram", null, username, username, null, text, 1000,
            Array.Empty<string>());

    [Fact]
    public void Start_caches_previous_winners_so_AddParticipantFromChat_does_not_requery()
    {
        var (svc, repo, customers, db, sid) = Fx();
        using var _ = db;

        // First giveaway: @winner wins
        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner", "🌹"));
        svc.Draw(g1.Id);

        // Second giveaway starts → cache is built from DB at Start time
        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);

        // Tamper with DB: forcibly clear the IsWinner flag from g1's participant.
        // If the service cached the winner set at Start, @winner is still filtered.
        // If it re-queries on every AddParticipantFromChat, @winner would now sneak in.
        using (var conn = db.Open())
        {
            conn.Execute("UPDATE GiveawayParticipant SET IsWinner = 0 WHERE Username = '@winner'");
        }

        svc.AddParticipantFromChat(g2.Id, Msg("@winner", "🎁"));
        svc.AddParticipantFromChat(g2.Id, Msg("@new",    "🎁"));

        // @winner is still filtered (cache used), only @new is added
        var ps = repo.GetParticipants(g2.Id);
        ps.Should().HaveCount(1);
        ps[0].Username.Should().Be("@new");
    }

    [Fact]
    public void Draw_clears_cache_so_next_Start_rebuilds()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;

        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner1", "🌹"));
        svc.Draw(g1.Id);

        // Cache from g1 is cleared on Draw. New Start should re-query.
        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g2.Id, Msg("@winner1", "🎁"));   // previous winner → filtered
        svc.AddParticipantFromChat(g2.Id, Msg("@fresh",   "🎁"));

        var ps = repo.GetParticipants(g2.Id);
        ps.Select(p => p.Username).Should().BeEquivalentTo(new[] { "@fresh" });
    }

    [Fact]
    public void Cancel_clears_cache()
    {
        var (svc, repo, _, db, sid) = Fx();
        using var _2 = db;

        var g1 = svc.Start(sid, "🌹", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g1.Id, Msg("@winner1", "🌹"));
        svc.Draw(g1.Id);

        var g2 = svc.Start(sid, "🎁", 60, 1, null, preventRewinning: true);
        svc.Cancel(g2.Id);

        // After Cancel, Start a fresh giveaway — cache must be rebuilt
        var g3 = svc.Start(sid, "✨", 60, 1, null, preventRewinning: true);
        svc.AddParticipantFromChat(g3.Id, Msg("@winner1", "✨"));   // still filtered

        repo.GetParticipants(g3.Id).Should().BeEmpty();
    }
}
```

Note: top of file needs `using System.Linq;`.

- [ ] **Step 2: Add `using System.Linq;`**

Add `using System.Linq;` to the new file's using block (above `using Moq;`).

- [ ] **Step 3: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayServicePreventRewinningCacheTests" 2>&1 | tail -10
```
Expected: first test FAILS (current code re-queries DB so @winner sneaks in after the IsWinner tamper).

- [ ] **Step 4: Implement cache in GiveawayService**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/GiveawayService.cs`.

Add field below `Active` property:
```csharp
private System.Collections.Generic.HashSet<string>? _activePreviousWinners;
```

Modify `Start` method — replace its current body with:
```csharp
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
    Active = g;

    // Cache previous winners ONCE so per-message lookups stay O(1) instead of hitting the DB.
    _activePreviousWinners = preventRewinning
        ? new System.Collections.Generic.HashSet<string>(
            _giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id))
        : null;

    Started?.Invoke(new GiveawayStartedEvent(
        g.Id, g.Keyword, g.WinnerCount, g.DurationSeconds, g.StartedAt));
    return g;
}
```

Modify `AddParticipantFromChat` PreventRewinning block — replace:
```csharp
// (d) PreventRewinning check
if (g.PreventRewinning)
{
    var prevWinners = _giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id);
    if (prevWinners.Contains(customer.Id)) return;
}
```

with:
```csharp
// (d) PreventRewinning check — cache populated by Start; defensive fallback to DB if null.
if (g.PreventRewinning)
{
    var prevWinners = _activePreviousWinners
        ?? new System.Collections.Generic.HashSet<string>(
            _giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id));
    if (prevWinners.Contains(customer.Id)) return;
}
```

Modify `Draw` method — at the end, just before the existing `WinnersDrawn?.Invoke(...)`, add cache clear. Replace the section:
```csharp
_giveaways.MarkEnded(g.Id, _clock.UnixNow());
if (Active?.Id == g.Id) Active = null;
```

with:
```csharp
_giveaways.MarkEnded(g.Id, _clock.UnixNow());
if (Active?.Id == g.Id)
{
    Active = null;
    _activePreviousWinners = null;
}
```

Modify `Cancel`:
```csharp
public void Cancel(string giveawayId)
{
    _giveaways.MarkCancelled(giveawayId, _clock.UnixNow());
    if (Active?.Id == giveawayId)
    {
        Active = null;
        _activePreviousWinners = null;
    }
    Cancelled?.Invoke(new GiveawayCancelledEvent(giveawayId));
}
```

- [ ] **Step 5: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~GiveawayServicePreventRewinningCacheTests" 2>&1 | tail -3
```
Expected: 3/3 pass.

- [ ] **Step 6: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: 82/82 pass (79 + 3 new).

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/GiveawayService.cs LiveDeck.Tests/Sales/GiveawayServicePreventRewinningCacheTests.cs
git commit -m "perf(core): cache previous winners per giveaway in GiveawayService"
```

---

### Task 4: Overlay — Late-join TotalCount snapshot + GetActiveParticipantCount proxy + platform emoji map

**Files:**
- Modify: `LiveDeck.Core/Sales/GiveawayService.cs` (add proxy method)
- Modify: `LiveDeck.Overlay/OverlayHost.cs` (extend snapshot)
- Modify: `LiveDeck.Overlay/wwwroot/giveaway.js` (platform emoji map)

**Context:** Three small changes that ship together because they all touch the OBS overlay layer.

#### 4a: GiveawayService.GetActiveParticipantCount

- [ ] **Step 1: Add proxy method**

In `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Core/Sales/GiveawayService.cs`, just after the `Active` property:

```csharp
/// <summary>Live participant count for the active giveaway, or 0 when none active.</summary>
public int GetActiveParticipantCount() =>
    Active is null ? 0 : _giveaways.GetParticipantCount(Active.Id);
```

#### 4b: OverlayHost late-join sends participant count

- [ ] **Step 2: Extend HandleGiveawayClient**

In `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/OverlayHost.cs`, locate the `HandleGiveawayClient` method's late-join block:

```csharp
var active = _giveaway.Active;
if (active is not null)
{
    var evt = new OverlayEvent("giveaway.started", new GiveawayStartedEvent(
        active.Id, active.Keyword, active.WinnerCount, active.DurationSeconds, active.StartedAt));
    await SendJson(ws, evt, ct);
}
```

Replace with:

```csharp
var active = _giveaway.Active;
if (active is not null)
{
    var startedEvt = new OverlayEvent("giveaway.started", new GiveawayStartedEvent(
        active.Id, active.Keyword, active.WinnerCount, active.DurationSeconds, active.StartedAt));
    await SendJson(ws, startedEvt, ct);

    // Late-joining overlay should also see current participant count, not 0.
    var count = _giveaway.GetActiveParticipantCount();
    if (count > 0)
    {
        var countEvt = new OverlayEvent("giveaway.participant", new GiveawayParticipantEvent(
            active.Id,
            Username: "",
            DisplayName: null,
            AvatarUrl: null,
            Platform: "",
            TotalCount: count));
        await SendJson(ws, countEvt, ct);
    }
}
```

#### 4c: giveaway.js platform emoji map

- [ ] **Step 3: Replace inline ternary with map**

In `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Overlay/wwwroot/giveaway.js`, locate the `revealWinners` function. The current code:

```javascript
li.innerHTML = `
  <span class="platform-${w.Platform}">${w.Platform === 'instagram' ? '📷' : '🎵'}</span>
  <span class="name">${escapeHtml(w.DisplayName || w.Username)}</span>
`;
```

Replace with:

```javascript
const PLATFORM_EMOJI = { instagram: '📷', tiktok: '🎵' };
const emoji = PLATFORM_EMOJI[w.Platform] || '💬';
li.innerHTML = `
  <span class="platform-${w.Platform}">${emoji}</span>
  <span class="name">${escapeHtml(w.DisplayName || w.Username)}</span>
`;
```

(`PLATFORM_EMOJI` defined inside the loop is fine — it's a constant assignment.)

- [ ] **Step 4: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -3
```
Expected: 0 errors.

- [ ] **Step 5: Run tests (regression check)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: 82/82 pass. (No new tests in Task 4 — overlay live behavior is verified by manual smoke; existing OverlayHost tests still pass.)

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.Core/Sales/GiveawayService.cs LiveDeck.Overlay/OverlayHost.cs LiveDeck.Overlay/wwwroot/giveaway.js
git commit -m "feat(overlay): send TotalCount snapshot on late-join + platform emoji map"
```

---

### Task 5: TrFormats helper

**Files:**
- Create: `LiveDeck.App/Formatting/TrFormats.cs`

**Context:** Şu anki tarih/saat formatlamaları kod içinde dağınık (`StreamHistoryViewModel`, `StreamReportViewModel`). Tek noktada `tr-TR` `CultureInfo` ve iki yardımcı metod.

- [ ] **Step 1: Create TrFormats.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Formatting/TrFormats.cs`:

```csharp
using System;
using System.Globalization;

namespace LiveDeck.App.Formatting;

/// <summary>
/// Application-wide tr-TR formatting helpers. Keep WPF Bindings culture-agnostic but
/// guarantee tr-TR rendering across machines regardless of system locale.
/// </summary>
public static class TrFormats
{
    /// <summary>Shared tr-TR culture instance — use for all explicit format calls.</summary>
    public static readonly CultureInfo TR = new("tr-TR");

    /// <summary>"100,50 TL" — fixed-grouping currency text.</summary>
    public static string Currency(decimal value) => value.ToString("N2", TR) + " TL";

    /// <summary>"28 Nis 2026 14:30" — short Turkish date.</summary>
    public static string DateTime(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("d MMM yyyy HH:mm", TR);

    /// <summary>"28 Nisan 2026 14:30" — long Turkish date.</summary>
    public static string DateTimeLong(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("d MMMM yyyy HH:mm", TR);
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
git add LiveDeck.App/Formatting/TrFormats.cs
git commit -m "feat(app): add TrFormats helper (shared tr-TR CultureInfo + currency/date)"
```

---

### Task 6: App-wide tr-TR culture

**Files:**
- Modify: `LiveDeck.App/App.xaml.cs`

**Context:** WPF Binding `StringFormat` ve C# `decimal.ToString("N2")` çağrıları current thread culture'ı kullanır. Sistem locale'ı en-US olan bir makinada `100.00 TL` görünür; tr-TR için `100,50 TL` doğru. App startup'ta culture sabitle.

- [ ] **Step 1: Set culture in App.OnStartup**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/App.xaml.cs`. Add usings:

```csharp
using System.Globalization;
using System.Windows.Markup;
using LiveDeck.App.Formatting;
```

Modify `OnStartup` — at the very top of the method, before `Host = new AppHost()`:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    // Lock culture to tr-TR so number/date/currency formatting is consistent regardless
    // of the OS locale. WPF Binding StringFormat and C# default formats both pick this up.
    var tr = TrFormats.TR;
    Thread.CurrentThread.CurrentCulture = tr;
    Thread.CurrentThread.CurrentUICulture = tr;
    CultureInfo.DefaultThreadCurrentCulture = tr;
    CultureInfo.DefaultThreadCurrentUICulture = tr;
    FrameworkElement.LanguageProperty.OverrideMetadata(
        typeof(FrameworkElement),
        new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(tr.IetfLanguageTag)));

    Host = new AppHost();

    // ... rest of existing OnStartup unchanged
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors.

- [ ] **Step 3: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: 82/82 pass. (Test runner uses xUnit's default culture, not the app's `OnStartup`. No regression.)

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/App.xaml.cs
git commit -m "feat(app): lock thread + framework culture to tr-TR at startup"
```

---

### Task 7: Window-X kapatma guard'ı

**Files:**
- Modify: `LiveDeck.App/MainWindow.xaml.cs`

**Context:** Aktif çekilişte ana pencereyi X ile kapatmak `MainShellViewModel.EndStream`'in çekiliş gate'ini bypass eder. `MainShellView`'in `DataContext`'i `MainShellViewModel`; `MainWindow` ise tek bir `MainShellView` host eder. Window'un `Closing` event'inde VM'in `IsGiveawayActive` flag'ine bak.

- [ ] **Step 1: Locate MainWindow's content binding**

Read `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/MainWindow.xaml`:

```bash
cd /c/Users/burak/source/repos/LiveDeck
head -20 LiveDeck.App/MainWindow.xaml
```

Confirm it embeds a `MainShellView`. The view's `DataContext` is set inside `MainShellView` constructor (already does `App.Host.Services.GetRequiredService<MainShellViewModel>()`). To reach the VM from `MainWindow`, walk the visual tree to find the `MainShellView`.

The simplest path: `MainShellViewModel` is registered as a singleton in DI. Read it directly from `App.Host.Services`.

- [ ] **Step 2: Add OnClosing override**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/MainWindow.xaml.cs`. Replace the file contents:

```csharp
using System.ComponentModel;
using System.Windows;
using LiveDeck.App.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // If a giveaway is active, refuse the close and tell the user to finish/cancel it
        // first — the regular EndStream path has the same gate.
        var vm = App.Host.Services.GetService<MainShellViewModel>();
        if (vm is not null && vm.IsGiveawayActive)
        {
            MessageBox.Show(
                "Aktif çekiliş var. Önce çekilişi tamamla veya iptal et.",
                "Çekiliş aktif",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }
        base.OnClosing(e);
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
git add LiveDeck.App/MainWindow.xaml.cs
git commit -m "fix(app): block window close while giveaway active"
```

---

### Task 8: StreamHistory tarih formatı tr-TR

**Files:**
- Modify: `LiveDeck.App/ViewModels/StreamHistoryViewModel.cs`

**Context:** Şu an `StreamHistoryViewModel` tarihi `"yyyy-MM-dd HH:mm"` ile formatlıyor — culture-bağımsız ama Türkçe ay adı yok. `TrFormats.DateTime`'a geçir.

- [ ] **Step 1: Replace the format call**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/StreamHistoryViewModel.cs`.

Add using:
```csharp
using LiveDeck.App.Formatting;
```

In `Reload`, replace:
```csharp
StartedLabel: DateTimeOffset.FromUnixTimeSeconds(s.StartedAt).LocalDateTime
                .ToString("yyyy-MM-dd HH:mm"),
```

with:
```csharp
StartedLabel: TrFormats.DateTime(s.StartedAt),
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
git add LiveDeck.App/ViewModels/StreamHistoryViewModel.cs
git commit -m "feat(app): use TrFormats.DateTime in StreamHistory rows"
```

---

### Task 9: Excel export tr-TR formatları

**Files:**
- Modify: `LiveDeck.App/ViewModels/StreamReportViewModel.cs`

**Context:** ClosedXML cell-level NumberFormat'ları cross-locale tr-TR davranışını garanti eder. Excel'i en-US locale'de açan bir kullanıcı için bile sayılar `100,50 TL` görünür.

- [ ] **Step 1: Add explicit cell formats**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/StreamReportViewModel.cs`.

Locate the `ExportToExcel` method. Replace the section starting from `ws.Cell(3, 1).Value = "Süre";` through the loop that fills `TopCustomers`, with:

```csharp
ws.Cell(3, 1).Value = "Süre";          ws.Cell(3, 2).Value = DurationLabel;
ws.Cell(4, 1).Value = "Toplam etiket"; ws.Cell(4, 2).Value = TotalLabels;
ws.Cell(4, 2).Style.NumberFormat.Format = "0";

ws.Cell(5, 1).Value = "Toplam ciro";   ws.Cell(5, 2).Value = TotalAmount;
ws.Cell(5, 2).Style.NumberFormat.Format = "#,##0.00 \"TL\"";

ws.Cell(6, 1).Value = "Tekil müşteri"; ws.Cell(6, 2).Value = UniqueCustomers;
ws.Cell(6, 2).Style.NumberFormat.Format = "0";

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
    ws.Cell(row, 3).Style.NumberFormat.Format = "0";
    ws.Cell(row, 4).Value = c.TotalAmount;
    ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00 \"TL\"";
    row++;
}
```

- [ ] **Step 2: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -3
```
Expected: 0 errors.

- [ ] **Step 3: Run full suite**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Tests 2>&1 | tail -3
```
Expected: 82/82 pass.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.App/ViewModels/StreamReportViewModel.cs
git commit -m "feat(report): use tr-TR cell number formats in Excel export"
```

---

### Task 10: Manual Acceptance Smoke

**Files:** None — execute and observe.

**Context:** Faz 3d sekiz lokalize değişikliği sürer. Smoke 5 senaryo ile bunları kapsar (madde 2 görsel doğrulama için Faz 3'te üçüncü platform yok; kod inspection yeter).

- [ ] **Step 1: Start app cleanly**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet run --project LiveDeck.App
```

Verify: App opens, log shows `Schema version: 4`, no errors.

- [ ] **Step 2: Window-X guard test**

- Click "Yayını Başlat".
- Click "🎁 Çekiliş", fill in keyword `test`, duration `5 sn`, winnerCount `1`. Start.
- While the giveaway is active, click the window's X button.

**Expected:** MessageBox appears: "Aktif çekiliş var. Önce çekilişi tamamla veya iptal et." Window does not close.

- [ ] **Step 3: TR keyword test**

- Send a chat message containing `TEST` (uppercase) — should add a participant.
- Send a chat message containing `İSTANBUL` after switching keyword to `istanbul` — should match.
- (Use the browser-extension test harness or manual chat injection tool.)

**Expected:** Both messages register as participants.

- [ ] **Step 4: Late-join overlay TotalCount**

- Start a fresh giveaway with keyword `katil`.
- Send 3 chat messages with keyword. OBS overlay shows `3 katılımcı`.
- In OBS, refresh the Browser Source (right-click → Refresh).
- The overlay reconnects via WebSocket.

**Expected:** After reconnect, the overlay shows `3 katılımcı` (not `0 katılımcı`).

- [ ] **Step 5: Currency format**

- End the stream → Yayın Raporu dialog.
- Verify the "Toplam ciro" line and the per-customer "Tutar" column show `100,50 TL` format (comma decimal separator).
- Click "Excel'e Aktar". Open the resulting .xlsx file (in Excel or LibreOffice Calc).

**Expected:** The currency cells render as `100,50 TL` regardless of the spreadsheet program's locale.

- [ ] **Step 6: Date format**

- Open Yayın Geçmişi dialog (⋮ menu → Yayın Geçmişi).

**Expected:** Date column renders Turkish month names: e.g., `28 Nis 2026 14:30`, not `2026-04-28 14:30`.

- [ ] **Step 7: Commit smoke log (optional)**

If a manual smoke log is kept (`docs/smoke-tests/2026-04-28-phase-3d-smoke.md`), record the date, OS locale, .NET version, and tick each step. Commit:

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add docs/smoke-tests/2026-04-28-phase-3d-smoke.md
git commit -m "docs: phase 3d smoke test results"
```

---

## Self-Review

**Spec coverage check:**

| Spec section                                    | Plan task |
|-------------------------------------------------|-----------|
| 2.1 Window-X guard                              | Task 7    |
| 2.2 Platform emoji map                          | Task 4c   |
| 2.3 Late-join TotalCount                        | Task 4a + 4b (+ Task 2 underlying repo method) |
| 2.4 Kazanan cache                               | Task 3    |
| 2.5 TR keyword eşleşmesi                        | Task 1    |
| 2.6 Para formatı tr-TR                          | Task 6 (global culture)    |
| 2.7 Tarih/saat tr-TR                            | Task 6 + Task 8 + helper Task 5 |
| 2.8 Excel format + locale                       | Task 9    |
| 3 Test stratejisi                               | Tasks 1, 2, 3 add tests; Task 10 manual smoke |
| 4 Hata yönetimi                                 | Distributed (defensive fallback in Task 3, Cancel=true in Task 7, etc.) |
| 5 YAGNI                                         | Plan refrains: no chat.js emoji, no third-platform constants, no test-runner culture flag |
| 7 Kabul kriterleri                              | Task 10 |

All sections covered.

**Placeholder scan:** Searched for "TBD", "TODO", "fill in", "implement later", "appropriate". None found. Every code step shows the actual code.

**Type consistency check:**

- `GiveawayService.GetActiveParticipantCount()` defined Task 4a, consumed Task 4b. Signature: `int` return, no params.
- `GiveawayRepository.GetParticipantCount(string giveawayId)` defined Task 2, consumed by `GetActiveParticipantCount` in Task 4a. Signature consistent.
- `_activePreviousWinners` field — defined Task 3, set in `Start`, read in `AddParticipantFromChat`, cleared in `Draw` and `Cancel`. Type `HashSet<string>?` consistent.
- `TrFormats.TR`, `Currency`, `DateTime`, `DateTimeLong` — defined Task 5, used in Tasks 6 and 8. Names consistent.
- `MainShellViewModel.IsGiveawayActive` — pre-existing public property (P2b), used by Task 7. Verified.

All consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-3d-polish.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Hangisi?
