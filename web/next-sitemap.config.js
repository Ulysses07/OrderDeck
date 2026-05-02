/** @type {import('next-sitemap').IConfig} */
module.exports = {
  siteUrl: 'https://orderdeckapp.com',
  generateRobotsTxt: false, // public/robots.txt elle yazıldı, override etme
  generateIndexSitemap: false,
  outDir: 'out',
  // Statik export'tan sonra postbuild olarak çalışır → out/sitemap.xml üretir.
  exclude: ['/_not-found'],
  changefreq: 'weekly',
  priority: 0.7,
};
