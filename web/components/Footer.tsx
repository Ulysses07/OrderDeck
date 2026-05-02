import Link from 'next/link';
import { ROUTE_MAP, BRAND, CONTACT_EMAIL, LEGAL_NAME, type Locale } from '@/lib/i18n';
import { tr } from '@/messages/tr';
import { en } from '@/messages/en';
import { Logo } from './Logo';

export function Footer({ locale }: { locale: Locale }) {
  const m = locale === 'tr' ? tr : en;
  const r = ROUTE_MAP;
  const path = (k: keyof typeof ROUTE_MAP) => r[k][locale];
  const year = new Date().getFullYear();

  return (
    <footer className="mt-24 border-t border-[var(--color-border)] bg-[var(--color-surface)]">
      <div className="mx-auto grid max-w-6xl gap-10 px-5 py-12 md:grid-cols-4">
        <div className="md:col-span-2">
          <Link href={path('home')} className="flex items-center gap-2 text-lg font-bold">
            <Logo size={28} />
            <span>{BRAND}</span>
          </Link>
          <p className="mt-3 max-w-sm text-sm text-[var(--color-text-mute)]">
            {m.footer.tagline}
          </p>
          <p className="mt-4 text-xs text-[var(--color-text-mute)]">
            {LEGAL_NAME} ·{' '}
            <a
              href={`mailto:${CONTACT_EMAIL}`}
              className="hover:text-[var(--color-accent)] transition-colors"
            >
              {CONTACT_EMAIL}
            </a>
          </p>
        </div>

        <div>
          <h4 className="text-sm font-semibold text-[var(--color-text)]">
            {m.footer.productLabel}
          </h4>
          <ul className="mt-3 space-y-2 text-sm text-[var(--color-text-mute)]">
            <li><Link href={path('features')} className="hover:text-[var(--color-text)]">{m.nav.features}</Link></li>
            <li><Link href={path('pricing')} className="hover:text-[var(--color-text)]">{m.nav.pricing}</Link></li>
            <li><Link href={path('faq')} className="hover:text-[var(--color-text)]">{m.nav.faq}</Link></li>
            <li><Link href={path('blog')} className="hover:text-[var(--color-text)]">{m.nav.blog}</Link></li>
          </ul>
        </div>

        <div>
          <h4 className="text-sm font-semibold text-[var(--color-text)]">
            {m.footer.legalLabel}
          </h4>
          <ul className="mt-3 space-y-2 text-sm text-[var(--color-text-mute)]">
            <li><Link href={path('privacy')} className="hover:text-[var(--color-text)]">{m.footer.privacy}</Link></li>
            <li><Link href={path('terms')} className="hover:text-[var(--color-text)]">{m.footer.terms}</Link></li>
            <li><Link href={path('contact')} className="hover:text-[var(--color-text)]">{m.nav.contact}</Link></li>
          </ul>
        </div>
      </div>

      <div className="border-t border-[var(--color-border)] py-5">
        <div className="mx-auto flex max-w-6xl items-center justify-between px-5 text-xs text-[var(--color-text-mute)]">
          <span>
            © {year} {LEGAL_NAME}. {m.footer.rights}
          </span>
          <span className="font-mono opacity-70">v0.1</span>
        </div>
      </div>
    </footer>
  );
}
