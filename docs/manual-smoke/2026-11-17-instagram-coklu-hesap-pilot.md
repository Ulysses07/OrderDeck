# Instagram çoklu hesap: pilot el listesi (PR-A)

Bu liste, iki (en çok dört) Instagram hesabının canlı yorumlarını aynı anda OrderDeck sohbetine akıtan değişikliğin elle yapılan saha denemesidir. Kapsam: ikinci hesabın Ayarlar'dan tarayıcı açmadan eklenmesi ve tek başına kaldırılması, her Instagram yorumunun yanında hangi hesaptan geldiğinin yazması, susan hesabın ana ekranda görünmesi. Değişiklik yeni bir WPF sürümüyle gelir; sunucu, uzantı, panel ve müşteri uygulaması değişmez, sunucu deploy'u yoktur. Bu sürümde hesaplar arası sıra varış sırasıdır: iki hesaptan gelen yorumlar ekrana geldikleri sırayla düşer, aralarında bir saniyeye kadar ters sıra olabilir. Sıralama ayrı bir değişikliktir (PR-B) ve bu pilotun 6. bölümündeki rakamla tasarlanır. Belgeye lisans, müşteri, Sayfa ya da hesap kimliği, e-posta ve token yazılmaz; gereken yerde `<…>` yer tutucusu kullanılır.

## 0. Önce: sunucu bayrağı

Tarayıcıda yapılan her Facebook onayı ("Facebook izinlerini güncelle" ve "Facebook'a Bağlan") sunucuda token takasını çağırır. Takas sırasında "!kayıt" botunu bağlama denemesi koşar: bayrak (`InstagramDmBotEnabled`) kapalıysa hiçbir şey saklanmaz, açıksa Instagram'lı ilk Sayfa bota bağlanır. Bu bugünkü "Facebook'a Bağlan" akışında da böyledir; yeni bir yol açılmıyor. Prod'daki değer bilinmiyor, bu yüzden pilotun ilk adımı budur.

- [ ] Burak aşağıdaki sorguyu prod SQL Server'da çalıştırır (sorgu yalnız okur).
- [ ] Satır dönmezse ya da `InstagramDmBotEnabled` 0 ise: devam.
- [ ] Değer 1 ise: "Facebook izinlerini güncelle"ye (ve "Facebook'a Bağlan"a) BASILMAZ. Bayrağı kapatmanın arayüz yolu bu depoda bulunamadı; kapatma sorgusu ayrıca istenir, bayrak kapandıktan sonra pilota dönülür.

```sql
SELECT c.Email, f.InstagramDmBotEnabled
FROM IntakeFormConfigs AS f
JOIN Customers AS c ON c.Id = f.CustomerId
WHERE c.Email = N'<hesabın e-postası>';
```

## 1. Tek hesap regresyon yayını

Yeni sürüm kurulur, ikinci hesap EKLENMEDEN bir yayın yapılır.

- [ ] Instagram sohbeti bugünkü gibi akar (gecikme ve kaçan yorum bugünkü gibi).
- [ ] Instagram satırlarında hesap etiketi yoktur (tek hesapta etiket basılmaz).
- [ ] Sohbet panelinin başında Instagram sağlık şeridi görünmez.
- [ ] Kenar çubuğunda Instagram noktası yayında yeşil yanar ve yanında sayı yoktur.
- [ ] Günlükte `[InstagramChatHostedService] usage …` satırları birinci hesabın olağan yüzdelerini verir. `call_count`, `total_cputime` ve `total_time` değerleri not edilir: 3. ve 5. bölümün karşılaştırma tabanı budur.
- [ ] Günlükte `[InstagramMergeGate] summary` satırı yoktur (tek hesapta kapı kurulmaz).

Not: `<YYYYMMDD>` tarihli günlük: `~/Documents/OrderDeck/Logs/log-<YYYYMMDD>.txt`. Süzgeç komutu "Günlük süzgeci" bölümündedir.

## 2. Ekleme

İkinci Instagram hesabı ayrı bir Facebook Sayfasına bağlıdır ve o Sayfa, OrderDeck'e bağlı Facebook hesabıyla yönetilir.

- [ ] Ayarlar > Facebook > "Instagram hesabı ekle"ye basılır. Ayarlar açılır açılmaz basılırsa düğme önce ilk izin kontrolünün bitmesini bekler; kısa bir gecikme olağandır. Ekleme sürdükçe (liste okunurken, çekmece açıkken, eklenirken) "Instagram hesabı ekle" ve "Facebook izinlerini güncelle" pasiftir.
- [ ] Açılan listede yeni Sayfa ve ona bağlı Instagram hesabı görünüyor mu? Not: `<görünüyor / görünmüyor>`.
- [ ] Görünmüyorsa: "Facebook izinlerini güncelle"ye basılır. Açılan Facebook onay ekranında İKİ Sayfa ve İKİ Instagram hesabı da işaretli bırakılır. Onay bitince kartta `Facebook izinleri güncellendi: …` yazar; tarayıcı bekleme metni ve `[İptal]` hemen kalkar, kart arkadan tazelenir (o sırada iki düğme tazeleme bitene dek pasif kalır; ağ yavaşsa bu bir dakikayı bulabilir). Ardından "Instagram hesabı ekle" listesi yeni Sayfayı gösteriyor mu? Not: `<görünüyor / görünmüyor>`.
- [ ] Facebook onay ekranında ne göründüğü not edilir. Meta belgesinin cevaplamadığı iki soru burada açılır:
  - (a) İlk onayda tek Sayfa seçildiyse ya da Sayfa sonradan açıldıysa, saklı giriş yeni Sayfayı görüyor mu (yani ilk "Instagram hesabı ekle" denemesi listeyi gösterdi mi)? Not: `<…>`.
  - (b) Onay ekranında hangi Sayfalar ve hesaplar önceden işaretli geldi; yalnız yeni Sayfa işaretlenseydi eski Sayfanın düşeceğine dair ekranda bir ipucu var mı? Not: `<…>`.
- [ ] Hesap seçilip "Ekle"ye basılır. Kartta `@<hesap> eklendi. Yayın açıksa birkaç saniye içinde okunmaya başlar.` yazar; kartta iki hesap satırı görünür, birincil satırda "Kaldır" yoktur.
- [ ] Göz kontrolü: kart sonucunu (başarı metni dahil) sarı şeritte gösteriyor. Başarı metninin de sarı çıkması operatörü yanıltıyor mu? Not: `<…>`.
- [ ] Bir kez de onay sekmesi kapatılır: "Facebook izinlerini güncelle"ye basılır, açılan Facebook sekmesi onay vermeden kapatılır. OrderDeck sekmenin kapandığını bilemez ve beklemeye devam eder (en çok 5 dakika).
  - [ ] Kartta görünen `[İptal]` düğmesine basılır: `Güncelleme iptal edildi; bağlantı değişmedi.` yazar ve akış bırakılır.
  - [ ] Hemen ardından "Facebook izinlerini güncelle"ye yeniden basılır: yeni tarayıcı akışı açılabiliyor (bekleyen onay var uyarısı çıkmıyor).
- [ ] İzin güncellemesi sürerken bağlantı kaldırılırsa, kaldırılıp yeniden kurulursa ya da başka bir izin güncellemesi önce kaydederse akış hiçbir şey yazmadan şu metinle biter: `Güncelleme başarısız: Onay beklenirken Facebook bağlantısı kaldırıldı. Bağlantı değişmedi.` Bu metin pilotta görülürse ne yapıldığı not edilir; bağlantı geri gelmemiş olmalıdır.
- [ ] Kart `Sayfalar okunamadı`, `yetki alınamadı`, `Hesap eklenemedi`, `Hesap kaldırılamadı` ya da `Güncelleme başarısız` metni gösterirse sebep günlükten okunur ("Günlük süzgeci" bölümü). `Hesap eklenemedi, yeniden dene.` çoğunlukla bağlantı dosyası yazılamadığında çıkar (kilitli dosya, dolu disk); yeniden denemek yeter, Facebook'ta yönetici rolüne bakmak gerekmez.
- [ ] "Kaldır"dan sonra kartın durum satırı tazeleme bitene dek `Kontrol ediliyor...` der, sonra güncel durumu yazar.

## 3. İkinci hesapta kısa deneme yayını

Mezat dışı, 10 ile 15 dakika, ilk gerçek iki hesaplı mezattan ÖNCE yapılır. Gerekçe: ikinci hesap yeni ya da az gösterimliyse Instagram'ın istek sınırı düşük olabilir; bunu ilk kez müşterili bir mezatta öğrenmek pahalıdır.

- [ ] İkinci hesapta yayın açılır. Yorumlar sohbete hesap etiketiyle geliyor mu? Not: `<…>`.
- [ ] Gelmiyorsa sohbet panelinin başındaki şeritte hangi durumun göründüğü aynen not edilir (ör. `bağlantı yenilenmeli`, `canlı yayın bulunamadı`, `yanıt yok (N sn)`, `hesap bilgisi okunamadı`). Bu, ikinci Sayfanın token'ıyla canlı yayın okumanın Meta'da nasıl davrandığını söyler. `hesap bilgisi okunamadı` görülürse sebep günlükteki `[InstagramAccountResolver]` Warning satırından okunur ("Günlük süzgeci" bölümü); HTTP durumu ve Graph kodu not edilir.
- [ ] Yeni hesabın kota yüzdesi ne hızla yükseliyor? Günlükteki `usage` satırlarından birkaç dakika arayla `call_count`, `total_cputime`, `total_time` not edilir (okuma kuralları 5. bölümde).
- [ ] Yüzdeler bu tempoda bir saat dolmadan %100'e varacak gibiyse: gerçek mezattan önce DUR. Aksi hâlde ikinci hesap gerçek mezatta dakikalarca susar. Çözüm o hesabın yoklama aralığını yükseltmektir; bu ayrı, küçük bir değişikliktir ve bu sürümde yoktur.

## 4. İki hesapla gerçek yayın

- [ ] **Sıra:** iki hesaba art arda ve aynı anda aynı kod yazılır; ekrandaki sıra ile gerçek yazılış sırası karşılaştırılır. Günlükten "ters sıra sayısı" okunur: yayın boyunca bütün `[InstagramMergeGate] summary` satırlarındaki `inversions=` değerlerinin TOPLAMI. En büyük fark: `maxGapSeconds=` değerlerinin en büyüğü (saniye). Not: toplam `<…>`, en büyük fark `<…>` sn.
- [ ] **Etiket:** her Instagram satırında doğru hesap yazıyor (`@kullanıcıadı`; kullanıcı adı okunamazsa "@"sız Sayfa adı). Etiketin üstüne gelince ipucunda tam ad ve yorumun Meta saati (Türkiye saatiyle) görünüyor.
  - [ ] Göz kontrolü: pencere daraltılır. Etiketli satırda mesaja kalan genişlik yeterli mi; uzun bir yorum daha çok satıra kırılır. Okunurluk bozuluyorsa not edilir: `<…>`.
- [ ] **Bir hesabın yayını kapatılır:** şeritte o hesap için `yayın kesildi (SS:DD)` yazar, üst bardaki nokta sarıdır. 2 dakika sonra üst nokta normale döner, şerit satırı sarı kalır. Öteki hesabın sohbeti kesintisiz akar.
- [ ] **Bir hesabın yayını hiç açılmaz:** öteki hesap canlıyken 90 saniye sonra şeritte `canlı yayın bulunamadı` yazar (yalnız şeritte sarı; üst noktayı sarartmaz).
- [ ] **Facebook:** canlı yorumlar ve sil/banla birincil Sayfada bugünkü gibi çalışır.

## 5. Kota ölçümü

Günlükteki kota satırının biçimi (hesap başına dakikada en çok bir satır; ilk satır ilk turda yazılır):

```text
[InstagramChatHostedService] usage <hesap> (page <PageId>): business <id> call_count <a>% total_cputime <b>% total_time <c>% regain <m> min
```

- Başlıkta birden çok işletme kovası varsa kovalar aynı satırda `; ` ile ayrılır.
- `regain` erişimin geri gelmesine kalan dakikadır; başlık bu alanı taşımıyorsa da `0` yazılır, yani `regain 0 min` "alan yok" ile "0 dakika"yı ayırt etmez (Meta alanı olağan durumda gönderir).
- Yanıtta başlık yoksa satırın sonu `X-Business-Use-Case-Usage header absent` olur.
- Başlık var ama okunamıyorsa `X-Business-Use-Case-Usage header unreadable` olur.
- Geçici hatalı turda (ağ hatası, 5xx) kota satırı yazılmaz.

İki hesabın `business`, `call_count`, `total_cputime`, `total_time` ve `regain` değerleri okunur. Okuma kuralları:

- [ ] **`call_count` çağrı sayısı değil, izin verilen çağrıların yüzdesidir** ve kayan 1 saatlik pencereyi anlatır. 24 saatlik "4800 × gösterim" formülüyle doğrudan karşılaştırılmaz. `total_cputime` ve `total_time` da yüzdedir; kısıtlama çağrı sayısından değil bu ikisinden de gelebilir, bu yüzden üç değer ayrı ayrı not edilir.
- [ ] **Sayaç ortak mı:** iki hesabın satırlarındaki `business` değeri aynı mı; yalnız bir hesabın yükü artarken (ör. yorum taşması) öteki hesabın yüzdesi de birlikte artıyor mu? İkisi de evetse kova büyük olasılıkla ortaktır. `business` aynı ama yüzdeler bağımsız hareket ediyorsa ortak değildir. Not: `<…>`.
- [ ] **Yeni hesabın sınırı düşük mü:** aynı istek temposunda yeni hesabın yüzdesi eskisinden belirgin hızlı mı yükseliyor? Not: `<…>`.
- [ ] **Başlık her yanıtta geliyor mu:** `header absent` satırları sayılır; hiç yoksa başlık her yanıtta geliyor demektir. Taşma anlarında yüzdenin nasıl değiştiği de not edilir.
- [ ] Göz kontrolü: `header unreadable` satırı görülürse saati ve hesabı not edilir. Muhtemel sebep yanıtın başlığı birden çok değerle göndermesidir; bu bilgi bir sonraki değişikliğin girdisidir.

## 6. Yanıt süresi

Kapı özeti satırının biçimi (yalnız iki ve üzeri Sayfa bağlıyken; olay geldikçe en çok dakikada bir satır):

```text
[InstagramMergeGate] summary: inversions=<n> maxGapSeconds=<sn> lanes: <hesap1> rounds=<n> p50Ms=<ms> p95Ms=<ms>; <hesap2> rounds=<n> p50Ms=<ms> p95Ms=<ms>
```

Sayılar pencere başınadır: her satır son bir dakikayı anlatır; oturum bitince yazılan son satır son pencereden kalan kısmı anlatır. Olay gelmeyen dakikada satır yazılmaz.

- [ ] Hesap başına `p50Ms` ve `p95Ms` değerleri satır satır not edilir.
- [ ] Yayın boyunca hesap başına en yüksek `p95Ms` ayrıca yazılır. Not: `<hesap1>` `<…>` ms, `<hesap2>` `<…>` ms.
- [ ] Sıralama değişikliğinin (PR-B) planladığı 400 ms'lik bekletme bu farkı kapsıyor mu? Bu sürümde bekletme yoktur (sıfır); PR-B'nin varsayılanı bu rakamla kesinleşir.

## 7. Bilgisayar sayısı

Burak yayın için tek bilgisayar kullanıyor (17.11.2026); ikinci hesap yalnız o bilgisayarda eklenir.

- [ ] Yayın sırasında Facebook'a bağlı ve "Yayın Başlat" basılı başka bir bilgisayar var mı? Not: `<…>`. Varsa o bilgisayar bugünkü gibi yalnız birincil hesabı yoklar; ikinci hesabın yükü tek bilgisayarda kalır.
- [ ] İkinci hesap başka bir bilgisayara da EKLENMEZ: eklenirse o hesaba giden istek sayısı bilgisayar sayısıyla katlanır ve az gösterimli hesap hız sınırına girebilir.

## Günlük süzgeci

Günlük dosyası: `~/Documents/OrderDeck/Logs/log-<YYYYMMDD>.txt` (günlük dosya, gün başına bir tane).

```bash
grep -iE "InstagramChatHostedService|InstagramMergeGate|InstagramAccountResolver|Facebook OAuth|Instagram account (listing|add|removal) failed|Facebook permission update failed" ~/Documents/OrderDeck/Logs/log-<YYYYMMDD>.txt
```

Hangi satır hangi önekle gelir:

- `[InstagramChatHostedService]`: hesap döngüsünün satırları (tek hesapta da yazılır).
  - Durum geçişi: `[InstagramChatHostedService] account <hesap> (page <PageId>): <eski durum> -> <yeni durum>`. Yeniden deneme saati varsa sonuna `, retry at SS:DD:ss` eklenir (yerel saat). Kalıcı hatanın geçiş satırı (`… -> Faulted, retry at …`) poller bittikten sonra yazılır.
  - Kota satırı: `[InstagramChatHostedService] usage …` (biçimi 5. bölümde).
  - Diğerleri: `starting poller for IG @<hesap>`, aynı Instagram hesabına çıkan ikinci Sayfa için `page <PageId> skipped: …` (Warning), kalıcı hatada `… retrying in 5 min` (Warning).
- `[InstagramMergeGate]`: kapı özeti (biçimi 6. bölümde); yalnız iki ve üzeri Sayfa bağlıyken.
- `[InstagramAccountResolver]`: Sayfanın Instagram hesabını çözen adım (tek hesapta da yazılır). Şerit `hesap bilgisi okunamadı` derse sebebe bakılacak yer burasıdır.
  - Warning, Graph hata döndü: `[InstagramAccountResolver] <HTTP durumu> for page <PageId>: Graph code <kod>, subcode <alt kod>`. Alt kod yoksa yalnız `Graph code <kod>`; yanıt Graph hatası değilse (ör. HTML sayfası) `no Graph error code` yazar.
  - Warning, istek düştü (ağ, zaman aşımı): `[InstagramAccountResolver] resolve failed for page <PageId>: <hata tipi>`.
  - Information: `page <PageId> → IG @<hesap> (<Instagram kimliği>)` (çözüldü) ve `page <PageId> has no linked Instagram professional account` (Sayfada hesap yok).
  - Ayarlar kartının kendi okuması bu satırları yazmaz; satırlar yayın sırasındaki hesap döngüsünden gelir.
- `Facebook OAuth …`: bağlantı servisinin satırları.
  - `Facebook OAuth connected for Page <PageId> (<Sayfa adı>)` ve `Facebook OAuth disconnected (local tokens cleared)`.
  - `Facebook OAuth extra Page added: <PageId> (<Sayfa adı>)` ve `Facebook OAuth extra Page removed: <PageId> (<Sayfa adı>)`.
  - `Facebook OAuth reauthorized for Page <PageId> (<Sayfa adı>): <n> refreshed, <m> missing [<PageId listesi>]`.
  - `Facebook OAuth Page listing needs reauthorize (Graph code <kod>, subcode <alt kod>)`: Sayfa listesi izin güncellemeye yönlendirdi.
  - Warning: `Facebook OAuth reauthorize kept the stored token for Page <PageId>: a new one could not be derived (<hata tipi>, HTTP <durum>, Graph code <kod>, subcode <alt kod>)`. O Sayfanın eski token'ı yerinde kaldı; HTTP durumu ya da kodlar bilinmiyorsa `null` yazar.
- Öneksiz satırlar (Warning, kaynak `SettingsViewModel`; istisna ekli, token ve Sayfa adı yok): Ayarlar kartındaki hata metni sebebi söylemediğinde sebep buradan okunur.
  - `Instagram account listing failed`: kartta `Sayfalar okunamadı`.
  - `Instagram account add failed for Page <PageId>`: kartta `yetki alınamadı`, `Hesap eklenemedi, yeniden dene.` ya da `Sayfalar okunamadı`.
  - `Instagram account removal failed for Page <PageId>`: kartta `Hesap kaldırılamadı, yeniden dene.`
  - `Facebook permission update failed`: kartta `Güncelleme başarısız: …`.

Günlükte izleyici kullanıcı adı ve yorum metni (Information ve üstünde) ve token yoktur; satırlarda yalnız PageId, Sayfa adı, Instagram hesap kimliği, yayıncının kendi hesap adı, işletme kimliği, durum adları ve sayılar geçer. Günlükten bu belgeye ya da PR'a satır kopyalanırken kimlikler `<…>` ile değiştirilir.

## Sürüm notu

```text
Instagram: aynı anda birden çok Instagram hesabının canlı yorumları okunur (en çok 4). İkinci hesap Ayarlar > Facebook > "Instagram hesabı ekle" ile eklenir; her Instagram yorumunun sağında hangi hesaptan geldiği yazar, kopan hesap sohbet panelinin başında görünür.
Eski sürüme dönülürse: uygulama ilk bağlanan (birincil) Sayfayla bugünkü gibi çalışır; eklenen hesaplar o sürümde okunmaz ve bunun göstergesi yoktur. Eski sürümde "Bağlantıyı Kaldır" ya da "Facebook'a Bağlan" yapılmadıkça yeniden yükseltmede eklenen hesaplar geri gelir; yapıldıysa hesap "Instagram hesabı ekle" ile yeniden eklenir.
```

## Teslim özeti

Yoklama temposu, gecikme ve Instagram'a giden istek yükü tek hesaplı kurulumda aynı kalır. Aşağıdaki yedi davranış ve beş küçük yan fark ise tek hesaplı kurulumda da değişir. Bu tasarımcının kararıdır; Burak'ın itiraz ettiği madde yalnız iki ve üzeri Sayfa bağlıyken çalışacak biçime çekilir. 5. madde bunun dışındadır: ikinci hesap o düğmelerle eklendiği için koşula bağlanamaz.

1. Yayın ortasında "Bağlantıyı Kaldır" ya da hesap kaldırma, o hesabın yoklamasını en geç 2 saniyede durdurur; bugün eski token'la oturum sonuna kadar sürüyor.
2. Kalıcı hata ve hesap çözümleme sonrası 5 dakikalık beklemeler, Sayfa token'ı yenilenince (ör. "Facebook izinlerini güncelle" sonrası) beklenmeden kesilir.
3. Kenar çubuğundaki Instagram noktası yayında yanar; bugün hiç yanmıyor.
4. Hesap o oturumda canlıyken koparsa sohbet panelinin başında sağlık şeridi ve sarı üst nokta görünür.
5. Ayarlar'daki Instagram kartında iki yeni düğme görünür: "Instagram hesabı ekle" ve "Facebook izinlerini güncelle".
6. Instagram izni eksik uyarısı "Bağlantıyı Kaldır + Facebook'a Bağlan" yerine "Facebook izinlerini güncelle"ye yönlendirir.
7. Tarayıcıda Facebook onayı beklemesi ("Facebook'a Bağlan" dahil) 5 dakikada zaman aşımına uğrar; bugün süresizdir.

Küçük yan farklar:

- (a) Ayarlar'daki Instagram kartı bağlan, kaldır ve izin güncelleme sonrasında hemen tazelenir; bugün yalnız Ayarlar açılırken tazeleniyor.
- (b) Oturum bitince 5 dakikalık beklemeler iptal olur ve yeni oturum hesabı hemen yeniden dener; bugün bekleme oturumdan bağımsız sürüyor.
- (c) Ayarlar'da hesabı okuyan çağrı düşerse satır "hesap yok" yerine `Durum okunamadı` der.
- (d) Sayfa token'ı değiştiğinde hesap bir kez yeniden çözülür (Instagram'a tek ek istek).
- (e) "Facebook'a Bağlan"ın üç hata metni değişti: bekleyen bir onay varken ikinci deneme artık `Bağlantı başarısız: Bekleyen bir Facebook onayı var. Tarayıcıdaki onayı tamamla ya da birkaç dakika sonra yeniden dene.` der (eskiden anlaşılmaz bir dinleyici hatası); Facebook'un hata yanıtı metne token'ı gizlenmiş olarak girer; boş yanıt metni artık adres taşımaz.

Plandan sonra eklenen sağlamlaştırmalar (hepsi kodda ve testli):

- Kalıcı hata beklemesi token değişince kesilirse şerit satırındaki yeniden deneme saati "şimdi"ye çekilir; yeni deneme cevap alamazsa `yanıt yok` en geç 45 saniyede görünür (eski saatte beklemez).
- "Facebook izinlerini güncelle" sürerken bağlantı kaldırılır, kaldırılıp yeniden kurulur ya da başka bir izin güncellemesi önce kaydederse akış hiçbir şey yazmaz ve `Güncelleme başarısız: Onay beklenirken Facebook bağlantısı kaldırıldı. Bağlantı değişmedi.` metniyle biter; kaldırılmış bağlantı dirilmez.
- Ayarlar açılır açılmaz "Instagram hesabı ekle"ye basılırsa komut ilk izin kontrolünün bitmesini bekler. Ekleme sürerken "Instagram hesabı ekle" ve "Facebook izinlerini güncelle" pasiftir: iki akış aynı anda yürümez.
- Eklemede bağlantı dosyası yazılamazsa (kilitli dosya, dolu disk) kart `Hesap eklenemedi, yeniden dene.` der ("yetki alınamadı" değil: Facebook'ta yönetici rolüne bakmak gerekmez, yeniden denemek yeter). Yanıt okunurken kopan bağlantı da aynı metne düşer. Facebook'tan HTML gibi okunamayan bir yanıt gelirse (ör. otel / kafe ağının giriş sayfası) `Sayfalar okunamadı, internetini kontrol edip yeniden dene.` çıkar.
- Ayarlar kartının durum satırı: `Sayfalar okunuyor...` ve tarayıcı bekleme metni geçicidir; kalkınca satır o anki gerçek duruma döner, arada biten bir kontrol bekleme talimatını silmez. "Facebook izinlerini güncelle" bitince bekleme metni ve `[İptal]` hemen kalkar, kart arkadan tazelenir. "Kaldır"dan sonra satır tazeleme bitene dek `Kontrol ediliyor...` der.
- Aynı Instagram hesabına iki Sayfa bağlıysa ve hesabı okuyan Sayfa yayın sırasında kaldırılırsa öteki Sayfa hesabı 5 dakika beklemeden, birkaç saniyede devralır.
- Şerit `hesap bilgisi okunamadı` derse sebep artık günlüğe Warning olarak düşer (`[InstagramAccountResolver]`); çözümleyici ve poller Facebook'un ham yanıtını günlüğe yazmaz.
- Ayarlar kartındaki hata metni sebebi söylemediğinde sebep günlüğe Warning olarak düşer ("Günlük süzgeci" bölümü); izin güncellemede token'ı türetilemeyen Sayfa ve izin yönlendirmesine dönen Sayfa listesi de günlüğe yazılır.

Tekrar süzgeci: süzgece dokunulmadı. Aynı izleyici aynı metni 2 saniye içinde iki hesaba da yazarsa sohbete tek satır düşer (tek yayında aynı satırı iki kez yazana bugün uygulanan kural); etiket o satırda öteki hesabı gösterebilir. İki satır da görünsün istenirse süzgecin anahtarına hesap eklenir; bu ayrı ve küçük bir iştir.

Yayın sırasında izin güncelleme: "Facebook izinlerini güncelle" yayın sırasında Instagram yorumlarında ve Facebook yorumlarında kısa bir kesinti yapabilir. Meta eski Sayfa token'ını düşürürse sağlam Instagram hesabı bir kez hatayla çıkar ve yeni token'la hemen yeniden kurulur (birkaç saniyelik yorum kaybolabilir); Facebook canlı yorum akışı da kopup yeniden bağlanır. Düğmenin ipucu bu bedeli söyler ve yayın sırasında yalnız kopan bir hesabı düzeltmek için basılmasını ister.

En ağır sonuç: yayın ortasında "Bağlantıyı Kaldır" + "Facebook'a Bağlan" yapan operatör bugün Instagram sohbetini kaybetmezken yeni sürümde tarayıcı onayı boyunca kaybeder ve yeni bağlantının ilk çekimi yutulur (aradaki yorumlar gelmez); Facebook yorumları ise akmaya devam eder.
