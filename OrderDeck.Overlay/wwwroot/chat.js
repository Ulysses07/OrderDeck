(function () {
  'use strict';

  // Twitch/YouTube live-chat density: 12-15 visible messages on screen.
  // 50 was overlay-snappy but visually overwhelming on a 1080p browser
  // source — operator's content (camera, products) ended up boxed-in.
  const MAX_VISIBLE = 15;
  const RECONNECT_BASE_MS = 1000;
  const RECONNECT_MAX_MS = 10000;

  const container = document.getElementById('chat-container');
  let reconnectAttempt = 0;
  let socket = null;

  function connect() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const url = `${proto}://${location.host}/ws/chat`;
    socket = new WebSocket(url);

    socket.onopen = () => { reconnectAttempt = 0; };
    socket.onmessage = (e) => {
      try {
        const evt = JSON.parse(e.data);
        if (evt.type === 'chat.snapshot') {
          (evt.data.recentMessages || []).forEach(appendMessage);
        } else if (evt.type === 'chat.message') {
          appendMessage(evt.data);
        }
      } catch (err) {
        console.error('OrderDeck overlay parse error', err);
      }
    };
    socket.onclose = scheduleReconnect;
    socket.onerror = () => { try { socket.close(); } catch (_) {} };
  }

  function scheduleReconnect() {
    reconnectAttempt++;
    const backoff = Math.min(RECONNECT_BASE_MS * Math.pow(2, reconnectAttempt - 1),
                              RECONNECT_MAX_MS);
    setTimeout(connect, backoff);
  }

  function appendMessage(msg) {
    const el = document.createElement('div');
    el.className = 'chat-message';
    el.dataset.id = msg.id;

    const badge = document.createElement('div');
    badge.className = `platform-badge ${msg.platform}`;
    el.appendChild(badge);

    const body = document.createElement('div');
    body.className = 'body';

    const user = document.createElement('div');
    user.className = 'username';
    user.textContent = msg.displayName || msg.username;
    body.appendChild(user);

    const text = document.createElement('div');
    text.className = 'text';
    text.textContent = msg.text;
    body.appendChild(text);

    el.appendChild(body);
    container.appendChild(el);

    while (container.childElementCount > MAX_VISIBLE) {
      const oldest = container.firstElementChild;
      if (oldest) {
        oldest.classList.add('fade-out');
        setTimeout(() => oldest.remove(), 400);
      } else {
        break;
      }
    }

    el.scrollIntoView({ block: 'end' });
  }

  connect();
})();
