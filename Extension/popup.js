/**
 * OrderDeck Chat Bridge - Popup Script
 */

document.addEventListener('DOMContentLoaded', async () => {
    const statusDot = document.getElementById('statusDot');
    const statusText = document.getElementById('statusText');
    const fbToggle = document.getElementById('fbScraperToggle');

    // Facebook scraper toggle — lets users opt out of the DOM scraper when
    // they're using the OrderDeck Graph API ingest, so the two paths don't
    // emit duplicate messages. Default true preserves legacy behavior.
    chrome.storage.local.get({ fbScraperEnabled: true }, (cfg) => {
        if (cfg.fbScraperEnabled) fbToggle.classList.add('on');
    });
    fbToggle.addEventListener('click', () => {
        const next = !fbToggle.classList.contains('on');
        fbToggle.classList.toggle('on', next);
        chrome.storage.local.set({ fbScraperEnabled: next });
    });

    function setStatus(connected) {
        if (connected) {
            statusDot.classList.add('connected');
            statusText.textContent = 'Connected to OrderDeck';
        } else {
            statusDot.classList.remove('connected');
            statusText.textContent = 'OrderDeck not running';
        }
    }

    // Probe the bridge directly via WebSocket (most reliable)
    try {
        const ws = new WebSocket('ws://localhost:4748/extension');
        const timeout = setTimeout(() => {
            try { ws.close(); } catch (e) {}
            setStatus(false);
        }, 3000);

        ws.onopen = () => {
            clearTimeout(timeout);
            setStatus(true);
            ws.close();
        };

        ws.onerror = () => {
            clearTimeout(timeout);
            setStatus(false);
        };
    } catch (e) {
        setStatus(false);
    }
});
