import Link from 'next/link';
import { Download, ArrowRight } from 'lucide-react';
import { ROUTE_MAP, type Locale } from '@/lib/i18n';
import { tr } from '@/messages/tr';
import { en } from '@/messages/en';

export function Hero({ locale }: { locale: Locale }) {
  const m = locale === 'tr' ? tr : en;
  const path = (k: keyof typeof ROUTE_MAP) => ROUTE_MAP[k][locale];

  return (
    <section className="relative overflow-hidden">
      {/* Arkaplan gradient — sıcak altın ışık dark surface üzerine yumuşak iniyor */}
      <div
        aria-hidden
        className="pointer-events-none absolute inset-0 -z-10 opacity-60"
        style={{
          background:
            'radial-gradient(60% 60% at 50% 0%, rgba(32,197,247,0.18) 0%, rgba(15,99,245,0.06) 40%, transparent 75%)',
        }}
      />
      <div className="mx-auto max-w-6xl px-5 pt-20 pb-16 md:pt-28 md:pb-24">
        <div className="text-center">
          <span className="inline-block rounded-full border border-[var(--color-border-strong)] bg-[var(--color-surface)] px-3 py-1 text-xs font-medium uppercase tracking-wider text-[var(--color-accent)]">
            {m.hero.eyebrow}
          </span>
          <h1 className="mt-6 mx-auto max-w-3xl text-4xl font-bold leading-[1.1] tracking-tight md:text-6xl">
            {m.hero.title}
          </h1>
          <p className="mt-6 mx-auto max-w-2xl text-base leading-relaxed text-[var(--color-text-dim)] md:text-lg">
            {m.hero.subtitle}
          </p>
          <div className="mt-10 flex flex-col items-center justify-center gap-3 sm:flex-row">
            <a
              href="#download"
              className="inline-flex items-center gap-2 rounded-lg bg-[var(--color-accent)] px-5 py-3 text-sm font-semibold text-[#ffffff] hover:bg-[var(--color-accent-hot)] transition-colors"
            >
              <Download size={16} aria-hidden />
              {m.hero.ctaPrimary}
            </a>
            <Link
              href={path('features')}
              className="inline-flex items-center gap-2 rounded-lg border border-[var(--color-border-strong)] px-5 py-3 text-sm font-semibold text-[var(--color-text)] hover:border-[var(--color-accent)] hover:text-[var(--color-accent)] transition-colors"
            >
              {m.hero.ctaSecondary}
              <ArrowRight size={16} aria-hidden />
            </Link>
          </div>
          <p className="mt-6 text-xs text-[var(--color-text-mute)]">
            {m.hero.runtimeHint}
          </p>
        </div>

        {/* Mock app preview — gerçek screenshot gelene kadar UI mockup'ı */}
        <div className="mt-16 mx-auto max-w-5xl">
          <div className="rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-2 shadow-2xl">
            <div className="flex items-center gap-1.5 border-b border-[var(--color-border)] px-3 py-2">
              <span className="h-2.5 w-2.5 rounded-full bg-[var(--color-danger)]" />
              <span className="h-2.5 w-2.5 rounded-full bg-[var(--color-accent)]" />
              <span className="h-2.5 w-2.5 rounded-full bg-[var(--color-success)]" />
              <span className="ml-3 font-mono text-[10px] uppercase tracking-wider text-[var(--color-text-mute)]">
                OrderDeck — canlı yayın
              </span>
            </div>
            <div className="grid grid-cols-1 gap-2 p-3 md:grid-cols-3">
              <div className="rounded-lg border border-[var(--color-border)] bg-[var(--color-bg)] p-3">
                <div className="text-[10px] uppercase tracking-wider text-[var(--color-text-mute)]">
                  Chat
                </div>
                <div className="mt-2 space-y-1.5 font-mono text-xs">
                  <div className="text-[var(--color-text-dim)]"><span className="text-pink-400">📷 ayse</span> aldım 250</div>
                  <div className="text-[var(--color-text-dim)]"><span className="text-rose-400">🎵 mehmet</span> XL var mı</div>
                  <div className="text-[var(--color-text-dim)]"><span className="text-blue-400">👥 fatma</span> aldım kırmızı 38</div>
                  <div className="text-[var(--color-text-dim)]"><span className="text-red-400">▶️ ali</span> bana da</div>
                  <div className="opacity-50 text-[var(--color-text-mute)]">spam: link  →  filtrelendi</div>
                </div>
              </div>
              <div className="rounded-lg border border-[var(--color-border)] bg-[var(--color-bg)] p-3">
                <div className="text-[10px] uppercase tracking-wider text-[var(--color-text-mute)]">
                  Etiket
                </div>
                <div className="mt-2 rounded border border-dashed border-[var(--color-border-strong)] p-3">
                  <div className="text-xs font-bold">ayse</div>
                  <div className="font-mono text-xs text-[var(--color-text-dim)]">aldım 250</div>
                  <div className="mt-2 text-2xl font-black tracking-tight text-[var(--color-accent)]">₺250</div>
                </div>
                <button className="mt-2 w-full rounded-md bg-[var(--color-accent)] py-1.5 text-xs font-semibold text-[#ffffff]" type="button">
                  Yazdır
                </button>
              </div>
              <div className="rounded-lg border border-[var(--color-border)] bg-[var(--color-bg)] p-3">
                <div className="text-[10px] uppercase tracking-wider text-[var(--color-text-mute)]">
                  Çekiliş
                </div>
                <div className="mt-2 grid place-items-center py-3">
                  <div className="relative h-20 w-20 rounded-full" style={{
                    background: 'conic-gradient(#ef4444 0 60deg, #f59e0b 60deg 120deg, #22c55e 120deg 180deg, #3b82f6 180deg 240deg, #a855f7 240deg 300deg, #ec4899 300deg 360deg)',
                  }}>
                    <div className="absolute inset-2 rounded-full bg-[var(--color-bg)] grid place-items-center text-lg">🎁</div>
                  </div>
                </div>
                <div className="text-center text-xs font-medium text-[var(--color-text)]">12 katılımcı</div>
              </div>
            </div>
          </div>
        </div>
      </div>
    </section>
  );
}
