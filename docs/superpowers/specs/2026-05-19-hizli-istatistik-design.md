# Hızlı istatistik (PIN korumalı) — design

**Status**: design
**Date**: 2026-05-19
**Sub-project of**: DahaFazlaScreen "Sonraki sürüm" placeholder bitirme — 2/3
  (1: Müşteriler ✓ merged, 3: Yayın Excel raporu, ayrı spec)

## Problem

Mobile panelde yayıncının kendi performansına hızlı bakacağı bir dashboard yok. Ciro/sipariş sayısı/aktif yayın gibi metrikler şu an dağınık — AnaScreen son yayın totalAmount'unu gösteriyor, dekontlar tab'ı bekleyen dekontu, ama bütünlük yok.

Ayrıca AnaScreen totalAmount card'ı herkes telefona baktığında görünebilir hale geliyor. Yayıncı omzu üstünden bakan biri (ekip üyesi, akraba) varken hassas finansal datanın görünmemesini istiyor.

## Solution

Yeni `/hizli-istatistik` ekranı — 8 metriği range tab'ları (bugün/hafta/ay) ile gösteren grid dashboard. PIN + biyometrik koruma katmanı ile hassas data filtrelenir. AnaScreen totalAmount card'ı da aynı PIN gate'in arkasına (inline blur) alınır.

PIN = "casual privacy filter" — yüksek güvenlik gerektirmez (JWT auth + cihaz kilidi zaten asıl koruma); amaç kazara göze çarpmayı engellemek.

## Architecture

```
mobile (React + Vite + Capacitor)         server (.NET LicenseServer)
─────────────────────────────              ───────────────────────────
[Daha Fazla] → "Hızlı istatistik"          PanelStatsController (yeni)
                ↓                            └─ GET /api/panel/stats
[HizliIstatistikScreen.tsx] (yeni)              ?range=today|week|month
  ├─ PIN gate (lock screen)                     Returns: revenue, count,
  │   ├─ Biometric prompt (ilk)                       avg, cancelRate,
  │   └─ 4-digit PIN fallback                         activeStream, pending*
  └─ Dashboard kartları (8 metrik)

[AnaScreen.tsx] (modify)                    Auth: Bearer-Customer (mevcut)
  └─ totalAmount card lock state
      (inline blur + "Göster" overlay)

[lib/pin.ts] (yeni)                         Plugin:
  ├─ setPin / verifyPin (SHA-256+salt)        capacitor-native-biometric (^5)
  ├─ clearPin (logout'ta tetiklenir)
  ├─ isPinSet / isUnlocked / lock
  └─ biometricAvailable / biometricPrompt
```

### Yeni dosyalar

**Server (LiveDeck):**
- `OrderDeck.LicenseServer/Controllers/Panel/PanelStatsController.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStatsControllerTests.cs`

**Mobile (OrderDeck-Mobile):**
- `apps/panel/src/lib/pin.ts` — PIN hash + storage + biometric helpers
- `apps/panel/src/screens/HizliIstatistikScreen.tsx`
- `apps/panel/src/components/PinGate.tsx` — reusable lock overlay
- `apps/panel/src/api/queries.ts` — `useStats(range)` hook ekle
- `apps/panel/src/App.tsx` — `/hizli-istatistik` route
- `apps/panel/src/screens/DahaFazlaScreen.tsx` — placeholder kaldır, NavRow ekle, logout'a `clearPin()` ekle
- `apps/panel/src/screens/AnaScreen.tsx` — totalAmount card'a PinGate sar
- `apps/panel/package.json` — `capacitor-native-biometric` ekle
- `apps/panel/android/app/src/main/AndroidManifest.xml` — `USE_BIOMETRIC` permission

## Server endpoint contract

### `GET /api/panel/stats?range={today|week|month}`

**Query params**:
- `range`: `today` (default) / `week` / `month`

**Range cutoffs** (Europe/Istanbul, +03:00, UTC'ye normalize):
- `today` = bugün 00:00 TR → şimdi
- `week` = bu Pazartesi 00:00 TR → şimdi
- `month` = ayın 1'i 00:00 TR → şimdi

**Response** (200 OK):
```json
{
  "range": "today",
  "rangeStart": "2026-05-19T00:00:00+03:00",
  "rangeEnd":   "2026-05-19T13:42:00+03:00",
  "revenue": 12450.00,
  "orderCount": 23,
  "averageOrderValue": 541.30,
  "cancelRate": 0.087,
  "activeStreamId": "guid-or-null",
  "activeStreamTitle": "Mezat #14",
  "pendingPaymentCount": 4,
  "pendingShipmentCount": 7,
  "lastUpdatedAt": "2026-05-19T10:42:00Z"
}
```

**Hesap kuralları** (mevcut endpoint'lerle tutarlı):
- **revenue** = `Orders.Where(PrintedAt != null && CancelledAt == null && !IsShippingFee && !IsTentativeBackup && AddedAt >= rangeStart)` `SUM(Price)`
- **orderCount** = aynı koşullar, `COUNT(*)`
- **averageOrderValue** = `revenue / orderCount` (0/0 = 0 dönülür)
- **cancelRate** = `COUNT(CancelledAt != null) / COUNT(all rows in range)` (range içindeki tüm satırlar denominator; 0/0 = 0)
- **activeStreamId/Title** = `StreamSessions.Where(EndedAt == null).OrderByDescending(StartedAt).FirstOrDefault()` — null olabilir
- **pendingPaymentCount** = `Payments.Where(Status == Pending)` — mevcut `PanelPaymentsController` kuralıyla aynı, all-time (range etkilemez)
- **pendingShipmentCount** = `Shipments.Where(Status == Held || Status == RecipientPays)` — mevcut `PanelShipmentsController` kuralıyla aynı, all-time
- **lastUpdatedAt** = response generation `DateTimeOffset.UtcNow`

**Tenant scope**: `licenseIds = _db.Licenses.Where(l => l.CustomerId == authCustomerId).Select(l => l.Id).ToList()` — sonra tüm aggregate query'ler `Where(o => licenseIds.Contains(o.LicenseId))` veya analog ile filtrelenir.

**Error handling**:
- Geçersiz `range` → `Problem(title: "invalid-range", statusCode: 400)`
- Auth fail → 401 (standard JWT)
- Empty data (yeni broadcaster) → `revenue=0, orderCount=0, ...` 200 OK

**Time zone**: server kodu `TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul")` veya hard-coded `+03:00` offset kullanır (DST yok). `rangeStart`/`rangeEnd` response'da +03:00 offsetli ISO 8601 string olarak döner. DB filtresi UTC'ye normalize edilir.

**Tests** (`PanelStatsControllerTests.cs`, ~6 test):
- `Stats_today_revenue_orderCount_correct` — basit happy path
- `Stats_today_excludes_cancelled_and_shipping_fees` — "real sale" filter doğru
- `Stats_week_aggregates_correctly` — week cutoff testi (geçen Pazartesi sonrası dahil, öncesi hariç)
- `Stats_cancelRate_calculation` — `2 cancelled / 10 total = 0.2`
- `Stats_activeStream_null_when_no_live_session` — null path
- `Stats_cross_tenant_returns_zero` — tenant B'nin verisi tenant A'da görünmez

Test fixture: existing `ApiFactory` + `CustomerAuthHelper`. Time için bir `IClock` mock'u inject edilirse `today` testleri deterministic olur — yoksa freeze-time pattern.

## PIN library — `apps/panel/src/lib/pin.ts`

**Plugin**: `capacitor-native-biometric` (community, Capacitor 6 uyumlu, MIT, npm ~50k weekly downloads).

**Storage**: Capacitor Preferences (auth token'da da kullanılıyor).
- Key: `orderdeck.pin.v1`
- Value: JSON `{ saltHex: string, hashHex: string }`

**Hash**: `SHA-256(saltHex + plaintextPin)` — Web Crypto API (`crypto.subtle.digest`). Salt 16 byte random.

**Justification — neden bcrypt değil**: PIN 4 digit numeric, attacker scenario casual phone-glance — server'da bilinmiyor, network exfiltrate yok, brute force off-line ancak local'de yapılabilir (zaten device-unlocked + app-unlocked durum gerektirir). SHA-256 + salt yeterli.

**API**:

```typescript
// Status
isPinSet(): Promise<boolean>
isUnlocked(): boolean             // in-memory timer
lock(): void                       // force lock
markUnlocked(): void               // internal — set/verify success'da çağrılır

// PIN management
setPin(plaintext: string): Promise<void>          // 4-digit regex enforced
verifyPin(plaintext: string): Promise<boolean>    // constant-time compare
clearPin(): Promise<void>                          // logout flow'da

// Biometric
biometricAvailable(): Promise<boolean>
biometricPrompt(): Promise<boolean>                // success → markUnlocked
```

**Unlock window**: 5 dk (in-memory `lastUnlockAt` timestamp). App process killed = locked. App background → lock değil; foreground'a gelince `isUnlocked()` kontrol edilir.

**Decision**: 5 dk timer in-memory only (Preferences'a yazılmaz) — process restart automatic re-lock istediğimiz davranış.

## PinGate component — `apps/panel/src/components/PinGate.tsx`

**Reusable lock overlay**. Çocuk render'ı `unlocked` state'ine bağlar.

**Variants**:
- `variant="screen"` — full-screen overlay (navigation engellenir; "← Geri" sayfa header'ında var)
- `variant="inline"` + `blurContent` prop — child'a `filter: blur(8px)` + tap-to-unlock button

**States**:
1. **`pinNotSet`** (Hızlı istatistik ilk açılış) → setup wizard:
   - Step 1: "4 haneli PIN belirle" (numeric pad)
   - Step 2: "Tekrar gir" (onay)
   - Step 3 (opsiyonel): "Biyometrik aç?" — eğer `biometricAvailable()` → enrollment offer
   - Step 4: dashboard'a yönlendir, `markUnlocked()`
2. **`pinSet + unlocked`** → child render
3. **`pinSet + locked`** → biometric prompt otomatik → success/cancel/no-biometric → numeric pad fallback

**Inline variant'ta `pinNotSet` davranışı**: PIN olmadığı için kilit yok — child normal render. Bu Ana totalAmount'un eski davranışını korur (yayıncı PIN kurmadıysa görünür).

**Numeric pad**: özel custom (mobile keyboard değil) — 4 hane güvenli giriş için iOS/Android keyboard problemleri yok.

```
┌─────────┐
│ Hızlı   │
│ istat.  │
│ kilitli │
│         │
│ ● ● ● ○ │  ← 4 hane gösterge
│         │
│ 1 2 3   │
│ 4 5 6   │
│ 7 8 9   │
│ ⌫ 0     │
│         │
│ Biometric tekrar dene  │
└─────────┘
```

**Setup wizard layout** (`pinNotSet` state):
- Step 1: "Hassas datayı görüntülemek için 4 haneli PIN belirle" + numeric pad
- Step 2: "PIN'i tekrar gir" + numeric pad
  - Step 2 yanlışsa → "PIN'ler eşleşmedi, tekrar başla" → Step 1
- Step 3 (biometric available ise): "Parmak izi/yüz tanıma ile de açabilirsin" — "Aç" / "Atla"
  - "Aç" → `NativeBiometric.verifyIdentity` ile credential save edilmez (sadece biometric availability'yi confirm eder — PIN her zaman fallback)

## Dashboard UI — `HizliIstatistikScreen.tsx`

```
┌─────────────────────────────────────┐
│ ← Geri                              │
│ Hızlı istatistik                    │
│                                     │
│ [Bugün] [Bu hafta] [Bu ay]         │  ← range tabs (segmented)
├─────────────────────────────────────┤
│ ┌────────────┐ ┌────────────┐      │
│ │ Ciro       │ │ Sipariş    │      │  ← 2x4 grid
│ │ ₺12.450    │ │ 23 adet    │      │
│ └────────────┘ └────────────┘      │
│ ┌────────────┐ ┌────────────┐      │
│ │ Ort. tutar │ │ İptal oranı│      │
│ │ ₺541.30    │ │ %8.7       │      │
│ └────────────┘ └────────────┘      │
│ ┌─────────────────────────────────┐│
│ │ 🔴 Şu an canlı: Mezat #14      ││  ← span 2 (varsa)
│ │ veya: Aktif yayın yok          ││
│ └─────────────────────────────────┘│
│ ┌────────────┐ ┌────────────┐      │
│ │ Bekl.dekont│ │ Bekl. kargo│      │
│ │ 4 adet     │ │ 7 adet     │      │
│ └────────────┘ └────────────┘      │
├─────────────────────────────────────┤
│ Son: 13:42 [↻ Yenile]              │
└─────────────────────────────────────┘
```

**Tab seçimi**: Default `today`. Tab değişir → `useStats(range)` farklı queryKey → cache rerun.

**Refresh**: TanStack `refetch()` butonu + automatic refetch when range changes. `staleTime: 60_000` (1 dk). Manual refresh her zaman fresh fetch.

**Active stream card**:
- Eğer `activeStreamId != null` → 🔴 rozet + title, tıklanabilir (`/siparisler/{id}` → SessionDetailScreen)
- Yoksa: "Aktif yayın yok — WPF'te yayın başlatınca burada görünür" (gri/opak)

**Loading**: 4-card skeleton grid (animate-pulse).
**Error**: "Yüklenemedi — Tekrar dene" button.
**Empty (sıfır data)**: ₺0 / 0 adet gösterilir (yeni broadcaster'lar için), error değil.

## AnaScreen integration

`AnaScreen.tsx`'in mevcut son yayın card'ında `totalAmount`'u `PinGate variant="inline" blurContent` ile sar:

```typescript
import { PinGate } from "../components/PinGate";

// Son yayın card'ı içinde:
<PinGate variant="inline" blurContent>
  <p className="text-base font-bold text-success">
    {formatTl(latest.totalAmount)}
  </p>
</PinGate>
```

**Davranış**:
- PIN set değil → gate kapalı, eski davranış (gözükür)
- PIN set + unlocked → görünür (5 dk window)
- PIN set + locked → CSS blur + "Göster" tap-to-unlock button → PinGate'in lock state UI'sini tetikler

## DahaFazlaScreen integration

1. **"Sonraki sürüm" section'ından** `<DisabledRow label="Hızlı istatistik" hint="PIN ile korumalı, sonraki sürüm" />` satırını sil.
2. **"İçerik" section'ında "Müşteriler" NavRow'unun altına** yeni NavRow ekle:
   ```typescript
   <NavRow
     to="/hizli-istatistik"
     label="Hızlı istatistik"
     hint="Bugünkü ciro, sipariş ve performans — PIN korumalı"
   />
   ```

## Logout integration

`auth/store.ts`'in `logout` action'ında `clearPin()` çağrısı eklenir:

```typescript
import { clearPin } from "../lib/pin";

logout: async () => {
  await clearPin();
  // ... mevcut auth clear logic
}
```

Sonuç: kullanıcı logout → relogin yaparsa PIN sıfırlanmış olur, ilk Hızlı istatistik açılışında setup wizard tetiklenir. Bu "PIN unutuldu" UX'i.

## Android manifest

`apps/panel/android/app/src/main/AndroidManifest.xml`'a ekle (mevcut `<uses-permission>` bloğunun yanına):

```xml
<uses-permission android:name="android.permission.USE_BIOMETRIC" />
```

Capacitor 6 için bu yeterli — `capacitor-native-biometric` plugin install + cap sync sonrası gerekli Android dependencies otomatik gelir.

## Testing

**Server**: 6 test (yukarıda Server bölümünde listelendi). `IClock` mock veya equivalent freeze-time pattern ile `today`/`week`/`month` deterministik.

**Mobile**: `npm run typecheck` clean. Otomatik test yok (broadcast posts pattern'i). Manuel smoke checklist:

- [ ] DahaFazla > Hızlı istatistik tap → ilk kez setup wizard
- [ ] PIN belirle → onay → biometric offer → atla → dashboard görünür
- [ ] Dashboard tabs `Bugün/Bu hafta/Bu ay` arasında geçiş + her birinde data fetch
- [ ] Refresh button çalışıyor
- [ ] App background'a düşür → 5 dk bekle → foreground → locked
- [ ] Locked + biometric available → biometric prompt → success → unlocked
- [ ] Locked + biometric fail/cancel → PIN pad → success → unlocked
- [ ] AnaScreen totalAmount = blur + "Göster" → PinGate prompt → unlocked → görünür
- [ ] Logout → relogin → Hızlı istatistik'e bas → PIN setup wizard tekrar tetiklenir
- [ ] PIN yanlış 5 kez gir → davranış? (out of scope: rate limit yok; ama UX olarak shake animation gösterebilir)

## Out of scope

- PIN rate limit (5 yanlış sonrası bekleme) — YAGNI, casual privacy filter
- PIN değiştirme UI (logout-relogin yolu var)
- 6+ digit PIN
- Server-side PIN sync (cross-device unlock)
- Email-based PIN reset
- Biometric credential storage (sadece availability check)
- DahaFazla'nın diğer placeholder'ı (Yayın Excel raporu) — ayrı spec
- Müşteriler endpoint follow-up (two-pass IQueryable refactor) — başka issue
- AnaScreen dışındaki finansal screen'ler (Dekontlar, Müşteriler, Siparişler) — bu spec sadece Ana + Hızlı istatistik
