// animations/wheel/index.js — original spinning wheel, refactored as the
// first plugin under the new pluggable host. Behaviour identical to
// pre-refactor giveaway.js (see git history before commit XXX).

const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

export default {
  id: 'wheel',
  name: 'Çark',
  description: 'Klasik dönen çark animasyonu',
  category: 'klasik',
  thumbnail: './thumbnail.svg',

  // Internals (set by init)
  _container: null,
  _audio: null,
  _canvas: null,
  _name: null,
  _root: null,

  async init(container, audio) {
    this._container = container;
    this._audio = audio;
    container.innerHTML = `
      <div class="wheel-plugin hidden">
        <div class="wheel-arrow"></div>
        <canvas class="wheel-canvas" width="520" height="520"></canvas>
        <div class="wheel-name"></div>
      </div>`;
    this._root = container.querySelector('.wheel-plugin');
    this._canvas = container.querySelector('.wheel-canvas');
    this._name = container.querySelector('.wheel-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }

    this._show();
    this._draw(pool, 0);

    for (let i = 0; i < winners.length; i++) {
      const w = winners[i];
      let idx = pool.findIndex(p =>
        p.Username === w.Username && p.Platform === w.Platform);
      if (idx < 0) idx = 0;

      this._root.classList.remove('landed');
      const dur = i === 0 ? 4500 : 2800;
      await this._spin(pool, idx, dur);
      await new Promise(r => setTimeout(r, 900));
    }
  },

  reset() {
    if (this._root) this._root.classList.add('hidden');
    if (this._audio) this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  _show() { this._root.classList.remove('hidden'); },

  _draw(participants, rotation) {
    const ctx = this._canvas.getContext('2d');
    const W = this._canvas.width;
    const cx = W / 2, cy = W / 2;
    const outerR = W / 2 - 8;
    const innerR = 36;

    ctx.clearRect(0, 0, W, W);
    if (participants.length === 0) return;

    const slice = (Math.PI * 2) / participants.length;

    for (let i = 0; i < participants.length; i++) {
      const start = rotation + i * slice - Math.PI / 2;
      const end = start + slice;
      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.arc(cx, cy, outerR, start, end);
      ctx.closePath();
      ctx.fillStyle = SLICE_COLORS[i % SLICE_COLORS.length];
      ctx.fill();
      ctx.strokeStyle = 'rgba(0,0,0,0.25)';
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    const labelArcMin = 14 * Math.PI / 180;
    if (slice >= labelArcMin) {
      ctx.font = 'bold 16px "Segoe UI", system-ui, sans-serif';
      ctx.fillStyle = '#fff';
      ctx.textAlign = 'right';
      ctx.textBaseline = 'middle';
      for (let i = 0; i < participants.length; i++) {
        const angle = rotation + i * slice + slice / 2 - Math.PI / 2;
        ctx.save();
        ctx.translate(cx, cy);
        ctx.rotate(angle);
        const text = (participants[i].DisplayName || participants[i].Username || '').slice(0, 14);
        ctx.shadowColor = 'rgba(0,0,0,0.65)';
        ctx.shadowBlur = 4;
        ctx.fillText(text, outerR - 14, 0);
        ctx.restore();
      }
    }

    ctx.beginPath();
    ctx.arc(cx, cy, innerR, 0, Math.PI * 2);
    ctx.fillStyle = '#0f1118';
    ctx.fill();
    ctx.strokeStyle = '#ffce46';
    ctx.lineWidth = 3;
    ctx.stroke();

    ctx.font = 'bold 22px "Segoe UI", system-ui, sans-serif';
    ctx.fillStyle = '#ffce46';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.shadowColor = 'transparent';
    ctx.fillText('🎁', cx, cy);
  },

  _targetRotation(participantCount, winnerIndex, extraTurns) {
    const slice = (Math.PI * 2) / participantCount;
    const jitter = (Math.random() - 0.5) * slice * 0.7;
    const baseAngle = -winnerIndex * slice - slice / 2 + jitter;
    return baseAngle + extraTurns * Math.PI * 2;
  },

  _spin(pool, winnerIndex, durationMs) {
    return new Promise(resolve => {
      const target = this._targetRotation(
        pool.length, winnerIndex, 5 + Math.floor(Math.random() * 3));
      const start = performance.now();
      let lastHighlightIdx = -1;

      const frame = (now) => {
        const t = Math.min(1, (now - start) / durationMs);
        const eased = 1 - Math.pow(1 - t, 3);
        const rotation = target * eased;

        this._draw(pool, rotation);

        const slice = (Math.PI * 2) / pool.length;
        const normalised = ((-rotation - slice / 2) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2);
        const idx = Math.floor(normalised / slice) % pool.length;
        if (idx !== lastHighlightIdx) {
          const p = pool[idx];
          this._name.textContent = p.DisplayName || p.Username || '';
          lastHighlightIdx = idx;
        }

        if (t < 1) {
          requestAnimationFrame(frame);
        } else {
          this._name.textContent =
            pool[winnerIndex].DisplayName || pool[winnerIndex].Username || '';
          this._root.classList.add('landed');
          resolve();
        }
      };
      requestAnimationFrame(frame);
    });
  }
};
