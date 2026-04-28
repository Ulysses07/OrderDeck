# LiveDeck Phase 2a — Multi-Platform + Admin UX Design

**Tarih:** 2026-04-28
**Durum:** Onay bekleniyor
**Önceki:** P1b commit `5a9f05b` (manual label workflow, 39/39 tests, smoke OK)
**Sonraki:** Faz 2b (Çekiliş motoru — ayrı spec)

---

## 1. Vizyon ve Kapsam

### 1.1 Tek cümlelik tanım

LiveDeck'i Instagram-only'den çoklu platforma açar (TikTok dahil) ve ⋮ menüden ulaşılan üç admin paneli ekler: Ayarlar, Yayın Geçmişi, Kara Liste.

### 1.2 Faz 2a'ya dahil 4 madde

| Madde | Yapısı |
|---|---|
| **TikTok ingestor** | Browser extension'a content script + manifest güncellemesi. C# tarafında `InstagramIngestor` → `ChatBridgeIngestor` olarak yeniden adlandırılır (tek class, tüm platformları taşır). |
| **Settings UI** | ⋮ menüden açılan tabbed modal. Yazıcı + OBS tab'ları. Test etiket basma butonu. |
| **Yayın Geçmişi UI** | ⋮ menüden açılan liste dialog'u. Çift tıkla → mevcut `StreamReportDialog` ile o yayının raporu açılır. |
| **Kara Liste UI + davranış** | `Customer.IsBlacklisted` artık işlevsel: kırmızı vurgu, ekleme/yönetim UI'ı. |

### 1.3 Faz 2a'da olmayanlar (Faz 2b veya Faz 3)

- **Çekiliş motoru** — ayrı spec (Faz 2b)
- **Facebook ingestor** — Faz 3 veya iptal
- **Stream Deck entegrasyonu** — Faz 3
- **Etiket şablon tasarımcısı (WYSIWYG)** — Faz 3
- **"Etiket basıldı" toast overlay** — gereksiz, iptal edildi

### 1.4 Hedef kitle

P1b ile aynı: Türk canlı satış yayıncıları. Çoğunluk Instagram, ama TikTok kritik diferansiyel — bu fazın ana satış argümanı.

---

## 2. Mimari Genel Görünüm

### 2.1 Dosya değişiklikleri özet

```
Extension/
├── manifest.json                       # MODIFIED: tiktok.com host_permissions + content_scripts
├── content-tiktok.js                   # NEW: UniCast'ten port + flat-format adaptation
└── popup.html                          # MODIFIED: "Instagram Live" → "Instagram + TikTok Live"

LiveDeck.Core/
├── Customers/Customer.cs               # MODIFIED: + long? BlacklistedAt
├── Customers/CustomerService.cs        # MODIFIED: + AddToBlacklist / RemoveFromBlacklist methods
└── Storage/
    ├── Migrations/003_blacklist_timestamp.sql   # NEW
    └── Repositories/CustomerRepository.cs       # MODIFIED: + UpdateBlacklist; + GetBlacklisted; + GetAllForHistory
    └── Repositories/SessionRepository.cs        # MODIFIED: + GetAllEnded(int limit)

LiveDeck.Chat/
└── Ingestors/
    ├── InstagramIngestor.cs            # DELETED (replaced by ChatBridgeIngestor)
    └── ChatBridgeIngestor.cs           # NEW: platform-agnostic bridge orchestrator

LiveDeck.App/
├── Converters/
│   ├── BoolToVisibilityConverter.cs    # NEW: for blacklist red-highlight binding
│   └── PlatformIconConverter.cs        # NEW: platform string → image source
├── ViewModels/
│   ├── MainShellViewModel.cs           # MODIFIED: + 3 menu commands; + blacklist filtering
│   ├── ChatMessageViewModel.cs         # NEW: wrap ChatMessage with IsBlacklisted lookup
│   ├── LabelViewModel.cs               # NEW: wrap Label with IsBlacklisted lookup (queue panel)
│   ├── SettingsViewModel.cs            # NEW
│   ├── StreamHistoryViewModel.cs       # NEW
│   └── BlacklistViewModel.cs           # NEW
├── Views/
│   ├── MainShellView.xaml              # MODIFIED: ⋮ button + dropdown; chat row template (red highlight)
│   ├── SettingsDialog.xaml + .cs       # NEW
│   ├── StreamHistoryDialog.xaml + .cs  # NEW
│   └── BlacklistDialog.xaml + .cs      # NEW
└── AppHost.cs                          # MODIFIED: register new VMs/dialogs/services
```

### 2.2 Bağımlılık akışı (değişmedi)

P1b ile aynı: `App → Core/Chat/Overlay/Labeling`, `Chat/Overlay/Labeling → Core`.

---

## 3. Özellik Tasarımları

### 3.1 TikTok ingestor

**Browser extension tarafı:**
- `content-tiktok.js` UniCast extension'ından kopyalanır (`C:/Users/burak/Downloads/UniCast/UniCast/Extension/content-tiktok.js`).
- UniCast'in **nested** payload formatı (`{type:'comment', data:{...}}`) → P1b'deki `ExtensionMessage` flat formatına dönüştürülür (Instagram'da yapılan dönüşümün TikTok'a uyarlaması).
- WebSocket: `ws://localhost:4748/extension`, `platform: "tiktok"`.
- `manifest.json`'a eklenenler:
  - `host_permissions`: `*://*.tiktok.com/*`
  - `content_scripts`: `{ matches: ["*://*.tiktok.com/*"], js: ["content-tiktok.js"], run_at: "document_idle" }`
- `popup.html` metni: "Instagram + TikTok Live chat'i LiveDeck'e iletir."

**C# tarafı:**
- Mevcut `InstagramIngestor` aslında platform-agnostik bir wrapper (sadece bridge'i start/stop ediyor). Bu yanıltıcı isim → **`ChatBridgeIngestor`** olarak yeniden adlandırılır.
- `Platform` property kaldırılır (tek ingestor birden çok platformu temsil ediyor; platform marker'ı ChatMessage'da zaten var).
- `ExtensionBridgeServer` zaten her platformu kabul ediyor — değişiklik yok.

**UI tarafı:**
- TikTok mesajları otomatik olarak sol "Canlı Chat" panelinde görünür.
- Platform sütunu kolonu zaten var (`{Binding Platform}`). TikTok için renk farkı: yeni `PlatformIconConverter` ile platform string'i bir SolidColorBrush'a map'lenir (instagram = pembe gradient, tiktok = siyah-pembe-mavi). v1 sadelik için renk değil, **küçük emoji/işaret** kullanır: `📷` Instagram, `🎵` TikTok.

### 3.2 Settings UI

**Erişim:** ⋮ menüden "Ayarlar" → modal `SettingsDialog`.

**Layout:** Tabbed (TabControl), 2 tab: **Yazıcı** ve **OBS**.

**Tab 1 — Yazıcı:**

| Field | Kontrol | Validasyon |
|---|---|---|
| Yazıcı | ComboBox (kurulu yazıcılar listesi `PrinterSettings.InstalledPrinters` + en üstte "Windows varsayılanı" — null'a karşılık gelir) | — |
| Etiket genişliği (mm) | NumericUpDown | 10-200 |
| Etiket yüksekliği (mm) | NumericUpDown | 10-200 |
| Etiket aralığı (mm) | NumericUpDown | 0-50 |
| Yazı tipi | ComboBox (sistem fontları, `InstalledFontCollection.Families`) | — |
| Kullanıcı font boyutu (pt) | NumericUpDown | 6-72 |
| Mesaj font boyutu (pt) | NumericUpDown | 6-72 |
| **[Test Etiketi Bas]** | Button | — |

**Tab 2 — OBS:**

| Field | Kontrol | Validasyon | Not |
|---|---|---|---|
| Overlay portu | NumericUpDown | 1024-65535 | Değiştirilirse "Yeniden başlatma gerekir" uyarısı (Kaydet anında) |
| Chat teması | ComboBox (şu an "minimal") | — | İleri fazlarda genişler |

**Save davranışı:**
- Auto-save **yok**. Explicit "Kaydet" / "İptal" butonları altta.
- Validation hatası varsa Kaydet pasif.
- Kaydet → `SettingsStore.Save(...)` → DI'da AppSettings instance'ını yeni snapshot ile değiştir → dialog kapanır.
- OverlayPort değişti ise: bilgi kutusu "Bu değişiklik için uygulamayı yeniden başlatman gerekir. Şimdi kapat?" — Evet/Hayır seçimi.
- Test Etiketi Bas: form'daki **mevcut (henüz kaydedilmemiş)** ayarlarla geçici bir `LabelPrintDocument.Build` çağrısı, sample veri (`@test / Test mesajı / 100 TL`).

**Dead settings temizliği:**
- `AppSettings.ParserHighConfidence` ve `AppSettings.ParserLowConfidence` kaldırılır (P1b'de auto-capture engine yok, dead code).

**AppSettings DI yenilenmesi:**
- AppSettings şu an singleton (boot zamanında SettingsStore.Load()'tan gelen snapshot). Settings dialog kaydederken DI'daki instance'ı değiştirmek zor — yerine `SettingsStore.Save` çağrılır + bellek içi `AppSettings` instance'ının property'leri tek tek güncellenir (mutable POCO zaten, `class` olduğu için aynı reference).
- `LabelPrinter` ve `OverlayHost` AppSettings instance'ı tutuyor — properties anlık değişir, restart gerekmez (port hariç).

### 3.3 Yayın Geçmişi UI

**Erişim:** ⋮ menüden "Yayın Geçmişi" → modal `StreamHistoryDialog`.

**Görünüm:** DataGrid, sütunlar:

| Sütun | Veri kaynağı |
|---|---|
| Tarih | `StreamSession.StartedAt` (UNIX → `yyyy-MM-dd HH:mm`) |
| Süre | `EndedAt - StartedAt` saniye → "2 saat 14 dakika" formatı |
| Etiket | `LabelRepository.GetSessionTotals(id).PrintedCount` |
| Ciro | `LabelRepository.GetSessionTotals(id).TotalAmount` (`{0:N2} TL`) |
| Platform | `StreamSession.Platforms` JSON dizisi (örn. "instagram, tiktok") |

**Yükleme:** dialog açılınca `SessionRepository.GetAllEnded(limit: 365)` çağrılır → her satır için `LabelRepository.GetSessionTotals(id)` çağrılır. ~365 ayrı sorgu olur, basit (200 satır × 5ms = 1 saniye altı). Optimization: tek SQL'de JOIN ile tüm totals'ı bir seferde getirmek mümkün → ileride performans sorunu olursa.

**Sıralama:** varsayılan en yeni üstte (`StartedAt DESC`). Kolon başlığına tıkla → DataGrid built-in sort.

**Çift tıkla → rapor aç:** `StreamReportDialog` zaten parametreli (`LoadReport(sessionId)`). DI'dan `StreamReportDialog` resolve edip yüklenir.

**Yalnız bitirilmiş yayınlar:** `EndedAt IS NOT NULL` filtresi. Aktif yayın varsa listede görünmez (zaten ekranda yapıyor).

**Sil/düzenle yok.** Veri korunur.

### 3.4 Kara Liste UI + davranış

**Davranış (kararlaşmış):** Görünür ama kırmızı vurgulu. Kullanıcı yine de çift tıklayıp kuyruğa ekleyebilir (`C` seçeneği).

**Sol "Canlı Chat" paneli:**
- Customer.IsBlacklisted true ise satır arka planı kırmızımsı (`#80FF6666` yarı şeffaf).
- ChatMessage'a Customer lookup zaten async olabilir — basit yaklaşım: ChatMessage geldiğinde MainShellViewModel `CustomerService.GetOrCreate` ile müşteriyi alır + IsBlacklisted bilgisini bir `ObservableCollection<ChatMessageViewModel>`'da tutar. ChatMessageViewModel sadece UI temsili — hafif.
- Alternatif basit: `IsSenderBlacklisted` özelliğini ChatMessage'a koymak (kirli, domain'e UI bilgisi karışır).
- **Önerilen:** `ChatMessageViewModel` (App'te, küçük wrapper) — wraps `ChatMessage` + lookup'tan gelen `IsBlacklisted` ve cached `Customer` reference. UI ViewModelleri için zaten doğru yer.

**Yazdırılacak Etiketler paneli:**
- Aynı kırmızı vurgu (`Label`'a karşılık gelen `Customer.IsBlacklisted` bakılır).
- Ekleyebilirsin (kararlaşmış C), ama görsel uyarı belirgin.

**Sağ tık menüleri (sol panel + sağ panel):**
- "Kara Listeye Al..." → küçük popover dialog (`AddToBlacklistDialog`):
  - Kullanıcı: (önceden dolu, salt okunur)
  - Sebep (opsiyonel): tek satır TextBox
  - [İptal] [Ekle]
- "Kara Listeden Çıkar" (zaten kara listedeyse): tek tık + onay → `CustomerService.RemoveFromBlacklist(customerId)`.

**Yönetim dialog'u (`BlacklistDialog`):**
- ⋮ menüden "Kara Liste" → açılır.
- Üstte sayaç: "Toplam: N kullanıcı".
- DataGrid, sütunlar: Platform / Kullanıcı / Sebep / Eklenme tarihi.
- Sıralama: en yeni üstte (`BlacklistedAt DESC`).
- Sağ tık → "Kara listeden çıkar" (onay sorulur).
- "[+ Manuel Ekle]" butonu → `AddToBlacklistDialog` (Platform dropdown + Kullanıcı + Sebep). Mevcut Customer yoksa create edilir, sonra IsBlacklisted=true.
- Manuel ekle yapılınca: aynı `(Platform, Username)` zaten varsa update, yoksa insert + update.

**Yeni schema kolonu:** `Customer.BlacklistedAt INTEGER NULL` — kara listeye eklenme zamanı. Migration `003_blacklist_timestamp.sql`.

**CustomerService API'leri (yeni):**
- `void AddToBlacklist(string customerId, string? reason)`
- `void RemoveFromBlacklist(string customerId)`
- `Customer EnsureBlacklistedManual(string platform, string username, string? reason)` — yoksa create + add to list

**CustomerRepository (yeni):**
- `void UpdateBlacklist(string id, bool isBlacklisted, string? reason, long? blacklistedAt)`
- `IReadOnlyList<Customer> GetBlacklisted()`

---

## 4. Veri Modeli Değişiklikleri

### 4.1 Customer'a yeni alan

```
Customer.BlacklistedAt  long?  (NULL when not blacklisted)
```

### 4.2 Migration `003_blacklist_timestamp.sql`

```sql
-- Phase 2a: track when a customer was blacklisted.
ALTER TABLE Customer ADD COLUMN BlacklistedAt INTEGER;

UPDATE _meta SET SchemaVersion = 3 WHERE Id = 1;
```

`MigrationRunner` zaten version-aware (P1b Task 4'te yapıldı). 003 sıfır iş.

`001_initial.sql` ve `002_pivot_to_labels.sql` dokunulmaz — geçmişi koru.

---

## 5. Navigation Mimarisi

### 5.1 ⋮ Menü butonu

**Konum:** Mevcut `MainShellView.xaml`'in üst bar'ının en sağ ucunda — "Yayını Bitir" butonunun yanında küçük bir 32×32 buton.

**Tıklama:** WPF `ContextMenu` tetikler:
- Ayarlar
- Yayın Geçmişi
- Kara Liste

**Implementation:** Button + `<Button.ContextMenu><ContextMenu><MenuItem ...>...</ContextMenu>` pattern. Button click → `ContextMenu.IsOpen = true` (code-behind, basit).

**ViewModel komutları:**
- `OpenSettingsCommand` → `App.Host.Services.GetRequiredService<SettingsDialog>()` resolve, `ShowDialog()`
- `OpenStreamHistoryCommand` → aynı yaklaşım, `StreamHistoryDialog`
- `OpenBlacklistCommand` → aynı yaklaşım, `BlacklistDialog`

Dialog'lar `Transient` DI ile registered (her açılışta fresh instance, P1b'deki `StreamReportDialog` pattern'ı).

---

## 6. Kapsamı Sınırlamalar

### 6.1 Yapılmayan / iptal

- Auto-detect TikTok login sorunlarına UI uyarısı — kullanıcı sorumluluğu, browser console'da yeterli
- Settings için "Geri Yükle" / "Varsayılan ayarlar" butonu — gereksiz, kullanıcı `Documents/LiveDeck/settings.json` siler veya elle düzenler
- Stream history'de yıl/ay filtresi — 365 gün yeterli, gerekirse Faz 3
- Blacklist için "kategori" (spam / ödemedi / taciz) — sebep alanı serbest metin yeter
- Blacklist'ten kaldırma'ya neden alanı (audit log) — gereksiz, P1b'de bile yok

### 6.2 Test stratejisi

- **TDD'li yeni servis metodları:** `CustomerService.AddToBlacklist/RemoveFromBlacklist/EnsureBlacklistedManual`, `CustomerRepository.UpdateBlacklist/GetBlacklisted`, `SessionRepository.GetAllEnded`.
- **Migration `003`:** `MigrationRunnerTests`'a yeni assertion (BlacklistedAt kolonu var) eklenir.
- **UI testleri yok:** WPF dialog'lar için unit test yazmıyoruz (P1b yaklaşımı). Manuel smoke test, Faz 2a sonu.
- Tahmini yeni test sayısı: ~8-10 (var olan 39 → ~47-49).

---

## 7. Risk ve Açık Sorular

### 7.1 Riskler

- **TikTok DOM değişiklikleri** — UniCast'tek `content-tiktok.js`'in selector'ları kırılmış olabilir (UniCast kodu eski). Smoke test sırasında doğrulama, kırıksa hızlı patch. Bu risk her zaman var, abonelik gerekçesi.
- **AppSettings instance mutation** — DI'daki singleton'ın property'lerini değiştirmek thread-safe mi? Settings dialog UI thread'inde, `LabelPrinter` background thread'inde okuyor. Pratikte yanılmaz çünkü properties primitive (int, string, bool); .NET memory model'e göre torn read riski yok.
- **InstalledPrinters / InstalledFontCollection performans** — bazı sistemlerde yavaş (özellikle network printerlarla). Dialog ilk açılışta 1-2 saniye gecikme olabilir. Önemsiz — kullanıcı Settings'i sıkça açmıyor.

### 7.2 Açık sorular (yok)

Tüm tasarım kararları kullanıcı ile netleştirildi.

---

## 8. Onay

Tasarım sahibi: kullanıcı (Burak)
Onaylanma tarihi: bekleniyor.

Onay sonrası `writing-plans` skill'i ile Faz 2a implementasyon planı yazılır.
