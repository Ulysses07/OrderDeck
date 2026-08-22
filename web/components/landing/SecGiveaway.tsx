'use client';
import { useEffect, useRef, useState } from 'react';
import { landingCopy, type LandingLocale } from './copy';

const NAMES = [
  '@ayse34',
  '@mehmet_k',
  '@selin_24',
  '@gul.han',
  '@derya.b',
  '@burak.t',
  '@zey.no',
  '@esra_m',
  '@yusuf99',
  '@hatice.k',
];

/** 04 · Çekiliş bölümü — çark (sol) + kopya/buton (sağ) aynı state'i paylaşır. */
export default function SecGiveaway({ locale }: { locale: LandingLocale }) {
  const c = landingCopy[locale].giveaway;
  const [winner, setWinner] = useState<string | null>(null);
  const [spinningLabel, setSpinningLabel] = useState(false);
  const wheelRef = useRef<HTMLDivElement>(null);
  const hostRef = useRef<HTMLElement>(null);
  const rot = useRef(0);
  const spinning = useRef(false);
  const reduce = useRef(false);
  const timer = useRef(0);

  const spin = () => {
    if (spinning.current || !wheelRef.current) return;
    spinning.current = true;
    setSpinningLabel(true);
    setWinner(null);
    const turns = 4 + Math.random() * 3;
    const extra = Math.random() * 360;
    rot.current += turns * 360 + extra;
    wheelRef.current.style.transform = `rotate(${rot.current}deg)`;
    const w = NAMES[Math.floor(Math.random() * NAMES.length)];
    timer.current = window.setTimeout(
      () => {
        spinning.current = false;
        setSpinningLabel(false);
        setWinner(w);
      },
      reduce.current ? 50 : 4700,
    );
  };

  useEffect(() => {
    reduce.current = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    const host = hostRef.current;
    if (!host || reduce.current) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting) {
          window.setTimeout(spin, 500);
          io.disconnect();
        }
      },
      { threshold: 0.4 },
    );
    io.observe(host);
    return () => {
      io.disconnect();
      clearTimeout(timer.current);
    };
  }, []);

  return (
    <section className="cek" id="cekilis" ref={hostRef}>
      <div className="cek__glow" aria-hidden="true" />
      <div className="wrap cek__grid">
        <div className="cek__stage reveal">
          <div className="obs">
            <span className="obs__tag">{c.obsTag}</span>
            <div className="bigwheel-box">
              <div className="bigwheel" ref={wheelRef} />
              <div className="bigwheel__ptr" />
              <div className="bigwheel__center" onClick={spin}>
                {spinningLabel ? '…' : c.spin}
              </div>
            </div>
            <div className={`obs__winner${winner ? ' has' : ''}`}>
              {winner ? (
                <>
                  {c.winnerPre}
                  <b>{winner}</b>
                  {c.winnerPost}
                </>
              ) : spinningLabel ? (
                c.spinning
              ) : (
                c.waiting
              )}
            </div>
          </div>
        </div>
        <div className="cek__copy reveal">
          <span className="kicker kicker--amber">{c.kicker}</span>
          <h2>{c.h2}</h2>
          <p>{c.p}</p>
          <div className="cek__row">
            {c.pills.map((pill) => (
              <div className="cek__pill" key={pill}>
                {pill}
              </div>
            ))}
          </div>
          <p className="cek__kicker">
            <b>{c.noteB}</b>
            {c.note}
          </p>
          <button className="btn btn--amber btn--lg" onClick={spin}>
            {c.btn}
          </button>
        </div>
      </div>
    </section>
  );
}
