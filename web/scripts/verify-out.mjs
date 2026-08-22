// Statik export çıktısının (`out/`) beklenen sayfaları içerdiğini doğrular.
//
// Bu liste iki yerde lazım: PR kapısında (web-ci.yml) ve deploy'da
// (web-deploy.yml). Kontrol daha önce deploy workflow'unun içine gömülü
// `test -f ...` satırlarıydı; PR kapısı eklenince aynı listenin iki kopyası
// olacaktı ve biri sessizce bayatlayacaktı. Tek tanım burada.
//
// `/` ve `/en/privacy-policy/` ile `/en/terms-of-service/` deploy sonrası
// smoke testinin curl'lediği üç adres — eksikleri rsync'ten SONRA, yani canlı
// sitede fark edilirdi. Burada build çıktısında yakalanıyorlar.

import { statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const outDir = path.join(path.dirname(fileURLToPath(import.meta.url)), '..', 'out');

const required = [
  'index.html',
  'en/index.html',
  'gizlilik-politikasi/index.html',
  'en/privacy-policy/index.html',
  'kullanim-kosullari/index.html',
  'en/terms-of-service/index.html',
];

const failures = [];

for (const rel of required) {
  const full = path.join(outDir, rel);
  let stat;
  try {
    stat = statSync(full);
  } catch {
    failures.push(`eksik: out/${rel}`);
    continue;
  }
  // Boş dosya da kırık: sayfa üretildi ama içerik yazılmadıysa 200 döner,
  // smoke testinin `curl -I`'sı bunu yakalamaz.
  if (!stat.isFile() || stat.size === 0) {
    failures.push(`boş veya dosya değil: out/${rel}`);
  }
}

if (failures.length > 0) {
  console.error('Build çıktısı doğrulaması BAŞARISIZ:');
  for (const f of failures) console.error(`  - ${f}`);
  process.exit(1);
}

console.log(`Build çıktısı doğrulandı (${required.length} sayfa).`);
