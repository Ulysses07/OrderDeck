# Spot Işığı — `spotlight-grid`

Sakin, düzenli bir animasyon. Tüm katılımcılar (24 cap) bir grid'de
isim kartları olarak gösterilir; gold spot ışığı hücreden hücreye
hızla zıplayıp yavaşlayarak kazananın kartında durur. Önceki
kazananların kartları kalıcı altın çerçeve + nabız atan glow ile
işaretli kalır.

Kategori: `minimal` — wheel/slot/bingo'nun kazino enerjisinin ve
card-draw'un dramatik anının dışında, "okunabilir" / "envanteri
gösteren" bir tarz.

Behaviour:
- Phase A: 4500 ms first / 2800 ms subsequent — hops use cubic
  ease-out on the interval (60ms → 440ms) so the light decelerates
  naturally and lands on the winner cell as the last hop.
- Pause: 900 ms before next winner.

Multi-winner: previously-lit cells stay gold via the `won` class,
so the audience can see all winners at the end while a new spot
hops to the next one.

Audio: none yet. Phase 2 sound design (light buzz + lock-in click)
ships later.
