/**
 * OrderDeck Chat Bridge — Shared Core
 *
 * WebSocket lifecycle, dedup cache, MutationObserver, periodic scan, debug
 * handle. Every platform-specific content script provides a small adapter
 * (scan + checkLive + observer-target + valid-comment helpers) and calls
 * `OrderDeckChatBridge.start(adapter)`.
 *
 * Extracted on 2026-05-01 when the platform count grew from 2 → 5.
 */

window.OrderDeckChatBridge = (function () {
    'use strict';

    /**
     * Platform izleyici-sayısı metnini ("1.234 izliyor", "1,2 B", "12 bin",
     * "523 watching") tam sayıya çevirir. TR ve EN biçimlerini, binlik
     * ayraçlarını ve kısaltma çarpanlarını (B/bin/K = ×1000, M/Mn = ×1e6)
     * destekler. Çözemezse null.
     */
    function parseViewerCount(text) {
        if (!text) return null;
        const t = String(text).toLowerCase().replace(/\u00a0/g, ' ').trim();
        const m = t.match(/(\d[\d.,]*)\s*(mn|bin|m|k|b)?/);
        if (!m) return null;
        const numRaw = m[1];
        const suffix = m[2];
        let mult = 1;
        if (suffix === 'k' || suffix === 'b' || suffix === 'bin') mult = 1000;
        else if (suffix === 'm' || suffix === 'mn') mult = 1000000;
        if (mult > 1) {
            // Çarpanlı biçim ondalıklıdır: "1,2" / "1.2" → 1.2
            const val = parseFloat(numRaw.replace(',', '.'));
            return isNaN(val) ? null : Math.round(val * mult);
        }
        // Çarpansız: tam sayı, binlik ayraçlarını (. , boşluk) at.
        const digits = numRaw.replace(/[.,\s]/g, '');
        const n = parseInt(digits, 10);
        return isNaN(n) ? null : n;
    }

    const WS_PORT = 4748;
    const RECONNECT_INTERVAL = 3000;
    const SCAN_INTERVAL = 200;
    // İzleyici sayısı sohbetten çok daha yavaş değişir — ayrı, seyrek döngü.
    const VIEWER_INTERVAL = 5000;
    // Observer-driven tarama için minimum aralık. MutationObserver arka planda
    // THROTTLE EDİLMEZ (setInterval/setTimeout edilir) — bu yüzden DOM mutasyonu
    // gelir gelmez anında tararız; ama churn'lü sayfalarda (FB) CPU'yu sınırlamak
    // için ardışık taramalar arasında en az bu kadar boşluk bırakırız. Periyodik
    // tarama yedek olarak kalır.
    const MIN_SCAN_GAP = 180;
    // Session-scoped dedupe: aynı (username, text) yayın boyunca bir kez
    // gönderilir. Önceki 5sn TTL implementasyonu regressionçü idi — Instagram
    // bir yorumu DOM'da 5sn'den uzun tuttuğunda extension onu "yeni" sanıp
    // yeniden gönderiyordu, WPF'te aynı yorum sürekli yenileniyor olarak
    // beliriyordu (2026-05-22 canlı yayında raporlandı).
    //
    // Map boyutu CACHE_LIMIT'e ulaşınca en eski insert edilen hash'ler
    // FIFO ile atılır (JS Map insertion order'ı korur) — uzun yayında
    // memory unbounded growth olmaz.
    const CACHE_LIMIT = 5000;

    // ── Bağlantı kopukken biriken yorumlar (outbox) ─────────────────────────
    // Köprü kapalıyken (WPF yeniden başlıyor, Velopack güncellemesi, port
    // takılması) MutationObserver çalışmaya devam eder: yorumlar taranır,
    // "görüldü" işaretlenir ve — outbox olmadan — sessizce kaybolurdu. Mezat
    // modelinde kaybolan yorum kaybolan siparişdir, üstelik operatör bunu
    // asla öğrenmezdi (tek iz DevTools konsolundaydı).
    //
    // Gönderilemeyen chat mesajları burada sıraya girer, bağlantı kurulunca
    // sırasıyla akıtılır. Sınır dolarsa EN ESKİ atılır: canlı yayında geçmişte
    // bir boşluk görmek, şimdiki akışı kaçırmaktan iyidir. Atılan sayısı
    // WPF'e "chat-dropped" mesajıyla bildirilir — kayıp sessiz kalmaz.
    const OUTBOX_LIMIT = 500;   // ~2dk yoğun akış (220 yorum/dk)

    // ── Stall watchdog (Instagram donması, 2026-07-15 logları) ──────────────
    // Instagram'ın web sayfası (hem /live izleyici hem yayıncı ekranı) uzun
    // oturumda (~30-45dk, liste binlerce satıra şişince) DOM'a yeni yorum
    // basmayı tamamen kesiyor: scans akıyor, observed sabitleniyor, sent=0.
    // IG UI'ında da yorumlar donuyor — yani kaynak IG render'ı; tek çare
    // operatörün elle yaptığı sayfa yenileme. Watchdog bunu otomatikleştirir:
    // canlı sayfada, DOM'da yeterli satır varken STALL_AFTER_MS boyunca tek
    // mesaj gönderilememişse önce adapter.stallRecovery.nudge() (chat'i dibe
    // scroll et), NUDGE_GRACE_MS içinde düzelmezse location.reload().
    const WATCHDOG_INTERVAL = 30_000;
    const STALL_AFTER_MS = 2 * 60_000;   // 2dk sessizlik = şüpheli (yoğun mezat akışında sessizlik olmuyor); yanlış alarm önce sadece scroll nudge yer
    const STALL_MIN_ROWS = 30;           // idle/boş sayfada tetiklenmesin
    const NUDGE_GRACE_MS = 60_000;
    const RELOAD_COOLDOWN_MS = 10 * 60_000; // reload döngüsü emniyeti
    const RELOAD_FLAG_KEY = '__orderdeck_watchdog_reload';
    const RELOAD_AT_KEY = '__orderdeck_watchdog_reload_at';

    /**
     * Start the bridge. `adapter` is required and must provide:
     *   platform           string  — short id sent to server ("instagram", "tiktok")
     *   externalIdPrefix   string  — short prefix for synthetic message ids ("ig", "tt")
     *   scanForComments    () => Array<{username, text, source?, displayName?, avatarUrl?}>
     *   checkIfLivePage    () => boolean
     *   getObserverTarget  () => Element | null  — root that gets MutationObserver
     *   debugLabel         string  — printed in console (e.g. "OrderDeck Instagram")
     */
    function start(adapter) {
        // Two-tier dedupe so the same customer can re-buy the same item
        // (live broadcaster scenario: "100 alıyorum" twice = two orders):
        //
        // Tier 1 — DOM element identity (primary)
        //   When the adapter returns the actual comment DOM node, we track
        //   it in a WeakSet. Instagram re-shows comments in the same node
        //   for ~10 minutes; re-typing creates a new node → new send.
        //   GC handles cleanup automatically when nodes leave the DOM.
        //
        // Tier 2 — (username, text) hash (fallback)
        //   When the adapter has no element (legacy adapters or the sibling-
        //   span fallback strategies in IG that can't reliably bind to one
        //   node), we fall back to session-scoped text dedupe with FIFO
        //   eviction. Worse UX than tier 1 (no re-buy) but prevents the
        //   "perpetually refreshing comment" regression.
        const seenElements = new WeakSet();
        const seenHashes = new Set();

        // Debug instrumentation — measured per 10s window, sent to WPF for log analysis.
        const STATS_INTERVAL_MS = 10_000;
        let stats = freshStats();
        let statsTimer = null;
        let viewerTimer = null;
        let lastScanAt = 0;   // maybeScanNow() rate-limit damgası

        // Watchdog durumu
        let watchdogTimer = null;
        let lastSentAt = 0;          // son başarılı emit (bağlantıda sıfırlanır)
        let lastObservedRows = 0;    // son taramada görülen satır sayısı
        let nudgedAt = 0;            // 0 = nudge denenmedi
        // Watchdog reload'ı sonrası ilk taramadaki satırlar zaten WPF'e gitmişti;
        // yeniden gönderme, sadece "görüldü" işaretle (duplicate seli önlenir).
        let suppressNextScanSends = false;
        try {
            if (sessionStorage.getItem(RELOAD_FLAG_KEY) === '1') {
                sessionStorage.removeItem(RELOAD_FLAG_KEY);
                suppressNextScanSends = true;
            }
        } catch (e) { /* sessionStorage erişilemezse duplicate koruması atlanır */ }

        // İzleyici sayısını oku ve köprüye yolla. Adaptör scanViewerCount
        // sağlamıyorsa (eski adaptör) sessizce atlar.
        function pollViewers() {
            if (!isConnected || !isLivePage) return;
            if (typeof adapter.scanViewerCount !== 'function') return;
            let count;
            try { count = adapter.scanViewerCount(); }
            catch (e) { return; }
            if (typeof count === 'number' && count >= 0) {
                sendMessage({ type: 'viewers', platform: adapter.platform, count });
            }
        }

        function freshStats() {
            return {
                scanCount: 0,
                commentsObserved: 0,       // total comments found across all scans this window
                deduped: 0,                // total dropped as duplicates
                sent: 0,                   // total emitted to WS
                queued: 0,                 // bağlantı kopukken outbox'a alınan
                observerBursts: 0,
                scanIntervalMs: SCAN_INTERVAL,
                dedupeWindowMs: 0,          // 0 = session-scoped (no TTL)
                windowStart: Date.now(),
            };
        }

        function flushStats() {
            if (!isConnected) return;
            const snapshot = stats;
            snapshot.windowEnd = Date.now();
            snapshot.windowDurationMs = snapshot.windowEnd - snapshot.windowStart;
            // WeakSet size'ı introspectable değil — hash fallback cache'i göster.
            snapshot.dedupeCacheSize = seenHashes.size;
            sendMessage({ type: 'debug-stats', platform: adapter.platform, stats: snapshot });
            // Operator-visible summary in DevTools console during broadcast.
            log(`📊 stats(${(snapshot.windowDurationMs/1000).toFixed(1)}s): observed=${snapshot.commentsObserved} sent=${snapshot.sent} queued=${snapshot.queued} deduped=${snapshot.deduped} scans=${snapshot.scanCount} bursts=${snapshot.observerBursts} cache=${snapshot.dedupeCacheSize}`);
            stats = freshStats();
        }

        let ws = null;
        let isConnected = false;
        let outbox = [];         // gönderilemeyen chat payload'ları (FIFO)
        let outboxDropped = 0;   // sınır taşınca atılan yorum sayısı
        let observer = null;
        let observerScanTimer = null;
        let scanTimer = null;
        let reconnectTimer = null;
        let isLivePage = false;
        let debugMode = true;

        function log(...args)      { if (debugMode) console.log(`[${adapter.debugLabel}]`, ...args); }
        function logError(...args) { console.error(`[${adapter.debugLabel}]`, ...args); }

        function connectWebSocket() {
            if (ws && ws.readyState === WebSocket.OPEN) return;

            try {
                log('Connecting to OrderDeck bridge...');
                ws = new WebSocket(`ws://localhost:${WS_PORT}/extension`);

                ws.onopen = () => {
                    log('WebSocket connected ✓');
                    isConnected = true;
                    clearTimeout(reconnectTimer);

                    sendMessage({
                        type: 'connected',
                        platform: adapter.platform,
                        url: window.location.href,
                        timestamp: Date.now()
                    });

                    try { chrome.runtime.sendMessage({ action: 'setConnected', connected: true, platform: adapter.platform }); } catch (e) {}

                    // Taramadan ÖNCE: kopukluk sırasında birikenler, yeni
                    // taramanın ürettiklerinden önce ve kendi sıralarında gitsin.
                    flushOutbox();

                    startPeriodicScan();

                    if (statsTimer) clearInterval(statsTimer);
                    statsTimer = setInterval(flushStats, STATS_INTERVAL_MS);

                    if (viewerTimer) clearInterval(viewerTimer);
                    viewerTimer = setInterval(pollViewers, VIEWER_INTERVAL);
                    pollViewers();

                    // Watchdog: bağlantı kurulunca saat sıfırlanır — bağlantı
                    // öncesi sessizlik "stall" sayılmaz.
                    lastSentAt = Date.now();
                    nudgedAt = 0;
                    if (watchdogTimer) clearInterval(watchdogTimer);
                    watchdogTimer = setInterval(watchdogTick, WATCHDOG_INTERVAL);
                };

                ws.onclose = () => {
                    log('WebSocket closed, reconnecting...');
                    isConnected = false;
                    stopPeriodicScan();
                    if (statsTimer) { clearInterval(statsTimer); statsTimer = null; }
                    if (viewerTimer) { clearInterval(viewerTimer); viewerTimer = null; }
                    if (watchdogTimer) { clearInterval(watchdogTimer); watchdogTimer = null; }
                    nudgedAt = 0;
                    try { chrome.runtime.sendMessage({ action: 'setConnected', connected: false, platform: adapter.platform }); } catch (e) {}
                    scheduleReconnect();
                };

                ws.onerror = (error) => {
                    logError('WebSocket error:', error);
                    isConnected = false;
                };

                ws.onmessage = (event) => {
                    try { handleServerMessage(JSON.parse(event.data)); }
                    catch (e) { logError('Message parse error:', e); }
                };
            } catch (error) {
                logError('WebSocket connection error:', error);
                scheduleReconnect();
            }
        }

        function scheduleReconnect() {
            if (reconnectTimer) clearTimeout(reconnectTimer);
            reconnectTimer = setTimeout(connectWebSocket, RECONNECT_INTERVAL);
        }

        function sendMessage(data) {
            if (ws && ws.readyState === WebSocket.OPEN) {
                ws.send(JSON.stringify(data));
                return true;
            }
            return false;
        }

        /// Kaybı kabul edilemez mesajlar (chat) için: gönderemezsek sıraya al.
        /// Diğer mesaj tipleri (viewers, debug-stats, watchdog, pong) bilerek
        /// sendMessage kullanmaya devam eder — onlar zaten periyodik ve
        /// bayatlamış bir kopyasını sonradan göndermek yanıltıcı olur.
        function sendOrQueue(data) {
            if (sendMessage(data)) return true;
            if (outbox.length >= OUTBOX_LIMIT) {
                outbox.shift();
                outboxDropped++;
            }
            outbox.push(data);
            return false;
        }

        /// Bağlantı kurulur kurulmaz sırayı akıt. Akıtma sırasında bağlantı
        /// yine düşerse kalanı outbox'ta bırakır — bir sonraki onopen dener.
        function flushOutbox() {
            if (outbox.length === 0 && outboxDropped === 0) return;

            const pending = outbox;
            outbox = [];
            let flushed = 0;
            for (const msg of pending) {
                if (!sendMessage(msg)) break;
                flushed++;
            }
            if (flushed < pending.length) {
                // Kalanları başa koy: sıra korunur, yeni gelenler arkaya eklenir.
                outbox = pending.slice(flushed).concat(outbox);
            }
            if (flushed > 0) {
                lastSentAt = Date.now();
                log(`Outbox: bağlantı kopukken biriken ${flushed} yorum gönderildi` +
                    (outbox.length > 0 ? ` (${outbox.length} hâlâ bekliyor)` : ''));
            }

            // Taşma sessiz kalmamalı — operatör WPF tarafında görsün.
            if (outboxDropped > 0 && sendMessage({
                type: 'chat-dropped', platform: adapter.platform,
                count: outboxDropped, timestamp: Date.now()
            })) {
                logError(`Outbox taştı: ${outboxDropped} yorum kaybedildi ` +
                    `(sınır ${OUTBOX_LIMIT}) — köprü çok uzun süre kapalı kaldı`);
                outboxDropped = 0;
            }
        }

        function handleServerMessage(data) {
            switch (data.type) {
                case 'ping':
                    sendMessage({ type: 'pong' });
                    break;
                case 'getStatus':
                    sendMessage({
                        type: 'status',
                        platform: adapter.platform,
                        observing: observer !== null,
                        commentCount: seenHashes.size,
                        url: window.location.href
                    });
                    break;
            }
        }

        function createCommentHash(username, text) {
            const str = `${username}:${text}`.toLowerCase().trim();
            let hash = 0;
            for (let i = 0; i < str.length; i++) {
                hash = ((hash << 5) - hash) + str.charCodeAt(i);
                hash = hash & hash;
            }
            return hash.toString(36);
        }

        function processComments(comments) {
            stats.scanCount++;
            stats.commentsObserved += comments.length;
            lastObservedRows = comments.length;

            // Watchdog reload'ı sonrası ilk tarama: satırları gönderme, işaretle.
            const suppress = suppressNextScanSends;
            if (suppress && comments.length > 0) {
                suppressNextScanSends = false;
                log(`Watchdog reload recovery: ${comments.length} mevcut satır gönderilmeden işaretlendi`);
            }

            comments.forEach(({ username, text, source, displayName, avatarUrl, element }) => {
                // Tier 1 (primary): DOM-element identity. Same node = already sent.
                // Re-enabled 2026-05-23 now that adapters bind to per-row elements
                // (PR #95 fixed IG Strategy 1; Strategy 2 div-2span has always
                // been per-row). Lets customers re-buy the same code in the same
                // session — re-typing creates a new DOM node, so new send.
                if (element && seenElements.has(element)) {
                    stats.deduped++;
                    return;
                }

                // Tier 2 (fallback): session-scoped text hash. Used only when
                // the adapter has no per-row element (very rare — IG sibling-span
                // fallback strategy). Same UX as PR #92: same (user, text) once.
                const hash = createCommentHash(username, text);
                if (!element && seenHashes.has(hash)) {
                    stats.deduped++;
                    return;
                }

                if (element) {
                    seenElements.add(element);
                } else {
                    // FIFO eviction for the hash fallback only — elements GC
                    // themselves when they leave the DOM.
                    if (seenHashes.size >= CACHE_LIMIT) {
                        const oldest = seenHashes.values().next().value;
                        seenHashes.delete(oldest);
                    }
                    seenHashes.add(hash);
                }

                if (suppress) {
                    // Reload öncesi zaten gönderilmişti — sayaçlara "deduped" yaz.
                    stats.deduped++;
                    return;
                }
                const payload = {
                    type: 'chat',
                    platform: adapter.platform,
                    username: username,
                    displayName: displayName ?? username,
                    avatarUrl: avatarUrl ?? null,
                    text: text,
                    externalId: `${adapter.externalIdPrefix}-${Date.now()}-${hash}`,
                    timestamp: Date.now()
                };

                log(`✓ [${source ?? 'scan'}]: @${username}: ${text.substring(0, 50)}${text.length > 50 ? '...' : ''}`);

                if (sendOrQueue(payload)) {
                    stats.sent++;
                    lastSentAt = Date.now();
                } else {
                    // Kayıp değil, ertelenmiş: bağlantı gelince flushOutbox atar.
                    // lastSentAt'e dokunulmuyor — watchdog zaten !isConnected
                    // durumunda tetiklenmiyor, ayrıca yanlış "akış canlı" izlenimi
                    // vermemeli.
                    stats.queued++;
                    log(`  -> köprü kapalı, sıraya alındı (${outbox.length}/${OUTBOX_LIMIT})`);
                }
            });

            return stats.sent;
        }

        function safeScan() {
            try { return adapter.scanForComments() || []; }
            catch (e) { logError('Adapter scan error:', e); return []; }
        }

        // Rate-limit'li tarama tetikleyici. Yeterli süre geçtiyse ANINDA tarar
        // (senkron — throttle'lı timer beklemez, arka planda da çalışır). Aksi
        // halde kalan süre için bir "trailing" tarama planlar (best-effort; arka
        // planda bu timer throttle olur ama bir sonraki mutasyon / periyodik
        // tarama onu yakalar). Hem observer hem periyodik tarama bunu kullanır →
        // tek rate-limit, çift tarama yok.
        function maybeScanNow() {
            const now = Date.now();
            const since = now - lastScanAt;
            if (since >= MIN_SCAN_GAP) {
                lastScanAt = now;
                processComments(safeScan());
                return;
            }
            if (observerScanTimer) return;
            observerScanTimer = setTimeout(() => {
                observerScanTimer = null;
                lastScanAt = Date.now();
                processComments(safeScan());
            }, MIN_SCAN_GAP - since);
        }

        function startPeriodicScan() {
            if (scanTimer) clearInterval(scanTimer);
            log(`Periodic scan started (${SCAN_INTERVAL}ms)`);

            const initial = safeScan();
            log(`Initial scan: ${initial.length} comment(s)`);
            processComments(initial);
            lastScanAt = Date.now();

            // Periyodik tarama artık yedek/taban: maybeScanNow rate-limit'i
            // paylaşır. Ön planda ~SCAN_INTERVAL'de bir; arka planda throttle
            // olur ama asıl akış observer-anında taramadan gelir.
            scanTimer = setInterval(maybeScanNow, SCAN_INTERVAL);
        }

        function stopPeriodicScan() {
            if (scanTimer) { clearInterval(scanTimer); scanTimer = null; }
            if (observerScanTimer) { clearTimeout(observerScanTimer); observerScanTimer = null; }
        }

        // ── Stall watchdog ───────────────────────────────────────────────────
        // adapter.stallRecovery: { nudge?: () => void, allowReload?: boolean }
        // sağlayan platformlarda (şimdilik Instagram) aktif.
        function watchdogTick() {
            if (!adapter.stallRecovery) return;
            if (!isConnected || !isLivePage) { nudgedAt = 0; return; }

            // Az satır = sayfa boş/idle; sessizlik normaldir.
            if (lastObservedRows < STALL_MIN_ROWS) { nudgedAt = 0; return; }

            const sinceSend = Date.now() - (lastSentAt || 0);
            if (lastSentAt === 0 || sinceSend < STALL_AFTER_MS) { nudgedAt = 0; return; }

            if (nudgedAt === 0) {
                nudgedAt = Date.now();
                logError(`Watchdog: akış durdu (${Math.round(sinceSend / 1000)}sn'dir gönderim yok, ` +
                    `DOM'da ${lastObservedRows} satır) — chat scroll dürtülüyor`);
                sendMessage({
                    type: 'watchdog', platform: adapter.platform,
                    action: 'nudge', sinceSendMs: sinceSend, rows: lastObservedRows
                });
                try { adapter.stallRecovery.nudge?.(); } catch (e) { logError('Watchdog nudge error:', e); }
                return;
            }

            if (Date.now() - nudgedAt < NUDGE_GRACE_MS) return;
            if (adapter.stallRecovery.allowReload !== true) return;

            // Reload döngüsü emniyeti: en fazla RELOAD_COOLDOWN_MS'de bir.
            let lastReloadAt = 0;
            try { lastReloadAt = Number(sessionStorage.getItem(RELOAD_AT_KEY) || 0); } catch (e) {}
            if (Date.now() - lastReloadAt < RELOAD_COOLDOWN_MS) return;

            logError(`Watchdog: nudge işe yaramadı — sayfa yenileniyor ` +
                `(${Math.round(sinceSend / 1000)}sn'dir gönderim yok)`);
            sendMessage({
                type: 'watchdog', platform: adapter.platform,
                action: 'reload', sinceSendMs: sinceSend, rows: lastObservedRows
            });
            try {
                sessionStorage.setItem(RELOAD_FLAG_KEY, '1');
                sessionStorage.setItem(RELOAD_AT_KEY, String(Date.now()));
            } catch (e) {}
            location.reload();
        }

        function startObserver() {
            if (observer) observer.disconnect();

            const target = adapter.getObserverTarget?.() ?? document.body;
            log('Starting MutationObserver on:', target.tagName ?? '#document');

            observer = new MutationObserver((mutations) => {
                if (!isLivePage) {
                    if (adapter.checkIfLivePage()) {
                        isLivePage = true;
                        log('Live page detected (MutationObserver)');
                        connectWebSocket();
                    }
                    return;
                }

                let anyAdded = false;
                for (const m of mutations) {
                    if (m.addedNodes.length > 0) { anyAdded = true; break; }
                }
                if (!anyAdded) return;

                stats.observerBursts++;
                // ANINDA tara (rate-limit'li). MutationObserver geri çağrıları
                // arka planda throttle EDİLMEZ; bu yüzden sekme gizli/mute
                // olsa bile yeni yorum DOM'a basılır basılmaz, throttle'lı timer
                // beklemeden çekeriz. Eski 50ms setTimeout debounce'u arka planda
                // dakikada 1'e düşüyordu (toplu düşme sorununun kaynağı).
                maybeScanNow();
            });

            observer.observe(target, { childList: true, subtree: true });
            log('MutationObserver active');
        }

        function init() {
            log('=========================================');
            // Sürüm damgası (2026-07-16): Chrome'un eski/yanlış kopyayı
            // çalıştırdığını ancak log metinlerinden anlayabiliyorduk.
            // Artık build doğrulaması tek satır: konsolda v1.4.13 görünmeli.
            let extVersion = '?';
            try { extVersion = chrome.runtime.getManifest().version; } catch { }
            log(`${adapter.debugLabel} bridge v${extVersion}`);
            log('URL:', window.location.href);
            isLivePage = adapter.checkIfLivePage();
            log('Live page:', isLivePage ? 'YES' : 'No (watching)');
            log('=========================================');

            if (isLivePage) connectWebSocket();
            setTimeout(startObserver, 1500);

            // SPA navigation watchdog
            let lastUrl = location.href;
            new MutationObserver(() => {
                const url = location.href;
                if (url === lastUrl) return;
                lastUrl = url;
                log('Page changed:', url);
                isLivePage = adapter.checkIfLivePage();

                // Başka yayına geçiliyor: köprü kapalıyken birikmiş yorumlar
                // ARTIK GÖNDERİLMEMELİ — WPF gelen mesajı o anki oturuma
                // yazar, yani eski yayının yorumları yeni yayının siparişi
                // olurdu. Atıyoruz ama sayıyoruz; bağlantı kurulunca operatöre
                // "şu kadar yorum kaybedildi" olarak bildirilir.
                if (outbox.length > 0) {
                    outboxDropped += outbox.length;
                    logError(`Sayfa değişti: gönderilememiş ${outbox.length} yorum ` +
                        `atıldı (yanlış yayına yazılmasınlar diye)`);
                    outbox = [];
                }

                if (isLivePage) {
                    seenHashes.clear();
                    // seenElements (WeakSet) has no clear() — but old DOM is gone
                    // after navigation so all old refs are GC'd anyway.
                    connectWebSocket();
                    setTimeout(startPeriodicScan, 2000);
                } else {
                    stopPeriodicScan();
                }
            }).observe(document, { subtree: true, childList: true });

            // Canlı algılama emniyet poll'u (2026-07-16). IG yayıncı modal'ı
            // <main> DIŞINDA render oluyor ve açılınca URL değişmiyor — yani
            // yukarıdaki iki tetik de (main-observer mutasyonu, URL değişimi)
            // onu kaçırabiliyor. Belirti: F5 sonrası "Live page: No (watching)"
            // durumunda takılma. Watchdog'un otomatik reload'ı da bu yarışa
            // yakalanabileceği için poll şart: 3sn'de bir ucuz re-check.
            setInterval(() => {
                if (isLivePage) return;
                if (!adapter.checkIfLivePage()) return;
                isLivePage = true;
                log('Live page detected (poll)');
                connectWebSocket();
                setTimeout(startPeriodicScan, 2000);
            }, 3000);

            // Re-arm the MutationObserver whenever the central selector
            // bundle rotates — the new observer-target selector might point
            // somewhere different now (e.g. TikTok renamed [data-e2e="chat-list"]).
            // We only re-arm if we're currently on a live page, otherwise the
            // tree under document.body is fine and a no-op spares churn.
            if (self.OrderDeckSelectors?.onUpdate) {
                self.OrderDeckSelectors.onUpdate(() => {
                    log('Selector bundle updated; re-arming observer');
                    isLivePage = adapter.checkIfLivePage();
                    if (isLivePage) startObserver();
                });
            }
        }

        // Debug handle on window — devtools-friendly per platform.
        window.__orderdeckBridge = {
            platform: adapter.platform,
            scan: () => { const c = safeScan(); console.log('Comments found:', c); return c; },
            send: sendMessage,
            status: () => ({
                connected: isConnected,
                wsState: ws?.readyState,
                seenCount: seenHashes.size,
                isLive: isLivePage,
                url: window.location.href,
                platform: adapter.platform,
                watchdog: {
                    enabled: !!adapter.stallRecovery,
                    lastSentAgoMs: lastSentAt ? Date.now() - lastSentAt : null,
                    lastObservedRows,
                    nudgedAt: nudgedAt || null
                }
            }),
            forceSend: () => {
                const comments = safeScan();
                comments.forEach(c => {
                    const hash = createCommentHash(c.username, c.text);
                    sendMessage({
                        type: 'chat',
                        platform: adapter.platform,
                        username: c.username,
                        displayName: c.displayName ?? c.username,
                        avatarUrl: c.avatarUrl ?? null,
                        text: c.text,
                        externalId: `${adapter.externalIdPrefix}-${Date.now()}-${Math.random().toString(36).substr(2, 9)}`,
                        timestamp: Date.now()
                    });
                });
                return `${comments.length} comment(s) sent`;
            }
        };

        try {
            if (document.readyState === 'loading')
                document.addEventListener('DOMContentLoaded', init);
            else
                init();
        } catch (e) { logError('INIT ERROR:', e); }

        log('Script loaded ✓');
    }

    return { start, parseViewerCount };
})();
