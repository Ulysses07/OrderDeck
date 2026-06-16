'use client';
import { useEffect, useRef, useState } from 'react';

type Sample = { plat: string; user: string; item: string; price: number; cargo: boolean; standby: boolean };

const SAMPLES: Sample[] = [
  { plat: 'IG', user: 'ayse34', item: 'kırmızı triko · 38', price: 250, cargo: false, standby: false },
  { plat: 'TT', user: 'mehmet_k', item: 'kaban · L', price: 410, cargo: true, standby: false },
  { plat: 'FB', user: 'selin_24', item: 'mor elbise · M', price: 180, cargo: false, standby: true },
  { plat: 'YT', user: 'gul.han', item: 'şal · krem', price: 120, cargo: true, standby: false },
];

let bticketKey = 0;

export default function BigPrinter() {
  const [ticket, setTicket] = useState<(Sample & { key: number; total: number; time: string }) | null>(null);
  const [status, setStatus] = useState<'idle' | 'printed'>('idle');
  const ref = useRef<HTMLDivElement>(null);
  const idx = useRef(0);
  const statusTimer = useRef(0);

  const printBig = () => {
    const d = SAMPLES[idx.current % SAMPLES.length];
    idx.current++;
    const now = new Date();
    const time =
      String(now.getHours()).padStart(2, '0') + ':' + String(now.getMinutes()).padStart(2, '0');
    setTicket({ ...d, total: d.price + (d.cargo ? 60 : 0), time, key: ++bticketKey });
    setStatus('printed');
    clearTimeout(statusTimer.current);
    statusTimer.current = window.setTimeout(() => setStatus('idle'), 1400);
  };

  // Görünüme girince bir kez otomatik bas.
  useEffect(() => {
    const host = ref.current;
    if (!host) return;
    const io = new IntersectionObserver(
      (entries) => {
        if (entries[0].isIntersecting) {
          printBig();
          io.disconnect();
        }
      },
      { threshold: 0.3 },
    );
    io.observe(host);
    return () => {
      io.disconnect();
      clearTimeout(statusTimer.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="spot__demo reveal">
      <div className="bigprinter" ref={ref}>
        <div className="bigprinter__head">
          <span className="bp__dot" /> termal yazıcı · hazır
          <span className="bp__status" style={{ color: status === 'printed' ? 'var(--red)' : 'var(--amber)' }}>
            {status === 'printed' ? 'BASILDI' : 'BEKLİYOR'}
          </span>
        </div>
        <div className="bigprinter__slot" />
        <div className="bigprinter__feed">
          {ticket && (
            <div className="bticket" key={ticket.key}>
              <div className="bticket__top">ORDERDECK · YAYIN #318</div>
              <div className="bticket__perf" />
              <div className="bticket__rw">
                <span>
                  {ticket.plat} · @{ticket.user}
                </span>
                <span>{ticket.time}</span>
              </div>
              <div className="bticket__rw">
                <span>{ticket.item}</span>
                <span>x1</span>
              </div>
              {ticket.standby && (
                <div className="bticket__rw standby">
                  <span>YEDEK ALICI</span>
                  <span className="flag">STANDBY</span>
                </div>
              )}
              {ticket.cargo && (
                <div className="bticket__rw">
                  <span>kargo (eşik altı)</span>
                  <span>+₺60</span>
                </div>
              )}
              <div className="bticket__perf" />
              <div className="bticket__price">
                <span>TUTAR</span>
                <b>₺{ticket.total}</b>
              </div>
              <div className="bticket__bar" />
              <div className="bticket__foot">OrderDeck ile basıldı · {ticket.time}</div>
            </div>
          )}
        </div>
        <button className="bigprinter__btn" onClick={printBig}>
          Etiketi bas <kbd>F2</kbd>
        </button>
      </div>
    </div>
  );
}
