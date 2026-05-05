// audio-controller.js — shared by all animation plugins. Owns the
// HTMLAudioElement cache so a plugin can call audio.play('tick.mp3')
// without rebuilding elements per-call. Routes through master volume +
// muted toggle from Settings (passed in at construction time, can be
// updated later via setVolume/setMuted).

export class AudioController {
  /**
   * @param {string} basePath  Folder that holds the plugin's audio files,
   *                           e.g. './animations/wheel/audio/'.
   * @param {number} volume    Master volume 0-1.
   * @param {boolean} muted    Hard mute switch.
   */
  constructor(basePath, volume, muted) {
    this.basePath = basePath.endsWith('/') ? basePath : basePath + '/';
    this._volume = clamp01(volume);
    this._muted = !!muted;
    /** @type {Map<string, HTMLAudioElement>} */
    this._cache = new Map();
  }

  setVolume(v) {
    this._volume = clamp01(v);
    for (const a of this._cache.values()) a.volume = this._effective();
  }

  setMuted(b) {
    this._muted = !!b;
    for (const a of this._cache.values()) a.muted = this._muted;
  }

  /** Plays a clip. Filename is relative to `basePath`. */
  play(filename) {
    const a = this._get(filename);
    a.currentTime = 0;
    // Best-effort; browsers may block autoplay before user gesture.
    a.play().catch(() => {});
  }

  stop(filename) {
    const a = this._cache.get(filename);
    if (a) { a.pause(); a.currentTime = 0; }
  }

  /** Tear-down — call from plugin reset(). */
  disposeAll() {
    for (const a of this._cache.values()) { a.pause(); a.src = ''; }
    this._cache.clear();
  }

  _get(filename) {
    let a = this._cache.get(filename);
    if (!a) {
      a = new Audio(this.basePath + filename);
      a.preload = 'auto';
      a.volume = this._effective();
      a.muted = this._muted;
      this._cache.set(filename, a);
    }
    return a;
  }

  _effective() {
    return this._muted ? 0 : this._volume;
  }
}

function clamp01(v) { return Math.max(0, Math.min(1, +v || 0)); }
