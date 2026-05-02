/**
 * TR mesaj sözlüğü. Komponentler `import { tr } from '@/messages/tr'` ile alıyor.
 * EN paralelini /messages/en.ts'de tut — anahtarlar bire bir aynı olmalı.
 */
export const tr = {
  nav: {
    features: 'Özellikler',
    pricing: 'Fiyatlandırma',
    blog: 'Blog',
    faq: 'SSS',
    contact: 'İletişim',
    download: 'İndir',
    login: 'Giriş',
  },

  footer: {
    tagline: 'Türk mezat yayıncıları için tek pencerede chat, etiket ve çekiliş.',
    productLabel: 'Ürün',
    legalLabel: 'Yasal',
    companyLabel: 'Şirket',
    privacy: 'Gizlilik Politikası',
    terms: 'Kullanım Koşulları',
    rights: 'Tüm hakları saklıdır.',
  },

  hero: {
    eyebrow: 'Mezat yayıncıları için',
    title: 'Yayında akan her sipariş, tek pencerede.',
    subtitle:
      'Instagram, TikTok, Facebook ve YouTube canlı yayın chat\'lerini birleştirir; kod ve fiyat girer girmez etiketi yazıcıya gönderir, çekilişleri çark çevirerek yapar.',
    ctaPrimary: 'Ücretsiz Dene',
    ctaSecondary: 'Özellikleri Gör',
    runtimeHint: 'Windows 10/11 · 14 gün ücretsiz deneme · Kart bilgisi istemez',
  },

  features: {
    title: 'Yayında ihtiyacın olan her şey',
    subtitle: 'Mezat akışını kesintiye uğratmadan tek bir araçla yönet.',
    items: [
      {
        title: 'Tek panelde 4 platform',
        body:
          'Instagram, TikTok, Facebook ve YouTube canlı yayın chat\'lerini aynı anda dinler; mesajları kronolojik tek listede gösterir.',
      },
      {
        title: 'Anlık etiket basımı',
        body:
          'Müşteri yazdığı an mesajı seç, fiyatı gir, etiket yazıcıdan çıksın. Yanlış mı oldu? Tek tıkla iptal — neden seçenekleri hazır.',
      },
      {
        title: 'Çark çevirerek çekiliş',
        body:
          'Anahtar kelimeyi söyle, katılımcılar otomatik toplansın. Yayında çark dönsün, kazananı izleyiciler birlikte görsün.',
      },
      {
        title: 'Spam ve trol filtresi',
        body:
          'Linkler, tekrar mesajlar, hep büyük harf yazımları ve operatörün belirlediği kelimeler chat\'e ulaşmadan elenir.',
      },
      {
        title: 'YouTube moderasyon entegrasyonu',
        body:
          'Yayın sırasında bir mesajı sil veya kullanıcıyı banla — OrderDeck doğrudan YouTube API üzerinden işlemi tamamlar.',
      },
      {
        title: 'Yerli ve ofis-altı',
        body:
          'Veriler senin makinende. Mesajlar OrderDeck sunucularına gitmez. Açtın bağlandın, kapattın silindi.',
      },
    ],
  },

  pricing: {
    title: 'Sade fiyatlandırma',
    subtitle:
      'Tek plan, tek seferlik ödeme. Ömür boyu kullan, istersen güncellemeleri yıllık paket ile takip et.',
    yearly: 'yıllık',
    perYear: '/yıl',
    cta: 'Lisans Al',
    contactCta: 'Bize yaz',
    plan: {
      badge: 'Ömür boyu',
      name: 'OrderDeck Lisansı',
      tagline: 'Bir kez öde, tüm platformlarla sınırsız kullan.',
      price: '100.000 ₺',
      priceNote: 'tek seferlik',
      priceSubnote: '14 gün ücretsiz deneme · Kart bilgisi istemez',
      features: [
        '4 platform birden (Instagram + TikTok + Facebook + YouTube)',
        'Anlık etiket basımı',
        'Çark çevirerek çekiliş',
        'YouTube moderasyon API entegrasyonu',
        'Gelişmiş spam ve trol filtresi',
        'Etiket geçmişi ve raporlama',
        'Tek kişiye/işletmeye özel lisans',
        'Lisans aldığınız sürümü ömür boyu kullanma hakkı',
      ],
    },
    support: {
      name: 'Yıllık güncelleme + destek paketi',
      tagline:
        'Yeni özellikler, platform değişiklikleri için API tamirleri ve öncelikli destek. Opsiyonel — almazsan mevcut sürümü çalıştırmaya devam edersin.',
      price: '10.000 ₺',
      priceNote: '/yıl',
      features: [
        'Tüm yeni özelliklere erişim',
        'Instagram, TikTok, Facebook ve YouTube için API tamirleri',
        'Öncelikli e-posta desteği (24 saat içinde yanıt)',
        'Yeni Windows sürümleri için güncellemeler',
      ],
      note:
        'İlk yıl ücretsiz dahildir. Sonraki yıllarda almak istersen panelden açabilirsin; ara verip sonra geri dönmek de mümkün.',
    },
    note:
      'Fiyatlar KDV dahildir. Kurumsal/çoklu lisans veya iade soruları için bizimle iletişime geç.',
  },

  faq: {
    title: 'Sıkça sorulanlar',
    items: [
      {
        q: 'OrderDeck nedir?',
        a: 'OrderDeck; Instagram, TikTok, Facebook ve YouTube canlı yayın chat\'lerini birleştiren, etiket basan ve çekilişleri yöneten bir Windows uygulamasıdır. Mezat yayını yapan satıcılar için tasarlandı.',
      },
      {
        q: 'Hangi yazıcılar destekleniyor?',
        a: 'Windows üzerinde tanımlı her termal etiket yazıcısı çalışır. Geliştirme sırasında Argox, Zebra ve TSC modelleriyle test edildi.',
      },
      {
        q: 'Yayın platformları için ek hesap/API anahtarı gerekiyor mu?',
        a: 'Hayır. Instagram, TikTok ve Facebook için tarayıcı eklentisi üzerinden yayını dinliyoruz. YouTube için OrderDeck\'in kendi onaylı YouTube API uygulamasını kullanıyoruz; sen sadece YouTube hesabınla giriş yapıyorsun.',
      },
      {
        q: 'Mesajlar bir yerde saklanıyor mu?',
        a: 'Hayır. Tüm mesajlar yalnızca senin makinende, son 500 mesaja kadar bellek tamponunda tutulur. Uygulama kapandığında silinir. OrderDeck sunucularına gönderilmez.',
      },
      {
        q: 'Deneme süresi nasıl çalışıyor?',
        a: 'İlk kurulumda 14 gün ücretsiz tam deneme aktiftir. Süre dolduğunda lisans almazsan uygulama salt-okunur moda geçer; eski etiketlerin korunur ama yeni yayın açamazsın.',
      },
      {
        q: 'Lisans birden fazla makinede çalışır mı?',
        a: 'Tek lisans, tek makineye bağlıdır. Yedek bilgisayar için makineler arası transfer destek talebi açmadan da panelden yapılabilir (ayda 2 kez).',
      },
      {
        q: 'YouTube moderasyon (mesaj sil, kullanıcı banla) nasıl çalışıyor?',
        a: 'Lisansının dahili bir özelliği; YouTube\'a giriş yapıp izin verdikten sonra chat\'te bir mesaja sağ tıklayarak silebilir veya kullanıcıyı banlayabilirsin. İşlem doğrudan YouTube\'a gönderilir.',
      },
      {
        q: 'Lisans bir kez ödenince güncellemeleri ne kadar süre alırım?',
        a: 'Lisans aldığın anda mevcut olan sürümü ömür boyu kullanmaya devam edebilirsin; ilk yıl tüm güncellemeler ücretsiz dahil. Sonraki yıllarda yeni özellik ve API tamirlerini almak istersen 10.000 ₺/yıl güncelleme paketi ekleyebilirsin. Almazsan eski sürüm çalışmaya devam eder.',
      },
      {
        q: 'Neden ömür boyu lisans yerine abonelik değil?',
        a: 'OrderDeck dış platformlara (Instagram, TikTok, Facebook, YouTube) bağlı olduğundan platformlar değiştikçe sürekli bakım yapıyoruz. Sen bir kez ödeyip sahip olursun — biz de güncelleme paketi ile bakımı sürdürürüz. İkili kazanım bu modelde mantıklı geliyor.',
      },
    ],
  },

  contact: {
    title: 'İletişim',
    subtitle:
      'Soru, talep ve geri bildirimlerin için en hızlı ulaşım yolu e-posta.',
    emailLabel: 'E-posta',
    responseTime: 'Genellikle 24 saat içinde yanıtlıyoruz (hafta içi).',
    privacyLink: 'Gizlilik politikamız',
    privacyText:
      'kişisel veri işleme şeklimizi açıklar. E-posta adresin sadece sana yanıt vermek için kullanılır.',
  },

  cta: {
    title: 'Hemen başla',
    subtitle:
      'Kart bilgisi istemiyoruz. Tüm özelliklerle 14 gün dene; beğenmezsen kapat.',
    button: 'Ücretsiz İndir',
  },

  langSwitch: {
    label: 'Dil',
    tr: 'Türkçe',
    en: 'English',
  },
};

export type Messages = typeof tr;
