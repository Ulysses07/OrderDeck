import type { Metadata } from 'next';
import { Nav } from '@/components/Nav';
import { Footer } from '@/components/Footer';
import { FAQList } from '@/components/FAQList';
import { CtaBanner } from '@/components/CtaBanner';

export const metadata: Metadata = {
  title: 'Sıkça sorulan sorular',
  description: 'OrderDeck hakkında sık sorulan sorular — kurulum, lisans, platform desteği, gizlilik.',
};

export default function FaqTr() {
  return (
    <>
      <Nav locale="tr" routeKey="faq" />
      <main>
        <FAQList locale="tr" />
        <CtaBanner locale="tr" />
      </main>
      <Footer locale="tr" />
    </>
  );
}
