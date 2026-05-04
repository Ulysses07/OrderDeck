/**
 * OrderDeck Chat Bridge — Selector Registry (extension-side facade)
 *
 * Content scripts no longer hard-code DOM selectors. They ask this module:
 *
 *     OrderDeckSelectors.get('instagram', 'comments.primaryContainers')
 *     OrderDeckSelectors.list('facebook', 'observerTarget')
 *     OrderDeckSelectors.validators('tiktok')
 *
 * The bundle resolution order (highest priority first):
 *
 *   1. chrome.storage.local['__orderdeck_selectors'] — populated by
 *      background.js after a successful refresh from the license server.
 *      This is what makes hot-fixes possible without an extension reinstall.
 *   2. selectors.bundled.json — shipped inside the extension package,
 *      synced with the C# constant in OrderDeck.LicenseServer at build
 *      time. Covers fresh installs and offline / VPS-down operation.
 *   3. Hard-coded <em>last-resort</em> fallbacks inside each content
 *      script's existing 3-strategy scan logic. Never reached unless the
 *      bundle itself is missing keys (schema mismatch).
 *
 * Bundle is loaded synchronously at content-script start so the first scan
 * pass already has the right selectors. The async storage read happens in
 * parallel; when it resolves we swap the active bundle in-place and notify
 * subscribers (chat-bridge-core re-arms its observer with the new values).
 */

(function (global) {
    'use strict';

    if (global.OrderDeckSelectors) return; // idempotent — survives reinjection.

    const STORAGE_KEY = '__orderdeck_selectors';

    // The bundled JSON is shipped as a web-accessible resource inside the
    // extension. Synchronous fetch isn't possible at content-script time, so
    // we kick off an async load and a chrome.storage.local read in parallel
    // and use whichever resolves first / has higher priority. Until both
    // complete the registry returns null on get(), and content scripts
    // gracefully short-circuit (their fallback strategies still work).
    let activeBundle = null;
    let bundleSource = 'pending'; // 'storage' | 'bundled' | 'pending'
    const subscribers = new Set();

    function getNested(obj, dotPath) {
        if (!obj) return undefined;
        const parts = dotPath.split('.');
        let cur = obj;
        for (const p of parts) {
            if (cur == null) return undefined;
            cur = cur[p];
        }
        return cur;
    }

    function applyBundle(bundle, source) {
        if (!bundle || typeof bundle !== 'object') return;
        if (typeof bundle.schemaVersion !== 'number') return;
        if (bundle.schemaVersion !== 1) {
            // Future schema — refuse to load so we don't crash on missing
            // fields. Fallback to bundled / hard-coded.
            console.warn('[OrderDeck] Unknown selector schema version:', bundle.schemaVersion);
            return;
        }
        // Storage wins over bundled but never goes backwards in time.
        if (activeBundle && bundleSource === 'storage' && source === 'bundled') return;
        activeBundle = bundle;
        bundleSource = source;
        for (const fn of subscribers) {
            try { fn(bundle); } catch (e) { console.error('[OrderDeck] selector subscriber threw', e); }
        }
    }

    /**
     * Loads the packaged fallback. Called on every content-script init —
     * the JSON is small (<5 KB) so the cost is negligible.
     */
    async function loadBundled() {
        try {
            const url = chrome.runtime.getURL('selectors.bundled.json');
            const resp = await fetch(url);
            if (!resp.ok) throw new Error('http ' + resp.status);
            const json = await resp.json();
            applyBundle(json, 'bundled');
        } catch (err) {
            console.warn('[OrderDeck] failed to load bundled selectors', err);
        }
    }

    async function loadFromStorage() {
        try {
            if (!chrome?.storage?.local) return;
            const result = await chrome.storage.local.get(STORAGE_KEY);
            if (result && result[STORAGE_KEY]) {
                applyBundle(result[STORAGE_KEY], 'storage');
            }
        } catch (err) {
            // chrome.storage may throw if the extension was reloaded mid-flight.
            console.debug('[OrderDeck] selector storage read failed', err);
        }
    }

    // Listen for background-driven refreshes — when license server returns
    // new selectors, background.js writes to storage and broadcasts via
    // chrome.runtime.onMessage. We re-read storage on that signal so every
    // open tab picks up the change without a page reload.
    if (chrome?.runtime?.onMessage) {
        chrome.runtime.onMessage.addListener((msg) => {
            if (msg && msg.type === 'orderdeck:selectorsUpdated') {
                loadFromStorage();
            }
        });
    }

    // Also react to direct storage edits (e.g. another tab refreshed first
    // and wrote storage; chrome fires onChanged in every other context).
    if (chrome?.storage?.onChanged) {
        chrome.storage.onChanged.addListener((changes, area) => {
            if (area !== 'local') return;
            if (changes[STORAGE_KEY] && changes[STORAGE_KEY].newValue) {
                applyBundle(changes[STORAGE_KEY].newValue, 'storage');
            }
        });
    }

    // Kick both loaders. Whichever resolves first wins until storage hops in.
    loadBundled();
    loadFromStorage();

    global.OrderDeckSelectors = {
        /**
         * Returns the deep-resolved value at <code>platform.dotPath</code>,
         * or undefined if the bundle hasn't loaded or the path is missing.
         */
        get(platform, dotPath) {
            const platformBundle = activeBundle?.platforms?.[platform];
            return getNested(platformBundle, dotPath);
        },

        /**
         * Convenience for arrays — returns [] when missing so callers can
         * forEach without null checks.
         */
        list(platform, dotPath) {
            const v = this.get(platform, dotPath);
            return Array.isArray(v) ? v : [];
        },

        /**
         * Whole validator object — used by isValidComment helpers.
         */
        validators(platform) {
            return this.get(platform, 'validators') || {};
        },

        /**
         * Source diagnostic ('storage' / 'bundled' / 'pending'). Surfaced
         * by chat-bridge-core for debug.status() output.
         */
        source() { return bundleSource; },

        /** SchemaVersion of the active bundle (1 today). */
        schemaVersion() { return activeBundle?.schemaVersion ?? null; },

        /**
         * Subscribe to bundle changes — chat-bridge-core uses this to
         * re-arm its observer when the live registry rotates.
         */
        onUpdate(fn) {
            subscribers.add(fn);
            return () => subscribers.delete(fn);
        },
    };
})(self);
