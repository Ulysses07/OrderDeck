/**
 * OrderDeck Chat Bridge — Twitch Adapter
 *
 * Twitch chat lives on twitch.tv/{channel} (sidebar) and twitch.tv/popout/
 * {channel}/chat (popped out). DOM is stable thanks to data-a-target attrs.
 */

(function () {
    'use strict';

    function checkIfLivePage() {
        const url = window.location.href.toLowerCase();
        // Popout chat URL is always live-chat-bound.
        if (url.includes('/popout/') && url.includes('/chat')) return true;
        // Channel page — chat exists when the channel is live OR even when
        // offline (Twitch keeps the chat sidebar visible). We accept either,
        // since scraping an idle chat just yields no new messages.
        if (document.querySelector('.chat-list--default')) return true;
        if (document.querySelector('[role="log"]')) return true;
        if (document.querySelector('[data-test-selector="chat-room-component-layout"]')) return true;
        return false;
    }

    function isValidComment(username, message) {
        if (!username || !message) return false;
        if (username.length === 0 || username.length > 80) return false;
        if (message.length === 0 || message.length > 2000) return false;
        if (username === message) return false;
        return true;
    }

    function pushIfNew(list, seen, username, message, source, displayName) {
        if (!isValidComment(username, message)) return;
        const key = `${username}|${message}`;
        if (seen.has(key)) return;
        seen.add(key);
        list.push({
            username: username.trim().toLowerCase(),       // Twitch login is canonical lowercase
            displayName: (displayName ?? username).trim(),  // Capitalised display name preserved
            text: message.trim(),
            source
        });
    }

    function scanForComments() {
        const comments = [];
        const seen = new Set();

        // Strategy 1 — chat-line__message (current standard).
        document.querySelectorAll('.chat-line__message').forEach(item => {
            const nameEl = item.querySelector('.chat-author__display-name, [data-a-target="chat-message-username"]');
            const display = nameEl?.textContent?.trim();
            // Username (login) lives in data-a-user attribute on some clients
            const login = item.querySelector('[data-a-user]')?.getAttribute('data-a-user')?.trim() ?? display;

            // Body: assemble from text-fragments so emote alt text still flows.
            const bodyEl = item.querySelector('[data-a-target="chat-line-message-body"]') || item;
            const fragments = Array.from(bodyEl.querySelectorAll('.text-fragment, [data-a-target="chat-message-text"]'))
                .map(f => f.textContent?.trim())
                .filter(Boolean);
            // If no fragments, fall back to whole-body text minus the username prefix.
            let text = fragments.length > 0 ? fragments.join(' ') : bodyEl.textContent?.trim();
            if (text && display && text.startsWith(display)) {
                text = text.slice(display.length).replace(/^[:\s]+/, '');
            }

            pushIfNew(comments, seen, login, text, 'chat-line', display);
        });

        // Strategy 2 — data-test-selector for older clients.
        if (comments.length === 0) {
            document.querySelectorAll('[data-test-selector="chat-line-message"]').forEach(item => {
                const display = item.querySelector('[data-a-target="chat-message-username"]')?.textContent?.trim();
                const text = item.querySelector('[data-a-target="chat-message-text"]')?.textContent?.trim() ??
                             item.textContent?.trim();
                pushIfNew(comments, seen, display, text, 'data-test-selector', display);
            });
        }

        // Strategy 3 — generic [role="log"] children (last-resort).
        if (comments.length === 0) {
            const log = document.querySelector('[role="log"]');
            if (log) {
                Array.from(log.children).forEach(item => {
                    const display = item.querySelector('.chat-author__display-name')?.textContent?.trim();
                    const body = item.querySelector('[data-a-target="chat-line-message-body"]')?.textContent?.trim();
                    pushIfNew(comments, seen, display, body, 'role-log', display);
                });
            }
        }

        return comments;
    }

    function getObserverTarget() {
        return document.querySelector('.chat-scrollable-area__message-container') ||
               document.querySelector('[role="log"]') ||
               document.querySelector('.chat-list--default') ||
               document.body;
    }

    OrderDeckChatBridge.start({
        platform: 'twitch',
        externalIdPrefix: 'tw',
        debugLabel: 'OrderDeck Twitch',
        scanForComments,
        checkIfLivePage,
        getObserverTarget
    });
})();
