# LiveDeck Phase 2b — Çekiliş (Giveaway) Design

**Tarih:** 2026-04-28
**Durum:** Onay bekleniyor
**Önceki:** P2a commit `77b90f2` (multi-platform + admin UX)

---

## 1. Vizyon ve Kapsam

### 1.1 Tek cümle

Yayıncı yayın sırasında bir anahtar kelime + süre + kazanan sayısı belirleyerek çekiliş başlatır; izleyiciler chat'te o keyword'ü yazarak katılır; süre bitince OBS overlay'de rulet animasyonuyla kazanan(lar) seçilir.

### 1.2 Faz 2b kapsamı

- Çekiliş kurulum dialog'u (anahtar kelime + süre + kazanan sayısı + platform filtresi + önceki kazananları dahil etme bayrağı)
- MainShell'e **🎁 Çekiliş** butonu (üst bar, Yayını Bitir'in yanında)
- Çekiliş aktifken status banner (canlı katılımcı sayacı + geri sayım + Şimdi Çek / İptal butonları)
- ChatBus subscriber: keyword içeren mesajlardan unique `(Platform, Username)` katılımcılarını DB'ye yazar; kara liste filtrelenir
- OBS overlay (`/overlay/giveaway`): geri sayım + canlı katılımcı sayısı + rulet animasyonu + kazanan ekranı + konfeti
- Adillik için `RandomSeed` her çekilişte loglanır
- StreamReportDialog'a "Çekiliş: N adet (M kazanan)" satırı

### 1.3 Faz 2b dışı (Faz 3'e bırakılan)

- Çekiliş Geçmişi admin paneli (yapılan çekilişlerin detay görüntüsü)
- Çekiliş şablonu preset'leri ("60sn 1 kazanan", "120sn 3 kazanan")
- Çoklu anahtar kelime
- Katılım koşulları ("takipçi olmalı", "yorumda etiketlemeli")
- Ses efekti (OBS audio routing karışmasın)

---

## 2. Mimari

### 2.1 Yeni dosyalar

```
LiveDeck.Core/
├── Sales/
│   ├── Giveaway.cs                          # NEW (record)
│   ├── GiveawayParticipant.cs               # NEW (record)
│   ├── GiveawayService.cs                   # NEW (Start/AddParticipantFromChat/Draw/Cancel)
│   └── GiveawayDrawer.cs                    # NEW (pure RNG-based selection, TDD)
└── Storage/
    ├── Migrations/004_giveaway.sql          # NEW
    └── Repositories/GiveawayRepository.cs   # NEW

LiveDeck.App/
├── ViewModels/
│   ├── MainShellViewModel.cs                # MODIFIED (active giveaway tracking + commands)
│   ├── NewGiveawayDialogViewModel.cs        # NEW
│   └── GiveawayBannerViewModel.cs           # NEW (countdown + participant counter, DispatcherTimer)
└── Views/
    ├── MainShellView.xaml                   # MODIFIED (🎁 button + banner)
    └── NewGiveawayDialog.xaml + .cs         # NEW

LiveDeck.Overlay/
├── OverlayHost.cs                           # MODIFIED (GET /overlay/giveaway + WS /ws/giveaway)
├── Models/OverlayEvent.cs                   # MODIFIED (giveaway.* events)
└── wwwroot/
    ├── giveaway.html                        # NEW
    ├── giveaway.js                          # NEW
    └── themes/minimal/giveaway.css          # NEW

LiveDeck.Tests/
├── Sales/
│   ├── GiveawayDrawerTests.cs               # NEW (deterministic RNG)
│   └── GiveawayServiceTests.cs              # NEW
└── Storage/
    └── GiveawayRepositoryTests.cs           # NEW
```

### 2.2 Bağımlılık akışı

P2a ile aynı: `App → Core/Chat/Overlay/Labeling`. Giveaway altyapısı `Core` içinde, overlay rendering `Overlay/wwwroot/`.

---

## 3. Özellik Tasarımları

### 3.1 Çekiliş başlatma

**Tetikleyici:** MainShell üst barında **🎁 Çekiliş** butonu. Yayını Bitir'in yanında, ⋮'den önce.

**Aktiflik kuralı:** Buton sadece aktif yayın varken etkin (`StreamSessionService.GetActive() != null`). Aksi durumda CanExecute=false.

**Aynı anda tek çekiliş:** Aktif çekiliş varken 🎁 butonu pasifleşir. İki çekiliş paralel olamaz.

**Kurulum dialog'u (`NewGiveawayDialog`):**

| Field | Kontrol | Validation | Default |
|---|---|---|---|
| Anahtar kelime | TextBox | 1-32 char, boşluk olabilir | (boş) |
| Süre | ComboBox | 30s / 60s / 120s / 300s / "Manuel bitir (0)" | 60s |
| Kazanan sayısı | NumericUpDown | 1-50 | 1 |
| Platform filtresi | ComboBox | "Tümü" / "Yalnız Instagram" / "Yalnız TikTok" | "Tümü" |
| Önceki kazananları dahil etme | CheckBox | — | true (excluded) |

Buttons: [İptal] [Başlat]. Başlat → validation → `GiveawayService.Start(...)` → dialog kapanır → MainShell banner çekiliş moduna geçer.

### 3.2 Çekiliş aktif durumu (status banner)

Çekiliş yokken status label: `"Yayın aktif (başlangıç: 14:32)"`

Çekiliş başlayınca aynı satır şu hale gelir:

```
🌹 Çekiliş aktif · 47 katılımcı · 0:32 · [Şimdi Çek] [İptal]
```

- Keyword soldaki rozet (örn. 🌹 veya KATIL)
- Katılımcı sayacı `DispatcherTimer` ile saniyede bir güncellenir (`GiveawayBannerViewModel`)
- Geri sayım `mm:ss` formatında. 0'a ulaştığında otomatik `Draw()` tetiklenir.
- "Manuel bitir" modunda geri sayım gösterilmez, yerine `(süre limitsiz)` yazılır
- "Şimdi Çek" — anlık çekim. Süre dolmasını beklemeden çek
- "İptal" — onay sorulur, çekiliş silinir

Çekiliş bittiğinde banner eski hale döner (yayın status).

### 3.3 Katılımcı toplama

`GiveawayService` aktif çekiliş varken `IChatBus` subscriber olarak çalışır:

```
ChatMessage geldi
  ↓
Aktif çekiliş var mı? Yoksa: at
  ↓
Mesaj keyword içeriyor mu? (case-insensitive Contains, P1b'deki MessageNormalizer pattern KULLANILMAZ — ham metinde geçiyor mu yeterli)
  ↓
Platform filtresine uyuyor mu?
  ↓
Customer.IsBlacklisted ise: at (sessizce)
  ↓
PreventRewinning aktifse: bu yayın oturumunun önceki çekilişlerinde bu Customer.Id kazanmış mı? Kazanmışsa: at
  ↓
GiveawayParticipant tablosunda (GiveawayId, Platform, Username) zaten varsa: at (dedupe)
  ↓
Yeni katılımcı kaydı oluştur (CustomerService.GetOrCreate ile customer otomatik)
```

UNIQUE INDEX `(GiveawayId, Platform, Username)` dedupe garantisi.

### 3.4 Çekme (Draw)

`GiveawayService.Draw(giveawayId)`:

1. Tüm katılımcıları yükle (UNIQUE garantisi sayesinde net; PreventRewinning filtrelemesi 3.3'te kayıt anında yapıldı, burada tekrar etmiyoruz)
2. **`GiveawayDrawer.Pick(participants, winnerCount, randomSeed)`** çağrılır:
   - Pure function, `System.Security.Cryptography.RandomNumberGenerator` ile shuffle
   - Seed string'inden deterministic generator (test edilebilir)
   - Katılımcı sayısı < kazanan sayısı: var olanı kadar döner
   - 0 katılımcı: boş liste döner
4. Dönen kazananlar için `GiveawayParticipant.IsWinner=1` set
5. `Giveaway.EndedAt = now`
6. OBS'ye `giveaway.draw` event'i gönder (kazanan listesi)
7. UI banner kaybolur (yayın status'a döner)

### 3.5 OBS overlay (`/overlay/giveaway`)

**HTML:** Statik tek sayfa, transparent body, `giveaway.css` import.

**WebSocket events** (`/ws/giveaway`):

| Event | Data | UI davranışı |
|---|---|---|
| `giveaway.start` | `{keyword, durationSeconds, winnerCount}` | Overlay aktif olur, geri sayım başlar |
| `giveaway.tick` | `{remainingSeconds, participantCount}` | Sayaç günceller, her saniye |
| `giveaway.draw` | `{winners: [{username, platform, displayName}]}` | Rulet animasyonu → kazanan(lar) gösterimi |
| `giveaway.empty` | `{}` | "Henüz katılımcı yok" 5 sn |
| `giveaway.cancel` | `{}` | Overlay anında kaybolur |

**Animasyon detayları (giveaway.js):**

```
giveaway.start →
  - Big keyword display: 🌹 / KATIL / +
  - Countdown timer: "00:60"
  - Participant counter: "0 katılımcı"
  - Pulsing border (CSS keyframes, slow)

giveaway.tick →
  - Counter incremented (animated count-up)
  - Countdown decremented

giveaway.draw → (sıralı multi-winner için her kazanan için tekrarla)
  - 3 saniye rulet:
    - Aday isimleri 60ms → 100ms → 150ms → 200ms aralıklarla (yavaşlayarak) flash et
    - Son 500ms tek isimde sabit, ufak bir highlight
  - 8 saniye kazanan ekranı (tek kazanan) / 5 saniye (çoklu kazananlar arası):
    - Username: 32pt sarı, sandığa yazılı bold
    - Platform ikonu (📷 instagram, 🎵 tiktok)
    - CSS konfeti (yarım sn delay'lı 50 parçacık, JS yok, pure CSS animation)
    - "Kazanan! 🎉" yazısı

giveaway.empty →
  - "Henüz katılımcı yok" 5 saniye, sönerek kaybolur
  - Overlay temiz olur

giveaway.cancel →
  - Anında fade out
  - Overlay tekrar şeffaf
```

### 3.6 Yayın Raporu eklenmesi

P2a'daki `StreamReportDialog` dialog'una bir satır:

```
Süre:           2 saat 14 dakika
Toplam etiket:  47
Toplam ciro:    8.450 TL
Tekil müşteri:  31 kişi
Çekiliş:        3 adet (5 kazanan)        ← YENİ
```

Bu için `GiveawayRepository.GetSessionTotals(sessionId)` metodu eklenir (LabelRepository pattern'ı):

```csharp
public sealed record GiveawaySessionTotals(int Count, int TotalWinners);

public GiveawaySessionTotals GetSessionTotals(string sessionId);
// SELECT COUNT(*) AS Count,
//        SUM(...winner count via subquery...) AS TotalWinners
// FROM Giveaway
// WHERE SessionId = @sessionId AND EndedAt IS NOT NULL AND CancelledAt IS NULL
```

`StreamReportViewModel.Load(sessionId)` bu totals'ı çekip `TotalGiveaways` ve `TotalWinners` property'lerine yazar.

---

## 4. Veri Modeli

### 4.1 Migration 004

```sql
-- Phase 2b: çekiliş tabloları
CREATE TABLE Giveaway (
    Id                 TEXT PRIMARY KEY,
    SessionId          TEXT NOT NULL,
    Keyword            TEXT NOT NULL,
    DurationSeconds    INTEGER NOT NULL,         -- 0 = manuel bitir
    WinnerCount        INTEGER NOT NULL,
    PlatformFilter     TEXT,                      -- null = tümü, JSON ["instagram"] / ["tiktok"]
    PreventRewinning   INTEGER NOT NULL DEFAULT 1,
    RandomSeed         TEXT NOT NULL,
    StartedAt          INTEGER NOT NULL,
    EndedAt            INTEGER,
    CancelledAt        INTEGER,
    FOREIGN KEY (SessionId) REFERENCES StreamSession(Id) ON DELETE CASCADE
);

CREATE INDEX IX_Giveaway_Session ON Giveaway(SessionId);
CREATE INDEX IX_Giveaway_Active  ON Giveaway(SessionId, EndedAt, CancelledAt);

CREATE TABLE GiveawayParticipant (
    Id           TEXT PRIMARY KEY,
    GiveawayId   TEXT NOT NULL,
    CustomerId   TEXT NOT NULL,
    Platform     TEXT NOT NULL,
    Username     TEXT NOT NULL,
    EnteredAt    INTEGER NOT NULL,
    IsWinner     INTEGER NOT NULL DEFAULT 0,
    FOREIGN KEY (GiveawayId) REFERENCES Giveaway(Id) ON DELETE CASCADE,
    FOREIGN KEY (CustomerId) REFERENCES Customer(Id)
);

CREATE UNIQUE INDEX UX_GiveawayParticipant_Unique
    ON GiveawayParticipant(GiveawayId, Platform, Username);

CREATE INDEX IX_GiveawayParticipant_Winners
    ON GiveawayParticipant(GiveawayId, IsWinner);

UPDATE _meta SET SchemaVersion = 4 WHERE Id = 1;
```

### 4.2 Domain records

```csharp
public sealed record Giveaway(
    string Id,
    string SessionId,
    string Keyword,
    int DurationSeconds,
    int WinnerCount,
    IReadOnlyList<string>? PlatformFilter,    // null = all
    bool PreventRewinning,
    string RandomSeed,
    long StartedAt,
    long? EndedAt,
    long? CancelledAt);

public sealed record GiveawayParticipant(
    string Id,
    string GiveawayId,
    string CustomerId,
    string Platform,
    string Username,
    long EnteredAt,
    bool IsWinner);
```

---

## 5. GiveawayDrawer (TDD odaklı)

```csharp
public sealed class GiveawayDrawer
{
    /// <summary>
    /// Selects up to <paramref name="winnerCount"/> winners from <paramref name="participants"/>
    /// using the supplied <paramref name="randomSeed"/> for deterministic shuffling.
    /// Returns fewer winners if not enough participants. Returns empty if 0 participants.
    /// </summary>
    public IReadOnlyList<GiveawayParticipant> Pick(
        IReadOnlyList<GiveawayParticipant> participants,
        int winnerCount,
        string randomSeed);
}
```

**Test cases:**
- Empty input → empty output
- 1 participant + 3 winners → 1 winner
- 5 participants + 1 winner → 1 winner (deterministic with seed)
- 10 participants + 3 winners → 3 distinct winners
- Same seed → same winners (reproducibility)
- Different seed → different winner sets (probabilistic, sampled)

Implementation: deterministic Fisher-Yates shuffle seeded from `string` → `int` (cheap deterministic hash; .NET'in built-in `string.GetHashCode()` runtime'a bağlı non-deterministic olduğu için kullanılmaz — yerine custom hash veya `XxHash` ailesi gibi deterministik bir fonksiyon. Plan task'ı somut seçim yapar). First N elements after shuffle = winners.

---

## 6. Edge Cases

| Senaryo | Davranış |
|---|---|
| 0 katılımcı + Şimdi Çek | `GiveawayService.Draw` boş liste döner; OBS `giveaway.empty` event; banner kaybolur; DB'ye kayıt: EndedAt set, hiç participant yok |
| Katılımcı < kazanan sayısı | Var olanı kadar kazanan, uyarısız |
| Aynı kullanıcı keyword'ü 5 kez yazmış | UNIQUE INDEX → ilk yazımda kayıt, sonrakilerini DB reddetir (try/catch swallow) |
| Kara listedeki kullanıcı keyword yazar | `IsBlacklisted=true` → katılımcı eklenmez, sessiz |
| PreventRewinning + bu yayında zaten kazanan biri keyword yazar | Katılımcı listesinden filtrelenir (Draw'da SQL ile çıkarılır), hatta hiç kaydedilmez (early-out) |
| Çekiliş aktif iken yeni 🎁 tıklarsa | Buton pasif, hiçbir şey olmaz |
| Çekiliş aktif iken Yayını Bitir | Engellenir: "Aktif çekiliş var. Önce çekilişi tamamla veya iptal et." |
| Çekiliş aktif iken App kapatılırsa | Aktif çekiliş `EndedAt=NULL, CancelledAt=NULL` olarak kalır. App tekrar açılırsa: stale çekilişler `CancelledAt=now()` ile kapatılır (boot zamanı `GiveawayService.RecoverOrphanedGiveaways(sessionId)` çağrısı) |
| Süre 0 (manuel bitir) seçildi | Geri sayım gösterilmez. Sadece "Şimdi Çek" / "İptal" çalışır. Otomatik draw yok |

---

## 7. Test Stratejisi

- **TDD: GiveawayDrawer** — pure logic, çoklu fixture (10+ test case)
- **TDD: GiveawayRepository** — CRUD + winners + session totals (~6 test)
- **TDD: GiveawayService** — orchestration: start/addParticipant/draw/cancel (~5-7 test)
- **Migration test:** version 4 + Giveaway/GiveawayParticipant tabloları assertion
- **UI**: manual smoke (rulet animasyonu + sayaç + button davranışları)

Tahmini yeni test sayısı: ~20. Toplam ~67.

---

## 8. Plan Sınırları (kapsamı sıkı tut)

- ✗ Çekiliş Geçmişi UI yok (DB'ye yazılır, görsel sonra)
- ✗ Kazanan bildirimi (DM gönder/WhatsApp open) yok
- ✗ "Tekrar oynat" butonu (kazananı yeniden çekme) yok — yayıncı isterse iptal edip yeni başlatır
- ✗ Çekiliş kayıtları için "Sebep ne yazdı kazanan" detayı yok — sadece username
- ✗ Çekiliş için audio yok

---

## 9. Onay

Tasarım sahibi: kullanıcı (Burak)
Onaylanma tarihi: bekleniyor.

Onay sonrası `writing-plans` skill'i ile Faz 2b implementasyon planı yazılır.
