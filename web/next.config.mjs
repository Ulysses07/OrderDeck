import path from 'node:path';
import { fileURLToPath } from 'node:url';

const projectDir = path.dirname(fileURLToPath(import.meta.url));

/** @type {import('next').NextConfig} */
const nextConfig = {
  // Statik export — Caddy `file_server` ile direkt servis ediliyor, prod'da Node.js çalışmıyor.
  output: 'export',

  // Workspace kökünü sabitle. Aksi hâlde Next kökü lockfile arayarak TAHMİN
  // ediyor ve repo dışına çıkabiliyor; geliştirici makinesinde
  // `C:\Users\burak\package-lock.json` yüzünden şu uyarıyı veriyordu:
  // "Next.js ignored package-lock.json in C:\Users\burak because it is outside
  // the current Git repository". CI'da (temiz ubuntu runner) böyle bir dosya
  // olmadığı için kök farklı seçiliyordu — yani yerel ile CI aynı ağacı aynı
  // şekilde çözmüyordu. Açıkça yazınca ikisi de bu klasörü kullanıyor.
  turbopack: {
    root: projectDir,
  },

  // Trailing slash → her route bir klasör + index.html üretiyor (Caddy file_server için ideal).
  trailingSlash: true,

  // Statik export'ta Next/Image optimisation runtime gerektirir, devre dışı.
  images: {
    unoptimized: true,
  },

  // Build çıktısı /out/ → rsync ile VPS'teki /opt/orderdeck/web-out/ klasörüne kopyalanıyor.
};

export default nextConfig;
