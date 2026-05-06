// synth-controller.js — Web Audio API ile ses sentezi.
// Her animasyon plugin'i SynthController'dan instance alır ve event-tipinde
// (tick, ding, whoosh, kick, fanfare, ...) sesleri tetikler. Hiçbir .mp3
// dosyası gerekmiyor — tüm sesler oscillator + envelope ile üretilir.
//
// AudioController ile aynı kontrat: setVolume(0-1), setMuted(bool),
// disposeAll(). Plugin'ler her ikisini de kullanabilir (audio.play('file.mp3')
// veya synth.tick()), ama Phase 1 default'u synth.

export class SynthController {
  /**
   * @param {number} volume  Master volume 0-1.
   * @param {boolean} muted  Hard mute.
   */
  constructor(volume, muted) {
    this._volume = clamp01(volume);
    this._muted = !!muted;
    this._ctx = null;        // lazy; created on first play (browser autoplay rules)
    this._masterGain = null;
  }

  setVolume(v) {
    this._volume = clamp01(v);
    if (this._masterGain) this._masterGain.gain.value = this._effective();
  }

  setMuted(b) {
    this._muted = !!b;
    if (this._masterGain) this._masterGain.gain.value = this._effective();
  }

  disposeAll() {
    if (this._ctx) {
      try { this._ctx.close(); } catch {}
      this._ctx = null;
      this._masterGain = null;
    }
  }

  // ─── Sound primitives ──────────────────────────────────────────────

  /** Short click — wheel slice tick, slot reel cell, roulette tick. */
  tick(freq = 800) {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'square';
    osc.frequency.value = freq;
    gain.gain.value = 0;
    gain.gain.linearRampToValueAtTime(0.4, ctx.currentTime + 0.005);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.05);
    osc.connect(gain).connect(this._masterGain);
    osc.start();
    osc.stop(ctx.currentTime + 0.06);
  }

  /** Bell-like landing chime — wheel/slot/roulette landing. */
  ding(freq = 1320) {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'sine';
    osc.frequency.value = freq;
    gain.gain.value = 0;
    gain.gain.linearRampToValueAtTime(0.5, ctx.currentTime + 0.01);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.8);
    osc.connect(gain).connect(this._masterGain);
    osc.start();
    osc.stop(ctx.currentTime + 0.85);
  }

  /** Whoosh / pitch slide — magic-hat absorb, falling-names spawn. */
  whoosh({ from = 1200, to = 200, durationMs = 400, type = 'sawtooth' } = {}) {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    const filter = ctx.createBiquadFilter();
    filter.type = 'bandpass';
    filter.frequency.value = 800;
    filter.Q.value = 0.5;
    osc.type = type;
    osc.frequency.setValueAtTime(from, ctx.currentTime);
    osc.frequency.exponentialRampToValueAtTime(to, ctx.currentTime + durationMs / 1000);
    gain.gain.setValueAtTime(0.3, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + durationMs / 1000);
    osc.connect(filter).connect(gain).connect(this._masterGain);
    osc.start();
    osc.stop(ctx.currentTime + durationMs / 1000 + 0.05);
  }

  /** Drum kick — eliminator elimination, race start. */
  kick() {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'sine';
    osc.frequency.setValueAtTime(150, ctx.currentTime);
    osc.frequency.exponentialRampToValueAtTime(40, ctx.currentTime + 0.15);
    gain.gain.setValueAtTime(0.7, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.18);
    osc.connect(gain).connect(this._masterGain);
    osc.start();
    osc.stop(ctx.currentTime + 0.2);
  }

  /** Soft pop — bingo ball drop, magic-hat sparkle. */
  pop(freq = 600) {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'triangle';
    osc.frequency.setValueAtTime(freq, ctx.currentTime);
    osc.frequency.exponentialRampToValueAtTime(freq * 0.5, ctx.currentTime + 0.08);
    gain.gain.setValueAtTime(0.4, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.1);
    osc.connect(gain).connect(this._masterGain);
    osc.start();
    osc.stop(ctx.currentTime + 0.12);
  }

  /** Card flip — card-draw reveal. */
  flip() {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const buffer = this._whiteNoise(0.12);
    if (!buffer) return;
    const src = ctx.createBufferSource();
    const filter = ctx.createBiquadFilter();
    const gain = ctx.createGain();
    src.buffer = buffer;
    filter.type = 'highpass';
    filter.frequency.setValueAtTime(2000, ctx.currentTime);
    filter.frequency.exponentialRampToValueAtTime(500, ctx.currentTime + 0.1);
    gain.gain.setValueAtTime(0.4, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.1);
    src.connect(filter).connect(gain).connect(this._masterGain);
    src.start();
  }

  /** Triumphant chord — winner reveal across all plugins. */
  fanfare() {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    // Major triad (C-E-G), each note a quick stagger for arpeggio feel.
    const notes = [523.25, 659.25, 783.99, 1046.50];  // C5 E5 G5 C6
    notes.forEach((freq, i) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'sine';
      osc.frequency.value = freq;
      const start = ctx.currentTime + i * 0.06;
      gain.gain.setValueAtTime(0, start);
      gain.gain.linearRampToValueAtTime(0.35, start + 0.02);
      gain.gain.exponentialRampToValueAtTime(0.001, start + 0.6);
      osc.connect(gain).connect(this._masterGain);
      osc.start(start);
      osc.stop(start + 0.65);
    });
  }

  /** Drum roll — eliminator buildup, race countdown. */
  roll(durationMs = 800) {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const buffer = this._whiteNoise(durationMs / 1000);
    if (!buffer) return;
    const src = ctx.createBufferSource();
    const filter = ctx.createBiquadFilter();
    const gain = ctx.createGain();
    src.buffer = buffer;
    filter.type = 'lowpass';
    filter.frequency.value = 400;
    gain.gain.setValueAtTime(0, ctx.currentTime);
    gain.gain.linearRampToValueAtTime(0.4, ctx.currentTime + 0.05);
    gain.gain.linearRampToValueAtTime(0.5, ctx.currentTime + durationMs / 1000 - 0.1);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + durationMs / 1000);
    src.connect(filter).connect(gain).connect(this._masterGain);
    src.start();
  }

  /** Engine rev — race start. */
  rev() {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    const osc = ctx.createOscillator();
    const gain = ctx.createGain();
    osc.type = 'sawtooth';
    osc.frequency.setValueAtTime(80, ctx.currentTime);
    osc.frequency.exponentialRampToValueAtTime(220, ctx.currentTime + 0.4);
    osc.frequency.exponentialRampToValueAtTime(180, ctx.currentTime + 0.6);
    gain.gain.setValueAtTime(0.3, ctx.currentTime);
    gain.gain.exponentialRampToValueAtTime(0.001, ctx.currentTime + 0.7);
    osc.connect(gain).connect(this._masterGain);
    osc.start();
    osc.stop(ctx.currentTime + 0.75);
  }

  /** Horn — race finish. */
  horn() {
    const ctx = this._ensureCtx();
    if (!ctx) return;
    [440, 554, 659].forEach((freq, i) => {
      const osc = ctx.createOscillator();
      const gain = ctx.createGain();
      osc.type = 'square';
      osc.frequency.value = freq;
      const start = ctx.currentTime + i * 0.15;
      gain.gain.setValueAtTime(0, start);
      gain.gain.linearRampToValueAtTime(0.3, start + 0.02);
      gain.gain.linearRampToValueAtTime(0.3, start + 0.18);
      gain.gain.exponentialRampToValueAtTime(0.001, start + 0.25);
      osc.connect(gain).connect(this._masterGain);
      osc.start(start);
      osc.stop(start + 0.3);
    });
  }

  // ─── Internals ─────────────────────────────────────────────────────

  _ensureCtx() {
    if (!this._ctx) {
      try {
        const Ctx = window.AudioContext || window.webkitAudioContext;
        if (!Ctx) return null;
        this._ctx = new Ctx();
        this._masterGain = this._ctx.createGain();
        this._masterGain.gain.value = this._effective();
        this._masterGain.connect(this._ctx.destination);
      } catch {
        return null;
      }
    }
    // Some browsers suspend the context until a user gesture; resume best-effort.
    if (this._ctx.state === 'suspended') {
      this._ctx.resume().catch(() => {});
    }
    return this._ctx;
  }

  _whiteNoise(durationSec) {
    const ctx = this._ctx;
    if (!ctx) return null;
    const sampleRate = ctx.sampleRate;
    const buffer = ctx.createBuffer(1, sampleRate * durationSec, sampleRate);
    const data = buffer.getChannelData(0);
    for (let i = 0; i < data.length; i++) data[i] = Math.random() * 2 - 1;
    return buffer;
  }

  _effective() {
    return this._muted ? 0 : this._volume * 0.6;  // 0.6 max ceiling — synth is loud
  }
}

function clamp01(v) { return Math.max(0, Math.min(1, +v || 0)); }
