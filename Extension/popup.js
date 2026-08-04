/**
 * OrderDeck Chat Bridge - Popup Script
 */

document.addEventListener('DOMContentLoaded', async () => {
    const statusDot = document.getElementById('statusDot');
    const statusText = document.getElementById('statusText');

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
