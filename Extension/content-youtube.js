/**
 * OrderDeck Chat Bridge — YouTube Live Adapter
 *
 * YouTube renders its live chat in an iframe whose URL starts with
 * /live_chat. The manifest matches that URL pattern directly so this
 * script runs INSIDE the chat iframe, where the DOM is stable and
 * uses semantic <yt-live-chat-text-message-renderer> custom elements.
 *
 * Outside the live_chat frame this script does nothing — the watch page
 * itself is irrelevant for chat scraping.
 */

(function () {
    'use strict';

    function checkIfLivePage() {
        // We only inject into the chat iframe; if we got here, it's "live" by definition.
        // (For paused/replayed live chat, YT routes through a different URL.)
        const url = window.location.href.toLowerCase();
        return url.includes('/live_chat');
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 80) return false;
        if (message.length === 0 || message.length > 2000) return false;
        if (username === message) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source, displayName, avatarUrl) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({
            username: username.trim(),
            text: message.trim(),
            displayName: (displayName ?? username).trim(),
            avatarUrl: avatarUrl ?? null,
            source
        });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        // Strategy 1 — text-message rows (the bulk of live chat).
        document.querySelectorAll('yt-live-chat-text-message-renderer').forEach(item => {
            const username = item.querySelector('#author-name')?.textContent?.trim();
            const text = item.querySelector('#message')?.textContent?.trim();
            const avatar = item.querySelector('#author-photo img')?.src ?? null;
            pushIfNew(comments, seen, username, text, 'yt-text-message', username, avatar);
        });

        // Strategy 2 — paid-message / Super-Chat rows (still chat, with money attached).
        document.querySelectorAll('yt-live-chat-paid-message-renderer').forEach(item => {
            const username = item.querySelector('#author-name')?.textContent?.trim();
            const text = item.querySelector('#message')?.textContent?.trim() ?? '[Super Chat]';
            const avatar = item.querySelector('#author-photo img')?.src ?? null;
            pushIfNew(comments, seen, username, text, 'yt-paid-message', username, avatar);
        });

        // Strategy 3 — fallback for the membership-renderer + sticker-renderer rows
        // we don't dedicate a full strategy to.
        if (comments.length === 0) {
            document.querySelectorAll('[id="items"] > *').forEach(item => {
                const username = item.querySelector('#author-name')?.textContent?.trim();
                const text = item.querySelector('#message, #header-content-inner-column')?.textContent?.trim();
                pushIfNew(comments, seen, username, text, 'yt-generic-renderer');
            });
        }

        return comments;
    }

    function getObserverTarget() {
        // The list container inside the chat iframe.
        return document.querySelector('#items.yt-live-chat-item-list-renderer') ||
               document.querySelector('#chat-messages') ||
               document.body;
    }

    OrderDeckChatBridge.start({
        platform: 'youtube',
        externalIdPrefix: 'yt',
        debugLabel: 'OrderDeck YouTube',
        scanForComments,
        checkIfLivePage,
        getObserverTarget
    });
})();
