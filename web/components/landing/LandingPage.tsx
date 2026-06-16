import { LATEST_RELEASE, downloadUrl } from '@/lib/i18n';
import { landingCopy, type LandingLocale } from './copy';
import LandingNav from './LandingNav';
import StudioPanel from './StudioPanel';
import BigPrinter from './BigPrinter';
import SecGiveaway from './SecGiveaway';
import Faq from './Faq';
import RevealObserver from './RevealObserver';

/**
 * Paylaşılan tek-sayfa landing. locale ile TR/EN kopyası copy.ts'ten çekilir;
 * hem (tr)/page hem (en)/en/page bunu render eder → kopya tek yerde, iki dil
 * tutarlı. Animasyonlu alt bileşenler client; bu kapsayıcı server.
 */
export default function LandingPage({ locale }: { locale: LandingLocale }) {
  const t = landingCopy[locale];
  const home = locale === 'tr' ? '/' : '/en/';

  return (
    <>
      <LandingNav locale={locale} />
      <main id="top">
        {/* HERO */}
        <section className="hero">
          <div className="hero__glow" aria-hidden="true" />
          <div className="wrap hero__grid">
            <div className="hero__copy reveal">
              <div className="eyebrow">
                <span className="eyebrow__live">
                  <i />
                  {t.hero.live}
                </span>
                <span className="eyebrow__plat">{t.hero.platform}</span>
              </div>
              <h1 className="hero__title">
                {t.hero.title.map((line) =>
                  line.includes(t.hero.titleEm) ? (
                    <span key={line}>
                      {line.replace(t.hero.titleEm, '')}
                      <em>{t.hero.titleEm}</em>
                    </span>
                  ) : (
                    <span key={line}>{line}</span>
                  ),
                )}
              </h1>
              <p className="hero__sub">{t.hero.sub}</p>
              <div className="hero__cta">
                <a href="#indir" className="btn btn--primary btn--lg">
                  {t.hero.ctaPrimary}
                </a>
                <a href="#nasil" className="btn btn--ghost btn--lg">
                  <span className="play">▶</span> {t.hero.ctaGhost}
                </a>
              </div>
              <div className="hero__micro">
                {t.hero.micro.map((mtxt, i) => (
                  <span key={mtxt} style={{ display: 'contents' }}>
                    <span>{mtxt}</span>
                    {i < t.hero.micro.length - 1 && <i />}
                  </span>
                ))}
              </div>
            </div>
            <StudioPanel locale={locale} />
          </div>
        </section>

        {/* TICKER */}
        <div className="ticker" aria-hidden="true">
          <div className="ticker__track">
            {[0, 1].map((dup) => (
              <span key={dup}>
                {t.ticker.map((b, i) => (
                  <span className="ticker__item" key={`${dup}-${i}`}>
                    <b>{b}</b>
                    <span className="x">✕</span>
                  </span>
                ))}
              </span>
            ))}
          </div>
        </div>

        {/* STAT STRIP */}
        <section className="stats wrap">
          {t.stats.map((s) => (
            <div className="stat reveal" key={s.s}>
              <b>{s.b}</b>
              <span>{s.s}</span>
            </div>
          ))}
        </section>

        {/* 01 · STREAM & CHAT */}
        <section className="sec" id="ozellikler">
          <div className="wrap">
            <div className="sec__head reveal">
              <span className="kicker">{t.sec01.kicker}</span>
              <h2>{t.sec01.h2}</h2>
              <p>{t.sec01.p}</p>
            </div>
            <div className="cards cards--2">
              <article className="card reveal">
                <div className="card__icon">
                  <span className="pp pp--ig">IG</span>
                  <span className="pp pp--tt">TT</span>
                  <span className="pp pp--fb">FB</span>
                  <span className="pp pp--yt">YT</span>
                  <span className="plat-arrow">{t.sec01.card1Arrow}</span>
                </div>
                <h3>{t.sec01.card1H}</h3>
                <p>{t.sec01.card1P}</p>
              </article>
              <article className="card reveal">
                <div className="card__icon">
                  <span className="strike">{t.sec01.card2Strike}</span>
                  <span className="elim">{t.sec01.card2Elim}</span>
                </div>
                <h3>{t.sec01.card2H}</h3>
                <p>{t.sec01.card2P}</p>
              </article>
            </div>
          </div>
        </section>

        {/* 02 · ORDER & LABEL */}
        <section className="spot">
          <div className="wrap spot__grid">
            <div className="spot__copy reveal">
              <span className="kicker">{t.sec02.kicker}</span>
              <h2>
                {t.sec02.h2a}
                <em>{t.sec02.h2em}</em>
                {t.sec02.h2b}
              </h2>
              <p>{t.sec02.p}</p>
              <ul className="ticks">
                {t.sec02.ticks.map(([b, rest]) => (
                  <li key={b}>
                    <b>{b}</b>
                    {rest}
                  </li>
                ))}
              </ul>
              <div className="spot__note">{t.sec02.note}</div>
            </div>
            <BigPrinter locale={locale} />
          </div>
        </section>

        {/* 03 · SECURE COLLECTION */}
        <section className="dekont">
          <div className="wrap dekont__grid">
            <div className="dekont__copy reveal">
              <span className="kicker kicker--amber">{t.sec03.kicker}</span>
              <h2>
                {t.sec03.h2a}
                <em>{t.sec03.h2em}</em>
                {t.sec03.h2b}
              </h2>
              <p>{t.sec03.p}</p>
              <ul className="ticks ticks--amber">
                {t.sec03.ticks.map(([b, rest]) => (
                  <li key={b}>
                    <b>{b}</b>
                    {rest}
                  </li>
                ))}
              </ul>
            </div>
            <div className="dekont__panel reveal">
              <div className="shield">
                <div className="shield__big">≈%90–95</div>
                <div className="shield__lbl">{t.sec03.shieldLbl}</div>
                <div className="dek dek--ok">
                  <span className="dek__ic">✓</span>
                  <div>
                    <b>{t.sec03.okName}</b>
                    <i>{t.sec03.okDesc}</i>
                  </div>
                </div>
                <div className="dek dek--bad">
                  <span className="dek__ic">✕</span>
                  <div>
                    <b>{t.sec03.badName}</b>
                    <i>{t.sec03.badDesc}</i>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* 04 · GIVEAWAY */}
        <SecGiveaway locale={locale} />

        {/* 05 · CUSTOMER & MOBILE */}
        <section className="sec" id="shopper">
          <div className="wrap shop__grid">
            <div className="shop__copy reveal">
              <span className="kicker">{t.shopper.kicker}</span>
              <h2>{t.shopper.h2}</h2>
              <p>{t.shopper.p}</p>
              <div className="shop__feats">
                {t.shopper.feats.map(([k, v]) => (
                  <div className="shopf reveal" key={k}>
                    <span className="shopf__k">{k}</span>
                    <span>{v}</span>
                  </div>
                ))}
              </div>
            </div>
            <div className="shop__phones reveal">
              <div className="phone phone--back">
                <div className="phone__notch" />
                <div className="phone__push">
                  {t.shopper.push[0]}
                  <b>{t.shopper.push[1]}</b>
                  {t.shopper.push[2]}
                </div>
                <div className="phone__screen phone__screen--list">
                  <div className="ps__top">
                    <span>{t.shopper.orders}</span>
                    <span className="ps__bal">₺430</span>
                  </div>
                  <div className="ps__order">
                    <span className="ps__pp">IG</span>
                    <div>
                      <b>{t.shopper.order1[0]}</b>
                      <i>{t.shopper.order1[1]}</i>
                    </div>
                    <span className="ps__st ps__st--ok">{t.shopper.order1[2]}</span>
                  </div>
                  <div className="ps__order">
                    <span className="ps__pp ps__pp--tt">TT</span>
                    <div>
                      <b>{t.shopper.order2[0]}</b>
                      <i>{t.shopper.order2[1]}</i>
                    </div>
                    <span className="ps__st">{t.shopper.order2[2]}</span>
                  </div>
                  <div className="ps__order">
                    <span className="ps__pp ps__pp--fb">FB</span>
                    <div>
                      <b>{t.shopper.order3[0]}</b>
                      <i>{t.shopper.order3[1]}</i>
                    </div>
                    <span className="ps__st">{t.shopper.order3[2]}</span>
                  </div>
                </div>
              </div>
              <div className="phone phone--front">
                <div className="phone__notch" />
                <div className="phone__screen phone__screen--bal">
                  <div className="pb__brand">Shopper</div>
                  <div className="pb__label">{t.shopper.balLabel}</div>
                  <div className="pb__amt">
                    ₺430<span>,00</span>
                  </div>
                  <div className="pb__row">
                    <span>{t.shopper.balPending}</span>
                    <b>₺180</b>
                  </div>
                  <div className="pb__row">
                    <span>{t.shopper.balMonth}</span>
                    <b>7</b>
                  </div>
                  <div className="pb__btn">{t.shopper.payBtn}</div>
                  <div className="pb__hint">{t.shopper.pwHint}</div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* TRUST */}
        <section className="trust">
          <div className="wrap trust__grid">
            {t.trust.map(([k, p, em]) => (
              <div className="trustc reveal" key={k}>
                <span className="trustc__k">{k}</span>
                <p>
                  {p}
                  {em && <em>{em}</em>}
                </p>
              </div>
            ))}
          </div>
        </section>

        {/* PRICING */}
        <section className="price" id="fiyat">
          <div className="wrap">
            <div className="sec__head sec__head--center reveal">
              <span className="kicker">{t.pricing.kicker}</span>
              <h2>{t.pricing.h2}</h2>
              <p>{t.pricing.p}</p>
            </div>
            <div className="price__grid">
              <div className="plan plan--main reveal">
                <div className="plan__badge">{t.pricing.badge}</div>
                <div className="plan__price">
                  <b>{t.pricing.price}</b>
                  <span>{t.pricing.priceNote}</span>
                </div>
                <div className="plan__feats">
                  {t.pricing.feats.map((f) => (
                    <span key={f}>{f}</span>
                  ))}
                </div>
                <div className="plan__cta">
                  <a href="#indir" className="btn btn--primary btn--lg">
                    {t.pricing.cta}
                  </a>
                  <span>{t.pricing.ctaNote}</span>
                </div>
              </div>
              <div className="plan__side">
                <div className="plan plan--addon reveal">
                  <span className="kicker">{t.pricing.addonKicker}</span>
                  <div className="addon__top">
                    <b>{t.pricing.addonTitle}</b>
                    <span className="addon__price">
                      {t.pricing.addonPrice}
                      <i>{t.pricing.addonPer}</i>
                    </span>
                  </div>
                  <p>{t.pricing.addonP}</p>
                </div>
                <div className="plan plan--ctx reveal">
                  <span className="kicker kicker--amber">{t.pricing.ctxKicker}</span>
                  <p>{t.pricing.ctxP}</p>
                  <div className="plan__trial">{t.pricing.trial}</div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* FAQ */}
        <section className="sec faq" id="sss">
          <div className="wrap faq__wrap">
            <div className="sec__head reveal">
              <span className="kicker">{t.faqTitle.kicker}</span>
              <h2>{t.faqTitle.h2}</h2>
            </div>
            <Faq locale={locale} />
          </div>
        </section>

        {/* DOWNLOAD */}
        <section className="download" id="indir">
          <div className="download__glow" aria-hidden="true" />
          <div className="wrap">
            <div className="sec__head sec__head--center reveal">
              <span className="kicker">{t.download.kicker}</span>
              <h2>{t.download.h2}</h2>
              <p>{t.download.p}</p>
            </div>
            <div className="dl__card reveal">
              <div className="dl__meta">
                <div>
                  <span>{t.download.version}</span>
                  <b>v{LATEST_RELEASE.version}</b>
                </div>
                <div>
                  <span>{t.download.size}</span>
                  <b>{LATEST_RELEASE.sizeMB} MB</b>
                </div>
                <div>
                  <span>{t.download.date}</span>
                  <b>{LATEST_RELEASE.releasedAt}</b>
                </div>
              </div>
              <a href={downloadUrl()} className="btn btn--primary btn--xl dl__btn">
                ⬇ {t.download.btn} <small>({LATEST_RELEASE.filename})</small>
              </a>
              <span className="dl__sub">{t.download.sub}</span>
            </div>
            <div className="dl__grid">
              <div className="dl__box reveal">
                <h4>
                  <span className="dl__warn">⚠</span> {t.download.warnH}
                </h4>
                <p>{t.download.warnP}</p>
                <ol>
                  {t.download.warnSteps.map((s) => (
                    <li key={s}>{s}</li>
                  ))}
                </ol>
              </div>
              <div className="dl__box reveal">
                <h4>{t.download.afterH}</h4>
                <ol>
                  {t.download.afterSteps.map((s) => (
                    <li key={s}>{s}</li>
                  ))}
                </ol>
              </div>
            </div>
            <div className="dl__req reveal">
              <h4>{t.download.reqH}</h4>
              <div className="dl__reqgrid">
                {t.download.req.map((r) => (
                  <span key={r}>{r}</span>
                ))}
              </div>
            </div>
          </div>
        </section>
      </main>

      {/* FOOTER */}
      <footer className="foot">
        <div className="wrap foot__grid">
          <div className="foot__brand">
            <a className="brand" href={home}>
              <span className="brand__mark">
                <span className="brand__dot" />
              </span>
              <span className="brand__name">
                Order<b>Deck</b>
              </span>
            </a>
            <p>{t.footer.tagline}</p>
            <p className="foot__contact">{t.footer.contact}</p>
          </div>
          <div className="foot__col">
            <h4>{t.footer.colProduct}</h4>
            {t.footer.product.map(([href, label]) => (
              <a href={href} key={label}>
                {label}
              </a>
            ))}
          </div>
          <div className="foot__col">
            <h4>{t.footer.colLegal}</h4>
            {t.footer.legal.map(([href, label]) => (
              <a href={href} key={label}>
                {label}
              </a>
            ))}
          </div>
        </div>
        <div className="wrap foot__bottom">
          <span>{t.footer.rights}</span>
          <span>v{LATEST_RELEASE.version}</span>
        </div>
      </footer>

      <RevealObserver />
    </>
  );
}
