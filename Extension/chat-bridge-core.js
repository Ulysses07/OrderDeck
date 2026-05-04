/**
 * OrderDeck Chat Bridge — Shared Core
 *
 * WebSocket lifecycle, dedup cache, MutationObserver, periodic scan, debug
 * handle. Every platform-specific content script provides a small adapter
 * (scan + checkLive + observer-target + valid-comment helpers) and calls
 * `OrderDeckChatBridge.start(adapter)`.
 *
 * Extracted on 2026-05-01 when the platform count grew from 2 → 5.
 */

window.OrderDeckChatBridge = (function () {
    'use strict';

    const WS_PORT = 4748;
    const RECONNECT_INTERVAL = 3000;
    const SCAN_INTERVAL = 500;
    const MAX_SEEN_CACHE = 500;

    /**
     * Start the bridge. `adapter` is required and must provide:
     *   platform           string  — short id sent to server ("instagram", "tiktok", "facebook", "youtube", "twitch")
     *   externalIdPrefix   string  — short prefix for synthetic message ids ("ig", "tt", "fb", "yt", "tw")
     *   scanForComments    () => Array<{username, text, source?, displayName?, avatarUrl?}>
     *   checkIfLivePage    () => boolean
     *   getObserverTarget  () => Element | null  — root that gets MutationObserver
     *   debugLabel         string  — printed in console (e.g. "OrderDeck Instagram")
     */
    function start(adapter) {
        const SEEN_COMMENTS = new Set();

        let ws = null;
        let isConnected = false;
        let observer = null;
        let scanTimer = null;
        let reconnectTimer = null;
        let isLivePage = false;
        let debugMode = true;

        function log(...args)      { if (debugMode) console.log(`[${adapter.debugLabel}]`, ...args); }
        function logError(...args) { console.error(`[${adapter.debugLabel}]`, ...args); }

        function connectWebSocket() {
            if (ws && ws.readyState === WebSocket.OPEN) return;

            try {
                log('Connecting to OrderDeck bridge...');
                ws = new WebSocket(`ws://localhost:${WS_PORT}/extension`);

                ws.onopen = () => {
                    log('WebSocket connected ✓');
                    isConnected = true;
                    clearTimeout(reconnectTimer);

                    sendMessage({
                        type: 'connected',
                        platform: adapter.platform,
                        url: window.location.href,
                        timestamp: Date.now()
                    });

                    try { chrome.runtime.sendMessage({ action: 'setConnected', connected: true, platform: adapter.platform }); } catch (e) {}

                    startPeriodicScan();
                };

                ws.onclose = () => {
                    log('WebSocket closed, reconnecting...');
                    isConnected = false;
                    stopPeriodicScan();
                    try { chrome.runtime.sendMessage({ action: 'setConnected', connected: false, platform: adapter.platform }); } catch (e) {}
                    scheduleReconnect();
                };

                ws.onerror = (error) => {
                    logError('WebSocket error:', error);
                    isConnected = false;
                };

                ws.onmessage = (event) => {
                    try { handleServerMessage(JSON.parse(event.data)); }
                    catch (e) { logError('Message parse error:', e); }
                };
            } catch (error) {
                logError('WebSocket connection error:', error);
                scheduleReconnect();
            }
        }

        function scheduleReconnect() {
            if (reconnectTimer) clearTimeout(reconnectTimer);
            reconnectTimer = setTimeout(connectWebSocket, RECONNECT_INTERVAL);
        }

        function sendMessage(data) {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify(data));
                return true;
            }
            return false;
        }

        function handleServerMessage(data) {
            switch (data.type) {
                case 'ping':
                    sendMessage({ type: 'pong' });
                    break;
                case 'getStatus':
                    sendMessage({
                        type: 'status',
                        platform: adapter.platform,
                        observing: observer !== null,
                        commentCount: SEEN_COMMENTS.size,
                        url: window.location.href
                    });
                    break;
            }
        }

        function createCommentHash(username, text) {
            const str = `${username}:${text}`.toLowerCase().trim();
            let hash = 0;
            for (let i = 0; i < str.length; i++) {
                hash = ((hash << 5) - hash) + str.charCodeAt(i);
                hash = hash & hash;
            }
            return hash.toString(36);
        }

        function processComments(comments) {
            let newCount = 0;

            comments.forEach(({ username, text, source, displayName, avatarUrl }) => {
                const hash = createCommentHash(username, text);
                if (SEEN_COMMENTS.has(hash)) return;
                SEEN_COMMENTS.add(hash);
                newCount++;

                const payload = {
                    type: 'chat',
                    platform: adapter.platform,
                    username: username,
                    displayName: displayName ?? username,
                    avatarUrl: avatarUrl ?? null,
                    text: text,
                    externalId: `${adapter.externalIdPrefix}-${Date.now()}-${hash}`,
                    timestamp: Date.now()
                };

                log(`✓ [${source ?? 'scan'}]: @${username}: ${text.substring(0, 50)}${text.length > 50 ? '...' : ''}`);

                if (!sendMessage(payload)) log('  -> ERROR: WebSocket not connected');
            });

            // LRU-ish eviction so the dedup set doesn't grow without bound during long streams.
            if (SEEN_COMMENTS.size > MAX_SEEN_CACHE) {
                const arr = Array.from(SEEN_COMMENTS);
                arr.splice(0, arr.length - MAX_SEEN_CACHE / 2);
                SEEN_COMMENTS.clear();
                arr.forEach(h => SEEN_COMMENTS.add(h));
            }

            return newCount;
        }

        function safeScan() {
            try { return adapter.scanForComments() || []; }
            catch (e) { logError('Adapter scan error:', e); return []; }
        }

        function startPeriodicScan() {
            if (scanTimer) clearInterval(scanTimer);
            log(`Periodic scan started (${SCAN_INTERVAL}ms)`);

            const initial = safeScan();
            log(`Initial scan: ${initial.length} comment(s)`);
            processComments(initial);

            scanTimer = setInterval(() => processComments(safeScan()), SCAN_INTERVAL);
        }

        function stopPeriodicScan() {
            if (scanTimer) { clearInterval(scanTimer); scanTimer = null; }
        }

        function startObserver() {
            if (observer) observer.disconnect();

            const target = adapter.getObserverTarget?.() ?? document.body;
            log('Starting MutationObserver on:', target.tagName ?? '#document');

            observer = new MutationObserver((mutations) => {
                if (!isLivePage) {
                    if (adapter.checkIfLivePage()) {
                        isLivePage = true;
                        log('Live page detected (MutationObserver)');
                        connectWebSocket();
                    }
                    return;
                }

                for (const m of mutations) {
                    if (m.addedNodes.length > 0) {
                        processComments(safeScan());
                        break;
                    }
                }
            });

            observer.observe(target, { childList: true, subtree: true });
            log('MutationObserver active');
        }

        function init() {
            log('=========================================');
            log(`${adapter.debugLabel} bridge`);
            log('URL:', window.location.href);
            isLivePage = adapter.checkIfLivePage();
            log('Live page:', isLivePage ? 'YES' : 'No (watching)');
            log('=========================================');

            if (isLivePage) connectWebSocket();
            setTimeout(startObserver, 1500);

            // SPA navigation watchdog
            let lastUrl = location.href;
            new MutationObserver(() => {
                const url = location.href;
                if (url === lastUrl) return;
                lastUrl = url;
                log('Page changed:', url);
                isLivePage = adapter.checkIfLivePage();
                if (isLivePage) {
                    SEEN_COMMENTS.clear();
                    connectWebSocket();
                    setTimeout(startPeriodicScan, 2000);
                } else {
                    stopPeriodicScan();
                }
            }).observe(document, { subtree: true, childList: true });

            // Re-arm the MutationObserver whenever the central selector
            // bundle rotates — the new observer-target selector might point
            // somewhere different now (e.g. TikTok renamed [data-e2e="chat-list"]).
            // We only re-arm if we're currently on a live page, otherwise the
            // tree under document.body is fine and a no-op spares churn.
            if (self.OrderDeckSelectors?.onUpdate) {
                self.OrderDeckSelectors.onUpdate(() => {
                    log('Selector bundle updated; re-arming observer');
                    isLivePage = adapter.checkIfLivePage();
                    if (isLivePage) startObserver();
                });
            }
        }

        // Debug handle on window — devtools-friendly per platform.
        window.__orderdeckBridge = {
            platform: adapter.platform,
            scan: () => { const c = safeScan(); console.log('Comments found:', c); return c; },
            send: sendMessage,
            status: () => ({
                connected: isConnected,
                wsState: ws?.readyState,
                seenCount: SEEN_COMMENTS.size,
                isLive: isLivePage,
                url: window.location.href,
                platform: adapter.platform
            }),
            forceSend: () => {
                const comments = safeScan();
                comments.forEach(c => {
                    const hash = createCommentHash(c.username, c.text);
                    sendMessage({
                        type: 'chat',
                        platform: adapter.platform,
                        username: c.username,
                        displayName: c.displayName ?? c.username,
                        avatarUrl: c.avatarUrl ?? null,
                        text: c.text,
                        externalId: `${adapter.externalIdPrefix}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                        timestamp: Date.now()
                    });
                });
                return `${comments.length} comment(s) sent`;
            }
        };

        try {
            if (document.readyState === 'loading')
                document.addEventListener('DOMContentLoaded', init);
            else
                init();
        } catch (e) { logError('INIT ERROR:', e); }

        log('Script loaded ✓');
    }

    return { start };
})();
