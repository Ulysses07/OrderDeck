// giveaway.js — pluggable animation host. The host owns the WebSocket
// connection, header/countdown/reveal UI, and confetti. Each animation
// plugin (under animations/<id>/index.js) renders the spin/draw/reveal
// inside a host-provided container.
//
// On `giveaway.started` the host:
//   1. Reads AnimationId + AudioVolume + AudioMuted from the event.
//   2. Dynamically imports `./animations/<AnimationId>/index.js`.
//      Falls back to `wheel` if import fails.
//   3. Loads the plugin's optional `style.css` link tag.
//   4. Constructs an AudioController scoped to the plugin's audio folder.
//   5. Calls plugin.init(container, audio).
//
// On `giveaway.winners.drawn` the host calls plugin.runFor(winners, pool)
// then reveals the winner list and spawns confetti (host-owned).

import { AudioController } from './audio-controller.js';

(() => {
  const $root = document.getElementById('giveaway-root');
  const $keyword = document.getElementById('keyword');
  const $counter = document.getElementById('counter');
  const $countdown = document.getElementById('countdown');
  const $stage = document.getElementById('plugin-stage');
  const $reveal = document.getElementById('reveal');
  const $winnersList = document.getElementById('winners-list');

  let state = {
    giveawayId: null,
    keyword: '',
    durationSeconds: 0,
    startedAt: 0,
    countdownTimer: null,
    plugin: null,
    pluginStyleEl: null
  };

  function show(el) { el.classList.remove('hidden'); }
  function hide(el) { el.classList.add('hidden'); }

  function reset() {
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
    if (state.plugin) { try { state.plugin.reset(); } catch {} }
    if (state.pluginStyleEl) { state.pluginStyleEl.remove(); }
    state = {
      giveawayId: null, keyword: '', durationSeconds: 0, startedAt: 0,
      countdownTimer: null, plugin: null, pluginStyleEl: null
    };
    $keyword.textContent = '';
    $counter.textContent = '0 katılımcı';
    $countdown.textContent = '';
    $stage.innerHTML = '';
    $winnersList.innerHTML = '';
    hide($reveal);
    hide($root);
  }

  function startCountdown() {
    if (state.durationSeconds <= 0) { $countdown.textContent = ''; return; }
    const tick = () => {
      const elapsed = Math.floor(Date.now() / 1000) - state.startedAt;
      const remaining = Math.max(0, state.durationSeconds - elapsed);
      const m = Math.floor(remaining / 60);
      const s = remaining % 60;
      $countdown.textContent = `${m.toString().padStart(2,'0')}:${s.toString().padStart(2,'0')}`;
      if (remaining <= 0 && state.countdownTimer) {
        clearInterval(state.countdownTimer); state.countdownTimer = null;
      }
    };
    tick();
    state.countdownTimer = setInterval(tick, 1000);
  }

  async function loadPlugin(animationId, audioVolume, audioMuted) {
    const id = animationId || 'wheel';
    let module;
    try {
      module = await import(`./animations/${id}/index.js`);
    } catch (err) {
      console.warn(`[giveaway-host] failed to load plugin '${id}', falling back to wheel`, err);
      module = await import('./animations/wheel/index.js');
    }
    const plugin = module.default;

    // Inject the plugin's optional style.css.
    const styleUrl = `./animations/${plugin.id}/style.css`;
    state.pluginStyleEl = document.createElement('link');
    state.pluginStyleEl.rel = 'stylesheet';
    state.pluginStyleEl.href = styleUrl;
    document.head.appendChild(state.pluginStyleEl);

    const audio = new AudioController(
      `./animations/${plugin.id}/audio/`,
      typeof audioVolume === 'number' ? audioVolume : 0.7,
      !!audioMuted);

    await plugin.init($stage, audio);
    state.plugin = plugin;
    return plugin;
  }

  async function onStarted(e) {
    reset();
    state.giveawayId = e.GiveawayId;
    state.keyword = e.Keyword;
    state.durationSeconds = e.DurationSeconds;
    state.startedAt = e.StartedAt;
    $keyword.textContent = `"${e.Keyword}"`;
    $counter.textContent = '0 katılımcı';
    show($root);
    startCountdown();

    await loadPlugin(e.AnimationId, e.AudioVolume, e.AudioMuted);
  }

  function onParticipant(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    $counter.textContent = `${e.TotalCount} katılımcı`;
  }

  function onCancelled(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    $root.classList.add('fade-out');
    setTimeout(() => { reset(); $root.classList.remove('fade-out'); }, 600);
  }

  async function onWinnersDrawn(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
    if (!state.plugin) {
      console.warn('[giveaway-host] winners drawn before plugin loaded');
      return;
    }

    const pool = e.AnimationPool || [];
    const winners = e.Winners || [];

    await state.plugin.runFor(winners, pool);
    revealWinners(winners);
  }

  function revealWinners(winners) {
    $stage.innerHTML = '';
    $winnersList.innerHTML = '';
    const PLATFORM_EMOJI = { instagram: '📷', tiktok: '🎵', facebook: '👥', youtube: '▶️' };
    for (const w of winners) {
      const li = document.createElement('li');
      li.className = 'winner';
      const emoji = PLATFORM_EMOJI[w.Platform] || '💬';
      li.innerHTML = `
        <span class="platform-${w.Platform}">${emoji}</span>
        <span class="name">${escapeHtml(w.DisplayName || w.Username)}</span>`;
      $winnersList.appendChild(li);
    }
    show($reveal);
    spawnConfetti();
  }

  function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c =>
      ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
  }

  function spawnConfetti() {
    for (let i = 0; i < 60; i++) {
      const c = document.createElement('span');
      c.className = 'confetti';
      c.style.left = (Math.random() * 100) + '%';
      c.style.background = `hsl(${Math.random() * 360}, 80%, 60%)`;
      c.style.animationDelay = (Math.random() * 0.5) + 's';
      c.style.animationDuration = (1.8 + Math.random() * 1.2) + 's';
      document.body.appendChild(c);
      setTimeout(() => c.remove(), 3500);
    }
  }

  function connect() {
    const ws = new WebSocket(`ws://${location.host}/ws/giveaway`);
    ws.onmessage = (msg) => {
      const evt = JSON.parse(msg.data);
      switch (evt.Type) {
        case 'giveaway.started':       onStarted(evt.Data); break;
        case 'giveaway.participant':   onParticipant(evt.Data); break;
        case 'giveaway.winners.drawn': onWinnersDrawn(evt.Data); break;
        case 'giveaway.cancelled':     onCancelled(evt.Data); break;
      }
    };
    ws.onclose = () => setTimeout(connect, 1500);
    ws.onerror = () => { try { ws.close(); } catch {} };
  }
  connect();
})();
