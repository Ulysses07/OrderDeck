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
        düzeltildi ve genişletildi; yayıncının müşteri ve satış kayıtları (Bölüm 1.2a), müşteri uygulaması ve dekontlar
        (Bölüm 1.5), hizmet sağlayıcılar ve yurt dışına aktarım (Bölüm 1.6) ile WhatsApp yazışmaları (Bölüm 1.7) bölümleri
        eklendi; politikanın kapsamı ve veri
        sorumluları, lisans ve hesap verileri, canlı yayın chat verileri (Instagram kayıt bağlantısı) ve kayıt formu
        bilgileri (Bölüm 1.1–1.3), YouTube API kullanımı ve tarayıcı eklentisi (Bölüm 2–3), saklama süreleri, haklarınız
        ve başvuru yolları, veri güvenliği (Bölüm 5–7) ve politika
        değişikliklerinin bildirimi (Bölüm 9) güncellendi; kayıt formundaki bilgilendirme metni yenilendi. Önceki sürüm:
        2026-10-19.
      </p>
      <p>
        Bu gizlilik politikası, <strong>{BRAND}</strong> hizmetinin (Windows masaüstü uygulaması, yayıncı paneli,
        müşteri uygulaması, kayıt formu ve bunlara hizmet veren sunucular) ve <strong>orderdeckapp.com</strong> web
        sitesinin kişisel verileri nasıl topladığını, işlediğini, sakladığını ve paylaştığını açıklar.
      </p>
      <p>
        <strong>Veri sorumlusu:</strong> {LEGAL_NAME} (şahıs). Posta adresi: [YER TUTUCU: posta adresi ve varsa KEP
        adresi — avukat 08/2, 08/9]. İletişim:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
      <p>
        Bölüm 1.2a'daki müşteri ve satış kayıtlarının, Bölüm 1.3'teki alıcı bilgilerinin, Bölüm 1.5'teki sipariş, ödeme,
        dekont ve kargo bilgilerinin, Bölüm 1.4'teki havale eşleştirmesinin, Bölüm 1.2'deki Instagram kayıt bağlantısı
        akışının ve Bölüm 1.7'deki WhatsApp yazışmalarının veri sorumlusu ise ilgili yayıncıdır; {BRAND} bu verileri yayıncı adına işler. Bölüm 1.4'te
        anlatılan eşleştirme doğruluğu ölçümünü ve Bölüm 1.5'te anlatılan, dekont özetlerinin bütün yayıncılar arasında
        karşılaştırılmasını ise {BRAND} kendi adına yapar.
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
        <li><strong>{BRAND} sunucularına gönderilmez</strong> (aşağıdaki iki istisna dışında), üçüncü kişilerle paylaşılmaz</li>
      </ul>
      <p>
        <strong>Birinci istisna:</strong> yayıncının <strong>siparişe/etikete
        dönüştürdüğü</strong> yorumlar. Sipariş kaydına dönüşen yorumun metni,
        yayıncının işini yürütebilmesi (sipariş, kargo, itiraz takibi) için
        sipariş kaydının bir parçası olarak yayıncının bilgisayarında saklanır
        ve sipariş verisiyle birlikte {BRAND} sunucularına aktarılır (Bölüm 1.2a).
        Ayrıntı için <a href="/veri-silme">Veri Silme</a> sayfasına bakın.
      </p>
      <p>
        <strong>İkinci istisna: Instagram kayıt bağlantısı.</strong> Instagram hesabını kayıt bağlantısı akışı (&quot;!kayıt&quot;)
        için {BRAND}'e bağlamış yayıncılarda, o hesabın canlı yayınlarına yazılan yorumlar (yorum metni, yorumu yazanın
        Instagram kullanıcı adı ve kimliği) Meta tarafından {BRAND} sunucusuna da iletilir (Bölüm 1.6). Sunucu yalnızca
        &quot;!kayıt&quot; yazan izleyiciye Instagram'dan özel mesajla yayıncının kayıt formunun bağlantısını gönderir; diğer yorumlar
        kaydedilmeden atılır. Meta'dan gelen bildirim, işlendikten sonra sunucunun iş kuyruğunda 1 gün kalır ve silinir;
        işlenemezse hata incelenip kaydı silinene kadar kalabilir. Bu işlemenin veri sorumlusu yayıncıdır;
        {' '}{BRAND} yayıncı adına işler. Hukuki sebepler: &quot;!kayıt&quot; yazan izleyicinin isteğinin karşılanması için sözleşmenin kurulmasıyla
        doğrudan ilgili işleme (KVKK m. 5/2-c); diğer yorumların sunucuya gelip atılması için yayıncının bu akışı yürütme
        konusundaki meşru menfaati (KVKK m. 5/2-f). Bu iki istisna dışında ham chat akışı sunucuya gönderilmez.
      </p>

      <h3>1.2a. Yayıncının müşteri ve satış kayıtları</h3>
      <p>
        Yayıncının masaüstü uygulamasındaki müşteri kayıtlarının tam bir kopyası {BRAND} sunucusunda da tutulur; canlı
        yayından oluşturulan kayıtlar ile kayıt formundan (Bölüm 1.3) ve müşteri uygulamasından (Bölüm 1.5) gelen kayıtlar
        buna dahildir. Bu kayıtlarda şunlar bulunabilir: platform ve kullanıcı adı, ad soyad, görünen ad (takma ad), telefon
        numarası, e-posta adresi, teslimat adresi, şifrelenmiş T.C. kimlik numarası, WhatsApp ve SMS izinlerinin kaydı,
        yayıncının kişi hakkındaki notları, engelleme bilgisi ve engelleme gerekçesi. Sunucuda ayrıca yayıncının sipariş ve
        etiket kayıtları (kullanıcı adı, görünen ad, siparişe dönüşen yorumun metni, ürün kodu ve fiyat), yayıncının masaüstü
        uygulamasına girdiği ödeme kayıtları (ödeyen adı, tutar ve tarih dahil), kargo ve müşteri bakiyesi kayıtları ile yayın
        kayıtları tutulur.
      </p>
      <p>
        <strong>Amaçlar.</strong> Bu kayıtların yayıncının bilgisayarları ve yayıncı paneli arasında eşitlenmesi ve panelde
        gösterilmesi; aynı kişiye ait kayıtların birleştirilmesi; müşteri uygulaması hesabının yayıncının müşteri kaydına
        bağlanması (Bölüm 1.5); müşterilerin İleti Yönetim Sistemi'nde (İYS) yayıncı adına kayıtlı SMS izinlerinin {BRAND}'e
        alınması (bunun için telefon numaraları Netgsm aracılığıyla İYS'de sorgulanır); yayıncının WhatsApp sohbetlerine
        sipariş ve kargo durumuna göre otomatik etiket konması (Bölüm 1.7); Bölüm 1.4'teki havale eşleştirmesi.
      </p>
      <p>
        <strong>Veri sorumlusu ve hukuki sebepler.</strong> Bu kayıtların veri sorumlusu yayıncıdır; {BRAND} bunları
        yayıncı adına barındırır ve işler. Bölüm 1.4'te anlatılan doğruluk ölçümünde ise {BRAND} bu kayıtlardaki kullanıcı
        adlarını ve adları kendi adına kullanır. Hukuki sebepler: [YER TUTUCU: hukuki sebep — avukat 08/19; öneri: kayıtların
        eşitlenmesi ve gösterilmesi ile sipariş, ödeme ve kargo kayıtları için müşteri ile yayıncı arasındaki satış
        sözleşmesinin kurulması ve ifası (KVKK m. 5/2-c); yayıncının notları, engelleme bilgisi, kayıtların birleştirilmesi
        ve sohbet etiketleri için yayıncının meşru menfaati (KVKK m. 5/2-f); T.C. kimlik numarası ve SMS izinleri için
        Bölüm 1.3'teki dayanaklar; havale eşleştirmesi ve doğruluk ölçümü için Bölüm 1.4].
      </p>
      <p>
        <strong>Saklama.</strong> Bu kayıtlar yayıncının {BRAND} hesabı silinene kadar saklanır [YER TUTUCU: saklama süresi
        — avukat 08/15]. Bir kaydın silinmesini yayıncıdan ya da Bölüm 6'daki yollarla bizden isteyebilirsiniz. Müşteri
        uygulaması hesabını silen kişinin bu kayıtlardaki kişisel bilgileri Bölüm 1.5'te anlatıldığı gibi silinir. Yayıncının
        masaüstü uygulamasının sunucuya yüklediği yedekler için Bölüm 5.
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
        <li>
          Formun gönderildiği IP adresi ve tarayıcı bilgisi (90 gün sonra silinir) ile alıcıya gösterilen bilgilendirme
          metninin ve yayıncının veri sorumlusu bilgisinin sürümü
        </li>
      </ul>
      <p>
        Bu veriler ilgili yayıncının sipariş, ödeme, kargo ve fatura süreçleri için işlenir; buna, yayıncının hesabına
        gelen bir havalenin hangi müşteriye ait olduğunun belirlenmesi de dahildir (Bölüm 1.4). Üçüncü kişilerle
        pazarlama amacıyla paylaşılmaz veya satılmaz. Bilgilendirme ve kampanya mesajları için alıcının izni formdaki onay
        kutularıyla istenir; alıcı bu izni dilediği an geri çekebilir. SMS izni olmayan numaraya {BRAND} üzerinden kampanya
        SMS'i gönderilmez. Siparişi, ödemesi ve kargosuyla ilgili mesajlar alıcının verdiği WhatsApp numarasına gönderilir.
        Bu verilerin veri sorumlusu, formu oluşturan yayıncıdır; {BRAND} verileri yayıncı adına barındırır ve yayıncının
        uygulamasına iletir. Bölüm 1.4'te anlatılan eşleştirme doğruluğu ölçümünde ise {BRAND} bu verileri kendi adına
        kullanır. Yayıncı veri sorumlusu bilgilerini (adı veya unvanı, posta adresi ve KVKK başvuru e-postası) bize
        bildirdiyse bu bilgiler formda gösterilir; bildirmediyse bu bilgileri bize yazarak öğrenebilirsiniz.
      </p>
      <p>Hukuki sebepler:</p>
      <ul>
        <li>
          Sipariş, ödeme, kargo ve fatura işlemleri ile platform kullanıcı adının (isteğe bağlı olarak Google veya
          Facebook ile) doğrulanması: alıcı ile yayıncı arasındaki satış sözleşmesinin kurulması ve ifası (KVKK m. 5/2-c).
        </li>
        <li>
          Fatura için T.C. kimlik numarası: [YER TUTUCU: dayanak mevzuat — avukat 08/19] uyarınca yayıncının hukuki
          yükümlülüğü (KVKK m. 5/2-ç).
        </li>
        <li>
          Bilgilendirme ve kampanya mesajları: alıcının açık rızası ve 6563 sayılı Kanun kapsamındaki onayı. SMS izninin
          İleti Yönetim Sistemi'ne bildirilmesi ve izin kayıtlarının saklanması: 6563 sayılı Kanun ve ilgili yönetmelikten
          doğan hukuki yükümlülük (KVKK m. 5/2-ç).
        </li>
        <li>
          Formun gönderildiği IP adresi ve tarayıcı bilgisi ile gösterilen metin sürümleri: [YER TUTUCU: hukuki sebep —
          avukat 08/19; öneri: formun kötüye kullanılmasının önlenmesi ve bilgilendirmenin yapıldığının ispatı için
          yayıncının ve {BRAND}'in meşru menfaati (KVKK m. 5/2-f)].
        </li>
        <li>Havale eşleştirmesi ve doğruluk ölçümü: Bölüm 1.4.</li>
      </ul>

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
        alabilmesi için Obifin'de saklanır. {BRAND}'in Obifin'e bağlanmak için kullandığı erişim bilgilerinden parola ve
        API anahtarı sunucuda şifrelenmiş olarak tutulur.
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
          tanıyıp onaylayabilmesi ya da reddedebilmesi için); onaylanan hesapta ayrıca IBAN'ın tanıma kodu ve Obifin'in
          hesap için ilettiği bildirim notu.
        </li>
        <li>Hesaba gelen her havalenin tutarı, para birimi, işlem zamanı, işlem kodu ve tipi ile banka referans numarası.</li>
        <li>
          Bankanın hareket açıklaması. Gönderenin yazdığı açıklamayı içerir; bankaya göre gönderenin adını da içerebilir.
          Açıklamada tanınabilir biçimde yazılmış TR IBAN'ları ve 11 haneli numaralar (örneğin T.C. kimlik numarası ya da
          telefon numarası) kayda alınırken maskelenir; bazı yazımlar (örneğin bölünerek ya da hatalı yazılmış
          numaralar) maskelenmeyebilir.
        </li>
        <li>Bankanın iletmesi hâlinde gönderenin adı. [YER TUTUCU: bu alanın bugünkü amacı — Burak, plan karar 24]</li>
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
        tahsilatı olarak tanımlanan hareketler otomatik eşleştirmede kullanılmaz ve bunlardan IBAN öğrenilmez.
      </p>
      <p>
        <strong>Açıklamalardaki hassas bilgiler.</strong> Havale açıklaması serbest metindir. Açıklamaya sağlık, din,
        dernek veya sendika üyeliği gibi bilgiler yazmamanızı rica ederiz. Böyle bir bilgi yazılmışsa ayrıca bir işleme
        konu edilmez ve açıklamayla birlikte Bölüm 5'teki sürede silinir.
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
          yürütür. Yayıncının adı veya unvanı, posta adresi ve KVKK başvuru e-postası, yayıncı bu bilgileri bize
          bildirdiyse yayıncının kayıt formunda ve müşteri uygulamasında yayıncıya ait ekranlarda gösterilir; bildirmediyse
          bu bilgileri bize yazarak öğrenebilirsiniz.
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
          Bankanın kaydının kopyası (teknik arızaların incelenmesi): kopyanın ait olduğu amacın veri sorumlusunun ((i)
          amacında yayıncının, (ii) amacında {BRAND}'in) banka bağlantısının ve eşleştirmenin doğru çalışmasını sağlama
          konusundaki meşru menfaati (KVKK m. 5/2-f). Kopya yalnızca yukarıda sayılan alanlarla ve Bölüm 5'teki süre kadar
          tutulur.
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
        dekontunu yüklemek için T.C. kimlik numarası gerekir). Bağlandığınız her yayıncı için hangi yayıncıya
        bağlandığınız, platform ve o platformdaki kullanıcı adınız kaydedilir. Parolanın kendisi saklanmaz; yalnızca parolanızı doğrulamaya
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
        Dekont, yayıncının kontrolüne yardımcı olmak için otomatik olarak denetlenir: aynı dosya ikinci kez yüklenemez
        (bu denetim bütün yayıncıları kapsar, aşağıda); aynı bilgilerle daha önce dekont yüklenip yüklenmediği, dekonttaki
        alıcı IBAN'ının yayıncının ödeme IBAN'ıyla uyumu ve dekontun ne kadar güvenle okunduğu yayıncıya uyarı olarak
        gösterilir. Ödemeyi onaylama ya da reddetme kararını yayıncı verir. Yayıncı dekontunuzu onaylarsa ve yayıncının
        banka bağlantısı varsa dekont bilgileri Bölüm 1.4'teki havale eşleştirmesinde kullanılır.
      </p>
      <p>
        <strong>Yayıncılar arası mükerrer dekont denetimi.</strong> Dekont dosyasının özeti, aynı dekontun başka bir
        yayıncıda da kullanılmasını önlemek için {BRAND}'teki bütün yayıncıların dekont özetleriyle karşılaştırılır; aynı
        dosya daha önce herhangi bir yayıncıya yüklenmişse yükleme reddedilir. Hesap silindikten sonra kalan dekont özetleri
        de bu amaçla kullanılır. Bu karşılaştırmanın veri sorumlusu {BRAND}'tir; hukuki sebebi, sahte ve mükerrer
        dekontun önlenmesindeki meşru menfaatimizdir (KVKK m. 5/2-f).
      </p>
      <p>
        <strong>Amaçlar.</strong> Hesabınızı açmak ve korumak; bağlandığınız yayıncıların siparişlerinizi, ödemelerinizi
        ve kargonuzu yönetmesi; yayıncının dekontunuzu kontrol edip onaylaması; sahte ya da mükerrer dekontun önlenmesi;
        Bölüm 1.4'teki havale eşleştirmesi; size bildirim göndermek; izin verdiyseniz yayıncının kampanya SMS'leri.
      </p>
      <p>
        <strong>Veri sorumlusu.</strong> Sipariş, ödeme, dekont ve kargo bilgilerinin veri sorumlusu ilgili yayıncıdır;
        {' '}{BRAND} bunları yayıncı adına işler. Hesabın kendisi (giriş, telefon doğrulama, bildirimler) için veri
        sorumlusu {BRAND}'tir [YER TUTUCU: avukat 08/10; müşteri uygulamasının kayıt ekranı metniyle aynı karar]. Bölüm
        1.4'teki doğruluk ölçümünde ve yukarıdaki yayıncılar arası mükerrer dekont denetiminde {BRAND} kendi adına işler.
      </p>
      <p>Hukuki sebepler:</p>
      <ul>
        <li>
          Hesabın açılması ve yönetimi; bağlandığınız yayıncıların siparişlerinizi, ödemelerinizi ve kargonuzu yönetmesi;
          dekontun okunması ve yayıncının kontrolüne sunulması: sizinle {BRAND} arasındaki kullanım ilişkisinin ve
          sizinle yayıncı arasındaki satış sözleşmesinin kurulması ve ifası (KVKK m. 5/2-c).
        </li>
        <li>
          Hesap güvenliği (telefon doğrulama, parola sıfırlama, isteklerin ve dekont gönderimlerinin IP adresi ve
          tarayıcı bilgisi) ile sahte ve mükerrer dekontun önlenmesi: {BRAND}'in ve yayıncının meşru menfaati (KVKK
          m. 5/2-f); yayıncılar arası mükerrer dekont denetiminde yalnızca {BRAND}'in meşru menfaati.
        </li>
        <li>
          T.C. kimlik numarası (fatura için ve 9.990 TL'yi aşan bir ödemenin dekontunda): [YER TUTUCU: dayanak mevzuat —
          avukat 08/19] uyarınca hukuki yükümlülük (KVKK m. 5/2-ç).
        </li>
        <li>
          Bildirimler: hesabınız ve siparişlerinizle ilgili bilgilendirme olarak kullanım ilişkisinin ifası (KVKK m.
          5/2-c). Bildirim jetonu yalnızca cihazınızda bildirimlere izin verdiğinizde alınır; izni cihaz ayarlarınızdan
          geri alabilirsiniz.
        </li>
        <li>Yayıncının kampanya SMS'leri: açık rızanız ve 6563 sayılı Kanun kapsamındaki onayınız.</li>
        <li>Havale eşleştirmesi ve doğruluk ölçümü: Bölüm 1.4.</li>
      </ul>
      <p>
        <strong>Saklama ve silme.</strong> Hesap bilgileriniz, dekont dosyalarınız ve dekonttan okunan bilgiler hesabınızı
        silene kadar saklanır. Bir yayıncıya bağlandığınızda adınız, telefonunuz ve adresiniz o yayıncının müşteri
        kayıtlarına eklenir ve yayıncıya gösterilir (yayıncı paneli ve masaüstü uygulaması; Bölüm 1.2a). Hesabınızı Profil
        ekranından silebilirsiniz: hesabınız hemen kapanır ve yayıncılarla bağlantınız kaldırılır; kişisel verileriniz silme
        talebinizden itibaren en geç 30 gün içinde silinir. Silme, yayıncıların müşteri kayıtlarındaki adınızı,
        telefonunuzu ve adresinizi de kapsar; yayıncının bilgisayarında bunlar, masaüstü uygulaması sunucuyla
        eşitlendiğinde silinir. Silmeden sonra ödemelerinizin tutarı, tarihi ve banka referansı, dekontlarınızın özeti ve
        bu ödeme kayıtlarının, kişisel bilgileri silinmiş hesabınıza bağı, ödeme itirazları ve aynı dekontun yeniden
        kullanılmasının önlenmesi için, [YER TUTUCU: hukuki sebep, örneğin KVKK m. 5/2-e — avukat 08/8] nedeniyle [YER
        TUTUCU: süre — avukat 08/8, 08/15] saklanır. Şunlar bu silmeyle silinmez; bunlar için yayıncıya başvurabilirsiniz:
        yayıncının müşteri kayıtlarında (sunucuda ve yayıncının bilgisayarında) kalan platform kullanıcı adınız, yayıncının
        sizinle ilgili notları ve engelleme bilgisi; yayıncının sipariş kayıtlarındaki (sunucuda ve yayıncının bilgisayarında)
        kullanıcı adınız, görünen adınız (takma adınız) ve siparişe dönüşen yorumlarınızın metni [YER TUTUCU: sipariş metni
        boşaltması (SalesSync__ScrubOrderTextOnPurge) yayından önce açılırsa görünen ad ve yorum metni bu listeden çıkar ve
        silinenler arasına girer, kullanıcı adı kalır — Burak]. Silinen veriler sunucu veritabanının yedeklerinde en fazla 30
        gün daha kalır; yayıncının masaüstü uygulamasının sunucuya yüklediği yedeklerde ise o yedeklerin saklama süresi
        boyunca kalır ve yalnızca geri yüklemede kullanılır (Bölüm 5).
      </p>

      <h3>1.6. Hizmet sağlayıcılar ve yurt dışına aktarım</h3>
      <p>
        {BRAND} sunucusu, Hostinger International Ltd.'in Almanya'daki (Frankfurt) veri merkezinde barındırılır. Bu
        politikada sunucuda tutulduğu belirtilen bütün veriler (yayıncı hesapları, kayıt formu bilgileri, yayıncının
        müşteri ve satış kayıtları, müşteri uygulaması hesapları, ödeme ve dekont kayıtları, banka hareketleri, WhatsApp
        yazışmaları) bu nedenle yurt dışında saklanır. Ayrıca şu hizmet sağlayıcılar kullanılır:
      </p>
      <ul>
        <li>
          Cloudflare, Inc. (R2 depolama hizmeti, yurt dışı): sunucu veritabanının ve masaüstü uygulamasının sunucuya
          yüklediği yedeklerin şifreli kopyaları, müşteri uygulamasından yüklenen dekont dosyaları ve WhatsApp
          yazışmalarındaki dosyalar (Bölüm 1.7).
        </li>
        <li>
          Google (Firebase Cloud Messaging, yurt dışı): yayıncı paneli ve müşteri uygulaması bildirimleri. Bildirimi
          alacak cihazın bildirim jetonu ve bildirimin metni (örneğin yayıncıya giden dekont bildiriminde ödeyenin adı ve
          tutar, müşteriye giden sipariş bildiriminde tutar) bu hizmetten geçer.
        </li>
        <li>
          Meta (WhatsApp Business Cloud API ile Instagram ve Facebook Graph API, yurt dışı): yayıncının WhatsApp hattına
          gelen ve o hattan giden mesajlar ve dosyalar, WhatsApp Business uygulamasından aktarılan geçmiş sohbetler ve
          yayıncının telefon rehberindeki kişi adları (Bölüm 1.7); Instagram canlı yayın yorumlarının {BRAND}'e
          iletilmesi ve kayıt bağlantısının özel mesajla gönderilmesi (Bölüm 1.2).
        </li>
        <li>
          Netgsm (Türkiye): SMS ile gönderilen doğrulama kodları, yayıncıların izin verilen kampanya SMS'leri ve İleti
          Yönetim Sistemi'ne iletilen SMS izinleri.
        </li>
        <li>
          İleti Yönetim Sistemi A.Ş. (İYS, Türkiye): verilen ve geri çekilen SMS ticari ileti izinleri, yayıncının markası
          adına, Netgsm aracılığıyla bildirilir; yayıncının müşteri kayıtlarındaki telefon numaraları için İYS'deki izin
          kaydı da Netgsm aracılığıyla sorgulanır (Bölüm 1.2a).
        </li>
        <li>Brevo (e-posta iletimi, yurt dışı): yayıncılara giden hesap, lisans ve bilgilendirme e-postaları.</li>
        <li>
          Lisans ödemesinin alındığı ödeme kuruluşu: [YER TUTUCU: adı ve ülkesi; lisans ödemesi bir ödeme kuruluşu
          üzerinden alınmıyorsa bu satır çıkar ve Bölüm 1.1'deki ifade düzeltilir].
        </li>
        <li>
          Obifin [YER TUTUCU: ticaret unvanı] (Türkiye; verilerin barındırıldığı yer: [YER TUTUCU: doğrulanacak]):
          yayıncının banka hesap hareketleri (Bölüm 1.4).
        </li>
      </ul>
      <p>
        [YER TUTUCU: sağlayıcıların sözleşme tarafı tüzel kişileri ve verinin işlendiği ülkeler (Google, Meta, Brevo,
        Cloudflare R2 kova konumu) — avukat paketi 06 §7; yurt dışına aktarımın dayanağı ve bu bölümün ara metni — avukat
        08/4]
      </p>

      <h3>1.7. WhatsApp yazışmaları</h3>
      <p>
        WhatsApp Business hattını {BRAND}'e bağlayan yayıncılarda, o hatta gelen ve o hattan giden WhatsApp mesajları
        Meta'nın WhatsApp Business Cloud API'si üzerinden {BRAND} sunucusuna gelir ve sunucuda saklanır; yayıncının
        panelden, otomatik olarak ya da telefonundaki WhatsApp Business uygulamasından gönderdiği mesajlar da buna dahildir.
        Hat, telefondaki WhatsApp Business uygulamasıyla birlikte bağlanırsa ve yayıncı bağlantı sırasında sohbet geçmişini
        paylaşmayı onaylarsa, bağlantı kurulduğunda o uygulamadaki bire bir sohbetlerin bağlantıdan önceki en çok 180 güne
        ait mesajları (grup sohbetleri hariç) bir kez sunucuya aktarılır; bu geçmiş mesajlardaki dosyalar indirilmez. Aynı
        bağlantıda yayıncının telefon rehberindeki kişilerin adları da Meta'dan gelir: bir ad yalnızca, WhatsApp profil adı
        bilinmeyen bir sohbette kişinin adı olarak gösterilmek için kaydedilir; sohbeti olmayan kişilerin rehber bilgileri
        kaydedilmez, rehber bildirimi işlendikten sonra sunucunun iş kuyruğunda 1 gün kalır ve silinir. Saklananlar: karşı
        tarafın telefon numarası ve WhatsApp profil adı (profil adı yoksa yayıncının rehberindeki adı), mesaj metinleri
        (bağlantıdan önceki geçmiş sohbetler dahil), gönderilen ve alınan fotoğraf, ses, video ve belgeler (Cloudflare R2'de,
        Bölüm 1.6), mesajların iletim durumu, yayıncının sohbete verdiği etiketler ve kişinin pazarlama mesajı tercihi.
        WhatsApp'tan gönderilen PDF dekontlar sunucuda okunur; gönderen adı, tutar, tarih ve referans numarası ile dosyanın
        bir özeti, yayıncı dekontu açmadan kontrol edebilsin diye sohbetin yanında gösterilir. Bu dekontlar kendiliğinden
        ödeme kaydına dönüşmez; kararı yayıncı verir.
      </p>
      <p>
        <strong>Amaçlar.</strong> Yayıncının müşterileriyle ve kendisine yazan diğer kişilerle sipariş, ödeme ve kargo
        konusunda yazışması; mesajların (bağlantıdan önceki geçmiş sohbetler dahil) yayıncı panelinde gösterilmesi ve
        yanıtlanması, profil adı bilinmeyen sohbetlerde kişinin yayıncının rehberindeki adıyla gösterilmesi; yayıncının
        bilgilendirme ve kampanya mesajı göndermesi; kişinin pazarlama mesajı tercihinin (Meta'nın bildirdiği ya da bir
        gönderim hatasından anlaşılan) kaydının tutulması.
      </p>
      <p>
        <strong>Veri sorumlusu ve hukuki sebepler.</strong> Bu yazışmaların veri sorumlusu, WhatsApp hattının sahibi olan
        yayıncıdır; {BRAND} bunları yayıncı adına işler. Hukuki sebepler: sipariş, ödeme ve kargo yazışmaları için
        müşteri ile yayıncı arasındaki satış sözleşmesinin kurulması ve ifası (KVKK m. 5/2-c); müşteri olmayan kişilerle
        yazışmalar, bağlantıdan önceki geçmiş sohbetlerin aktarılması, rehberdeki adların kaydı ve dekontların ön kontrolü
        için yayıncının meşru menfaati (KVKK m. 5/2-f); kampanya mesajları için kişinin açık rızası ve 6563 sayılı Kanun
        kapsamındaki onayı; pazarlama mesajı tercihinin kaydı için 6563 sayılı Kanundan doğan hukuki yükümlülük (KVKK m.
        5/2-ç).
      </p>
      <p>
        <strong>Saklama.</strong> Yazışmalar (bağlantıdan önceki geçmiş sohbetler dahil), rehberden alınan adlar ve
        dekontlardan okunan bilgiler için bugün ayrı bir saklama süresi yoktur; yayıncının {BRAND} hesabı silinene kadar
        saklanır [YER TUTUCU: saklama süresi — avukat 08/15]. Gönderilen ve alınan dosyalar (Cloudflare R2'de) bugün otomatik
        olarak silinmez; yayıncının hesabı silindiğinde de kalır [YER TUTUCU: dosyaların saklama süresi ve silinme yolu —
        Burak (hesap silmesinde dosyaların da silinmesi), avukat 08/15]. Mesaj gönderimlerinin teknik deneme kayıtları 30 gün
        sonra silinir. Bu verilerin silinmesini yayıncıdan ya da Bölüm 6'daki yollarla bizden isteyebilirsiniz.
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
        <li>
          Siparişe/etikete dönüştürülen yorumlar ile bu yorumları yazanların kanal adı ve kullanıcı adı dışında (Bölüm
          1.2, 1.2a) yalnızca kullanıcının kendi makinesinde işlenir
        </li>
        <li>
          Bu istisnalar dışında {BRAND} sunucularına aktarılmaz; sunucuya aktarılan adlar, yayıncının banka bağlantısı
          varsa Bölüm 1.4'teki havale eşleştirmesinde ve doğruluk ölçümünde de kullanılır
        </li>
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
          parçası olarak saklanır ve alıcının kayıt olduğu yayıncıya görünür; yayıncının banka bağlantısı varsa
          Bölüm 1.4'teki eşleştirmede ve doğruluk ölçümünde de kullanılır ve {BRAND}'in yetkili yöneticisi
          tarafından görülebilir (saklama süresi: Bölüm 5, &quot;Kayıt formu ile toplanan alıcı bilgileri&quot;)
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
        <li>
          Sohbet verisini başka bir adrese göndermez; yalnızca {BRAND} sunucusundan güncel sayfa seçicilerini (sohbet
          alanını sayfada bulmaya yarayan teknik tanımları) indirir. Bu istekte sohbet verisi yoktur; isteğin IP adresi
          ve tarayıcı bilgisi sunucu erişim loglarına düşer (Bölüm 5)
        </li>
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
        <li>
          Yayıncının müşteri ve satış kayıtlarının sunucudaki kopyası (Bölüm 1.2a): yayıncının hesabı silinene kadar [YER
          TUTUCU: saklama süresi — avukat 08/15]; müşteri uygulaması hesabını silen kişinin bu kayıtlardaki kişisel
          bilgileri silme talebinden itibaren en geç 30 gün içinde (Bölüm 1.5)
        </li>
        <li>
          Kayıt formu ile toplanan alıcı bilgileri: form kaydı sunucuda yayıncının hesabı silinene kadar [YER TUTUCU:
          saklama süresi — avukat 08/15]; yayıncının müşteri kayıtlarına aktarılan kopyası Bölüm 1.2a'daki, masaüstü
          uygulamasının sunucuya yüklediği yedeklerdeki kopyası aşağıdaki masaüstü yedekleri satırındaki süreyle
        </li>
        <li>
          Müşteri uygulaması hesabı, dekont dosyaları ve dekonttan okunan bilgiler: hesap silinene kadar (silmeden sonra
          kalanlar: Bölüm 1.5); dekonttan okunan tam metin: gönderimden 30 gün; SMS doğrulama ve parola sıfırlama kodları
          ile isteğin IP adresi: 7 gün
        </li>
        <li>
          WhatsApp yazışmaları (bağlantıdan önceki geçmiş sohbetler dahil), rehberden alınan adlar ve WhatsApp'tan gelen
          dekontlardan okunan bilgiler (Bölüm 1.7): yayıncının hesabı silinene kadar [YER TUTUCU: saklama süresi — avukat
          08/15]; yazışmalardaki dosyalar: bugün otomatik olarak silinmez, yayıncının hesabı silindiğinde de kalır [YER
          TUTUCU: dosyaların saklama süresi ve silinme yolu — Burak, avukat 08/15]; rehberden gelip kaydedilmeyen kişi
          bilgileri: iş kuyruğunda 1 gün; mesaj gönderimlerinin teknik deneme kayıtları: 30 gün
        </li>
        <li>
          Instagram canlı yayın yorumlarının bildirimleri (Bölüm 1.2): kaydedilmez; bildirim işlendikten sonra sunucunun iş
          kuyruğunda 1 gün
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
          Banka veri talepleri ve müşteri uygulamasından gelen silme talepleri: talebin kaydı (tarihi ve sonucu) [YER
          TUTUCU: süre — avukat, plan karar 22] saklanır; banka veri talebindeki IBAN tanıma kodu talep kapanınca, kısaltılmış IBAN talep kapandıktan 30 gün sonra, silme
          talebindeki telefon numarası talep işlendikten 30 gün sonra silinir
        </li>
        <li>
          Banka eşleştirmesinde {BRAND} yöneticisinin işlemlerine ve banka yönetim sayfalarının görüntülenmesine ait
          denetim kayıtları (işlem, zaman, müşteri kayıt numarası): 24 ay
        </li>
        <li>Silme ve imha işlemlerinin kayıtları (kişi adı içermez): en az 3 yıl</li>
        <li>Sunucu erişim logları: 30 gün</li>
        <li>Güvenlik kayıtları: 90 gün</li>
        <li>
          Yedekler: sunucu veritabanının şifreli uzak yedekleri en fazla 30 gün tutulur; silinen veriler bu yedeklerde en
          fazla bu süre kadar daha kalır ve yedeğin süresi dolunca kendiliğinden silinir. Sunucunun kendi diskindeki
          şifreli yedek kopya en fazla 1 gün tutulur. Yedekler yalnızca bir arıza sonrası geri yükleme için kullanılır;
          geri yükleme yapılırsa, yedeğin alındığı tarihten sonra yapılmış silme işlemleri yeniden uygulanır (son kayıtlı
          imha özetine kadar)
        </li>
        <li>
          Masaüstü uygulamasının sunucuya yüklediği yedekler (yayıncının bilgisayarındaki müşteri, sipariş, etiket, yayın ve
          çekiliş kayıtlarının şifreli kopyası): her yayıncı için son 5 yedek ile her ayın ilk yedeği, yayıncının hesabı
          silinene kadar [YER TUTUCU: aylık yedeklerin saklama süresi — Burak, avukat 08/15]; bu yedeklerin Cloudflare
          R2'deki kopyası sunucudakiyle her gece eşitlenir. Bir kayıt sonradan silinse de (örneğin müşteri uygulaması
          hesabının silinmesiyle) daha önce alınmış yedeklerdeki kopyası o yedeğin süresi boyunca kalır ve yalnızca geri
          yüklemede kullanılır
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
        <strong>Başvuru yolları.</strong> Başvurunuzu yazılı olarak [YER TUTUCU: posta adresi — avukat 08/2, 08/9]
        adresine, [YER TUTUCU: varsa KEP adresi] kayıtlı elektronik posta adresimize, güvenli elektronik imza veya mobil
        imza ile ya da bize daha önce bildirdiğiniz ve sistemimizde kayıtlı e-posta adresinizden{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>{' '}
        adresine iletebilirsiniz. Başvurunuzda ad soyadınızı, yazılı başvuruda imzanızı, T.C. kimlik numaranızı (yabancılar için uyruk ve pasaport
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
        başvurunun havale eşleştirmesine ilişkin kısmını veri sorumlusu olarak yayıncı sonuçlandırır. Doğruluk ölçümüne
        ilişkin kısmını, başvurunuzun bize ulaştığı tarihten itibaren en geç 30 gün içinde biz sonuçlandırırız. Hangi
        yayıncının hesabı olduğunu bilmiyorsanız havalenin tarihini ve tutarını yazmanız yeterlidir; IBAN'ınızı e-postanın
        içine yazmayın, gerekirse size güvenli bir yol bildiririz. Kimliğinizi ve hesabın size ait olduğunu doğrulamak için
        ek bilgi ve belge isteyebiliriz.
      </p>
      <p>
        Silme talebiniz kabul edildiğinde sizinle ilişkilendirilmiş IBAN bağı silinir; ilgili havalelerin açıklaması,
        gönderen adı ve bankanın kaydının kopyası silinir ve eşleştirme kayıtlarındaki sizinle bağ kaldırılır.
        Havalelerin tutarı, tarihi, kısaltılmış IBAN'ı ve tanıma kodu [YER TUTUCU: saklama sebebi — avukat 08/8, plan
        karar 6] nedeniyle Bölüm 5'teki süre dolana kadar kalır. Bir alanın
        saklanmasını gerektiren hukuki bir sebep sürüyorsa (örneğin devam eden bir ödeme itirazı), hangi alanın, hangi
        sebeple ve ne zamana kadar saklanacağını size yazılı olarak bildiririz. Obifin'deki kopyanın silinmesini
        gerektiğinde Obifin'den isteriz. Bankaların kendi kayıtları bankaların sorumluluğundadır; {BRAND} bu kayıtları
        değiştiremez veya silemez.
      </p>

      <h2>7. Veri güvenliği</h2>
      <p>
        Sunucularımızda iletim TLS 1.2 ve üzeri ile şifrelenir. Sunucu veritabanının uzak yedekleri OpenPGP (AES-256)
        ile, masaüstü uygulamasının sunucuya yüklediği yedekler AES-256-GCM ile şifrelenir; sunucunun kendi diskindeki
        kısa süreli yedek kopya da şifrelenir. Banka hareketlerinde gönderen hesabın IBAN'ı ayrı bir alanda yalnızca
        kısaltılmış hâli ve anahtarlı bir tanıma kodu (HMAC-SHA256) olarak tutulur (gönderenin açıklamaya yazdığı
        IBAN'lar için Bölüm 1.4); bu kodun anahtarı sunucu yedeklerine alınmaz, ayrı saklanır. Banka yönetim sayfalarının görüntülenmesi ve banka eşleştirmesindeki elle eşleme ve eşlemeyi kaldırma
        işlemleri denetim kaydına yazılır. JWT token'ları güvenli bir secret ile imzalanır. Bir veri ihlali olursa,
        {' '}{BRAND}'in veri sorumlusu olduğu veride KVKK m. 12/5 ve Kurul kararları uyarınca Kurul'a ve ilgili kişilere biz
        bildiririz; yayıncı adına işlediğimiz verileri etkileyen bir ihlali ilgili yayıncıya gecikmeksizin bildiririz ve
        yayıncının Kurul'a ve ilgili kişilere yapacağı bildirimde ona yardımcı oluruz. Yine de internet üzerinden veri
        iletiminin %100 güvenli olduğu garanti edilemez; siz de hesap şifrenizi gizli tutmakla yükümlüsünüz.
      </p>

      <h2>8. Çocuklar</h2>
      <p>
        {BRAND} 18 yaş altı kullanıcılar için tasarlanmamıştır. Bilerek 18 yaş altı bir
        kişiden veri toplamayız.
      </p>

      <h2>9. Politika değişiklikleri</h2>
      <p>
        Bu politikayı güncellediğimizde, üst kısımdaki &quot;Yürürlük tarihi&quot; güncellenir ve değişikliğin kısa özeti sayfanın
        başında gösterilir. Önemli değişikliklerde yayıncılara e-posta ile bilgi veririz. Müşteri uygulaması kullanıcılarına
        önemli değişiklikleri, veri sorumlusu yayıncı olan veriler için yayıncı adına da, uygulamada bir kez gösterilen bir
        duyuruyla bildiririz. Kayıt formunu yeniden dolduran müşterilere formda güncel bilgilendirme gösterilir ve
        okunduğunun onayı yeniden istenir.
      </p>

      <h2>10. İletişim</h2>
      <p>
        Gizlilik ile ilgili her tür soru için:{' '}
        <a href={`mailto:${CONTACT_EMAIL}`}>{CONTACT_EMAIL}</a>
      </p>
    </LegalLayout>
  );
}
