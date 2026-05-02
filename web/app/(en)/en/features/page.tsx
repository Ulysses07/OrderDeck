import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { FeatureCard } from '@/components/FeatureCard';
import { CtaBanner } from '@/components/CtaBanner';
import { en } from '@/messages/en';
import { Layers, Printer, Sparkles, Shield, Youtube, Lock } from 'lucide-react';

export const metadata: Metadata = {
  title: 'Features',
  description:
    'All of OrderDeck — chat aggregation, label printing, spinning-wheel giveaways, spam filter and YouTube moderation.',
};

const ICONS = [Layers, Printer, Sparkles, Shield, Youtube, Lock];

export default function FeaturesEn() {
  const m = en;
  return (
    <>
      <Nav locale="en" routeKey="features" />
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
        <CtaBanner locale="en" />
      </main>
      <Footer locale="en" />
    </>
  );
}
