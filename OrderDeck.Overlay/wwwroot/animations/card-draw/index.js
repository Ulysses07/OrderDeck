// animations/card-draw/index.js — elegant deck-draw plugin.
// DOM/CSS-based: a 5-card stacked deck shuffles in place, the top card
// peels off horizontally to a stage, then flips 180° on Y to reveal the
// winner's name on a cream card face with gold star decorations.
// Cubic ease-out matches wheel/slot-machine/bingo for cross-animation
// visual consistency.

export default {
  id: 'card-draw',
  name: 'Kart Çekimi',
  description: 'Şıklı kart destesinden çekim',
  category: 'elegant',
  thumbnail: './thumbnail.svg',

  // Internals (set by init)
  _container: null,
  _audio: null,
  _synth: null,
  _root: null,
  _deck: null,
  _stage: null,
  _name: null,
  _animationFrame: null,

  // ── Lifecycle ─────────────────────────────────────────────────────────────

  async init(container, audio, synth) {
    this._container = container;
    this._audio = audio;  // reserved for Phase-2 audio packs; no play() yet
    this._synth = synth || null;

    container.innerHTML = `
      <div class="card-plugin hidden">
        <div class="card-deck"></div>
        <div class="card-stage"></div>
        <div class="card-name"></div>
      </div>`;

    this._root  = container.querySelector('.card-plugin');
    this._deck  = container.querySelector('.card-deck');
    this._stage = container.querySelector('.card-stage');
    this._name  = container.querySelector('.card-name');
    this._refillDeck(5);
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
      const winner  = winners[i];
      const isFirst = i === 0;

      // Top up the visible deck if it shrunk to zero.
      if (this._deck.children.length === 0) this._refillDeck(1);

      await this._shufflePhase(isFirst ? 1500 : 800);
      await this._peelTopCard();
      await this._flipPhase(winner, isFirst ? 1200 : 800);

      this._name.textContent = winner.DisplayName || winner.Username || '';
      this._root.classList.add('landed');

      await new Promise(r => setTimeout(r, 900));

      if (i < winners.length - 1) {
        await this._exitCard();
        this._root.classList.remove('landed');
        // Visually deplete the deck (remove one card-back) but never go below 1.
        if (this._deck.children.length > 1) this._deck.lastElementChild.remove();
      }
    }
  },

  reset() {
    if (this._animationFrame) {
      cancelAnimationFrame(this._animationFrame);
      this._animationFrame = null;
    }
    if (this._root)      this._root.classList.add('hidden');
    if (this._audio)     this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  // ── Private ───────────────────────────────────────────────────────────────

  _show() { this._root.classList.remove('hidden'); },

  /** Rebuild the deck with `count` stacked card-back divs. */
  _refillDeck(count) {
    this._deck.innerHTML = '';
    for (let i = 0; i < count; i++) {
      const back = document.createElement('div');
      back.className = 'card card-back';
      // Slight Y stagger so the deck looks 3D-stacked from the side.
      back.style.transform = `translateY(${-i * 3}px)`;
      back.style.zIndex    = String(count - i);
      this._deck.appendChild(back);
    }
  },

  /**
   * Phase A — apply the CSS `card-shuffle` keyframe to the deck for
   * `durationMs`, then remove it.
   */
  async _shufflePhase(durationMs) {
    this._deck.classList.add('shuffling');
    if (this._synth) this._synth.flip();
    await new Promise(r => setTimeout(r, durationMs));
    this._deck.classList.remove('shuffling');
  },

  /**
   * Peel the top card off the deck: CSS transition moves it to the right,
   * then we detach it from the deck and attach it to the stage as the
   * flip host (with both face divs already inserted).
   */
  _peelTopCard() {
    return new Promise(resolve => {
      const top = this._deck.firstElementChild;
      if (!top) { resolve(); return; }

      top.classList.add('peeling');

      // Defensive: if `transitionend` doesn't fire (e.g. the host's
      // stylesheet hadn't loaded so `peeling` doesn't change `transform`,
      // or the browser optimised the no-op transition out), the Promise
      // would otherwise hang forever and the runFor() chain stalls. Cap
      // the wait at the CSS transition duration (650ms) + a small safety
      // margin and force-resolve.
      let resolved = false;
      const finish = () => {
        if (resolved) return;
        resolved = true;
        top.removeEventListener('transitionend', onEnd);

        // Rebuild the element as the flip host.
        top.classList.remove('peeling', 'card-back');
        top.classList.add('card-flip-host');
        top.style.transform = '';   // reset inline transform from the deck stagger
        top.style.zIndex    = '';

        top.innerHTML = `
          <div class="card-face card-face-back"></div>
          <div class="card-face card-face-front">
            <div class="card-front-content">
              <span class="card-deco">★</span>
              <span class="card-front-name"></span>
              <span class="card-deco">★</span>
            </div>
          </div>`;

        this._stage.innerHTML = '';
        this._stage.appendChild(top);
        resolve();
      };

      const onEnd = (e) => {
        // transitionend fires once per property; guard against multiple fires.
        if (e.propertyName !== 'transform') return;
        finish();
      };

      top.addEventListener('transitionend', onEnd);
      setTimeout(finish, 750);
    });
  },

  /**
   * Phase B — rAF loop that rotates the card from 0→180° using cubic
   * ease-out (same formula as wheel/slot-machine/bingo).  Once complete,
   * adds the `.flipped` class so CSS `backface-visibility` ensures the
   * front face is fully visible.
   */
  _flipPhase(winner, durationMs) {
    return new Promise(resolve => {
      const card  = this._stage.firstElementChild;
      const front = card.querySelector('.card-front-name');
      front.textContent = winner.DisplayName || winner.Username || '';

      const start = performance.now();

      const frame = (now) => {
        const t      = Math.min(1, (now - start) / durationMs);
        const eased  = 1 - Math.pow(1 - t, 3);   // cubic ease-out
        const angle  = eased * 180;
        card.style.transform = `rotateY(${angle}deg)`;

        if (t < 1) {
          this._animationFrame = requestAnimationFrame(frame);
        } else {
          this._animationFrame = null;
          card.style.transform = 'rotateY(180deg)';
          card.classList.add('flipped');
          if (this._synth) {
            this._synth.flip();
            setTimeout(() => { if (this._synth) this._synth.fanfare(); }, 600);
          }
          resolve();
        }
      };

      this._animationFrame = requestAnimationFrame(frame);
    });
  },

  /**
   * Exit transition: slide the revealed card downward and fade it out,
   * then remove it from the stage DOM.
   */
  _exitCard() {
    return new Promise(resolve => {
      const card = this._stage.firstElementChild;
      if (!card) { resolve(); return; }

      card.classList.add('exiting');

      const onEnd = (e) => {
        if (e.propertyName !== 'opacity') return;
        card.removeEventListener('transitionend', onEnd);
        this._stage.innerHTML = '';
        resolve();
      };

      card.addEventListener('transitionend', onEnd);
    });
  }
};
