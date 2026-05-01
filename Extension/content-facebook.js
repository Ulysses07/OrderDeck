/**
 * OrderDeck Chat Bridge — Facebook Adapter
 *
 * FB Live runs on facebook.com/{user}/live, /watch/live, and /{user}/videos/{id}.
 * Class names are heavily obfuscated and rotate, so we lean on aria-label /
 * role attributes and structural fallbacks. Best-effort — FB DOM is the most
 * volatile of the five supported platforms.
 */

(function () {
    'use strict';

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        if (url.includes('/live')) return true;
        if (url.includes('/videos/') || url.includes('/watch')) {
            // Live indicator badge: red "LIVE" pill or aria-label
            if (document.querySelector('[aria-label*="Live broadcast" i]')) return true;
            if (document.querySelector('[aria-label*="canlı yayın" i]')) return true;
            if (document.querySelector('[data-pagelet*="LiveVideo"]')) return true;
        }
        // Fallback: comment composer with the live-specific placeholder
        if (document.querySelector('[aria-label*="Yorum yaz" i]') ||
            document.querySelector('[aria-label*="Write a comment" i]')) {
            // Only treat as live if there's also a video element playing
            return !!document.querySelector('video');
        }
        return false;
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 80) return false;
        if (message.length === 0 || message.length > 2000) return false;
        if (username === message) return false;
        if (username.includes('\n')) return false;

        const uiTexts = [
            'like', 'reply', 'share', 'comment', 'follow', 'react',
            'beğen', 'yanıtla', 'paylaş', 'yorum', 'takip et', 'tepki'
        ];
        if (uiTexts.includes(username.toLowerCase())) return false;

        // Drop relative-time strings that FB sometimes renders as headers ("2 dk", "1h")
        if (/^\d+\s*(dk|sa|gün|sn|m|h|d|s|ay|yıl|min|hr|sec)$/i.test(message)) return false;
        // Drop reaction counts like "5", "1.2K"
        if (/^\d+(\.\d+)?[KMB]?$/i.test(message)) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source, displayName) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({ username: username.trim(), text: message.trim(), source, displayName: displayName ?? username });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        // Strategy 1 — explicit comment role / aria-label container.
        // FB sometimes uses role="article" with an aria-label like "Comment by Jane Doe".
        document.querySelectorAll('[role="article"][aria-label*="Comment" i], [role="article"][aria-label*="Yorum" i]').forEach(item => {
            const label = item.getAttribute('aria-label') || '';
            // "Comment by {name}" / "Yorum: {name}" — extract name from label
            const nameFromLabel = label.replace(/^(Comment by|Yorum yapan|Yorum:)\s*/i, '').trim();
            // Body: try to find a span block that isn't inside a nested article (replies)
            const bodyEl = item.querySelector('div[dir="auto"], span[dir="auto"]');
            const body = bodyEl?.textContent?.trim();
            if (nameFromLabel && body) {
                pushIfNew(comments, seen, nameFromLabel, body, 'aria-article', nameFromLabel);
            }
        });

        // Strategy 2 — strong/anchor pattern: <strong>{name}</strong> + sibling text.
        if (comments.length === 0) {
            document.querySelectorAll('strong, a[role="link"][tabindex="0"]').forEach(nameEl => {
                const username = nameEl.textContent?.trim();
                if (!username || username.length > 80) return;
                // Walk up to a likely comment row, then look for the message body
                const row = nameEl.closest('div[role="article"], li, [class]');
                if (!row) return;
                const bodyCandidates = row.querySelectorAll('div[dir="auto"]');
                for (const b of bodyCandidates) {
                    const text = b.textContent?.trim();
                    if (text && text !== username) {
                        pushIfNew(comments, seen, username, text, 'strong-sibling');
                        break;
                    }
                }
            });
        }

        // Strategy 3 — data-ad-rendering-role hint (FB sometimes tags comment messages).
        if (comments.length === 0) {
            document.querySelectorAll('[data-ad-rendering-role="comment_message"]').forEach(msg => {
                const text = msg.textContent?.trim();
                // Username likely lives in a sibling/parent <strong>
                const row = msg.closest('div[role="article"], li');
                const nameEl = row?.querySelector('strong, a[role="link"]');
                const username = nameEl?.textContent?.trim();
                pushIfNew(comments, seen, username, text, 'data-ad-rendering');
            });
        }

        return comments;
    }

    function getObserverTarget() {
        // Sidebar / dialog hosting the comment thread
        return document.querySelector('[role="complementary"]') ||
               document.querySelector('[role="dialog"]') ||
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
