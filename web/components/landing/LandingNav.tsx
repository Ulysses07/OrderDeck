'use client';
import { useEffect, useState } from 'react';

/** Sticky nav — scroll'da (>12px) zemin koyulaşır + alt çizgi belirir. */
export default function LandingNav() {
  const [scrolled, setScrolled] = useState(false);

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 12);
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => window.removeEventListener('scroll', onScroll);
  }, []);

  return (
    <header className={`nav${scrolled ? ' scrolled' : ''}`} id="nav">
      <div className="nav__inner">
        <a className="brand" href="#top" aria-label="OrderDeck">
          <span className="brand__mark" aria-hidden="true">
            <span className="brand__dot" />
          </span>
          <span className="brand__name">
            Order<b>Deck</b>
          </span>
        </a>
        <nav className="nav__links">
          <a href="#ozellikler">Özellikler</a>
          <a href="#cekilis">Çekiliş</a>
          <a href="#shopper">Mobil</a>
          <a href="#fiyat">Fiyat</a>
          <a href="#sss">SSS</a>
        </nav>
        <div className="nav__actions">
          <a className="nav__lang" href="/en/">EN</a>
          <a href="#indir" className="btn btn--primary btn--sm">
            İndir
          </a>
        </div>
      </div>
    </header>
  );
}
