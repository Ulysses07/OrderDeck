/**
 * OrderDeck Chat Bridge — Facebook Background Keep-Alive
 *
 * Facebook pauses its live-comment feed (stops writing new comment nodes to
 * the DOM) when the tab is backgrounded — it listens to the Page Visibility
 * API and halts rendering on `document.hidden`. The DOM scraper then has
 * nothing to read until the operator refocuses the tab, at which point FB
 * flushes the backlog all at once ("toplu düşme").
 *
 * This shim runs in the MAIN world at document_start (before FB's own
 * scripts read visibility) and reports the page as permanently visible, so
 * FB keeps streaming comments into the DOM regardless of tab focus.
 *
 * Read-only: it only overrides client-side getters in this browser — no
 * extra network traffic, no automated actions. Validated 2026-06-26 on a
 * live broadcast (comments kept flowing while the tab was backgrounded).
 */
(function () {
    'use strict';

    const visible = (v) => ({ get: () => v, configurable: true });
    try { Object.defineProperty(document, 'hidden', visible(false)); } catch (e) { /* sealed */ }
    try { Object.defineProperty(document, 'webkitHidden', visible(false)); } catch (e) { /* sealed */ }
    try { Object.defineProperty(document, 'visibilityState', visible('visible')); } catch (e) { /* sealed */ }
    try { Object.defineProperty(document, 'webkitVisibilityState', visible('visible')); } catch (e) { /* sealed */ }

    // Swallow the event in the capture phase so FB's own listeners never hear
    // "you went hidden". The getter overrides above are the primary defense
    // (FB re-reads document.hidden when the event fires); this is belt-and-
    // suspenders for listeners that act purely on the event firing.
    const block = (e) => e.stopImmediatePropagation();
    document.addEventListener('visibilitychange', block, true);
    document.addEventListener('webkitvisibilitychange', block, true);
})();
