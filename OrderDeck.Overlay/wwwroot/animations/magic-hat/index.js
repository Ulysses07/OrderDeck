// animations/magic-hat/index.js — whimsical magic-show plugin.
// DOM/CSS-based: participant names fly in as coloured cards, swirl above a
// black top hat, then shrink-fade into it. A golden wand swings down, taps
// the hat (jiggle), and the winner's name rises out with radial sparkles.

const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

export default {
  id: 'magic-hat',
  name: 'Sihirli Şapka',
  description: 'Şapkadan kazananın çekildiği büyücü gösterisi',
  category: 'eğlenceli',
  thumbnail: './thumbnail.svg',

  _container: null, _audio: null, _synth: null, _root: null,
  _stage: null, _flyingNames: null, _winner: null,
  _sparkles: null, _name: null, _wand: null,
  _activeTimers: [],

  async init(container, audio, synth) {
    this._container = container;
    this._audio = audio;
    this._synth = synth || null;
    container.innerHTML = `
      <div class="hat-plugin hidden">
        <div class="hat-stage">
          <div class="hat-wand"></div>
          <div class="hat-shape">
            <div class="hat-brim"></div>
            <div class="hat-body"></div>
            <div class="hat-band"></div>
          </div>
          <div class="hat-flying-names"></div>
          <div class="hat-winner"></div>
          <div class="hat-sparkles"></div>
        </div>
        <div class="hat-name"></div>
      </div>`;
    this._root        = container.querySelector('.hat-plugin');
    this._stage       = container.querySelector('.hat-stage');
    this._flyingNames = container.querySelector('.hat-flying-names');
    this._winner      = container.querySelector('.hat-winner');
    this._sparkles    = container.querySelector('.hat-sparkles');
    this._name        = container.querySelector('.hat-name');
    this._wand        = container.querySelector('.hat-wand');
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
      const isFirst = i === 0;
      this._clearStage();
      await this._absorbPhase(pool, isFirst ? 3000 : 1500);
      await this._wandTapPhase();
      await this._revealPhase(winners[i], isFirst ? 1200 : 800);
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
    if (this._flyingNames) this._flyingNames.innerHTML = '';
    if (this._sparkles)    this._sparkles.innerHTML = '';
    if (this._winner) {
      this._winner.innerHTML = '';
      this._winner.classList.remove('rising');
    }
    if (this._wand)  this._wand.classList.remove('tapping');
    if (this._stage) this._stage.classList.remove('shaken');
    if (this._name)  this._name.textContent = '';
  },

  // ── Phase A ─────────────────────────────────────────────────────────────────

  /** Spawn participant cards that fly toward the hat and disappear into it. */
  async _absorbPhase(pool, durationMs) {
    const visibleCount = Math.min(pool.length, 12);
    const visible      = pool.slice(0, visibleCount);
    const stagger      = Math.max(80, Math.floor((durationMs - 1200) / Math.max(1, visibleCount)));

    return new Promise(resolve => {
      let spawned = 0;

      const spawnNext = () => {
        if (spawned >= visible.length) {
          // Allow last card's transition to finish.
          this._activeTimers.push(setTimeout(resolve, 900));
          return;
        }
        const p    = visible[spawned++];
        const card = this._spawnFlyingCard(p);
        this._flyingNames.appendChild(card);
        if (this._synth) this._synth.whoosh({ from: 800 + Math.random() * 400, to: 200, durationMs: 250 });

        // Trigger the absorb transition on next frame.
        requestAnimationFrame(() => card.classList.add('absorbing'));

        // Remove from DOM after transition.
        this._activeTimers.push(setTimeout(() => card.remove(), 1300));
        this._activeTimers.push(setTimeout(spawnNext, stagger));
      };

      spawnNext();
    });
  },

  _spawnFlyingCard(p) {
    const card       = document.createElement('div');
    card.className   = 'hat-flying-card';
    card.textContent = (p.DisplayName || p.Username || '').slice(0, 14);
    card.style.background = SLICE_COLORS[Math.floor(Math.random() * SLICE_COLORS.length)];

    // Random start position outside the hat, in stage coords (percent).
    const edge = Math.floor(Math.random() * 3);  // top / left / right
    const r    = () => Math.floor(10 + Math.random() * 80) + '%';

    if      (edge === 0) { card.style.left = r();    card.style.top = '-10%'; }
    else if (edge === 1) { card.style.left = '-10%'; card.style.top = r();   }
    else                 { card.style.left = '110%'; card.style.top = r();   }

    return card;
  },

  // ── Phase B ─────────────────────────────────────────────────────────────────

  async _wandTapPhase() {
    return new Promise(resolve => {
      this._wand.classList.add('tapping');
      if (this._synth) this._synth.tick(1200);
      this._activeTimers.push(setTimeout(() => {
        this._stage.classList.add('shaken');
        if (this._synth) this._synth.kick();
        this._activeTimers.push(setTimeout(resolve, 300));
      }, 200));
    });
  },

  // ── Phase C ─────────────────────────────────────────────────────────────────

  async _revealPhase(winner, durationMs) {
    return new Promise(resolve => {
      this._winner.textContent = winner.DisplayName || winner.Username || '';
      requestAnimationFrame(() => this._winner.classList.add('rising'));
      this._spawnSparkles(20);
      this._name.textContent = winner.DisplayName || winner.Username || '';
      if (this._synth) {
        this._synth.ding(1500);
        this._activeTimers.push(setTimeout(() => { if (this._synth) this._synth.fanfare(); }, 200));
      }
      this._activeTimers.push(setTimeout(resolve, durationMs));
    });
  },

  _spawnSparkles(count) {
    for (let i = 0; i < count; i++) {
      const s       = document.createElement('span');
      s.className   = 'hat-sparkle';

      // Spread radially around the hat top.
      const angle    = (i / count) * Math.PI * 2;
      const distance = 80 + Math.random() * 60;
      s.style.setProperty('--dx', Math.cos(angle) * distance + 'px');
      s.style.setProperty('--dy', Math.sin(angle) * distance + 'px');
      s.style.left       = '50%';
      s.style.top        = '40%';
      s.style.background = `hsl(${Math.random() * 60 + 30}, 90%, 60%)`;

      this._sparkles.appendChild(s);
      this._activeTimers.push(setTimeout(() => s.remove(), 1200));
    }
  }
};
