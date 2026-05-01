/**
 * OrderDeck Chat Bridge — Instagram Adapter
 *
 * IG DOM is class-obfuscated; we lean on aria-label and span structure
 * heuristics. Three scan strategies in precision order — first non-empty wins.
 */

(function () {
    'use strict';

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        if (url.includes('/live')) return true;
        if (document.querySelector('[aria-label*="Live" i]')) return true;
        if (document.querySelector('[aria-label*="Canlı" i]')) return true;
        return false;
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
        if (uiTexts.includes(username.toLowerCase())) return false;
        // Drop "5 dk", "1 sa" timestamps that show up as fake messages.
        if (/^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$/i.test(message)) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({ username: username.replace('@', ''), text: message, source });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        // Strategy 1 — aria-label flagged comment containers.
        document.querySelectorAll('[aria-label*="yorum" i], [aria-label*="comment" i]').forEach(el => {
            const spans = el.querySelectorAll('span[dir="auto"]');
            if (spans.length >= 2) {
                pushIfNew(comments, seen,
                    spans[0]?.textContent?.trim(),
                    spans[1]?.textContent?.trim(),
                    'aria-label');
            }
        });

        // Strategy 2 — divs with exactly 2 child spans (chat row pattern).
        if (comments.length === 0) {
            document.querySelectorAll('div').forEach(div => {
                const spans = Array.from(div.children).filter(el => el.tagName === 'SPAN');
                if (spans.length === 2) {
                    pushIfNew(comments, seen,
                        spans[0]?.textContent?.trim(),
                        spans[1]?.textContent?.trim(),
                        'div-2span');
                }
            });
        }

        // Strategy 3 — sibling spans (loose fallback).
        if (comments.length === 0) {
            document.querySelectorAll('span').forEach(span => {
                const text = span.textContent?.trim();
                const prev = span.previousElementSibling;
                if (prev?.tagName === 'SPAN' && text) {
                    pushIfNew(comments, seen, prev.textContent?.trim(), text, 'sibling-span');
                }
            });
        }

        return comments;
    }

    function getObserverTarget() {
        return document.querySelector('[role="main"]') || document.querySelector('section') || document.body;
    }

    OrderDeckChatBridge.start({
        platform: 'instagram',
        externalIdPrefix: 'ig',
        debugLabel: 'OrderDeck Instagram',
        scanForComments,
        checkIfLivePage,
        getObserverTarget
    });
})();
