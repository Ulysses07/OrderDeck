# Kayıt formu — "Hesabınla bağlan" (Faz 2) kurulum ve yayına alma

## Env değişkenleri (VPS `.env`)

| Değişken | Anlamı |
|---|---|
| `IntakeLogin__GoogleClientId` | Google OAuth istemci kimliği (AYRI "Web application" client — masaüstünün client'ı DEĞİL) |
| `IntakeLogin__GoogleClientSecret` | Aynı client'ın sırrı |
| `IntakeLogin__YouTubeEnabled` | `true` yapılınca YouTube bağlama açılır (Google doğrulaması ONAYLANMADAN açma) |
| `IntakeLogin__FacebookEnabled` | `true` yapılınca Facebook bağlama açılır (review istemez, hemen açılabilir) |

Facebook app kimliği/sırrı MEVCUT `OrderDeck__Facebook__*` değişkenlerinden
okunur — yeni değişken yok. Redirect URI kodda sabit:
`https://orderdeckapp.com/musteri-kayit/baglanti-donusu`.

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

## Meta (app 3939617702835404) kurulumu

1. **Facebook Login → Settings → Valid OAuth Redirect URIs**'e
   `https://orderdeckapp.com/musteri-kayit/baglanti-donusu` EKLE (mevcut
   masaüstü redirect'i kalır).
2. `public_profile` için App Review GEREKMEZ. `.env`'e
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
> https://orderdeckapp.com/privacy.

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
