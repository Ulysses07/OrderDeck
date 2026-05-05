# Giveaway Animations — Pluggable Multi-Style Library

**Status:** designed, awaiting user spec review then writing-plans
**Author:** captured 2026-05-05 from operator request: "tüm kullanıcılar
aynı animasyonu kullanmak istemeyebilir, başka animasyonlar da ekleyelim"

---

## Context

Today every giveaway uses the single `spinning-wheel` animation
(`OrderDeck.Overlay/wwwroot/giveaway.js` lines 90-219, ~520×520 canvas
+ ease-out cubic spin, 4500ms first winner / 2800ms subsequent). The
visual style is fixed — operators with different stream aesthetics
(elegant, casino, retro, etc.) cannot vary the look without forking the
overlay.

Goal: ship a **pluggable animation library** that gives operators a
gallery of 10 distinct giveaway-reveal styles, picks one as default per
operator and lets them override per-giveaway. The plugin host stays
thin so future animations are pure additions (folder + manifest entry,
no host changes).

## Goals

1. 10 distinct animation styles available out of the box.
2. Default animation chosen per operator (Settings); per-giveaway
   override in `CreateGiveawayDialog`.
3. New animations can be added by dropping a folder + manifest entry —
   no changes to `giveaway.js`, no recompile.
4. Existing wheel keeps working bytewise as default (zero regression).
5. Audio is per-animation (each plugin packs its own sounds), with a
   master volume + "muted" toggle in Settings.
6. Accessibility: respect `prefers-reduced-motion`, ≤3 flashes/sec,
   ≥4.5:1 contrast for all rendered text.

## Non-goals

- Viewer-side voting on animation style (operator-only choice).
- Custom user-uploaded animations (community plugin marketplace) —
  defer to future once the in-house library proves the architecture.
- Per-winner different animations within one giveaway (each giveaway
  uses one chosen style throughout).
- Visual-regression test framework (Playwright screenshot diff). Manual
  smoke checklist covers it; framework overkill at this project size.

## Architecture

### File layout

```
OrderDeck.Overlay/wwwroot/animations/
├── manifest.json                    # metadata for all animations
├── wheel/                           # existing wheel, refactored as plugin
│   ├── index.js                     # ES module — animation contract
│   ├── thumbnail.webp               # 3-second loop, ~200 KB
│   └── audio/
│       ├── tick.mp3
│       └── ding.mp3
├── slot-machine/
│   ├── index.js
│   ├── thumbnail.webp
│   └── audio/{scroll,kachunk}.mp3
├── bingo/
├── card-draw/
├── magic-hat/
├── roulette-strip/
├── spotlight-grid/
├── falling-names/
├── eliminator/
└── race/
```

### Plugin interface (every `index.js` exports this)

```js
export default {
  // Identity (must match manifest entry)
  id: 'slot-machine',
  name: 'Slot Machine',           // display name shown in UI
  description: '3-reel kazino tarzı kayan isim',
  category: 'klasik',             // 'klasik' | 'elegant' | 'eğlenceli'
  thumbnail: './thumbnail.webp',  // relative to plugin folder

  // Lifecycle, called by host (giveaway.js)

  /**
   * Called once when overlay loads, right after the operator's
   * Animation choice resolves. Build initial DOM into `container`,
   * preload audio (audio = AudioController, see below).
   */
  async init(container, audio) { ... },

  /**
   * Drive the show. `winners` = N entries; `pool` = full participant
   * list (used as the visible content the animation churns through).
   * Animation is responsible for its own multi-winner choreography
   * (replay, batch, etc.). Resolves when the show is fully done
   * (winner reveal + any flourish).
   */
  async runFor(winners, pool) { ... },

  /**
   * Tear down DOM, stop any timers, free audio handles.
   * Called when giveaway ends or operator cancels mid-show.
   */
  reset() { ... }
}
```

### Manifest format

```json
{
  "version": 1,
  "animations": [
    {
      "id": "wheel",
      "name": "Çark",
      "description": "Klasik dönen çark",
      "category": "klasik",
      "thumbnail": "wheel/thumbnail.webp"
    },
    {
      "id": "slot-machine",
      "name": "Slot Machine",
      "description": "3-reel kazino tarzı",
      "category": "klasik",
      "thumbnail": "slot-machine/thumbnail.webp"
    }
    /* … 8 daha */
  ]
}
```

The host fetches `manifest.json` once on overlay load. Settings UI in
the WPF app fetches the same file via HTTP for the picker gallery.

### Host responsibilities (giveaway.js refactor)

```js
// On giveaway.started event:
const animationId = startedEvent.AnimationId || 'wheel';
const module = await import(`./animations/${animationId}/index.js`);
const animation = module.default;
const audio = new AudioController(startedEvent.AudioVolume, startedEvent.AudioMuted);
await animation.init(container, audio);

// On giveaway.winners.drawn:
await animation.runFor(event.Winners, event.AnimationPool);

// On giveaway.cancelled or giveaway.ended:
animation.reset();
```

### AudioController (shared utility)

```js
class AudioController {
  constructor(volume /* 0-1 */, muted /* bool */) { ... }
  play(filename)            // resolve relative to plugin folder
  stop(filename)
  setVolume(v)
  setMuted(b)
}
```

Each plugin gets its own `audio/` subfolder; `audio.play('tick.mp3')`
resolves from there. Volume/mute are global, controlled by Settings.

## Server-side changes

### Domain

`OrderDeck.Core/Sales/Giveaway.cs` — add field:
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
    string AnimationId);  // ← NEW. Default 'wheel' for backward compat.
```

### DB migration

`OrderDeck.Core/Storage/Migrations/013_giveaway_animation.sql`:
```sql
ALTER TABLE Giveaway ADD COLUMN AnimationId TEXT NOT NULL DEFAULT 'wheel';
```

Existing rows → `'wheel'` (preserves current behavior).

### Settings shape

`AppSettings` (JSON-persisted today via `SettingsStore`):
```json
{
  "GiveawayAnimation": {
    "DefaultId": "wheel",
    "Volume": 0.7,
    "MutedMode": false
  }
}
```

### Service contract

`GiveawayService.Start(...)` gains an optional `animationId` param:
- Empty/null → use `AppSettings.GiveawayAnimation.DefaultId`
- Unknown id → fallback to `'wheel'` + warning log
- Validation list comes from manifest.json (loaded once at app start)

### Overlay event payload

`giveaway.started` event grows three fields:
```json
{
  "type": "giveaway.started",
  "data": {
    "GiveawayId": "...",
    "Keyword": "...",
    "DurationSeconds": 60,
    "WinnerCount": 1,
    "StartedAt": 1234567890,
    "AnimationId": "slot-machine",        // ← NEW
    "AudioVolume": 0.7,                   // ← NEW
    "AudioMuted": false                    // ← NEW
  }
}
```

Old overlays (cached giveaway.js without plugin host) ignore the new
fields and use the wheel — graceful degradation during rollout.

## Operator UI

### Settings dialog — new "Çekiliş Animasyonu" tab

Layout:
- Header: **"Varsayılan stil (her çekiliş için)"**
- 3-column grid of cards (10 cards across ~4 rows). Each card:
  - Thumbnail loop (~200×150, autoplay muted on hover)
  - Animation name below
  - Selected card: highlighted border + ✓ badge
- Below grid: **[ Önizle ]** button — opens preview window with mock data
- Audio section:
  - Master volume slider (0-100%)
  - "☐ Sessiz mod" checkbox

### CreateGiveawayDialog — override dropdown

Existing fields stay; new row at the bottom:
```
Animasyon: [⚙ Varsayılan (Çark) ▼]  [👁 Önizle]
```
- First dropdown option `⚙ Varsayılan (X)` resolves at start time to the
  Settings default (so changing Settings later updates this).
- Other options = manifest list, override for this one giveaway.
- Eye icon → preview window for the currently-picked option.

### Reusable XAML control: `AnimationPickerControl`

Shared between Settings and Dialog. Bindable:
- `ItemsSource` → list of animation metadata (from manifest)
- `SelectedId` → two-way bound id
- `Mode` → `Gallery` (Settings) | `Compact` (Dialog dropdown)

### Preview window: `AnimationPreviewWindow`

WebView2-hosted (Microsoft.Web.WebView2 NuGet, requires Edge runtime
which ships with Win10 21H2+). Loads the overlay HTML with mock data
injected via `window.postMessage` instead of a real WebSocket:

```js
// Test harness inside preview window
window.addEventListener('message', e => {
  if (e.data.type === 'mock-giveaway') {
    onStarted({ GiveawayId: 'preview', Keyword: 'preview',
                DurationSeconds: 0, WinnerCount: 1,
                AnimationId: e.data.animationId,
                AudioVolume: e.data.volume, AudioMuted: e.data.muted });
    setTimeout(() => onWinnersDrawn({
      GiveawayId: 'preview',
      AnimationPool: ['Ali', 'Ayşe', 'Mehmet', 'Zeynep', 'Burak',
                       'Cem', 'Deniz', 'Ela', 'Fatih', 'Gizem']
        .map(n => ({ Username: n, DisplayName: n, Platform: 'instagram' })),
      Winners: [{ Username: 'Ayşe', DisplayName: 'Ayşe', Platform: 'instagram' }]
    }), 500);
  }
});
```

Operator sees the animation render exactly as it will in OBS browser
source.

## Accessibility

### Plugin guidelines (`wwwroot/animations/CONTRIBUTING.md` to be added)

Mandatory for every animation:
- Reduced-motion fallback: under `@media (prefers-reduced-motion: reduce)`,
  skip kinetic motion; reveal winner via 1-second fade-in.
- Flash rate: **≤3 flashes/sec** in any 1-second window (WCAG 2.3.1).
- Text contrast: **≥4.5:1** for all displayed names (WCAG AA).
- Audio defaults: master volume 70%, muted-mode off (sound enabled on
  first install). The "opt-in" rule is that no animation may exceed
  master volume internally — every `audio.play(file)` call goes
  through `AudioController` which respects the muted toggle and global
  volume. No animation may bypass the controller (e.g. raw
  `<audio>` tag) — design contract, enforced by code review.

### Host enforcement

`giveaway.js` host wraps the animation container in a CSS class:
`.respect-motion`. The base stylesheet:
```css
@media (prefers-reduced-motion: reduce) {
  .respect-motion *,
  .respect-motion *::before,
  .respect-motion *::after {
    animation: none !important;
    transition: opacity 1s ease !important;
  }
}
```

This is a host-level safety net so a non-compliant plugin still
degrades gracefully instead of strobing a vulnerable viewer.

## Testing strategy

### Unit (xUnit)
- `GiveawayService.Start(animationId)`:
  - Empty → uses settings default
  - Unknown id → falls back to `'wheel'` + logs warning
  - Valid id → propagates verbatim to the inserted Giveaway row
- DB migration `013_giveaway_animation.sql` is idempotent.
- `AnimationPickerControl` view-model: `SelectedId` change fires
  `PropertyChanged` once.

### Integration
- `OverlayHost` `giveaway.started` event payload contains `AnimationId`,
  `AudioVolume`, `AudioMuted` from the active giveaway + settings.
- Settings → AppSettings round-trip persists `GiveawayAnimation` block
  bytewise.

### Manual smoke
For each of the 10 animations:
- 0 / 1 / 5 / 30 participants
- 1 / 3 / 5 winners
- Audio on / off
- `prefers-reduced-motion` enabled
- Normal CSS rebuild after dropping a new manifest entry — old overlay
  refresh picks up the change without restart.

Each animation gets a checklist in
`docs/manual-smoke/giveaway-animation-<id>.md`.

### Out of scope
- Visual snapshot diff (Playwright). Re-evaluate at 30+ animations.
- Per-frame timing assertion (animation duration jitter is fine).

## Backward compatibility

- DB migration backfills `'wheel'` for every pre-existing Giveaway row.
- Old `giveaway.js` (browser-cached, hasn't reloaded since release)
  ignores the new event fields and falls into its existing wheel render
  path — visually identical to today.
- New `giveaway.js` host with `AnimationId` missing from a server it
  shouldn't see (mismatched versions) defaults to `'wheel'`.

## Implementation phases

### Phase 1 — Foundation (~30-40 h focused)
- Refactor `giveaway.js` → plugin host (host responsibilities listed
  above; existing wheel logic stays in tree but is unused).
- Create `wheel/index.js` plugin: 1:1 port of the current wheel
  (ease-out cubic, slice palette, 4500ms / 2800ms timings preserved).
- DB migration + `Giveaway.AnimationId` field + service param.
- `AppSettings.GiveawayAnimation` block + `SettingsStore` round-trip.
- `AnimationPickerControl` (gallery mode) + new Settings tab.
- Manifest scaffolding + `manifest.json` with one entry (`wheel`).
- Unit + integration tests for the above.

**Deliverable:** existing operators see no change; system can host
plugins.

### Phase 2 — First new animations (~40-50 h focused)
- 3 new animations: `slot-machine`, `bingo`, `card-draw`.
- Per-plugin audio packs.
- Manifest grows to 4 entries.
- `AnimationPreviewWindow` (WebView2-hosted).
- `CreateGiveawayDialog` override dropdown.
- Manual smoke for all 4.

**Deliverable:** operators have meaningful choice; gallery is real.

### Phase 3 — Completion (~50-60 h focused)
- 6 remaining animations: `magic-hat`, `roulette-strip`,
  `spotlight-grid`, `falling-names`, `eliminator`, `race`.
- Accessibility audit pass over all 10.
- Manifest auto-generation script (drop folder + entry → script
  rebuilds manifest.json) — quality-of-life for future.
- All-10 manual smoke checklist runs green.

**Deliverable:** full library shipped.

Total: ~120-150 hours focused work; ~6-8 weeks at part-time solo dev
pace. Each phase is its own PR / tag, shippable independently.

## Risks & mitigations

1. **WebView2 dependency on Win10 < 21H2.** Mitigation: detect at
   runtime, fall back to "preview unavailable" with a deep link to
   open overlay in default browser with `?preview=animationId`.
2. **Audio asset bloat.** Each animation's audio could grow to MBs.
   Mitigation: strict per-animation budget (≤500 KB total audio),
   prefer `.mp3` 96 kbps mono. CONTRIBUTING.md captures this.
3. **Animation creator divergence.** Without strict guidelines a
   contributor might break the contract. Mitigation: TypeScript-style
   JSDoc on the interface + a smoke-mock harness any new animation
   must run through. (Phase 3 task.)
4. **Browser cache staleness.** Operators upgrading might cache an old
   giveaway.js. Mitigation: cache-bust via versioned URL param on the
   overlay HTML (`giveaway.js?v=<assemblyVersion>`).
5. **Phase 3 fatigue.** 6 animations is a lot. Mitigation: phases 2
   and 3 each gate on user value (operator feedback after phase 2 may
   prioritise different animations than the original 6).

## Open questions

None at design time; all decisions covered above.
