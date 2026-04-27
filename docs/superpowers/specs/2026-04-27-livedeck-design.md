# LiveDeck — Tasarım Dokümanı

**Tarih:** 2026-04-27
**Durum:** Onay bekleniyor
**Hedef versiyon:** v1 MVP

---

## 1. Vizyon ve Kapsam

### 1.1 Tek cümlelik tanım

OBS üzerinden canlı satış yapan yayıncılar için tek ekrandan çoklu platform chat, sipariş yakalama ve çekiliş yönetimi yazılımı.

### 1.2 Hedef kitle

Birincil: Instagram Live ve TikTok Live üzerinden canlı satış yapan Türk yayıncılar — tekstil/giyim, kozmetik, takı segmentleri. Tipik kullanıcı: bireysel yayıncı veya 2-3 kişilik küçük ekip.

### 1.3 Ürün konumu

LiveDeck **yayın iletimini yapmaz**. Yayın iletimini OBS Studio yapmaya devam eder. LiveDeck OBS'in yanında çalışır, OBS'e Browser Source olarak chat/çekiliş/sipariş overlay'leri sağlar, ve yayıncıya sipariş yönetimi panelini sunar.

### 1.4 Yapar / Yapmaz

| ✅ Yapar | ❌ Yapmaz |
|---|---|
| Instagram, TikTok, Facebook Live chat birleştirir | Video yayın iletimini yapmaz (OBS yapar) |
| Chat'i OBS overlay'de gösterir (Browser Source) | Ürün stoğu yönetmez |
| "MAVİ XL aldım" mesajlarını sipariş olarak yakalar | E-ticaret platformu (Shopify, Trendyol vs.) ile senkronize olmaz |
| Sipariş kuyruğu + durum takibi | Fatura kesmez |
| Etiket için clipboard otomasyonu (mevcut etiket.exe ile uyumlu) | v1'de doğrudan yazdırma yapmaz |
| Çekiliş — çoklu platform katılım, OBS animasyonu | Otomatik ödeme almaz |
| Yayın sonrası sipariş raporu (Excel) | Kargo entegrasyonu yapmaz |
| Username bazlı müşteri tracking + kara liste | WhatsApp ile entegre olmaz (kullanıcı manuel kullanır) |
| Yıllık abonelik lisans modeli | Cloud API entegrasyonu yapmaz |

### 1.5 Fiyatlandırma

**LiveDeck Yıllık** — 50.000 TL/yıl, tek tier
- Tüm özellikler dahil
- 1 makinede çalışır (donanım bağlı)
- Sınırsız yayın, sınırsız sipariş, sınırsız çekiliş
- Yazılım güncellemeleri dahil
- E-posta destek
- 14 gün ücretsiz deneme (kart gerekmez), trial sırasında **sadece Instagram** aktif

Pro/Enterprise tier'lar ürün büyüdükçe eklenir (çoklu operatör, audit log, beyaz etiket için). MVP'de tek tier.

### 1.6 Geliştirici/kullanıcı durumu

Ürünün ilk geliştiricisi aynı zamanda ilk kullanıcısıdır (canlı satış yapıyor). Bu durum geliştirme stratejisini belirler:
- Faz 1 bittiğinde geliştirici kendi yayınında dogfood eder
- Her fazda gerçek yayın feedback'i alınır
- Lisans sistemi sadece dış müşteri için gerekir; geliştirici DEBUG modunda lisans bypass kullanır

---

## 2. Mimari

### 2.1 Solution proje düzeni

| Proje | Hedef framework | Sorumluluk |
|---|---|---|
| `LiveDeck.App` | `net10.0-windows` (WPF) | Ana masaüstü UI, MVVM, DI yapılanması, panel ekranları |
| `LiveDeck.Core` | `net10.0` | İş mantığı: ChatBus, OrderCaptureEngine, GiveawayEngine, Customer kayıtları, ActiveCode yöneticisi |
| `LiveDeck.Chat` | `net10.0` | Platform ingestor'ları (Instagram, TikTok, Facebook, opsiyonel YouTube/Twitch/Kick), extension köprüsü |
| `LiveDeck.Overlay` | `net10.0` | ASP.NET Core minimal API + WebSocket — OBS Browser Source için chat/çekiliş/sipariş overlay yayını |
| `LiveDeck.Labeling` | `net10.0` | v1: clipboard formatlayıcı. v2: termal yazdırma + WYSIWYG şablon tasarımcısı |
| `LiveDeck.Tests` | `net10.0` | xUnit, Moq, FluentAssertions — özellikle OrderCaptureEngine için fixture-based testler |

Bağımlılıklar:
```
LiveDeck.App → Core, Chat, Overlay, Labeling
LiveDeck.Chat → Core
LiveDeck.Overlay → Core
LiveDeck.Labeling → Core
LiveDeck.Tests → Core, Chat, Labeling
```

İleride `LiveDeck.LicenseServer` projesi eklenir (Faz 4) — ASP.NET Core REST API, ayrı VPS'de host.

### 2.2 UniCast'ten taşınacaklar

| UniCast'teki şey | LiveDeck'te yeri | Değişiklik |
|---|---|---|
| `ChatBus` | `LiveDeck.Core/Chat/ChatBus` | ChatMessage persistence kaldırıldı, sadece in-memory akış. Sipariş yakalama event'leri eklendi. |
| `TwitchChatIngestor`, `YoutubeChatIngestor`, `FacebookChatScraper` | `LiveDeck.Chat/Ingestors/` | .NET 10'a uyarlandı |
| `ExtensionBridgeServer` | `LiveDeck.Chat/Bridge/` | Aynı |
| Browser Extension (`Extension/`) | `Extension/` | Manifest'te branding değişimi |
| Logging (`UniCast.Logging`) | `LiveDeck` içine kopyalandı | Aynı |
| Lisans altyapısı | Faz 4'te adapte edilir | Tier ve abonelik döngüsü eklenir |

UniCast'ten **taşınmayacaklar:** `UniCast.Encoder`, `Unicast.Capture`, `StreamController`, FFmpeg argüman üretimi, GPU compositor, frame buffer pool, hardware encoder detection — hepsi gereksiz.

### 2.3 LiveDeck.App içi mimari (WPF MVVM)

**Views:** MainView, ChatPanelView, SalesPanelView, GiveawayView, CustomerView, SettingsView, ReportView, LicenseView.

**ViewModels:** Karşılık gelen dosyalar.

**Services (singleton, DI):**
- `ActiveCodeService` — aktif kodları yönetir
- `OrderCaptureService` — chat'i dinler, kodlara göre eşleştirir
- `GiveawayService` — çekiliş başlat/bitir/çek
- `OverlayService` — `LiveDeck.Overlay` ile köprü
- `LabelService` — clipboard'a yazma + (v2) yazdırma
- `LicenseService` — abonelik kontrolü
- `HotkeyService` — global kısayollar (Stream Deck + klavye)

### 2.4 Sipariş yakalama motoru

`LiveDeck.Core/Sales/OrderCaptureEngine` — bağımsız, test edilebilir kütüphane. Fixture-based testlerle korunur.

Pipeline:
```
ChatMessage → Filter → Normalizer → CodeMatcher → VariantExtractor
            → QuantityExtractor → IntentScorer → ConfidenceScorer
            → Order veya Reject
```

Detayları Bölüm 4'te.

### 2.5 Konfigürasyon ve veri saklama

| Veri | Yer | Format |
|---|---|---|
| Uygulama ayarları | `%USERPROFILE%\Documents\LiveDeck\settings.json` | JSON |
| Yayın oturumları (siparişler, çekilişler, müşteriler) | `%USERPROFILE%\Documents\LiveDeck\data\livedeck.db` | SQLite (Dapper) |
| Loglar | `%USERPROFILE%\Documents\LiveDeck\Logs\log-{date}.txt` | Serilog |
| Lisans | `%LOCALAPPDATA%\LiveDeck\License\license.dat` | AES şifreli |
| Anti-piracy state | `%PROGRAMDATA%\LiveDeck\system_state.dat` + Registry | AES şifreli |

Chat mesajları **disk'e yazılmaz**, sadece RAM'de akar (overlay reconnect snapshot için son ~200 mesaj tutulur).

---

## 3. Çekirdek İş Akışları

### 3.1 Aktif kod yönetimi

**Veri modeli:**
```
ActiveCode {
  Id, SessionId, Code, Sizes (JSON), Price,
  ImageUrl?, Aliases (JSON)?, StartedAt, EndedAt?
}
```

**UX — operatör (PC):**
- "Yeni Kod Ekle" modal: Kod, bedenler (chip input), fiyat, opsiyonel ürün fotoğrafı
- Aktif kodlar kart görünümü: kod, bedenler, fiyat, süre, sipariş sayısı
- Kart tek tık → düzenle modal'ı

**UX — yayıncı (hızlı kontrol):**
- **Mini overlay paneli** — şeffaf, sürüklenebilir, yayıncının kendi ekranında (OBS yayınında değil)
- Aktif kodlar listesi, ±fiyat butonları
- Stream Deck tuş atama, F1-F8 ile hızlı seçim
- Klavye kısayolları: Ctrl+Shift+N yeni kod, Ctrl+↑/↓ fiyat değişimi

**Edge case'ler:**
- Aynı kod iki kez: uyarı + "var olanı düzenle"
- Geriye dönük fiyat: "Daha önceki 5 sipariş 199 TL'de. Yeni fiyata güncellensin mi?" — varsayılan EVET
- Kod kapatma: o koddaki yarım kalmış siparişler "Yeniden değerlendir" durumuna düşer

### 3.2 Sipariş yakalama akışı

**Pipeline detayı:**

1. **Filtre** — sistem mesajı, bot, "join" event'leri at
2. **Normalize** — Türkçe diakritikler (ı/i/I/İ), büyük-küçük, fazla boşluk, emoji temizle
   - "Mavıı  XL  aldıım" → "MAVI XL ALDIM"
3. **CodeMatcher** — aktif kod listesi vs. mesaj, fuzzy match (Levenshtein ≤ 1)
4. **VariantExtractor** — bedenler (`S`, `M`, `XL`, `36`, `tek beden`)
5. **QuantityExtractor** — `2 tane`, `iki adet`, `x2`, `+2`, `ikişer`
6. **IntentScorer** — niyet kelimeleri ("aldım", "olsun", "istiyorum", "alıyorum", "🌹", "🛒"), soru tonu skoru düşürür
7. **ConfidenceScorer** — 0-100 skor

**Confidence eşikleri:**
- ≥ 80 → otomatik kuyruğa
- 50-79 → "Onay Bekliyor" sekmesi
- < 50 → reddet (logla)

**Sipariş kuyruğu paneli:** Tablo görünümü; sekmeler: Yeni / Onay Bekliyor / DM Atıldı / Ödendi / Kargoya / Tamamlandı / İptal. Sağ tıklama menüsü, çoklu seçim toplu işlem.

### 3.3 Etiket akışı (v1 — clipboard otomasyonu)

**Tetikleyici:** Sipariş satırını seç → F9 (veya sağ tık → "Etiket için kopyala")

**Format:**
```
@kullanıcı_adı YORUM_METNİ
```

**Universal yaklaşım (her etiket uygulamasıyla çalışır):**
- LiveDeck clipboard'a `@username YORUM` formatında yazar
- Clipboard'u izleyen herhangi bir etiket uygulaması (mevcut etiket.exe dahil) bunu otomatik yakalar
- Kullanıcı kendi etiket uygulamasında "Yazdır" basar

**Mevcut etiket.exe için ek entegrasyon (opsiyonel):**
- Settings'te "etiket.exe penceresine fiyatı otomatik yaz" bayrağı açıkken:
  - LiveDeck pencereyi bulur (`FindWindow`)
  - textBox1'e siparişin fiyatını yazar (UI Automation)
  - Sonra clipboard'a yorum formatını yazar
- Bayrak kapalıyken sadece clipboard yazılır, kullanıcı fiyatı kendisi girer
- Bu özellik etiket.exe'nin Form2 yapısına bağlı, başka etiket uygulamalarıyla çalışmaz; bu yüzden opsiyonel

**Toplu mod:** N sipariş seç → F9 → 200ms aralıkla sırayla clipboard'a yazılır.

**v2 (gelecek):** LiveDeck içine entegre WYSIWYG etiket şablon tasarımcısı + doğrudan termal yazdırma. Mevcut etiket.exe gereksizleşir. Yazdırma motor: `System.Drawing.Printing.PrintDocument` — yazıcı bağımsız (Windows driver olan her cihaz).

### 3.4 Çekiliş akışı

**Kurulum modal'ı:**
- Anahtar kelime/emoji ("🌹", "KATIL", regex)
- Süre (30s / 60s / 2dk / 5dk / manuel)
- Kazanan sayısı (1 / 3 / 5 / N)
- Platform filtresi
- Tekrar kazanma (varsayılan: hayır)
- Ödül adı (overlay'de gösterilir)

**Çalışma:**
- Mesajları toplar (her platformdan)
- Aynı kullanıcı çekilişte yalnız bir kez (`(platform, username)` tekil)
- OBS overlay'de canlı sayaç + geri sayım + ödül

**Sonuç:**
- "Çek" butonu → rulet animasyonu (3-5 saniye) → kazanan büyük gösterilir + konfeti
- Kazanan otomatik müşteri kartına eklenir
- Geçmiş kazananlar bir sonraki çekilişte filtrelenir

**Adillik:** `RandomNumberGenerator` (cryptographically secure), seed + katılımcı listesi (anonim hash) + kazanan loglanır. Şüpheli durumda "Çekiliş kayıtları" ekranı.

**Edge cases:**
- 0 katılımcı → "Katılımcı yok", iptal
- 1 katılımcı 3 kazanan istendi → 1 kazanan seçilir, otomatik mesaj

### 3.5 Yayın oturumu yaşam döngüsü

- **Başlat:** "Yeni Yayın" → modal: başlık, hedef platformlar → tüm ingestor'lar başlar → kuyruk boş başlar → OBS overlay URL'leri gösterilir
- **Sırasında:** Sürekli chat akışı, sipariş yakalama, kod yönetimi, çekiliş, müşteri kayıt güncelleme
- **Bitir:** "Yayını Bitir" → ingestor'lar durur → aktif kodlar kapanır → otomatik Excel raporu üretilir (`Documents/LiveDeck/Reports/{YYYY-MM-DD-HHMM}.xlsx`)

**Rapor içeriği:** Toplam sipariş, ciro (potansiyel/ödenen), en çok satan kod, en çok alan müşteri, çekilişler, platform dağılımı, sipariş başına ortalama süre.

### 3.6 Müşteri yaşam döngüsü (Level 2 — username bazlı)

**Otomatik kayıt:** Sipariş yakalandığında `(Platform, Username)` tek anahtarıyla Customer otomatik oluşturulur. Anonim — sadece username, telefon/ad/adres yok.

**Customer kartında ne var:**
- Username, Platform, FirstSeen, LastSeen
- TotalOrders, CompletedOrders, CancelledOrders
- TrustScore (`CompletedOrders / TotalOrders × 100`)
- IsBlacklisted + reason
- Notes (manuel)

**Kara liste etkisi:**
- Yeni siparişler otomatik kuyruğa girmez, "Onay Bekliyor"a düşer
- Operatör panelde 🚫 işaretiyle gösterilir
- OBS overlay'inde mesajları gizlenebilir (ayar)

**WhatsApp / telefon / adres yönetimi LiveDeck'in dışındadır** — kullanıcı bu işi mobil WhatsApp Business uygulamasıyla kendi yapar.

---

## 4. OBS Overlay Teslim Mekanizması

### 4.1 Sunucu kurulumu

`LiveDeck.Overlay` projesinde — ASP.NET Core 10 minimal API + statik dosya servisi + WebSocket.

**Yaşam döngüsü:** LiveDeck.App ile birlikte ayağa kalkar/iner.

**Bağlanma:** Yalnız localhost. Dış ağdan erişilemez (güvenlik).

**Port:** Varsayılan `4747`. Doluysa otomatik 4748, 4749... Seçilen port settings'e yazılır, ana panelde "OBS URL" kısmında gösterilir.

**HTTPS:** Gerekmez. OBS CEF tarayıcısı localhost HTTP ile sorunsuz çalışır.

### 4.2 Mevcut overlay'ler

| URL | Ne yapar | Tipik OBS boyutu |
|---|---|---|
| `http://localhost:4747/overlay/chat` | Birleşik chat akışı | 500 × 800 |
| `http://localhost:4747/overlay/giveaway` | Çekiliş overlay'i (sayaç, geri sayım, rulet) | 1280 × 720 veya 600 × 400 |
| `http://localhost:4747/overlay/orders` | Yeni sipariş bildirim toast'ı | 400 × 150 (köşede) |
| `http://localhost:4747/overlay/active-codes` | Aktif kodlar kart görünümü | 320 × 240 (köşede) |
| `http://localhost:4747/overlay/sales-counter` | Yayın boyu sipariş + ciro sayacı | 300 × 80 |

URL parametreleriyle özelleştirme: `?theme=neon&fontSize=20&platformIcons=1`.

### 4.3 İletişim protokolü

**WebSocket endpoint:** `ws://localhost:4747/ws/{overlay-tipi}`

**Event tipleri:**

```json
// chat
{ "type": "chat.message", "data": {
    "id", "platform", "username", "displayName", "avatarUrl",
    "text", "timestamp", "badges": []
}}

// orders
{ "type": "order.captured", "data": {
    "platform", "username", "code", "size", "quantity", "price"
}}

// giveaway
{ "type": "giveaway.start", "data": { "keyword", "duration", "prize" }}
{ "type": "giveaway.tick", "data": { "remaining", "participantCount" }}
{ "type": "giveaway.draw", "data": { "winners": [], "participants" }}

// active codes
{ "type": "code.added"      | "code.priceChanged" | "code.removed", "data": {...} }
```

**Bağlantı kopması:** Otomatik yeniden bağlanma (exponential backoff: 1s, 2s, 4s, max 10s). LiveDeck restart edildiğinde overlay'ler otomatik geri gelir.

**State recovery:** Bağlantı kurulunca server önce snapshot atar:
```json
{ "type": "state.snapshot", "data": {
    "activeCodes": [...],
    "recentMessages": [...son 200],
    "activeGiveaway": null veya {...}
}}
```

### 4.4 Tema sistemi

**5 varsayılan tema:** Minimal, Card, Neon, Compact, Bubble.

URL parametreleriyle ayarlanır. Settings'te WYSIWYG önizleme + URL üreteci. Tema dosyaları statik CSS (`Overlay/wwwroot/themes/{name}/style.css`).

**Filigran:** Trial sürümünde küçük "LiveDeck" logosu altta (viral pazarlama). Ücretli sürümde kapatılır.

### 4.5 Sipariş bildirim toast'ı

Yeni sipariş yakalandığında 3-5 sn'lik toast (sosyal kanıt etkisi):
```
📦  Ayşe MAVİ M aldı  🛍 199 TL
```
Konfigürasyon: süre, konum, animasyon, eşik tutar (büyük tutarlarda ekstra şovlu), ad maskeleme.

### 4.6 OBS yapılandırma yardımı

- Settings → OBS Overlay sekmesinde her URL + önerilen boyut + "Kopyala" butonu
- "OBS Test Et" — yerleşik tarayıcıda önizleme
- İlk açılışta wizard: adım adım OBS Browser Source ekleme

### 4.7 Performans hedefleri

- WebSocket payload ~200-500 byte/mesaj
- 100 mesaj/dakika rahat
- Overlay'de max 50 mesaj DOM'da, eskiler temizlenir
- 10+ saatlik yayında bellek sızıntısı testi (faz 1 acceptance criteria)

---

## 5. Veri Modeli (SQLite)

### 5.1 Tablolar (6 ana + Settings KV)

```
StreamSession        — yayın oturumu
ActiveCode           — yayında satılan kodlar
OrderItem            — yakalanan siparişler
Customer             — username bazlı müşteri (Level 2)
Giveaway             — çekilişler
GiveawayParticipant  — çekiliş katılımcıları
Settings             — key-value ayarlar
```

ChatMessage tablosu **yok** — mesajlar disk'e yazılmaz, sadece in-memory akar.

### 5.2 Tablo tanımları

**StreamSession:**
| Sütun | Tip | Açıklama |
|---|---|---|
| Id | TEXT (UUID) | PK |
| Title | TEXT | |
| StartedAt | INTEGER (unix sec) | |
| EndedAt | INTEGER? | null = aktif |
| Platforms | TEXT | JSON dizi |
| Notes | TEXT? | |

İndeks: `StartedAt DESC`

**ActiveCode:**
| Sütun | Tip | Açıklama |
|---|---|---|
| Id | TEXT | PK |
| SessionId | TEXT | FK |
| Code | TEXT | "MAVI" |
| Sizes | TEXT | JSON: `["S","M","XL"]` |
| Price | REAL | |
| ImageUrl | TEXT? | |
| Aliases | TEXT? | JSON |
| StartedAt | INTEGER | |
| EndedAt | INTEGER? | |

İndeks: `(SessionId, Code)`, `(SessionId, EndedAt)`

**OrderItem:**
| Sütun | Tip | Açıklama |
|---|---|---|
| Id | TEXT | PK |
| SessionId | TEXT | FK |
| ActiveCodeId | TEXT | FK |
| CustomerId | TEXT | FK |
| Code | TEXT | snapshot |
| Size | TEXT | |
| Quantity | INTEGER | |
| UnitPrice | REAL | snapshot |
| TotalPrice | REAL | computed |
| Confidence | INTEGER | 0-100 |
| Status | TEXT | new / pending / dm_sent / paid / shipped / completed / cancelled |
| OriginalMessageText | TEXT | siparişi yaratan ham yorum (etiket için) |
| CapturedAt | INTEGER | |
| StatusUpdatedAt | INTEGER | |
| LabelPrintedAt | INTEGER? | |
| Notes | TEXT? | |

İndeks: `(SessionId, Status, CapturedAt DESC)`, `(CustomerId)`

Kritik: `Code`, `UnitPrice` snapshot olarak yazılır — aktif kod silinse/değişse bile geçmiş bozulmaz.

**Customer (Level 2 — sadeleşmiş):**
| Sütun | Tip | Açıklama |
|---|---|---|
| Id | TEXT | PK |
| Platform | TEXT | |
| Username | TEXT | |
| DisplayName | TEXT? | son görülen |
| AvatarUrl | TEXT? | |
| FirstSeenAt | INTEGER | |
| LastSeenAt | INTEGER | |
| TotalOrders | INTEGER | |
| CompletedOrders | INTEGER | |
| CancelledOrders | INTEGER | |
| TrustScore | INTEGER | 0-100 |
| IsBlacklisted | INTEGER (0/1) | |
| BlacklistReason | TEXT? | |
| Notes | TEXT? | |

İndeks: `UNIQUE(Platform, Username)`, `(IsBlacklisted)`, `(TrustScore DESC)`

**Telefon, ad, adres alanları yok** — kullanıcı bunları WhatsApp'ında kendi yönetir.

**Giveaway:**
| Sütun | Tip | Açıklama |
|---|---|---|
| Id | TEXT | PK |
| SessionId | TEXT | FK |
| Keyword | TEXT | |
| Prize | TEXT? | |
| WinnerCount | INTEGER | |
| PlatformFilter | TEXT? | JSON |
| PreventRewinning | INTEGER (0/1) | |
| StartedAt, EndedAt?, DrawnAt? | INTEGER | |
| RandomSeed | TEXT | adillik için |

**GiveawayParticipant:**
| Sütun | Tip | Açıklama |
|---|---|---|
| Id | TEXT | PK |
| GiveawayId | TEXT | FK |
| CustomerId | TEXT | FK |
| Platform, Username | TEXT | |
| EnteredAt | INTEGER | |
| IsWinner | INTEGER (0/1) | |

İndeks: `(GiveawayId, IsWinner)`, `(CustomerId, IsWinner)`

**Settings (key-value):**
| Key | Value (JSON/TEXT) |
|---|---|
| `obs.port` | `4747` |
| `obs.theme.chat` | `"neon"` |
| `parser.confidence.high` | `80` |
| `hotkey.captureOrder` | `"F9"` |
| `tier` | `"yearly"` |
| ... | ... |

### 5.3 Migrasyonlar

`Migrations/` klasöründe sıralı SQL dosyaları (`001_initial.sql`, `002_*.sql` vs.). Uygulama açılışta `_meta` tablosundan kontrol eder, eksikleri çalıştırır. EF Core kullanılmaz — Dapper + raw SQL.

### 5.4 Yedekleme

- Yayın bitince otomatik kopya: `Documents/LiveDeck/Backups/livedeck-{timestamp}.db`
- Son 30 yedek tutulur
- Settings'te manuel "Şimdi yedekle" + "Yedeği geri yükle"

### 5.5 Veri büyüme tahmini

Yayın başına ~1 MB (chat persistence yok); yıllık 200 yayın ~200 MB. SQLite bu boyutta zorlanmaz.

---

## 6. Lisans ve Abonelik

### 6.1 Lisans anahtar formatı

```
LD-XXXX-XXXX-XXXX-XXXX-XXXX
```

20 karakter, 5 grup. E-posta ile gönderilir, ilk açılışta yapıştırılır.

### 6.2 Hardware fingerprint

CPU ID + Anakart Seri No + Disk Volume ID + Windows Install Date hash'i.

Eşleşme kuralı (sunucuda): tam hash eşleşmesi VEYA bileşen similarity ≥ 75% → "aynı makine".

### 6.3 Yerel lisans dosyası

`%LOCALAPPDATA%\LiveDeck\License\license.dat` — AES şifreli. İçerik: anahtar, fingerprint, son doğrulama tarihi, abonelik bitiş tarihi.

### 6.4 Online doğrulama + offline grace

- Açılışta + 24 saatte bir online validation
- Sunucuya ulaşılamadıysa: son başarılı doğrulamadan **14 gün grace period**
- 14 günden fazla offline → "İnternete bağlan, lisans yenilenmeli"
- Yayın esnasında ekstra istek atılmaz

### 6.5 Lisans sunucusu (LiveDeck.LicenseServer)

ASP.NET Core REST API, ayrı VPS host (örn. `https://license.livedeck.app/api/v1`).

Endpoint'ler:
```
POST /api/v1/validate     — lisans + fingerprint kontrolü
POST /api/v1/activate     — yeni makine kayıt
POST /api/v1/deactivate   — makine ayır
POST /api/v1/heartbeat    — periyodik ping (opsiyonel metrik)
GET  /api/v1/status       — admin için
POST /api/v1/trial/start  — yeni deneme başlat / mevcut bilgiyi döndür
```

Veritabanı: PostgreSQL. Tablolar: `licenses`, `activations`, `customers`, `payments`, `trials`, `audit_log`.

Admin paneli (basit web UI): müşteri ekle, lisans oluştur, ödeme onayla, lisans uzat/iptal et, aktivasyonları görüntüle, MRR/expiring soon listeleri.

### 6.6 Trial mode (14 gün, Instagram-only)

**Kısıtlar:**
- Yalnız Instagram ingestor aktif → sadece Instagram'dan chat akar, sipariş yakalanır, çekiliş katılımcısı toplanır
- TikTok/Facebook/YouTube/Twitch/Kick UI'da görünür ama "Aboneliğin gerekiyor" rozetiyle kilitli; kullanıcı bağlantı kuramaz
- Diğer tüm özellikler (Instagram'dan gelen veriyle: sipariş yakalama, çekiliş, OBS overlay, etiket otomasyonu, raporlar) tam çalışır
- Süre dolduğunda salt-okunur mod

**Kayıt için:** Sadece e-posta + telefon, kart yok.

**Yeniden kurulumda sıfırlanma koruması (3 katmanlı):**

1. **Sunucu fingerprint binding** — `POST /trial/start` → sunucu eşleşme bulursa mevcut deneme bilgisini döner (kalan gün korunur)
2. **Yerel gizli artefakt** (uninstall'a dirençli):
   - `%PROGRAMDATA%\LiveDeck\system_state.dat` (encrypted)
   - Registry: `HKLM\Software\LiveDeck\State` (encrypted blob)
   - Custom uninstaller bu artefaktları temizlemez
3. **Eşleşme genişliği** — sunucuda fingerprint similarity ≥ 75% VEYA email VEYA phone eşleşirse "mevcut deneme" sayılır

**Durdurulan saldırı vektörleri:**
- Casual reinstall ile sıfırlama: durur
- Disk format + reinstall: durur (sunucu fingerprint hatırlar)
- Aynı kişi farklı e-posta: telefon yakalar
- Aynı kişi farklı telefon: fingerprint yakalar

**Kabul edilen:** Tamamen yeni bilgisayar + yeni e-posta + yeni telefon → yeni müşteri sayılır.

KVKK uyumu için EULA'da açıkça belirtilir: "Deneme suiistimalini önlemek için anonim donanım fingerprint'i ve kurulum durumu sunucuda saklanır."

### 6.7 Yenileme akışı

- 30 gün önce: açılışta uyarı + e-posta
- 14 gün önce: app içi banner + e-posta hatırlatma
- Bitiş günü: salt-okunur mod (geçmiş erişim açık, yeni yayın yok). Yenileme onaylanınca otomatik açılır.

### 6.8 Ödeme akışı (v1 — manuel)

- Banka havalesi/EFT (B2B premium yazılım için yaygın) veya iyzico kredi kartı
- Müşteri öder → admin panelinden onaylanır → lisans key e-postası
- v2'de iyzico subscription billing ile otomatikleşir

### 6.9 Anti-piracy felsefesi

- Donanım fingerprint binding
- AES şifreli lisans dosyası
- 14 günde bir online doğrulama
- Versiyon imzalama
- Hafif anti-debugger flag

**Yapılmayan:** Aşırı obfuscation, DRM rootkit, sürekli online şart.

Felsefe: amatör korsanı yavaşlat, profesyonel cracker'a karşı kale yapma. Hedef kitle (profesyonel ekipler) korsan kullanım riski düşük.

### 6.10 Geliştirici/test modu

DEBUG build'de lisans kontrolü atlanır. Yerel `dev.license.json` dosyası varsa otomatik geçerli sayılır. Release build'de bu davranış kapalı.

---

## 7. Geliştirme Aşamaları

Her faz tamamlandığında **kullanılabilir** çıktı verir.

### Faz 1 — Instagram-only çekirdek

Hedef: Geliştirici kendi Instagram yayınında LiveDeck kullanmaya başlar.

- Solution iskeleti (.NET 10, 6 proje)
- UniCast'ten ChatBus + Browser Extension + ExtensionBridgeServer taşıma
- Instagram chat akışı (extension → bridge → ChatBus)
- SQLite + Dapper + ilk migration (6 ana tablo)
- WPF MainWindow + temel navigasyon
- ActiveCode paneli (CRUD)
- OrderCaptureEngine (Türkçe normalize + fuzzy + variant + quantity + intent + confidence)
- Sipariş kuyruğu paneli (durum akışı)
- OBS chat overlay (`/overlay/chat`, varsayılan tema, WebSocket)
- Etiket clipboard otomasyonu (F9)
- TDD: OrderCaptureEngine için 50+ test case

**Acceptance criteria:** 1 saatlik canlı Instagram yayınında 30+ sipariş yakalanır, hiçbiri kaçmaz, etiketler basılır.

### Faz 2 — Çoklu platform + çekiliş

- TikTok ingestor (extension bridge)
- Facebook ingestor
- Çekiliş motoru + giveaway overlay (rulet animasyonu)
- Sipariş bildirim toast overlay
- Aktif kodlar overlay
- Yayın sonrası Excel raporu
- Settings UI (platform hesap bağlantıları, OBS port, hotkey'ler)

**Acceptance criteria:** Multi-platform yayında chat tek panelde akar, çekiliş çekilir, rapor üretilir.

### Faz 3 — Müşteri tracking + UX cilası

- Customer auto-create (Level 2)
- Customer history view
- TrustScore hesaplama
- Kara liste (UI + overlay/order capture'a etki)
- Yayıncı mini overlay paneli (sürüklenebilir)
- Stream Deck SDK entegrasyonu
- Klavye kısayolları
- Sipariş kuyruğunda toplu işlemler

### Faz 4 — Lisans sistemi

- LiveDeck.LicenseServer (ASP.NET Core + PostgreSQL + admin web UI)
- Hardware fingerprint hesaplama
- LiveDeck.Licensing modülü (validation, offline grace, deactivate)
- Trial mode (Instagram-only, 14 gün, anti-reset)
- Lisans bitince salt-okunur mod
- Yenileme uyarıları + e-posta otomatik

### Faz 5 — Sat-hazır cila

- İlk açılış wizard'ı
- 5 OBS chat teması
- Auto-update sistemi
- Inno Setup installer
- Crash reporter + opsiyonel telemetry
- App içi yardım/SSS
- Pazarlama landing page
- Demo video
- E-posta şablonları

### Faz 6+ (gelecek — v2/v3)

- Doğrudan termal yazdırma + WYSIWYG etiket şablon tasarımcısı (etiket.exe gereksizleşir)
- YouTube, Twitch, Kick ingestor'ları
- Çoklu operatör (Pro tier)
- Beyaz etiket / ajans modu
- iyzico/PayTR payment links
- Audit log (B2B kurumsal)
- Cloud DB yedekleme

### Test stratejisi

- TDD (özellikle OrderCaptureEngine)
- Fixture-based testler (gerçek Türkçe chat örneklerinden anonimleştirilmiş)
- Integration testler (chat → order capture → DB)
- Manuel QA: her faz sonunda dogfood, eksikler not edilir
- Stack: xUnit + Moq + FluentAssertions

---

## 8. Açık Kararlar / Ileride Netleşecek

- v2'de WYSIWYG etiket şablon tasarımcısı UI detayları (faz 6 başlangıcında detay tasarım)
- Pro tier yapısı (faz 6+'da müşteri feedback'iyle netleşir)
- Cloud yedekleme detayı (Google Drive vs. OneDrive vs. kendi storage)
- Stream Deck plugin manifest formatı (faz 3'te detay)

---

## 9. Risk ve Varsayımlar

**Riskler:**
- Instagram/TikTok DOM değişiklikleri → extension content script'leri kırılır → sürekli bakım gerekir. Bu zaten ürünün abonelik gerekçesi.
- Türkçe parsing edge case'ler — gerçek yayın verisiyle iteratif iyileştirme şart.
- Trial reset bypass — VM + farklı kimlik kullananlar geçer, kabul edilen sınır.

**Varsayımlar:**
- UniCast'in mevcut Browser Extension content script'leri Instagram, TikTok, Facebook için çalışıyor durumda
- Kullanıcının mevcut etiket.exe uygulaması manuel kullanım için çalışıyor (LiveDeck dokunmaz, sadece clipboard ile besler)
- Geliştirici ürünü kendi yayınlarında dogfood edebilir (sürekli feedback döngüsü)

---

## Onay

Tasarım sahibi: [kullanıcı]
Onaylanma tarihi: [bekleniyor]

Onay sonrası `writing-plans` skill'i ile faz faz implementasyon planı yazılır.
