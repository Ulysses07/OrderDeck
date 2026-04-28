# Faz 3d — P2b Polish + Türkçe UX Cilası (Tasarım)

**Hedef:** Faz 2b code review'dan dönen 4 ertelenmiş madde + 4 Türkçe yerelleştirme cilasını tek bundle olarak ship et.

**Kapsam:** 8 lokalize fix + 1 ortak yardımcı dosya. Mimari değişiklik yok.

**Pre-Faz-3d state:** P2b (HEAD `332e4c5`), 74/74 test geçer.

---

## 1. Mimari Etkisi

Yeni proje yok. Yeni soyutlama yok. Yeni dosya: `LiveDeck.App/Formatting/TrFormats.cs` — para/tarih için tek noktada `tr-TR` `CultureInfo` ve iki yardımcı metod. Diğer 8 madde mevcut dosyaların içine yamalanır.

**Etkilenen dosya seti:**

| # | Madde | Dosya |
|---|---|---|
| 1 | Window-X kapatma guard'ı | `LiveDeck.App/MainWindow.xaml.cs` |
| 2 | Platform emoji map | `LiveDeck.Overlay/wwwroot/giveaway.js` |
| 3 | Geç-katılan TotalCount snapshot | `LiveDeck.Overlay/OverlayHost.cs` |
| 4 | Önceki kazanan cache | `LiveDeck.Core/Sales/GiveawayService.cs` |
| 5 | TR-uyumlu keyword eşleşmesi | `LiveDeck.Core/Sales/GiveawayService.cs` (4 ile aynı dosya) |
| 6 | Para formatı tr-TR sabit | `LiveDeck.App/App.xaml.cs` + `LiveDeck.App/Formatting/TrFormats.cs` (yeni) |
| 7 | Tarih/saat tr-TR | 6 ile aynı (global culture) |
| 8 | Excel format + locale | `LiveDeck.App/ViewModels/StreamReportViewModel.cs` |

**Yeni testler:** Madde 3, 4, 5 için ~3 xUnit test. Toplam 74 → ~77.

---

## 2. Madde Ayrıntıları

### 2.1 Window-X kapatma guard'ı

**Sorun:** Aktif çekiliş sırasında ana pencere "X" ile kapatılırsa `MainShellViewModel.EndStream`'in çekiliş gate'i atlanır. Boot'taki orphan recovery (`332e4c5`) phantom row'u temizler ama kullanıcı için kafa karıştırıcı.

**Çözüm:** `MainWindow.xaml.cs`'e `Closing` event handler ekle. Handler:

```csharp
protected override void OnClosing(CancelEventArgs e)
{
    if (DataContext is MainShellViewModel vm && vm.IsGiveawayActive)
    {
        MessageBox.Show(
            "Aktif çekiliş var. Önce çekilişi tamamla veya iptal et.",
            "Çekiliş aktif",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
        e.Cancel = true;
        return;
    }
    base.OnClosing(e);
}
```

**Kapsam dışı:** Aktif yayın için ek gate (orijinal `EndStream` zaten yayını sonlandırma onayını yönetiyor; pencere kapatma sırasında yayını otomatik bitirmek istemiyoruz).

### 2.2 Platform emoji map

**Sorun:** `giveaway.js`:
```js
${w.Platform === 'instagram' ? '📷' : '🎵'}
```
Üçüncü platform için yanlış (örn. Twitch eklenirse 🎵 görünür).

**Çözüm:** Map + fallback.

```js
const PLATFORM_EMOJI = { instagram: '📷', tiktok: '🎵' };
const emoji = PLATFORM_EMOJI[w.Platform] || '💬';
```

`<span class="platform-${w.Platform}">${emoji}</span>` yapısı korunur (CSS sınıfı dinamik, yeni platform için ek styling kolay).

**Kapsam dışı:** `chat.js` aynı tedaviyi hak ediyor ama şu an emoji kullanmıyor; ileride.

### 2.3 Geç-katılan TotalCount snapshot

**Sorun:** OBS Browser Source bağlantısı koparsa `giveaway.js` 1.5sn sonra reconnect ediyor. Reconnect sırasında `OverlayHost.HandleGiveawayClient` sadece `giveaway.started` event'ini gönderiyor; participant counter `0 katılımcı` olarak sıfırlanıyor.

**Çözüm:** `HandleGiveawayClient`'in late-join branch'ine ikinci event:

```csharp
if (active is not null)
{
    var startedEvt = new OverlayEvent("giveaway.started", new GiveawayStartedEvent(
        active.Id, active.Keyword, active.WinnerCount, active.DurationSeconds, active.StartedAt));
    await SendJson(ws, startedEvt, ct);

    // Mevcut katılımcı sayısını da yolla — JS counter'ı doğru başlasın.
    var count = _giveaways.GetParticipantCount(active.Id);  // yeni method, aşağıda
    if (count > 0)
    {
        var countEvt = new OverlayEvent("giveaway.participant", new GiveawayParticipantEvent(
            active.Id,
            Username: "",        // late-join snapshot — hangi user olduğu önemsiz
            DisplayName: null,
            AvatarUrl: null,
            Platform: "",
            TotalCount: count));
        await SendJson(ws, countEvt, ct);
    }
}
```

`GiveawayService` (host'un sahibi) `Repository.GetParticipants(id).Count` çağırabilir, ama N participants döndüren list'i sadece sayım için almak ziyan. **`GiveawayRepository.GetParticipantCount(string giveawayId)`** ekle:

```csharp
public int GetParticipantCount(string giveawayId)
{
    using var conn = _factory.Open();
    return conn.ExecuteScalar<int>(
        "SELECT COUNT(*) FROM GiveawayParticipant WHERE GiveawayId=@id",
        new { id = giveawayId });
}
```

`OverlayHost`'un `GiveawayRepository`'e doğrudan erişimi yok (`GiveawayService` üzerinden çalışıyor). İki seçenek:

- (a) `GiveawayService.GetActiveParticipantCount()` proxy method ekle.
- (b) `OverlayHost`'a `GiveawayRepository` da inject et.

**Seçim (a)**: `OverlayHost`'un Service'e bağımlılığı zaten var; ekstra dep eklemeyelim. `GiveawayService`'e:
```csharp
public int GetActiveParticipantCount() =>
    Active is null ? 0 : _giveaways.GetParticipantCount(Active.Id);
```

### 2.4 Önceki kazanan cache

**Sorun:** PreventRewinning aktif olan çekilişte her chat mesajı için `GiveawayRepository.GetWinnerCustomerIdsForSession` (JOIN sorgu) çağırılıyor. Yoğun chat'te N×saniye DB hit.

**Çözüm:** Çekilişin lifetime'ı boyunca set'i bir kez al, cache'le.

`GiveawayService` instance state'ine ekle:
```csharp
private HashSet<string>? _activePreviousWinners;
```

`Start`:
```csharp
_giveaways.Insert(g);
Active = g;

// PreventRewinning aktifse önceki kazananları cache'le
_activePreviousWinners = preventRewinning
    ? new HashSet<string>(_giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id))
    : null;

Started?.Invoke(...);
```

`AddParticipantFromChat`'in PreventRewinning bloğu:
```csharp
if (g.PreventRewinning && _activePreviousWinners?.Contains(customer.Id) == true) return;
```

`Draw` ve `Cancel`'ın sonu:
```csharp
_activePreviousWinners = null;
```

**Cache invalidation:** Aktif bir çekiliş varken yeni `Start` çağrılamaz (UI guard). `Draw`/`Cancel` set'i temizler. Yeni `Start` set'i yeniden inşa eder. Yarış koşulu yok (UI thread).

**Defensive fallback:** `_activePreviousWinners is null` ve `g.PreventRewinning is true` durumunda eski path (DB sorgusu) fallback olarak korunur. Tek bir koşul ile birleştirilir:
```csharp
if (g.PreventRewinning)
{
    var prevWinners = _activePreviousWinners
        ?? new HashSet<string>(_giveaways.GetWinnerCustomerIdsForSession(g.SessionId, g.Id));
    if (prevWinners.Contains(customer.Id)) return;
}
```

### 2.5 TR-uyumlu keyword eşleşmesi

**Sorun:** `StringComparison.OrdinalIgnoreCase` Türkçe noktalı/noktasız 'I'/'i' farklarını yanlış eşliyor.

| Keyword | Mesaj | Mevcut davranış | Beklenen |
|---|---|---|---|
| `istanbul` | "İSTANBUL" | match (✓ tesadüfen) | match |
| `İstanbul` | "istanbul" | NO match (✗) | match |
| `ışık` | "IŞIK" | NO match (✗) | match |

**Çözüm:** `CultureInfo("tr-TR").CompareInfo.IndexOf` ile.

`GiveawayService`:
```csharp
private static readonly System.Globalization.CompareInfo TrCompare =
    new System.Globalization.CultureInfo("tr-TR").CompareInfo;

// AddParticipantFromChat içinde:
if (TrCompare.IndexOf(message.Text, g.Keyword, System.Globalization.CompareOptions.IgnoreCase) < 0)
    return;
```

### 2.6 Para formatı tr-TR sabitlenmiş

**Sorun:** `{0:N2} TL` ve benzeri WPF binding'leri culture-bağımlı; en-US makinada `100.00 TL`, tr-TR'de `100,00 TL`. Tutarsız davranış.

**Çözüm:** App-level culture sabitleme. `App.xaml.cs` `OnStartup`'a:

```csharp
protected override void OnStartup(StartupEventArgs e)
{
    var tr = new CultureInfo("tr-TR");
    Thread.CurrentThread.CurrentCulture = tr;
    Thread.CurrentThread.CurrentUICulture = tr;
    CultureInfo.DefaultThreadCurrentCulture = tr;
    CultureInfo.DefaultThreadCurrentUICulture = tr;
    FrameworkElement.LanguageProperty.OverrideMetadata(
        typeof(FrameworkElement),
        new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(tr.IetfLanguageTag)));

    base.OnStartup(e);
}
```

Bu tek değişiklik:
- WPF Binding `StringFormat` artık tr-TR culture kullanır
- C# `decimal.ToString("N2")` tr-TR culture kullanır
- `DateTimeOffset.ToString()` tr-TR culture kullanır
- WPF `Run.Text="{Binding ...}"` kümeleme/decimal separator otomatik düzelir

**Yan etki:** `MainShellViewModel.TryParsePrice` zaten her iki culture'ı deniyor (`InvariantCulture` + `tr-TR`); değişiklik yok.

### 2.7 Tarih/saat tr-TR

Madde 6'daki global culture set'i tarih formatlarını da hâlleder. Ek dokunuşlar:

- `StreamHistoryViewModel` tarih sütunu format string'i: `"d MMMM yyyy HH:mm"` (örn. `28 Nisan 2026 14:30`).
- `StreamReportViewModel.Load` içindeki `DateTimeOffset.FromUnixTimeSeconds(...).ToString(...)` çağrılarına explicit culture ver: `TrFormats.TR`.
- XAML `StringFormat={}{0:HH:mm}` artık tr-TR.

`TrFormats.cs` yardımcısı:
```csharp
namespace LiveDeck.App.Formatting;

public static class TrFormats
{
    public static readonly CultureInfo TR = new("tr-TR");

    /// <summary>"100,50 TL" — tabanı sabit, tr-TR formatı.</summary>
    public static string Currency(decimal v) => v.ToString("N2", TR) + " TL";

    /// <summary>"28 Nis 2026 14:30" — kısa Türkçe tarih.</summary>
    public static string DateTime(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("d MMM yyyy HH:mm", TR);

    /// <summary>"28 Nisan 2026 14:30" — uzun Türkçe tarih.</summary>
    public static string DateTimeLong(long unixSeconds) =>
        DateTimeOffset.FromUnixTimeSeconds(unixSeconds).ToLocalTime()
            .ToString("d MMMM yyyy HH:mm", TR);
}
```

ViewModel'lerde `Binding` zincirinde culture sabit; ham `string` formatlama gerekiyorsa `TrFormats.Currency(...)` veya `TrFormats.DateTime(...)`.

### 2.8 Excel format + locale

**Sorun:** ClosedXML cell format'ları varsayılan kullanılıyor; Excel'i tr-TR olmayan makinada açan kullanıcı için sayı/para sütunları yanlış görünebilir.

**Çözüm:** `StreamReportViewModel.ExportToExcel` içinde her sayısal/tarih hücreye explicit format:

```csharp
ws.Cell(5, 1).Value = "Toplam ciro";
ws.Cell(5, 2).Value = TotalAmount;
ws.Cell(5, 2).Style.NumberFormat.Format = "#,##0.00 \"TL\"";   // 100,50 TL

ws.Cell(row, 4).Value = c.TotalAmount;
ws.Cell(row, 4).Style.NumberFormat.Format = "#,##0.00 \"TL\"";

ws.Cell(row, 3).Value = c.LabelCount;
ws.Cell(row, 3).Style.NumberFormat.Format = "0";   // tam sayı, ayraçsız
```

Tarih sütunu (varsa, mevcut export'ta yok ama Faz 3 ileride ekleyebilir): `"dd.MM.yyyy HH:mm"`.

ClosedXML format string'leri Excel cell-level formatlardır; receiving Excel'in locale'ini umursamaz.

---

## 3. Test Stratejisi

### 3.1 Yeni xUnit testleri

**`OverlayHostGiveawayLateJoinTests`** (madde 3):
```csharp
[Fact]
public async Task Late_join_receives_started_plus_participant_count_when_giveaway_active()
{
    // Setup: GiveawayService.Active set, 3 participants
    // Connect WS, expect 2 events: giveaway.started + giveaway.participant{TotalCount: 3}
}
```

**`GiveawayServicePreventRewinningCacheTests`** (madde 4):
```csharp
[Fact]
public void Start_with_preventRewinning_caches_winner_set_and_AddParticipantFromChat_does_not_hit_repo()
{
    // Mock GiveawayRepository, expect GetWinnerCustomerIdsForSession called exactly once at Start.
    // Send 5 chat messages → still exactly 1 call.
}
```

**`GiveawayServiceTurkishKeywordTests`** (madde 5):
```csharp
[Theory]
[InlineData("istanbul", "İSTANBUL gel", true)]
[InlineData("İstanbul", "istanbul gel", true)]
[InlineData("ışık", "IŞIK kapı", true)]
[InlineData("kazan", "kaybetti", false)]
public void AddParticipantFromChat_matches_keyword_with_turkish_culture(
    string keyword, string text, bool shouldAdd) { ... }
```

### 3.2 Manuel smoke

| # | Test |
|---|---|
| 1 | Stream + giveaway başlat, ana pencereyi X ile kapat → MessageBox uyarısı, kapanmaz |
| 2 | (Üçüncü platform yok şu an, görsel doğrulama ileride; kod review yeter) |
| 6 | Etiket yazdır, fiyat `100,50 TL` görünmeli (en-US locale'de bile) |
| 7 | Yayın geçmişi açıldığında tarihler `28 Nis 2026 14:30` Türkçe ay adıyla |
| 8 | Excel export'u en-US makinada da tr-TR formatı korumalı |

### 3.3 Mevcut test korunumu

74/74 hâlâ pass etmeli. Madde 5 mevcut `AddParticipantFromChat_is_case_insensitive_for_alphanumeric_keywords` testini bozma riski taşır (test "katil" / "KATIL ben" ASCII case folding'i bekliyor; tr-TR culture bunu da match eder). Test yeşil kalır.

Madde 6'nın global culture'ı testleri etkileyebilir: `decimal.Parse(...)` veya `DateTime.Parse(...)` testlerde varsa kırılır. Mevcut testler `InMemorySqlite` üstünde çalışıyor, format'a duyarlı testler yok — risk düşük. Yine de test runner'da `dotnet test` lokali bağımsız tutmak için CI'da explicit `-c Release` + culture flag eklemek isteyebiliriz; **kapsam dışı.**

---

## 4. Hata Yönetimi

| # | Hata yolu |
|---|---|
| 1 | `MessageBox` + `Cancel = true`. Kullanıcı butona basıp kapatamıyor; başka hata yok. |
| 2 | `PLATFORM_EMOJI[unknown]` → `undefined`, `\|\|` operatörü `'💬'` fallback. NPE yok. |
| 3 | `GetParticipantCount` 0 dönerse counter event yollanmaz (dolu giveaway için bu mümkün değil ama branch ucuz). |
| 4 | `_activePreviousWinners is null` durumu defansif fallback ile DB sorgusu çağırır. Mevcut davranışa dönüş. |
| 5 | `CompareInfo.IndexOf` exception fırlatmaz; `-1` sonucu mevcut early-return ile zaten beklenen davranış. |
| 6 | `OverrideMetadata` zaten override edilmişse `ArgumentException`. App startup'ta tek seferlik çağrılır; yeniden start mümkün değil. |
| 7 | Madde 6 ile aynı. |
| 8 | ClosedXML format string'leri sessizce ham metin yazar. Mevcut try/catch dosya I/O hatasını yakalıyor. |

---

## 5. YAGNI Kararları (yapılmayanlar)

- `chat.js` için emoji map (madde 2 sadece giveaway'i hedefliyor; chat zaten emoji kullanmıyor).
- Üçüncü bir platform için resmi destek (Twitch, YouTube): scope dışı, ileride `Platform` constants seti tek noktada toplanacak.
- Excel hücrelerinde renk/border styling (raporun temel okunurluğu yeterli).
- Test runner culture invariance flag'i.
- WPF `XmlLanguage` resource'lar üzerinden XAML-only set (kod tabanlı set yeterli).
- `MainShellViewModel.IsGiveawayActive` window-close guard'ı için yeni ViewModel hook (DataContext üstünden direkt erişim yeterli).
- `GiveawayRepository.GetParticipantCount` için ayrı index (mevcut `IX_GiveawayParticipant_Winners` zaten `(GiveawayId, IsWinner)` üstünde, `COUNT(*) WHERE GiveawayId=?` bu indexi kullanır).

---

## 6. Faz Sonrası Açık Bırakılanlar (Faz 3a/3b/3c'ye)

- Müşteri history view + TrustScore (Faz 3a)
- Klavye kısayolları, mini overlay panel, kuyruk toplu işlemleri (Faz 3b)
- Stream Deck SDK (Faz 3c)
- Test runner culture invariance (CI hardening, ileride)
- `chat.js` platform emoji desteği (üçüncü platform eklendiğinde)

---

## 7. Kabul Kriterleri

- ✅ Build temiz, 0 warning, 0 error
- ✅ ~77/77 unit test pass (74 mevcut + 3 yeni)
- ✅ Manuel smoke 5/5 pass (yukarıdaki tablo)
- ✅ Mevcut Faz 2b davranışları regrese olmamış
- ✅ Tek PR, tek "Faz 3d polish" merge commit'i (squash veya merge)
