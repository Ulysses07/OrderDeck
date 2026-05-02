import type { Metadata } from 'next';
import '../globals.css';
import { SITE_URL, BRAND } from '@/lib/i18n';

/**
 * TR root layout. Next.js multi-root layout deseni: app/(tr)/layout.tsx ve
 * app/(en)/layout.tsx birbirinden bağımsız <html><body> sağlıyor → her dil
 * kendi `lang` attribute'una sahip oluyor (SEO + erişilebilirlik için kritik).
 */

export const metadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  title: {
    default: `${BRAND} — Mezat yayıncıları için chat, etiket ve çekiliş yönetimi`,
    template: `%s | ${BRAND}`,
  },
  description:
    'Instagram, TikTok, Facebook ve YouTube canlı yayın chat\'lerini birleştir, anında etiket bas, çark çevirerek çekiliş yap. Mezat yayıncıları için Windows uygulaması.',
  alternates: {
    canonical: '/',
    languages: {
      'tr-TR': '/',
      'en-US': '/en/',
    },
  },
  openGraph: {
    type: 'website',
    locale: 'tr_TR',
    url: SITE_URL,
    siteName: BRAND,
    title: `${BRAND} — Mezat yayıncıları için chat, etiket ve çekiliş`,
    description:
      'Canlı yayın chat\'lerini birleştir, anında etiket bas, çark çevirerek çekiliş yap.',
  },
  twitter: {
    card: 'summary_large_image',
    title: `${BRAND}`,
    description:
      'Canlı yayın chat\'lerini birleştir, anında etiket bas, çark çevirerek çekiliş yap.',
  },
  robots: { index: true, follow: true },
  icons: { icon: '/favicon.ico' },
};

export default function TrRootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="tr" suppressHydrationWarning>
      <body className="bg-[var(--color-bg)] text-[var(--color-text)]">
        {children}
      </body>
    </html>
  );
}
