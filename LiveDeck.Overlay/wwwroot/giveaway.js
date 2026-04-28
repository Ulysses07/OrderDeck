(() => {
  const $root = document.getElementById('giveaway-root');
  const $keyword = document.getElementById('keyword');
  const $counter = document.getElementById('counter');
  const $countdown = document.getElementById('countdown');
  const $roulette = document.getElementById('roulette');
  const $rouletteName = document.getElementById('roulette-name');
  const $reveal = document.getElementById('reveal');
  const $winnersList = document.getElementById('winners-list');

  let state = {
    giveawayId: null,
    keyword: '',
    durationSeconds: 0,
    startedAt: 0,
    countdownTimer: null
  };

  function show(el) { el.classList.remove('hidden'); }
  function hide(el) { el.classList.add('hidden'); }

  function reset() {
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }
    state = { giveawayId: null, keyword: '', durationSeconds: 0, startedAt: 0, countdownTimer: null };
    $keyword.textContent = '';
    $counter.textContent = '0 katılımcı';
    $countdown.textContent = '';
    $rouletteName.textContent = '';
    $winnersList.innerHTML = '';
    hide($roulette);
    hide($reveal);
    hide($root);
  }

  function startCountdown() {
    if (state.durationSeconds <= 0) {
      $countdown.textContent = '';
      return;
    }
    const tick = () => {
      const elapsed = Math.floor(Date.now() / 1000) - state.startedAt;
      const remaining = Math.max(0, state.durationSeconds - elapsed);
      const m = Math.floor(remaining / 60);
      const s = remaining % 60;
      $countdown.textContent = `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`;
      if (remaining <= 0 && state.countdownTimer) {
        clearInterval(state.countdownTimer);
        state.countdownTimer = null;
      }
    };
    tick();
    state.countdownTimer = setInterval(tick, 1000);
  }

  function onStarted(e) {
    reset();
    state.giveawayId = e.GiveawayId;
    state.keyword = e.Keyword;
    state.durationSeconds = e.DurationSeconds;
    state.startedAt = e.StartedAt;
    $keyword.textContent = `"${e.Keyword}"`;
    $counter.textContent = '0 katılımcı';
    show($root);
    startCountdown();
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

  function onWinnersDrawn(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }

    const pool = e.AnimationPool || [];
    if (pool.length === 0) {
      // 0 katılımcı durumu
      $rouletteName.textContent = 'Henüz katılımcı yok';
      show($roulette);
      setTimeout(() => { reset(); }, 5000);
      return;
    }

    show($roulette);
    const totalMs = 4000;
    const startTime = performance.now();
    let lastIdx = -1;

    function frame(now) {
      const t = Math.min(1, (now - startTime) / totalMs);
      // ease-out: spin fast then slow
      const eased = 1 - Math.pow(1 - t, 3);
      const idx = Math.min(pool.length - 1, Math.floor(eased * (pool.length - 1)));
      if (idx !== lastIdx) {
        const p = pool[idx];
        $rouletteName.textContent = p.DisplayName || p.Username;
        lastIdx = idx;
      }
      if (t < 1) {
        requestAnimationFrame(frame);
      } else {
        revealWinners(e.Winners || []);
      }
    }
    requestAnimationFrame(frame);
  }

  function revealWinners(winners) {
    hide($roulette);
    $winnersList.innerHTML = '';
    const PLATFORM_EMOJI = { instagram: '📷', tiktok: '🎵' };
    for (const w of winners) {
      const li = document.createElement('li');
      li.className = 'winner';
      const emoji = PLATFORM_EMOJI[w.Platform] || '💬';
      li.innerHTML = `
        <span class="platform-${w.Platform}">${emoji}</span>
        <span class="name">${escapeHtml(w.DisplayName || w.Username)}</span>
      `;
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
