'use client';
import { useEffect } from 'react';

/**
 * Scroll-reveal: tüm `.reveal` öğelerini IntersectionObserver ile izler, görünüme
 * girince `.in` ekler. reduced-motion'da anında gösterir. Failsafe: fold üstündeki
 * öğeler ilk anda görünür kalır (asla opacity 0'da takılmaz).
 */
export default function RevealObserver() {
  useEffect(() => {
    const els = Array.from(document.querySelectorAll<HTMLElement>('.reveal'));
    if (els.length === 0) return;

    const reduce =
      window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    if (reduce) {
      els.forEach((el) => el.classList.add('in'));
      return;
    }

    els.forEach((el, i) => {
      el.style.transitionDelay = `${Math.min(i % 3, 2) * 0.07}s`;
    });

    const io = new IntersectionObserver(
      (entries) => {
        entries.forEach((e) => {
          if (e.isIntersecting) {
            (e.target as HTMLElement).classList.add('in');
            io.unobserve(e.target);
          }
        });
      },
      { rootMargin: '0px 0px -8% 0px' },
    );
    els.forEach((el) => io.observe(el));

    // Fold üstünü hemen göster (IO ilk frame'de tetiklenmezse boş kalmasın).
    const vh = window.innerHeight || document.documentElement.clientHeight;
    els.forEach((el) => {
      if (el.getBoundingClientRect().top < vh * 0.92) el.classList.add('in');
    });

    return () => io.disconnect();
  }, []);

  return null;
}
