import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { PricingTable } from '@/components/PricingTable';
import { CtaBanner } from '@/components/CtaBanner';

export const metadata: Metadata = {
  title: 'Pricing',
  description: 'OrderDeck — single plan, lifetime license, optional yearly update pack. 14-day free trial.',
};

export default function PricingEn() {
  return (
    <>
      <Nav locale="en" routeKey="pricing" />
      <main>
        <PricingTable locale="en" />
        <CtaBanner locale="en" />
      </main>
      <Footer locale="en" />
    </>
  );
}
