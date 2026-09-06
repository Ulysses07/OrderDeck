# Instagram "!kayıt" → DM ile kayıt linki — tasarım

Tarih: 2026-09-04 · Durum: kullanıcı onaylı tasarım (uygulama planı ayrı)

## Problem

Kayıt formunda izleyiciler Instagram kullanıcı adlarını elle yazıyor ve büyük
bölümü hatalı/eksik (taban ölçüm: `docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`,
hareketsiz oran %46-95). YouTube (Google OAuth) ve Facebook (FB Login) için
"hesabınla bağlan" çözüldü; Instagram'da **bireysel hesaplar için resmi OAuth
yolu YOK** — Basic Display API Aralık 2024'te kapatıldı, Graph API yalnız
Business/Creator hesap destekliyor. İzleyiciler bireysel hesap kullandığı için
"Instagram ile bağlan" butonu kurulamaz.

## Çözüm: kimliği formdan değil sohbetten al

İzleyici canlı yayında `!kayıt` yazar → Meta `live_comments` webhook'u yorumu
kullanıcı adı + ID'siyle sunucuya düşürür → sunucu **Private Reply** ile o
izleyiciye DM atar: kişiye özel tokenlı kayıt linki → izleyici linke tıklar,
formda Instagram kimliği **baştan doğrulanmış bağlı** gelir; yalnız adresini
doldurur. Kullanıcı adı hiç elle yazılmaz.

Meta API kısıtları (doğrulandı, 2026-09-04):

- Private reply canlı yayın yorumları için çalışır; pencere = **yayın süresi**
  (yayın bitince gönderilemez). Bizim kullanım anı zaten yayın sırası.
- Yorum başına **1** private reply. Yeterli.
- DM'i gönderen taraf yayıncının **professional** hesabı — tüm yayıncılarda
  zaten var. İzleyicinin bireysel hesabı sorun değil.

## Kapsam

- **Yalnız Instagram.** Facebook tarafı FB Login ile zaten çözüldü; motor
  içeride platform-soyut durabilir ama FB için açılmayacak.
- Formdaki elle "Instagram kullanıcı adı" alanı **kalır** (fallback — webhook
  düşerse / DM açılmazsa / yayıncı bağlı değilse kayıt yine alınabilir).
- TikTok kapsam dışı: böyle bir API yok, elle giriş sürer.

## Bileşenler

### 1. Yayıncı bağlama — mevcut akışın üstüne, yeni ekran YOK

- WPF'teki "Facebook'a bağlan" code→token takası zaten sunucudan geçiyor
  (`FacebookOAuthController`, #286). Scope listesi kodda değil, Meta login
  config'inde (`LoginConfigId`).
- Meta dashboard: login config'e `instagram_manage_messages` +
  `pages_manage_metadata` (webhook aboneliği için) eklenir. Kod değişmez.
- Sunucu, exchange sırasında uzun ömürlü kullanıcı token'ından Page token'ı ve
  `instagram_business_account{id,username}` bilgisini kendisi çözer; yeni
  `InstagramAccount` entity'sine yazar: `LicenseId`, `PageId`, `IgUserId`,
  `IgUsername`, **şifrelenmiş** Page token. Sayfayı app webhook'una abone eder
  (`POST /{page-id}/subscribed_apps`).
- **Saklama opt-in:** uç bugün "sunucu token SAKLAMAZ" sözü veriyor. Saklama,
  müşteri bazında admin panelden açılan "IG kayıt botu" bayrağına bağlı;
  bayrak kapalıysa davranış bugünkü gibi (yalnız relay).
- Yayıncının tek işi: bayrak açıldıktan sonra mevcut butonla **bir kez yeniden
  bağlanıp** yeni izinleri onaylamak.

### 2. Webhook ucu

- `WhatsAppWebhookController` deseninin eşi, ayrı uç:
  `/api/v1/instagram/webhook`. GET `hub.challenge` doğrulama; POST'ta
  `X-Hub-Signature-256` HMAC imzası masaüstü Meta app'inin
  (3939617702835404) secret'ıyla, sabit-zamanlı karşılaştırma.
- `live_comments` alanına abonelik. Gelen yorumdaki IG hesap ID'sinden
  `InstagramAccount` → `LicenseId` → `IntakeFormConfig.Slug` bulunur.

### 3. Tetik + DM

- Yorum metni normalize edilir (trim, küçük harf, `ı/i` toleransı):
  `!kayıt` ve `!kayit` tetikler.
- **Tek kullanımlık**, 24 saat geçerli token üretilir; `IgUserId`,
  `IgUsername` ve slug'a bağlıdır. (Yayın bitse de izleyici DM'deki linki
  sonradan açabilmeli — DM penceresi kapansa bile link çalışır.)
- Private reply ile DM: kayıt linki `…/musteri-kayit/{slug}?ig=TOKEN`.
- Spam koruması: aynı IG kullanıcısına saatte en fazla 1 DM.

### 4. Form tarafı

- `?ig=TOKEN` parametresi görülünce token çözülür ve mevcut `IntakeLinkStore`
  mekanizmasına `IntakeLinkedIdentity` konur — OAuth dönüşüyle birebir aynı
  yol: çipte `@kullanıcıadı`, gönderimde `InstagramUsername` doğrulanmış
  yazılır. Token geçersiz/süresi dolmuşsa form normal (bağlantısız) açılır,
  hata ekranı yok.

### 5. Karanlık yayın + App Review

- `IntakeLogin` desenindeki gibi bayrak: kapalıyken webhook/DM uçları 404.
- `instagram_manage_messages` advanced access için Meta App Review gerekir
  (ekran kaydı ister; FB `public_profile` başvurusundan ağır). Onaya kadar
  yalnız app'te rolü olan hesaplarla test edilir.

## Riskler / ilk canlı testte ölçülecekler

- DM'in "İstekler" kutusuna düşme ihtimali (yorum = etkileşim başlattığı için
  düşük beklenir; sahada doğrulanacak).
- `live_comments` webhook gecikmesi ve teslim güvenilirliği.
- Private reply hız sınırları (yoğun yayında art arda `!kayıt`).

## Başarı ölçütü

`docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md` sorguları yeniden
koşturulduğunda Instagram kaynaklı eşleşmeyen kayıt oranının düşmesi.
