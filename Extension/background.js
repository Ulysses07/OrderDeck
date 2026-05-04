/**
 * OrderDeck Chat Bridge - Background Service Worker
 * Manages extension status badge, periodically checks OrderDeck connection,
 * and refreshes the DOM selector registry from the license server so that
 * silent platform-side DOM changes can be patched centrally without forcing
 * users to reinstall the extension.
 */

let isConnected = false;
let checkInterval = null;

// ─── Selector registry refresh ──────────────────────────────────────────
// The license server hosts the canonical selector bundle at
// https://license.orderdeckapp.com/api/v1/extension/selectors. We poll
// every ~10 minutes via chrome.alarms (timers don't survive MV3 service
// worker eviction). On a 200 with a new ETag, the body is written into
// chrome.storage.local under SELECTOR_STORAGE_KEY and every chat tab is
// notified so its content script can swap selectors live.
const SELECTORS_URL = 'https://license.orderdeckapp.com/api/v1/extension/selectors';
const SELECTOR_STORAGE_KEY = '__orderdeck_selectors';
const SELECTOR_ETAG_KEY = '__orderdeck_selectors_etag';
const SELECTOR_REFRESH_ALARM = 'orderdeck:selectorRefresh';
const SELECTOR_REFRESH_PERIOD_MIN = 10;

// Update the badge to reflect connection state
function updateBadge(connected) {
    isConnected = connected;

    if (connected) {
        chrome.action.setBadgeText({ text: '●' });
        chrome.action.setBadgeBackgroundColor({ color: '#22c55e' }); // Green
    } else {
        chrome.action.setBadgeText({ text: '○' });
        chrome.action.setBadgeBackgroundColor({ color: '#ef4444' }); // Red
    }
}

/**
 * Probe the OrderDeck WebSocket bridge to check whether it is reachable.
 * Uses the /extension endpoint (same one the content script connects to).
 */
function checkConnection() {
    try {
        const ws = new WebSocket('ws://localhost:4748/extension');
        const timeout = setTimeout(() => {
            try { ws.close(); } catch (e) {}
            updateBadge(false);
        }, 3000);

        ws.onopen = () => {
            clearTimeout(timeout);
            updateBadge(true);
            try { ws.close(); } catch (e) {}
        };

        ws.onerror = () => {
            clearTimeout(timeout);
            updateBadge(false);
        };
    } catch (e) {
        updateBadge(false);
    }
}

// Receive messages from content scripts
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    switch (message.action) {
        case 'setConnected':
            updateBadge(message.connected);
            break;
        case 'getStatus':
            sendResponse({ connected: isConnected });
            break;
    }
    return true;
});

// Start periodic connection check (every 30 seconds)
function startPeriodicCheck() {
    checkConnection();
    if (checkInterval) clearInterval(checkInterval);
    checkInterval = setInterval(checkConnection, 30000);
}

/**
 * Fetches the canonical selector bundle, sending If-None-Match when we
 * already have a cached ETag. On 304 we do nothing (cache stays valid);
 * on 200 we persist the body, save the new ETag, and broadcast a refresh
 * message so every open chat tab can re-arm its observer.
 *
 * Failure modes are non-fatal — content scripts already have selectors.bundled.json
 * and last-known-good storage to fall back on.
 */
async function refreshSelectorsFromServer() {
    try {
        const cached = await chrome.storage.local.get([SELECTOR_STORAGE_KEY, SELECTOR_ETAG_KEY]);
        const headers = {};
        if (cached[SELECTOR_ETAG_KEY]) {
            headers['If-None-Match'] = cached[SELECTOR_ETAG_KEY];
        }

        const resp = await fetch(SELECTORS_URL, {
            method: 'GET',
            headers,
            credentials: 'omit',
        });

        if (resp.status === 304) return; // cache still valid
        if (!resp.ok) {
            console.warn('[OrderDeck Bridge] Selector refresh HTTP', resp.status);
            return;
        }

        const body = await resp.json();
        if (!body || typeof body !== 'object' || body.schemaVersion !== 1) {
            console.warn('[OrderDeck Bridge] Unexpected selector schema', body?.schemaVersion);
            return;
        }

        const etag = resp.headers.get('etag') || '';
        await chrome.storage.local.set({
            [SELECTOR_STORAGE_KEY]: body,
            [SELECTOR_ETAG_KEY]: etag,
        });

        // Broadcast to every chat tab. We can't easily address content
        // scripts directly, so iterate the tabs that match our host
        // permissions and ignore failures (some tabs may not have the
        // content script loaded yet, e.g. chrome:// pages).
        try {
            const tabs = await chrome.tabs.query({
                url: [
                    '*://*.instagram.com/*',
                    '*://*.tiktok.com/*',
                    '*://*.facebook.com/*',
                ],
            });
            for (const t of tabs) {
                chrome.tabs.sendMessage(t.id, { type: 'orderdeck:selectorsUpdated' })
                    .catch(() => {/* tab without our content script */});
            }
        } catch (e) {
            console.debug('[OrderDeck Bridge] tab broadcast failed', e);
        }

        console.log('[OrderDeck Bridge] Selectors refreshed,', body.publishedAt);
    } catch (err) {
        console.debug('[OrderDeck Bridge] selector refresh failed', err);
    }
}

function startSelectorRefresh() {
    // chrome.alarms is the MV3-safe periodic timer (setTimeout/setInterval
    // die with the service worker; alarms wake it back up).
    if (chrome.alarms) {
        chrome.alarms.create(SELECTOR_REFRESH_ALARM, {
            periodInMinutes: SELECTOR_REFRESH_PERIOD_MIN,
        });
    }
    // Also kick once on startup so a fresh install / browser launch picks
    // up any post-bundle changes immediately.
    refreshSelectorsFromServer();
}

if (chrome.alarms) {
    chrome.alarms.onAlarm.addListener((alarm) => {
        if (alarm.name === SELECTOR_REFRESH_ALARM) refreshSelectorsFromServer();
    });
}

// Extension installed
chrome.runtime.onInstalled.addListener(() => {
    console.log('[OrderDeck Bridge] Extension loaded');
    startPeriodicCheck();
    startSelectorRefresh();
});

// Extension started (browser launch)
chrome.runtime.onStartup.addListener(() => {
    startPeriodicCheck();
    startSelectorRefresh();
});

// Run immediately on service worker activation
startPeriodicCheck();
startSelectorRefresh();
