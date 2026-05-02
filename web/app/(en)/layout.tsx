import type { Metadata } from 'next';
import '../globals.css';
import { SITE_URL, BRAND } from '@/lib/i18n';

/**
 * EN root layout — TR root ile bağımsız. SEO için `lang="en"` ve EN
 * canonical/alternate metadata.
 */

export const metadata: Metadata = {
  metadataBase: new URL(SITE_URL),
  title: {
    default: `${BRAND} — Live-stream chat, label printing and giveaways`,
    template: `%s | ${BRAND}`,
  },
  description:
    'Aggregates Instagram, TikTok, Facebook and YouTube live chat into one feed. Print labels in real-time. Run keyword giveaways with a spinning wheel on screen. Windows app for live auction streamers.',
  alternates: {
    canonical: '/en/',
    languages: {
      'tr-TR': '/',
      'en-US': '/en/',
    },
  },
  openGraph: {
    type: 'website',
    locale: 'en_US',
    url: `${SITE_URL}/en/`,
    siteName: BRAND,
    title: `${BRAND} — Live-stream chat, label printing and giveaways`,
    description:
      'Aggregate live chat from 4 platforms, print labels in real-time, run wheel giveaways.',
  },
  twitter: {
    card: 'summary_large_image',
    title: `${BRAND}`,
    description:
      'Aggregate live chat from 4 platforms, print labels in real-time, run wheel giveaways.',
  },
  robots: { index: true, follow: true },
  icons: { icon: '/favicon.ico' },
};

export default function EnRootLayout({ children }: { children: React.ReactNode }) {
  return (
    <html lang="en" suppressHydrationWarning>
      <body className="bg-[var(--color-bg)] text-[var(--color-text)]">
        {children}
      </body>
    </html>
  );
}
