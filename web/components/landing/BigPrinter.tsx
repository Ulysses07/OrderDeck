'use client';
import { useEffect, useRef, useState } from 'react';
import { landingCopy, type LandingLocale } from './copy';

type Sample = { plat: string; user: string; item: string; price: number; cargo: boolean; standby: boolean };

let bticketKey = 0;

export default function BigPrinter({ locale }: { locale: LandingLocale }) {
  const c = landingCopy[locale].printer;
  const SAMPLES = landingCopy[locale].printerSamples as readonly Sample[];
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
          <span className="bp__dot" /> {c.head}
          <span className="bp__status" style={{ color: status === 'printed' ? 'var(--red)' : 'var(--amber)' }}>
            {status === 'printed' ? c.printed : c.idle}
          </span>
        </div>
        <div className="bigprinter__slot" />
        <div className="bigprinter__feed">
          {ticket && (
            <div className="bticket" key={ticket.key}>
              <div className="bticket__top">ORDERDECK · {c.yayin}</div>
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
                  <span>{c.standby}</span>
                  <span className="flag">STANDBY</span>
                </div>
              )}
              {ticket.cargo && (
                <div className="bticket__rw">
                  <span>{c.cargo}</span>
                  <span>+₺60</span>
                </div>
              )}
              <div className="bticket__perf" />
              <div className="bticket__price">
                <span>{c.amount}</span>
                <b>₺{ticket.total}</b>
              </div>
              <div className="bticket__bar" />
              <div className="bticket__foot">{c.foot} · {ticket.time}</div>
            </div>
          )}
        </div>
        <button className="bigprinter__btn" onClick={printBig}>
          {c.btn} <kbd>F2</kbd>
        </button>
      </div>
    </div>
  );
}
