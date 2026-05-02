import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Gizlilik Politikası',
  description: `${BRAND} kişisel verileri nasıl işliyor — kapsam, saklama, paylaşım, kullanıcı hakları.`,
};

const EFFECTIVE_DATE = '2026-05-02';

export default function PrivacyTr() {
  return (
    <LegalLayout
      locale="tr"
      routeKey="privacy"
      title="Gizlilik Politikası"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        Bu gizlilik politikası, <strong>{BRAND}</strong> Windows masaüstü uygulamasının
        ve <strong>orderdeckapp.com</strong> web sitesinin kişisel verileri nasıl topladığını,
        işlediğini, sakladığını ve paylaştığını açıklar.
      </p>
      <p>
        <strong>Veri sorumlusu:</strong> {LEGAL_NAME} (şahıs). İletişim:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>

      <h2>1. Toplanan veriler</h2>
      <p>
        {BRAND} masaüstü uygulaması iki tür veri ile çalışır:
      </p>

      <h3>1.1. Lisans ve hesap verileri</h3>
      <ul>
        <li>E-posta adresi (kayıt ve lisans yönetimi için)</li>
        <li>Lisans anahtarı, makine kimliği (parmak izi)</li>
        <li>Ödeme tutarı, lisans ve güncelleme paketi durumu (ödeme bilgisi <em>sadece ödeme sağlayıcılarda</em> tutulur, biz saklamayız)</li>
        <li>IP adresi ve giriş zamanı (güvenlik kayıtları için, 90 gün içinde silinir)</li>
      </ul>

      <h3>1.2. Canlı yayın chat verileri</h3>
      <p>
        Uygulama, bağlanan platformların (Instagram, TikTok, Facebook, YouTube) canlı
        yayın chat\'lerini okur. Bu mesajlar:
      </p>
      <ul>
        <li><strong>Sadece kullanıcının kendi bilgisayarında</strong> bellek tamponunda tutulur (en fazla son 500 mesaj)</li>
        <li>Uygulama kapatıldığında <strong>tamamen silinir</strong></li>
        <li><strong>{BRAND} sunucularına gönderilmez</strong>, üçüncü kişilerle paylaşılmaz</li>
      </ul>

      <h2>2. YouTube API kullanımı</h2>
      <p>
        Uygulamanın YouTube canlı yayın özelliği, YouTube Data API v3 üzerinden çalışır.
        Google API Hizmetleri Kullanıcı Verileri Politikası (Google API Services User Data Policy)
        ve <strong>Limited Use</strong> gerekliliklerine uygunluk taahhüt edilir.
      </p>
      <p>İstenen OAuth izinleri (kapsamlar):</p>
      <ul>
        <li>
          <code>https://www.googleapis.com/auth/youtube</code> — Kanal bilgisi (görünen ad, profil resmi)
          ve canlı yayın chat mesajlarını okumak için
        </li>
        <li>
          <code>https://www.googleapis.com/auth/youtube.force-ssl</code> — Kullanıcının uygulama içinde
          açıkça tıkladığı an, tek tek mesaj silme veya kullanıcı banlama işlemleri için
        </li>
      </ul>
      <p>YouTube API üzerinden alınan veriler:</p>
      <ul>
        <li>Sadece kullanıcının kendi makinesinde işlenir</li>
        <li>{BRAND} sunucularına aktarılmaz</li>
        <li>Reklamcılık, profilleme veya makine öğrenmesi modeli eğitimi için kullanılmaz</li>
        <li>Üçüncü kişilere satılmaz veya devredilmez</li>
      </ul>
      <p>
        OAuth refresh token\'ı kullanıcının makinesinde Windows Data Protection API (DPAPI)
        ile şifreli olarak saklanır. Kullanıcı dilediği an{' '}
        <a href="https://myaccount.google.com/permissions" target="_blank" rel="noreferrer">
          https://myaccount.google.com/permissions
        </a>{' '}
        adresinden izni iptal edebilir.
      </p>

      <h2>3. Tarayıcı eklentisi (Instagram, TikTok, Facebook)</h2>
      <p>
        Bu üç platform için resmi açık API olmadığından, OrderDeck Chrome/Edge tarayıcı
        eklentisi kullanılır. Eklenti yalnızca:
      </p>
      <ul>
        <li>Kullanıcının açtığı canlı yayın sayfasındaki chat mesajlarını okur</li>
        <li>Mesajları yerel WebSocket bağlantısı ile masaüstü uygulamasına iletir</li>
        <li>Hiçbir veriyi başka bir adrese göndermez</li>
      </ul>

      <h2>4. Çerezler ve site analitiği</h2>
      <p>
        <strong>orderdeckapp.com</strong> web sitesi şu an üçüncü taraf analitik veya reklam
        çerezi kullanmıyor. Yalnızca Caddy reverse proxy'nin standart sunucu logları
        (IP adresi, istek yolu, kullanıcı ajanı) tutulur ve 30 gün içinde silinir.
      </p>

      <h2>5. Veri saklama süreleri</h2>
      <ul>
        <li>Hesap verileri (e-posta, lisans): hesap silinene kadar</li>
        <li>Sunucu erişim logları: 30 gün</li>
        <li>Güvenlik kayıtları: 90 gün</li>
        <li>Canlı yayın chat mesajları: yalnızca uygulama açık kaldığı sürece</li>
        <li>OAuth token: kullanıcı izni iptal edene kadar (kullanıcının makinesinde)</li>
      </ul>

      <h2>6. Kullanıcı hakları</h2>
      <p>KVKK ve GDPR çerçevesinde aşağıdaki haklara sahipsiniz:</p>
      <ul>
        <li>Verilerinize erişme</li>
        <li>Düzeltme isteme</li>
        <li>Silme isteme (hesabı tamamen kapatma)</li>
        <li>İşlemeye itiraz etme</li>
        <li>Veri taşınabilirliği</li>
      </ul>
      <p>
        Bu haklarınızı kullanmak için <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>{' '}
        adresine yazabilirsiniz. Talepleriniz 30 gün içinde yanıtlanır.
      </p>

      <h2>7. Veri güvenliği</h2>
      <p>
        Sunucularımızda iletim TLS 1.2+ ile şifrelenir. Yedekler AES-256-GCM ile şifreli
        tutulur. JWT token'ları güvenli bir secret ile imzalanır. Yine de internet üzerinden
        veri iletiminin %100 güvenli olduğu garanti edilemez; siz de hesap şifrenizi gizli
        tutmakla yükümlüsünüz.
      </p>

      <h2>8. Çocuklar</h2>
      <p>
        {BRAND} 18 yaş altı kullanıcılar için tasarlanmamıştır. Bilerek 18 yaş altı bir
        kişiden veri toplamayız.
      </p>

      <h2>9. Politika değişiklikleri</h2>
      <p>
        Bu politikayı güncellediğimizde, üst kısımdaki "Yürürlük tarihi" güncellenir.
        Önemli değişikliklerde kayıtlı kullanıcılara e-posta ile bilgi veririz.
      </p>

      <h2>10. İletişim</h2>
      <p>
        Gizlilik ile ilgili her tür soru için:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
    </LegalLayout>
  );
}
