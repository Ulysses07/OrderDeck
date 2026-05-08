import Link from 'next/link';
import { ROUTE_MAP, BRAND, type Locale } from '@/lib/i18n';
import { tr } from '@/messages/tr';
import { en } from '@/messages/en';
import { LangSwitch } from './LangSwitch';
import { Logo } from './Logo';

interface NavProps {
  locale: Locale;
  /**
   * Mevcut sayfanın `ROUTE_MAP`'teki anahtarı — LangSwitch'in karşı dile
   * doğru route ile yönlendirebilmesi için. Page'lerden geçirilir.
   */
  routeKey: keyof typeof ROUTE_MAP;
}

export function Nav({ locale, routeKey }: NavProps) {
  const m = locale === 'tr' ? tr : en;
  const r = ROUTE_MAP;
  const path = (k: keyof typeof ROUTE_MAP) => r[k][locale];

  return (
    <header className="sticky top-0 z-50 border-b border-[var(--color-border)] bg-[color-mix(in_srgb,var(--color-bg)_92%,transparent)] backdrop-blur">
      <nav className="mx-auto flex max-w-6xl items-center justify-between px-5 py-4">
        <Link
          href={path('home')}
          className="group flex items-center gap-2 text-lg font-bold tracking-tight"
          aria-label={`${BRAND} home`}
        >
          <Logo size={28} />
          <span>{BRAND}</span>
        </Link>

        <div className="hidden items-center gap-6 text-sm text-[var(--color-text-dim)] md:flex">
          <Link href={path('features')} className="hover:text-[var(--color-text)] transition-colors">
            {m.nav.features}
          </Link>
          <Link href={path('pricing')} className="hover:text-[var(--color-text)] transition-colors">
            {m.nav.pricing}
          </Link>
          <Link href={path('blog')} className="hover:text-[var(--color-text)] transition-colors">
            {m.nav.blog}
          </Link>
          <Link href={path('faq')} className="hover:text-[var(--color-text)] transition-colors">
            {m.nav.faq}
          </Link>
        </div>

        <div className="flex items-center gap-3">
          <LangSwitch currentLocale={locale} routeKey={routeKey} />
          <Link
            href={path('download')}
            className="hidden rounded-md bg-[var(--color-accent)] px-3 py-1.5 text-sm font-semibold text-white hover:bg-[var(--color-accent-hot)] transition-colors sm:inline-block"
          >
            {m.nav.download}
          </Link>
        </div>
      </nav>
    </header>
  );
}
