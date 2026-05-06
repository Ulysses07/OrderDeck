// animations/spotlight-grid/index.js — calm grid layout with a decelerating
// spotlight that hops from cell to cell and lands on the winner.
// Ease-out cubic on hop intervals (60ms → 440ms) mirrors wheel/slot-machine.

export default {
  id: 'spotlight-grid',
  name: 'Spot Işığı',
  description: 'Adların grid\'inde zıplayan ışık',
  category: 'minimal',
  thumbnail: './thumbnail.svg',

  // Internals (set by init)
  _container: null, _audio: null, _synth: null, _root: null,
  _grid: null, _name: null,
  _activeTimers: [],
  _pool: null,

  async init(container, audio, synth) {
    this._container = container;
    this._audio = audio;
    this._synth = synth || null;
    container.innerHTML = `
      <div class="spotlight-plugin hidden">
        <div class="spotlight-grid"></div>
        <div class="spotlight-name"></div>
      </div>`;
    this._root = container.querySelector('.spotlight-plugin');
    this._grid = container.querySelector('.spotlight-grid');
    this._name = container.querySelector('.spotlight-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }
    this._show();
    this._pool = pool;
    this._buildGrid(pool);

    for (let i = 0; i < winners.length; i++) {
      const winner = winners[i];
      // WYSIWYG: pool tail holds winners in order (server contract).
      let winnerIdx = pool.length - winners.length + i;
      if (winnerIdx < 0 || winnerIdx >= pool.length) winnerIdx = pool.length - 1;
      // Cap to visible: if winner not in visible window, fall back to last visible cell.
      const visible = Math.min(pool.length, 24);
      if (winnerIdx >= visible) winnerIdx = visible - 1;

      const dur = i === 0 ? 4500 : 2800;
      await this._hopTo(winnerIdx, dur);
      await new Promise(r => setTimeout(r, 900));
      // Mark this cell as won so the next hop sequence styles it differently.
      const cells = this._grid.children;
      cells[winnerIdx].classList.remove('lit');
      cells[winnerIdx].classList.add('won');
    }
  },

  reset() {
    for (const t of this._activeTimers) clearTimeout(t);
    this._activeTimers = [];
    if (this._root) this._root.classList.add('hidden');
    if (this._audio) this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  _show() { this._root.classList.remove('hidden'); },

  _buildGrid(pool) {
    this._grid.innerHTML = '';
    const visible = Math.min(pool.length, 24);
    for (let i = 0; i < visible; i++) {
      const cell = document.createElement('div');
      cell.className = 'spotlight-cell';
      cell.textContent = (pool[i].DisplayName || pool[i].Username || '').slice(0, 14);
      this._grid.appendChild(cell);
    }
  },

  // Animate the light hopping from random cell to random cell, decelerating
  // (cubic ease-out on the interval between hops), landing on winnerIdx.
  async _hopTo(winnerIdx, durationMs) {
    return new Promise(resolve => {
      const cells = this._grid.children;
      let lastIdx = -1;

      // Compute hop schedule: total hops based on durationMs / mean interval (~200ms).
      // Use ease-out: hop intervals start short, get longer.
      const totalHops = Math.max(8, Math.floor(durationMs / 200));
      const intervals = [];
      for (let h = 0; h < totalHops; h++) {
        const t = (h + 1) / totalHops;
        // Cubic ease-out: durations grow non-linearly toward the end.
        const eased = 1 - Math.pow(1 - t, 3);
        intervals.push(60 + Math.floor(eased * 380));  // 60ms → 440ms
      }

      const doHop = (hopIdx) => {
        // Clear previous lit cell.
        if (lastIdx >= 0) cells[lastIdx]?.classList.remove('lit');

        let nextIdx;
        if (hopIdx === intervals.length - 1) {
          nextIdx = winnerIdx;  // Final hop lands on winner.
        } else {
          // Random walk that avoids same-cell-twice.
          do {
            nextIdx = Math.floor(Math.random() * cells.length);
          } while (nextIdx === lastIdx && cells.length > 1);
        }

        cells[nextIdx].classList.add('lit');
        this._name.textContent = (this._pool[nextIdx].DisplayName ||
                                  this._pool[nextIdx].Username || '');
        lastIdx = nextIdx;
        if (this._synth) this._synth.tick(900 + (Math.random() - 0.5) * 200);

        if (hopIdx < intervals.length - 1) {
          this._activeTimers.push(setTimeout(
            () => doHop(hopIdx + 1), intervals[hopIdx]));
        } else {
          // Final landing: resolve after a short beat.
          if (this._synth) {
            this._synth.ding(1320);
            this._activeTimers.push(setTimeout(() => { if (this._synth) this._synth.fanfare(); }, 200));
          }
          this._activeTimers.push(setTimeout(resolve, 200));
        }
      };
      doHop(0);
    });
  }
};
