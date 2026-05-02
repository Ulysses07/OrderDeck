import Link from 'next/link';
import { ROUTE_MAP, type Locale } from '@/lib/i18n';

interface LangSwitchProps {
  currentLocale: Locale;
  routeKey: keyof typeof ROUTE_MAP;
}

/**
 * İki dilli site için minimal switcher. Her sayfa bilinen `routeKey` ile
 * geliyor (ROUTE_MAP'teki anahtar) ve LangSwitch karşı dilin URL'ine
 * yönlendiriyor. Statik export uyumlu — hiçbir client-state yok.
 */
export function LangSwitch({ currentLocale, routeKey }: LangSwitchProps) {
  const otherLocale: Locale = currentLocale === 'tr' ? 'en' : 'tr';
  const otherUrl = ROUTE_MAP[routeKey][otherLocale];

  return (
    <Link
      href={otherUrl}
      hrefLang={otherLocale}
      className="inline-flex items-center gap-1 rounded-md border border-[var(--color-border)] px-2.5 py-1 text-xs font-medium uppercase tracking-wider text-[var(--color-text-dim)] hover:border-[var(--color-accent)] hover:text-[var(--color-accent)] transition-colors"
      aria-label={`Switch to ${otherLocale.toUpperCase()}`}
    >
      {otherLocale}
    </Link>
  );
}
