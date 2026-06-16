'use client';
import { useEffect, useRef, useState } from 'react';

type Msg = { p: string; u: string; m: string; buy?: boolean; price?: number; item?: string };

const POOL: Msg[] = [
  { p: 'ig', u: 'ayse34', m: 'aldım 250', buy: true, price: 250, item: 'kırmızı 38' },
  { p: 'tt', u: 'mehmet_k', m: 'XL var mı?' },
  { p: 'fb', u: 'fatma.gul', m: 'aldım kırmızı 38', buy: true, price: 180, item: 'kırmızı 38' },
  { p: 'yt', u: 'ali_eks', m: 'bana da ayır' },
  { p: 'ig', u: 'zey.no', m: 'çekilişe yazıldım' },
  { p: 'tt', u: 'derya.b', m: 'fiyat ne kadar' },
  { p: 'ig', u: 'selin_24', m: 'aldım 320', buy: true, price: 320, item: 'mor triko' },
  { p: 'fb', u: 'hatice.k', m: 'kargo dahil mi' },
  { p: 'yt', u: 'burak.t', m: 'aldım 150', buy: true, price: 150, item: 'şal krem' },
  { p: 'tt', u: 'esra_m', m: 'beden tablosu?' },
  { p: 'ig', u: 'gul.han', m: 'aldım 410', buy: true, price: 410, item: 'kaban L' },
  { p: 'fb', u: 'yusuf99', m: 'stok kaldı mı' },
];

let uid = 0;

export default function StudioPanel() {
  const [clock, setClock] = useState('02:14:09');
  const [viewers, setViewers] = useState('1.284');
  const [rate, setRate] = useState(186);
  const [parts, setParts] = useState(37);
  const [rows, setRows] = useState<(Msg & { id: number })[]>([]);
  const [label, setLabel] = useState({ u: '—', m: '—', price: '—' });
  const [ticket, setTicket] = useState({
    plat: 'IG',
    user: 'ayse34',
    msg: 'aldım 250 — kırmızı 38',
    price: 250,
    time: '21:34',
  });
  const [printKey, setPrintKey] = useState(0);
  const [paused, setPaused] = useState(false);

  const st = useRef({ h: 2, m: 14, s: 9, viewers: 1284, parts: 37, ci: 0, paused: false, reduce: false });

  useEffect(() => {
    st.current.paused = paused;
    document.body.classList.toggle('motion-paused', paused);
  }, [paused]);

  useEffect(() => {
    const reduce = window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ?? false;
    st.current.reduce = reduce;
    const pad = (n: number) => (n < 10 ? '0' : '') + n;
    const clockStr = () => pad(st.current.h) + ':' + pad(st.current.m);

    const fillLabel = (d: Msg) => {
      setLabel({ u: d.u, m: '"' + d.m + '"', price: String(d.price ?? '') });
      window.setTimeout(() => {
        setTicket({
          plat: d.p.toUpperCase(),
          user: d.u,
          msg: d.m + (d.item ? ' — ' + d.item : ''),
          price: d.price ?? 0,
          time: clockStr(),
        });
        if (!st.current.reduce) setPrintKey((k) => k + 1);
        if (Math.random() > 0.5) {
          st.current.parts++;
          setParts(st.current.parts);
        }
      }, 600);
    };

    const pushMsg = () => {
      const d = POOL[st.current.ci % POOL.length];
      st.current.ci++;
      setRows((prev) => {
        const next = [...prev, { ...d, id: ++uid }];
        while (next.length > 6) next.shift();
        return next;
      });
      if (d.buy) fillLabel(d);
    };

    // seed
    pushMsg();
    pushMsg();

    const intervals: number[] = [];
    let feedTimer = 0;

    if (!reduce) {
      intervals.push(
        window.setInterval(() => {
          if (st.current.paused) return;
          st.current.s++;
          if (st.current.s >= 60) {
            st.current.s = 0;
            st.current.m++;
          }
          if (st.current.m >= 60) {
            st.current.m = 0;
            st.current.h++;
          }
          setClock(pad(st.current.h) + ':' + pad(st.current.m) + ':' + pad(st.current.s));
        }, 1000),
      );
      intervals.push(
        window.setInterval(() => {
          if (st.current.paused) return;
          st.current.viewers += Math.floor(Math.random() * 21) - 8;
          if (st.current.viewers < 900) st.current.viewers = 900;
          setViewers(st.current.viewers.toLocaleString('tr-TR'));
          setRate(150 + Math.floor(Math.random() * 90));
        }, 2600),
      );
      const feed = () => {
        if (!st.current.paused) pushMsg();
        feedTimer = window.setTimeout(feed, 1300 + Math.random() * 1100);
      };
      feedTimer = window.setTimeout(feed, 1400);
    }

    return () => {
      intervals.forEach((id) => clearInterval(id));
      clearTimeout(feedTimer);
    };
  }, []);

  return (
    <div className="studio reveal" id="nasil" aria-label="OrderDeck canlı yayın paneli (canlandırma)">
      <div className="studio__bar">
        <div className="studio__dots">
          <i />
          <i />
          <i />
        </div>
        <span className="studio__file">OrderDeck — yayın #318</span>
        <span className="studio__live">
          <i />
          CANLI <b>{clock}</b>
        </span>
        <span className="studio__viewers">👁 <b>{viewers}</b></span>
        <button
          className="studio__pause"
          onClick={() => setPaused((p) => !p)}
          aria-label={paused ? 'Animasyonu sürdür' : 'Animasyonu duraklat'}
          title={paused ? 'Animasyonu sürdür' : 'Animasyonu duraklat'}
        >
          {paused ? '▶' : '⏸'}
        </button>
      </div>
      <div className="studio__body">
        {/* CHAT */}
        <div className="panel panel--chat">
          <div className="panel__head">
            <span>BİRLEŞİK SOHBET</span>
            <span className="panel__rate">
              4 platform · <b>{rate}</b>/dk
            </span>
          </div>
          <div className="chat">
            {rows.map((r) => (
              <div className={`chat__row${r.buy ? ' sel' : ''}`} key={r.id}>
                <span className="chat__pp" data-p={r.p}>
                  {r.p.toUpperCase()}
                </span>
                <span className="chat__u">{r.u}</span>
                <span className="chat__m">{r.m}</span>
                {r.buy && <span className="chat__tag">F2 → ETİKET</span>}
              </div>
            ))}
          </div>
          <div className="chat__filter">
            spam: link içeren 2 mesaj <b>filtrelendi</b>
          </div>
        </div>
        {/* LABEL / PRINTER */}
        <div className="panel panel--label">
          <div className="panel__head">
            <span>SİPARİŞ → ETİKET</span>
          </div>
          <div className="labeler">
            <div className="labeler__form">
              <div className="lf__row">
                <span>MÜŞTERİ</span>
                <b>{label.u}</b>
              </div>
              <div className="lf__row">
                <span>MESAJ</span>
                <b>{label.m}</b>
              </div>
              <div className="lf__price">
                ₺ <b>{label.price}</b>
              </div>
              <div className="lf__btn">
                Yazdır <kbd>⏎</kbd>
              </div>
            </div>
            <div className="printer">
              <div className="printer__slot" />
              <div className={`ticket${printKey > 0 ? ' print' : ''}`} key={printKey}>
                <div className="ticket__top">ORDERDECK · {ticket.plat}</div>
                <div className="ticket__perf" />
                <div className="ticket__user">{ticket.user}</div>
                <div className="ticket__msg">{ticket.msg}</div>
                <div className="ticket__perf" />
                <div className="ticket__price">
                  <span>TUTAR</span>
                  <b>₺{ticket.price}</b>
                </div>
                <div className="ticket__bar" />
                <div className="ticket__foot">{ticket.time} · YAYIN #318</div>
              </div>
            </div>
          </div>
        </div>
        {/* WHEEL */}
        <div className="panel panel--wheel">
          <div className="panel__head">
            <span>ÇEKİLİŞ</span>
          </div>
          <div className="wheelbox">
            <div className="wheel">
              <div className="wheel__hub" />
            </div>
            <div className="wheel__ptr" />
          </div>
          <div className="wheelbox__meta">
            <b>{parts}</b> katılımcı
          </div>
        </div>
      </div>
    </div>
  );
}
