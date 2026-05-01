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
 */

(function () {
    'use strict';

    function checkIfLivePage() {
        // Don't gate by URL — FB Live runs from many different paths and
        // a strict gate misses real live streams. Letting the scanner run
        // costs ~one DOM query per 500ms when there are no comments.
        return true;
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 50) return false;
        if (message.length === 0 || message.length > 1000) return false;
        if (username === message) return false;

        // FB UI strings (TR + EN)
        const uiTexts = [
            'beğen', 'like', 'yanıtla', 'reply', 'paylaş', 'share',
            'gizle', 'hide', 'bildir', 'report', 'sabitle', 'pin',
            'yorum yap', 'comment', 'görüntüle', 'view',
            'canlı', 'live', 'izliyor', 'watching', 'izleyici', 'viewers',
            'mesaj gönder', 'send message'
        ];
        const userLower = username.toLowerCase();
        const msgLower = message.toLowerCase();
        if (uiTexts.includes(userLower)) return false;
        if (uiTexts.includes(msgLower)) return false;

        // Skip pure relative-time strings ("1d", "2sa", "3dk")
        if (/^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$/i.test(message)) return false;

        // Skip URL-shaped usernames
        if (username.includes('http') || username.includes('www.')) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({ username, text: message, source });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        // Strategy 1 (primary) — aria-label flagged comment containers.
        // FB tags comment rows with "yorum" (TR) / "comment" (EN) in aria-label.
        // Inside, the first two span[dir="auto"] are username + message;
        // any third span is the relative timestamp.
        document.querySelectorAll('[aria-label*="yorum" i], [aria-label*="comment" i]').forEach(el => {
            const spans = el.querySelectorAll('span[dir="auto"]');
            if (spans.length >= 2) {
                pushIfNew(comments, seen,
                    spans[0]?.textContent?.trim(),
                    spans[1]?.textContent?.trim(),
                    'aria-label');
            }
        });

        // Strategy 2 — div[role="article"] containers (FB wraps each comment
        // in an article role; aria-label may be absent on some surfaces).
        if (comments.length === 0) {
            document.querySelectorAll('div[role="article"]').forEach(article => {
                const spans = article.querySelectorAll('span[dir="auto"]');
                if (spans.length >= 2) {
                    pushIfNew(comments, seen,
                        spans[0]?.textContent?.trim(),
                        spans[1]?.textContent?.trim(),
                        'article');
                }
            });
        }

        // Strategy 3 (fallback) — any div whose direct children are 2+
        // span[dir="auto"] elements. Loose; only fires when the first two
        // strategies returned nothing.
        if (comments.length === 0) {
            document.querySelectorAll('div').forEach(div => {
                const childSpans = Array.from(div.children).filter(
                    el => el.tagName === 'SPAN' && el.getAttribute('dir') === 'auto'
                );
                if (childSpans.length >= 2) {
                    pushIfNew(comments, seen,
                        childSpans[0]?.textContent?.trim(),
                        childSpans[1]?.textContent?.trim(),
                        'span-pair');
                }
            });
        }

        return comments;
    }

    function getObserverTarget() {
        // Sidebar / dialog hosts the comment thread on most Live UIs.
        return document.querySelector('[role="complementary"]') ||
               document.querySelector('[role="main"]') ||
               document.body;
    }

    OrderDeckChatBridge.start({
        platform: 'facebook',
        externalIdPrefix: 'fb',
        debugLabel: 'OrderDeck Facebook',
        scanForComments,
        checkIfLivePage,
        getObserverTarget
    });
})();
