import type { Locale } from '@/lib/i18n';
import { tr } from '@/messages/tr';
import { en } from '@/messages/en';

/**
 * Native <details>/<summary> kullanımı — JS gerektirmiyor, statik export'a
 * mükemmel uyuyor; klavye + ekran okuyucu erişimi tarayıcıdan geliyor.
 */
export function FAQList({ locale }: { locale: Locale }) {
  const m = locale === 'tr' ? tr : en;

  return (
    <section className="mx-auto max-w-3xl px-5 py-16">
      <h2 className="text-3xl font-bold tracking-tight md:text-4xl">{m.faq.title}</h2>
      <div className="mt-8 space-y-3">
        {m.faq.items.map((item, i) => (
          <details
            key={i}
            className="group rounded-lg border border-[var(--color-border)] bg-[var(--color-surface)] p-5 transition-colors open:border-[var(--color-border-strong)]"
          >
            <summary className="flex cursor-pointer list-none items-center justify-between gap-4 text-left font-medium">
              <span>{item.q}</span>
              <span
                aria-hidden
                className="grid h-6 w-6 shrink-0 place-items-center rounded-full bg-[var(--color-bg)] text-[var(--color-text-dim)] transition-transform group-open:rotate-45"
              >
                +
              </span>
            </summary>
            <p className="mt-3 text-sm leading-relaxed text-[var(--color-text-dim)]">{item.a}</p>
          </details>
        ))}
      </div>
    </section>
  );
}
