import type { Metadata } from 'next';
import '../landing.css';
import { BRAND } from '@/lib/i18n';
import LandingPage from '@/components/landing/LandingPage';

export const metadata: Metadata = {
  title: `${BRAND} — Canlı satışın komuta merkezi`,
  description:
    'Instagram, TikTok, Facebook ve YouTube canlı yayın sohbetlerini tek pencerede birleştir, anında termal etiket bas, sahte dekontu yakala, çark çevirerek çekiliş yap. Mezat yayıncıları için Windows masaüstü uygulaması.',
  openGraph: {
    title: `${BRAND} — Canlı satışın komuta merkezi`,
    description:
      'Canlı yayın sohbetlerini birleştir, anında etiket bas, çark çevirerek çekiliş yap.',
  },
};

export default function HomeTr() {
  return <LandingPage locale="tr" />;
}
