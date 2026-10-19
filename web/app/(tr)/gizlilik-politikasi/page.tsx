import type { Metadata } from 'next';
import { LegalLayout } from '@/components/LegalLayout';
import { CONTACT_EMAIL, LEGAL_NAME, BRAND } from '@/lib/i18n';

export const metadata: Metadata = {
  title: 'Gizlilik Politikası',
  description: `${BRAND} kişisel verileri nasıl işliyor — kapsam, saklama, paylaşım, kullanıcı hakları.`,
};

// Bu metnin bir kopyası kayıt formunda, onay kutusunun üstündeki açılır
// kutuda gösteriliyor: OrderDeck.LicenseServer/Pages/Public/_GizlilikPolitikasi.cshtml.
// İKİSİ AYNI TUTULMALI — birini değiştiren diğerini de aynı değişiklikle
// günceller ve KvkkNotice.TextVersion'ı artırır (kural
// OrderDeck.LicenseServer/Services/IntakeForm/KvkkNotice.cs'te). Müşteri formda
// bu metni okuduğunu onaylıyor; IntakeFormKvkkTests bu dosyanın özetini tutuyor
// ve metnini formdaki kopyayla blok blok karşılaştırıyor.
const EFFECTIVE_DATE = '2026-10-19';

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
        {BRAND} aşağıdaki veri türleriyle çalışır:
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
        yayın chat'lerini okur. Bu mesajlar:
      </p>
      <ul>
        <li><strong>Sadece kullanıcının kendi bilgisayarında</strong> bellek tamponunda tutulur (en fazla son 500 mesaj)</li>
        <li>Uygulama kapatıldığında <strong>tamamen silinir</strong></li>
        <li><strong>{BRAND} sunucularına gönderilmez</strong>, üçüncü kişilerle paylaşılmaz</li>
      </ul>
      <p>
        <strong>Tek istisna:</strong> yayıncının <strong>siparişe/etikete
        dönüştürdüğü</strong> yorumlar. Sipariş kaydına dönüşen yorumun metni,
        yayıncının işini yürütebilmesi (sipariş, kargo, itiraz takibi) için
        sipariş kaydının bir parçası olarak yayıncının bilgisayarında saklanır
        ve sipariş verisiyle birlikte {BRAND} sunucularına aktarılır. Ham chat
        akışının kendisi hiçbir zaman toplu olarak sunucuya gönderilmez.
        Ayrıntı için <a href="/veri-silme">Veri Silme</a> sayfasına bakın.
      </p>

      <h3>1.3. Kayıt formu ile toplanan alıcı bilgileri</h3>
      <p>
        Yayıncılar, izleyicilerinin sipariş, kargo ve fatura işlemleri için kişisel bir
        kayıt linki (web formu) oluşturabilir. Bu formu dolduran alıcılardan toplanan
        bilgiler:
      </p>
      <ul>
        <li>Platform kullanıcı adları (Instagram, YouTube, Facebook, TikTok — alıcının bildirdiği)</li>
        <li>
          Alıcı formdaki opsiyonel &quot;Google ile bağla&quot; veya &quot;Facebook ile
          bağla&quot; butonunu kullanırsa: kendi hesabının doğrulanmış kimliği — YouTube
          için kanal adı, handle ve kanal kimliği; Facebook için görünen ad (YouTube
          akışının ayrıntıları için Bölüm 2)
        </li>
        <li>Ad soyad, e-posta, telefon (WhatsApp) numarası ve kargo adresi</li>
        <li>Fatura için opsiyonel T.C. Kimlik Numarası (yalnızca alıcı girmek isterse)</li>
        <li>WhatsApp ve/veya SMS ile bilgilendirme mesajı almaya dair açık rıza (onay kutuları)</li>
      </ul>
      <p>
        Bu veriler yalnızca ilgili yayıncının sipariş, ödeme, kargo ve fatura süreçleri için
        işlenir (havaleyle yapılan ödemenin kime ait olduğunun tespiti dahil, bkz. Bölüm 1.4);
        üçüncü kişilerle pazarlama amacıyla paylaşılmaz veya satılmaz. Mesaj
        gönderimi yalnızca alıcının açıkça onay verdiği kanallardan yapılır; alıcı bu
        onayı dilediği an geri çekebilir. Bu verilerin veri sorumlusu, formu oluşturan
        yayıncıdır; {BRAND} verileri yayıncı adına barındırır ve yayıncının uygulamasına
        iletir.
      </p>

      <h3>1.4. Yayıncının banka hesap hareketleri (havale/EFT eşleştirme)</h3>
      <p>
        Banka bağlantısı kurulan yayıncılarda {BRAND}, yayıncının kendi banka hesabındaki
        hareketleri, banka hesap hareketlerini toplayan hizmet sağlayıcı Obifin aracılığıyla okur.
        Bu veriler ilgili kişilerden (örneğin havaleyi gönderen alıcıdan) değil, yayıncının
        bankasından gelir. Toplama otomatiktir: {BRAND} sunucusu Obifin'i yaklaşık 5 dakikada
        bir sorgular; bağlantı ilk kurulduğunda son 90 günün hareketleri bir kez alınır.
        Bağlantıyı yayıncının talebi üzerine {BRAND} yöneticisi kurar. Bankanın web servis
        şifresi {BRAND}'te saklanmaz, yalnızca bağlantıyı kurmak için Obifin'e iletilir; Obifin
        erişim bilgileri sunucuda şifrelenmiş olarak tutulur. Bu özellik şu an deneme aşamasındadır.
      </p>
      <p>İşlenen bilgiler:</p>
      <ul>
        <li>Yayıncının hesabı: banka, para birimi, IBAN'ın maskeli hâli ve anahtarlı özeti</li>
        <li>Her hareketin yönü (gelen/giden), tutarı, para birimi ve işlem zamanı</li>
        <li>Bankanın açıklama metni: havaleyi gönderenin yazdığı açıklamayı ve çoğu bankada gönderenin adını içerir</li>
        <li>Bankanın iletmesi hâlinde gönderen adı</li>
        <li>İşlem kodu, işlem tipi ve banka referans numarası</li>
        <li>
          Karşı tarafın IBAN'ı: yalnızca anahtarlı özeti (HMAC; açık IBAN'a geri çevrilemez,
          yalnızca aynı IBAN'ın yeniden tanınmasına yarar) ve ilk 4 ile son 3 karakteri görünen
          maskeli hâli
        </li>
        <li>
          Bankanın iletmesi hâlinde karşı tarafın vergi kimlik numarası veya T.C. Kimlik
          Numarası: yalnızca aynı yöntemle üretilmiş anahtarlı özeti
        </li>
        <li>
          Bankanın hareket için gönderdiği ham kayıt, teknik arıza incelemesi için: IBAN, vergi
          kimlik ve T.C. kimlik numarası alanları silinmiş olarak; diğer alanlar (örneğin adlar,
          gönderen banka ve şube, işlem sonrası hesap bakiyesi) kalır
        </li>
      </ul>
      <p>
        IBAN ve kimlik numaraları açık hâliyle ayrı bir alanda saklanmaz. Banka bu bilgileri
        açıklama metninin içine yazmışsa, açıklama metni Bölüm 5'teki süre dolana kadar olduğu
        gibi saklanır.
      </p>
      <p>
        Okunan hareketler yalnızca yayıncının alıcılarına ait değildir: yayıncının hesabına para
        gönderen ya da yayıncının hesabından para gönderilen herkesin (örneğin tedarikçiler, kira
        ve maaş ödemeleri, yayıncının kendi hesapları arasındaki aktarımlar) hareketleri de aynı
        biçimde okunur ve saklanır. Hesaptan giden hareketler eşleştirmede kullanılmaz.
      </p>
      <p>
        Amaç, hesaba gelen bir havalenin yayıncının hangi müşterisinin ödemesi olduğunu tahmin
        etmektir. Bunun için gelen havalenin açıklaması, yayıncının müşteri kayıtlarındaki
        kullanıcı adları ve ad soyadlarla karşılaştırılır; havalenin daha önce aynı müşteriye
        bağlanmış bir IBAN'dan gelip gelmediğine de bakılır. Tahminin doğruluğu, yayıncının
        panelde dekont onaylarken verdiği kararla karşılaştırılarak ölçülür. Dekont onayıyla
        ya da {BRAND} yöneticisinin elle eşlemesiyle doğrulanan bir bağdan, o IBAN'ın (yalnızca
        özeti ve maskeli hâliyle) o müşteriye ait olduğu kaydedilir.
      </p>
      <p>
        Bu aşamada hiçbir ödeme otomatik onaylanmaz ve tahminler hiçbir ödemenin durumunu
        değiştirmez. Tahminler yayıncı paneli, masaüstü uygulaması veya müşteri uygulamasında
        gösterilmez; yalnızca {BRAND} yöneticisi, eşleştirmenin doğruluğunu ölçmek için bir
        yönetim sayfasında görür.
      </p>
      <p>
        Bu verilerin veri sorumlusu, banka hesabı kendisine ait olan
        yayıncıdır; {BRAND} verileri yayıncı adına ve yayıncının talimatıyla işler. Hukuki sebepler: havaleyi gönderen
        alıcılar için alıcı ile yayıncı arasındaki satış sözleşmesinin ifası, yani ödemenin hangi
        siparişe ait olduğunun tespiti (KVKK m. 5/2-c); yayıncının kendi hesap bilgileri için
        yayıncı ile {BRAND} arasındaki hizmet sözleşmesinin ifası (KVKK m. 5/2-c); alıcı olmayan
        karşı taraflar ve eşleştirmenin doğruluğunun ölçülmesi için yayıncının, gelen ödemeleri
        doğru kişiye bağlama konusundaki meşru menfaati (KVKK m. 5/2-f). Bu işleme için açık
        rızaya dayanılmaz.
      </p>
      <p>
        Hareketler yayıncının bankasından Obifin aracılığıyla {BRAND}'e ulaşır; Obifin bu
        hizmette {BRAND}'in yararlandığı hizmet sağlayıcıdır (alt veri
        işleyen). {BRAND} bu verileri satmaz, pazarlama veya reklam amacıyla kullanmaz. Veriler, sunucudaki diğer
        verilerle birlikte şifreli yedeklere (Bölüm 7) dahildir.
      </p>

      <h2>2. YouTube API kullanımı</h2>
      <p>
        {BRAND}, YouTube Data API v3'ü iki ayrı akışta kullanır.
        Google API Hizmetleri Kullanıcı Verileri Politikası (Google API Services User Data Policy)
        ve <strong>Limited Use</strong> gerekliliklerine uygunluk taahhüt edilir.
      </p>

      <h3>2.1. Masaüstü uygulaması (yayıncının kendi hesabı)</h3>
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
      <p>Masaüstü akışında YouTube API üzerinden alınan veriler:</p>
      <ul>
        <li>Sadece kullanıcının kendi makinesinde işlenir</li>
        <li>{BRAND} sunucularına aktarılmaz</li>
        <li>Reklamcılık, profilleme veya makine öğrenmesi modeli eğitimi için kullanılmaz</li>
        <li>Üçüncü kişilere satılmaz veya devredilmez</li>
      </ul>
      <p>
        OAuth refresh token'ı kullanıcının makinesinde Windows Data Protection API (DPAPI)
        ile şifreli olarak saklanır. Kullanıcı dilediği an{' '}
        <a href="https://myaccount.google.com/permissions" target="_blank" rel="noreferrer">
          https://myaccount.google.com/permissions
        </a>{' '}
        adresinden izni iptal edebilir.
      </p>

      <h3>2.2. Kayıt formunda &quot;Google ile bağla&quot; (alıcının kendi kanalı)</h3>
      <p>
        Alıcı kayıt formunda (Bölüm 1.3) alıcı, YouTube kullanıcı adını elle yazmak
        yerine dilerse &quot;Google ile bağla&quot; butonunu kullanabilir. Bu akışta tek
        bir OAuth kapsamı istenir:
      </p>
      <ul>
        <li>
          <code>https://www.googleapis.com/auth/youtube.readonly</code> — Sunucu tarafında
          tek bir <code>channels.list(mine=true)</code> çağrısıyla yalnızca alıcının{' '}
          <strong>kendi</strong> kanalının adı, handle'ı (customUrl) ve kanal kimliği
          okunur. Abonelikler, oynatma listeleri, videolar, analitik veya başka hiçbir
          veri okunmaz; hiçbir şey yazılmaz, değiştirilmez veya silinmez.
        </li>
      </ul>
      <p>Bu akışta:</p>
      <ul>
        <li>
          OAuth erişim token'ı sunucuda bir kez kullanılır ve hemen atılır — erişim veya
          yenileme token'ı <strong>saklanmaz</strong>
        </li>
        <li>
          Alınan kanal adı, handle ve kanal kimliği yalnızca alıcının kendi kaydının bir
          parçası olarak saklanır ve yalnızca alıcının kayıt olduğu yayıncıya görünür
          (saklama süresi: Bölüm 5, &quot;Kayıt formu ile toplanan alıcı bilgileri&quot;)
        </li>
        <li>Bu veri reklamcılık, profilleme veya makine öğrenmesi modeli eğitimi için kullanılmaz; üçüncü kişilere satılmaz veya devredilmez</li>
        <li>
          Alıcı dilediği an{' '}
          <a href="https://myaccount.google.com/permissions" target="_blank" rel="noreferrer">
            https://myaccount.google.com/permissions
          </a>{' '}
          adresinden uygulamanın erişimini iptal edebilir ve Bölüm 6'daki yolla kaydının
          silinmesini isteyebilir
        </li>
      </ul>
      <p>
        {BRAND} YouTube Data API Hizmetleri&apos;ni kullanır. Google&apos;ın kullanıcı verilerini
        işlemesi, <strong>Google Gizlilik Politikası&apos;na</strong>{' '}
        (<a href="https://policies.google.com/privacy" target="_blank" rel="noreferrer">
          http://www.google.com/policies/privacy
        </a>) tabidir.
      </p>

      <h2>3. Tarayıcı eklentisi (yalnızca TikTok)</h2>
      <p>
        YouTube, Instagram ve Facebook canlı yayın chat'leri platformların resmi
        API'leri üzerinden okunur. TikTok resmi bir açık API sunmadığından yalnızca
        TikTok için OrderDeck Chrome/Edge tarayıcı eklentisi kullanılır. Eklenti
        yalnızca:
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
        <li>Kayıt formu ile toplanan alıcı bilgileri: ilgili yayıncı silene kadar</li>
        <li>
          Banka hesap hareketleri (Bölüm 1.4): ham kayıt 90 gün; açıklama metni, gönderen adı ve
          eşleştirmenin gerekçe metni 180 gün; tutar, tarih, yön, işlem kodu, banka referans
          numarası, maskeli IBAN, IBAN ve kimlik numarası özetleri ile eşleştirme sonucu
          yayıncının hesabı silinene kadar. Süreler hareketin {BRAND}'e alındığı günden işler;
          silme her gün otomatik yapılır
        </li>
        <li>
          Öğrenilen IBAN–müşteri bağı (yalnızca IBAN özeti ve maskeli hâli): bağ kaldırılana,
          ilgili kişinin silme talebi uygulanana ya da yayıncının hesabı silinene kadar
        </li>
        <li>Sunucu erişim logları: 30 gün</li>
        <li>Güvenlik kayıtları: 90 gün</li>
        <li>
          Canlı yayın chat mesajları: yalnızca uygulama açık kaldığı sürece
          (siparişe/etikete dönüştürülen yorumların metni hariç — o metin,
          sipariş kaydının parçası olarak sipariş kaydıyla aynı süre saklanır)
        </li>
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
      <p>
        Banka hesap hareketleriyle (Bölüm 1.4) ilgili taleplerinizi, ödeme yaptığınız ya da ödeme
        aldığınız yayıncıya veya doğrudan bize iletebilirsiniz; bize gelen talebi yayıncıyla
        birlikte sonuçlandırırız. Bankaların kendi kayıtları bankaların
        sorumluluğundadır; {BRAND} o kayıtları değiştiremez veya silemez.
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
