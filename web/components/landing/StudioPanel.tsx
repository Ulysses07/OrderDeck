'use client';
import { useEffect, useRef, useState } from 'react';
import { landingCopy, type LandingLocale } from './copy';

type Msg = { p: string; u: string; m: string; buy?: boolean; price?: number; item?: string };

let uid = 0;

export default function StudioPanel({ locale }: { locale: LandingLocale }) {
  const c = landingCopy[locale].studio;
  const POOL = landingCopy[locale].chatPool as readonly Msg[];
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
          setViewers(st.current.viewers.toLocaleString(locale === 'tr' ? 'tr-TR' : 'en-US'));
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
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  return (
    <div className="studio reveal" id="nasil" aria-label="OrderDeck">
      <div className="studio__bar">
        <div className="studio__dots">
          <i />
          <i />
          <i />
        </div>
        <span className="studio__file">{c.file}</span>
        <span className="studio__live">
          <i />
          {c.live} <b>{clock}</b>
        </span>
        <span className="studio__viewers">👁 <b>{viewers}</b></span>
        <button
          className="studio__pause"
          onClick={() => setPaused((p) => !p)}
          aria-label={paused ? c.pauseOff : c.pauseOn}
          title={paused ? c.pauseOff : c.pauseOn}
        >
          {paused ? '▶' : '⏸'}
        </button>
      </div>
      <div className="studio__body">
        {/* CHAT */}
        <div className="panel panel--chat">
          <div className="panel__head">
            <span>{c.chatHead}</span>
            <span className="panel__rate">{c.chatRate(rate)}</span>
          </div>
          <div className="chat">
            {rows.map((r) => (
              <div className={`chat__row${r.buy ? ' sel' : ''}`} key={r.id}>
                <span className="chat__pp" data-p={r.p}>
                  {r.p.toUpperCase()}
                </span>
                <span className="chat__u">{r.u}</span>
                <span className="chat__m">{r.m}</span>
                {r.buy && <span className="chat__tag">F2 → {locale === 'tr' ? 'ETİKET' : 'LABEL'}</span>}
              </div>
            ))}
          </div>
          <div className="chat__filter">{c.filter}</div>
        </div>
        {/* LABEL / PRINTER */}
        <div className="panel panel--label">
          <div className="panel__head">
            <span>{c.labelHead}</span>
          </div>
          <div className="labeler">
            <div className="labeler__form">
              <div className="lf__row">
                <span>{c.lfUser}</span>
                <b>{label.u}</b>
              </div>
              <div className="lf__row">
                <span>{c.lfMsg}</span>
                <b>{label.m}</b>
              </div>
              <div className="lf__price">
                ₺ <b>{label.price}</b>
              </div>
              <div className="lf__btn">
                {c.lfBtn} <kbd>⏎</kbd>
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
                  <span>{c.tkAmount}</span>
                  <b>₺{ticket.price}</b>
                </div>
                <div className="ticket__bar" />
                <div className="ticket__foot">{ticket.time} · {c.tkFoot}</div>
              </div>
            </div>
          </div>
        </div>
        {/* WHEEL */}
        <div className="panel panel--wheel">
          <div className="panel__head">
            <span>{c.wheelHead}</span>
          </div>
          <div className="wheelbox">
            <div className="wheel">
              <div className="wheel__hub" />
            </div>
            <div className="wheel__ptr" />
          </div>
          <div className="wheelbox__meta">
            <b>{parts}</b> {c.parts}
          </div>
        </div>
      </div>
    </div>
  );
}
