/**
 * LiveDeck Chat Bridge - Instagram Content Script
 * Observes Instagram Live chat and forwards messages to LiveDeck.
 *
 * v4.0 (ported from UniCast) - Injects into all Instagram pages; starts
 * scraping once a Live page is detected.
 */

// Expose a debug handle immediately so it's available even if init crashes.
window.__livedeckBridge = { status: () => ({ early: true, error: 'Not initialized yet' }) };

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
        if (debugMode) {
            console.log('[LiveDeck Instagram]', ...args);
        }
    }

    function logError(...args) {
        console.error('[LiveDeck Instagram]', ...args);
    }

    /**
     * Returns true if the current page looks like an Instagram Live stream.
     */
    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        if (url.includes('/live')) return true;
        if (document.querySelector('[aria-label*="Live" i]')) return true;
        if (document.querySelector('[aria-label*="Canlı" i]')) return true;
        return false;
    }

    /**
     * Open the WebSocket connection to the LiveDeck bridge server.
     */
    function connectWebSocket() {
        if (ws && ws.readyState === WebSocket.OPEN) {
            return;
        }

        try {
            log('Connecting to LiveDeck bridge...');
            ws = new WebSocket(`ws://localhost:${LIVEDECK_WS_PORT}/extension`);

            ws.onopen = () => {
                log('WebSocket connected ✓');
                isConnected = true;
                clearTimeout(reconnectTimer);

                sendMessage({
                    type: 'connected',
                    platform: 'instagram',
                    url: window.location.href,
                    timestamp: Date.now()
                });

                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: true });
                } catch (e) {}

                startPeriodicScan();
            };

            ws.onclose = () => {
                log('WebSocket closed, reconnecting...');
                isConnected = false;
                stopPeriodicScan();
                try {
                    chrome.runtime.sendMessage({ action: 'setConnected', connected: false });
                } catch (e) {}
                scheduleReconnect();
            };

            ws.onerror = (error) => {
                logError('WebSocket error:', error);
                isConnected = false;
            };

            ws.onmessage = (event) => {
                try {
                    const data = JSON.parse(event.data);
                    handleServerMessage(data);
                } catch (e) {
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
            const char = str.charCodeAt(i);
            hash = ((hash << 5) - hash) + char;
            hash = hash & hash;
        }
        return hash.toString(36);
    }

    /**
     * Scan the DOM for Instagram Live chat comments.
     * Three strategies in order of precision.
     */
    function scanForComments() {
        const comments = [];
        const foundPairs = new Set();

        // Strategy 1: aria-label-based comment containers
        try {
            document.querySelectorAll('[aria-label*="yorum" i], [aria-label*="comment" i]').forEach(el => {
                const spans = el.querySelectorAll('span[dir="auto"]');
                if (spans.length >= 2) {
                    const username = spans[0]?.textContent?.trim();
                    const message = spans[1]?.textContent?.trim();

                    if (isValidComment(username, message)) {
                        const pairKey = `${username}|${message}`;
                        if (!foundPairs.has(pairKey)) {
                            foundPairs.add(pairKey);
                            comments.push({ username, text: message, source: 'aria-label' });
                        }
                    }
                }
            });
        } catch (e) {
            logError('Strategy 1 error:', e);
        }

        // Strategy 2: divs with exactly 2 child spans
        if (comments.length === 0) {
            try {
                document.querySelectorAll('div').forEach(div => {
                    const childSpans = Array.from(div.children).filter(el => el.tagName === 'SPAN');

                    if (childSpans.length === 2) {
                        const username = childSpans[0]?.textContent?.trim();
                        const message = childSpans[1]?.textContent?.trim();

                        if (isValidComment(username, message)) {
                            const pairKey = `${username}|${message}`;
                            if (!foundPairs.has(pairKey)) {
                                foundPairs.add(pairKey);
                                comments.push({ username: username.replace('@', ''), text: message, source: 'div-2span' });
                            }
                        }
                    }
                });
            } catch (e) {
                logError('Strategy 2 error:', e);
            }
        }

        // Strategy 3: sibling spans
        if (comments.length === 0) {
            try {
                document.querySelectorAll('span').forEach(span => {
                    const text = span.textContent?.trim();
                    const prevSibling = span.previousElementSibling;

                    if (prevSibling?.tagName === 'SPAN' && text) {
                        const username = prevSibling.textContent?.trim();

                        if (isValidComment(username, text)) {
                            const pairKey = `${username}|${text}`;
                            if (!foundPairs.has(pairKey)) {
                                foundPairs.add(pairKey);
                                comments.push({ username: username.replace('@', ''), text, source: 'sibling-span' });
                            }
                        }
                    }
                });
            } catch (e) {
                logError('Strategy 3 error:', e);
            }
        }

        return comments;
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 50) return false;
        if (message.length === 0 || message.length > 1000) return false;
        if (username.includes('\n')) return false;
        if (username === message) return false;

        const uiTexts = [
            'live', 'messages', 'share', 'like', 'comment', 'send', 'follow',
            'canlı', 'mesajlar', 'paylaş', 'beğen', 'yorum', 'gönder', 'takip et',
            'izliyor', 'watching', 'viewers', 'izleyici'
        ];

        const userLower = username.toLowerCase();
        if (uiTexts.some(ui => userLower === ui)) return false;

        if (/^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$/i.test(message)) return false;

        return true;
    }

    function processComments(comments) {
        let newCount = 0;

        comments.forEach(({ username, text, source }) => {
            const hash = createCommentHash(username, text);

            if (!SEEN_COMMENTS.has(hash)) {
                SEEN_COMMENTS.add(hash);
                newCount++;

                // Flat ExtensionMessage shape expected by LiveDeck bridge
                const payload = {
                    type: 'chat',
                    platform: 'instagram',
                    username: username,
                    displayName: username,
                    avatarUrl: null,
                    text: text,
                    externalId: `ig-${Date.now()}-${hash}`,
                    timestamp: Date.now()
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
        if (scanTimer) {
            clearInterval(scanTimer);
            scanTimer = null;
        }
    }

    function startObserver() {
        if (observer) observer.disconnect();

        const liveContainer = document.querySelector('[role="main"]') ||
                             document.querySelector('section') ||
                             document.body;

        log('Starting MutationObserver on:', liveContainer.tagName);

        observer = new MutationObserver((mutations) => {
            // Re-check for Live page on SPA navigation
            if (!isLivePage) {
                if (checkIfLivePage()) {
                    isLivePage = true;
                    log('Live page detected (MutationObserver)');
                    connectWebSocket();
                }
                return;
            }

            let hasNewNodes = false;
            for (const mutation of mutations) {
                if (mutation.addedNodes.length > 0) {
                    hasNewNodes = true;
                    break;
                }
            }

            if (hasNewNodes) {
                const comments = scanForComments();
                processComments(comments);
            }
        });

        observer.observe(liveContainer, {
            childList: true,
            subtree: true
        });

        log('MutationObserver active');
    }

    function init() {
        log('=========================================');
        log('LiveDeck Instagram Bridge v4.0');
        log('URL:', window.location.href);

        isLivePage = checkIfLivePage();
        log('Live page:', isLivePage ? 'YES' : 'No (watching)');
        log('=========================================');

        if (isLivePage) {
            connectWebSocket();
        }

        setTimeout(() => {
            startObserver();
        }, 1500);

        // Watch for SPA navigation
        let lastUrl = location.href;
        new MutationObserver(() => {
            const url = location.href;
            if (url !== lastUrl) {
                lastUrl = url;
                log('Page changed:', url);
                isLivePage = checkIfLivePage();

                if (isLivePage) {
                    log('Navigated to Live page');
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
    window.__livedeckBridge = {
        scan: () => {
            const comments = scanForComments();
            console.log('Comments found:', comments);
            return comments;
        },
        send: sendMessage,
        status: () => ({
            connected: isConnected,
            wsState: ws?.readyState,
            seenCount: SEEN_COMMENTS.size,
            isLive: isLivePage,
            url: window.location.href
        }),
        forceSend: () => {
            const comments = scanForComments();
            comments.forEach(c => {
                const hash = createCommentHash(c.username, c.text);
                sendMessage({
                    type: 'chat',
                    platform: 'instagram',
                    username: c.username,
                    displayName: c.username,
                    avatarUrl: null,
                    text: c.text,
                    externalId: `ig-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                    timestamp: Date.now()
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
