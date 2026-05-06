# Tombala — `bingo`

Bingo / lottery hopper animation. N balls (one per participant, capped
at 25 visible) bounce inside a circular hopper with simple physics
(gravity + elastic walls). At draw time, the winner's ball separates
from the swarm and lands in the display zone below.

Behaviour:
- Phase A (chaos): 3000 ms first winner / 1800 ms subsequent — slightly
  faster than wheel's 4500/2800 because the busy visual saturates
  attention sooner
- Phase B (drop): 1500 ms cubic ease-out from swarm to winner zone
- Phase C (pause): 900 ms before the next draw

Multi-winner: each landed ball is removed from the swarm so the next
draw runs against the remaining pool.

Audio: none yet. Phase 2 sound design (ball-bounce loop + drop chime)
ships with the audio-pack pass.
