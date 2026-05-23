/**
 * OrderDeck Chat Bridge — Facebook Adapter
 *
 * Strategies ported from UniCast's working content-facebook.js v2.0:
 * Facebook obfuscates and rotates class names aggressively, so we
 * anchor on aria-label + span[dir="auto"] which are far more stable.
 *
 * Live-page detection is intentionally lax — FB's live UI doesn't
 * expose a single reliable "I'm live" signal across all surfaces (Live
 * sidebar in News Feed vs. /watch live vs. /{user}/videos/{id} live).
 * We treat every facebook.com page as potentially scrapeable; if there
 * are no chat rows, scanForComments() returns empty and nothing flows.
 *
 * Selector strings come from OrderDeckSelectors so DOM rotations can be
 * patched centrally — see selector-registry.js + the license-server endpoint.
 */

(function () {
    'use strict';

    const PLATFORM = 'facebook';

    const HARD_FALLBACK = {
        primaryContainers: '[aria-label*="yorum" i], [aria-label*="comment" i]',
        primaryRowItems: 'span[dir="auto"]',
        secondaryContainers: ['div[role="article"]'],
        observerTarget: ['[role="complementary"]', '[role="main"]'],
        validators: {
            usernameMaxLength: 50,
            messageMaxLength: 1000,
            uiTextBlocklist: [
                'beğen', 'like', 'yanıtla', 'reply', 'paylaş', 'share',
                'gizle', 'hide', 'bildir', 'report', 'sabitle', 'pin',
                'yorum yap', 'comment', 'görüntüle', 'view',
                'canlı', 'live', 'izliyor', 'watching', 'izleyici', 'viewers',
                'mesaj gönder', 'send message',
            ],
            timeStringRegex: /^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$/i,
            urlShapedUsernameDenied: true,
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
        // FB has no single reliable "live" signal — let the scanner run on
        // every page; empty results are cheap. Server schema can flip this
        // back on via isLivePage.alwaysTrue (defaults to true in our bundle).
        const livePageCfg = self.OrderDeckSelectors?.get(PLATFORM, 'isLivePage');
        if (!livePageCfg) return true; // bundle not loaded yet — be permissive
        return livePageCfg.alwaysTrue !== false;
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
            urlShapedUsernameDenied: v.urlShapedUsernameDenied !== false,
        };
    }

    function isValidComment(username, message) {
        const v = getValidators();
        if (!username || !message) return false;
        if (username.length === 0 || username.length > v.usernameMaxLength) return false;
        if (message.length === 0 || message.length > v.messageMaxLength) return false;
        if (username === message) return false;

        const userLower = username.toLowerCase();
        const msgLower = message.toLowerCase();
        if (v.uiTextBlocklist.includes(userLower)) return false;
        if (v.uiTextBlocklist.includes(msgLower)) return false;

        if (v.timeStringRegex && v.timeStringRegex.test(message)) return false;

        if (v.urlShapedUsernameDenied) {
            if (username.includes('http') || username.includes('www.')) return false;
        }
        return true;
    }

    function pushIfNew(list, seen, username, message, source, element) {
        if (!isValidComment(username, message)) return;
        if (element) {
            if (seen.has(element)) return;
            seen.add(element);
        } else {
            const key = `${username}|${message}`;
            if (seen.has(key)) return;
            seen.add(key);
        }
        list.push({ username, text: message, source, element });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        const primaryContainers = selOrFallback(
            'comments.primaryContainers', HARD_FALLBACK.primaryContainers);
        const primaryRowItems = selOrFallback(
            'comments.primaryRowItems', HARD_FALLBACK.primaryRowItems);
        const secondaryContainers = listOrFallback(
            'comments.secondaryContainers', HARD_FALLBACK.secondaryContainers);

        // Strategy 1 (primary) — aria-label flagged comment containers.
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

        // Strategy 2 — div[role="article"] containers.
        if (comments.length === 0) {
            for (const sel of secondaryContainers) {
                document.querySelectorAll(sel).forEach(article => {
                    const spans = article.querySelectorAll(primaryRowItems);
                    if (spans.length >= 2) {
                        pushIfNew(comments, seen,
                            spans[0]?.textContent?.trim(),
                            spans[1]?.textContent?.trim(),
                            sel,
                            article);
                    }
                });
            }
        }

        // Strategy 3 (fallback) — any div whose direct children are 2+
        // span[dir="auto"] elements. Pure logic, stays hard-coded.
        if (comments.length === 0) {
            document.querySelectorAll('div').forEach(div => {
                const childSpans = Array.from(div.children).filter(
                    el => el.tagName === 'SPAN' && el.getAttribute('dir') === 'auto'
                );
                if (childSpans.length >= 2) {
                    pushIfNew(comments, seen,
                        childSpans[0]?.textContent?.trim(),
                        childSpans[1]?.textContent?.trim(),
                        'span-pair',
                        div);
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
        externalIdPrefix: 'fb',
        debugLabel: 'OrderDeck Facebook',
        scanForComments,
        checkIfLivePage,
        getObserverTarget
    });
})();
