(() => {
  const $root = document.getElementById('giveaway-root');
  const $keyword = document.getElementById('keyword');
  const $counter = document.getElementById('counter');
  const $countdown = document.getElementById('countdown');
  const $wheel = document.getElementById('wheel');
  const $wheelCanvas = document.getElementById('wheel-canvas');
  const $wheelName = document.getElementById('wheel-name');
  const $reveal = document.getElementById('reveal');
  const $winnersList = document.getElementById('winners-list');

  // Wheel slice palette — vibrant but harmonised; colours alternate so
  // adjacent slices are always distinguishable. ~12 hues so 24+ participant
  // wheels still get colour variety without two same-colour slices touching.
  const SLICE_COLORS = [
    '#ef4444', '#f97316', '#f59e0b', '#eab308',
    '#84cc16', '#22c55e', '#10b981', '#14b8a6',
    '#06b6d4', '#0ea5e9', '#3b82f6', '#6366f1',
    '#8b5cf6', '#a855f7', '#d946ef', '#ec4899'
  ];

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
    $wheelName.textContent = '';
    $winnersList.innerHTML = '';
    $wheel.classList.remove('landed');
    hide($wheel);
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

  // ─── Wheel rendering ────────────────────────────────────────────────
  // Drawn at logical 520×520 then scaled by CSS to 260; the high-res
  // canvas keeps text crisp on any DPI without a transparent retina hack.
  function drawWheel(participants, rotation) {
    const ctx = $wheelCanvas.getContext('2d');
    const W = $wheelCanvas.width;
    const cx = W / 2, cy = W / 2;
    const outerR = W / 2 - 8;
    const innerR = 36;

    ctx.clearRect(0, 0, W, W);
    if (participants.length === 0) return;

    const slice = (Math.PI * 2) / participants.length;

    // Slice fills + dividers.
    for (let i = 0; i < participants.length; i++) {
      const start = rotation + i * slice - Math.PI / 2;
      const end = start + slice;
      const color = SLICE_COLORS[i % SLICE_COLORS.length];

      ctx.beginPath();
      ctx.moveTo(cx, cy);
      ctx.arc(cx, cy, outerR, start, end);
      ctx.closePath();
      ctx.fillStyle = color;
      ctx.fill();

      ctx.strokeStyle = 'rgba(0,0,0,0.25)';
      ctx.lineWidth = 1;
      ctx.stroke();
    }

    // Per-slice label text — only when slice is wide enough to read.
    // Below ~10 px arc length we hide the label to avoid noisy overlap.
    const labelArcMin = 14 * Math.PI / 180;  // 14°
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
        // Outline for legibility on bright fills.
        ctx.shadowColor = 'rgba(0,0,0,0.65)';
        ctx.shadowBlur = 4;
        ctx.fillText(text, outerR - 14, 0);
        ctx.restore();
      }
    }

    // Centre hub.
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
  }

  /**
   * Returns the rotation (radians) such that the slice at `winnerIndex`
   * lands under the top arrow. With slice 0 already pointing up at
   * rotation=0, we negate winnerIndex's centre-of-slice angle. Plus
   * `extraTurns` full rotations so the spin feels physical, and a small
   * jitter so the arrow doesn't land exactly on the boundary.
   */
  function targetRotation(participantCount, winnerIndex, extraTurns) {
    const slice = (Math.PI * 2) / participantCount;
    // -slice/2 puts the arrow at the centre of the slice; jitter ±35 % of slice.
    const jitter = (Math.random() - 0.5) * slice * 0.7;
    const baseAngle = -winnerIndex * slice - slice / 2 + jitter;
    return baseAngle + extraTurns * Math.PI * 2;
  }

  /**
   * Spins the wheel once and resolves when it lands. winnerIndex is the
   * participant in `pool` that should be under the arrow at landing.
   */
  function spinOnce(pool, winnerIndex, durationMs) {
    return new Promise(resolve => {
      const target = targetRotation(pool.length, winnerIndex, 5 + Math.floor(Math.random() * 3));
      const start = performance.now();
      let lastHighlightIdx = -1;

      function frame(now) {
        const t = Math.min(1, (now - start) / durationMs);
        // Strong ease-out (cubic) — fast spin then gentle landing.
        const eased = 1 - Math.pow(1 - t, 3);
        const rotation = target * eased;

        drawWheel(pool, rotation);

        // Live name under arrow (top of wheel).
        const slice = (Math.PI * 2) / pool.length;
        const normalised = ((-rotation - slice / 2) % (Math.PI * 2) + Math.PI * 2) % (Math.PI * 2);
        const idx = Math.floor(normalised / slice) % pool.length;
        if (idx !== lastHighlightIdx) {
          const p = pool[idx];
          $wheelName.textContent = p.DisplayName || p.Username || '';
          lastHighlightIdx = idx;
        }

        if (t < 1) {
          requestAnimationFrame(frame);
        } else {
          // Snap final name to the actual winner so the displayed text
          // matches the awarded participant exactly (rotation jitter
          // could leave the highlight one slice off).
          $wheelName.textContent = pool[winnerIndex].DisplayName || pool[winnerIndex].Username || '';
          $wheel.classList.add('landed');
          resolve();
        }
      }
      requestAnimationFrame(frame);
    });
  }

  async function onWinnersDrawn(e) {
    if (e.GiveawayId !== state.giveawayId) return;
    if (state.countdownTimer) { clearInterval(state.countdownTimer); state.countdownTimer = null; }

    const pool = e.AnimationPool || [];
    if (pool.length === 0) {
      $wheelName.textContent = 'Henüz katılımcı yok';
      show($wheel);
      setTimeout(() => { reset(); }, 5000);
      return;
    }

    show($wheel);
    drawWheel(pool, 0);

    const winners = e.Winners || [];

    // For each winner, find their index in the pool and spin to land on it.
    // Multiple winners → sequential spins so each gets the moment-of-reveal.
    for (let i = 0; i < winners.length; i++) {
      const w = winners[i];
      let idx = pool.findIndex(p =>
        p.Username === w.Username && p.Platform === w.Platform);
      if (idx < 0) idx = 0;  // shouldn't happen — pool always contains winners

      $wheel.classList.remove('landed');
      // Slightly faster spins for subsequent winners — keeps energy up.
      const dur = i === 0 ? 4500 : 2800;
      await spinOnce(pool, idx, dur);
      // Brief pause so audience reads the landed name before the next spin.
      await new Promise(r => setTimeout(r, 900));
    }

    revealWinners(winners);
  }

  function revealWinners(winners) {
    hide($wheel);
    $winnersList.innerHTML = '';
    const PLATFORM_EMOJI = { instagram: '📷', tiktok: '🎵', facebook: '👥', youtube: '▶️' };
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
