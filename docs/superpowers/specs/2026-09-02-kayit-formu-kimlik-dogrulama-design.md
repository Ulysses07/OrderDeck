# Kayıt formunda kimliği yazdırmayı bırakma — tasarım

**Tarih:** 2026-09-02
**Durum:** onaylandı, uygulama planı bekliyor

## Sorun

Kayıt formuna (`/musteri-kayit/{slug}`) girilen platform kullanıcı adları yanlış
giriliyor ve **kayıt sessizce ölü doğuyor**: panelde sapasağlam görünen kayıt,
yayın sohbetindeki kişiyle hiçbir zaman eşleşmiyor.

Biçim doğrulaması zaten var. `HandleValidator` boşluğu, `@`'i, URL'i, Türkçe
harfi, uzunluğu ve platform kurallarını yakalıyor; JS tarafı aynı kuralları
aynalıyor. Bunlar "Musa Sevinç" tipi girdiyi eledi.

Kalan arıza bambaşka: **biçimi doğru ama kişi yanlış.** `aysegulyilmaz` her
regex'ten geçer, gerçek hesabı `ayse.gul_98` olsa bile. Hiçbir biçim kuralı
bunu yakalayamaz, çünkü doğrulanması gereken şey "bu handle var mı" değil,
"bu handle **sen** misin".

En kötü hâli YouTube'da: kişi `test1234` yerine `test` yazdığında, `test`
gerçekten var olan (başkasının) kanalı olduğu için doğrulama **hata vermez,
yeşil onay verir** ve o yabancının `channelId`'sini gizli alana yazıp WPF'e
gönderir (`IntakeForm.cshtml:377`). Kayıt yanlış kişiye *sert* bağlanır —
hiç kontrol olmamasından beter.

## Kapsam

Kimliği kullanıcıya **yazdırmayı bırakmak**. İki faz:

**Faz 1 (hemen):** dört alanda da elle girdi kalır, ama
- YouTube / Instagram / TikTok'ta **profil adresi yapıştırılabilir**, adresten
  handle çıkarılır (bugün `HandleValidator` URL'i reddediyor)
- YouTube'da bulunan kanal için **zorunlu onay**, bulunamayan kanal için
  **gönderim engeli**

**Faz 2 (Google doğrulaması onaylanınca):** YouTube için "Google ile giriş",
Facebook için "Facebook ile giriş". Onay geldiğinde bu iki platformda elle
girdi kaldırılır.

**Bilerek kapsam dışı:**
- **Instagram girişi** — Meta kişisel hesapların API erişimini tamamen kapattı
  (Basic Display Aralık 2024'te sonlandı; yerine gelen "Instagram API with
  Instagram Login" yalnız İşletme/Creator hesaplarını kabul ediyor). Müşteri
  kitlesi kişisel hesap kullanıyor, buton çoğunda ilk adımda patlar.
- **TikTok girişi** — Login Kit `display_name` (görünen ad) döndürüyor, biz
  TikTok'ta `@username` ile eşleştiriyoruz. Giriş yanlış alanı verir. Ayrıca
  app review istiyor.
- **Facebook için URL yapıştırma** — Facebook profil adresi kullanıcı adı
  verir, biz Facebook'ta **görünen adla** eşleştiriyoruz. Linkten çıkan değer
  işe yaramaz.
- **Sohbete doğrulama kodu yazdırma** — değerlendirildi, kullanıcı reddetti
  (müşteriye fazla sürtünme; ayrıca yalnız yayın açıkken çalışır).
- **Geriye dönük düzeltme** — yalnız ölçüm var (aşağıda), düzeltme yok.

## Doğrulanmış dış kurallar

2026-09-02'de doğrulandı. Uygulamaya başlamadan tazelenmeli.

**Google `youtube.readonly` hassas kapsamdır.** Uygulama doğrulaması şart:
gizlilik politikası, uygulama ana sayfası, gerekçe metni, demo videosu.
Doğrulanana kadar **100 kullanıcı** sınırı var ve kullanıcılar "doğrulanmamış
uygulama" uyarısını görür. Müşteri formunda 100 sınırı hiçbir şey demek — bu
yüzden giriş butonu **onay gelmeden yayına açılmaz** (101. müşteride sessizce
patlardı, tam da kurtulmaya çalıştığımız hata sınıfı).

**Facebook `public_profile`** ek review istemiyor; app zaten onaylı
(`3939617702835404`) ve sunucuda kod→token takası mevcut
(`FacebookOAuthController` + `FacebookOAuthExchanger`).

## Mevcut kodun ilgili yerleri

- Form: `OrderDeck.LicenseServer/Pages/Public/IntakeForm.cshtml` (+ `.cshtml.cs`)
- Biçim kuralları: `OrderDeck.LicenseServer/Services/IntakeForm/HandleValidator.cs`
- YouTube varlık kontrolü: `OrderDeck.LicenseServer/Controllers/YouTubeVerifyController.cs`
  (API key ile `channels.list?forHandle`, 1 kota birimi, 1 saat cache, IP rate limit)
- Facebook OAuth sunucu ayağı: `OrderDeck.LicenseServer/Controllers/Facebook/FacebookOAuthController.cs`
  — `[Authorize(Bearer-Customer)]`, masaüstü için; **böyle kalacak**
- Kimlik eşleşmesi: `OrderDeck.Core/Storage/Repositories/CustomerRepository.cs`
  `FindExistingForIntake` (satır 601)

**Neden `channelId` doğru anahtar:** sohbetten gelen YouTube satırları
`Username = channelId` ile açılıyor. Intake `channelId` verdiğinde satır
`Username = channelId`, `DisplayName = @handle` olarak yazılıyor ve eşleşme
birebir oluyor. `channelId` yoksa satır `@handle` ile açılıyor ve ancak
`FindExistingForIntake`'in ikinci dalıyla (DisplayName üzerinden) köprülenmeye
çalışılıyor. Facebook'ta eşleşme `(facebook, Username)` üzerinden harf
duyarsız — yani girişten gelen görünen ad doğrudan kullanılabilir.

**Şema değişmiyor.** Google `channelId` + `@handle` veriyor → mevcut
`YouTubeChannelId` + `YouTubeUsername`. Facebook `name` veriyor → mevcut
`FacebookUsername`.

---

## Faz 1 — URL yapıştırma ve zorunlu onay

### `ProfileUrlParser` (yeni)

`Services/IntakeForm/ProfileUrlParser.cs` — saf, HTTP'siz, tek işi profil
adresinden handle çıkarmak. `HandleValidator` doğrulama işine odaklı kalıyor.

Akış: `ham girdi → ProfileUrlParser → HandleValidator.Normalize → Validate`.
JS aynı kuralları aynalıyor (bugünkü desen: sunucu yetkili, istemci ayna).

**Kabul edilenler:**

| Platform | Girdi | Sonuç |
|---|---|---|
| YouTube | `youtube.com/@handle` | handle |
| YouTube | `youtube.com/channel/UC...` | **doğrudan channelId** — API'ye gidilmez |
| Instagram | `instagram.com/kullanici` (`?igsh=...` atılır) | handle |
| TikTok | `tiktok.com/@kullanici` (`/video/...` kırpılır) | handle |

`http`/`https`/şemasız ve `www.`/`m.` önekleri kabul edilir.

**Reddedilenler** — her biri ne yapılması gerektiğini söyleyen mesajla:

| Girdi | Neden |
|---|---|
| `youtube.com/c/...`, `youtube.com/user/...` | eski biçim, API'den çözülemiyor → "@ ile başlayan adresi kullan" |
| `youtu.be/...` | video adresi, kanal değil |
| `instagram.com/p/...`, `/reel/...`, `/stories/...` | gönderi adresi, profil değil |
| `vm.tiktok.com/...`, `vt.tiktok.com/...` | kısa link; çözmek dışarıya HTTP isteği gerektirir — herkese açık formdan dışarı istek attırmıyoruz → "linki tarayıcıda aç, adres çubuğundakini yapıştır" |
| Başka platformun adresi | yanlış kutu |

Adres tanınmazsa mevcut "yalnız kullanıcı adını yaz" mesajı korunur.

### YouTube zorunlu onayı

| Doğrulama sonucu | Davranış |
|---|---|
| Kanal bulundu | Kanal adı + avatar gösterilir; **"Bu benim kanalım" kutusu işaretlenmeden form gönderilemez** |
| Kanal bulunamadı | **Gönderim engellenir** (bugün sessizce geçiyor) |
| API erişilemiyor (`available:false`) | Geçilir, engelleme yok — kota/ağ arızası müşteriyi kilitlemesin |

**Değişmez kural:** sunucu, istemcinin POST ettiği `YouTubeChannelId`'ye
**güvenmez**. POST sırasında handle'ı kendisi yeniden çözer ve **kendi
bulduğu** channelId'yi kaydeder. Aksi hâlde onay kutusu süsten ibaret olurdu:
JS'i atlayan bir istek her şeyi geçerdi. Sonuç 1 saat cache'li, ek kota
maliyeti ihmal edilebilir.

Kullanıcı `youtube.com/channel/UC...` yapıştırdıysa channelId doğrudan
adresten geldiği için varlık kontrolüne gerek yoktur; bu durumda onay kutusu
da aranmaz.

Instagram ve TikTok'ta varlık kontrolü mümkün olmadığı için onay kutusu yok;
oradaki kazanç tamamen URL yapıştırmadan geliyor.

---

## Faz 2 — Google ve Facebook girişi

Yaklaşım: **tam yönlendirme** (pop-up değil). Kimlik bölümü formun en üstünde
olduğu için yönlendirme anında kaybedilecek veri yok; yazılmış bir şey varsa
kısa ömürlü taslak çerezi korur. Pop-up mobil tarayıcılarda engelleniyor ve
müşteri kitlesi telefondan geliyor.

### Akış (iki platform için aynı iskelet)

1. Kimlik bölümünde buton: *"YouTube kanalını bağla"* / *"Facebook hesabını bağla"*.
2. `GET /musteri-kayit/{slug}/baglan/{platform}` → sunucu tek kullanımlık
   `state` üretir (10 dk ömür, çereze bağlı, slug `state` içinde taşınır),
   sağlayıcıya yönlendirir.
3. Sabit tek dönüş adresi: `GET /musteri-kayit/baglanti-donusu`. Slug `state`
   içinden okunur — sağlayıcı konsoluna her yayıncı için ayrı dönüş adresi
   kaydedilemez.
4. Sunucu `state`'i doğrular → kodu token'a çevirir (secret sunucuda kalır) →
   kimliği okur → **token'ı atar, saklamaz**.
   - Google: `channels.list?mine=true&part=id,snippet` → `channelId`, kanal
     adı, `customUrl` (@handle). Kapsam `youtube.readonly`.
   - Facebook: `/me?fields=id,name` → görünen ad. Kapsam `public_profile`.
     Mevcut `FacebookOAuthExchanger` yeniden kullanılır.
5. Kimlik **sunucu tarafında** çereze bağlı kısa ömürlü bir kayıtta tutulur.
   Forma dönülür, alan salt-okunur görünür: `✓ Kanal Adı (@handle)` +
   "bağlantıyı kaldır".
6. POST'ta sunucu kimliği **kendi kaydından** okur. İstemciden gelen değer
   hiçbir koşulda kullanılmaz.

**Giriş ile elle girdi çakışırsa giriş kazanır.** Faz 2'nin ilk PR'ında iki
yol bir arada duruyor; bir platform için bağlantı kurulmuşsa o platformun elle
yazma kutusu kilitlenir ve POST'ta içeriği yok sayılır. Aksi hâlde aynı
platform için iki farklı kimlik iddiası olur ve hangisinin kazandığı örtük
kalırdı.

### Kötüye kullanım koruması

Mevcut `FacebookOAuthController` `[Authorize(Bearer-Customer)]` ve öyle
kalacak; kendi dokümanı sebebini yazmış: aksi hâlde uç herkese açık ücretsiz
bir OAuth vekiline dönüşür. Yeni **anonim** uçlarda aynı risk şöyle kapatılır:

- Başlatma ucu **geçerli ve aktif bir slug** ister; bilinmeyen slug → 404
- IP başına rate limit (mevcut `youtube-verify` / `facebook-oauth`
  politikalarının yanına yeni politika)
- `state` tek kullanımlık + çereze bağlı → CSRF kapalı
- Dönüş adresi sabit ve sağlayıcı konsolunda kayıtlı

### Kenar durumlar

| Durum | Davranış |
|---|---|
| Google hesabına bağlı YouTube kanalı yok | "Bu hesapta kanal yok, başka hesapla dene" |
| Kişi izni reddetti | Forma hatasız dönüş; elle girdi (henüz duruyorsa) açık kalır |
| `state` süresi doldu / tekrar kullanıldı | "Tekrar dene" — sessiz başarısızlık yok |
| Sağlayıcı hatası | Açık hata mesajı, form kilitlenmez |

### Onay gelince kaldırılacaklar (Faz 2'nin ikinci PR'ı)

- YouTube ve Facebook elle yazma kutuları
- Bu iki platformun `HandleValidator` kuralları
- `ProfileUrlParser`'ın YouTube dalı
- Faz 1'de eklenen "Bu benim kanalım" onay kutusu — girişte doğrulanacak bir
  şey kalmıyor
- Çağıranı kalmayan `YouTubeVerifyController`

### Kod dışı çıktılar

- Google doğrulama başvurusu: gizlilik politikası, uygulama ana sayfası,
  kapsam gerekçesi, demo videosu
- Google ve Meta artık veri işleyen konumunda → formun aydınlatma metni ve
  gizlilik politikası güncellenmeli (KVKK)
- Buton yanında ne aldığımızı yazan tek cümle. Google onay ekranı "YouTube
  hesabınızı görüntüleyin" diyecek; açıklanmazsa dönüşümü ciddi düşürür.

---

## Ölçüm (kod yayınlanmadan)

Eşleşme durumu yayıncı PC'sindeki yerel `Customer` tablosunda; sunucuda değil.
Oraya UI eklemek anlamsız — sahadaki WPF master'ın çok gerisinde, yazılanı
görmez.

Bunun yerine **bir kerelik salt-okunur sorgu** tariflenir ve yerel
veritabanında elle çalıştırılır:

- Intake'ten doğmuş ama sohbette hiç görülmemiş satırlar:
  `LastSeenAt == FirstSeenAt`, `TotalLabelsPrinted = 0`, `TotalAmount = 0`
- YouTube özel durumu: `@handle` ile açılmış ama hiçbir `channelId` satırıyla
  gruplanmamış kayıtlar — bunlar tanım gereği ölü
- Çıktı: platform bazında sayı ve toplam içindeki oran

Rakam, geriye dönük düzeltmenin yapılmaya değip değmediğine karar vermek için.
Düzeltmenin kendisi bu spec'in kapsamında değil.

## Hata davranışının tek kuralı

**Sessiz başarısızlık yok.** Bugünkü asıl arıza yanlış veri değil, yanlış
verinin *doğru görünmesi*. Her başarısızlık ya kişiye ne yapacağını söyler ya
da açıkça engeller. Tek istisna, bilinçli olarak, YouTube doğrulama API'sinin
erişilemez olduğu durum: orada engellemek arızayı müşteriye fatura ederdi.

## Testler

- `ProfileUrlParser` saf birim testi — kabul ve ret biçimlerinin tamamı
  `[Theory]` ile
- **Değişmez kural testi:** POST'a uydurma bir `YouTubeChannelId`
  gönderildiğinde sunucunun onu yok sayıp kendi çözdüğünü kaydettiği
  kanıtlanır. Bu test kaybolursa onay kutusu süse döner
- "Kanal bulunamadı → gönderim engellenir" ve "`available:false` → geçilir"
  ayrı testler
- OAuth: uyuşmayan/tekrar kullanılmış `state` reddi, bilinmeyen slug → 404;
  sağlayıcı çağrıları sahte `HttpMessageHandler` ile (WhatsApp tarafındaki
  `CapturingHandler` deseni)
- Testlerde sabit kimlik dizesi yazılmaz; `$"{prefix}-{Guid.NewGuid():N}"`
  ile üretilir (repo public)

## Kabul edilen takas

YouTube ve Facebook'ta elle girdi kaldırıldığında, girişe yanaşmayan kişi o
platformu kaydedemez. Kayıt oranında düşüş görülebilir. Bilerek kabul
ediliyor: **yanlış kayıt, eksik kayıttan beterdir**, çünkü yanlış kayıt
sağlam görünüp sessizce ölür.
