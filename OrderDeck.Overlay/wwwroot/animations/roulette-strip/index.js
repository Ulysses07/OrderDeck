// animations/roulette-strip/index.js — CS:GO case-opening-style horizontal rulet plugin.
// The strip is a wide row of name-cells scrolled with translateX.
// Ease-out cubic matches wheel/slot-machine/spotlight for cross-animation consistency.

// ── Palette ─────────────────────────────────────────────────────────────────
const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

// ── Geometry constants ───────────────────────────────────────────────────────
const CELL_WIDTH     = 140;  // px — width of each name cell
const VIEWPORT_WIDTH = 700;  // px — shows 5 cells (5 × 140)
const EXTRA_CYCLES   = 5;    // full pool repeats before the landing segment

export default {
  id: 'roulette-strip',
  name: 'Rulet Şeridi',
  description: 'Yatay isim şeridi kazino işaretçisinde durur',
  category: 'klasik',
  thumbnail: './thumbnail.svg',

  // Internals (set by init)
  _container: null, _audio: null, _root: null,
  _viewport: null, _strip: null, _name: null,
  _animationFrame: null,

  async init(container, audio) {
    this._container = container;
    this._audio = audio;  // reserved for Phase-2 audio packs; no play() yet

    container.innerHTML = `
      <div class="roulette-plugin hidden">
        <div class="roulette-frame">
          <div class="roulette-marker-top"></div>
          <div class="roulette-viewport">
            <div class="roulette-strip"></div>
          </div>
          <div class="roulette-marker-bottom"></div>
        </div>
        <div class="roulette-name"></div>
      </div>`;

    this._root     = container.querySelector('.roulette-plugin');
    this._viewport = container.querySelector('.roulette-viewport');
    this._strip    = container.querySelector('.roulette-strip');
    this._name     = container.querySelector('.roulette-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }

    this._show();

    for (let i = 0; i < winners.length; i++) {
      const winner = winners[i];
      const winnerIdx = pool.findIndex(p =>
        p.Username === winner.Username && p.Platform === winner.Platform);
      const dur = i === 0 ? 4500 : 2800;

      this._buildStrip(pool, winnerIdx >= 0 ? winnerIdx : 0);
      await this._spin(dur);

      if (i < winners.length - 1) {
        await new Promise(r => setTimeout(r, 900));
      }
    }
  },

  reset() {
    if (this._animationFrame) cancelAnimationFrame(this._animationFrame);
    this._animationFrame = null;
    if (this._root)      this._root.classList.add('hidden');
    if (this._audio)     this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  // ── Private ─────────────────────────────────────────────────────────────

  _show() { this._root.classList.remove('hidden'); },

  /**
   * Build strip DOM.
   * Layout: EXTRA_CYCLES full pool repeats, then pool items up to and
   * including winnerIdx, so the last cell rendered IS the winner.
   * Total cells = EXTRA_CYCLES * pool.length + winnerIdx + 1.
   */
  _buildStrip(pool, winnerIdx) {
    this._strip.innerHTML = '';
    this._strip.classList.remove('landed');

    const totalCells = EXTRA_CYCLES * pool.length + winnerIdx + 1;
    for (let i = 0; i < totalCells; i++) {
      const p    = pool[i % pool.length];
      const cell = document.createElement('div');
      cell.className   = 'roulette-cell';
      cell.textContent = (p.DisplayName || p.Username || '').slice(0, 14);
      cell.style.background = SLICE_COLORS[i % SLICE_COLORS.length];
      this._strip.appendChild(cell);
    }

    // Store the winner's absolute cell index on the strip element.
    this._strip._winnerCellIndex = totalCells - 1;
    // Reset to start position before the first animation frame.
    this._strip.style.transform = 'translateX(0)';
  },

  async _spin(durationMs) {
    return new Promise(resolve => {
      const winnerIdx    = this._strip._winnerCellIndex;
      const cellCenter   = (winnerIdx + 0.5) * CELL_WIDTH;
      const viewportCenter = VIEWPORT_WIDTH / 2;
      // Add small random jitter (±20 % of cell width) so the landing
      // looks natural and not pixel-perfect every time.
      const jitter  = (Math.random() - 0.5) * CELL_WIDTH * 0.4;
      const targetX = -(cellCenter - viewportCenter + jitter);

      const start     = performance.now();
      let   lastIdx   = -1;

      const frame = (now) => {
        const t      = Math.min(1, (now - start) / durationMs);
        const eased  = 1 - Math.pow(1 - t, 3);   // cubic ease-out (same as wheel/slot)
        const x      = targetX * eased;
        this._strip.style.transform = `translateX(${x}px)`;

        // Which cell is currently centred under the marker?
        const centerCellIdx = Math.floor((-x + viewportCenter) / CELL_WIDTH);
        if (centerCellIdx !== lastIdx) {
          const cell = this._strip.children[centerCellIdx];
          if (cell) this._name.textContent = cell.textContent;
          lastIdx = centerCellIdx;
        }

        if (t < 1) {
          this._animationFrame = requestAnimationFrame(frame);
        } else {
          // Snap to exact targetX and highlight the winner cell.
          this._strip.style.transform = `translateX(${targetX}px)`;
          const winnerCell = this._strip.children[winnerIdx];
          if (winnerCell) {
            // Always display the true winner name regardless of jitter offset.
            this._name.textContent = winnerCell.textContent;
            winnerCell.classList.add('center-cell');
          }
          this._strip.classList.add('landed');
          resolve();
        }
      };

      this._animationFrame = requestAnimationFrame(frame);
    });
  }
};
