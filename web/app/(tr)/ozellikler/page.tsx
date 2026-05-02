import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { FeatureCard } from '@/components/FeatureCard';
import { CtaBanner } from '@/components/CtaBanner';
import { tr } from '@/messages/tr';
import { Layers, Printer, Sparkles, Shield, Youtube, Lock } from 'lucide-react';

export const metadata: Metadata = {
  title: 'Özellikler',
  description:
    'OrderDeck\'in tüm özellikleri — chat birleştirme, etiket basımı, çark çevirme, spam filtresi ve YouTube moderasyon.',
};

const ICONS = [Layers, Printer, Sparkles, Shield, Youtube, Lock];

export default function FeaturesTr() {
  const m = tr;
  return (
    <>
      <Nav locale="tr" routeKey="features" />
      <main>
        <section className="mx-auto max-w-6xl px-5 py-16">
          <div className="text-center">
            <h1 className="text-4xl font-bold tracking-tight md:text-5xl">{m.features.title}</h1>
            <p className="mt-4 text-base text-[var(--color-text-dim)] md:text-lg">{m.features.subtitle}</p>
          </div>
          <div className="mt-14 grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
            {m.features.items.map((item, i) => (
              <FeatureCard key={item.title} icon={ICONS[i] ?? Sparkles} title={item.title} body={item.body} />
            ))}
          </div>
        </section>
        <CtaBanner locale="tr" />
      </main>
      <Footer locale="tr" />
    </>
  );
}
