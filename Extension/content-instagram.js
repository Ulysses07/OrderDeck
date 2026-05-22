/**
 * OrderDeck Chat Bridge — Instagram Adapter
 *
 * IG DOM is class-obfuscated; we lean on aria-label and span structure
 * heuristics. Three scan strategies in precision order — first non-empty wins.
 *
 * The actual selector strings live in OrderDeckSelectors (see
 * selector-registry.js + selectors.bundled.json + license-server endpoint),
 * so DOM regressions can be patched centrally without forcing every operator
 * to reinstall the extension.
 */

(function () {
    'use strict';

    const PLATFORM = 'instagram';

    // Last-resort fallbacks — only reached if the registry hasn't loaded
    // (offline + no storage cache). Keep these in sync with selectors.bundled.json
    // so dev install still works on day one.
    const HARD_FALLBACK = {
        livePageDom: ['[aria-label*="Live" i]', '[aria-label*="Canlı" i]'],
        primaryContainers: '[aria-label*="yorum" i], [aria-label*="comment" i]',
        primaryRowItems: 'span[dir="auto"]',
        observerTarget: ['[role="main"]', 'section'],
        validators: {
            usernameMaxLength: 50,
            messageMaxLength: 1000,
            uiTextBlocklist: [
                'live', 'messages', 'share', 'like', 'comment', 'send', 'follow',
                'canlı', 'mesajlar', 'paylaş', 'beğen', 'yorum', 'gönder', 'takip et',
                'izliyor', 'watching', 'viewers', 'izleyici',
            ],
            timeStringRegex: /^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$/i,
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

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        const urlPatterns = listOrFallback('isLivePage.urlPatterns', ['/live']);
        for (const p of urlPatterns) {
            if (p && url.includes(p)) return true;
        }
        const domSels = listOrFallback('isLivePage.domSelectors', HARD_FALLBACK.livePageDom);
        for (const sel of domSels) {
            try {
                if (document.querySelector(sel)) return true;
            } catch { /* malformed selector — skip */ }
        }
        return false;
    }

    function getValidators() {
        const v = self.OrderDeckSelectors?.validators(PLATFORM);
        if (!v || !v.uiTextBlocklist) return HARD_FALLBACK.validators;
        const re = typeof v.timeStringRegex === 'string' && v.timeStringRegex.length > 0
            ? new RegExp(v.timeStringRegex, 'i')
            : null;
        return {
            usernameMaxLength: v.usernameMaxLength ?? 50,
            messageMaxLength: v.messageMaxLength ?? 1000,
            uiTextBlocklist: v.uiTextBlocklist,
            timeStringRegex: re,
        };
    }

    function isValidComment(username, message) {
        const v = getValidators();
        if (!username || !message) return false;
        if (username.length === 0 || username.length > v.usernameMaxLength) return false;
        if (message.length === 0 || message.length > v.messageMaxLength) return false;
        if (username.includes('\n')) return false;
        if (username === message) return false;

        if (v.uiTextBlocklist.includes(username.toLowerCase())) return false;
        if (v.timeStringRegex && v.timeStringRegex.test(message)) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source, element) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({
            username: username.replace('@', ''),
            text: message,
            source,
            // element: stable per-comment DOM node so the core can dedupe
            // by identity (WeakSet). Re-typing the same text creates a new
            // node → counted as a new order (live broadcast multi-buy).
            element,
        });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        const primaryContainers = selOrFallback(
            'comments.primaryContainers', HARD_FALLBACK.primaryContainers);
        const primaryRowItems = selOrFallback(
            'comments.primaryRowItems', HARD_FALLBACK.primaryRowItems);

        // Strategy 1 — aria-label flagged comment containers.
        document.querySelectorAll(primaryContainers).forEach(el => {
            const spans = el.querySelectorAll(primaryRowItems);
            if (spans.length >= 2) {
                pushIfNew(comments, seen,
                    spans[0]?.textContent?.trim(),
                    spans[1]?.textContent?.trim(),
                    'aria-label',
                    el);
            }
        });

        // Strategy 2 — divs with exactly 2 child spans (chat row pattern).
        // This is logic-shaped, not selector-shaped, so it stays hard-coded.
        if (comments.length === 0) {
            document.querySelectorAll('div').forEach(div => {
                const spans = Array.from(div.children).filter(el => el.tagName === 'SPAN');
                if (spans.length === 2) {
                    pushIfNew(comments, seen,
                        spans[0]?.textContent?.trim(),
                        spans[1]?.textContent?.trim(),
                        'div-2span',
                        div);
                }
            });
        }

        // Strategy 3 — sibling spans (loose fallback). No stable container
        // here — two adjacent <span>'s aren't a row in IG's sense. Pass
        // undefined element so the core falls back to text-hash dedupe.
        if (comments.length === 0) {
            document.querySelectorAll('span').forEach(span => {
                const text = span.textContent?.trim();
                const prev = span.previousElementSibling;
                if (prev?.tagName === 'SPAN' && text) {
                    pushIfNew(comments, seen, prev.textContent?.trim(), text, 'sibling-span', undefined);
                }
            });
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
        externalIdPrefix: 'ig',
        debugLabel: 'OrderDeck Instagram',
        scanForComments,
        checkIfLivePage,
        getObserverTarget
    });
})();
