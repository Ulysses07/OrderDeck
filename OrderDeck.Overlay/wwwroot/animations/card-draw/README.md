# Kart Çekimi — `card-draw`

Şıklı kart-destesi animasyonu. Üst kart desteden ayrılıp sahnede
yatayda kayar, sonra Y ekseninde 180° dönerek arka yüz → ön yüz
flip eder. Ön yüzde kazananın ismi kremimsi bir kart üstünde gold
yıldızlarla gösterilir.

Kategori: `elegant` (wheel/slot-machine/bingo'nun aksine daha sakin
ve hızlı bitiren bir tempo).

## Behaviour

- **Phase A (shuffle):** 1500 ms first / 800 ms subsequent — desteyi
  hafifçe titretir, sahneye odaklanmayı sağlar
- **Phase B (flip):** 1200 ms cubic ease-out rotateY 0→180°
- **Phase C (pause):** 900 ms before next winner

Total per first winner: **3600 ms** (~80 % of wheel's 4500 ms).  
Total per subsequent winner: **2500 ms**.

## Multi-winner

Çekilen kart aşağı kayıp kaybolur (300 ms exit transition), deste bir
kart eksilir (en az 1 kart kalır), sonraki kazanan için sequence baştan
döner.

## Visual spec

| Element | Value |
|---|---|
| Card dimensions | 220 × 320 px |
| Card back bg | `#7f1d1d` + `repeating-linear-gradient` diamond pattern |
| Card front bg | `#fffaf0` (warm cream) |
| Border / accent | `4px solid #ffce46` (gold) |
| Border-radius | `12px` |
| Winner name font | bold 22 px Segoe UI on card · bold 28 px below stage |
| Star decorations | `★` in `#ffce46`, 28 px, top + bottom of front face |

## Audio

None yet. Phase 2 sound design (shuffle rustle + card flick on flip)
ships with the audio-pack pass.
