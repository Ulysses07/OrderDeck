import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { FAQList } from '@/components/FAQList';
import { CtaBanner } from '@/components/CtaBanner';

export const metadata: Metadata = {
  title: 'Frequently asked questions',
  description: 'Common questions about OrderDeck — installation, licensing, platform support, privacy.',
};

export default function FaqEn() {
  return (
    <>
      <Nav locale="en" routeKey="faq" />
      <main>
        <FAQList locale="en" />
        <CtaBanner locale="en" />
      </main>
      <Footer locale="en" />
    </>
  );
}
