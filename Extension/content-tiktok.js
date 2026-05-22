/**
 * OrderDeck Chat Bridge — TikTok Adapter
 *
 * TikTok exposes stable data-e2e attributes on chat rows when available,
 * with class-name and child-span fallbacks for when the experiment flag is off.
 *
 * Selectors are sourced from OrderDeckSelectors so DOM rotations can be
 * patched centrally — see selector-registry.js + the license-server endpoint.
 */

(function () {
    'use strict';

    const PLATFORM = 'tiktok';

    const HARD_FALLBACK = {
        livePageDom: ['[data-e2e="chat-list"]', '[class*="ChatList"]'],
        primaryContainers: '[data-e2e="chat-message"]',
        primaryRowItems: '[data-e2e="comment-username"], [data-e2e="chat-username"]',
        messageItem: '[data-e2e="comment-text"], [data-e2e="chat-text"]',
        secondaryContainers: [
            '[class*="DivCommentItemContainer"]',
            '[class*="comment-item"]',
            '[class*="ChatMessage"]',
        ],
        observerTarget: ['[data-e2e="chat-list"]', '[class*="ChatList"]', '[role="main"]'],
        validators: {
            usernameMaxLength: 50,
            messageMaxLength: 1000,
            uiTextBlocklist: [
                'live', 'follow', 'share', 'gift', 'like', 'comment', 'send',
                'rose', 'viewers', 'watching', 'joined', 'top', 'gifts', 'chat', 'settings',
            ],
        },
    };

    function selOrFallback(dotPath, fallback) {
        const v = self.OrderDeckSelectors?.get(PLATFORM, dotPath);
        return (v ?? fallback);
    }

    function listOrFallback(dotPath, fallback) {
        const v = self.OrderDeckSelectors?.list(PLATFORM, dotPath);
        return (v && v.length > 0) ? v : fallback;
    }

    function extractStreamerHandle() {
        const m = window.location.pathname.match(/@([^/]+)/);
        return m ? m[1] : 'unknown';
    }

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        const urlPatterns = listOrFallback('isLivePage.urlPatterns', ['/live']);
        for (const p of urlPatterns) {
            if (p && url.includes(p)) return true;
        }
        const domSels = listOrFallback('isLivePage.domSelectors', HARD_FALLBACK.livePageDom);
        for (const sel of domSels) {
            try { if (document.querySelector(sel)) return true; }
            catch { /* malformed — skip */ }
        }
        return false;
    }

    function cleanUsername(u) {
        if (!u) return 'unknown';
        return u.replace(/^@/, '').replace(/:$/, '').trim();
    }

    function getValidators() {
        const v = self.OrderDeckSelectors?.validators(PLATFORM);
        if (!v || !v.uiTextBlocklist) return HARD_FALLBACK.validators;
        return {
            usernameMaxLength: v.usernameMaxLength ?? 50,
            messageMaxLength: v.messageMaxLength ?? 1000,
            uiTextBlocklist: v.uiTextBlocklist,
        };
    }

    function isValidComment(username, message) {
        const v = getValidators();
        if (!username || !message) return false;
        if (username.length === 0 || username.length > v.usernameMaxLength) return false;
        if (message.length === 0 || message.length > v.messageMaxLength) return false;
        if (username === message) return false;
        if (v.uiTextBlocklist.includes(username.toLowerCase())) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source, element) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({ username: cleanUsername(username), text: message, source, element });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        const primaryContainers = selOrFallback(
            'comments.primaryContainers', HARD_FALLBACK.primaryContainers);
        const primaryRowItems = selOrFallback(
            'comments.primaryRowItems', HARD_FALLBACK.primaryRowItems);
        const messageItem = selOrFallback(
            'comments.messageItem', HARD_FALLBACK.messageItem);

        // Strategy 1 — data-e2e attributes (precise; available in newer UI).
        document.querySelectorAll(primaryContainers).forEach(item => {
            const u = item.querySelector(primaryRowItems)?.textContent?.trim();
            const t = item.querySelector(messageItem)?.textContent?.trim();
            pushIfNew(comments, seen, u, t, 'data-e2e', item);
        });

        // Strategy 2 — class-name suffix selectors.
        if (comments.length === 0) {
            const selectors = listOrFallback(
                'comments.secondaryContainers', HARD_FALLBACK.secondaryContainers);
            for (const sel of selectors) {
                document.querySelectorAll(sel).forEach(item => {
                    const spans = item.querySelectorAll('span');
                    if (spans.length >= 2) {
                        pushIfNew(comments, seen,
                            spans[0]?.textContent?.trim(),
                            spans[1]?.textContent?.trim(),
                            sel,
                            item);
                    }
                });
            }
        }

        // Strategy 3 — chat-list children.
        if (comments.length === 0) {
            const observerTargets = listOrFallback(
                'observerTarget', HARD_FALLBACK.observerTarget);
            // First two entries are typical chat-list candidates.
            const list = document.querySelector(observerTargets[0]) ||
                         document.querySelector(observerTargets[1] || '');
            if (list) {
                Array.from(list.children).forEach(item => {
                    const spans = item.querySelectorAll('span');
                    if (spans.length >= 2) {
                        pushIfNew(comments, seen,
                            spans[0]?.textContent?.trim(),
                            spans[1]?.textContent?.trim(),
                            'chatlist-child',
                            item);
                    }
                });
            }
        }

        return comments;
    }

    function getObserverTarget() {
        const targets = listOrFallback('observerTarget', HARD_FALLBACK.observerTarget);
        for (const sel of targets) {
            try {
                const el = document.querySelector(sel);
                if (el) return el;
            } catch { /* malformed — skip */ }
        }
        return document.body;
    }

    OrderDeckChatBridge.start({
        platform: PLATFORM,
        externalIdPrefix: 'tt',
        debugLabel: 'OrderDeck TikTok',
        scanForComments,
        checkIfLivePage,
        getObserverTarget,
        getStreamerHandle: extractStreamerHandle
    });
})();
