# Slot Machine — `slot-machine`

Casino-style vertical reel that scrolls through participant names and
decelerates to land the winner in the centered viewport cell. Visual
ID: dark red + gold (`#8b0000` / `#ffce46`).

Behaviour:
- 4500 ms first winner, 2800 ms subsequent (matches wheel timings)
- Cubic ease-out, identical curve to wheel for cross-animation consistency
- Multi-winner: strip rebuilt per winner, sequential spins, 900 ms pause

Audio: none yet. Phase 2 sound design (scroll loop + ka-chunk on land)
ships with the audio-pack pass.
