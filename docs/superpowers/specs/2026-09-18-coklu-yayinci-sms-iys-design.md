# Çok Yayıncılı SMS + İYS Altyapısı — Tasarım

**Tarih:** 2026-09-18
**Durum:** Onaylandı (Burak), uygulama planı bekliyor
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

**Risk düşük:** prod'da `balances=0 tx=0 campaigns=0` — kredi sistemi hiç
kullanılmamış. Göç değil, temiz silme. Panel reposunda kredi referansı yok.

### 1.5 Bakiye artık Netgsm'de

Panelde **salt-okunur** gösterilir. Netgsm bakiyeyi **TL** döndürüyor, "kaç SMS"
değil; maliyet segment sayısına ve operatöre göre değişiyor. Bu yüzden kesin bir
"N mesaj hakkın kaldı" sayısı **gösterilmez** — TL + "yaklaşık" damgalı tahmin.

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
`paused`, dokunulmamış alıcılar `pending` kalır. Diğer kodlarda alıcı `failed`,
döngü devam eder.

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

### 5.2 Çok yayıncılı shopper

`ShopperBroadcasterLink` bir shopper'ı birden fazla lisansa bağlıyor, ama
profildeki `SmsConsent` tek boolean (`ShopperMeController.cs:152`).

- **Geri çekme:** kutu kaldırılınca **bağlı TÜM markalara RET** gider. Aşırı
  geniş, ama güvenli yönde aşırı; gitmezse kişi izni geri çeker ve gönderim
  yasal olarak sürer.
- **Onay verme:** profil kutusundan **yapılmaz**. Onay yalnız toplandığı yerde
  verilir (kayıt formu, o yayıncıya kayıt) — kişi hangi markaya izin verdiğini
  bilerek verir.

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
2. **30. günde bizden tamamen silinir** (`NetgsmAccount` + `IysConsent`
   satırları). Netgsm hesabına **dokunulmaz**.
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

7 ve 10 için emsal: `IysAddResult`'ta `Accepted` alanının olmadığını yansımayla
kilitleyen mevcut test.

**Silinecek:** kredi sistemine ait testler — var olmayan bir davranışı koruyorlar.

`NullIysClient` sahte ONAY döndürmemeye devam eder.

---

## 9. Doğrulanmamış varsayımlar (plan aşamasında kapatılacak)

1. **Netgsm bakiye sorgusu REST v2 yolu.** Ucun var olduğu dokümanda geçiyor,
   tam yol doğrulanmadı. Panel bakiye göstergesi buna bağlı.
2. **Onaylı başlık listesi için salt-okunur uç.** Var olduğu **doğrulanmadı**.
   Yoksa başlık ilk gerçek gönderimde doğrulanır ve hesap `failed`'a düşer —
   bedeli bir kampanyanın patlaması, veri kaybı değil. `/iys/search` kapısı
   bundan bağımsız çalışır.

Her ikisi de `/iys/search` zorunlu kapısını **etkilemez**.

---

## 10. Geçiş

- **Boru hattını açmak ayrı ve GERİ ALINAMAZ bir adımdır** (`Netgsm__BrandCode`
  = `731734`). Çok-tenant işini beklemez; bugünkü kod çalışıyor ve `bekleyen=0`
  olduğu için **şimdi en güvenli an**. Açmadan önce bekleyen sayısı yeniden
  doğrulanmalıdır — değer yazılır yazılmaz hepsi ilk turda gider.
- **EMAR tenant #1 olarak tohumlanır**: env'deki değerler `NetgsmAccount`
  satırına taşınır. Bu, çok-tenant sürümü çıktıktan sonra yapılır.
