# Eleme — `eliminator`

Highest-drama animation. Tüm katılımcılar (cap 30) grid'de gösterilir;
isimler tek tek kırmızıya dönüp shake+drop ile elenir. Eleme hızı
cubic ease-out ile yavaşlar (220 ms → 1320 ms aralık) — başta hızlı
elenir, son birkaçı uzun bir tansiyonla ayrılır. Son kalan kazanır,
gold pulse ile büyür.

Kategori: `dramatik`.

Behaviour:
- Phase A (eliminate): scales with pool size, ~350ms average per
  elimination. Cubic ease-out interval growth means the final
  eliminations take longest — peak audience tension.
- Phase B (winner reveal): 1500 ms first / 800 ms subsequent
- Pause: 900 ms before next winner

Multi-winner: previous winners get a permanent `champion` style
(gold border + glow, smaller scale than the active winner). The
elimination round skips champions — they can't be re-eliminated.

`.elim-status` shows live count "N kalan" through the round, flips
to "KAZANAN!" at the reveal.

Audio: none yet. Phase 2 sound design (drum-roll buildup, kick on
each elim, fanfare on winner) will be especially impactful here.
