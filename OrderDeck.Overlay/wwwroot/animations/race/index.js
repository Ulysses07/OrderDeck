// animations/race/index.js — horizontal lane-based race plugin.
// Each participant is a coloured "car" (rounded rectangle with name) in its own
// lane. Cars race left→right; the winner edges out at the finish line at 95%.
// Ease-out quadratic (1-(1-t)²) gives a natural acceleration → deceleration arc.

const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

const VISIBLE_LANES    = 8;
const FINISH_LINE_PCT  = 92;  // % from left; finish line is drawn at 95%

export default {
  id: 'race',
  name: 'Yarış',
  description: 'İsim arabaları finiş çizgisine yarışır',
  category: 'dramatik',
  thumbnail: './thumbnail.svg',

  _container: null, _audio: null, _synth: null, _root: null,
  _lanes: null, _name: null,
  _animationFrame: null,

  async init(container, audio, synth) {
    this._container = container;
    this._audio = audio;  // reserved for Phase-2 sound pack; no play() yet
    this._synth = synth || null;

    container.innerHTML = `
      <div class="race-plugin hidden">
        <div class="race-track">
          <div class="race-lanes"></div>
          <div class="race-finish-line"></div>
          <div class="race-banner">FİNİŞ</div>
        </div>
        <div class="race-name"></div>
      </div>`;

    this._root  = container.querySelector('.race-plugin');
    this._lanes = container.querySelector('.race-lanes');
    this._name  = container.querySelector('.race-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }
    this._show();

    const wonIds = new Set();

    for (let i = 0; i < winners.length; i++) {
      const winner = winners[i];
      let winnerIdx = pool.findIndex(p =>
        p.Username === winner.Username && p.Platform === winner.Platform);
      if (winnerIdx < 0) winnerIdx = 0;

      // Pick visible lane occupants: winner + up to (VISIBLE_LANES-1) others,
      // excluding participants that have already won a previous round.
      const lanePool = this._pickLaneOccupants(pool, winnerIdx, wonIds);
      const winnerLaneIdx = lanePool.indexOf(pool[winnerIdx]);

      this._buildLanes(lanePool);

      const dur = i === 0 ? 4500 : 2800;
      await this._racePhase(lanePool, winnerLaneIdx, dur);

      // Phase B (finish reveal) is woven into _racePhase's final frame.
      this._name.textContent = winner.DisplayName || winner.Username || '';

      // Phase C — pause before next winner.
      await new Promise(r => setTimeout(r, 900));

      wonIds.add(winnerIdx);
    }
  },

  reset() {
    if (this._animationFrame) cancelAnimationFrame(this._animationFrame);
    this._animationFrame = null;
    if (this._root)      this._root.classList.add('hidden');
    if (this._audio)     this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  // ── Private ────────────────────────────────────────────────────────────────

  _show() { this._root.classList.remove('hidden'); },

  /**
   * Build the lane occupant list. Always includes the winner at one slot;
   * fills remaining up to VISIBLE_LANES from non-winning, non-past-winner
   * participants (shuffled for variety). Final list is shuffled so the
   * winner doesn't always appear in lane 0.
   */
  _pickLaneOccupants(pool, winnerIdx, wonIds) {
    const winner = pool[winnerIdx];
    const result = [winner];

    // Collect eligible fillers (not the winner, not a past winner).
    const others = pool
      .map((p, idx) => ({ p, idx }))
      .filter(({ idx }) => idx !== winnerIdx && !wonIds.has(idx))
      .map(({ p }) => p);

    // Fisher-Yates shuffle for filler candidates.
    for (let k = others.length - 1; k > 0; k--) {
      const j = Math.floor(Math.random() * (k + 1));
      [others[k], others[j]] = [others[j], others[k]];
    }
    for (const o of others) {
      if (result.length >= VISIBLE_LANES) break;
      result.push(o);
    }

    // Shuffle final occupant order so the winner isn't always lane 0.
    for (let k = result.length - 1; k > 0; k--) {
      const j = Math.floor(Math.random() * (k + 1));
      [result[k], result[j]] = [result[j], result[k]];
    }
    return result;
  },

  /** Create lane + car DOM nodes for the given occupant list. */
  _buildLanes(occupants) {
    this._lanes.innerHTML = '';
    for (let i = 0; i < occupants.length; i++) {
      const p    = occupants[i];
      const lane = document.createElement('div');
      lane.className = 'race-lane';

      const car  = document.createElement('div');
      car.className   = 'race-car';
      car.textContent = (p.DisplayName || p.Username || '').slice(0, 14);
      car.style.background = SLICE_COLORS[i % SLICE_COLORS.length];

      lane.appendChild(car);
      this._lanes.appendChild(lane);
    }
  },

  /**
   * Animate the race. Returns a Promise that resolves after the winner
   * crosses the finish line and the won class has been applied (phase B).
   *
   * Strategy:
   *   - Each car gets a random base-speed multiplier (0.85–1.15).
   *   - Loser cars are additionally capped at a random position 3–15%
   *     short of the finish line, so the winner cleanly edges them out.
   *   - Progress is driven by quadratic ease-out on the global time ratio t.
   */
  _racePhase(lanePool, winnerLaneIdx, durationMs) {
    return new Promise(resolve => {
      const cars      = Array.from(this._lanes.querySelectorAll('.race-car'));
      // Random speed multiplier per car (all cars, including winner).
      const speeds    = cars.map(() => 0.85 + Math.random() * 0.30);
      // Per-loser hard cap: they stop 3–15% short of the finish line.
      const loserCaps = cars.map(() => FINISH_LINE_PCT - (3 + Math.random() * 12));

      const start = performance.now();
      // Rev at race start
      if (this._synth) this._synth.rev();
      // Track which 25% milestones have already fired (0=25%, 1=50%, 2=75%, 3=100%)
      const milestonesFired = [false, false, false, false];

      const frame = (now) => {
        const t     = Math.min(1, (now - start) / durationMs);
        // Quadratic ease-out: fast start, smooth deceleration into finish.
        const eased = 1 - Math.pow(1 - t, 2);

        // Fire hoofbeat ticks at 25%, 50%, 75%, 100% progress milestones
        if (this._synth) {
          const mIdx = Math.floor(t * 4);  // 0–3 over t 0–1
          for (let m = 0; m <= Math.min(mIdx, 3); m++) {
            if (!milestonesFired[m]) {
              milestonesFired[m] = true;
              this._synth.tick(1500);
            }
          }
        }

        for (let i = 0; i < cars.length; i++) {
          let progress;
          if (i === winnerLaneIdx) {
            // Winner always reaches exactly FINISH_LINE_PCT by t = 1.
            progress = eased * FINISH_LINE_PCT;
          } else {
            // Losers: scale by their speed multiplier, then clamp at their cap.
            progress = Math.min(eased * speeds[i] * FINISH_LINE_PCT, loserCaps[i]);
          }
          cars[i].style.left = progress + '%';
        }

        if (t < 1) {
          this._animationFrame = requestAnimationFrame(frame);
        } else {
          // Snap winner to exact finish position and apply gold-flash class (Phase B).
          cars[winnerLaneIdx].style.left = FINISH_LINE_PCT + '%';
          cars[winnerLaneIdx].classList.add('won');
          this._animationFrame = null;
          if (this._synth) {
            this._synth.horn();
            setTimeout(() => { if (this._synth) this._synth.fanfare(); }, 500);
          }
          resolve();
        }
      };

      this._animationFrame = requestAnimationFrame(frame);
    });
  }
};
