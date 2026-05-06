# Rulet Şeridi — `roulette-strip`

CS:GO case-opening tarzı yatay rulet animasyonu. Tüm katılımcılar
isim kartları olarak yatay bir şeritte tekrar ederek dizilir; şerit
sola hızla kayar ve cubic ease-out ile yavaşlayarak kazananın
kartını orta işaretçinin altına bırakır. Viewport kenarları fade
mask ile kararır, derinlik hissi verir.

Kategori: `klasik`.

## Behaviour

- **Spin:** 4500 ms first / 2800 ms subsequent (wheel/slot/spotlight'la
  aynı timing) — cubic ease-out
- **Pause:** 900 ms before next winner
- **Strip composition:** pool 5 kez tekrarlanır + son segmentte kazanan
  konumu; total `~5 * pool.length + 1` cell. Kazanan cell'in tam ortası
  (jitter ±20 % cell width) marker hizasına gelir.

## Multi-winner

Her kazanan için strip baştan kurulur (yeni shuffle gerekmez,
`EXTRA_CYCLES` sabittir). Sequential spins.

## Audio

None yet. Phase 2 sound design (tick-tick-tick + bell on land) will
be the natural pairing for this animation's tempo.

## DOM structure

```html
<div class="roulette-plugin hidden">
  <div class="roulette-frame">
    <div class="roulette-marker-top"></div>
    <div class="roulette-viewport">
      <div class="roulette-strip"></div>
    </div>
    <div class="roulette-marker-bottom"></div>
  </div>
  <div class="roulette-name"></div>
</div>
```

## Constants

| Name | Value | Description |
|---|---|---|
| `CELL_WIDTH` | 140 px | Width of each name cell |
| `VIEWPORT_WIDTH` | 700 px | Visible window (5 cells) |
| `EXTRA_CYCLES` | 5 | Full pool repeats before landing |
