// animations/bingo/index.js — lottery hopper with physics-driven balls.
// Canvas-based (similar to wheel). N balls (one per pool member, capped at 25)
// bounce inside a circular hopper with simple gravity + elastic-wall physics,
// then the winner's ball separates from the swarm and lands in a display zone.

const SLICE_COLORS = [
  '#ef4444', '#f97316', '#f59e0b', '#eab308',
  '#84cc16', '#22c55e', '#10b981', '#14b8a6',
  '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
  '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
];

// ── Geometry constants ────────────────────────────────────────────────────────
const CW          = 520;   // canvas width (CSS halves this for retina sharpness)
const CH          = 520;   // canvas height
const HCX         = 260;   // hopper center X
const HCY         = 220;   // hopper center Y (slightly above canvas center so
                            // the winner zone fits below)
const HOPPER_R    = 180;   // inner bounce radius (drawn border is 200)
const HOPPER_VIS  = 200;   // visible hopper border radius
const BALL_R      = 14;    // ball radius in canvas pixels
const WINNER_X    = 260;   // winner zone center X
const WINNER_Y    = 455;   // winner zone center Y
const MAX_BALLS   = 25;    // visual cap

export default {
  id: 'bingo',
  name: 'Tombala',
  description: 'Bingo top makinesi tarzı çekiliş',
  category: 'klasik',
  thumbnail: './thumbnail.svg',

  // Internals (set by init)
  _container:      null,
  _audio:          null,
  _synth:          null,
  _canvas:         null,
  _ctx:            null,
  _name:           null,
  _root:           null,
  _balls:          null,   // array of ball state objects
  _animationFrame: null,   // current rAF id so reset() can cancel
  _lastPopAt:      0,      // throttle for pop sounds

  // ── Lifecycle ────────────────────────────────────────────────────────────

  async init(container, audio, synth) {
    this._container = container;
    this._audio = audio;  // reserved for Phase-2 audio packs; no play() yet
    this._synth = synth || null;

    container.innerHTML = `
      <div class="bingo-plugin hidden">
        <canvas class="bingo-canvas" width="520" height="520"></canvas>
        <div class="bingo-name"></div>
      </div>`;

    this._root   = container.querySelector('.bingo-plugin');
    this._canvas = container.querySelector('.bingo-canvas');
    this._ctx    = this._canvas.getContext('2d');
    this._name   = container.querySelector('.bingo-name');
  },

  async runFor(winners, pool) {
    if (!pool || pool.length === 0) {
      this._name.textContent = 'Henüz katılımcı yok';
      this._show();
      await new Promise(r => setTimeout(r, 5000));
      return;
    }

    this._show();
    this._buildBalls(pool);

    for (let i = 0; i < winners.length; i++) {
      const winner   = winners[i];
      const dur      = i === 0 ? 3000 : 1800;

      // Find the winner's ball by matching pool entry
      const winnerIdx = pool.findIndex(p =>
        p.Username === winner.Username && p.Platform === winner.Platform);
      const ballIdx = this._balls.findIndex(
        b => b._poolIdx === (winnerIdx >= 0 ? winnerIdx : 0));

      this._root.classList.remove('landed');
      await this._chaosPhase(dur);
      await this._dropPhase(ballIdx >= 0 ? ballIdx : 0, 1500);
      this._root.classList.add('landed');
      await new Promise(r => setTimeout(r, 900));
      this._root.classList.remove('landed');

      // Remove the dropped ball from the swarm for subsequent winners
      if (ballIdx >= 0) this._balls.splice(ballIdx, 1);
    }
  },

  reset() {
    if (this._animationFrame) {
      cancelAnimationFrame(this._animationFrame);
      this._animationFrame = null;
    }
    this._balls = null;
    if (this._root)      this._root.classList.add('hidden');
    if (this._audio)     this._audio.disposeAll();
    if (this._container) this._container.innerHTML = '';
  },

  // ── Private: setup ────────────────────────────────────────────────────────

  _show() { this._root.classList.remove('hidden'); },

  /** Spawn one ball per pool member (capped at MAX_BALLS visually). */
  _buildBalls(pool) {
    const cap = Math.min(pool.length, MAX_BALLS);
    this._balls = [];
    for (let i = 0; i < cap; i++) {
      const p     = pool[i];
      const angle = Math.random() * Math.PI * 2;
      const dist  = Math.random() * (HOPPER_R - BALL_R * 2);
      // Random spawn inside hopper
      const x     = HCX + Math.cos(angle) * dist;
      const y     = HCY + Math.sin(angle) * dist;
      // Random initial velocity
      const speed = 3 + Math.random() * 2;
      const dir   = Math.random() * Math.PI * 2;
      const raw   = p.DisplayName || p.Username || '';
      this._balls.push({
        x, y,
        vx:       Math.cos(dir) * speed,
        vy:       Math.sin(dir) * speed,
        r:        BALL_R,
        color:    SLICE_COLORS[i % SLICE_COLORS.length],
        name:     raw,
        displayName: raw.length > 8 ? raw.slice(0, 7) + '…' : raw,
        _poolIdx: i   // index into the original pool array
      });
    }
  },

  // ── Private: physics ─────────────────────────────────────────────────────

  /** Advance physics one tick for all balls. */
  _stepPhysics() {
    for (const b of this._balls) {
      // Gravity pulls toward bottom of hopper
      b.vy += 0.15;
      // Mild friction
      b.vx *= 0.99;
      b.vy *= 0.99;

      b.x += b.vx;
      b.y += b.vy;

      // Elastic wall bounce — confine to HOPPER_R - BALL_R
      const dx  = b.x - HCX;
      const dy  = b.y - HCY;
      const d   = Math.sqrt(dx * dx + dy * dy);
      const max = HOPPER_R - b.r;
      if (d > max) {
        // Normalised radial direction
        const nx = dx / d;
        const ny = dy / d;
        // Project velocity onto normal and reflect
        const dot = b.vx * nx + b.vy * ny;
        b.vx -= 2 * dot * nx;
        b.vy -= 2 * dot * ny;
        // Push ball back inside
        b.x = HCX + nx * max;
        b.y = HCY + ny * max;
        // Throttled pop sound on wall bounce (max 1 per 80ms)
        if (this._synth) {
          const now = performance.now();
          if (now - this._lastPopAt >= 80) {
            this._lastPopAt = now;
            this._synth.pop(400 + Math.random() * 300);
          }
        }
      }
    }
  },

  // ── Private: drawing ─────────────────────────────────────────────────────

  /** Draw one full frame: hopper + all balls (+ optional winner zone). */
  _drawFrame(droppingIdx, dropProgress, droppedX, droppedY, droppedScale) {
    const ctx = this._ctx;
    ctx.clearRect(0, 0, CW, CH);

    // Hopper background fill
    ctx.beginPath();
    ctx.arc(HCX, HCY, HOPPER_VIS, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(15,17,24,0.85)';
    ctx.fill();

    // Hopper border (gold ring)
    ctx.beginPath();
    ctx.arc(HCX, HCY, HOPPER_VIS, 0, Math.PI * 2);
    ctx.strokeStyle = '#ffce46';
    ctx.lineWidth = 3;
    ctx.stroke();

    // Exit chute triangle at bottom of hopper
    const notchW = 18;
    const notchH = 16;
    const notchY = HCY + HOPPER_VIS;
    ctx.beginPath();
    ctx.moveTo(HCX - notchW, notchY - notchH);
    ctx.lineTo(HCX + notchW, notchY - notchH);
    ctx.lineTo(HCX, notchY + notchH);
    ctx.closePath();
    ctx.fillStyle = '#ffce46';
    ctx.fill();

    // Winner display zone (horizontal box below hopper)
    const zoneX = HCX - 200;
    const zoneY = WINNER_Y - 40;
    const zoneW = 400;
    const zoneH = 80;
    ctx.beginPath();
    ctx.roundRect(zoneX, zoneY, zoneW, zoneH, 12);
    ctx.fillStyle = 'rgba(15,17,24,0.7)';
    ctx.fill();
    ctx.strokeStyle = 'rgba(255,206,70,0.45)';
    ctx.lineWidth = 1.5;
    ctx.stroke();

    // Swarm balls (all except the one currently dropping)
    const isDropping = droppingIdx >= 0 && dropProgress > 0;
    for (let i = 0; i < this._balls.length; i++) {
      if (i === droppingIdx) continue;   // drawn separately below
      const b = this._balls[i];
      ctx.globalAlpha = isDropping ? 0.4 : 1;
      this._drawBall(ctx, b.x, b.y, b.r, b.color, b.displayName);
    }
    ctx.globalAlpha = 1;

    // The dropping ball (drawn last so it renders on top)
    if (droppingIdx >= 0 && droppingIdx < this._balls.length) {
      const b     = this._balls[droppingIdx];
      const scale = 1 + (droppedScale - 1) * dropProgress;
      const r     = b.r * scale;
      this._drawBall(ctx, droppedX, droppedY, r, b.color, b.displayName);
    }
  },

  /** Draw a single ball with name label. */
  _drawBall(ctx, x, y, r, color, label) {
    // Filled circle
    ctx.beginPath();
    ctx.arc(x, y, r, 0, Math.PI * 2);
    ctx.fillStyle = color;
    ctx.fill();
    // Subtle inner highlight
    ctx.beginPath();
    ctx.arc(x - r * 0.25, y - r * 0.3, r * 0.35, 0, Math.PI * 2);
    ctx.fillStyle = 'rgba(255,255,255,0.25)';
    ctx.fill();
    // Name text
    ctx.font = 'bold 11px "Segoe UI", system-ui, sans-serif';
    ctx.textAlign = 'center';
    ctx.textBaseline = 'middle';
    ctx.shadowColor = '#000';
    ctx.shadowBlur = 2;
    ctx.fillStyle = '#fff';
    ctx.fillText(label, x, y);
    ctx.shadowBlur = 0;
  },

  // ── Private: phases ───────────────────────────────────────────────────────

  /**
   * Phase A — balls bounce chaotically inside the hopper.
   * .bingo-name shows whichever ball is nearest the ejection chute
   * (bottom-center of hopper).
   */
  _chaosPhase(durationMs) {
    return new Promise(resolve => {
      const start = performance.now();
      let lastNameBall = null;

      const frame = (now) => {
        this._stepPhysics();
        this._drawFrame(-1, 0, HCX, HCY, 1);

        // Nearest ball to the exit chute (bottom of hopper)
        let nearest = null;
        let nearDist = Infinity;
        for (const b of this._balls) {
          const dy = b.y - (HCY + HOPPER_R);
          const dx = b.x - HCX;
          const d  = Math.sqrt(dx * dx + dy * dy);
          if (d < nearDist) { nearDist = d; nearest = b; }
        }
        if (nearest && nearest !== lastNameBall) {
          this._name.textContent = nearest.name;
          lastNameBall = nearest;
        }

        if (now - start < durationMs) {
          this._animationFrame = requestAnimationFrame(frame);
        } else {
          this._animationFrame = null;
          resolve();
        }
      };

      this._animationFrame = requestAnimationFrame(frame);
    });
  },

  /**
   * Phase B — the chosen ball travels from its current position to the
   * winner display zone. Other balls keep bouncing at reduced opacity.
   * Cubic ease-out matches wheel/slot-machine pattern.
   */
  _dropPhase(ballIdx, durationMs) {
    if (ballIdx < 0 || ballIdx >= this._balls.length) {
      ballIdx = 0;
    }
    const b      = this._balls[ballIdx];
    const startX = b.x;
    const startY = b.y;

    // Snap winner name immediately (Phase B start)
    this._name.textContent = b.name;

    return new Promise(resolve => {
      const start = performance.now();

      const frame = (now) => {
        const t      = Math.min(1, (now - start) / durationMs);
        const eased  = 1 - Math.pow(1 - t, 3);   // cubic ease-out (same as wheel)

        // Interpolated position
        const cx = startX + (WINNER_X - startX) * eased;
        const cy = startY + (WINNER_Y - startY) * eased;

        // Swarm keeps moving during drop
        // (only step physics for the non-dropping balls — we reuse _stepPhysics
        //  but hold the dropping ball outside the array temporarily)
        this._stepPhysics();

        // Draw with dropping ball tracked separately
        this._drawFrame(ballIdx, t, cx, cy, 1.3);

        if (t < 1) {
          this._animationFrame = requestAnimationFrame(frame);
        } else {
          this._animationFrame = null;
          // Final draw at exact destination with full scale
          this._drawFrame(ballIdx, 1, WINNER_X, WINNER_Y, 1.3);
          if (this._synth) {
            this._synth.kick();
            setTimeout(() => { if (this._synth) this._synth.fanfare(); }, 300);
          }
          resolve();
        }
      };

      this._animationFrame = requestAnimationFrame(frame);
    });
  }
};
