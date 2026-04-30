/**
 * OrderDeck Chat Bridge - Background Service Worker
 * Manages extension status badge and periodically checks OrderDeck connection.
 */

let isConnected = false;
let checkInterval = null;

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

// Extension installed
chrome.runtime.onInstalled.addListener(() => {
    console.log('[OrderDeck Bridge] Extension loaded');
    startPeriodicCheck();
});

// Extension started (browser launch)
chrome.runtime.onStartup.addListener(() => {
    startPeriodicCheck();
});

// Run immediately on service worker activation
startPeriodicCheck();
