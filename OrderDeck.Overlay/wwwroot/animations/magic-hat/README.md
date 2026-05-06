# Sihirli Şapka — `magic-hat`

Whimsical magic-show animation. Participant names fly in from random
edges as small colored cards, swirl above a black top hat, and shrink
away into it (one per ~150ms stagger). When all are absorbed, a
golden wand swings down, taps the hat (which jiggles), and the
winner's name rises out with sparkles fanning outward.

Category: `eğlenceli` (different vibe from wheel/slot-machine/bingo's
casino energy and card-draw's elegance).

## Behaviour

- **Phase A (absorb):** 3000 ms first / 1500 ms subsequent — names cap
  at 12 visible to keep the stage readable
- **Phase B (wand tap):** ~700 ms wand swing + 350 ms hat jiggle
- **Phase C (reveal + pause):** 1200 ms first / 800 ms subsequent +
  900 ms before next winner

## Implementation notes

Pure CSS keyframes for `hat-jiggle`, `wand-tap`, `sparkle-fly`.
Per-card absorb transitions handled inline via JS-set CSS properties.
No canvas, no rAF — fully event-loop driven with `setTimeout` stagger.

All pending timers are tracked in `_activeTimers` and cleared in
`reset()` to avoid dangling callbacks if the host tears down the plugin
mid-animation.

## Audio

None yet. Phase 2 sound design (whoosh + pop on absorb,
wand-swish + magical chime on reveal) ships later.
