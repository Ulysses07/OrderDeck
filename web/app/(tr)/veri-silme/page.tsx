import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Veri Silme Talimatları',
  description: `${BRAND} ve Facebook Login ile toplanan verilerin nasıl sildirileceği.`,
};

const EFFECTIVE_DATE = '2026-07-02';

export default function DataDeletionTr() {
  return (
    <LegalLayout
      locale="tr"
      routeKey="dataDeletion"
      title="Veri Silme Talimatları"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        Bu sayfa, <strong>{BRAND}</strong> aracılığıyla (Facebook Login ve bağlı
        platformlar dahil) toplanan verilerin nasıl sildirileceğini açıklar.
        Veri sorumlusu: {LEGAL_NAME}. İletişim:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>

      <h2>1. Facebook üzerinden toplanan veriler</h2>
      <p>
        {BRAND}, yayıncının kendi Facebook Sayfasına ait canlı yayınında:
      </p>
      <ul>
        <li>Canlı yayın <strong>yorum metinlerini</strong> (sipariş almak için) okur,</li>
        <li>Yorumu yazan kullanıcının Facebook tarafından sağlanan <strong>adı ve
          sayfa-kapsamlı kimliğini (PSID)</strong> alır,</li>
        <li>Facebook Login ile verilen <strong>Sayfa erişim jetonunu</strong> (yayıncının
          makinesinde şifreli olarak) saklar.</li>
      </ul>
      <p>
        Canlı chat akışının kendisi geçicidir ve uygulama kapatıldığında silinir.
        Ancak yorumdan oluşturulan{' '}
        <strong>müşteri kaydı ve sipariş/etiket</strong> bilgileri, yayıncının
        işini yürütebilmesi için saklanır.
      </p>

      <h2>2. Yayıncıysanız (OrderDeck kullanıcısı)</h2>
      <ul>
        <li>Uygulamadaki <strong>Müşteri Detayı</strong> ekranından siparişleri
          <strong> iptal edebilir</strong> ve müşteri notlarını düzenleyebilirsiniz.
          Kayıtların kalıcı olarak silinmesi bu ekrandan yapılmaz; aşağıdaki
          e-posta yoluyla talep edilir.</li>
        <li>Ayarlar → Facebook üzerinden <strong>“Bağlantıyı Kaldır”</strong> ile Sayfa
          erişim jetonunu ve OAuth verilerini kaldırabilirsiniz.</li>
        <li>Belirli müşteri/sipariş kayıtlarının kalıcı olarak silinmesi ya da tüm
          hesabınızın ve ilişkili verilerin silinmesi için{' '}
          <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a> adresine yazın.
          Talebinizi doğruladıktan sonra <strong>30 gün içinde</strong> sileriz.</li>
      </ul>

      <h2>3. İzleyici / yorumcuysanız</h2>
      <p>
        Bir yayıncının canlı yayınına yorum yaptıysanız ve verinizin silinmesini
        istiyorsanız, iki yol vardır:
      </p>
      <ul>
        <li>Verinizi işleyen <strong>yayıncıya (Sayfa sahibine)</strong> doğrudan
          başvurabilirsiniz, ya da</li>
        <li>Facebook adınız ve varsa yorum detayınızla birlikte{' '}
          <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a> adresine e-posta
          gönderebilirsiniz. Talebinizi doğruladıktan sonra ilgili kayıtları
          <strong> 30 gün içinde</strong> sileriz.</li>
      </ul>

      <h2>4. Facebook Login verisi</h2>
      <p>
        Facebook hesabınızla verdiğiniz izinleri istediğiniz zaman{' '}
        <a href="https://www.facebook.com/settings?tab=business_tools" target="_blank" rel="noopener noreferrer">
          Facebook → Ayarlar → İş Araçları</a>{' '}
        bölümünden kaldırabilirsiniz. İzin kaldırıldığında {BRAND} artık verinize
        erişemez; makinede saklanan jeton da geçersiz olur.
      </p>
    </LegalLayout>
  );
}
