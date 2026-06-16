'use client';
import { useState } from 'react';

const ITEMS: { q: string; a: string }[] = [
  {
    q: 'OrderDeck nedir?',
    a: 'Instagram, TikTok, Facebook ve YouTube canlı yayın sohbetlerini birleştiren, sipariş alıp etiket basan, çekiliş ve müşteri yöneten bir Windows masaüstü uygulaması. Mezat usulü canlı satış yapan yayıncılar için tasarlandı.',
  },
  {
    q: 'Hangi yazıcılar destekleniyor?',
    a: 'Windows üzerinde tanımlı her termal etiket yazıcısı çalışır. Geliştirme sırasında Argox, Zebra ve TSC modelleriyle test edildi.',
  },
  {
    q: 'Yayın platformları için ek hesap/API anahtarı gerekiyor mu?',
    a: 'Hayır. Ayrı bir API anahtarı oluşturman ya da geliştirici hesabı açman gerekmez; bağlanmak istediğin platform hesaplarınla giriş yapıp yayına başlarsın.',
  },
  {
    q: 'Mesajlar bir yerde saklanıyor mu?',
    a: 'Hayır. Tüm mesajlar yalnızca senin makinende, son 500 mesaja kadar bellekte tutulur ve uygulama kapanınca silinir. OrderDeck sunucularına gönderilmez.',
  },
  {
    q: 'Deneme süresi nasıl çalışıyor?',
    a: 'İlk kurulumda 14 gün ücretsiz tam deneme aktiftir. Süre dolunca lisans almazsan uygulama salt-okunur moda geçer; eski etiketlerin korunur ama yeni yayın açamazsın.',
  },
  {
    q: 'Lisans birden fazla makinede çalışır mı?',
    a: 'Tek lisans tek makineye bağlıdır. Yedek bilgisayar için makineler arası transferi panelden, destek talebi açmadan yapabilirsin (ayda 2 kez).',
  },
  {
    q: 'Neden abonelik değil de ömür boyu lisans?',
    a: 'OrderDeck dış platformlara bağlı olduğundan, platformlar değiştikçe sürekli bakım yapıyoruz. Sen bir kez ödeyip sahip olursun; biz de opsiyonel güncelleme paketiyle bakımı sürdürürüz. Bu model ikimiz için de adil.',
  },
];

/** SSS akordeonu — tek seferde bir açık (max-height geçişi). */
export default function Faq() {
  const [open, setOpen] = useState<number | null>(null);

  return (
    <div className="acc" id="acc">
      {ITEMS.map((it, i) => {
        const isOpen = open === i;
        return (
          <div className={`acc__item${isOpen ? ' open' : ''}`} key={i}>
            <button
              className="acc__q"
              aria-expanded={isOpen}
              onClick={() => setOpen(isOpen ? null : i)}
            >
              {it.q}
              <i />
            </button>
            <div
              className="acc__a"
              style={{ maxHeight: isOpen ? '320px' : 0 }}
            >
              <p>{it.a}</p>
            </div>
          </div>
        );
      })}
    </div>
  );
}
