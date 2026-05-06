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
    $winner.textContent = `Plugin yüklenemedi: ${animationId}`;
    console.error(err);
    return;
  }
  const plugin = module.default;

  // Inject plugin stylesheet
  activeStyleEl = document.createElement('link');
  activeStyleEl.rel = 'stylesheet';
  activeStyleEl.href = `./animations/${plugin.id}/style.css`;
  document.head.appendChild(activeStyleEl);

  const audio = new AudioController(`./animations/${plugin.id}/audio/`, 0.7, false);
  activeSynth = new SynthController(0.7, false);

  await plugin.init($stage, audio, activeSynth);
  activePlugin = plugin;

  const pool = mockPool();
  const winner = pool[Math.floor(Math.random() * pool.length)];
  await plugin.runFor([winner], pool);
  $winner.textContent = `Kazanan (mock): ${winner.DisplayName}`;
}

$replay.addEventListener('click', loadAndPlay);

// First-load run. Browsers block AudioContext start before user gesture,
// so synth.tick/etc. become no-ops on the FIRST run; clicking "Tekrar oynat"
// produces sound from then on. The visuals always work.
loadAndPlay();
