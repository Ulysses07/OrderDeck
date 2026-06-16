'use client';
import { useEffect, useRef, useState } from 'react';

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
export default function SecGiveaway() {
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <section className="cek" id="cekilis" ref={hostRef}>
      <div className="cek__glow" aria-hidden="true" />
      <div className="wrap cek__grid">
        <div className="cek__stage reveal">
          <div className="obs">
            <span className="obs__tag">OBS YAYIN KATMANI · CANLI</span>
            <div className="bigwheel-box">
              <div className="bigwheel" ref={wheelRef} />
              <div className="bigwheel__ptr" />
              <div className="bigwheel__center" onClick={spin}>
                {spinningLabel ? '…' : 'ÇEVİR'}
              </div>
            </div>
            <div className={`obs__winner${winner ? ' has' : ''}`}>
              {winner ? (
                <>
                  🏆 kazanan: <b>{winner}</b> · etiketi basıldı
                </>
              ) : spinningLabel ? (
                'çark dönüyor…'
              ) : (
                'kazanan bekleniyor…'
              )}
            </div>
          </div>
        </div>
        <div className="cek__copy reveal">
          <span className="kicker kicker--amber">04 · ÇEKİLİŞ</span>
          <h2>Çekiliş, izleyicinin gözü önünde döner</h2>
          <p>
            Anahtar kelimeyi söyle; yazan herkes otomatik listeye girer. Çark OBS yayın
            katmanında döner, kazananı binlerce kişi aynı anda görür. İtiraz yok, ekran
            görüntüsü isteyen yok.
          </p>
          <div className="cek__row">
            <div className="cek__pill">10+ çark &amp; animasyon</div>
            <div className="cek__pill">mükerrer katılım engeli</div>
          </div>
          <p className="cek__kicker">
            <b>Kazanan etiketi otomatik basılır.</b> Çekiliş biter bitmez kazananın etiketi
            yazıcıdan çıkar — ödülü kim aldı, kayıt altında.
          </p>
          <button className="btn btn--amber btn--lg" onClick={spin}>
            Çarkı çevir
          </button>
        </div>
      </div>
    </section>
  );
}
