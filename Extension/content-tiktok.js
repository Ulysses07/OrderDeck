/**
 * OrderDeck Chat Bridge — TikTok Adapter
 *
 * TikTok exposes stable data-e2e attributes on chat rows when available,
 * with class-name and child-span fallbacks for when the experiment flag is off.
 */

(function () {
    'use strict';

    function extractStreamerHandle() {
        const m = window.location.pathname.match(/@([^/]+)/);
        return m ? m[1] : 'unknown';
    }

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        if (url.includes('/live')) return true;
        if (document.querySelector('[data-e2e="chat-list"]')) return true;
        if (document.querySelector('[class*="ChatList"]')) return true;
        return false;
    }

    function cleanUsername(u) {
        if (!u) return 'unknown';
        return u.replace(/^@/, '').replace(/:$/, '').trim();
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

    function pushIfNew(list, seen, username, message, source) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({ username: cleanUsername(username), text: message, source });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        // Strategy 1 — data-e2e attributes (precise; available in newer UI).
        document.querySelectorAll('[data-e2e="chat-message"]').forEach(item => {
            const u = item.querySelector('[data-e2e="comment-username"], [data-e2e="chat-username"]')?.textContent?.trim();
            const t = item.querySelector('[data-e2e="comment-text"], [data-e2e="chat-text"]')?.textContent?.trim();
            pushIfNew(comments, seen, u, t, 'data-e2e');
        });

        // Strategy 2 — class-name suffix selectors.
        if (comments.length === 0) {
            const selectors = [
                '[class*="DivCommentItemContainer"]',
                '[class*="comment-item"]',
                '[class*="ChatMessage"]'
            ];
            for (const sel of selectors) {
                document.querySelectorAll(sel).forEach(item => {
                    const spans = item.querySelectorAll('span');
                    if (spans.length >= 2) {
                        pushIfNew(comments, seen,
                            spans[0]?.textContent?.trim(),
                            spans[1]?.textContent?.trim(),
                            sel);
                    }
                });
            }
        }

        // Strategy 3 — chat-list children.
        if (comments.length === 0) {
            const list = document.querySelector('[data-e2e="chat-list"], [class*="ChatList"]');
            if (list) {
                Array.from(list.children).forEach(item => {
                    const spans = item.querySelectorAll('span');
                    if (spans.length >= 2) {
                        pushIfNew(comments, seen,
                            spans[0]?.textContent?.trim(),
                            spans[1]?.textContent?.trim(),
                            'chatlist-child');
                    }
                });
            }
        }

        return comments;
    }

    function getObserverTarget() {
        return document.querySelector('[data-e2e="chat-list"]') ||
               document.querySelector('[class*="ChatList"]') ||
               document.querySelector('[role="main"]') ||
               document.body;
    }

    OrderDeckChatBridge.start({
        platform: 'tiktok',
        externalIdPrefix: 'tt',
        debugLabel: 'OrderDeck TikTok',
        scanForComments,
        checkIfLivePage,
        getObserverTarget,
        // Expose the streamer handle for debug.status() — adapter can attach extras.
        getStreamerHandle: extractStreamerHandle
    });
})();
