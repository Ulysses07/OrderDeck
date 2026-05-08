import Link from 'next/link';
import { ROUTE_MAP, type Locale } from '@/lib/i18n';
import { tr } from '@/messages/tr';
import { en } from '@/messages/en';
import { Download } from 'lucide-react';

export function CtaBanner({ locale }: { locale: Locale }) {
  const m = locale === 'tr' ? tr : en;
  const downloadPath = ROUTE_MAP.download[locale];

  return (
    <section
      id="download"
      className="mx-auto max-w-6xl px-5 py-16"
    >
      <div
        className="relative overflow-hidden rounded-2xl border border-[var(--color-accent)] bg-[var(--color-surface)] px-6 py-12 text-center md:px-12 md:py-16"
        style={{
          backgroundImage:
            'radial-gradient(60% 100% at 50% 0%, rgba(32,197,247,0.18) 0%, rgba(15,99,245,0.05) 50%, transparent 75%)',
        }}
      >
        <h2 className="text-3xl font-bold tracking-tight md:text-4xl">{m.cta.title}</h2>
        <p className="mt-3 mx-auto max-w-xl text-base text-[var(--color-text-dim)]">
          {m.cta.subtitle}
        </p>
        <Link
          href={downloadPath}
          className="mt-7 inline-flex items-center gap-2 rounded-lg bg-[var(--color-accent)] px-6 py-3 text-sm font-semibold text-[#ffffff] hover:bg-[var(--color-accent-hot)] transition-colors"
        >
          <Download size={16} aria-hidden />
          {m.cta.button}
        </Link>
      </div>
    </section>
  );
}
