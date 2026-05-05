// animations/slot-machine/index.js — casino-style vertical reel plugin.
// The strip is a tall column of name-cells scrolled with translateY.
// Ease-out cubic matches wheel/index.js for cross-animation consistency.

// ── Geometry constants ──────────────────────────────────────────────────
const CELL_H      = 60;   // px — height of each name cell in the strip
const VIEWPORT_H  = 180;  // px — shows 3 cells (3 × 60)
// The "landing slot" is the middle cell: its top edge is at CELL_H from
// viewport top, so its centre sits at CELL_H + CELL_H/2 = 90 = viewport
// centre. Landing translateY for cell index N (0-based inside strip):
//   -(N * CELL_H - CELL_H) === -(N - 1) * CELL_H
const EXTRA_CYCLES = 6;   // full strip scrolls added before the landing

export default {
  id: 'slot-machine',
  name: 'Slot Machine',
  description: 'Kazino tarzı 3-reel kayan isim',
  category: 'klasik',
  thumbnail: './thumbnail.svg',

  // Internals (set by init, matches wheel pattern)
  _container: null,
  _audio: null,
  _root: null,
  _strip: null,
  _nameEl: null,

  async init(container, audio) {
    this._container = container;
    this._audio = audio;  // reserved for Phase-2 audio packs; no play() yet

    container.innerHTML = `
      <div class="slot-plugin hidden">
        <div class="slot-frame">
          <div class="slot-marker"></div>
          <div class="slot-viewport">
            <div class="slot-strip"></div>
          </div>
          <div class="slot-marker"></div>
        </div>
        <div class="slot-name"></div>
      </div>`;

    this._root   = container.querySelector('.slot-plugin');
    this._strip  = container.querySelector('.slot-strip');
    this._nameEl = container.querySelector('.slot-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._nameEl.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }

    this._show();

    for (let i = 0; i < winners.length; i++) {
      const w = winners[i];
      const dur = i === 0 ? 4500 : 2800;

      this._root.classList.remove('landed');
      await this._spin(w, pool, dur);

      if (i < winners.length - 1) {
        await new Promise(r => setTimeout(r, 900));
      }
    }
  },

  reset() {
    if (this._root)      this._root.classList.add('hidden');
    if (this._audio)     this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  // ── Private ────────────────────────────────────────────────────────────

  _show() { this._root.classList.remove('hidden'); },

  /** Build the strip DOM for one spin. Returns the final translateY value. */
  _buildStrip(winner, pool) {
    // Shuffle pool so the scroll looks random each time
    const shuffled = [...pool].sort(() => Math.random() - 0.5);

    // Repeat the shuffled list enough times so the total strip height
    // covers EXTRA_CYCLES of full-strip scroll + the landing position.
    const repeats = EXTRA_CYCLES + 2;            // generous padding
    const cells   = [];
    for (let r = 0; r < repeats; r++) {
      cells.push(...shuffled);
    }

    // Place the winner at a specific index deep in the strip so the
    // landing target is well past the beginning. Pick an index in the
    // second-to-last repeat so the deceleration always has room.
    // Target index = last repeat's start + winner's position in shuffled.
    const winnerInShuffle = shuffled.findIndex(
      p => p.Username === winner.Username && p.Platform === winner.Platform
    );
    const landingIdx = (repeats - 1) * shuffled.length +
                       (winnerInShuffle >= 0 ? winnerInShuffle : 0);

    // Overwrite that cell to guarantee the correct name shows.
    cells[landingIdx] = winner;

    // Render cells into the DOM strip
    this._strip.innerHTML = '';
    cells.forEach((p, idx) => {
      const div = document.createElement('div');
      div.className = 'slot-cell';
      div.dataset.idx = idx;
      div.textContent = p.DisplayName || p.Username || '';
      this._strip.appendChild(div);
    });

    // translateY so landingIdx cell sits in the centre slot.
    // Centre slot top = CELL_H from viewport top → strip must shift up by
    // (landingIdx * CELL_H - CELL_H) pixels = (landingIdx - 1) * CELL_H.
    const finalY = -((landingIdx - 1) * CELL_H);

    return { finalY, landingIdx };
  },

  _spin(winner, pool, durationMs) {
    return new Promise(resolve => {
      const { finalY, landingIdx } = this._buildStrip(winner, pool);

      // Total scroll: start at translateY 0, end at finalY (negative).
      // finalY is already negative (scrolls upward).
      const startY = 0;
      const totalY = finalY - startY;   // negative delta → scroll up

      const start = performance.now();
      let lastCentreIdx = -1;

      const frame = (now) => {
        const t      = Math.min(1, (now - start) / durationMs);
        const eased  = 1 - Math.pow(1 - t, 3);   // cubic ease-out (same as wheel)
        const currentY = startY + totalY * eased;

        this._strip.style.transform = `translateY(${currentY}px)`;

        // Which cell is currently in the centre slot?
        // Centre slot top = CELL_H. Strip top is at currentY relative to
        // viewport → cell whose top edge hits CELL_H inside viewport is at
        // strip-local offset CELL_H - currentY.
        const centreOffset = CELL_H - currentY;  // pixels from strip top
        const centreIdx    = Math.max(0, Math.floor(centreOffset / CELL_H));

        if (centreIdx !== lastCentreIdx) {
          const el = this._strip.children[centreIdx];
          if (el) this._nameEl.textContent = el.textContent;
          lastCentreIdx = centreIdx;
        }

        if (t < 1) {
          requestAnimationFrame(frame);
        } else {
          // Snap to exact final position and mark winner cell
          this._strip.style.transform = `translateY(${finalY}px)`;
          const winnerEl = this._strip.children[landingIdx];
          if (winnerEl) {
            winnerEl.classList.add('center-cell');
            this._nameEl.textContent = winnerEl.textContent;
          }
          this._root.classList.add('landed');
          resolve();
        }
      };

      requestAnimationFrame(frame);
    });
  }
};
