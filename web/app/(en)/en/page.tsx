import type { Metadata } from 'next';
import '../../landing.css';
import { BRAND } from '@/lib/i18n';
import LandingPage from '@/components/landing/LandingPage';

export const metadata: Metadata = {
  title: `${BRAND} — The command center for live selling`,
  description:
    'Unify Instagram, TikTok, Facebook and YouTube live chat in one window, print thermal labels instantly, catch fake receipts, and run wheel giveaways. Windows desktop app for live-auction sellers.',
  openGraph: {
    title: `${BRAND} — The command center for live selling`,
    description:
      'Unify live chat, print labels instantly, run wheel giveaways on stream.',
  },
};

export default function HomeEn() {
  return <LandingPage locale="en" />;
}
