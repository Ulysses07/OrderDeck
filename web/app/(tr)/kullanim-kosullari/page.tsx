import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Kullanım Koşulları',
  description: `${BRAND} hizmet koşulları, lisans ve sorumluluk şartları.`,
};

const EFFECTIVE_DATE = '2026-05-02';

export default function TermsTr() {
  return (
    <LegalLayout
      locale="tr"
      routeKey="terms"
      title="Kullanım Koşulları"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        Bu koşullar, <strong>{BRAND}</strong> Windows masaüstü uygulamasını ve{' '}
        <strong>orderdeckapp.com</strong> web sitesini kullanırken sizinle{' '}
        <strong>{LEGAL_NAME}</strong> arasındaki sözleşmeyi düzenler. Hizmeti kullanmak,
        bu koşulları kabul ettiğiniz anlamına gelir.
      </p>

      <h2>1. Hizmetin tanımı</h2>
      <p>
        {BRAND}, Türkiye merkezli mezat yayıncıları için tasarlanmış bir Windows masaüstü
        uygulamasıdır. Sağladığı temel özellikler:
      </p>
      <ul>
        <li>Çoklu platform canlı yayın chat'lerinin tek panelde toplanması</li>
        <li>Anlık etiket yazıcı çıktısı</li>
        <li>Çekiliş yönetimi (anahtar kelime + çark çevirme)</li>
        <li>Spam ve trol filtresi</li>
        <li>YouTube canlı yayın moderasyon entegrasyonu</li>
      </ul>

      <h2>2. Lisans modeli</h2>
      <ul>
        <li>İlk kurulumda 14 gün ücretsiz tam deneme aktiftir, ödeme bilgisi istenmez.</li>
        <li>
          OrderDeck lisansı tek seferlik ödemeyle <strong>ömür boyu kullanım hakkı</strong>{' '}
          sağlar. Lisans aldığınız anda mevcut olan sürümü süresiz çalıştırma hakkına
          sahip olursunuz.
        </li>
        <li>
          Tek lisans, tek makineye bağlıdır ve tek bir kullanıcı/işletme adınadır.
          Aktarım panelden ayda 2 kez yapılabilir.
        </li>
        <li>
          <strong>Yıllık güncelleme + destek paketi (opsiyonel):</strong> Yeni özellikler,
          üçüncü taraf platform değişikliklerinden kaynaklanan API tamirleri ve öncelikli
          destek için yıllık abonelik paketi sunulur. İlk yıl lisans bedeline dahildir.
          Sonraki yıllarda almak/almamak tamamen sizin tercihinizdir; almazsanız satın
          aldığınız sürüm çalışmaya devam eder.
        </li>
        <li>
          Güncelleme paketi alınmadığı takdirde uygulamanın <strong>üçüncü taraf platform
          entegrasyonlarının</strong> (Instagram/TikTok/Facebook/YouTube) ileride çalışmaya
          devam edeceği taahhüt edilmez; platformlar değiştiğinde tamiri yeni paketle
          gelir.
        </li>
        <li>İade politikası: ilk ödeme tarihinden itibaren 14 gün içinde tam iade.</li>
      </ul>

      <h2>3. Kabul edilebilir kullanım</h2>
      <p>{BRAND} kullanırken aşağıdaki davranışlardan kaçınmayı kabul edersiniz:</p>
      <ul>
        <li>Mezat dolandırıcılığı, sahte teklif veya yanıltıcı satış pratikleri</li>
        <li>
          Satılan ürünlerin yasal mevzuata aykırı olması (taklit, telif ihlali, yasaklı
          maddeler vb.)
        </li>
        <li>Yayın platformlarının (Instagram, TikTok, Facebook, YouTube) hizmet koşullarını ihlal etmek</li>
        <li>{BRAND} altyapısını tersine mühendislik yapmak, lisans kontrolünü atlatmaya çalışmak</li>
        <li>Otomatik yöntemlerle aşırı API trafiği yaratıp hizmeti aksatmak</li>
        <li>Başka kullanıcıların hesap bilgilerine yetkisiz erişim girişimi</li>
      </ul>
      <p>
        Bu maddelere ihlalde, hesap önceden bildirim yapılmadan askıya alınabilir veya
        sonlandırılabilir.
      </p>

      <h2>4. Fikri mülkiyet</h2>
      <p>
        {BRAND} markası, logosu, tasarımı, kaynak kodu ve dökümantasyonu {LEGAL_NAME}'ın
        mülkiyetindedir. Lisans, size lisans kapsamı içinde uygulamayı kullanma
        hakkı verir; kaynak kod sahipliğini değil.
      </p>

      <h2>5. Üçüncü taraf hizmetler</h2>
      <p>
        Uygulama, çalışmak için bağımsız üçüncü taraf hizmetlere bağlanır: YouTube Data
        API (Google), tarayıcı eklentisi aracılığıyla Instagram, TikTok ve Facebook
        ortamları. Bu hizmetlerin kendi koşulları ve gizlilik politikaları geçerlidir.
        {BRAND}, bu hizmetlerin kesintisiz çalışacağını garanti etmez.
      </p>

      <h2>6. Garanti reddi</h2>
      <p>
        {BRAND} "olduğu gibi" sunulur. Kesintisiz, hatasız veya belirli bir amaca uygun
        çalışacağı yönünde herhangi bir garanti verilmez. Donanım uyumluluğu, yazıcı
        sürücüleri, internet bağlantısı ve üçüncü taraf platform değişiklikleri sizin
        sorumluluğunuzdadır.
      </p>

      <h2>7. Sorumluluğun sınırlandırılması</h2>
      <p>
        Yasaların izin verdiği azami ölçüde, {LEGAL_NAME}'ın bu hizmetten kaynaklanan her
        türlü dolaylı, arızi, özel veya cezai zarardan (kâr kaybı, veri kaybı, iş kesintisi
        dahil) sorumlu olmayacağı kabul edilir. Doğrudan zararlar için toplam sorumluluk,
        son 12 ay içinde tarafınızdan {LEGAL_NAME}'a ödenen toplam tutarı aşamaz.
      </p>

      <h2>8. Hesap askıya alma ve fesih</h2>
      <ul>
        <li>Bu koşulları ihlal etmeniz durumunda hesabınız askıya alınabilir veya sonlandırılabilir.</li>
        <li>Hesabınızı dilediğiniz an müşteri panelinden silebilirsiniz.</li>
        <li>Hesap silindiğinde lisans hakkınız sona erer; iadeler madde 2'deki politikaya göre değerlendirilir.</li>
      </ul>

      <h2>9. Koşullarda değişiklik</h2>
      <p>
        Bu koşullar zaman zaman güncellenebilir. Önemli değişikliklerde kayıtlı kullanıcılara
        en az 30 gün önceden e-posta ile bilgi verilir. Değişiklik sonrası hizmeti kullanmaya
        devam etmek, yeni koşulları kabul etmek anlamına gelir.
      </p>

      <h2>10. Uygulanacak hukuk ve yetki</h2>
      <p>
        Bu sözleşme Türkiye Cumhuriyeti hukukuna tabidir. Doğacak uyuşmazlıklarda Türkiye
        Cumhuriyeti mahkemeleri ve icra daireleri yetkilidir.
      </p>

      <h2>11. İletişim</h2>
      <p>
        Sorularınız için: <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
    </LegalLayout>
  );
}
