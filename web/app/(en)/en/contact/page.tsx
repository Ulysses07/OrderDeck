import type { Metadata } from 'next';
import Link from 'next/link';
import { Mail } from 'lucide-react';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { en } from '@/messages/en';
import { CONTACT_EMAIL, ROUTE_MAP } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Contact',
  description: 'Get in touch with the OrderDeck team — support, sales, feedback.',
};

export default function ContactEn() {
  const m = en;

  return (
    <>
      <Nav locale="en" routeKey="contact" />
      <main className="mx-auto max-w-2xl px-5 py-16">
        <h1 className="text-3xl font-bold tracking-tight md:text-4xl">{m.contact.title}</h1>
        <p className="mt-3 text-base text-[var(--color-text-dim)]">{m.contact.subtitle}</p>

        <div className="mt-10 rounded-xl border border-[var(--color-border)] bg-[var(--color-surface)] p-6">
          <div className="flex items-start gap-4">
            <div className="grid h-10 w-10 place-items-center rounded-lg bg-[var(--color-bg)] text-[var(--color-accent)]">
              <Mail size={20} aria-hidden />
            </div>
            <div>
              <p className="text-sm text-[var(--color-text-mute)]">{m.contact.emailLabel}</p>
              <a
                href={`mailto:${CONTACT_EMAIL}`}
                className="mt-1 block text-lg font-semibold text-[var(--color-accent)] hover:text-[var(--color-accent-hot)]"
              >
                {CONTACT_EMAIL}
              </a>
              <p className="mt-3 text-xs text-[var(--color-text-mute)]">
                {m.contact.responseTime}
              </p>
            </div>
          </div>
        </div>

        <p className="mt-8 text-sm text-[var(--color-text-dim)]">
          <Link href={ROUTE_MAP.privacy.en} className="text-[var(--color-accent)] hover:underline">
            {m.contact.privacyLink}
          </Link>{' '}
          {m.contact.privacyText}
        </p>
      </main>
      <Footer locale="en" />
    </>
  );
}
