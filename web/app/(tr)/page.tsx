import type { Metadata } from 'next';
import '../landing.css';
import { LATEST_RELEASE, downloadUrl, BRAND } from '@/lib/i18n';
import LandingNav from '@/components/landing/LandingNav';
import StudioPanel from '@/components/landing/StudioPanel';
import BigPrinter from '@/components/landing/BigPrinter';
import SecGiveaway from '@/components/landing/SecGiveaway';
import Faq from '@/components/landing/Faq';
import RevealObserver from '@/components/landing/RevealObserver';

export const metadata: Metadata = {
  title: `${BRAND} — Canlı satışın komuta merkezi`,
  description:
    'Instagram, TikTok, Facebook ve YouTube canlı yayın sohbetlerini tek pencerede birleştir, anında termal etiket bas, sahte dekontu yakala, çark çevirerek çekiliş yap. Mezat yayıncıları için Windows masaüstü uygulaması.',
  openGraph: {
    title: `${BRAND} — Canlı satışın komuta merkezi`,
    description:
      'Canlı yayın sohbetlerini birleştir, anında etiket bas, çark çevirerek çekiliş yap.',
  },
};

const TICKER = [
  'INSTAGRAM', 'TIKTOK', 'FACEBOOK', 'YOUTUBE', 'ANLIK ETİKET', 'DEKONT DOĞRULAMA',
  'ÇARK ÇEKİLİŞİ', 'YEDEK SATIŞ', 'WHATSAPP ÖDEME', 'PUSH BİLDİRİM', 'YAYIN RAPORU', 'OBS OVERLAY',
];

function TickerUnit({ k }: { k: string }) {
  return (
    <span>
      {TICKER.map((b, i) => (
        <span className="ticker__item" key={`${k}-${i}`}>
          <b>{b}</b>
          <span className="x">✕</span>
        </span>
      ))}
    </span>
  );
}

export default function HomeTr() {
  return (
    <>
      <LandingNav />
      <main id="top">
        {/* ===== HERO ===== */}
        <section className="hero">
          <div className="hero__glow" aria-hidden="true" />
          <div className="wrap hero__grid">
            <div className="hero__copy reveal">
              <div className="eyebrow">
                <span className="eyebrow__live">
                  <i />
                  CANLI SATIŞ İÇİN
                </span>
                <span className="eyebrow__plat">Windows 10 / 11</span>
              </div>
              <h1 className="hero__title">
                <span>Sohbet akar.</span>
                <span>
                  Etiket <em>basılır.</em>
                </span>
                <span>Çark döner.</span>
              </h1>
              <p className="hero__sub">
                Instagram, TikTok, Facebook ve YouTube canlı sohbetleri tek pencerede.
                Müşteri “aldım” yazdığı an sipariş etikete döner, çekiliş OBS yayınında canlı
                döner — sen yayını hiç bölmezsin.
              </p>
              <div className="hero__cta">
                <a href="#indir" className="btn btn--primary btn--lg">
                  14 gün ücretsiz dene
                </a>
                <a href="#nasil" className="btn btn--ghost btn--lg">
                  <span className="play">▶</span> Nasıl çalışır
                </a>
              </div>
              <div className="hero__micro">
                <span>Kart bilgisi istemez</span>
                <i />
                <span>Kurulum 2 dakika</span>
                <i />
                <span>Veriler senin makinende</span>
              </div>
            </div>
            <StudioPanel />
          </div>
        </section>

        {/* ===== TICKER ===== */}
        <div className="ticker" aria-hidden="true">
          <div className="ticker__track">
            <TickerUnit k="a" />
            <TickerUnit k="b" />
          </div>
        </div>

        {/* ===== STAT STRIP ===== */}
        <section className="stats wrap">
          <div className="stat reveal">
            <b>4</b>
            <span>platform, tek pencere</span>
          </div>
          <div className="stat reveal">
            <b>Tek tık</b>
            <span>mesajdan termal etikete</span>
          </div>
          <div className="stat reveal">
            <b>≈%90+</b>
            <span>sahte dekont elenir</span>
          </div>
          <div className="stat reveal">
            <b>0</b>
            <span>veri sunucuya gider</span>
          </div>
        </section>

        {/* ===== 01 · YAYIN & SOHBET ===== */}
        <section className="sec" id="ozellikler">
          <div className="wrap">
            <div className="sec__head reveal">
              <span className="kicker">01 · YAYIN &amp; SOHBET</span>
              <h2>Dört platformu aynı anda dinle, gürültüyü sen duyma</h2>
              <p>
                Pencere değiştirmek yok. Bütün sohbet kronolojik tek akışta; trol ve spam sana
                ulaşmadan elenir.
              </p>
            </div>
            <div className="cards cards--2">
              <article className="card reveal">
                <div className="card__icon">
                  <span className="pp pp--ig">IG</span>
                  <span className="pp pp--tt">TT</span>
                  <span className="pp pp--fb">FB</span>
                  <span className="pp pp--yt">YT</span>
                  <span className="plat-arrow">→ tek liste</span>
                </div>
                <h3>4 platform tek panelde</h3>
                <p>
                  Instagram, TikTok, Facebook ve YouTube canlı sohbetleri aynı anda, kronolojik
                  tek listede akar. Ek hesap ya da API anahtarı oluşturman gerekmez — bağlan,
                  yayına başla.
                </p>
              </article>
              <article className="card reveal">
                <div className="card__icon">
                  <span className="strike">bit.ly/kazan…</span>
                  <span className="elim">→ elendi</span>
                </div>
                <h3>Spam &amp; trol filtresi</h3>
                <p>
                  Linkler, tekrar eden mesajlar, hep-büyük-harf yazımlar ve senin belirlediğin
                  yasaklı kelimeler sohbete hiç düşmez. Listeyi sen yönetirsin.
                </p>
              </article>
            </div>
          </div>
        </section>

        {/* ===== 02 · SİPARİŞ & ETİKET ===== */}
        <section className="spot">
          <div className="wrap spot__grid">
            <div className="spot__copy reveal">
              <span className="kicker">02 · SİPARİŞ &amp; ETİKET</span>
              <h2>
                “Aldım” yazıldığı an, etiket <em>yazıcıdan</em> çıkar
              </h2>
              <p>
                OrderDeck’in kalbi burası. Mesajı seç, fiyatı gir, Enter. Termal yazıcı gerisini
                halleder — yayın hiç durmaz.
              </p>
              <ul className="ticks">
                <li>
                  <b>Tek tıkla iptal</b> — yanlış etiket mi bastın? Neden seçenekleriyle anında
                  geri al.
                </li>
                <li>
                  <b>Yedek (standby) satış</b> — müşteri vazgeçerse sipariş otomatik yedek alıcıya
                  devreder.
                </li>
                <li>
                  <b>Kümülatif kargo eşiği</b> — toplam eşik altındaysa kargo satırı etikete
                  kendiliğinden eklenir.
                </li>
                <li>
                  <b>WhatsApp ile ödeme isteği</b> — IBAN/ödeme mesajı müşteriye tek tıkla gider.
                </li>
                <li>
                  <b>Yayın raporu &amp; geçmişi</b> — ciro, özet ve geçmiş yayınlar; tek tıkla
                  Excel’e.
                </li>
              </ul>
              <div className="spot__note">
                Argox, Zebra, TSC ve Windows’ta tanımlı tüm termal etiket yazıcılarıyla çalışır.
              </div>
            </div>
            <BigPrinter />
          </div>
        </section>

        {/* ===== 03 · GÜVENLİ TAHSİLAT ===== */}
        <section className="dekont">
          <div className="wrap dekont__grid">
            <div className="dekont__copy reveal">
              <span className="kicker kicker--amber">03 · GÜVENLİ TAHSİLAT</span>
              <h2>
                Sahte dekont gönderen <em>kapıdan giremez</em>
              </h2>
              <p>
                Müşteriden gelen banka dekontunu (WhatsApp/e-posta PDF) ekle; OrderDeck dekontu
                doğru müşteri ve siparişle otomatik eşleştirir, tutar ve gönderen uyuşmazlıklarını
                anında yakalar.
              </p>
              <ul className="ticks ticks--amber">
                <li>
                  <b>Otomatik eşleştirme</b> — dekont, doğru müşteri ve siparişle kendiliğinden
                  bağlanır.
                </li>
                <li>
                  <b>Uyuşmazlık alarmı</b> — tutar ya da gönderen tutmuyorsa işaretlenir, gözden
                  kaçmaz.
                </li>
                <li>
                  <b>“Ödedim” yalanına son</b> — ödemeden sipariş kapatan ya da sahte fiş atan
                  dolandırıcılığı keser.
                </li>
              </ul>
            </div>
            <div className="dekont__panel reveal">
              <div className="shield">
                <div className="shield__big">≈%90–95</div>
                <div className="shield__lbl">düzmece / sahte dekont otomatik elenir</div>
                <div className="dek dek--ok">
                  <span className="dek__ic">✓</span>
                  <div>
                    <b>@ayse34 · ₺250</b>
                    <i>dekont eşleşti · sipariş onaylandı</i>
                  </div>
                </div>
                <div className="dek dek--bad">
                  <span className="dek__ic">✕</span>
                  <div>
                    <b>@kullanici_x · ₺250</b>
                    <i>sahte dekont — tutar / gönderen uyuşmuyor</i>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* ===== 04 · ÇEKİLİŞ ===== */}
        <SecGiveaway />

        {/* ===== 05 · MÜŞTERİ & MOBİL ===== */}
        <section className="sec" id="shopper">
          <div className="wrap shop__grid">
            <div className="shop__copy reveal">
              <span className="kicker">05 · MÜŞTERİ &amp; MOBİL</span>
              <h2>Müşterini tanı, bakiyesini tut, uygulamasını ver</h2>
              <p>
                Yayında biriken her müşteri için bir kart: cari/bakiye, iade ve geçmiş tek yerde.
                Sorunlu kullanıcıları kara listeye al; gerisini Shopper halleder.
              </p>
              <div className="shop__feats">
                <div className="shopf reveal">
                  <span className="shopf__k">Cari &amp; bakiye</span>
                  <span>Müşteri kartı, bakiye, iade ve sipariş geçmişi.</span>
                </div>
                <div className="shopf reveal">
                  <span className="shopf__k">Kara liste</span>
                  <span>Sorunlu kullanıcıları işaretle, otomatik uzak tut.</span>
                </div>
                <div className="shopf reveal">
                  <span className="shopf__k">Shopper uygulaması</span>
                  <span>Müşteri kayıt olur, siparişlerini ve bakiyesini görür, parolasını kendi sıfırlar.</span>
                </div>
                <div className="shopf reveal">
                  <span className="shopf__k">Anında push bildirim</span>
                  <span>Yeni yayın, duyuru ve ödeme olaylarında müşteriye Shopper’dan anında bildirim.</span>
                </div>
                <div className="shopf reveal">
                  <span className="shopf__k">Duyuru &amp; akış</span>
                  <span>Metin + fotoğraf paylaş; müşteriler Shopper akışında görür, push alır.</span>
                </div>
                <div className="shopf reveal">
                  <span className="shopf__k">Özel kayıt formu</span>
                  <span>Yayına özel, özelleştirilebilir formla müşteri topla.</span>
                </div>
              </div>
            </div>
            <div className="shop__phones reveal">
              <div className="phone phone--back">
                <div className="phone__notch" />
                <div className="phone__push">
                  🔔 OrderDeck · <b>Yayın başladı!</b> Bu akşam 21:00
                </div>
                <div className="phone__screen phone__screen--list">
                  <div className="ps__top">
                    <span>Siparişlerim</span>
                    <span className="ps__bal">₺430</span>
                  </div>
                  <div className="ps__order">
                    <span className="ps__pp">IG</span>
                    <div>
                      <b>Mor triko · 38</b>
                      <i>Yayın #318 · ₺250</i>
                    </div>
                    <span className="ps__st ps__st--ok">Ödendi</span>
                  </div>
                  <div className="ps__order">
                    <span className="ps__pp ps__pp--tt">TT</span>
                    <div>
                      <b>Şal · krem</b>
                      <i>Yayın #318 · ₺120</i>
                    </div>
                    <span className="ps__st">Bekliyor</span>
                  </div>
                  <div className="ps__order">
                    <span className="ps__pp ps__pp--fb">FB</span>
                    <div>
                      <b>Kargo</b>
                      <i>Eşik altı · ₺60</i>
                    </div>
                    <span className="ps__st">Bekliyor</span>
                  </div>
                </div>
              </div>
              <div className="phone phone--front">
                <div className="phone__notch" />
                <div className="phone__screen phone__screen--bal">
                  <div className="pb__brand">Shopper</div>
                  <div className="pb__label">Güncel bakiye</div>
                  <div className="pb__amt">
                    ₺430<span>,00</span>
                  </div>
                  <div className="pb__row">
                    <span>Bekleyen ödeme</span>
                    <b>₺180</b>
                  </div>
                  <div className="pb__row">
                    <span>Bu ay sipariş</span>
                    <b>7</b>
                  </div>
                  <div className="pb__btn">WhatsApp’tan öde</div>
                  <div className="pb__hint">Parolanı uygulamadan kendin sıfırlayabilirsin.</div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* ===== TRUST ===== */}
        <section className="trust">
          <div className="wrap trust__grid">
            <div className="trustc reveal">
              <span className="trustc__k">Yerel ve gizli</span>
              <p>
                Sohbet verileri senin makinende kalır. OrderDeck sunucularına gönderilmez;
                uygulama kapanınca silinir.
              </p>
            </div>
            <div className="trustc reveal">
              <span className="trustc__k">Yedekleme &amp; geri yükleme</span>
              <p>Veriyi yedekle, geri yükle, lisansı başka makineye taşı. Arıza senin işini durdurmaz.</p>
            </div>
            <div className="trustc reveal">
              <span className="trustc__k">Otomatik güncelleme</span>
              <p>Yeni sürümler uygulama içinden gelir. Sen sadece yayına bak.</p>
            </div>
            <div className="trustc reveal">
              <span className="trustc__k">Toplu SMS kampanyaları</span>
              <p>
                Müşteri listene toplu kampanya. <em>Çok yakında.</em>
              </p>
            </div>
          </div>
        </section>

        {/* ===== PRICING ===== */}
        <section className="price" id="fiyat">
          <div className="wrap">
            <div className="sec__head sec__head--center reveal">
              <span className="kicker">FİYATLANDIRMA</span>
              <h2>Bir kez öde, ömür boyu senin</h2>
              <p>
                Abonelik yok. Aldığın sürüm senindir; ilk yıl güncellemeler dahil, sonrası tamamen
                opsiyonel.
              </p>
            </div>
            <div className="price__grid">
              <div className="plan plan--main reveal">
                <div className="plan__badge">ÖMÜR BOYU LİSANS</div>
                <div className="plan__price">
                  <b>250.000&nbsp;₺</b>
                  <span>tek seferlik · KDV dahil</span>
                </div>
                <div className="plan__feats">
                  <span>4 platform birden</span>
                  <span>Anlık etiket basımı</span>
                  <span>Yedek satış + kargo eşiği</span>
                  <span>WhatsApp ödeme isteği</span>
                  <span>Banka dekontu doğrulama</span>
                  <span>Çekiliş + OBS overlay</span>
                  <span>Kazanan etiketi otomatik</span>
                  <span>Müşteri / cari / bakiye</span>
                  <span>Shopper + push bildirim</span>
                  <span>Duyuru &amp; özel kayıt formu</span>
                  <span>Yayın raporu + geçmiş → Excel</span>
                  <span>Spam &amp; trol filtresi</span>
                  <span>Yedekleme &amp; geri yükleme</span>
                  <span>İlk yıl güncelleme dahil</span>
                </div>
                <div className="plan__cta">
                  <a href="#indir" className="btn btn--primary btn--lg">
                    Lisans al
                  </a>
                  <span>Tek kişiye/işletmeye özel · WhatsApp ile aktivasyon</span>
                </div>
              </div>
              <div className="plan__side">
                <div className="plan plan--addon reveal">
                  <span className="kicker">SONRAKİ YILLAR — OPSİYONEL</span>
                  <div className="addon__top">
                    <b>Güncelleme + destek</b>
                    <span className="addon__price">
                      10.000&nbsp;₺<i>/yıl</i>
                    </span>
                  </div>
                  <p>
                    Yeni özellikler, platform değişiklikleri için API tamirleri, öncelikli e-posta
                    desteği (24 saat içinde yanıt). İlk yıl ücretsiz dahildir; almazsan mevcut sürüm
                    çalışmaya devam eder.
                  </p>
                </div>
                <div className="plan plan--ctx reveal">
                  <span className="kicker kicker--amber">NEYİ ORTADAN KALDIRIR</span>
                  <p>
                    İkinci telefonu, açık unutulan sekmeleri, kayıp siparişi ve “kim ne aldı”
                    kavgasını. Tek pencere, tek akış, temiz kayıt.
                  </p>
                  <div className="plan__trial">14 gün ücretsiz tam deneme · kart bilgisi istemez</div>
                </div>
              </div>
            </div>
          </div>
        </section>

        {/* ===== FAQ ===== */}
        <section className="sec faq" id="sss">
          <div className="wrap faq__wrap">
            <div className="sec__head reveal">
              <span className="kicker">SIKÇA SORULANLAR</span>
              <h2>Merak edilenler</h2>
            </div>
            <Faq />
          </div>
        </section>

        {/* ===== DOWNLOAD ===== */}
        <section className="download" id="indir">
          <div className="download__glow" aria-hidden="true" />
          <div className="wrap">
            <div className="sec__head sec__head--center reveal">
              <span className="kicker">İNDİR</span>
              <h2>OrderDeck’i bu akşam kur</h2>
              <p>
                Windows 10 (22H2+) ve 11 için self-contained kurulum — ek .NET ya da başka runtime
                kurmana gerek yok.
              </p>
            </div>
            <div className="dl__card reveal">
              <div className="dl__meta">
                <div>
                  <span>SÜRÜM</span>
                  <b>v{LATEST_RELEASE.version}</b>
                </div>
                <div>
                  <span>BOYUT</span>
                  <b>{LATEST_RELEASE.sizeMB} MB</b>
                </div>
                <div>
                  <span>YAYIN TARİHİ</span>
                  <b>{LATEST_RELEASE.releasedAt}</b>
                </div>
              </div>
              <a href={downloadUrl()} className="btn btn--primary btn--xl dl__btn">
                ⬇ Şimdi indir <small>({LATEST_RELEASE.filename})</small>
              </a>
              <span className="dl__sub">14 gün tam deneme · kart bilgisi istemez</span>
            </div>
            <div className="dl__grid">
              <div className="dl__box reveal">
                <h4>
                  <span className="dl__warn">⚠</span> SmartScreen uyarısı çıkarsa
                </h4>
                <p>
                  OrderDeck şimdilik kod imzalama sertifikasız dağıtılıyor (2026 Q3’te eklenecek).
                  Windows “PC’nizi korudu” uyarısı gösterirse:
                </p>
                <ol>
                  <li>“Daha fazla bilgi” linkine tıkla</li>
                  <li>“Yine de çalıştır” butonuna bas</li>
                </ol>
              </div>
              <div className="dl__box reveal">
                <h4>Kurulumdan sonra</h4>
                <ol>
                  <li>İlk açılışta kurulum sihirbazı otomatik başlar.</li>
                  <li>
                    6 adımda lisans aktivasyonu, YouTube kanal ayarı, Chrome eklentisi ve OBS browser
                    source URL’leri ayarlanır.
                  </li>
                  <li>Chrome eklentisi mağaza onayı beklerken sihirbaz sideload adımlarını gösterir.</li>
                </ol>
              </div>
            </div>
            <div className="dl__req reveal">
              <h4>Sistem gereksinimleri</h4>
              <div className="dl__reqgrid">
                <span>Windows 10 (22H2+) veya Windows 11</span>
                <span>64-bit işlemci (Intel/AMD veya ARM64)</span>
                <span>~500 MB boş disk alanı</span>
                <span>Lisans aktivasyonu için internet bağlantısı</span>
                <span>Google Chrome (canlı yayın chat’i için)</span>
              </div>
            </div>
          </div>
        </section>
      </main>

      {/* ===== FOOTER ===== */}
      <footer className="foot">
        <div className="wrap foot__grid">
          <div className="foot__brand">
            <a className="brand" href="#top">
              <span className="brand__mark">
                <span className="brand__dot" />
              </span>
              <span className="brand__name">
                Order<b>Deck</b>
              </span>
            </a>
            <p>Türk mezat yayıncıları için tek pencerede sohbet, etiket ve çekiliş.</p>
            <p className="foot__contact">Musa Sevinç · support@orderdeckapp.com</p>
          </div>
          <div className="foot__col">
            <h4>Ürün</h4>
            <a href="#ozellikler">Özellikler</a>
            <a href="#fiyat">Fiyatlandırma</a>
            <a href="#sss">SSS</a>
            <a href="/blog/">Blog</a>
          </div>
          <div className="foot__col">
            <h4>Yasal</h4>
            <a href="/gizlilik-politikasi/">Gizlilik Politikası</a>
            <a href="/kullanim-kosullari/">Kullanım Koşulları</a>
            <a href="/iletisim/">İletişim</a>
          </div>
        </div>
        <div className="wrap foot__bottom">
          <span>© 2026 Musa Sevinç. Tüm hakları saklıdır.</span>
          <span>v{LATEST_RELEASE.version}</span>
        </div>
      </footer>

      <RevealObserver />
    </>
  );
}
