'use client';
import { useState } from 'react';
import { landingCopy, type LandingLocale } from './copy';

/** SSS akordeonu — tek seferde bir açık (max-height geçişi). */
export default function Faq({ locale }: { locale: LandingLocale }) {
  const [open, setOpen] = useState<number | null>(null);
  const items = landingCopy[locale].faq;

  return (
    <div className="acc" id="acc">
      {items.map(([q, a], i) => {
        const isOpen = open === i;
        return (
          <div className={`acc__item${isOpen ? ' open' : ''}`} key={i}>
            <button
              className="acc__q"
              aria-expanded={isOpen}
              onClick={() => setOpen(isOpen ? null : i)}
            >
              {q}
              <i />
            </button>
            <div className="acc__a" style={{ maxHeight: isOpen ? '320px' : 0 }}>
              <p>{a}</p>
            </div>
          </div>
        );
      })}
    </div>
  );
}
