// animations/falling-names/index.js — Tetris-style gravity name shower.
// Participant cards spawn from the top of the stage, fall under gravity
// (CSS cubic-bezier deceleration), land at the ground line with a small
// bounce, then slide off sideways with rotation. The winner's card falls
// in slow-motion (2× slower) and stays centered with a gold pulse glow.

const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

export default {
  id: 'falling-names',
  name: 'Düşen İsimler',
  description: 'Tetris gibi yukarıdan düşen isimler — sonuncu kalan kazanır',
  category: 'eğlenceli',
  thumbnail: './thumbnail.svg',

  _container: null, _audio: null, _root: null,
  _stage: null, _spawned: null, _winnerEl: null, _name: null,
  _activeTimers: [],

  async init(container, audio) {
    this._container = container;
    this._audio = audio;
    container.innerHTML = `
      <div class="falling-plugin hidden">
        <div class="falling-stage">
          <div class="falling-spawned"></div>
          <div class="falling-ground"></div>
          <div class="falling-winner"></div>
        </div>
        <div class="falling-name"></div>
      </div>`;
    this._root     = container.querySelector('.falling-plugin');
    this._stage    = container.querySelector('.falling-stage');
    this._spawned  = container.querySelector('.falling-spawned');
    this._winnerEl = container.querySelector('.falling-winner');
    this._name     = container.querySelector('.falling-name');
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
      this._clearStage();
      const isFirst = i === 0;
      await this._fallPhase(pool, isFirst ? 4500 : 2800);
      await this._winnerPhase(winners[i], isFirst ? 1500 : 800);
      await new Promise(r => setTimeout(r, 900));
    }
  },

  reset() {
    for (const t of this._activeTimers) clearTimeout(t);
    this._activeTimers = [];
    if (this._root)      this._root.classList.add('hidden');
    if (this._audio)     this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  _show() { this._root.classList.remove('hidden'); },

  _clearStage() {
    if (this._spawned) this._spawned.innerHTML = '';
    if (this._winnerEl) {
      this._winnerEl.innerHTML = '';
      this._winnerEl.classList.remove('landed');
    }
    if (this._name) this._name.textContent = '';
  },

  // ── Phase A — fall ───────────────────────────────────────────────────────────

  async _fallPhase(pool, durationMs) {
    return new Promise(resolve => {
      // Stagger spawns evenly across the spawn window.
      // Leave the last 1500ms so the final card can land + slide off before
      // the phase resolves.
      const spawnWindow = Math.max(1000, durationMs - 1500);
      const spawnCount  = Math.max(8, Math.min(pool.length, 24));
      const stagger     = Math.floor(spawnWindow / spawnCount);

      let spawned = 0;

      const spawnNext = () => {
        if (spawned >= spawnCount) {
          // Allow last falling card to land + slide off before resolving.
          this._activeTimers.push(setTimeout(resolve, 1500));
          return;
        }

        const p    = pool[spawned % pool.length];
        const card = this._spawnCard(p);
        this._spawned.appendChild(card);

        // Schedule slide-off: ~600ms after the card lands (~1500ms drop → land at 600ms extra).
        this._activeTimers.push(setTimeout(() => {
          card.classList.add('slide-off');
          this._activeTimers.push(setTimeout(() => card.remove(), 1000));
        }, 2100));

        this._name.textContent = (p.DisplayName || p.Username || '');
        spawned++;
        this._activeTimers.push(setTimeout(spawnNext, stagger));
      };

      spawnNext();
    });
  },

  _spawnCard(p) {
    const card       = document.createElement('div');
    card.className   = 'falling-card';
    card.textContent = (p.DisplayName || p.Username || '').slice(0, 14);
    card.style.background = SLICE_COLORS[Math.floor(Math.random() * SLICE_COLORS.length)];
    // Random horizontal position, kept within 10%–90% so cards stay visible.
    card.style.left = (10 + Math.random() * 80) + '%';
    // Per-card CSS custom properties for unique slide-off and rotation.
    card.style.setProperty('--slide-dir', Math.random() > 0.5 ? '1' : '-1');
    card.style.setProperty('--rot', (Math.random() - 0.5) * 30 + 'deg');
    return card;
  },

  // ── Phase B — winner reveal ──────────────────────────────────────────────────

  async _winnerPhase(winner, durationMs) {
    return new Promise(resolve => {
      const card       = document.createElement('div');
      card.className   = 'falling-winner-card';
      card.textContent = (winner.DisplayName || winner.Username || '');
      card.style.background = '#fef3c7';
      card.style.color      = '#1a1a1a';
      this._winnerEl.appendChild(card);

      // After the slow drop animation finishes, add 'landed' for the pulse glow.
      this._activeTimers.push(setTimeout(() => {
        this._winnerEl.classList.add('landed');
        this._name.textContent = winner.DisplayName || winner.Username || '';
      }, 1500));

      this._activeTimers.push(setTimeout(resolve, 1500 + durationMs));
    });
  }
};
