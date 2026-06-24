# YouTube Resmi API'ye Geçiş Planı (chat ingestion → streamList)

**Durum:** Planlandı, henüz uygulanmadı. Bugün canlı sohbet **scraper** ile
çalışıyor (PR #165 — "Canlı sohbet" görünümü). Bu doküman, scraper'ı resmi
YouTube Data/Live Streaming API ile değiştirme planıdır.

**Tarih:** 2026-06-24

---

## 1. Neden

Scraper (innertube `get_live_chat`) çalışıyor ama:
- **Top chat filtresine tabi** — PR #165 "Canlı sohbet" görünümünü çekerek
  bunu büyük ölçüde aştı, ama Canlı sohbet bile %100 garanti değil (YouTube'un
  taban kötüye-kullanım filtresinden geçer).
- **Kırılgan** — innertube HTML/JSON yapısı habersiz değişebilir (geçmişte
  TikTok/FB'de yaşandı).
- **Mesaj ID'leri moderasyonla uyumsuz olabilir** — scraper ID'leri resmi
  `liveChatMessages.delete`/`bans` ile birebir eşleşmeyebilir.

Resmi API kazancı: **eksiksiz akış + stabil sözleşme + moderasyonla aynı ID
uzayı**. Bedeli: **kota** ve (streamList için) **manuel streaming kodu**.

## 2. Mevcut durum (yeniden kullanılacak altyapı)

Zaten var ve moderasyon için çalışıyor (`OrderDeck.Chat/YouTube/`):
- `YouTubeOAuthService` + `YouTubeOAuthDefaults` (gömülü client id/secret) +
  `EncryptedYouTubeTokenStore` — OAuth akışı ve şifreli token.
- `YouTubeModerationService.GetActiveLiveChatIdAsync()` — aktif yayının
  `liveChatId`'sini `liveBroadcasts.list?mine=true` ile çözüp 60 sn cache'liyor.
- `Google.Apis.YouTube.v3` 1.69.0.3742 — `LiveChatMessages.List` **var** (tipli).
  `StreamList` **tipli metot olarak YOK** (DLL'de `StreamListRequest` yok).

Eksik: chat **ingestion** için resmi API kullanan bir `IChatIngestor`.

## 3. Ön koşullar (kod yazmadan önce)

1. **Kota artışı onayı.** Audit başvurusu yapıldı (hedef 750k/gün, 3k/dk).
   `liveChatMessages.list` maliyeti Google tarafından **belgelenmemiş**;
   toplulukta **~5 birim/çağrı** olarak ölçülüyor. 5 birimse adaptif-polling
   tek yayında ~33.750 birim/gün → **varsayılan 10k yetmez, artış şart**.
2. **Gerçek maliyeti ölç.** İlk poll'da Cloud Console kota grafiğinden /
   response'tan birim tüketimini doğrula. Eşik/aralık ayarını buna göre yap.
3. **Scope doğrula.** Moderasyonun `youtube.force-ssl` scope'u okumayı da
   kapsar; ek scope gerekmez (yine de ilk bağlantıda doğrula).

## 4. Mimari

Yeni: `OrderDeck.Chat/Ingestors/YouTube/YouTubeOfficialChatIngestor.cs`
(`IChatIngestor`). Hosted service kalıbı mevcut `YouTubeChatHostedService`
ile aynı (resolve → ingest → silence/end → idle).

**Ayar bazlı geçiş (feature flag):** `AppSettings.YouTubeIngestMode`
= `Scraper | OfficialApi`. Varsayılan `Scraper`; opt-in `OfficialApi`. Böylece
- Sorun çıkarsa anında scraper'a geri dön,
- A/B karşılaştırması yap (aynı yayında iki kaynak say, fark ölç).

**İki faz:**
- **Faz 1 — adaptif-polling `list`** (client hazır, hızlı, doğru temel).
- **Faz 2 — `streamList` push** (manuel HTTP streaming, kota/gecikme optimizasyonu).

### Yeniden kullanım
- `liveChatId` → `YouTubeModerationService.GetActiveLiveChatIdAsync()` (zaten
  cache'li). Resolver'a gerek yok; handle değil, mine=true ile geliyor.
- OAuth token → `EncryptedYouTubeTokenStore` üzerinden `YouTubeService`
  (Faz 1) veya ham Bearer header (Faz 2).
- Çıktı → mevcut `ChatMessage` kaydı + `IChatBus.Publish` (scraper ile aynı).
- `ViewerCountTracker`, `SpamFilter` entegrasyonu scraper'daki gibi.

## 5. Faz 1 — adaptif-polling `list`

Akış:
1. `liveChatId` al (cache'li).
2. `LiveChatMessages.List(liveChatId, "snippet,authorDetails")`,
   `PageToken = nextPageToken` (artımlı).
3. Yanıttaki her item → `ChatMessage`'a map'le (bkz. §7), `Publish`.
4. **Adaptif bekleme:**
   ```
   hız = sonBatchMesajSayısı / geçenSüreSn
   aralık = (hız >= 7) ? 2sn : 3sn
   aralık = max(aralık, pollingIntervalMillis)   // API'den hızlı pollama → 403
   ```
   Eşik (7 msg/sn) ve aralıklar (2/3 sn) ayarlanabilir sabitler; ilk gerçek
   yayında ölçüp ince ayar yap.
5. **Kota guard:** günlük çağrı/birim sayacı tut; bütçe eşiğine yaklaşınca
   (örn. günlük kotanın %90'ı) **scraper'a degrade** + uyarı logla.
6. Stream-end heuristiği: scraper'daki silence timeout mantığını koru
   (`liveChatId` boş dönerse / `offlineAt` gelirse yayın bitti).

Kota (adaptif 2/3, %50-%50, 4.5 sa): ~6.750 çağrı/gün. Birim 5 ise
~33.750/gün → 750k kotada rahat.

## 6. Faz 2 — `streamList` (push)

**ÖNEMLİ — streamList gRPC-ONLY'dir.** HTTP/REST uç noktası YOK. Discovery
tabanlı client'lar (Python `google-api-python-client`, bizim .NET
`Google.Apis.YouTube.v3` 1.69) bu metodu **içermez** (`StreamListRequest`
DLL'de yok). Tek yol gRPC stub üretmek.

Proto (resmi `stream_list.proto`):
```protobuf
service V3DataLiveChatMessageService {
  rpc StreamList(LiveChatMessageListRequest)
      returns (stream LiveChatMessageListResponse) {}
}
```
- Endpoint: `youtube.googleapis.com:443` (gRPC/HTTP2/TLS).
- İstek: `live_chat_id` (zorunlu), `part` (id/snippet/authorDetails),
  `max_results` (200-2000, vars.500), `page_token`, `hl`, `profile_image_size`.
- Yanıt (stream halinde tekrar): `items[]`, `next_page_token`,
  `pollingIntervalMillis`, `offline_at` (yayın bitti), `active_poll_item` (anket).

.NET uygulama:
1. Paketler: `Grpc.Net.Client` + `Google.Protobuf` + `Grpc.Tools` (build'de
   `stream_list.proto`'dan C# stub üret).
2. `GrpcChannel.ForAddress("https://youtube.googleapis.com")`.
3. Auth metadata: `("authorization", "Bearer "+token)` — token mevcut
   `EncryptedYouTubeTokenStore`/`YouTubeOAuthService`'ten; 401/UNAUTHENTICATED'te yenile.
   (Alternatif: `("x-goog-api-key", KEY)` ama özel chat okuması için OAuth.)
4. `responseStream.ReadAllAsync()` ile akışı oku → her item'ı `ChatMessage`'a
   map'le (§7) → `IChatBus.Publish`.
5. **Reconnect:** stream biter/koparsa **`page_token = son next_page_token`** ile
   yeniden bağlan (mesaj kaybı yok).
6. liveChatId → mevcut `GetActiveLiveChatIdAsync()`.
- **Kanal havuzu GEREKMEZ:** tek yayıncı = tek stream; havuz yalnız çok
  eşzamanlı stream'de gerekir (HTTP/2 SETTINGS_MAX_CONCURRENT_STREAMS).
- **gRPC hata kodları:** PERMISSION_DENIED(7), INVALID_ARGUMENT(3),
  FAILED_PRECONDITION(9=chat kapalı/bitti), NOT_FOUND(5),
  RESOURCE_EXHAUSTED(8=rate limit). **Gotcha:** koddan
  LIVE_CHAT_DISABLED ile LIVE_CHAT_ENDED ayırt EDİLEMEZ (ikisi de 9).
- **Fallback zinciri:** streamList hata → Faz 1 `list` polling → o da olmazsa
  scraper (kademeli düşüş; yayın hiç chat'siz kalmasın).

Kazanç: anlık gecikme + (beklenen) çok düşük kota (poll yok) + yapısal
event'ler (superchat/üyelik/anket/ban) + moderasyonla aynı ID uzayı.
Risk: gRPC bağımlılığı + proto codegen (projede yeni), **kota maliyeti
belgesiz → ilk çalıştırmada ölç**, yeni/az-denenmiş, disabled/ended ayrımı yok.

Kaynak: Google "Streaming Live Chat" rehberi + liveChatMessages.streamList ref
(doküman 2025-07-14 güncellendi; Java client rev20241010 ≈ 2024-10).

## 7. Mesaj eşleme (parser)

Resmi API şekli scraper'dan **farklı** — ayrı mapper gerekir:
- `id` → `ExternalId` (moderasyonla aynı ID uzayı — bonus).
- `snippet.displayMessage` (veya `textMessageDetails.messageText`) → `Text`.
- `authorDetails.displayName` → `DisplayName`,
  `authorDetails.channelId` → `Username`/`channelId`.
- `authorDetails.isChatOwner/Moderator/Sponsor` → `Badges` (owner/moderator/member).
- `snippet.superChatDetails` → superchat rozeti + tutar.
- Emoji/run birleştirme: API `displayMessage`'ı düz metin verir (scraper'daki
  runs birleştirmesine gerek kalmayabilir; doğrula).

## 8. Test stratejisi

- **Birim:** mapper (örnek API JSON → `ChatMessage`); adaptif aralık fonksiyonu
  (hız→aralık, pollingIntervalMillis clamp); kota guard eşiği.
- **Entegrasyon (manuel):** gerçek yayında scraper vs official aynı anda say,
  fark ve kota tüketimini ölç (PR #165'teki Console snippet mantığı gibi).
- Mevcut 666 test paketi yeşil kalmalı.

## 9. Rollout

1. Faz 1 ingestor + `YouTubeIngestMode` ayarı (varsayılan Scraper) → sürüm.
2. Kota onayı gelince kendi yayınında `OfficialApi`'ye al, bir yayın A/B ölç.
3. Eksiksizlik + kota tatmin ediciyse varsayılanı `OfficialApi` yap, scraper'ı
   fallback olarak bırak.
4. Faz 2 (streamList) ayrı sürümde; yine flag arkasında, fallback list.

## 10. Riskler

| Risk | Etki | Önlem |
|---|---|---|
| `list` 5 birim → kota | Yüksek | Kota artışı (750k) ön koşul; günlük guard + scraper degrade |
| streamList .NET'te manuel | Orta | Faz 2'ye ertele; önce list ile sağlam temel |
| OAuth token süresi/yenileme | Orta | `YouTubeOAuthService` refresh; 401'de yenile+retry |
| Çok yayınlı gün kotayı 2×'ler | Orta | Guard + artış kotası |
| API şekli moderasyon ID'siyle map | Düşük | `id` doğrudan kullanılır (bonus uyum) |

## 11. Açık sorular

1. `liveChatMessages.list` gerçek birim maliyeti (1 mi 5 mi) — ilk çağrıda ölç.
2. streamList'in kota maliyeti (belgelenmemiş) — Faz 2'de ölç.
3. Kota artışı onay durumu (audit başvurusu) — onaylanmadan Faz 1 prod'a alınmaz.
4. `Google.Apis` ileride streamList'i tipli sunarsa manuel kod gerekmez —
   sürüm notlarını izle.
