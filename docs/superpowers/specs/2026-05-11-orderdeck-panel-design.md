# OrderDeck Panel — Yayıncı Mobile App (Design Doc)

**Tarih**: 2026-05-11
**Status**: Brainstorming tamamlandı → implementation plan'a hazır
**Supersedes**: `2026-05-08-mobile-customer-vendor-app-design.md` (tek app + role selection planı; yerine 2 ayrı app)

## Karar Özeti

Tek mobile app + role selection yerine **2 ayrı app**:

- **OrderDeck Panel** — yayıncılar için (önce yapılacak)
- **OrderDeck** — müşteriler için (sonra yapılacak)

Bu doc sadece **Panel**'i kapsar.

## Context

OrderDeck.App (WPF .NET 10) yayıncının ana iş merkezi: canlı yayın chat overlay,
çekiliş, sipariş kuyruğu, etiket yazıcısı. Tüm bunlar masaüstüne bağlı.

Yayıncı bilgisayar başında değilken yapamadıkları:
- Dekont onayı/reddi (en sık şikâyet — müşteri WhatsApp'ta dekont yolluyor, yayıncı yatakta)
- Sipariş hazırlık durumu güncelleme (kargocu personel telefonunu kullanıyor)
- Müşteri telefon/adres bilgisine hızlı erişim
- Yayın sonrası muhasebe için Excel rapor share
- Müşterilere duyuru push'lama

Panel bu gap'i kapatır. WPF'i değiştirmez, tamamlar.

## Hedef Kullanıcılar

1. **İşletme sahibi yayıncı** — full erişim
2. **Çalışan** (kargocu, müşteri hizmetleri) — aynı hesabı paylaşır, sadece ciro/istatistik kilitli

MVP'de role-based hesap yok. Tek lisans → tek hesap → çalışanla paylaşılır. Hassas
ekranlar (istatistik) PIN ile kilitli. Faz 2'de alt hesaplar gelir.

## Scope (MVP)

### Panel ekranları

1. **Login** — email + parola (mevcut LicenseServer hesabı)
2. **Dashboard** — bugün/aktif yayın özeti, son aksiyon kartları
3. **Dekont kuyruğu** — bekleyen ödemeler, onayla/reddet, swipe
4. **Sipariş listesi** — yayın bazlı, durum güncelleme (hazırlanıyor → kargolandı → teslim)
5. **Müşteri profili** — ad, telefon, adres, geçmiş sipariş listesi (ciro yok)
6. **Duyuru gönderme** — yayıncının takipçilerine push notification
7. **Hızlı istatistik** — PIN kilitli — bugün/hafta ciro, sipariş, kar
8. **Yayın Excel raporu** — bittiyi seç → backend export → native share sheet

### Panel'de OLMAYACAK (WPF'te kalır)

- Canlı yayın chat overlay (OBS bağımlı)
- Çekiliş yönetimi (yayın anı, masaüstü)
- Lisans yönetimi
- Yazıcı / etiket boyutu ayarları
- Chrome eklenti kurulum sihirbazı
- Settings (yıllık değişen ayar, WPF yeter)
- Backup/restore

## Mimari

### Stack

- **Capacitor 6** — iOS + Android native wrapper
- **React 18 + TypeScript** — UI layer
- **Vite** — build
- **TanStack Query** — server state
- **Zustand** — local state (PIN cache, push token)
- **Tailwind + shadcn/ui** — design system (mobile uyumlu komponenler)

### Repo stratejisi

Mobile **ayrı GitHub repo**: `github.com/Ulysses07/OrderDeck-Mobile` (private).

Sebepler:
- Farklı stack (.NET vs TS/Capacitor) → ayrı tooling, ayrı IDE, ayrı CI runner
- Mobile build artifact'leri (Pods, .ipa, fastlane certs) LiveDeck repo'sunu şişirir
- Apple/Google Developer hesap setup'ı repo-bazlı kolay
- Release cycle bağımsız (App Store review 2 hafta vs backend daily deploy)
- Tek geliştirici için cross-repo overhead minimal

LiveDeck repo'sunda **sadece** backend tarafı: `OrderDeck.LicenseServer/Controllers/PanelApiController.cs` + push migration. Yeni .sln dosyası açılmaz, mevcut `LiveDeck.sln`'e dahil olur (csproj zaten orada).

### Monorepo yapısı (OrderDeck-Mobile repo'su)

```
OrderDeck-Mobile/
├── apps/
│   ├── panel/        # yayıncı app (önce)
│   └── customer/     # müşteri app (sonra)
├── packages/
│   ├── shared-api/   # LicenseServer API client (axios + react-query hooks)
│   ├── shared-ui/    # ortak komponenler (Button, Card, Input, BottomSheet)
│   ├── shared-auth/  # login + token refresh + secure storage
│   └── shared-push/  # FCM/APNS registration + handler
├── package.json      # npm workspaces root
├── tsconfig.base.json
└── .github/workflows/  # lint + typecheck (mobile build native gerektirir, CI'da değil)
```

İki app farklı Capacitor config (appId, appName, icons), ortak ~%60 kod paylaşır.

### API contract sync

LiveDeck repo'sunda `OrderDeck.LicenseServer` Swagger/OpenAPI üretir
(`/swagger/panel-v1.json`). OrderDeck-Mobile'da `packages/shared-api/` bunu
build script ile çekip TypeScript tip dosyaları generate eder (`openapi-typescript`).
Backend kontratı değişirse mobile build kırılır → drift erken yakalanır.

### Backend (mevcut LicenseServer'a ekleme)

`OrderDeck.LicenseServer/Controllers/PanelApiController.cs` yeni controller:

```
POST /api/panel/auth/login            → JWT (mevcut auth ile aynı)
GET  /api/panel/dashboard             → bugün özeti
GET  /api/panel/dekontlar?status=...  → kuyruk
POST /api/panel/dekontlar/{id}/approve
POST /api/panel/dekontlar/{id}/reject
GET  /api/panel/yayinlar              → yayın listesi
GET  /api/panel/yayinlar/{id}/excel   → Excel byte stream
GET  /api/panel/siparisler?yayin={id} → liste
PATCH /api/panel/siparisler/{id}      → durum güncelle
GET  /api/panel/musteriler/{id}       → profil + geçmiş
POST /api/panel/duyurular             → push gönder
GET  /api/panel/istatistik            → PIN sonrası ciro (header'da X-Pin-Verified)
POST /api/panel/devices               → push token register
DELETE /api/panel/devices/{token}     → logout cleanup
```

Auth: mevcut `LicensingController` JWT pattern'i.

### Veri modeli (yeni tablolar)

**`PushDevices`** (LicenseServer DB)
```
Id            BIGINT PK
UserId        BIGINT FK Users
LicenseId     UNIQUEIDENTIFIER FK Licenses
DeviceId      NVARCHAR(64)   -- Capacitor Device.getId()
Platform      NVARCHAR(16)   -- 'ios' | 'android'
PushToken     NVARCHAR(512)
LastSeenUtc   DATETIME2
CreatedUtc    DATETIME2
```

**`PanelEvents`** (audit; opsiyonel MVP)
```
yayıncı kim, ne zaman, ne onayladı/reddetti
```

Mevcut `Payments`, `Orders`, `Customers`, `Sessions` tablolarına dokunulmaz.

### Push mimarisi

- **FCM** (Android): backend Firebase Admin SDK
- **APNS** (iOS): backend `dotnet-apns` veya direkt HTTP/2

Topic stratejisi: device token-based direct send (topic değil, çünkü yayıncının
sadece kendi device'ları). Yayıncı 2 telefondan login olursa 2 token, her ikisine
yollanır.

Event tipleri:
- `new_dekont` → dekont kuyruğu deep link
- `new_order` → sipariş listesi deep link
- `daily_summary` (Faz 2)

Priority: iOS `time-sensitive`, Android `high`. Default sound. Sessizde çalsın.

### PIN kilidi (sadece istatistik)

- İlk login sonrası onboarding'de 4 hane PIN belirlenir
- PIN hash'i (Argon2) **Capacitor Secure Storage**'da (iOS Keychain, Android Keystore)
- Backend'e gitmez — local-only
- İstatistik ekranına basınca PIN modal'ı
- 5 dakika cache (Zustand state)
- 3 yanlış → 30 sn lockout (local)
- "PIN unuttum" → tekrar login + ana ekrandan PIN sıfırla flow

İstatistik API çağrısında `X-Pin-Verified: <random-session-token>` header. Token
backend'de doğrulanmaz (local guarantee yeterli) ama analytics için loglanır.

### Auth flow

- Mevcut LicenseServer hesap sistemi (`POST /api/auth/login` JWT)
- Mobile login: email + parola → JWT alır
- JWT 7 gün geçerli, refresh token 30 gün
- Logout: token sil + `DELETE /devices/{token}`

Çalışanla paylaşım: yayıncı kendi hesabını çalışana verir (MVP). Tüm cihazlar
aynı hesap → tüm dekont/sipariş push'ları her cihaza gider.

### Offline mode

MVP'de **yok**. Her ekran network gerektirir. Connection lost → banner + retry button.
Faz 2'de sipariş listesi cache + offline read-only.

## UI/UX kararları

### Navigation

Bottom tab bar (4 tab):
- 🏠 Ana sayfa (Dashboard)
- 💳 Dekontlar (badge: bekleyen sayısı)
- 📦 Siparişler
- ⚙️ Daha fazla (Müşteriler, Duyuru, İstatistik, Excel rapor, Çıkış)

### Dekont kuyruğu UX

- Liste: en yeni üstte, kart formatında (PayerName, Tutar, Tarih, ReferansNo, Müşteri)
- Karta tap → detay sheet (PDF değil, sadece parse edilmiş data)
- Swipe sağ → ✓ Onayla
- Swipe sol → ✗ Reddet (sebep dropdown: "tutar uyuşmuyor", "referans no kullanılmış", "dekont sahte görünüyor", "diğer")
- Onay sonrası haptic feedback + toast

### Sipariş durumu güncelleme

- Liste yayın bazlı filtreli
- Sipariş kartında durum chip'i (renkli)
- Tap → bottom sheet → durum seçimi (radio):
  - Hazırlanıyor
  - Paketlendi
  - Kargolandı (+ kargo no input)
  - Teslim edildi
  - İade
- "Kargolandı" seçilince barkod tarama (Capacitor Barcode Scanner) ile kargo no doldurulabilir

### Excel rapor share

- Yayın listesi → bir yayını seç → "Rapor al" butonu
- Backend `GET /yayinlar/{id}/excel` → `application/vnd.ms-excel` stream
- Capacitor Filesystem ile temp'e kaydet
- Capacitor Share API → native share sheet (email, WhatsApp, Drive)
- Dosya adı: `OrderDeck-{yayın-tarihi}-rapor.xlsx`

### Duyuru gönderme

- Form: başlık (max 60), mesaj (max 200), opsiyonel görsel (max 1 MB)
- Hedef: "Tüm takipçilerim" (MVP'de filtreleme yok)
- "Gönder" → backend push fan-out → her takipçinin müşteri app'ine push (müşteri app henüz yok, MVP'de bu sadece DB'ye kaydedilir, müşteri app gelince activate olur)

### Tema

Dark mode default (WPF App ile uyumlu — DarkControls.xaml palette'i).
Light mode setting (Faz 2).

## Güvenlik

- HTTPS only (LicenseServer zaten Caddy + Let's Encrypt)
- JWT short-lived (7 gün) + refresh
- Push token Argon2 hash'lenmez (raw, çünkü FCM/APNS'e iletilmesi gerek)
- PIN device-local, backend'e gitmez
- Audit log: dekont onay/red, durum değişikliği (kim, ne zaman, ne)
- Rate limit: dekont approve/reject 60 req/dk (mevcut LicenseServer middleware)

## Store / Submit

### Apple App Store

- Developer hesap: $99/yıl (kullanıcı açacak)
- Bundle ID: `com.orderdeck.panel`
- TestFlight: internal testing → external testers (~25 yayıncı beta) → review
- App Privacy: dekont metadata, müşteri ad/telefon, push token disclose
- Review riski: ilk publisher 2-4 hafta sürer

### Google Play

- Developer hesap: $25 one-time
- Package: `com.orderdeck.panel`
- Internal Testing → Closed Testing → Production
- Review: 1-7 gün
- Data Safety form: müşteri PII (telefon, adres), banking metadata (ReferansNo, Tutar)

## Maliyet

- Apple Developer: $99/yıl
- Google Play: $25 (tek seferlik)
- FCM: ücretsiz
- APNS: ücretsiz
- VPS: mevcut, ek yük minimal (push fan-out backend tarafında)
- R2 (dekont PDF 1 yıl retention, müşteri app gelince devreye girer): ~$32/yıl @ 100 yayıncı

Toplam ek operasyon yıllık: **~$130** (Apple + R2)

## Riskler ve mitigasyonlar

| Risk | Mitigasyon |
|------|------------|
| Apple ilk review uzun sürer (2-4 hafta) | Erken submit, paralel Google Play geliştirme |
| Push delivery iOS'ta gecikir (1-2 dk) | "Çek-yenile" + WPF App zaten primary, mobile push opsiyonel hızlı haber |
| WPF + mobile state desync (dekont WPF'te onaylanır, mobile pending görünür) | Mobile screen focus'ta auto-refresh + websocket (Faz 2) |
| Çalışan hesap paylaşımı güvenlik riski | İstatistik PIN ile kilitli + Faz 2'de alt hesap |
| Capacitor plugin uyumsuzluk (örn. barcode scanner iOS 18'de bug) | MVP'de barcode opsiyonel, manuel kargo no input fallback |
| Reklamcılık reddi (App Store: "WhatsApp benzeri" kategorize edilirse) | Açık description: "yayıncı yönetim aracı, mesajlaşma yok" |

## Sonraki fazlar

- **Faz 2 (Panel v1.1)**: Alt hesap (rol-based), websocket realtime, offline cache
- **Müşteri app**: ayrı design doc, ayrı PR ailesi
- **Faz 3**: in-app analytics dashboard (chart.js), takvim view (yayın planı), AI dekont sahtelik skoru

## İlgili dosyalar

**LiveDeck repo** (mevcut):
- WPF App: `OrderDeck.App/` (değişiklik yok, sadece Panel API consume eder)
- LicenseServer: `OrderDeck.LicenseServer/Controllers/PanelApiController.cs` (yeni)
- Migration: `OrderDeck.LicenseServer/Data/Migrations/AddPushDevices.cs` (yeni)
- `.sln` değişiklik: yok (csproj zaten içeride, sadece dosya eklenir)

**OrderDeck-Mobile repo** (yeni, ayrı):
- Monorepo: `apps/panel`, `apps/customer`, `packages/shared-*`

## Onay

Bu doc brainstorming sonucu. User onayladıktan sonra implementation plan
(`2026-05-11-orderdeck-panel-implementation-plan.md`) yazılır ve PR sıralaması
çıkarılır.
