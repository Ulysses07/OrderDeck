# Yayın Excel Raporu — design

**Status**: design
**Date**: 2026-05-19
**Sub-project of**: DahaFazlaScreen "Sonraki sürüm" placeholder bitirme — 3/3 (Müşteriler + Hızlı istatistik tamamlandı)

## Problem

DahaFazlaScreen'in son "Sonraki sürüm" placeholder'ı: "Yayın Excel raporu". Yayıncı tamamlanmış bir yayının sipariş özetini muhasebesine / WhatsApp grubuna / Drive'ına göndermek istiyor. Şu an mobile'da export yok; SessionDetailScreen siparişleri görsel olarak listeliyor ama dosya olarak paylaşılamıyor.

## Solution

**Client-side CSV** generation — sıfır server kodu. Mevcut `/api/panel/sessions/{id}/orders` endpoint'inden çekilen veri mobile'da CSV string'e çevrilir, Capacitor Filesystem ile cache'a yazılır, Capacitor Share API ile native paylaşım sheet açılır (WhatsApp/Drive/Email/vs.).

Yeni bir `YayinRaporuScreen` (`/yayin-raporu` route) tüm yayınları listeler; her card'ın kendi "📊 Excel paylaş" butonu var.

## Architecture

```
mobile (React + Vite + Capacitor)
─────────────────────────────
[Daha Fazla] → "Yayın raporu" NavRow
                ↓
[YayinRaporuScreen.tsx] (yeni)
  ├─ useSessions() ile yayın listesi
  └─ Her card'da [📊 Excel paylaş] butonu
                ↓
[lib/sessionExport.ts] (yeni)
  ├─ buildOrdersCsv(orders) → string (UTF-8 BOM + ; sep + Türkçe headers)
  ├─ buildFilename(title) → "yayin-rapor-{sanitized}-{YYYYMMDD}.csv"
  └─ shareSessionExport(session, orders) → Filesystem.writeFile + Share.share
```

**Yeni dosyalar:**
- `apps/panel/src/lib/sessionExport.ts` — CSV builder + filesystem write + share orchestration
- `apps/panel/src/screens/YayinRaporuScreen.tsx` — session picker list + share button

**Modify:**
- `apps/panel/src/App.tsx` — `/yayin-raporu` route
- `apps/panel/src/screens/DahaFazlaScreen.tsx` — placeholder kaldır, NavRow ekle
- `apps/panel/package.json` — `@capacitor/filesystem` ekle (eğer yoksa)
- `apps/panel/android/app/src/main/AndroidManifest.xml` — yok (Filesystem.Directory.Cache app-private, izin gerekmez)

**Server**: değişiklik yok. Mevcut `GET /api/panel/sessions` (yayın listesi) ve `GET /api/panel/sessions/{id}/orders` (her yayının siparişleri) endpoint'leri yeterli.

## CSV format

**Encoding**: UTF-8 with **BOM** (`\uFEFF` prefix). BOM olmazsa Excel TR locale Türkçe karakterleri bozar.

**Separator**: `;` (noktalı virgül). Türkçe Windows Excel'in default ondalık ayracı `,` olduğu için CSV separator olarak `;` zorunlu — yoksa "Tutar" sütunundaki `12,50` virgülü kolon ayracı sayar ve dosya bozulur.

**Quote**: `"..."` her hücrede. Hücrede `"` varsa `""` ile escape, içinde `;`, `\r`, `\n` varsa hücre quote'lanır.

**Newline**: `\r\n` (Excel Windows uyumlu).

**Sütunlar** (7 sütun):

| # | Header | Kaynak (Order entity) | Format |
|---|---|---|---|
| 1 | `Tarih` | `AddedAt` | `dd.MM.yyyy HH:mm` (TR locale) |
| 2 | `Müşteri` | `DisplayName ?? Username` | string (boşsa "—") |
| 3 | `Platform` | `Platform` | string ("Instagram" / "TikTok" / "Facebook" / "YouTube" / "Web" — kapital sözcükle map'lenir) |
| 4 | `Sipariş Kodu` | `Code ?? ""` | string |
| 5 | `Açıklama` | `MessageText` | string, ilk 200 karakter (truncated, sonuna `…`) |
| 6 | `Tutar (₺)` | `Price` | `decimal.toFixed(2)` virgül ondalık (`12,50` — Türkçe Excel doğrular okuyor) |
| 7 | `Durum` | computed | "İptal" / "Yazıldı" / "Kargo ücreti" / "Yedek" / "Beklemede" |

**Durum hesaplama**:
- `CancelledAt != null` → "İptal"
- `IsShippingFee == true` → "Kargo ücreti"
- `IsTentativeBackup == true` → "Yedek"
- `PrintedAt != null` → "Yazıldı"
- yoksa → "Beklemede"

**Sıralama**: order'lar `AddedAt` ASC (CSV reader için kronolojik anlamlı). Eğer server orders'ı farklı sırada dönüyorsa client mobile'da sort'lar.

**Boş dosya senaryosu**: Yayında hiç sipariş yoksa CSV sadece header satırını içerir — paylaşılabilir, hata değil.

## Filename

Format: `yayin-rapor-{sanitize(sessionTitle)}-{YYYYMMDD}.csv`

`sanitize` algoritması:
- TR karakter normalize: `Ş`→`S`, `ş`→`s`, `ı`→`i`, `İ`→`I`, `ğ`→`g`, `Ğ`→`G`, `ü`→`u`, `Ü`→`U`, `ö`→`o`, `Ö`→`O`, `ç`→`c`, `Ç`→`C`
- Lowercase
- Boşluk → `-`
- `[^a-z0-9-]` → silinir
- Birden çok `-` arka arkaya → tek `-`
- Trim leading/trailing `-`
- Empty fallback: `yayin-{sessionId.substring(0,8)}`

`{YYYYMMDD}` = bugünün TR tarih (Date.now() → local TR locale, `2026-05-19` → `20260519`).

Örnekler:
- "Mezat #14" → `yayin-rapor-mezat-14-20260519.csv`
- "Akşam Yayını 🌙" → `yayin-rapor-aksam-yayini-20260519.csv`
- "" (title boş) → `yayin-rapor-<8-char-id>-20260519.csv`

## Share flow

```typescript
import { Filesystem, Directory, Encoding } from "@capacitor/filesystem";
import { Share } from "@capacitor/share";

export async function shareSessionExport(
  sessionTitle: string,
  sessionId: string,
  orders: Order[],
): Promise<void> {
  const filename = buildFilename(sessionTitle, sessionId);
  const csv = "\uFEFF" + buildOrdersCsv(orders);

  const { uri } = await Filesystem.writeFile({
    path: filename,
    directory: Directory.Cache,
    data: csv,
    encoding: Encoding.UTF8,
  });

  await Share.share({
    title: `${sessionTitle} - Yayın Raporu`,
    url: uri,
    dialogTitle: "Paylaş",
  });
}
```

**Cache lifetime**: `Directory.Cache` (Android: `getCacheDir()`, app-private). OS otomatik temizleyebilir. Kullanıcı share dialog'undan paylaştıktan sonra dosya gerek yok. Persistent değil.

**Permissions**: gerekmez — `Directory.Cache` app-internal, manifest permission ekle gerekmiyor.

**Plugin install**: `@capacitor/filesystem` yoksa eklenir. `@capacitor/share` zaten yüklü (broadcast posts tarafından).

## UI — `YayinRaporuScreen.tsx`

```
┌─────────────────────────────────────┐
│ ← Geri                              │
│ Yayın raporu                        │
│ Bir yayın seç, Excel olarak paylaş  │
├─────────────────────────────────────┤
│ ┌─────────────────────────────────┐│
│ │ Mezat #14         ₺12.450  →   ││  ← tap edilebilir veya not
│ │ 23 sipariş · 2g önce            ││
│ │           [📊 Excel paylaş]     ││
│ └─────────────────────────────────┘│
│ ┌─────────────────────────────────┐│
│ │ Mezat #13         ₺ 8.200  →   ││
│ │ 15 sipariş · 3g önce            ││
│ │           [📊 Excel paylaş]     ││
│ └─────────────────────────────────┘│
│ ... (useSessions() limit ~20-30)    │
└─────────────────────────────────────┘
```

**Davranış**:
- `useSessions()` ile yayın listesi (mevcut hook); limit default ~10-20 (broadcast posts'ta AnaScreen limit=5 kullanıyor; bu screen daha fazla — limit param yok ise mevcut hook'a göre adapt edilir).
- Her card'da "📊 Excel paylaş" buton.
- Tap → o session için `useSessionOrders(sessionId)` lazy fetch (TanStack cache veya fresh query).
- Fetch loading boyunca: button "Hazırlanıyor..." disabled.
- Orders geldikten sonra: `shareSessionExport` çağrılır → native share sheet.
- Error: button altında küçük kırmızı satır "Paylaşılamadı, tekrar dene".

**Optional**: card tap (button dışında) → mevcut `/siparisler/:sessionId` (SessionDetailScreen). İsteğe bağlı, kullanıcı yayını görmek isterse. — out of scope (sadece "→" görsel hint kalır, navigation yok).

**Empty state**: hiç yayın yoksa `📭 Henüz yayın yok — WPF App'te yayın başlatınca burada görünür`.

**Loading**: 3 skeleton card.

**Error (yayın list fetch)**: "Yüklenemedi — Tekrar dene" buton.

## DahaFazla integration

1. **"Sonraki sürüm" section'ından şu satırı sil**:
   ```typescript
   <DisabledRow label="Yayın Excel raporu" hint="Sonraki sürüm" />
   ```
   Sonuç: "Sonraki sürüm" section'ında sadece "Müşteriler" kalır (zaten Hızlı istatistik'te kaldırıldı). Eğer "Sonraki sürüm" tamamen boşalırsa section başlığı + p paragrafı da silinir.

   > Önemli: 3 placeholder'dan 3'ü kaldırıldı — "Sonraki sürüm" section'ı tamamen boş kalmamalı. DahaFazla'da hâlâ disabled olan rows varsa kalır; yoksa section başlığı silinir. (Diğer iki feature'da yapılmış kontrol implementer task'ta yapılır.)

2. **"İçerik" section'ında, "Hızlı istatistik" NavRow'unun altına yeni NavRow ekle**:
   ```typescript
   <NavRow
     to="/yayin-raporu"
     label="Yayın raporu"
     hint="Bir yayını CSV olarak indir ve paylaş"
   />
   ```

## Testing

**Mobile**: `npm run typecheck` clean. Otomatik test yok (broadcast posts/müşteriler/hızlı istatistik pattern'i).

**Manuel smoke** (Android emulator):
- DahaFazla > Yayın raporu → yayın listesi
- Yayın card'ında "📊 Excel paylaş" → loading → native share sheet
- WhatsApp/Drive/Email seç → dosya gönderilir
- Telefonda dosyayı Excel/Sheets'te aç → 7 kolon görünür, Türkçe karakterler doğru, tutar sütunu numeric
- Test edge cases:
  - Yayın siparişi yok → CSV header-only paylaşılır
  - Yayın başlığı boş veya emoji'li → filename sanitize çalışır
  - Cancel/Backup/ShippingFee siparişleri "Durum" kolonunda doğru gösterilir

## Out of scope

- Server-side XLSX (ClosedXML/OpenXml) — client-side CSV yeterli, XLSX 3-4 kat iş
- Multi-sheet (orders + payments + shipments birlikte) — payments/shipments per-session join gerekir, MVP'de yok
- iOS testi — Capacitor iOS build pipeline kurulu değil, MVP Android-only
- Excel formatting (renkler, başlık satırı kalın, totals row) — CSV format düzdür
- "Bütün yayınlar" toplu rapor — şimdilik per-session
- Tarih aralığı seçimi — session = kendi tarih aralığı
- DahaFazla 4. placeholder yok — bu 3/3, tamamen biter
