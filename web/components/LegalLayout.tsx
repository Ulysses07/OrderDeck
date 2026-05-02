import { Nav } from './Nav';
import { Footer } from './Footer';
import type { Locale } from '@/lib/i18n';
import type { ROUTE_MAP } from '@/lib/i18n';

interface LegalLayoutProps {
  locale: Locale;
  routeKey: keyof typeof ROUTE_MAP;
  title: string;
  effectiveDate: string;
  children: React.ReactNode;
}

/**
 * Privacy / Terms gibi yasal sayfalar için ortak çerçeve. İçerik <article>
 * içinde `prose` sınıfıyla biçimlendiriliyor (globals.css'de tanımlı).
 */
export function LegalLayout({
  locale,
  routeKey,
  title,
  effectiveDate,
  children,
}: LegalLayoutProps) {
  return (
    <>
      <Nav locale={locale} routeKey={routeKey} />
      <main className="mx-auto max-w-3xl px-5 py-16">
        <h1 className="text-3xl font-bold tracking-tight md:text-4xl">{title}</h1>
        <p className="mt-3 text-sm text-[var(--color-text-mute)]">
          {locale === 'tr' ? 'Yürürlük tarihi' : 'Effective date'}: {effectiveDate}
        </p>
        <article className="prose mt-10">{children}</article>
      </main>
      <Footer locale={locale} />
    </>
  );
}
