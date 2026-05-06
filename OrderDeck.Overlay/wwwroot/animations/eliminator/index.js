// animations/eliminator/index.js — highest-drama last-one-standing round.
// All participants shown in a grid; names flash red, shake, and drop one by
// one. Elimination interval ramps 220ms → 1320ms (cubic ease-out) so tension
// peaks at the final two. Last cell standing glows gold — that's the winner.

export default {
  id: 'eliminator',
  name: 'Eleme',
  description: 'Adlar tek tek elenerek son kazanan belirlenir',
  category: 'dramatik',
  thumbnail: './thumbnail.svg',

  _container: null, _audio: null, _synth: null, _root: null,
  _grid: null, _status: null, _name: null,
  _activeTimers: [],

  async init(container, audio, synth) {
    this._container = container;
    this._audio = audio;
    this._synth = synth || null;
    container.innerHTML = `
      <div class="elim-plugin hidden">
        <div class="elim-grid"></div>
        <div class="elim-status"></div>
        <div class="elim-name"></div>
      </div>`;
    this._root = container.querySelector('.elim-plugin');
    this._grid = container.querySelector('.elim-grid');
    this._status = container.querySelector('.elim-status');
    this._name = container.querySelector('.elim-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }
    this._show();

    const wonIds = new Set();  // pool indices of past winners (stay gold)

    for (let i = 0; i < winners.length; i++) {
      const winner = winners[i];
      let winnerIdx = pool.findIndex(p =>
        p.Username === winner.Username && p.Platform === winner.Platform);
      if (winnerIdx < 0) winnerIdx = 0;

      // Cap visible to 30 for layout sanity.
      const visible = Math.min(pool.length, 30);
      if (winnerIdx >= visible) winnerIdx = visible - 1;

      this._buildGrid(pool, visible, wonIds);

      // Collect all cells to eliminate: skip winner and past champions.
      const targets = [];
      for (let idx = 0; idx < visible; idx++) {
        if (idx === winnerIdx) continue;
        if (wonIds.has(idx)) continue;
        targets.push(idx);
      }

      // Shuffle for dramatic, non-sequential elimination order.
      for (let k = targets.length - 1; k > 0; k--) {
        const j = Math.floor(Math.random() * (k + 1));
        [targets[k], targets[j]] = [targets[j], targets[k]];
      }

      await this._eliminatePhase(pool, targets, visible - wonIds.size);
      await this._revealPhase(winner, winnerIdx, i === 0 ? 1500 : 800);
      wonIds.add(winnerIdx);
      await new Promise(r => setTimeout(r, 900));
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

  _buildGrid(pool, visible, wonIds) {
    this._grid.innerHTML = '';
    for (let i = 0; i < visible; i++) {
      const cell = document.createElement('div');
      cell.className = 'elim-cell';
      cell.dataset.idx = String(i);
      cell.textContent = (pool[i].DisplayName || pool[i].Username || '').slice(0, 14);
      if (wonIds.has(i)) cell.classList.add('champion');
      this._grid.appendChild(cell);
    }
    this._status.textContent = `${visible - wonIds.size} kalan`;
  },

  async _eliminatePhase(pool, targets, totalActive) {
    return new Promise(resolve => {
      let idx = 0;
      const total = targets.length;

      // Cubic ease-out interval growth: fast at start → slow at end.
      // Range: 220ms (first) → 1320ms (last).
      const intervalAt = (t) => {
        const eased = 1 - Math.pow(1 - t, 3);
        return 220 + Math.floor(eased * 1100);  // 220ms → 1320ms
      };

      const eliminateNext = () => {
        if (idx >= total) {
          if (this._synth) this._synth.roll(600);
          resolve();
          return;
        }
        const target = targets[idx];
        const cell = this._grid.querySelector(`[data-idx="${target}"]`);
        if (cell) {
          cell.classList.add('eliminating');
          this._name.textContent = (pool[target].DisplayName || pool[target].Username || '');
          if (this._synth) this._synth.kick();
          // Drop the cell after the flash animation completes.
          this._activeTimers.push(setTimeout(() => {
            cell.classList.add('dropped');
            const remaining = totalActive - (idx + 1);
            this._status.textContent = `${remaining} kalan`;
          }, 350));
        }
        idx++;
        const t = idx / total;
        this._activeTimers.push(setTimeout(eliminateNext, intervalAt(t)));
      };
      eliminateNext();
    });
  },

  async _revealPhase(winner, winnerIdx, durationMs) {
    return new Promise(resolve => {
      const cell = this._grid.querySelector(`[data-idx="${winnerIdx}"]`);
      if (cell) cell.classList.add('winner');
      this._name.textContent = winner.DisplayName || winner.Username || '';
      this._status.textContent = 'KAZANAN!';
      if (this._synth) this._synth.fanfare();
      this._activeTimers.push(setTimeout(resolve, durationMs));
    });
  }
};
