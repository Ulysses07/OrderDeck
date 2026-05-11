# OrderDeck Panel — Implementation Plan

**Tarih**: 2026-05-11
**Design doc**: `2026-05-11-orderdeck-panel-design.md`
**Hedef**: Yayıncı için iOS + Android mobile app (Capacitor wrapper)

## Repo stratejisi

İki ayrı repo'da paralel ilerler:

- **LiveDeck** (mevcut): backend tarafı (PanelApiController, PushDevices migration, push servisi). `LiveDeck.sln` değişmez — sadece dosya eklenir.
- **OrderDeck-Mobile** (yeni, açılacak): Capacitor monorepo (`apps/panel`, `apps/customer`, `packages/shared-*`)

PR'lar her iki repo'da paralel açılır. Backend PR'ı önce merge edilir, mobile PR'ı onun endpoint'lerine bağlanır.

## PR sıralaması (toplam 8 PR + repo açma)

Her PR mümkün olduğunca **bağımsız test edilebilir** ve **küçük** (~500-1500 satır).
Test yeşili + manual smoke + merge → bir sonraki PR'a geçiş.

### PR #0 — OrderDeck-Mobile repo'sunu aç (manuel)

```bash
gh repo create Ulysses07/OrderDeck-Mobile --private --description "OrderDeck mobile apps (Panel + Customer) — Capacitor"
git clone git@github.com:Ulysses07/OrderDeck-Mobile.git
```

Initial commit: `.gitignore` (Node + iOS + Android + Capacitor), README placeholder, LICENSE (mevcut LiveDeck pattern'i).

---

### PR #1 — Mobile monorepo iskeleti (OrderDeck-Mobile repo'su)

**Repo**: `OrderDeck-Mobile`

- `package.json` workspace root (npm workspaces)
- `apps/panel/` — Vite + React + TS + Capacitor
- `packages/shared-api/` — placeholder axios client
- `packages/shared-ui/` — placeholder export
- `packages/shared-auth/` — placeholder
- `packages/shared-push/` — placeholder
- Capacitor config (`appId: com.orderdeck.panel`, `appName: OrderDeck Panel`)
- iOS + Android platform `npx cap add ios && npx cap add android`
- Tailwind config
- README: `cd apps/panel && npm run dev` + `npx cap run ios`
- GitHub Actions: lint + typecheck (build no, çünkü mobile build native gerektirir)

**Doğrulama**:
- `npm install` root'tan çalışır
- `cd apps/panel && npm run dev` → Vite dev server açılır
- `npx cap sync` hatasız
- iOS simulator + Android emulator boş app açar ("Hello OrderDeck Panel")

---

### PR #2 — LicenseServer PanelApiController + JWT auth

**Repo**: `LiveDeck`
**Klasör**: `OrderDeck.LicenseServer/`

- `Controllers/PanelApiController.cs` — yeni controller, mevcut `LicensingController` JWT middleware ile korumalı
- `POST /api/panel/auth/login` — email + parola → JWT
- `GET /api/panel/me` — token doğrulama + kullanıcı bilgisi
- `Data/Migrations/AddPushDevices.cs` — PushDevices tablosu
- `POST /api/panel/devices` — push token kayıt
- `DELETE /api/panel/devices/{token}` — logout cleanup
- Unit testler (xUnit): 4 endpoint için happy + auth fail path

**Doğrulama**:
- `dotnet test OrderDeck.LicenseServer.Tests` yeşil
- Postman/curl ile login → JWT alma → `/me` çağrısı

---

### PR #3 — Panel auth UI + login flow

**Repo**: `OrderDeck-Mobile`
**Klasör**: `apps/panel/`

- `shared-auth/`: axios interceptor (JWT header), refresh logic, Capacitor Secure Storage
- Login screen: email + parola form, validation, error toast
- Token persistence (Secure Storage)
- App.tsx: token varsa → home, yoksa → login
- "Çıkış" butonu (placeholder ekran)

**Doğrulama**:
- iOS simulator: login → token saklanır → app restart → otomatik home'a gider
- Yanlış parola → error toast
- Logout → secure storage temizlenir → login'e döner

---

### PR #4 — Dashboard + bottom nav

**Repo**: `OrderDeck-Mobile` (+ küçük LiveDeck PR dashboard endpoint için)
**Klasör**: `apps/panel/`

- Bottom tab navigator (React Router + custom tab bar)
- 4 tab: Ana, Dekontlar (badge), Siparişler, Daha fazla
- Dashboard screen: bugün özeti (3 kart — bekleyen dekont sayısı, bugün sipariş sayısı, aktif yayın varsa adı)
- LicenseServer: `GET /api/panel/dashboard` endpoint
- TanStack Query setup + auth header injection

**Doğrulama**:
- Dashboard kartları gerçek veri gösterir
- Tab geçişleri akıcı, badge dekont sayısını gösterir

---

### PR #5 — Dekont kuyruğu (en kritik ekran)

**Repo**: `OrderDeck-Mobile` + `LiveDeck` (paralel iki PR)
**Klasörler**: `apps/panel/` + `OrderDeck.LicenseServer/`

- LicenseServer:
  - `GET /api/panel/dekontlar?status=pending&take=50`
  - `POST /api/panel/dekontlar/{id}/approve`
  - `POST /api/panel/dekontlar/{id}/reject` (body: `{ reason }`)
  - Audit log satırı her aksiyona
- Mobile:
  - Liste ekranı (FlashList, performant)
  - Kart: PayerName + Tutar + Tarih + ReferansNo + Müşteri linki
  - Swipe-to-approve / swipe-to-reject (react-native-gesture-handler equivalent: `framer-motion` swipe)
  - Reject sebep dropdown (4 seçenek)
  - Pull-to-refresh
  - Empty state ("Bekleyen dekont yok")
  - Optimistic update + rollback on error
- Haptic feedback Capacitor Haptics plugin

**Doğrulama**:
- Backend testler: approve/reject/list happy + auth + 404 path
- Mobile: liste yüklenir, swipe çalışır, onay sonrası kart kalkar, sayı badge güncellenir
- WPF'te aynı anda onaylanırsa mobile refresh'te kart kaybolur (state desync mitigation)

---

### PR #6 — Sipariş listesi + durum güncelleme + müşteri profili

**Repo**: `OrderDeck-Mobile` + `LiveDeck`
**Klasörler**: `apps/panel/` + `OrderDeck.LicenseServer/`

- LicenseServer:
  - `GET /api/panel/yayinlar` — son 30 yayın
  - `GET /api/panel/siparisler?yayin={id}`
  - `PATCH /api/panel/siparisler/{id}` — body: `{ status, kargoNo? }`
  - `GET /api/panel/musteriler/{id}` — ad, telefon, adres, geçmiş sipariş listesi (toplam ciro YOK)
- Mobile:
  - Siparişler tab: yayın seçici (dropdown) + sipariş liste
  - Sipariş kartı: müşteri adı, ürünler özeti, durum chip
  - Tap → bottom sheet → durum radio + kargo no input (kargolandı seçilince visible)
  - Barcode scanner opsiyonel button (Capacitor `@capacitor-community/barcode-scanner`)
  - Müşteri profil ekranı: tap'la geçer, profil bilgileri + geçmiş sipariş listesi

**Doğrulama**:
- Backend testler: list + patch + customer detail
- Mobile: yayın seç → sipariş listesi gelir, durum değiş → kart anlık güncellenir, müşteri profili açılır (ciro yok)

---

### PR #7 — Push notifications + duyuru gönderme

**Repo**: `OrderDeck-Mobile` + `LiveDeck`
**Klasörler**: `apps/panel/` + `OrderDeck.LicenseServer/`

- LicenseServer:
  - Firebase Admin SDK + APNS HTTP/2 client (NuGet: `dotnet-apns`)
  - `services/PushService.cs` — fan-out helper
  - Dekont yükleme webhook'una push trigger: `new_dekont`
  - Yeni sipariş webhook'una: `new_order`
  - `POST /api/panel/duyurular` endpoint (MVP: DB'ye kaydet, müşteri app gelince fan-out aktif)
- Mobile:
  - Capacitor Push Notifications plugin setup
  - Permission request: onboarding'de agresif (ilk login sonrası modal)
  - Token registration → `POST /devices`
  - Notification handler: tap → deep link (dekont/sipariş ekranına)
  - Foreground notification banner
  - Duyuru gönderme ekranı (form: başlık, mesaj, opsiyonel görsel)
- iOS Push capability + APNS .p8 key setup (manuel, README'de adımlar)
- Android: google-services.json setup (manuel, README'de adımlar)

**Doğrulama**:
- iOS real device: dekont yüklendi → push gelir → tap → dekont ekranı açılır
- Android emulator: aynı flow
- Duyuru gönder → DB'ye yazılır (müşteri app yok, sadece kayıt)

---

### PR #8 — Hızlı istatistik (PIN kilitli) + Excel rapor + share

**Repo**: `OrderDeck-Mobile` + `LiveDeck`
**Klasörler**: `apps/panel/` + `OrderDeck.LicenseServer/`

- LicenseServer:
  - `GET /api/panel/istatistik?range=today|week|month` — ciro, sipariş sayısı, kar (X-Pin-Verified header opsiyonel, sadece log)
  - `GET /api/panel/yayinlar/{id}/excel` — ClosedXML ile generate, `application/vnd.openxmlformats-officedocument.spreadsheetml.sheet`
- Mobile:
  - Onboarding'de PIN belirleme adımı (4 hane)
  - PIN: Argon2 hash → Capacitor Secure Storage
  - İstatistik ekranı: tap → PIN modal → doğru ise data, yanlış 3x → 30 sn lockout
  - 5 dk PIN cache (Zustand)
  - Excel rapor ekranı: yayın seçici → "Rapor al" → backend stream → Filesystem.writeFile → Share.share()
  - "Daha fazla" tabında her iki ekrana giriş

**Doğrulama**:
- PIN belirleme + doğrulama + lockout flow çalışır
- 5 dk cache: istatistiğe ikinci kez gir → PIN sorma
- Excel: yayın seç → indir → WhatsApp/email share sheet açılır → dosya iletilir

---

### PR #9 (opsiyonel — submit hazırlık)

- iOS App Store screenshots (5-8 ekran, 6.5" + 5.5")
- Google Play screenshots
- Privacy policy URL (`orderdeckapp.com/gizlilik-panel`)
- App description (TR + EN)
- TestFlight internal testers (5 yayıncı beta)
- Submit to Apple + Google

## Tahmini efor

| PR | Karmaşıklık |
|----|------|
| 1. Monorepo iskelet | Düşük |
| 2. LicenseServer auth | Düşük |
| 3. Auth UI | Düşük |
| 4. Dashboard + nav | Orta |
| 5. Dekont kuyruğu | Yüksek |
| 6. Sipariş + müşteri | Yüksek |
| 7. Push + duyuru | Yüksek |
| 8. İstatistik + Excel | Orta |
| 9. Submit | Düşük |

## Test stratejisi

- **Backend**: xUnit, mevcut pattern (FluentAssertions, FakeHttpMessageHandler, InMemorySqlite)
- **Mobile**: Vitest unit + Playwright component test (kritik flow'lar: login, dekont swipe, PIN)
- **E2E**: yok (MVP); Faz 2'de Detox veya Maestro

## Branch + release stratejisi

- **LiveDeck repo**: backend PR'ları `master`'a feature branch'tan (mevcut pattern)
- **OrderDeck-Mobile repo**: mobile PR'ları `main`'e feature branch'tan
- Mobile versiyonlama: `panel-vX.Y.Z` git tag (sadece OrderDeck-Mobile repo'da)
- Build artifact: TestFlight + Play Internal otomatik (Faz 2 — fastlane)
- MVP: manuel `npx cap build ios && npx cap build android` + Xcode/Android Studio upload

## Riskler ve karar noktaları

- **PR #2 öncesi**: LicenseServer'da Argon2 password hashing var mı? Yoksa mevcut hash mekanizmasına dokun, JWT auth zaten var
- **PR #7 öncesi**: FCM project + APNS .p8 key kullanıcı tarafında hazırlanmalı (Apple Developer hesabı açılınca)
- **PR #5 sonrası**: real yayıncıyla beta test → UX feedback alınmadan PR #6'ya geçilmez

## Sonraki adım

User design doc + bu plan'ı onayladıktan sonra:
1. **PR #0**: `OrderDeck-Mobile` repo'sunu `gh repo create` ile aç (manuel)
2. **PR #1** (OrderDeck-Mobile repo): monorepo iskelet
3. **PR #2** (LiveDeck repo): LicenseServer PanelApiController + push migration

PR #1 ve PR #2 paralel ilerleyebilir (bağımsız repo'larda). PR #3'ten itibaren mobile backend'e bağlandığı için sıralı.
