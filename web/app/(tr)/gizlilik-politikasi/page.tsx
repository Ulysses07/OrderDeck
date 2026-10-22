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
//
// Sayfa içi bağlantı noktaları (id) iki kopyada da aynı ve dışarıdan
// kullanılıyor: "banka" (1.4; yayıncı paneli, kayıt formu), "dekont" (1.5;
// müşteri uygulamasının dekont ekranı), "haklar" (6; müşteri uygulamasının
// Profil ve hesap silme bağlantıları). Kaldırırsan ya da adını değiştirirsen
// o bağlantılar sayfanın başına düşer.
//
// Yürürlük tarihi değişince İngilizce sayfadaki TURKISH_EFFECTIVE_DATE de
// değişir (PolicyEffectiveDateParityTests).
const EFFECTIVE_DATE = '2026-10-21';

export default function PrivacyTr() {
  return (
    <LegalLayout
      locale="tr"
      routeKey="privacy"
      title="Gizlilik Politikası"
      effectiveDate={EFFECTIVE_DATE}
    >
      <p>
        <strong>Son değişiklik (2026-10-21):</strong> Yayıncının banka hesap hareketlerinin işlenmesi (Bölüm 1.4)
        düzeltildi ve genişletildi; müşteri uygulaması ve dekontlar (Bölüm 1.5) ile hizmet sağlayıcılar ve yurt dışına
        aktarım (Bölüm 1.6) bölümleri eklendi; saklama süreleri, haklarınız ve başvuru yolları ile veri güvenliği
        (Bölüm 5–7) güncellendi. Önceki sürüm: 2026-10-19.
      </p>
      <p>
        Bu gizlilik politikası, <strong>{BRAND}</strong> hizmetinin (Windows masaüstü uygulaması, yayıncı paneli,
        müşteri uygulaması, kayıt formu ve bunlara hizmet veren sunucular) ve <strong>orderdeckapp.com</strong> web
        sitesinin kişisel verileri nasıl topladığını, işlediğini, sakladığını ve paylaştığını açıklar.
      </p>
      <p>
        <strong>Veri sorumlusu:</strong> {LEGAL_NAME} (şahıs). İletişim:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
      <p>
        Bölüm 1.3'teki alıcı bilgilerinin, Bölüm 1.5'teki sipariş, ödeme, dekont ve kargo bilgilerinin ve Bölüm 1.4'teki
        havale eşleştirmesinin veri sorumlusu ise ilgili yayıncıdır; {BRAND} bu verileri yayıncı adına işler. Bölüm
        1.4'te anlatılan eşleştirme doğruluğu ölçümünü ise {BRAND} kendi adına yapar.
      </p>

      <h2>1. Toplanan veriler</h2>
      <p>
        {BRAND} aşağıdaki veri türleriyle çalışır:
      </p>

      <h3>1.1. Lisans ve hesap verileri</h3>
      <ul>
        <li>E-posta adresi (kayıt ve lisans yönetimi için)</li>
        <li>Lisans anahtarı, makine kimliği (parmak izi)</li>
        <li>Lisans ödemesine ait tutar, lisans ve güncelleme paketi durumu (kart ve ödeme aracı bilgileri yalnızca ödeme sağlayıcısında tutulur, biz saklamayız; yayıncının banka hesap hareketleri için bkz. Bölüm 1.4)</li>
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
      <p>
        Yayıncının masaüstü uygulamasındaki müşteri kayıtları (canlı yayından oluşturulanlar dahil) {BRAND} sunucusunda
        da tutulur; bu kayıtlardaki platform kullanıcı adı, ad soyad ve görünen ad Bölüm 1.4'teki havale eşleştirmesinde
        kullanılır.
      </p>

      <h3>1.3. Kayıt formu ile toplanan alıcı bilgileri</h3>
      <p>
        Yayıncılar, izleyicilerinin sipariş, ödeme, kargo ve fatura işlemleri için kişisel bir kayıt linki (web formu)
        oluşturabilir. Bu formu dolduran alıcılardan toplanan bilgiler:
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
        Bu veriler ilgili yayıncının sipariş, ödeme, kargo ve fatura süreçleri için işlenir; buna, yayıncının hesabına
        gelen bir havalenin hangi müşteriye ait olduğunun belirlenmesi de dahildir (Bölüm 1.4). Üçüncü kişilerle
        pazarlama amacıyla paylaşılmaz veya satılmaz. Mesaj gönderimi yalnızca alıcının açıkça onay verdiği kanallardan
        yapılır; alıcı bu onayı dilediği an geri çekebilir. Bu verilerin veri sorumlusu, formu oluşturan yayıncıdır;
        {' '}{BRAND} verileri yayıncı adına barındırır ve yayıncının uygulamasına iletir. Bölüm 1.4'te anlatılan eşleştirme
        doğruluğu ölçümünde ise {BRAND} bu verileri kendi adına kullanır. Yayıncı veri sorumlusu bilgilerini (adı veya
        unvanı, posta adresi ve KVKK başvuru e-postası) girdiyse bu bilgiler formda gösterilir.
      </p>

      <h3 id="banka">1.4. Yayıncının banka hesap hareketleri (havale ve EFT eşleştirme)</h3>
      <p>
        Bu bölümde &quot;müşteri&quot;, yayıncıdan alışveriş yapan kişiyi (Bölüm 1.3'teki alıcıyı ve Bölüm 1.5'teki müşteri
        uygulaması kullanıcısını) ifade eder.
      </p>
      <p>
        <strong>Verinin kaynağı ve toplanma yöntemi.</strong> Banka bağlantısı kurulan yayıncılarda {BRAND}, yayıncının
        banka hesabına gelen hareketleri okur. Erişim, yayıncının kendi bankasından aldığı ve hesap hareketlerinin
        okunmasına izin veren web servis yetkisine dayanır. Hareketler bankadan teknik hizmet sağlayıcı Obifin'in
        sistemine, oradan {BRAND} sunucusuna gelir. {BRAND} sunucusu Obifin'i 5 dakikada bir sorgular. Bağlantı ilk
        kurulduğunda, kurulduğu günden en çok iki gün öncesine kadar uzanan hareketler bir kez alınır; daha eski
        hareketler istenmez ve kaydedilmez. Hizmet sonunda verileri silinmiş bir bağlantı yeniden açılırsa da aynı kural,
        yeniden açıldığı gün için uygulanır.
      </p>
      <p>
        Bağlantıyı yayıncının hesabı için {BRAND}'in yetkili yöneticisi kurar. Bağlantı altında görünen banka
        hesaplarından yalnızca yöneticinin, yayıncının müşteri ödemelerini aldığı hesap olarak onayladığı hesabın
        hareketleri kaydedilir; diğer hesapların hareketleri kaydedilmeden atılır. Bugün banka bağlantısı yalnızca bir
        yayıncının hesabında açıktır. {BRAND} bu bağlantıyla yalnızca hesap hareketlerini okur; yayıncı adına ödeme
        başlatmaz. Yayıncının banka web servis bilgileri {BRAND}'te saklanmaz; Obifin'in hareketleri bankadan
        alabilmesi için Obifin'de saklanır. {BRAND}'in Obifin'e bağlanmak için kullandığı erişim bilgileri sunucuda
        şifrelenmiş olarak tutulur.
      </p>
      <p>
        Bu hareketler havaleyi gönderen kişiden değil, yayıncının bankasından gelir. Eşleştirmede ise müşterilerin kendi
        verdiği bilgiler de kullanılır: kayıt formundaki ve yayıncının müşteri kayıtlarındaki kullanıcı adı ve ad soyad,
        müşteri uygulamasından yüklenen dekonttaki tutar, tarih ve gönderen adı.
      </p>
      <p><strong>İşlenen bilgiler.</strong></p>
      <ul>
        <li>
          Bağlantı altında görünen banka hesapları: banka, para birimi ve IBAN'ın kısaltılmış hâli (yöneticinin hesabı
          tanıyıp onaylayabilmesi ya da reddedebilmesi için); onaylanan hesapta ayrıca IBAN'ın tanıma kodu.
        </li>
        <li>Hesaba gelen her havalenin tutarı, para birimi, işlem zamanı, işlem kodu ve tipi ile banka referans numarası.</li>
        <li>
          Bankanın hareket açıklaması. Gönderenin yazdığı açıklamayı içerir; bankaya göre gönderenin adını da içerebilir.
          Açıklamada tanınabilir biçimde yazılmış TR IBAN'ları ve 11 haneli numaralar (örneğin T.C. kimlik numarası ya da
          telefon numarası) kayda alınırken maskelenir; bazı yazımlar (örneğin bölünerek ya da hatalı yazılmış
          numaralar) maskelenmeyebilir.
        </li>
        <li>Bankanın iletmesi hâlinde gönderenin adı.</li>
        <li>
          Gönderen hesabın IBAN'ı: IBAN'ın kendisi ayrı bir alanda saklanmaz. Yalnızca ilk 4 ve son 3 karakteri görünen
          kısaltılmış hâli ile, aynı IBAN'dan yeniden ödeme geldiğinde bunu tanımaya yarayan ve gizli bir anahtarla
          üretilen bir tanıma kodu saklanır. Anahtarı bilmeyen biri bu koddan IBAN'ı elde edemez; yine de kısaltılmış
          IBAN ve tanıma kodu kişisel veridir ve öyle korunur. Banka IBAN'ı kısaltılmış (maskeli) ya da geçerli bir IBAN
          olmayan bir değer olarak gönderiyorsa tanıma kodu üretilmez ve değerin hiçbir karakteri saklanmaz.
        </li>
        <li>
          Bankanın hareket için gönderdiği kaydın kopyası, yalnızca teknik arızaların incelenmesi için ve yalnızca şu
          alanlarla: açıklama, işlem açıklaması, gönderen adı, gönderen banka, işlem kodu, referans ve makbuz numarası.
          Bu kopyadaki açıklamalar da aynı biçimde maskelenir.
        </li>
      </ul>
      <p>
        <strong>Müşteri ödemesi olmayan hareketler.</strong> Hesaba gelen havaleler yalnızca müşterilerden gelmez;
        örneğin pazaryeri ödemeleri, iadeler, yayıncının kendi hesaplarından ya da yakınlarından gelen aktarımlar da
        okunur. Bir müşteriyle eşleştirilmemiş (dekont onayıyla ya da {BRAND}'in yetkili yöneticisinin elle
        eşlemesiyle bir müşteriye bağlanmamış) havalelerin açıklaması ve gönderen adı işlem tarihinden 30 gün sonra
        silinir; tutar, tarih ve banka referansı Bölüm 5'teki süre kadar kalır. Yayıncının hesabından giden hareketler ve
        tutarı sıfır olan hareketler Obifin'in gönderdiği listede yer alır; {BRAND} bunları kaydetmeden atar. POS
        tahsilatları eşleştirmede kullanılmaz.
      </p>
      <p>
        <strong>Açıklamalardaki hassas bilgiler.</strong> Havale açıklaması serbest metindir. Açıklamaya sağlık, din,
        dernek veya sendika üyeliği gibi bilgiler yazmamanızı rica ederiz. Böyle bir bilgi yazılmışsa hiçbir amaçla
        kullanılmaz ve açıklamayla birlikte Bölüm 5'teki sürede silinir.
      </p>
      <p><strong>Amaçlar.</strong></p>
      <ul>
        <li>
          (i) Yayıncı için: hesaba gelen bir havalenin yayıncının hangi müşterisine ait olduğunu belirlemek. Bunun için
          havalenin açıklaması, yayıncının kayıtlı bütün müşterilerinin (havale yapmamış olanlar dahil) kullanıcı
          adlarıyla ve ad soyadlarıyla, ad soyad yoksa görünen adlarıyla karşılaştırılır. Havalenin daha önce bir
          müşteriyle ilişkilendirilmiş bir IBAN'dan gelip gelmediğine de bakılır.
        </li>
        <li>
          (ii) {BRAND} için: bu eşleştirmenin ne kadar doğru çalıştığını ölçmek. Tahmin, yayıncının panelde onayladığı
          dekontla (dekontun tutarı, tarihi ve dekonttaki gönderen adı) karşılaştırılır. Dekont onayıyla ya da
          {' '}{BRAND}'in yetkili yöneticisinin elle yaptığı bir eşlemeyle doğrulanan bir bağdan sonra, o IBAN'dan gelen
          havalelerin o müşteriyle ilişkili olduğu kaydedilir (yalnızca kısaltılmış IBAN ve tanıma koduyla). Bu IBAN
          müşterinin kendisine değil, örneğin müşteri adına ödeme yapan bir yakınına ait olabilir.
        </li>
      </ul>
      <p>
        <strong>Bugünkü kullanım.</strong> Şu an eşleştirmenin tek kullanımı (ii)'dir: hiçbir ödeme bu yolla onaylanmaz,
        tahminler hiçbir ödemenin durumunu değiştirmez ve yayıncı panelinde, masaüstü uygulamasında ya da müşteri
        uygulamasında gösterilmez. Tahminleri yalnızca {BRAND}'in yetkili yöneticisi bir yönetim sayfasında görür.
        Ölçümün sonucu, ödemelerin bu yolla yayıncıya sorulmaya başlanıp başlanmayacağına karar vermekte kullanılır; böyle
        bir değişiklikten önce bu metin güncellenir ve size bildirilir.
      </p>
      <p><strong>Veri sorumlusu ve hukuki sebepler.</strong></p>
      <ul>
        <li>
          (i) amacındaki işlemenin veri sorumlusu, banka hesabı izlenen yayıncıdır; {BRAND} bu işlemeyi yayıncı adına
          yürütür. Yayıncının adı veya unvanı, posta adresi ve KVKK başvuru e-postası, yayıncının kayıt formunda ve
          müşteri uygulamasında yayıncıya ait ekranlarda gösterilir; bu bilgileri bize yazarak da öğrenebilirsiniz.
        </li>
        <li>(ii) amacındaki doğruluk ölçümünün veri sorumlusu {BRAND}'tir (iletişim bilgileri sayfanın başındadır).</li>
      </ul>
      <p>Hukuki sebepler:</p>
      <ul>
        <li>
          Doğruluk ölçümü: {BRAND}'in, sunduğu eşleştirme hizmetinin yanlış ödeme onaylarına yol açmamasını sağlama
          konusundaki meşru menfaati (KVKK m. 5/2-f). Bu menfaatin ilgili kişilerin hak ve özgürlüklerine zarar vermemesi
          için yalnızca hesaba gelen havaleler kaydedilir, IBAN açık hâliyle ayrı bir alanda saklanmaz, açıklamalar ve
          adlar Bölüm 5'teki sürede silinir ve sonuçları yalnızca {BRAND}'in yetkili yöneticisi görür.
        </li>
        <li>
          (i) amacı, yayıncıya sunulduğunda: havaleyi gönderen müşteri için müşteri ile yayıncı arasındaki satış
          sözleşmesinin ifası (KVKK m. 5/2-c); müşteri adına başkasının hesabından ödeme yapan kişiler, karşılaştırmada
          adı ve kullanıcı adı kullanılan diğer müşteriler ve müşteri olmayan gönderenler için yayıncının gelen ödemeleri
          doğru müşteriye bağlama konusundaki meşru menfaati (KVKK m. 5/2-f).
        </li>
        <li>
          Yayıncının kendi hesap bilgileri: veri sorumlusu {BRAND}'tir; hukuki sebep yayıncı ile {BRAND} arasındaki
          hizmet sözleşmesinin ifasıdır (KVKK m. 5/2-c). Yayıncı tüzel kişiyse bu bilgiler kişisel veri değildir.
        </li>
      </ul>
      <p>Bu işleme için açık rızaya dayanılmaz.</p>
      <p><strong>Kimler erişir, nereye aktarılır.</strong></p>
      <ul>
        <li>
          Obifin: hareketleri bankadan alıp {BRAND}'e iletir; bunun için yayıncının banka web servis bilgilerini ve
          hareketlerin bir kopyasını kendi sisteminde saklar. Obifin'deki kopyanın silinmesini gerektiğinde Obifin'den
          yazılı olarak isteriz.
        </li>
        <li>
          {BRAND} sunucusu ve şifreli yedekleri yurt dışındaki hizmet sağlayıcılarda barındırılır (Bölüm 1.6); banka
          verileri de buna dahildir.
        </li>
        <li>Başka bir yayıncı bu verilere erişemez. {BRAND} bu verileri satmaz, pazarlama veya reklam amacıyla kullanmaz.</li>
      </ul>

      <h3 id="dekont">1.5. Müşteri uygulaması ({BRAND} Shopper) ve dekontlar</h3>
      <p>
        <strong>Hesap.</strong> Müşteri uygulamasında hesap açarken ad soyad, cep telefonu, teslimat adresi ve parola;
        isteğe bağlı olarak e-posta adresi ve fatura için T.C. kimlik numarası alınır (9.990 TL'yi aşan bir ödemenin
        dekontunu yüklemek için T.C. kimlik numarası gerekir). Bağlandığınız her yayıncı için o yayıncının kodu, platform
        ve o platformdaki kullanıcı adınız kaydedilir. Parolanın kendisi saklanmaz; yalnızca parolanızı doğrulamaya
        yarayan bir özeti saklanır. Bildirim tercihleriniz ve ticari ileti (SMS) izniniz, iznin verildiği ve geri
        çekildiği tarih ve kaynağıyla birlikte kaydedilir.
      </p>
      <p>
        <strong>Telefon doğrulama ve parola sıfırlama.</strong> Telefonunuzu doğrulamak ve parolanızı sıfırlamak için SMS
        ile tek kullanımlık kod gönderilir; SMS'ler Türkiye'deki hizmet sağlayıcı Netgsm üzerinden gider. Kodun yalnızca
        özeti saklanır; kod kayıtları ve isteğin geldiği IP adresi 7 gün sonra silinir.
      </p>
      <p>
        <strong>Bildirimler.</strong> Bildirimlere izin verirseniz cihazınızın uygulama kimliği ve bildirim jetonu
        kaydedilir; bildirimler Google'ın Firebase Cloud Messaging hizmeti üzerinden gönderilir (Bölüm 1.6). Bildirim
        türlerini Profil ekranından kapatabilirsiniz.
      </p>
      <p>
        <strong>Dekontlar.</strong> Yüklediğiniz dekont (PDF) sunucuda okunur; tutar, ödeme tarihi, gönderen adı, banka
        referans numarası ile dekonttaki alıcı adı ve IBAN'ı çıkarılıp ödeme kaydına yazılır. Gönderen adını, tutarı,
        tarihi ve referans numarasını kendiniz de girebilirsiniz; girdiğiniz bilgi dekonttakinin yerine kullanılır.
        Dekont dosyası Cloudflare R2 depolama hizmetinde saklanır (Bölüm 1.6). Aynı dekontun ikinci kez kullanılmasını
        önlemek için dosyanın ve dekont bilgilerinin bir özeti tutulur; bu özet de kişisel veridir. Dekonttan okunan tam
        metin (gönderen ve alıcı adı, IBAN'ların açık hâli ve dekontta yazıyorsa kimlik numarası dahil) gönderim kaydında
        30 gün tutulur ve sonra silinir; gönderimin IP adresi ve tarayıcı bilgisi 90 gün sonra silinir.
      </p>
      <p>
        Dekont, yayıncının kontrolüne yardımcı olmak için otomatik olarak denetlenir: aynı dosya ikinci kez yüklenemez;
        aynı bilgilerle daha önce dekont yüklenip yüklenmediği, dekonttaki alıcı IBAN'ının yayıncının ödeme IBAN'ıyla
        uyumu ve dekontun ne kadar güvenle okunduğu yayıncıya uyarı olarak gösterilir. Ödemeyi onaylama ya da reddetme
        kararını yayıncı verir. Yayıncı dekontunuzu onaylarsa ve yayıncının banka bağlantısı varsa dekont bilgileri Bölüm
        1.4'teki havale eşleştirmesinde kullanılır.
      </p>
      <p>
        <strong>Amaçlar.</strong> Hesabınızı açmak ve korumak; bağlandığınız yayıncıların siparişlerinizi, ödemelerinizi
        ve kargonuzu yönetmesi; yayıncının dekontunuzu kontrol edip onaylaması; sahte ya da mükerrer dekontun önlenmesi;
        Bölüm 1.4'teki havale eşleştirmesi; size bildirim göndermek; izin verdiyseniz yayıncının kampanya SMS'leri.
      </p>
      <p>
        <strong>Veri sorumlusu.</strong> Sipariş, ödeme, dekont ve kargo bilgilerinin veri sorumlusu ilgili yayıncıdır;
        {' '}{BRAND} bunları yayıncı adına işler. Hesabın kendisi (giriş, telefon doğrulama, bildirimler) için veri
        sorumlusu {BRAND}'tir. Bölüm 1.4'teki doğruluk ölçümünde {BRAND} kendi adına işler.
      </p>
      <p>
        <strong>Saklama ve silme.</strong> Hesap bilgileriniz, dekont dosyalarınız ve dekonttan okunan bilgiler hesabınızı
        silene kadar saklanır. Hesabınızı Profil ekranından silebilirsiniz: hesabınız hemen kapanır ve yayıncılarla
        bağlantınız kaldırılır; kişisel verileriniz silme talebinizden itibaren en geç 30 gün içinde silinir. Silmeden
        sonra ödemelerinizin tutarı, tarihi ve banka referansı, dekontlarınızın özeti ve bu ödeme kayıtlarının, kişisel
        bilgileri silinmiş hesabınıza bağı, ödeme itirazları ve aynı dekontun yeniden kullanılmasının önlenmesi için
        kalır. Yayıncının müşteri ve sipariş kayıtlarında kalan bilgiler (örneğin platform kullanıcı adınız) için
        yayıncıya da başvurabilirsiniz. Silinen veriler yedeklerde en fazla 30 gün daha kalır (Bölüm 5).
      </p>

      <h3>1.6. Hizmet sağlayıcılar ve yurt dışına aktarım</h3>
      <p>
        {BRAND} sunucusu, Hostinger International Ltd.'in Almanya'daki (Frankfurt) veri merkezinde barındırılır. Bu
        politikada sunucuda tutulduğu belirtilen bütün veriler (yayıncı hesapları, kayıt formu bilgileri, müşteri
        kayıtları, müşteri uygulaması hesapları, ödeme ve dekont kayıtları, banka hareketleri) bu nedenle yurt dışında
        saklanır. Ayrıca şu hizmet sağlayıcılar kullanılır:
      </p>
      <ul>
        <li>
          Cloudflare, Inc. (R2 depolama hizmeti, yurt dışı): sunucu veritabanının ve masaüstü uygulamasının sunucuya
          yüklediği yedeklerin şifreli kopyaları ile müşteri uygulamasından yüklenen dekont dosyaları.
        </li>
        <li>
          Google (Firebase Cloud Messaging, yurt dışı): yayıncı paneli ve müşteri uygulaması bildirimleri. Bildirimi
          alacak cihazın bildirim jetonu ve bildirimin metni (örneğin yayıncıya giden dekont bildiriminde ödeyenin adı ve
          tutar, müşteriye giden sipariş bildiriminde tutar) bu hizmetten geçer.
        </li>
        <li>Netgsm (Türkiye): SMS ile gönderilen doğrulama kodları ve yayıncıların, izin verilen kampanya SMS'leri.</li>
        <li>Obifin: yayıncının banka hesap hareketleri (Bölüm 1.4).</li>
      </ul>

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
          Müşteri uygulaması hesabı, dekont dosyaları ve dekonttan okunan bilgiler: hesap silinene kadar (silmeden sonra
          kalanlar: Bölüm 1.5); dekonttan okunan tam metin: gönderimden 30 gün; SMS doğrulama ve parola sıfırlama kodları
          ile isteğin IP adresi: 7 gün
        </li>
        <li>
          Banka hesap hareketleri (Bölüm 1.4): bankanın gönderdiği kaydın kopyası 30 gün; açıklama metni, gönderen adı ve
          eşleştirmenin gerekçe metni 180 gün, bir müşteriyle eşleştirilmemiş havalelerde 30 gün; tutar, tarih, işlem
          kodu ve tipi, banka referans numarası, kısaltılmış IBAN, IBAN tanıma kodu ve eşleştirme kaydı 180 gün. Süreler
          havalenin işlem tarihinden işler; geç işlenen bir hareket {BRAND}'e alındıktan sonra en az 7 gün kalır.
          Silme her gün otomatik yapılır
        </li>
        <li>
          Öğrenilen IBAN–müşteri bağı (yalnızca kısaltılmış IBAN ve tanıma kodu): o IBAN'dan gelen son doğrulanmış
          havalenin işlem tarihinden itibaren 12 ay; bağ kaldırılırsa ya da ilgili kişinin silme talebi kabul edilirse
          daha önce
        </li>
        <li>
          Banka bağlantısı sona erdiğinde: bağlantı yayıncının talebiyle ya da yayıncının {BRAND} lisansının süresi
          dolduğu veya iptal edildiği için durdurulursa yeni hareket alınmaz; o yayıncıya ait banka hareketleri,
          eşleştirme kayıtları ve IBAN–müşteri bağları durdurmadan 30 gün sonra, bağlantı kaldırılırsa hemen silinir.
          Lisans bu süre içinde yenilenirse (bağlantıyı yönetici durdurmadıysa) veriler silinmez. Bağlantının Obifin'den
          kaldırılması için gereken kayıtlar (Obifin erişim bilgileri ve IBAN'sız hesap kayıtları) bağlantı kaldırılana
          kadar kalır
        </li>
        <li>
          Banka veri talepleri ve müşteri uygulamasından gelen silme talepleri: talebin kaydı (tarihi ve sonucu) saklanır;
          banka veri talebindeki IBAN tanıma kodu talep kapanınca, kısaltılmış IBAN talep kapandıktan 30 gün sonra, silme
          talebindeki telefon numarası talep işlendikten 30 gün sonra silinir
        </li>
        <li>
          Banka eşleştirmesinde {BRAND} yöneticisinin işlemlerine ve banka yönetim sayfalarının görüntülenmesine ait
          denetim kayıtları (işlem, zaman, müşteri kayıt numarası): 24 ay
        </li>
        <li>Silme ve imha işlemlerinin kayıtları (kişisel veri içermez): en az 3 yıl</li>
        <li>Sunucu erişim logları: 30 gün</li>
        <li>Güvenlik kayıtları: 90 gün</li>
        <li>
          Yedekler: sunucu veritabanının şifreli uzak yedekleri en fazla 30 gün tutulur; silinen veriler bu yedeklerde en
          fazla bu süre kadar daha kalır ve yedeğin süresi dolunca kendiliğinden silinir. Sunucunun kendi diskindeki
          şifreli yedek kopya en fazla 1 gün tutulur. Yedekler yalnızca bir arıza sonrası geri yükleme için kullanılır;
          geri yükleme yapılırsa, yedeğin alındığı tarihten sonra yapılmış silme işlemleri yeniden uygulanır
        </li>
        <li>
          Canlı yayın chat mesajları: yalnızca uygulama açık kaldığı sürece
          (siparişe/etikete dönüştürülen yorumların metni hariç — o metin,
          sipariş kaydının parçası olarak sipariş kaydıyla aynı süre saklanır)
        </li>
        <li>OAuth token: kullanıcı izni iptal edene kadar (kullanıcının makinesinde)</li>
      </ul>

      <h2 id="haklar">6. Haklarınız ve başvuru</h2>
      <p>KVKK'nın 11. maddesi uyarınca, kişisel verisi işlenen herkes veri sorumlusuna başvurarak şu haklara sahiptir:</p>
      <ul>
        <li>a) kişisel verisinin işlenip işlenmediğini öğrenme</li>
        <li>b) işlenmişse buna ilişkin bilgi talep etme</li>
        <li>c) işlenme amacını ve amacına uygun kullanılıp kullanılmadığını öğrenme</li>
        <li>ç) yurt içinde veya yurt dışında aktarıldığı üçüncü kişileri bilme</li>
        <li>d) eksik veya yanlış işlenmişse düzeltilmesini isteme</li>
        <li>e) KVKK'nın 7. maddesindeki şartlar çerçevesinde silinmesini veya yok edilmesini isteme</li>
        <li>f) (d) ve (e) bentleri uyarınca yapılan işlemlerin, verilerin aktarıldığı üçüncü kişilere bildirilmesini isteme</li>
        <li>g) münhasıran otomatik sistemlerle analiz edilmesi suretiyle aleyhine bir sonuç çıkmasına itiraz etme</li>
        <li>ğ) kanuna aykırı işleme nedeniyle zarara uğramışsa zararın giderilmesini talep etme</li>
      </ul>
      <p>GDPR kapsamındaki kişiler ayrıca veri taşınabilirliği hakkına sahiptir.</p>
      <p>
        <strong>Başvuru yolları.</strong> Başvurunuzu yazılı olarak, güvenli elektronik imza veya mobil imza ile ya da
        bize daha önce bildirdiğiniz ve sistemimizde kayıtlı e-posta adresinizden{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>{' '}
        adresine iletebilirsiniz; yazılı başvuru için posta adresimizi bu e-posta adresinden isteyebilirsiniz.
        Başvurunuzda ad soyadınızı, yazılı başvuruda imzanızı, T.C. kimlik numaranızı (yabancılar için uyruk ve pasaport
        ya da kimlik numarası), tebligata esas adresinizi, varsa e-posta, telefon ve faks numaranızı ve talebinizi
        belirtin. Başvurular, talebin niteliğine göre en kısa sürede ve en geç 30 gün içinde ücretsiz olarak
        sonuçlandırılır. Başvurunuz reddedilir, cevabı yetersiz bulursanız ya da süresinde cevap verilmezse, cevabı
        öğrendiğiniz tarihten itibaren 30 gün ve her hâlde başvuru tarihinden itibaren 60 gün içinde Kişisel Verileri
        Koruma Kurulu'na şikâyette bulunabilirsiniz.
      </p>
      <p>
        <strong>Banka hesap hareketleri (Bölüm 1.4).</strong> Havale eşleştirmesine ilişkin veri sorumlusu yayıncıdır;
        başvurunuzu ödeme yaptığınız ya da ödeme aldığınız yayıncıya veya yukarıdaki yollarla bize iletebilirsiniz. Bize
        gelen ve yayıncıyı ilgilendiren başvuruyu en geç 5 iş günü içinde yayıncıya iletir ve bunu size bildiririz;
        başvurunuz, bize ulaştığı tarihten itibaren en geç 30 gün içinde yayıncıyla birlikte sonuçlandırılır ve sonucu
        size bildirilir. Doğruluk ölçümüne ilişkin başvurunuzu doğrudan biz sonuçlandırırız. Hangi yayıncının hesabı
        olduğunu bilmiyorsanız havalenin tarihini ve tutarını yazmanız yeterlidir; IBAN'ınızı düz e-postayla göndermeyin.
        Kimliğinizi ve hesabın size ait olduğunu doğrulamak için ek bilgi ve belge isteyebiliriz.
      </p>
      <p>
        Silme talebiniz kabul edildiğinde sizinle ilişkilendirilmiş IBAN bağı silinir; ilgili havalelerin açıklaması,
        gönderen adı ve bankanın kaydının kopyası silinir ve eşleştirme kayıtlarındaki sizinle bağ kaldırılır.
        Havalelerin tutarı, tarihi, kısaltılmış IBAN'ı ve tanıma kodu Bölüm 5'teki süre dolana kadar kalır. Bir alanın
        saklanmasını gerektiren hukuki bir sebep sürüyorsa (örneğin devam eden bir ödeme itirazı), hangi alanın, hangi
        sebeple ve ne zamana kadar saklanacağını size yazılı olarak bildiririz. Obifin'deki kopyanın silinmesini
        gerektiğinde Obifin'den isteriz. Bankaların kendi kayıtları bankaların sorumluluğundadır; {BRAND} bu kayıtları
        değiştiremez veya silemez.
      </p>

      <h2>7. Veri güvenliği</h2>
      <p>
        Sunucularımızda iletim TLS 1.2 ve üzeri ile şifrelenir. Sunucu veritabanının uzak yedekleri OpenPGP (AES-256)
        ile, masaüstü uygulamasının sunucuya yüklediği yedekler AES-256-GCM ile şifrelenir; sunucunun kendi diskindeki
        kısa süreli yedek kopya da şifrelenir. Banka hareketlerinde gönderen hesabın IBAN'ı yalnızca kısaltılmış hâli ve
        anahtarlı bir tanıma kodu (HMAC-SHA256) olarak tutulur; bu kodun anahtarı sunucu yedeklerine alınmaz, ayrı
        saklanır. Banka yönetim sayfalarının görüntülenmesi ve banka eşleştirmesindeki elle eşleme ve eşlemeyi kaldırma
        işlemleri denetim kaydına yazılır. JWT token'ları güvenli bir secret ile imzalanır. Bir veri ihlali olursa, KVKK
        m. 12/5 ve Kurul kararları uyarınca Kurul'a ve etkilenen kişilere bildirim yapılır; banka verilerini etkileyen bir
        ihlali ilgili yayıncıya gecikmeksizin bildiririz. Yine de internet üzerinden veri iletiminin %100 güvenli olduğu
        garanti edilemez; siz de hesap şifrenizi gizli tutmakla yükümlüsünüz.
      </p>

      <h2>8. Çocuklar</h2>
      <p>
        {BRAND} 18 yaş altı kullanıcılar için tasarlanmamıştır. Bilerek 18 yaş altı bir
        kişiden veri toplamayız.
      </p>

      <h2>9. Politika değişiklikleri</h2>
      <p>
        Bu politikayı güncellediğimizde, üst kısımdaki &quot;Yürürlük tarihi&quot; güncellenir ve değişikliğin kısa özeti sayfanın
        başında gösterilir. Önemli değişikliklerde yayıncılara e-posta ile bilgi veririz; kayıt formuyla ya da müşteri
        uygulamasıyla kaydolan müşterilere önemli değişiklikleri ilk temas noktasında (örneğin müşteri uygulamasında bir
        duyuruyla) bildiririz.
      </p>

      <h2>10. İletişim</h2>
      <p>
        Gizlilik ile ilgili her tür soru için:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
    </LegalLayout>
  );
}
