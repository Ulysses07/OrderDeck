'use client';
import { useEffect, useState } from 'react';
import { landingCopy, type LandingLocale } from './copy';

/** Sticky nav — scroll'da (>12px) zemin koyulaşır + alt çizgi belirir. */
export default function LandingNav({ locale }: { locale: LandingLocale }) {
  const [scrolled, setScrolled] = useState(false);
  const c = landingCopy[locale].nav;
  const home = locale === 'tr' ? '/' : '/en/';

  useEffect(() => {
    const onScroll = () => setScrolled(window.scrollY > 12);
    onScroll();
    window.addEventListener('scroll', onScroll, { passive: true });
    return () => window.removeEventListener('scroll', onScroll);
  }, []);

  return (
    <header className={`nav${scrolled ? ' scrolled' : ''}`} id="nav">
      <div className="nav__inner">
        <a className="brand" href={home} aria-label="OrderDeck">
          <span className="brand__mark" aria-hidden="true">
            <span className="brand__dot" />
          </span>
          <span className="brand__name">
            Order<b>Deck</b>
          </span>
        </a>
        <nav className="nav__links">
          {c.links.map((l) => (
            <a href={l.href} key={l.href}>
              {l.label}
            </a>
          ))}
        </nav>
        <div className="nav__actions">
          <a className="nav__lang" href={c.langHref}>
            {c.lang}
          </a>
          <a href="#indir" className="btn btn--primary btn--sm">
            {c.download}
          </a>
        </div>
      </div>
    </header>
  );
}
