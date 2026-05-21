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
    const SCAN_INTERVAL = 200;
    const DEDUPE_WINDOW_MS = 5000;
    const DEDUPE_CLEANUP_INTERVAL_MS = 30_000;

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
        // Sliding-window TTL deduplication: hash → expiryTimestamp (ms since epoch).
        // Same (username, text) is "same message" only if seen within DEDUPE_WINDOW_MS.
        // After 5s the same text from the same user is treated as a new message
        // (operator-friendly: customers re-type codes for different products).
        const seenComments = new Map(); // hash -> expiryTimestamp (ms since epoch)
        let cleanupTimer = null;

        // Debug instrumentation — measured per 10s window, sent to WPF for log analysis.
        const STATS_INTERVAL_MS = 10_000;
        let stats = freshStats();
        let statsTimer = null;

        function freshStats() {
            return {
                scanCount: 0,
                commentsObserved: 0,       // total comments found across all scans this window
                deduped: 0,                // total dropped as duplicates
                sent: 0,                   // total emitted to WS
                observerBursts: 0,
                scanIntervalMs: SCAN_INTERVAL,
                dedupeWindowMs: DEDUPE_WINDOW_MS,
                windowStart: Date.now(),
            };
        }

        function flushStats() {
            if (!isConnected) return;
            const snapshot = stats;
            snapshot.windowEnd = Date.now();
            snapshot.windowDurationMs = snapshot.windowEnd - snapshot.windowStart;
            snapshot.dedupeCacheSize = seenComments.size;
            sendMessage({ type: 'debug-stats', platform: adapter.platform, stats: snapshot });
            log(`📊 stats(${(snapshot.windowDurationMs/1000).toFixed(1)}s): observed=${snapshot.commentsObserved} sent=${snapshot.sent} deduped=${snapshot.deduped} scans=${snapshot.scanCount} bursts=${snapshot.observerBursts} cache=${snapshot.dedupeCacheSize}`);
            stats = freshStats();
        }

        let ws = null;
        let isConnected = false;
        let observer = null;
        let observerScanTimer = null;
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

                    if (cleanupTimer) clearInterval(cleanupTimer);
                    cleanupTimer = setInterval(pruneExpiredDedupe, DEDUPE_CLEANUP_INTERVAL_MS);

                    if (statsTimer) clearInterval(statsTimer);
                    statsTimer = setInterval(flushStats, STATS_INTERVAL_MS);
                };

                ws.onclose = () => {
                    log('WebSocket closed, reconnecting...');
                    isConnected = false;
                    stopPeriodicScan();
                    if (cleanupTimer) { clearInterval(cleanupTimer); cleanupTimer = null; }
                    if (statsTimer) { clearInterval(statsTimer); statsTimer = null; }
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
                        commentCount: seenComments.size,
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

        function pruneExpiredDedupe() {
            const now = Date.now();
            for (const [hash, expiry] of seenComments) {
                if (expiry <= now) seenComments.delete(hash);
            }
        }

        function processComments(comments) {
            stats.scanCount++;
            stats.commentsObserved += comments.length;

            comments.forEach(({ username, text, source, displayName, avatarUrl }) => {
                const hash = createCommentHash(username, text);
                const now = Date.now();
                const existing = seenComments.get(hash);
                if (existing !== undefined && existing > now) {
                    stats.deduped++;
                    return;
                }
                seenComments.set(hash, now + DEDUPE_WINDOW_MS);
                stats.sent++;

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

            return stats.sent;
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
            if (observerScanTimer) { clearTimeout(observerScanTimer); observerScanTimer = null; }
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

                let anyAdded = false;
                for (const m of mutations) {
                    if (m.addedNodes.length > 0) { anyAdded = true; break; }
                }
                if (!anyAdded) return;

                stats.observerBursts++;
                // Debounce: coalesce a burst of mutations into one scan, but ensure each
                // burst (separated by >= 50ms idle) gets its own scan — vs the previous
                // implementation that returned after the FIRST mutation event of the burst.
                if (observerScanTimer) return;
                observerScanTimer = setTimeout(() => {
                    observerScanTimer = null;
                    processComments(safeScan());
                }, 50);
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
                    seenComments.clear();
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
                seenCount: seenComments.size,
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
