import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { Hero } from '@/components/Hero';
import { FeatureCard } from '@/components/FeatureCard';
import { PricingTable } from '@/components/PricingTable';
import { FAQList } from '@/components/FAQList';
import { CtaBanner } from '@/components/CtaBanner';
import { en } from '@/messages/en';
import { Layers, Printer, Sparkles, Shield, Youtube, Lock } from 'lucide-react';

const ICONS = [Layers, Printer, Sparkles, Shield, Youtube, Lock];

export default function HomeEn() {
  const m = en;

  return (
    <>
      <Nav locale="en" routeKey="home" />
      <main>
        <Hero locale="en" />

        <section id="features" className="mx-auto max-w-6xl px-5 py-16">
          <div className="text-center">
            <h2 className="text-3xl font-bold tracking-tight md:text-4xl">{m.features.title}</h2>
            <p className="mt-3 text-base text-[var(--color-text-dim)]">{m.features.subtitle}</p>
          </div>
          <div className="mt-12 grid gap-5 sm:grid-cols-2 lg:grid-cols-3">
            {m.features.items.map((item, i) => (
              <FeatureCard
                key={item.title}
                icon={ICONS[i] ?? Sparkles}
                title={item.title}
                body={item.body}
              />
            ))}
          </div>
        </section>

        <PricingTable locale="en" />
        <FAQList locale="en" />
        <CtaBanner locale="en" />
      </main>
      <Footer locale="en" />
    </>
  );
}
