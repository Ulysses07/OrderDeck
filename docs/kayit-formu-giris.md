# Kayıt formu — "Hesabınla bağlan" (Faz 2) kurulum ve yayına alma

## Env değişkenleri (VPS `.env`)

| Değişken | Anlamı |
|---|---|
| `IntakeLogin__GoogleClientId` | Google OAuth istemci kimliği (AYRI "Web application" client — masaüstünün client'ı DEĞİL) |
| `IntakeLogin__GoogleClientSecret` | Aynı client'ın sırrı |
| `IntakeLogin__FacebookAppId` | "OrderDeck Kayit" Consumer app'inin kimliği (`1090037693602616`) — masaüstünün app'i DEĞİL |
| `IntakeLogin__FacebookAppSecret` | Aynı app'in sırrı (Meta → App settings → Basic → App secret) |
| `IntakeLogin__YouTubeEnabled` | `true` yapılınca YouTube bağlama açılır (Google doğrulaması ONAYLANMADAN açma) |
| `IntakeLogin__FacebookEnabled` | `true` yapılınca Facebook bağlama açılır (review istemez, hemen açılabilir) |

Redirect URI kodda sabit: `https://orderdeckapp.com/musteri-kayit/baglanti-donusu`.

## Google Cloud kurulumu (proje 876199969087 — mevcut onaylı proje)

1. **APIs & Services → Credentials → Create Credentials → OAuth client ID →
   Web application.** Masaüstü client'ına DOKUNMA; sunucu akışı için ayrı client.
2. Authorized redirect URI: `https://orderdeckapp.com/musteri-kayit/baglanti-donusu`
3. **OAuth consent screen → Scopes → Add scope:**
   `https://www.googleapis.com/auth/youtube.readonly` ekle → doğrulama başvurusu
   tetiklenir (aşağıdaki gerekçe metnini kullan).
4. Doğrulama varlıkları önceki başvurudan hazır:
   `C:\Users\burak\Documents\OrderDeck\youtube-audit\` (demo video, ekran
   görüntüleri). Yeni video kayıt formundaki akışı göstermeli: forma gir →
   "Google ile bağla" → hesap seç → formda kanal adı çipi → kaydı gönder.
5. Onay gelene KADAR: kod prod'da, `IntakeLogin__YouTubeEnabled` yazılMAMIŞ
   (bayrak kapalı) — uçlar 404, formda link yok. Onay gelince `.env`'e
   `IntakeLogin__YouTubeEnabled=true` ekle + `docker compose up -d license-server`.

## Meta kurulumu — AYRI Consumer app ("OrderDeck Kayit", 1090037693602616)

Masaüstünün app'i (3939617702835404) **Facebook Login for Business** tipinde ve
yalnız `public_profile` isteyen klasik `dialog/oauth` çağrısını "It looks like
this app isn't available / supported permission" hatasıyla REDDEDİYOR (sahada
gerçek kullanıcıyla görüldü, 2026-09-04). FLB'de dialog `config_id` ister ve
izin kümesi login config'ten gelir; formun ihtiyacı olan salt-`public_profile`
orada tanımlanamıyor. Çözüm: form için Consumer tipinde ayrı app.

Kurulum durumu (2026-09-04, hepsi yapıldı):

1. Consumer app + "Authenticate and request data from users with Facebook
   Login" kullanım durumu. Classic login — `config_id` YOK, `public_profile`
   otomatik erişimli, App Review gerekmez.
2. **Facebook Login → Settings → Valid OAuth Redirect URIs**:
   `https://orderdeckapp.com/musteri-kayit/baglanti-donusu`
3. Basic settings: gizlilik/koşullar/veri-silme URL'leri, kategori (Alışveriş),
   1024×1024 ikon.
4. App, Emar Global işletme portföyüne bağlandı (işletme doğrulaması oradan
   sağlanıyor) ve **Published** durumda.
5. `.env`'e `IntakeLogin__FacebookAppId` + `IntakeLogin__FacebookAppSecret` +
   `IntakeLogin__FacebookEnabled=true` → `docker compose up -d license-server`.

## Google doğrulama başvurusu — gerekçe metni

**Scope justification (EN — başvuru formuna):**

> OrderDeck is a live-stream commerce tool for Turkish broadcasters. Viewers
> buy items by typing a product code into the live chat; the broadcaster then
> matches the chat message to a shipping-info registration the viewer filled
> in on our public web form.
>
> Today viewers type their YouTube handle into that form by hand, and roughly
> half of the entries are misspelled or incomplete, so their chat messages can
> never be matched to their shipping registration and their orders are lost.
>
> We request the `youtube.readonly` scope solely so a viewer can press
> "Sign in with Google" on the registration form and we can read the channel
> title, handle (customUrl) and channel ID of THEIR OWN channel via
> `channels.list(mine=true)` — one API call at sign-in, nothing else. This is
> the minimum scope that includes `channels.list` with `mine=true`. We do not
> read subscriptions, playlists, videos, analytics or any other data; we do
> not store the OAuth tokens (the access token is used once, server-side, and
> discarded); we never post, modify or delete anything.
>
> The retrieved channel identity is stored only as part of that viewer's own
> shipping registration, visible only to the broadcaster they are registering
> with, and is covered by our privacy policy at
> https://orderdeckapp.com/en/privacy-policy/.

**Aynı metnin TR özeti (kendi kaydımız için):** izleyici formda "Google ile
bağla"ya basar; `channels.list(mine=true)` ile YALNIZ kendi kanalının adı,
handle'ı ve kimliği okunur; token saklanmaz, başka veri okunmaz, hiçbir şey
yazılmaz. Amaç: elle yazılan hatalı kullanıcı adları yüzünden sohbetle
eşleşemeyen kayıtların (taban ölçüm: hareketsiz oran %46-95, bkz.
`docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`) önüne geçmek.

## Yayın sonrası doğrulama

1. Deploy sonrası `IntakeLogin__*` yazılmadan: form eskisi gibi, `/baglan/...`
   404 (karanlık deploy kanıtı).
2. Facebook açılınca: gerçek telefonla forma gir → Facebook ile bağla →
   çipte adını gör → kaydı gönder → panelde kaydın `FacebookUsername`'inde
   görünen adı doğrula.
3. YouTube onayı gelince aynı akış + WPF müşteri listesinde kanal `UC…`
   channelId'siyle eşleşiyor mu kontrol et.
4. Bir süre sonra `docs/olcum/2026-09-02-eslesmeyen-kayit-olcumu.md`
   sorgularını tekrar koştur — oran düşüyor mu, özelliğin varlık sebebi bu.

---

## Instagram "!kayıt" → DM

### Özellik özeti

İzleyici IG canlı yayın sohbetine `!kayıt` yazar; bot bunu yakalar ve private
reply DM olarak tokenlı kayıt linki gönderir. İzleyici linke tıklayınca form
açılır ve Instagram kimliği otomatik bağlanır — `InstagramUsername` alanı elle
girme gerekmez.

Yayıncı tarafı kurulumu iki adım:

1. **Panelden** `instagramDmBotEnabled` bayrağını aç (`PUT /api/v1/me/intake-form`).
2. **WPF'ten Facebook'a (yeniden) bağlan.** Bağlanma sırasında IG hesabı ve
   sayfa long-lived token'ı sunucuda şifreli saklanır; `live_comments` webhook
   aboneliği otomatik kurulur.

### Env değişkenleri (VPS `.env`)

| Değişken | Anlamı |
|---|---|
| `InstagramDm__Enabled` | Varsayılan `false` — webhook ucu karanlık. `true` yapılınca `/api/v1/instagram/webhook` aktifleşir. |
| `InstagramDm__VerifyToken` | Meta panelinde girilen verify token ile **birebir aynı** olmalı. Örnek: `<rastgele-uzun-değer>` |

### Meta paneli el adımları

**Sıra ÖNEMLİ:** önce `.env`'e değerleri yaz ve container'ı yeniden başlat;
ardından Meta panelinde doğrulama başlat — uç `Enabled=true` olmadan 404
döner ve Meta doğrulaması geçemez.

1. **Masaüstü uygulamanın app'i** (App ID `3939617702835404`) → **Facebook
   Login for Business** → ilgili login config'e şu scope'ları ekle:
   `instagram_manage_messages`, `pages_manage_metadata`.

2. **App → Webhooks → Instagram nesnesi:**
   - Callback URL: `https://license.orderdeckapp.com/api/v1/instagram/webhook`
   - Verify Token: `.env`'deki `InstagramDm__VerifyToken` değeriyle aynı
   - Alan: `live_comments` → abone ol

3. **App Review:** `instagram_manage_messages` advanced access başvurusu aç.
   Meta ekran kaydı ister (`!kayıt` → DM akışını gösteren video). Onay
   gelene kadar yalnız app'te rolü olan hesaplar (test kullanıcıları) özelliği
   kullanabilir.

4. **Yayın sonrası uçtan uca doğrulama:**
   - Panelden `instagramDmBotEnabled` bayrağını aç.
   - WPF'ten Facebook'a yeniden bağlan.
   - Sunucuda `InstagramAccounts` tablosunda bağlı yayıncı satırını kontrol et.
   - Canlı yayında `!kayıt` yaz.
   - Birkaç saniye içinde DM'de tokenlı link gelmeli.
   - Linkte form açılınca Instagram çipi (`@kullanici_adi`) görünmeli.
   - Kaydın tamamlanması sonrası `InstagramUsername` alanının dolu olduğunu
     panelden doğrula.
