import { Check } from 'lucide-react';
import { ROUTE_MAP, type Locale } from '@/lib/i18n';
import { tr } from '@/messages/tr';
import { en } from '@/messages/en';
import Link from 'next/link';

/**
 * Tek plan pricing — ömür boyu lisans + opsiyonel yıllık güncelleme paketi
 * (JetBrains stili). Her kullanıcı tüm platformları (IG, TikTok, FB, YouTube)
 * tek lisansla kullanır; ekstra tier yok.
 */
export function PricingTable({ locale }: { locale: Locale }) {
  const m = locale === 'tr' ? tr : en;
  const path = (k: keyof typeof ROUTE_MAP) => ROUTE_MAP[k][locale];
  const plan = m.pricing.plan;

  return (
    <section className="mx-auto max-w-3xl px-5 py-16">
      <div className="text-center">
        <h2 className="text-3xl font-bold tracking-tight md:text-4xl">{m.pricing.title}</h2>
        <p className="mt-3 text-base text-[var(--color-text-dim)]">{m.pricing.subtitle}</p>
      </div>

      <div className="mt-12">
        <div
          className="relative overflow-hidden rounded-2xl border border-[var(--color-accent)] bg-[var(--color-surface)] p-6 md:p-10"
          style={{
            backgroundImage:
              'radial-gradient(80% 60% at 50% 0%, rgba(32,197,247,0.10) 0%, transparent 65%)',
          }}
        >
          <div className="text-center">
            <span className="inline-block rounded-full border border-[var(--color-border-strong)] bg-[var(--color-bg)] px-3 py-1 text-xs font-medium uppercase tracking-wider text-[var(--color-accent-hot)]">
              {plan.badge}
            </span>
            <h3 className="mt-4 text-2xl font-bold">{plan.name}</h3>
            <p className="mt-2 text-sm text-[var(--color-text-mute)]">{plan.tagline}</p>

            <div className="mt-6 flex items-baseline justify-center gap-2">
              <span className="text-5xl font-black tracking-tight">{plan.price}</span>
              <span className="text-sm text-[var(--color-text-mute)]">{plan.priceNote}</span>
            </div>
            <p className="mt-2 text-xs text-[var(--color-text-mute)]">{plan.priceSubnote}</p>
          </div>

          <ul className="mt-8 grid gap-3 text-sm text-[var(--color-text-dim)] sm:grid-cols-2">
            {plan.features.map((f) => (
              <li key={f} className="flex items-start gap-2">
                <Check
                  size={16}
                  className="mt-0.5 shrink-0 text-[var(--color-accent-hot)]"
                  aria-hidden
                />
                <span>{f}</span>
              </li>
            ))}
          </ul>

          <Link
            href={path('contact')}
            className="mt-8 block rounded-lg bg-[var(--color-accent)] py-3 text-center text-sm font-semibold text-white hover:bg-[var(--color-accent-hot)] transition-colors"
          >
            {m.pricing.cta}
          </Link>
        </div>

        {/* Yıllık destek paketi — küçük, secondary kart */}
        <div className="mt-5 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6">
          <div className="flex flex-col gap-3 sm:flex-row sm:items-baseline sm:justify-between">
            <div>
              <h4 className="text-base font-semibold">{m.pricing.support.name}</h4>
              <p className="mt-1 text-sm text-[var(--color-text-mute)]">
                {m.pricing.support.tagline}
              </p>
            </div>
            <div className="text-right">
              <span className="text-2xl font-bold">{m.pricing.support.price}</span>
              <span className="ml-1 text-sm text-[var(--color-text-mute)]">
                {m.pricing.support.priceNote}
              </span>
            </div>
          </div>
          <ul className="mt-4 space-y-2 text-sm text-[var(--color-text-dim)]">
            {m.pricing.support.features.map((f) => (
              <li key={f} className="flex items-start gap-2">
                <Check
                  size={14}
                  className="mt-1 shrink-0 text-[var(--color-success)]"
                  aria-hidden
                />
                <span>{f}</span>
              </li>
            ))}
          </ul>
          <p className="mt-4 text-xs text-[var(--color-text-mute)]">
            {m.pricing.support.note}
          </p>
        </div>
      </div>

      <p className="mt-8 text-center text-xs text-[var(--color-text-mute)]">{m.pricing.note}</p>
    </section>
  );
}
