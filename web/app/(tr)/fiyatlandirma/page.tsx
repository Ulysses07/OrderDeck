import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { PricingTable } from '@/components/PricingTable';
import { CtaBanner } from '@/components/CtaBanner';

export const metadata: Metadata = {
  title: 'Fiyatlandırma',
  description: 'OrderDeck — tek plan, ömür boyu lisans, opsiyonel yıllık güncelleme paketi. 14 gün ücretsiz deneme.',
};

export default function PricingTr() {
  return (
    <>
      <Nav locale="tr" routeKey="pricing" />
      <main>
        <PricingTable locale="tr" />
        <CtaBanner locale="tr" />
      </main>
      <Footer locale="tr" />
    </>
  );
}
