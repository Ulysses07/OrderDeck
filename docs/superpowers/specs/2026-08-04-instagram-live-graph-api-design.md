# Instagram Live yorumları — resmi Graph API entegrasyonu

**Tarih:** 2026-08-04
**Durum:** Tasarım onaylandı, uygulama planı bekliyor
**Kapsam:** Instagram Live yorumlarını Chrome extension DOM scraper'ı yerine
resmi Instagram Graph API ile okumak. Read-only (moderasyon yok).

## Neden

Bugün Instagram canlı yorumları Chrome extension'ın DOM scraper'ı ile
çekiliyor. Bu yol:

- **IG ToS'a aykırı.** Facebook tarafını resmi Graph API'ye taşıdık
  (App Review onaylı, 2026-08-04); IG hâlâ scraping.
- **Kırılgan.** IG web arayüzü DOM'u değiştikçe selector'lar bozuluyor;
  uzun oturumlarda yorum render'ı donuyor (2026-07 logları), tek çare
  sayfa yenileme.
- **Tarayıcı sekmesi zorunlu.** Operatörün yayın boyunca IG sekmesini açık
  ve görünür tutması gerekiyor.

Facebook'un resmi API'ye geçişi çalıştığı için aynı deseni IG'ye taşıyoruz.

## Kabul edilen ödünler

Bunlar bilinçli tercih, eksiklik değil:

1. **Moderasyon yok.** Meta canlı video yorumlarında hide ve delete'i
   desteklemiyor (*"Comments on live video IG Media are not supported"* —
   IG Comment reference). Ban/engelleme karşılığı da yok. **Kayıp değil:**
   mevcut scraper'da da moderasyon yok.
2. **Yayın sonu ~1 saniyelik kayıp.** Yayın bitince yorumlar yapısal olarak
   erişilemez hale geliyor (aşağıda "Yayın sonu draini" bölümü). Son polling
   aralığındaki yorumlar kaybolur.
3. **Yanıtlar (replies) düz muamele görüyor.** `parent_id` yok sayılıyor.
   Canlı yayında iş parçacığı olup olmadığı doğrulanamadı; muhtemelen şema
   tekrarı. Bilerek kaçırıyoruz.
4. **Webhook yerine polling.** Meta resmen webhook öneriyor
   (*"We strongly recommend using webhooks to prevent rate limiting"*) ama
   webhook `pages_manage_metadata` izni + public hesap şartı + **var olmayan
   bir sunucu→masaüstü kanalı** gerektiriyor. Kota polling için sorun
   olmadığından (aşağıda) ve forumlarda 2025-2026'da IG webhook olaylarının
   sessizce kaybolduğu raporlandığından, polling ile başlıyoruz. Webhook
   gelecekteki bir seçenek olarak açık kalıyor.

## API varyantı seçimi

**"Instagram API with Facebook Login"** kullanılıyor. Bu bir tercih değil,
zorunluluk: `live_media` ucu **sadece** bu varyantta var. "Instagram Login"
varyantında polling ucu yok, yalnızca webhook.

Varyant sağlıklı: sunset tarihi yok, en son Haziran 2026'da yeni özellik
aldı. `instagram_basic` / `instagram_manage_comments` izinleri emekli
edilmedi — Ocak 2025'teki yeniden adlandırma diğer aileyi
(`instagram_business_*`) etkiledi.

**İzleme notu (risk):** `live_media` Meta'nın yeni birleşik `/reference/`
doküman ağacında görünmüyor, yalnızca eski `/instagram-graph-api/` ağacında.
Kaldırıldığına dair kanıt yok, ama sessiz deprecation ihtimaline karşı takip
edilmeli.

## İzinler

Hepsi **Advanced Access** gerektiriyor:

| İzin | Ne için |
|---|---|
| `instagram_basic` | IG business hesabını çözmek |
| `instagram_manage_comments` | Yorumları **ve `username` alanını** okumak |
| `pages_show_list` | Sayfa listesi |
| `pages_read_engagement` | Sayfa ↔ IG hesabı bağı |
| `ads_read` | **Koşullu** — aşağıya bakınız |

**Kritik:** 27 Ağustos 2024'ten beri profesyonel hesap yorumlarında
`username` alanını okumak `instagram_manage_comments` istiyor.
`instagram_basic` tek başına yetmez — yorumu yazanın adı olmadan chat
anlamsız.

### `ads_read` koşulu (açık risk)

Hem `live_media` hem `{ig-media-id}/comments` referans sayfaları şu koşullu
maddeyi taşıyor:

> *"If the app user was granted a role via the Business Manager on the Page
> connected to the targeted IG User, you will also need one of:
> `ads_management`, `ads_read`."*

Bu madde **güncel** (yeni birleşik `/instagram-platform/reference/` ağacında
da var, eski sayfalarda kalmış bir artık değil) ve yalnızca "Facebook Login"
varyantı için geçerli.

**Bizi neden ilgilendiriyor:** kullanıcılarımızın hepsi Instagram reklamı
veriyor, yani Sayfa ve IG hesapları Business Manager içinde. Rollerinin BM
üzerinden verilmiş olması çok muhtemel → madde tetiklenir.

İki hafifletici unsur:
- **`ads_read` yeterli**, `ads_management` gerekmiyor. Meta her yerde "one of"
  diyor. `ads_read` salt-okunur ve çok daha hafif bir izin.
- `GET /{page-id}?fields=instagram_business_account` çağrısı bu maddeden
  **etkilenmiyor** — yani hesap çözümlemesi temiz, sorun çıkarsa yorum
  okumada çıkar.

**Belirsiz kalan:** "BM üzerinden rol verilmiş" tam olarak neyi kapsıyor?
Klasik Sayfa yöneticisi ama Sayfa bir Business Portfolio'ya bağlıysa madde
tetikleniyor mu — Meta hiçbir yerde tanımlamıyor. Forumlarda pratikte
gerekip gerekmediğine dair kullanılabilir rapor bulunamadı.

**Çözüm: ölçümle kapat.** Pilotta gerçek bir satıcının BM yönetimindeki
Sayfası ile `ads_read` OLMADAN `live_media` + `comments` çağrısı yapılır.
Çalışıyorsa izin listesine hiç eklenmez; 200/10 hatası dönerse `ads_read`
App Review'a dahil edilir. Bu tek test tüm soruyu çözer ve App Review
başvurusundan **önce** yapılmalı.

`pages_manage_metadata` **gerekmiyor** (sadece webhook için). Hesabın public
olması şartı da **sadece webhook** için. Yani polling yolu, App Review'da
reddedilen izne hiç dokunmuyor.

**App Review notu — ÖNCEKİ İDDİA GERİ ÇEKİLDİ.** Bu dokümanın ilk halinde
"dışarıdan test edilemeyen uygulamalar için 'private app' istisnası var"
yazıyordu. **Doğrulanamadı.** Meta'nın güncel App Review dokümantasyonunda
böyle bir muafiyet bulunamadı; ekran kaydı sayfasında da istisna yok. Aksine:
*"Your app must be publicly available or you must provide instructions on how
to access it."*

Dolayısıyla normal süreci planlıyoruz: **her izni fiilen kullanan arayüzü
gösteren ekran kaydı**. Bu, `pages_manage_metadata` reddinden çıkan dersle
birebir aynı (o izin "kullanan arayüz göremedik" gerekçesiyle reddedilmişti) —
IG yorumlarının uygulamada aktığı gerçek bir yayın kaydı çekilecek.

### İzinlerin kodda olmadığı tuzağı

`FacebookOAuthService` yetkilendirme URL'inde `scope=` göndermiyor;
Facebook Login for Business **`config_id`** kullanıyor ve izin setini Meta
o yapılandırmadan okuyor. Yani IG izinlerini eklemek **kod değişikliği değil,
Meta panelinde yapılandırma değişikliği.**

**Sonuç:** mevcut kullanıcıların token'ları yeni izni kendiliğinden kazanmaz.
Herkesin yeniden bağlanması gerekiyor. `GET /{user-id}/permissions` ile
kontrol edilip uygulamada uyarı gösterilecek.

### Panelde yapıldı — ampirik bulgular (2026-08-05)

Mevcut Facebook Login for Business yapılandırmasına IG izinleri eklendi
(`instagram_basic` + `instagram_manage_comments`; nihai set: `business_management`,
`instagram_basic`, `instagram_manage_comments`, `pages_manage_engagement`,
`pages_read_engagement`, `pages_read_user_content`, `pages_show_list`).
Ayrı yapılandırma **açılmadı** — Meta'nın kendi "Instagram API with Facebook
Login" kurulum dokümanı IG ve Pages izinlerini tek akışta topluyor, ayrıca
App Review izin+app düzeyinde işlediği için ikinci yapılandırma hiçbir avantaj
sağlamıyor.

Meta'nın hiçbir yerde belgelemediği, panelde ölçülerek doğrulanan iki şey:

| Soru | Sonuç |
|---|---|
| Yayında olan yapılandırmayı düzenleyince `config_id` değişiyor mu? | **Hayır.** `1699947754625407` aynı kaldı → kod/ayar değişikliği gerekmedi. |
| "Users will be required to give this app every permission you select" — IG hesabı bağlı olmayan Sayfa'da login kırılıyor mu? | **Hayır.** İzin ekranında IG izinleri görünüyor, seçilecek IG hesabı çıkmıyor, akış normal tamamlanıyor. FB-only yayıncılar etkilenmiyor. |

**Hâlâ açık:** IG hesabı Sayfa'ya bağlandıktan sonra `GET /me/permissions`
çıktısında IG izinleri `granted` mı — bakılmadı.

## Mimari

Facebook sınıflarının aynası, moderasyon hariç:

- **`InstagramAccountResolver`** — `GET /{page-id}?fields=instagram_business_account{id,username}`
  ile bağlı Sayfa'dan IG business hesabını çözer, cache'ler.
- **`InstagramLiveMediaPoller`** — `GET /{ig-user-id}/live_media?fields=id,media_type,media_product_type,owner,username,comments.limit(50)`
  ile aktif yayını ve yorumlarını **tek çağrıda** alır. Yayın yoksa boş döner.
- **`InstagramChatHostedService`** — polling döngüsü, watermark yönetimi,
  hata sınıflandırma; mesajları mevcut chat hattına basar.

**Ayrı Instagram OAuth yok.** Mevcut Facebook Sayfa bağlantısına biniyor.
Yeni ayar alanı, token deposu veya handle girişi eklenmiyor.

### Mevcut iskele

`ac3c0d7` (2026-07-20) commit'i zemini hazırlamış:
- `OrderDeck.Core/Chat/InstagramIngestMode.cs` — `Scraper` (0) / `OfficialApi` (1)
- `AppSettings.InstagramIngestMode` — varsayılan `Scraper`

Bayrak şu an hiçbir yerde okunmuyor. Bu iş onu okuyan tarafı ekliyor.

## Veri akışı

### Watermark (yeni yorum tespiti)

Comments ucunda **timestamp filtresi yok** (*"Comments cannot be filtered by
timestamp"*), sayfa başına **en fazla 50** yorum, sıralama **ters kronolojik**
(en yeni önce, v3.2+).

"Son görülen id'ye kadar yürü" yaklaşımı kırılgan: yayıncı o yorumu IG
uygulamasından silerse id listeden kaybolur, algoritma eşleşme bulamaz ve
50 yorumun hepsini yeni sanıp tekrar basar.

Bunun yerine **timestamp watermark**:

- `lastTimestamp` = en son işlenen yorumun **Meta** zaman damgası
- `seenIds` = o saniyeye ait id kümesi (aynı saniyede birden çok yorum için)
- Yeni sayılma kuralı:
  `timestamp > lastTimestamp` **veya**
  (`timestamp == lastTimestamp` **ve** `id ∉ seenIds`)

Karşılaştırma **daima Meta zamanı ile Meta zamanı** arasında. Yerel saat hiç
karışmaz — kullanıcının sistem saati yanlışsa etkilenmemeliyiz.

Watermark **media id'ye anahtarlanır**: yayıncı yeni yayın açınca temiz
başlar.

### Döngü

1. `live_media` çağrısı (saniyede 1).
2. Dönen `comments` listesini watermark kuralıyla filtrele.
3. Yeni olanları kronolojik sıraya çevir (`timestamp` birincil, `id`
   ikincil — deterministik olsun; aynı saniyedeki gerçek sıra bilinemez ama
   tutarlı olmalı), chat'e bas.
4. Watermark'ı güncelle.

### Sayfa taşması — sayfalama YOK, uyarlanabilir aralık

Comments ucunun sayfalama şeması dokümante edilmemiş (örnek yanıtta `paging`
nesnesi bile yok). Üstelik Meta yorumlar için özel uyarı veriyor:
*"(#100) The After Cursor specified exceeds the max limit supported by this
endpoint"* ve *"Don't store cursors. Cursors can quickly become invalid if
items are added or deleted."*

`after` ile sayfalama yerine **uyarlanabilir polling aralığı**. Taşma tespiti
deterministik:

> Bir çekimde dönen sayfanın **en eski** yorumu bile watermark'tan yeniyse →
> sayfa taştı, mesaj kaybedildi.

Bu tespit edildiğinde aralık sıkılaşır (1 sn → 0.5 sn → 0.3 sn), akış
sakinleşince gevşer. Kota darboğaz olmadığından bu bedava bir emniyet supabı
ve sayfalamadan çok daha sağlam.

### `comments.limit(50)` açıkça yazılır

50 sınırı yalnızca **doğrudan** uçta belgeli. İç içe alan genişletmesinde
(`live_media?fields=...,comments`) Graph'ın genel varsayılanı olan **25**
gelme ihtimali yüksek; hiçbir dokümanda yazmıyor. Limit her zaman açık
yazılır, gerçekte kaç döndüğü pilot yayında ölçülür.

### Yayın sonu draini — imkânsız

Meta iki ayrı yerde açıkça belirtiyor:

> *"Comments on live video IG Media can only be read while the IG Media upon
> which the comment was created is being broadcast."* (IG Comment reference)

> *"Live video Instagram Media can only be read while they are being
> broadcast."* (IG Media reference)

Media id'yi cache'lemek işe yaramıyor — kısıt **okuma** üzerinde. Yayın
Reels'e paylaşılırsa **yeni bir media** oluşur, canlı yorumları taşımaz.
Belgelenmiş bir tolerans penceresi yok.

Son polling aralığının yorumları kaybolur. Kabul ediliyor (Ödün #2).

## Hata yönetimi

| Durum | Davranış |
|---|---|
| **İlk açılış / yayına ortadan bağlanma** | Geçmişi basma. İlk çağrıdaki en yeni yorumu watermark kabul et, hiçbir şey yayınlama. |
| **Uygulama yeniden başlatma (yayın sürerken)** | Aynı kural. Downtime'daki mesajlar kaybolur; alternatif kullanıcıyı eski mesaj seliyle boğmak. |
| **Yayın yok / bitti** (`live_media` boş) | Polling'i 10 sn'ye yavaşlat; yayın gelince 1 sn'ye dön. Watermark sıfırla. |
| **Token süresi doldu** (190, 463) | Döngüyü durdur. "Instagram bağlantını yenilemen gerekiyor." Sonsuz retry yok. |
| **İzin eksik** (200, 10) | Durdur. "Instagram yorum izni verilmemiş." |
| **Geçici sunucu hatası** (1, 2, HTTP 5xx, ağ) | Üstel geri çekilme. Oturumu öldürme. |
| **Kota aşımı** (4, 17, 32, 613, 80002 / alt kod 2446079) | `X-Business-Use-Case-Usage` başlığındaki `estimated_time_to_regain_access` kadar bekle. Sabit backoff uydurma. |
| **IG hesabı Sayfa'ya bağlı değil** | `instagram_business_account` null → net Türkçe hata, döngüyü hiç başlatma. |
| **Hesap yayın sırasında kişisele düşürüldü** | Hata → durdur, uyar. |
| **`comments` alanı yok vs. boş dizi** | Ayır: **yok** = alan gelmedi (izin/hata sinyali); **boş** = yorum yok (normal). Aynı muamele etme. |
| **Makine uyudu / uzun kesinti** | Watermark halleder; 50'yi aşarsa taşma tespiti devreye girer. |

**Dedupe:** Graph gerçek comment id veriyor → mevcut `externalId` dedupe'u
olduğu gibi çalışır.

**`media_product_type`:** belgelenmiş `LIVE` değeri yok (`AD`, `FEED`,
`STORY`, `REELS`). Bu alana göre filtreleme yapılmayacak.

## Kota

Formül: `Calls within 24 hours = 4800 × Number of Impressions`, app-kullanıcı
çifti başına, kayan 24 saat. `instagram` ve `pages` BUC kovaları **ayrı** —
Facebook ve Instagram birbirini aç bırakmıyor.

Saniyede 1 polling = saatte 3.600 çağrı. 3,5 saatlik yayın = 12.600 çağrı →
yalnızca **3 gösterim** yeterli. Kota hiçbir zaman darboğaz değil.

(Bloglardaki "200 çağrı/saat" rakamı emekli **Instagram Basic Display API**'ye
ait; bizim varyantımızla ilgisi yok.)

**Açık risk:** app seviyesindeki eski limit (`200 × günlük aktif kullanıcı`
saat başına, `X-App-Usage` başlığı) de geçerliyse 1/sn polling onu patlatır —
saatte 3.600 çağrı için ~18 DAU gerekir. Meta dokümanı *"If both Platform and
Business Use Case rate limits can be applied to a request, BUC rate limits
will be applied"* diyerek bunu dışlıyor gibi görünüyor, ama bloglar tersini
iddia ediyor ve primer kaynak yok. **Pilot yayında ölçülecek.**

## Extension ile birlikte yaşama

`OfficialApi` açıkken `ExtensionBridgeServer` extension'dan gelen
`instagram` platformlu mesajları **sessizce düşürür**. TikTok etkilenmez.

Gerekçe: Facebook'ta yaşadığımız çift-mesaj sorununun birebir tekrarı olurdu —
extension DOM düğümünden id türetiyor, Graph gerçek comment id veriyor;
`externalId` dedupe'u iki ayrı anahtar uzayı gördüğü için yakalayamıyor.

Susturma **bridge tarafında tek taraflı** yapılır (extension'dan komple
çıkarmak yerine): Web Store sürüm yayılma gecikmesinden bağımsız çalışır ve
`Scraper` moduna dönüldüğünde eski yol yedek olarak durur.

**IG izleyici sayısı:** extension'da kod var (`scanViewerCount`,
`live_viewers` selector'ları) ama canlı yayında hiç doğrulanmadı, ilk turda
tahminle yazıldı ve çalışmıyor. `live_media` da izleyici sayısı vermiyor.
Yani bu geçişte **kayıp yok**.

## Ayarlar ve UI

**Ayarlar**
- `InstagramIngestMode`: `Scraper` → `OfficialApi`. **Varsayılan `Scraper`
  kalıyor.** App Review onayı gelene ve pilot yayında doğrulanana kadar
  opt-in — Facebook'ta izlenen yolun aynısı.
- Yeni ayar alanı yok.

**UI**
- Ayarlar'daki Facebook bağlantı kartının altına IG durum satırı: hesap
  çözülebiliyorsa `@kullaniciadi`, çözülemiyorsa "Bu Sayfa'ya bağlı Instagram
  profesyonel hesabı yok."
- **İzin uyarısı:** `GET /{user-id}/permissions` ile `instagram_manage_comments`
  kontrol edilir; yoksa "Instagram yorumları için bağlantını yenilemen
  gerekiyor" + yeniden bağlan butonu. Mevcut kullanıcıların hepsi görecek.
- Chat mesajlarındaki IG rozeti değişmiyor; kaynak değişikliği operatöre
  görünmüyor.
- IG mesajlarında sağ-tık moderasyon menüsü **açılmaz** (Facebook'tan farkı).

## Test stratejisi

xUnit, ağ erişimi yok:

- **Watermark:** normal akış / silinen yorum / aynı saniyede çoklu yorum /
  ilk açılış (geçmiş basılmaz) / yeni media id → sıfırlama.
- **Sıralama:** ters kronolojik sayfayı kronolojiye çevirme, `id` ikincil
  anahtar.
- **Taşma tespiti:** en eski yorum bile watermark'tan yeniyse → aralık
  sıkılaştırma tetiklenir.
- **Hata sınıflandırma:** 190/463 → durdur; 200/10 → durdur + farklı mesaj;
  4/17/32/613/80002 → `estimated_time_to_regain_access` kadar bekle;
  5xx/ağ → üstel geri çekilme.
- **Alan ayrımı:** `comments` yok ≠ boş dizi.
- **Bridge:** `OfficialApi` açıkken extension'ın `instagram` mesajları düşer,
  `tiktok` düşmez.

## Pilot yayın doğrulaması

Tahmin bırakmıyoruz; şunlar gerçek yayında ölçülecek:

0. **`ads_read` gerekiyor mu?** BM yönetimindeki gerçek bir Sayfa ile,
   `ads_read` olmadan `live_media` + `comments` çağrısı. Çalışıyorsa izin
   listesine eklenmez. **App Review'dan önce yapılmalı.**
1. `comments.limit(50)` yazınca gerçekten kaç yorum dönüyor — 50 mi, 25 mi?
2. `X-App-Usage` **ve** `X-Business-Use-Case-Usage` başlıklarının ikisi de
   loglanır → app seviyesi limit gerçekten devrede mi?
3. Instagram'ın Hidden Words filtresine takılan bir test yorumu yazılır →
   API döndürüyor mu? `hidden` alanı doğru mu? (Dokümanda dışlanan üç şey
   sayılıyor — yaş kısıtlı medya, Mentions API yorumları, Restrict edilmiş
   kullanıcılar — Hidden Words listede yok, yani muhtemelen geliyor.
   Geliyorsa IG'nin bastırdığı hakareti operatöre göstermiş oluruz;
   moderasyon yapamadığımız için bu ters etki yaratır.) `hidden` alanının
   güvenilirliği şüpheli: 2024'e kadar süren bir Meta forum başlığında
   natively görünen yorumların `is_hidden: true` döndüğü raporlanmış.
4. Yayın bitiminde kayıp gerçekten ~1 saniye mi?
5. Yorum sırası doğru mu; çift mesaj var mı (bridge susturması çalışıyor mu)?

## Sıralama

1. Kod yazılır, testler geçer, `InstagramIngestMode` varsayılan `Scraper`
   kalır → kullanıcı davranışı **değişmez**.
2. Meta panelinde `config_id` yapılandırmasına IG izinleri eklenir.
3. Pilot yayın: geliştirici hesabıyla `OfficialApi` açılıp aşağıdaki
   ölçümler yapılır. **Standard Access yeterli** — Meta belgeli: *"Permissions
   with Standard Access can only be requested from app users who have a role
   on the requesting app"*, ve bu erişim seviyesi Live/Development modundan
   bağımsız. App zaten Live olduğu halde yönetici/geliştirici kendi hesabıyla
   test edebilir.
4. Pilot sonucuna göre izin listesi kesinleşir (`ads_read` gerekli mi?),
   ekran kaydı çekilir ve App Review'a başvurulur.
5. **Onay geldikten sonra** varsayılan `OfficialApi`'ye çevrilir ve
   kullanıcılara yeniden bağlanma uyarısı gösterilir. Onaya kadar herkes
   extension ile devam eder.

## Kapsam dışı

- **Webhook (`live_comments`)** — `pages_manage_metadata` + public hesap +
  sunucu→masaüstü kanalı gerektiriyor; o kanal repo'da yok (SignalR/hub
  aranıp bulunamadı; tüm WebSocket kullanımı yerel). Gelecek seçenek.
- **Moderasyon** — Meta desteklemiyor.
- **Replies / `parent_id`** — düz muamele.
- **Instagram için ayrı OAuth akışı** — Facebook bağlantısına biniyor.
- **Yeni ayar alanları** — mevcut `InstagramIngestMode` yeterli.

## Kaynaklar

- [IG User `live_media`](https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-user/live_media/)
- [IG Media `comments`](https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-media/comments/)
- [IG Comment reference](https://developers.facebook.com/docs/instagram-platform/instagram-graph-api/reference/ig-comment)
- [Graph API rate limiting](https://developers.facebook.com/docs/graph-api/overview/rate-limiting/)
- [Instagram Platform overview](https://developers.facebook.com/docs/instagram-platform/overview)
- [Field expansion](https://developers.facebook.com/docs/graph-api/guides/field-expansion/)
- [Paging / results](https://developers.facebook.com/docs/graph-api/results)
- [Comment moderation](https://developers.facebook.com/docs/instagram-platform/comment-moderation/)
- [Instagram webhooks](https://developers.facebook.com/docs/instagram-platform/webhooks/)
