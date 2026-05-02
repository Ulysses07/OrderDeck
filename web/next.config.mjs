/** @type {import('next').NextConfig} */
const nextConfig = {
  // Statik export — Caddy `file_server` ile direkt servis ediliyor, prod'da Node.js çalışmıyor.
  output: 'export',

  // Trailing slash → her route bir klasör + index.html üretiyor (Caddy file_server için ideal).
  trailingSlash: true,

  // Statik export'ta Next/Image optimisation runtime gerektirir, devre dışı.
  images: {
    unoptimized: true,
  },

  // Build çıktısı /out/ → rsync ile VPS'teki /opt/orderdeck/web-out/ klasörüne kopyalanıyor.
};

export default nextConfig;
