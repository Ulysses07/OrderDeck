# Düşen İsimler — `falling-names`

Tetris-style falling animation. Participant cards spawn from above
the stage, fall under gravity (CSS keyframe with cubic deceleration),
land near the bottom ground line with a small bounce, then slide off
sideways with a rotation. After the fall window completes, the
winner's card drops in slow-motion (2x slower) and lands centered
with a gold pulse glow.

Kategori: `eğlenceli` — playful kinetic chaos, distinct from
magic-hat's whimsy and wheel/slot/bingo's casino energy.

## Behaviour

- **Phase A (fall):** 4500 ms first / 2800 ms subsequent — spawn-window
  ends 1500 ms early so the last regular card has time to land + exit
- **Phase B (winner):** 1500 ms drop + 800–1500 ms pulse hold
- **Pause:** 900 ms before next winner

## Spawn logic

Spawn cap: ~24 cards per round, staggered evenly across the spawn
window. Per-card slide-off direction (left/right) and rotation are
set via CSS custom properties (`--slide-dir`, `--rot`) so each card
looks unique without per-card keyframes.

## DOM structure

```html
<div class="falling-plugin hidden">
  <div class="falling-stage">
    <div class="falling-spawned"></div>
    <div class="falling-ground"></div>
    <div class="falling-winner"></div>
  </div>
  <div class="falling-name"></div>
</div>
```

## Audio

None yet. Phase 2 sound design (per-card "thunk" + winner-land chime)
ships later.
