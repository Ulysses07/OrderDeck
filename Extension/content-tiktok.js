/**
 * OrderDeck Chat Bridge - TikTok Content Script
 * TikTok Live sayfasındaki yorumları izler ve OrderDeck'e gönderir.
 *
 * v2.0 (ported from UniCast) - Tüm TikTok sayfalarında inject olur,
 * live tespit ederse taramaya başlar.
 */

// Expose a debug handle immediately so it's available even if init crashes.
window.__orderdeckBridge = { status: () => ({ early: true, error: 'Not initialized yet' }) };

(function() {
    'use strict';

    const LIVEDECK_WS_PORT = 4748;
    const RECONNECT_INTERVAL = 3000;
    const SCAN_INTERVAL = 500;
    const SEEN_COMMENTS = new Set();
    const MAX_SEEN_CACHE = 500;

    let ws = null;
    let isConnected = false;
    let observer = null;
    let scanTimer = null;
    let reconnectTimer = null;
    let debugMode = true;
    let isLivePage = false;

    function log(...args) {
        if (debugMode) console.log('[OrderDeck TikTok]', ...args);
    }

    function logError(...args) {
        console.error('[OrderDeck TikTok]', ...args);
    }

    function extractUsername() {
        const match = window.location.pathname.match(/@([^/]+)/);
        return match ? match[1] : 'unknown';
    }

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        if (url.includes('/live')) return true;
        if (document.querySelector('[data-e2e="chat-list"]')) return true;
        if (document.querySelector('[class*="ChatList"]')) return true;
        return false;
    }

    /**
     * Open the WebSocket connection to the OrderDeck bridge server.
     */
    function connectWebSocket() {
        if (ws && ws.readyState === WebSocket.OPEN) return;

        try {
            log('Connecting to OrderDeck bridge...');
            ws = new WebSocket(`ws://localhost:${LIVEDECK_WS_PORT}/extension`);

            ws.onopen = () => {
                log('WebSocket connected ✓');
                isConnected = true;
                clearTimeout(reconnectTimer);

                sendMessage({
                    type: 'connected',
                    platform: 'tiktok',
                    username: extractUsername(),
                    url: window.location.href,
                    timestamp: Date.now()
                });

                try { chrome.runtime.sendMessage({ action: 'setConnected', connected: true, platform: 'tiktok' }); } catch (e) {}

                startPeriodicScan();
            };

            ws.onclose = () => {
                log('WebSocket closed, reconnecting...');
                isConnected = false;
                stopPeriodicScan();
                try { chrome.runtime.sendMessage({ action: 'setConnected', connected: false, platform: 'tiktok' }); } catch (e) {}
                scheduleReconnect();
            };

            ws.onerror = (error) => {
                logError('WebSocket error:', error);
                isConnected = false;
            };

            ws.onmessage = (event) => {
                try { handleServerMessage(JSON.parse(event.data)); } catch (e) {
                    logError('Message parse error:', e);
                }
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
                    platform: 'tiktok',
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

    function cleanUsername(username) {
        if (!username) return 'unknown';
        return username.replace(/^@/, '').replace(/:$/, '').trim();
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 50) return false;
        if (message.length === 0 || message.length > 1000) return false;
        if (username === message) return false;

        const uiTexts = ['live', 'follow', 'share', 'gift', 'like', 'comment', 'send',
                         'rose', 'viewers', 'watching', 'joined', 'top', 'gifts', 'chat', 'settings'];
        if (uiTexts.includes(username.toLowerCase())) return false;

        return true;
    }

    /**
     * Scan the DOM for TikTok Live chat comments.
     * Three strategies in order of precision.
     */
    function scanForComments() {
        const comments = [];
        const foundPairs = new Set();

        // Strategy 1: data-e2e attributes (most reliable)
        try {
            document.querySelectorAll('[data-e2e="chat-message"]').forEach(item => {
                const usernameEl = item.querySelector('[data-e2e="comment-username"], [data-e2e="chat-username"]');
                const messageEl = item.querySelector('[data-e2e="comment-text"], [data-e2e="chat-text"]');

                const username = usernameEl?.textContent?.trim();
                const message = messageEl?.textContent?.trim();

                if (isValidComment(username, message)) {
                    const pairKey = `${username}|${message}`;
                    if (!foundPairs.has(pairKey)) {
                        foundPairs.add(pairKey);
                        comments.push({ username: cleanUsername(username), text: message, source: 'data-e2e' });
                    }
                }
            });
        } catch (e) { logError('Strategy 1 error:', e); }

        // Strategy 2: class-based selectors
        if (comments.length === 0) {
            try {
                const selectors = [
                    '[class*="DivCommentItemContainer"]',
                    '[class*="comment-item"]',
                    '[class*="ChatMessage"]'
                ];

                for (const selector of selectors) {
                    document.querySelectorAll(selector).forEach(item => {
                        const spans = item.querySelectorAll('span');
                        if (spans.length >= 2) {
                            const username = spans[0]?.textContent?.trim();
                            const message = spans[1]?.textContent?.trim();
                            if (isValidComment(username, message)) {
                                const pairKey = `${username}|${message}`;
                                if (!foundPairs.has(pairKey)) {
                                    foundPairs.add(pairKey);
                                    comments.push({ username: cleanUsername(username), text: message, source: selector });
                                }
                            }
                        }
                    });
                }
            } catch (e) { logError('Strategy 2 error:', e); }
        }

        // Strategy 3: Chat list children
        if (comments.length === 0) {
            try {
                const chatList = document.querySelector('[data-e2e="chat-list"], [class*="ChatList"]');
                if (chatList) {
                    Array.from(chatList.children).forEach(item => {
                        const spans = item.querySelectorAll('span');
                        if (spans.length >= 2) {
                            const username = spans[0]?.textContent?.trim();
                            const message = spans[1]?.textContent?.trim();
                            if (isValidComment(username, message)) {
                                const pairKey = `${username}|${message}`;
                                if (!foundPairs.has(pairKey)) {
                                    foundPairs.add(pairKey);
                                    comments.push({ username: cleanUsername(username), text: message, source: 'chatlist-child' });
                                }
                            }
                        }
                    });
                }
            } catch (e) { logError('Strategy 3 error:', e); }
        }

        return comments;
    }

    function processComments(comments) {
        let newCount = 0;

        comments.forEach(({ username, text, source }) => {
            const hash = createCommentHash(username, text);

            if (!SEEN_COMMENTS.has(hash)) {
                SEEN_COMMENTS.add(hash);
                newCount++;

                // Flat ExtensionMessage shape expected by OrderDeck bridge
                const payload = {
                    type: 'chat',
                    platform: 'tiktok',
                    username: username,
                    displayName: username,         // TikTok DOM doesn't expose separate display name
                    avatarUrl: null,
                    text: text,
                    externalId: `tt-${Date.now()}-${hash}`,
                    timestamp: Math.floor(Date.now() / 1000)
                };

                log(`✓ [${source}]: @${username}: ${text.substring(0, 50)}${text.length > 50 ? '...' : ''}`);

                if (!sendMessage(payload)) {
                    log('  -> ERROR: WebSocket not connected');
                }
            }
        });

        if (SEEN_COMMENTS.size > MAX_SEEN_CACHE) {
            const arr = Array.from(SEEN_COMMENTS);
            arr.splice(0, arr.length - MAX_SEEN_CACHE / 2);
            SEEN_COMMENTS.clear();
            arr.forEach(h => SEEN_COMMENTS.add(h));
        }

        return newCount;
    }

    function startPeriodicScan() {
        if (scanTimer) clearInterval(scanTimer);

        log('Periodic scan started (' + SCAN_INTERVAL + 'ms)');

        const comments = scanForComments();
        log(`Initial scan: ${comments.length} comment(s) found`);
        processComments(comments);

        scanTimer = setInterval(() => {
            const comments = scanForComments();
            processComments(comments);
        }, SCAN_INTERVAL);
    }

    function stopPeriodicScan() {
        if (scanTimer) { clearInterval(scanTimer); scanTimer = null; }
    }

    function startObserver() {
        if (observer) observer.disconnect();

        const container = document.querySelector('[data-e2e="chat-list"]') ||
                         document.querySelector('[class*="ChatList"]') ||
                         document.querySelector('[role="main"]') ||
                         document.body;

        log('Starting MutationObserver on:', container.tagName || 'body');

        observer = new MutationObserver((mutations) => {
            if (!isLivePage) {
                if (checkIfLivePage()) {
                    isLivePage = true;
                    log('Live page detected (MutationObserver)');
                    connectWebSocket();
                }
                return;
            }

            for (const m of mutations) {
                if (m.addedNodes.length > 0) {
                    processComments(scanForComments());
                    break;
                }
            }
        });

        observer.observe(container, { childList: true, subtree: true });
        log('MutationObserver active');
    }

    function init() {
        log('=========================================');
        log('OrderDeck TikTok Bridge v2.0');
        log('URL:', window.location.href);
        isLivePage = checkIfLivePage();
        log('Live page:', isLivePage ? 'YES' : 'No (watching)');
        log('=========================================');

        if (isLivePage) connectWebSocket();

        setTimeout(startObserver, 2000);

        // Watch for SPA navigation
        let lastUrl = location.href;
        new MutationObserver(() => {
            const url = location.href;
            if (url !== lastUrl) {
                lastUrl = url;
                log('Page changed:', url);
                isLivePage = checkIfLivePage();
                if (isLivePage) {
                    SEEN_COMMENTS.clear();
                    connectWebSocket();
                    setTimeout(startPeriodicScan, 2000);
                } else {
                    stopPeriodicScan();
                }
            }
        }).observe(document, { subtree: true, childList: true });
    }

    // Update the debug handle
    window.__orderdeckBridge = {
        scan: () => { const c = scanForComments(); console.log('Comments found:', c); return c; },
        send: sendMessage,
        status: () => ({
            connected: isConnected,
            wsState: ws?.readyState,
            seenCount: SEEN_COMMENTS.size,
            isLive: isLivePage,
            username: extractUsername()
        }),
        forceSend: () => {
            const comments = scanForComments();
            comments.forEach(c => {
                const hash = createCommentHash(c.username, c.text);
                sendMessage({
                    type: 'chat',
                    platform: 'tiktok',
                    username: c.username,
                    displayName: c.username,
                    avatarUrl: null,
                    text: c.text,
                    externalId: `tt-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                    timestamp: Math.floor(Date.now() / 1000)
                });
            });
            return `${comments.length} comment(s) sent`;
        }
    };

    // Bootstrap
    try {
        if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', init);
        } else {
            init();
        }
    } catch (e) {
        logError('INIT ERROR:', e);
    }

    log('Script loaded ✓');
})();
