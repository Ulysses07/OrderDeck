// preview.js — standalone preview page. Loads the chosen animation
// plugin with mock data so the operator can see what each option looks
// like before picking it for a real giveaway. No WebSocket, no server
// state — fully synthetic.

import { AudioController } from './audio-controller.js';
import { SynthController } from './synth-controller.js';

const $stage = document.getElementById('plugin-stage');
const $id = document.getElementById('preview-id');
const $keyword = document.getElementById('preview-keyword');
const $winner = document.getElementById('preview-winner');
const $replay = document.getElementById('preview-replay');

const params = new URLSearchParams(location.search);
const animationId = params.get('animation') || 'wheel';

$id.textContent = `[${animationId}]`;
$keyword.textContent = `"önizleme"`;

const MOCK_NAMES = [
  'Ayşe Yılmaz', 'Mehmet Demir', 'Fatma Şahin', 'Ali Öztürk',
  'Zeynep Kara', 'Mustafa Aydın', 'Elif Çelik', 'Hasan Yıldız'
];

function mockPool() {
  return MOCK_NAMES.map(name => ({
    Username: name.toLowerCase().replace(/\s+/g, '.'),
    DisplayName: name,
    Platform: 'instagram',
    AvatarUrl: null
  }));
}

let activePlugin = null;
let activeStyleEl = null;
let activeSynth = null;

/**
 * Surface error on the page (red banner) so the operator sees it without
 * opening DevTools. Mirrored to console for debugging.
 */
function showError(stage, err) {
  console.error('[preview]', stage, err);
  $winner.innerHTML =
    `<div style="color:#fff;background:#b91c1c;padding:8px 16px;` +
    `border-radius:6px;font:600 13px monospace;text-align:left;` +
    `max-width:480px;margin:0 auto;white-space:pre-wrap;">` +
    `❌ <b>${stage} fail:</b>\n${escapeForHtml(err && err.stack ? err.stack : String(err))}` +
    `</div>`;
}

function escapeForHtml(s) {
  return String(s).replace(/[&<>"']/g, c =>
    ({'&':'&amp;','<':'&lt;','>':'&gt;','"':'&quot;',"'":'&#39;'}[c]));
}

async function loadAndPlay() {
  // Cleanup previous run if any
  if (activePlugin) { try { activePlugin.reset(); } catch {} }
  if (activeSynth) { try { activeSynth.disposeAll(); } catch {} }
  if (activeStyleEl) activeStyleEl.remove();
  $stage.innerHTML = '';
  $winner.textContent = '';

  let module;
  try {
    module = await import(`./animations/${animationId}/index.js`);
  } catch (err) {
    showError(`import('./animations/${animationId}/index.js')`, err);
    return;
  }

  const plugin = module && module.default;
  if (!plugin) {
    showError('module load', new Error(`'${animationId}/index.js' has no default export`));
    return;
  }

  // Inject plugin stylesheet — ABSOLUTE path so it works regardless of
  // document URL (this page is served at /overlay/preview, so a relative
  // './animations/...' would resolve to /overlay/animations/... which 404s).
  try {
    activeStyleEl = document.createElement('link');
    activeStyleEl.rel = 'stylesheet';
    activeStyleEl.href = `/animations/${plugin.id}/style.css`;
    document.head.appendChild(activeStyleEl);
  } catch (err) {
    showError('stylesheet inject', err);
    return;
  }

  let audio, synth;
  try {
    audio = new AudioController(`/animations/${plugin.id}/audio/`, 0.7, false);
    synth = new SynthController(0.7, false);
    activeSynth = synth;
  } catch (err) {
    showError('audio/synth construct', err);
    return;
  }

  try {
    await plugin.init($stage, audio, synth);
  } catch (err) {
    showError(`${plugin.id}.init()`, err);
    return;
  }
  activePlugin = plugin;

  // Server contract (BuildAnimationPool): winners live at the TAIL of the
  // pool in order. Plugins now use position-based winner index
  // (pool.length - winners.length + i), so preview/diagnose mock pools must
  // honour the same layout — otherwise the wheel/strip lands on whoever
  // happens to be at the tail (e.g. pool[7]) while we display a different
  // random name as the winner. That is exactly the dispute case the
  // WYSIWYG fix was meant to prevent, just on the preview side.
  const pool = mockPool();
  const winnerIdx = Math.floor(Math.random() * pool.length);
  const winner = pool[winnerIdx];
  // Move the chosen winner to the end of the pool to match server layout.
  pool.splice(winnerIdx, 1);
  pool.push(winner);
  try {
    await plugin.runFor([winner], pool);
    $winner.textContent = `Kazanan (mock): ${winner.DisplayName}`;
  } catch (err) {
    showError(`${plugin.id}.runFor()`, err);
  }
}

$replay.addEventListener('click', loadAndPlay);

// First-load run. Browsers block AudioContext start before user gesture,
// so synth.tick/etc. become no-ops on the FIRST run; clicking "Tekrar oynat"
// produces sound from then on. The visuals always work.
loadAndPlay();
