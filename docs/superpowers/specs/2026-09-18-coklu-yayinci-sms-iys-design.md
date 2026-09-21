# Çok Yayıncılı SMS + İYS Altyapısı — Tasarım

**Tarih:** 2026-09-18
**Durum:** Taslak — Astra denetimi işlendi (2026-09-18), yeniden onay bekliyor
**Önceki spec:** `2026-09-18-iys-onay-boru-hatti-design.md` (tek-tenant boru hattı, master'da)

---

## Amaç

SMS/İYS altyapısını tek-tenant'tan çok-tenant'a taşımak: her yayıncı **kendi
Netgsm hesabından, kendi onaylı başlığından, kendi İYS marka koduyla** ticari
mesaj gönderir. Kredi sistemi emekliye ayrılır.

## Neden Model A (her yayıncı kendi hesabı)

Onay **marka bazlıdır** — 2026-09-17'de ölçüldü: aynı numara `731734`'te ONAY,
`763208`'de RET. Bir yayıncının topladığı onay, başka bir markanın altında
hukuken yok hükmündedir. Merkezi bir hesaptan herkes adına göndermek, onayı
toplayan ile gönderen kişiyi ayırır ve 6563 karşısında savunulamaz.

İkincil fayda: yayıncı ayrılırsa listesini yanında götürür — onay zaten onun
markası altında İYS'de durmaktadır.

## Kapsam dışı (bilerek)

- **Shopper uygulamasında marka bazlı onay listesi** — takip işi, ayrı repo
  (OrderDeck-Shopper). Bu spec'te profil kutusu yalnız geri çekme yapar.
- **Backfill yok.** 2026-09-17'de kaybedilen 284 onay için yeniden deneme yok.
- **Resmî tatiller modellenmiyor** (yalnız Cmt/Paz) — son tarih bir hedef değil,
  sessiz kalmayı yasaklayan alarm eşiği.
- **Netgsm bakiye ve başlık listesi uçları** — bkz. "Doğrulanmamış varsayımlar".

---

## 1. Mimari

### 1.1 İki gönderim yolu, mevcut enum

`SmsKind` zaten mesaj başına taşınıyor ve **her çağrı yeri şimdiden doğru
değeri geçiyor**. Hesap seçimi buna bağlanır:

| `SmsKind` | hesap | çağrı yerleri |
|---|---|---|
| `Transactional` | **merkezi** — bugünkü `NetgsmOptions` aynen kalır | `ShopperAuthController.cs:382`, `:470`, `PanelSupportRequestsController.cs:120` |
| `Commercial` | **yayıncının** — `NetgsmAccount`'tan çözülür | `SmsCampaignSendJob` |

OTP çağrı yerlerine **dokunulmaz**.

`ORDERDECK` gönderici başlığı henüz yok; merkezi OTP mevcut başlıktan çıkmaya
devam eder. Marka tescili SMS başlığı için zorunlu değil (Netgsm 10 belgeden
birini kabul ediyor), ama başlık "firma adıyla bağlantılı" olmak zorunda —
`EMAR GLOBAL LTD` ile `ORDERDECK` bağını kuran en temiz belge tescil.

### 1.2 Arayüz bölünür

`ISmsSender` bugünkü imzasıyla kalır (tenant bilmez, bilmesine gerek yok).
Kampanya için ayrı `ITenantSmsSender` gelir; kimlikleri parametre alır,
`HttpClient`'ı paylaşır.

**Neden ayrı arayüz:** tek arayüze kimlik parametresi eklemek, "yanlış marka
altında ticari SMS" hatasını çalışma-anı kontrolüne indirger. Ayrı arayüz onu
**yapısal olarak imkânsız** kılar. Ek güvence: merkezi gönderici `Commercial`
alırsa fırlatır.

### 1.3 Yeni tablo: `NetgsmAccount`

`WhatsAppAccount` desenini birebir taklit eder.

| alan | not |
|---|---|
| `Id` | |
| `LicenseId` | tenant anahtarı |
| `UserCode` | Netgsm abone no (gizli değil) |
| `PasswordProtected` | `IDataProtector` — **asla düz metin dönmez** |
| `Header` | onaylı gönderici başlığı |
| `BrandCode` | İYS marka kodu |
| `Status` | `verified` \| `failed` \| `disabled` |
| `LastError` | panelde uyarı metni |
| `LastVerifiedAt` | son başarılı doğrulama |
| `CreatedAt`, `UpdatedAt` | |

Satırın **yokluğu** "hiç girilmemiş" demektir; ayrı `pending` durumu yok.
`verifying` de yok — geçici durum kalıcı sütun hak etmiyor.

### 1.4 Silinenler

`LicenseSmsBalance`, `LicenseSmsTransaction`, `LicenseSmsBalanceService`,
`AdminSmsController`, `LicensesSmsBalanceController`, `SmsCampaign.ReservedCredits`,
`SmsCampaign.RefundedCredits` ve bağlı uçlar/testler.

**Veri riski düşük:** prod'da `balances=0 tx=0 campaigns=0` — kredi sistemi hiç
kullanılmamış. Göç değil, temiz silme. Panel reposunda kredi referansı yok.

**Ama silme "sadece kredi"yi silmiyor — iki taşıyıcı davranış kredi servisinin
içinde yaşıyor:**

**(a) Kampanya kaydı kredi çağrısının içinde.** Kampanyayı ve alıcı satırlarını
diske yazan şey `ApplyAndSaveAsync`'in kendisi
(`LicensesSmsCampaignsController.cs:158` → `LicenseSmsBalanceService.cs:98`);
koddaki yorum bunu açıkça söylüyor: *"kampanya + alıcı satırları da aynı
SaveChanges içinde yazılır (atomik)"*. İş sonundaki sonuç kaydı da iade
çağrısına asılı (`SmsCampaignSendJob.cs:155`). Kredi servisi düşünmeden
silinirse **kampanya hiç kaydedilmeden kuyruğa girer.** Silme sırasında
korunacaklar: enqueue öncesi atomik kampanya/alıcı yazımı, mevcut idempotency
yakalaması, iş sonundaki sonuç yazımı.

**(b) WPF istemcisi krediye bağlı — ve saha sürümleri geride kalır.**
`BulkSmsViewModel.cs:131` geçmişi yüklemeden **önce** `/sms/balance`'ı
bekliyor; uç kalkarsa geçmiş listesi de ölür. Daha kötüsü
`CanSend() => PreviewDone && Sufficient && ...` (`:182`): gönder düğmesi
`Sufficient` alanına bağlı — alan dolmazsa düğme **kalıcı olarak kapalı** kalır.

Bu, panelden farklı bir sınıf problem: panel web, anında güncellenir; WPF
**Velopack ile dağıtılıyor ve sahada eski sürümler kalır.** Sunucu ucu
kaldırıldığı anda güncellemeyi almamış her yayıncının toplu SMS ekranı bozulur.
Kapsama alınacaklar: WPF ViewModel, ortak DTO/API istemcisi
(`SmsCampaignDtos.cs`), ve eski istemcilerin geçiş davranışı — uçlar bir sürüm
boyunca **uyumluluk için sabit değer döndürerek** yaşatılır mı, yoksa silme
zorunlu WPF sürümüne mi bağlanır; plan aşamasında karara bağlanacak.

**Karar (Plan 3, 2026-09-21):** uçlar bir sürüm boyunca sabit değerle
yaşatılıyor — `/sms/balance` stub (0), `Sufficient` = kurulum doğrulanmış,
`CreditsRefunded` = 0. Kaldırma koşulu: saha WPF sürümleri kredisiz istemciye
geçtiğinde.

### 1.5 Bakiye artık Netgsm'de

Panelde **salt-okunur** gösterilir.

Bakiyenin "kaç SMS" karşılığı **doğrudan okunamaz**: maliyet segment sayısına ve
operatöre göre değişir. Bu yüzden panelde kesin bir "N mesaj hakkın kaldı"
sayısı **gösterilmez** — ham değer + "yaklaşık" damgalı tahmin.

Daha önce bu bölümde "Netgsm yalnız TL döndürür" yazıyordu; **yanlıştı.**
Dokümana göre `stip` parametresi yanıtı değiştiriyor: `stip=1/3` paket/SMS
adedi, `stip=2` kredi bilgisi döndürebiliyor. Hangi `stip` değerinin kullanılacağı
ve yanıtın tam biçimi §9'da açık madde.

---

## 2. Kurulum yaşam döngüsü

### 2.1 Durum → yetki tablosu

| durum | SMS kampanyası | kayıt formunda onay kutusu |
|---|---|---|
| *(satır yok)* | kapalı | **görünmez** |
| `failed` | kapalı | **görünmez** |
| `verified` | açık | görünür |
| `disabled` | kapalı | **görünmez** |

**Onay toplama `verified`'a fail-closed bağlıdır.** Gerekçe §5.1'de.

Kurulumu bitmemiş yayıncıya panelde açık uyarı gösterilir:

> "Netgsm kurulumun tamamlanana kadar SMS onayı toplanmıyor. Bu sürede gelen
> kayıtların telefonu alınır ama ticari mesaj izni istenmez."

Kutuyu gösterip onayı çöpe atmak izleyiciye yalan söylemektir.

### 2.2 Giriş ve doğrulama

Yayıncı **kendi panelinden** dört alan girer: abone no, API şifresi, gönderici
başlığı, İYS marka kodu. Kaydetme anında doğrulama **senkron** koşar.

**Zorunlu kapı — `/iys/search`:** tek çağrıda abone no + şifre + marka kodunun
üçünü birden doğrular, çünkü üçü de gövdede gidiyor ve marka kodu hesaba ait
değilse İYS reddediyor. `NetgsmIysClient.SearchAsync` zaten var.

**Test SMS'i ATILMAZ** — yayıncının parasını harcar, bir alıcı numarası ister
ve İYS'ye dokunur.

### 2.3 Yeniden doğrulama

Tek seferlik doğrulama yetmez: abonelik biter, şifre döner, başlık iptal olur.
Günlük bir Hangfire işi her `verified` hesapta `/iys/search` kontrolünü
tekrarlar. Düşerse hesap `failed` → kampanya kapısı **ve onay toplama** durur.
Marka kodu geçersizken toplanan onay zaten geçersiz olurdu.

### 2.4 Şifre saklama

`IDataProtector`, `WhatsAppAccountService` deseni. Panel yalnız
"girildi/girilmedi" gösterir. Anahtar halkası kaybolursa `Unprotect` null döner
→ hesap `disabled` + *"Netgsm kimliklerini tekrar gir"*. Sessiz bozulma yok.

### 2.5 Kill switch

Admin panelinden `disabled`. Süren kampanya `paused`, kalan alıcılar `pending`.
Bakiye tükenmesiyle **aynı duraklama yolu** — iki sebep, tek mekanizma.

---

## 3. Gönderim akışı

### 3.1 Kitle

İki kaynağın birleşimi: bu lisansa bağlı shopper'lar + bu lisansın kayıt
formundan gelen onaylı kayıtlar. (Bugün yalnız `Shoppers`, pratikte 1 kişi.)

**Kitle "kime göndermek istiyoruz", kapı "kime gönderebiliriz".** Kitleyi
genişletmek kapıyı gevşetmez; yoksa kapı dekoratif olur.

### 3.2 Alıcı döngüsü

Kampanya başlarken **hesap kontrolü**: `NetgsmAccount` yoksa veya `verified`
değilse kampanya hiç başlamaz — bu bir kampanya hatası, alıcı hatası değil.

Alıcı başına:

1. **İYS kapısı** — `IysConsentGate.CanSend` = `Status == Onay &&
   LastVerifiedStatus == Onay`. Geçmezse alıcı **`skipped`**, sebep
   `iys-consent-missing` / `iys-consent-not-onay`.
2. **Gönderim** — yayıncının kimlikleriyle, yayıncının başlığından.

### 3.3 `skipped` neden `failed`'dan ayrı

Bugün kapı `failed` yazıyor ve gerekçesi **kredi iade yoluydu**
(`SmsCampaignSendJob.cs:158-171` iadeyi `failed` sayısından hesaplıyor).
Kredi silinince o gerekçe ölüyor.

Ayırmazsak yayıncı raporda "47 başarısız" görüp altyapı arızası sanar — gerçekte
47'si onaysız, yani sistem **doğru** çalışmıştır. `skipped` oranı ayrıca kötüye
kullanımın tek erken göstergesidir.

### 3.4 Bakiye tükenmesi

`NetgsmSmsSender.cs:73` Netgsm'in hata **kodunu** exception'a koymuyor, yalnız
HTTP durumunu; kampanya job'ı her exception'ı `failed` yazıyor. **Bugünkü kodla
"bakiye bitti → duraklat" imkânsız.**

Gereken: `NetgsmSmsException(code)`. Bakiye kodunda döngü kırılır, kampanya
`paused`, dokunulmamış alıcılar `pending` kalır.

**Ama "diğer tüm kodlarda alıcı `failed`, döngü devam eder" demek kabul
edilemez** — ilk taslakta öyle yazıyordu, §7 ile de çelişiyordu. Hata üç sınıf,
ikisi değil:

| sınıf | örnek kod | sonuç |
|---|---|---|
| **bakiye** | yetersiz kredi | kampanya `paused`, kalan alıcılar `pending` |
| **hesap** | şifre geçersiz, başlık reddedildi, abonelik kapalı | kampanya `paused` + hesap `failed`; kalan alıcılar **`pending` korunur** |
| **alıcı** | numara geçersiz/kara liste | o alıcı `failed`, döngü devam eder |

Hesap sınıfını alıcı sınıfına katmak gerçek bir veri kaybı üretir:
`SmsCampaignSendJob.cs:134` bugün **her** exception'ı terminal alıcı hatasına
çeviriyor ve yeniden koşuda yalnız `pending` seçiliyor (`:98`). Doğrulamadan
sonra dönen bir şifre, böylece tek turda **tüm kitleyi** `failed` yazıp tüketir;
şifre düzeltilince geri gelecek kimse kalmaz. Hesap hatası kitleyi harcamamalı.

**Bakiye hatası senkron olmayabilir.** `NetgsmSmsSender.cs:97` yalnız başarı
kodunu okuyor, dönen `jobid`'yi saklamıyor; `SmsCampaignSendJob.cs:129` hemen
`sent` yazıyor. Netgsm dokümanı bakiye yetersizliğini rapor tarafında da
tanımlıyor (`/sms/rest/v2/stats` → `notEnoughCredit`, SMS raporunda
`status=14`). Yani **senkron exception tek başına bu yolu yakalamaya
yetmeyebilir**; gönderim "başarılı" görünüp mesaj hiç gitmeyebilir. Bu, bakiye
hatasının *daima* asenkron olduğu anlamına gelmiyor — hangi kodun hangi aşamada
döndüğü §9'da açık madde. Karara bağlanacak: `jobid` saklanacak mı ve
raporlanan sonucu izleyen bir takip adımı olacak mı.

**Karar (Plan 3):** `jobid` `SmsCampaignRecipient.ProviderJobId`'de saklanıyor;
rapor-takip adımı kurulmadı (§9.3 doğrulanmadan kurulmayacak). Bilinmeyen
temiz-ret kodu varsayılanı: kampanya `paused` (kitle korunur).

`SmsCampaignRecoveryJob` iki düzeltme ister:
- `paused` kampanyayı **devralmaz** (bayat `ClaimedAt` görüp diriltirse bakiye
  hâlâ yokken tekrar tekrar çarpar)
- devam ettirilen eski kampanyayı `pending && CreatedAt < 2dk` kuralıyla her
  turda yeniden kuyruklamaz

---

## 4. Çok-markalı İYS işleri

`IysConsentPushJob.cs:111-118` yapılandırma hatasını bilerek rethrow ediyor —
tek markada **doğru** karar ("bekleyenleri sırayla harcama"). Çok markada aynı
satır felakete dönüşür: bir yayıncının bozuk şifresi diğer herkesin push'unu
keser ve onların 3 iş günü penceresi **sessizce** dolar.

Push ve verify işleri marka başına döner, her marka kendi try/catch'i içinde.
Bir marka düşerse yalnız o marka `failed` + `LastError`; diğerleri devam eder.
"O markayı durdur" anlamı korunur, "herkesi durdur" anlamı kalkar.

**Marka döngüsü tek başına YETMİYOR.** İki şey daha gerekiyor:

### 4.1 İstemci kiracı bağlamı taşımıyor

`IIysClient.AddAsync/SearchAsync` (`IIysClient.cs:47-53`) ne hesap ne marka
parametresi alıyor; `NetgsmIysClient.cs:110` her isteğe **global** kimlikleri
gövdeye koyuyor. Bu istemciyle marka başına dönmek sorunu çözmez, gizler:
B markası için dönülen tur isteği yine **merkezî marka** altında sorar, dönen
sonuç `IysConsentVerifyJob.cs:86` üzerinden **B'nin satırına** yazılır. Yani
B'nin onay durumu, hiç sorulmamış bir markanın cevabıyla güncellenir.

Gereken: Add/Search çağrılarının **değişmez bir hesap bağlamı** alması ya da
hesaba bağlı bir istemci fabrikası. İstek markası ile yazılan satırın markası
eşleşmeli ve bu **testle kilitlenmeli**. `Program.cs:183` içindeki merkezî
sağlayıcıya bağlı DI seçimi ve global marka kapıları (`RunAsync` başındaki
`BrandCode` boşsa dön kontrolleri) de bu dönüşüme dahil.

### 4.2 Doğrulama sınırı markadan önce uygulanıyor

`IysConsentVerifyJob.cs:50` bütün markaların kayıtlarını tek sırada toplayıp
**önce `Take(BatchSize * 5)`** ile kesiyor. Sorgu hata alınca `continue`
ediliyor ve randevu (`NextVerifyAt`) **değişmiyor** (`:66-76`). Sonuç: A
markasının en eski 100 kaydı sürekli hata veriyorsa her turda yine onlar
seçilir, B'nin kaydı seçim kümesine **hiç girmez** — sonradan eklenen marka
bazlı catch'e ulaşamaz bile. Klasik hat başı tıkanması.

Sınır **marka başına** uygulanmalı. Test iki kayıtla yetinmemeli: *"A'da 100
eski hatalı kayıt, B'de bir hazır kayıt → B doğrulanır"* durumu kapsanmalı.

---

## 5. Onay toplama

### 5.1 Markasız satır AÇILMAZ

`IysConsent` unique index'i `(BrandCode, ChannelType, RecipientType, Recipient)`
(`LicenseDbContext.cs:534`). `BrandCode=""` yazılırsa **kurulumu bitmemiş tüm
yayıncıların aynı numaraya ait onayı tek satıra çakışır** — B yayıncısının
RET'i A yayıncısının ONAY'ını ezer, hata çıkmaz.

`RecordAsync` artık `licenseId` alır ve markayı ondan çözer. Marka
çözülemezse satır açmaz, yalnız `IysConsentEvent` + `ErrorCode="no-brand"`
yazar — bozuk telefon için var olan desenin aynısı.

### 5.1b Olay şeması da kiracı taşımalı

`RecordAsync`'e `licenseId` eklemek yetmiyor: **kanıtın kendisi kiracısız.**
`IysConsentEvent` (`IysConsentEvent.cs:25-45`) yalnız `Recipient` taşıyor —
`BrandCode` yok, `LicenseId` yok, `IysConsentId` yok. API olaylarında kaynak
kimliği de yazılmıyor (`IysConsentPushJob.cs:165`, `IysConsentVerifyJob.cs:91`).

Sonuç: aynı telefonun A markasındaki ONAY'ı ile B markasındaki RET'i **aynı
telefon numarasına asılı iki olay** olarak durur; denetimde hangisinin hangi
yayıncıya ait olduğu güvenilir biçimde ayrıştırılamaz. Oysa bu tablonun tek
varlık sebebi ispat: İYS `consentDate` ve `source` alanlarını bize boş
döndürüyor, "bu kişi izni ne zaman, nereden verdi" sorusunun tek cevabı burası.

Olay şemasına `BrandCode` + `LicenseId` eklenir; API olaylarına kaynak satır
kimliği yazılır.

### 5.2 Çok yayıncılı shopper

`ShopperBroadcasterLink` bir shopper'ı birden fazla lisansa bağlıyor, ama
profildeki `SmsConsent` tek boolean (`ShopperMeController.cs:152`).

- **Geri çekme:** kutu kaldırılınca **bağlı TÜM markalara RET** gider. Aşırı
  geniş, ama güvenli yönde aşırı; gitmezse kişi izni geri çeker ve gönderim
  yasal olarak sürer.
- **Onay verme:** profil kutusundan **yapılmaz**. Onay yalnız toplandığı yerde
  verilir (kayıt formu, o yayıncıya kayıt) — kişi hangi markaya izin verdiğini
  bilerek verir.

**Geri çekme global boolean'a bağlanamaz.** `ShopperMeController.cs:134` olayı
yalnız istek değeri `Shopper.SmsConsent`'ten **farklıysa** üretiyor. Kaçak şu:
kişi bir kez geri çeker (`SmsConsent=false`), sonra B yayıncısının kayıt
formundan yeniden onay verir — `IntakeFormService.cs:154` bu onayı kaydeder ama
shopper boolean'ını `true`'ya çekmez. Artık kişi profilden açıkça
`PATCH {smsConsent:false}` gönderse bile değer zaten `false` olduğu için
**hiçbir RET üretilmez**; B'nin markasındaki ONAY yerinde durur ve gönderim
yasal olarak sürer. Tam da önlemek istediğimiz şey.

Geri çekmenin işlenmesi boolean'ın *değişmesine* değil, **marka bazlı mevcut
duruma** bağlanmalı: bağlı markalardan herhangi birinde ONAY varsa geri çekme
isteği o markalara RET yazar. Idempotency de oradan gelir.

### 5.2b Mevcut shopper ikinci yayıncıya kaydolurken onay kayboluyor

`ShopperAuthController.cs:142` numarayı bulunca mevcut shopper'ı yeniden
kullanıyor; `RecordAsync` ise **yalnız yeni shopper oluşturulan dalda**
(`:181`). Yani zaten kayıtlı biri ikinci bir yayıncıya kaydolup onay kutusunu
işaretlerse bağlantı kurulur ama **onay hiç işlenmez** — o yayıncı, izni olan
bir kişiye hiç SMS gönderemez.

Onay yazımı shopper *oluşturma* işleminden ayrılıp **o yayıncıya kayıt olma**
işlemine bağlanmalı. Çok-marka modelinde onayın doğal yeri zaten budur.

Bunun görünür bir sonucu var: Shopper uygulamasındaki kutu bugün iki yönde de
çalışıyor. Sunucu "açma" yönünü yok sayarsa kullanıcı kutuyu işaretler, kaydeder
ve hiçbir şey olmaz — sessiz başarısızlık. Sunucu tarafı bu isteği açıkça
reddetmeli (kutu geri çekme amaçlıdır), Shopper uygulaması da kutuyu buna göre
sunmalıdır. **Shopper tarafı bu spec'in kapsamında değildir; takip işidir ve o
değişiklik yayına girene kadar kullanıcıya yanlış bir arayüz gösterilir.**

### 5.3 6563 asimetrisi korunur

Kayıt formu + müşteri kaydı yalnız `true`'da onay yazar (işaretsiz kutu
sessizliktir, geri çekme değil). Profil PATCH'i geri çekme yönünde yazar.

---

## 6. Yayıncı ayrılışı

1. Ayrılış anında veri **dışa aktarılıp yayıncıya teslim edilir** — müşteri
   listesi onun, kopyası onda olmalı.
2. **30. günde bizden silinir** (`NetgsmAccount` + `IysConsent` satırları).
   Netgsm hesabına **dokunulmaz**.

   **"Tamamen" demek bu hâliyle doğru değil.** `IysConsentEvent` bilerek FK'siz
   ve ekle-only ("kayıt satırı silinse bile olay kalır" —
   `IysConsentEvent.cs:29`). Account + Consent silinince olaylarda **telefon
   numarası, ispat IP'si, ham API yanıtları** kalır. Bu, ispat yükü için
   bilinçli bir tasarımdı; ama silme vaadiyle çelişiyor ve KVKK tarafında
   savunulması gereken şey artık "sildik" değil.

   Karara bağlanacak (plan aşamasında): ayrılan yayıncının olayları da mı
   silinecek, yoksa telefon/IP anonimleştirilip olay iskeleti mi kalacak?
   İkisinin de bedeli var — silmek 6563 ispatını yok eder, tutmak "tamamen
   sildik" diyememek demektir. Aydınlatma metni hangisi seçilirse ona uymalı.
3. **30 günden sonra dönerse:** onay kaybolmamıştır — İYS yetkili kayıttır ve
   onay onun markası altında yaşamaya devam eder. Yayıncı kendi listesiyle
   döner, `/iys/search` ile onaylar tazelenir.

`SearchAsync` **numara listesi ister**; İYS "bu markanın tüm onayları" diye liste
vermez. Dışa aktarım bu yüzden isteğe bağlı bir nezaket değil, dönüş yolunun
taşıyıcı adımıdır.

**Aynalanan satırlar işaretlenir:** `SourceCode = "IYS_MIRROR"`. Yeniden
kurmada hiçbir şey beyan etmiyoruz, İYS'yi aynalıyoruz. İşaretlemezsek denetim
izi yalan söyler: hiç yapmadığımız bir bildirimi yapmışız gibi görünür.

**Aynalanan satır push kuyruğuna GİRMEMELİDİR.** `PushState` bekleyen bir
değerle yazılırsa push job onu alır ve İYS'de zaten kayıtlı olan onayı yeniden
bildirir — `consentDate` bugüne kayar, gerçek onay tarihi kaybolur ve denetimde
"onayı biz bugün aldık" gibi görünür. Aynalanan satırlar terminal bir
`PushState` ile açılır.

**KVKK:** 30 günlük saklama süresi aydınlatma metnine yazılmalıdır.

---

## 7. Hata yönetimi

| katman | örnek | sonuç | görünürlük |
|---|---|---|---|
| **Hesap** | şifre döndü, marka kodu geçersiz | `failed` + `LastError`; kampanya **ve** onay toplama durur | yayıncı panelinde kalıcı uyarı |
| **Kampanya** | bakiye bitti, kill switch | `paused`, alıcılar `pending` | yayıncı panelinde "devam et" |
| **Alıcı** | onay yok / Netgsm reddetti | `skipped` / `failed` + sebep | kampanya raporu |

Amaç: **düzeltilebilir bir şeyi kalıcı yara gibi göstermemek.**

Telefon her log satırında `PiiMasker` ile maskeli kalır.

**Admin görünürlüğü:** `failed` hesaplar, yayıncı bazlı `skipped` oranı, kill
switch.

---

## 8. Test stratejisi

**EF InMemory unique index uygulamıyor** — §5.1 çakışması InMemory'de
**görünmez**, test yeşil yanar ve prod'da veri ezilir. O testler
`SqlServerContainerFixture` ile gerçek SQL Server ister (Docker açık olmalı).

Kilitlenecek sözleşmeler:

1. Doğrulanmamış hesapta onay satırı açılmaz, yalnız `no-brand` olayı *(TC)*
2. İki lisans + aynı telefon + farklı marka → iki ayrı satır, unique ihlali yok *(TC)*
3. Marka A yapılandırma hatası atarsa marka B yine push edilir
4. Bakiye kodu → kampanya `paused`, dokunulmamış alıcılar `pending`
5. Kapı elerse alıcı `skipped`, `failed` **değil**
6. `paused` kampanyayı recovery job devralmaz
7. Merkezi gönderici `Commercial` alırsa fırlatır
8. Geri çekme bağlı **tüm** markalara RET gider
9. `verified → failed` hem kampanyayı hem onay toplamayı durdurur
10. `Unprotect` null → hesap `disabled`, sessiz gönderim yok
11. **İstek markası = yazılan satırın markası.** B için yapılan `Search`, B'nin
    kimlikleriyle gider; merkezî markanın cevabı B'nin satırına yazılamaz (§4.1)
12. **Hat başı tıkanması yok:** A'da 100 eski hatalı kayıt varken B'nin tek
    hazır kaydı yine doğrulanır (§4.2)
13. **Hesap hatası kitleyi harcamaz:** gönderim ortasında şifre hatası →
    kampanya `paused`, dokunulmamış alıcılar `pending` kalır (§3.4)
14. **Olay kiracı taşır:** aynı telefon A'da ONAY + B'de RET → olaylar
    `BrandCode`/`LicenseId` ile ayrıştırılabilir *(TC)* (§5.1b)
15. **Geri çekme boolean'a bağlı değil:** shopper `SmsConsent=false` iken B'nin
    formundan onay vermişse, profilden gelen geri çekme yine RET üretir (§5.2)
16. **Mevcut shopper ikinci yayıncıya kaydolurken onayı işlenir** (§5.2b)
17. **Kampanya kaydı krediden bağımsız atomik:** kredi servisi yokken de
    kampanya + alıcılar enqueue'dan önce tek `SaveChanges` ile yazılır (§1.4a)

7 ve 10 için emsal: `IysAddResult`'ta `Accepted` alanının olmadığını yansımayla
kilitleyen mevcut test.

**Silinecek:** kredi sistemine ait testler — var olmayan bir davranışı koruyorlar.

`NullIysClient` sahte ONAY döndürmemeye devam eder.

---

## 9. Doğrulanmamış varsayımlar (plan aşamasında kapatılacak)

Aşağıdaki üç madde Netgsm'in resmî dokümanına karşı kontrol edildi
(<https://www.netgsm.com.tr/dokuman/>). **Hiçbiri canlı hesap çağrısıyla
sınanmadı** — doğrulama plan aşamasında gerçek çağrıyla yapılacak.

1. **Bakiye sorgusu.** İlk taslakta "REST v2 yolu doğrulanmadı" yazıyordu;
   dokümanda görünen yol `POST /balance` ve **REST v2 altında değil**. Ayrıca
   yanıt `stip` parametresine göre değişiyor (`stip=1/3` paket/SMS adedi,
   `stip=2` kredi bilgisi). Hangi `stip` ile çağrılacağı ve yanıtın panelde
   nasıl sunulacağı açık (§1.5).
2. **Onaylı başlık listesi.** `GET /sms/rest/v2/msgheader` **mevcut** — ilk
   taslaktaki "var olduğu doğrulanmadı" notu düzeltildi. Ancak listenin
   başlığın **onay durumunu** garanti ettiği doğrulanmadı; liste başlığı
   içeriyor diye başlık gönderime hazır sayılmamalı.
3. **Bakiye hatasının kodu ve aşaması.** Dokümanda `/sms/rest/v2/stats` →
   `notEnoughCredit` ve SMS raporunda `status=14` tanımlı. Bakiye yetersizliği
   gönderim anında senkron mu döner, rapor aşamasında mı görünür, yoksa
   ikisi birden mi — belirsiz. §3.4'teki duraklatma mekanizması buna bağlı.

Üçü de `/iys/search` zorunlu kapısını **etkilemez** (§2.2).

---

## 10. Geçiş

- **Boru hattını açmak ayrı ve GERİ ALINAMAZ bir adımdır** (`Netgsm__BrandCode`
  = `731734`).

  Bu spec'in ilk hâli *"çok-tenant işini beklemez, `bekleyen=0` olduğu için
  şimdi en güvenli an"* diyordu. **Bu yanlıştı ve tavsiye geri çekilmiştir.**

  `bekleyen=0` yalnız **açılış anını** korur, sonrasını değil. Bugünkü kodda
  `IntakeFormService.cs:154` **hangi yayıncıdan gelirse gelsin** onayı
  collector'a veriyor, `IysConsentCollector.cs:109` global markaya yazıyor ve
  `IysConsentPushJob.cs:81` bekleyenleri **marka filtresi olmadan** seçiyor.
  Yani marka açıldıktan sonra B yayıncısının kayıt formundan gelen her onay
  merkezî markaya — hukuken EMAR'ın markasına — bildirilir. Geri alınamaz:
  İYS'ye giden kayıt geri çağrılamaz ve yanlış marka altında yazılmış onay
  hem geçersiz hem de temizlenmesi gereken bir kirlilik olur.

  **Açmanın ön koşulu, ikisinden biri:**
  1. Kiracı izolasyonu (§4.1 + §5.1) önce yayına girer, **veya**
  2. Boru hattı geçici olarak **merkezî markanın sahibi olan tek lisansla**
     sınırlanır: collector yalnız o `LicenseId` için satır açar, diğerleri
     `no-brand` olayına düşer.

  Bugün fiilen tek yayıncı olması bu koşulu karşılamaz — koşul kodda
  uygulanmalı, sahadaki mevcut duruma güvenilmemeli. İkinci yayıncı ilk
  kaydını aldığı anda kimse "acaba boru hattı açık mıydı" diye düşünmeyecek.

  Ön koşul sağlandıktan sonra, açmadan hemen önce bekleyen sayısı yeniden
  doğrulanmalıdır — değer yazılır yazılmaz hepsi ilk turda gider.
- **EMAR tenant #1 olarak tohumlanır**: env'deki değerler `NetgsmAccount`
  satırına taşınır. Bu, çok-tenant sürümü çıktıktan sonra yapılır.
