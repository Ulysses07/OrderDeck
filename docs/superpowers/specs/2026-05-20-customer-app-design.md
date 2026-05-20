# OrderDeck Müşteri App — Tasarım

**Tarih:** 2026-05-20
**Durum:** Brainstorming tamamlandı, review bekliyor
**Sahibi:** Burak (yayıncı/karar verici)

## Bu spec'in konumu

Bu spec, **`2026-05-15-customer-app-onboarding-design.md`** spec'inin yerini alır ve geniş kapsama taşır:

- 2026-05-15 spec'i sadece **iskelet + onboarding + auth** kapsıyordu (Spec 1)
- Bugünkü spec **MVP'nin tamamını** kapsar: onboarding + 4 ana feature (broadcast feed, sipariş görüntüleme, dekont görüntüleme, PDF dekont yükleme) + push subsystem + dekont fraud koruması + yayıncı kodu yönetimi + rollout planı

Bazı kararlar 2026-05-15 spec'ine göre revize edildi (auth yöntemi, entity isimleri, repo yerleşimi). 2026-05-08 spec'i (`mobile-customer-vendor-app-design.md`) ise vizyon değişikliği sonrası tamamen deprecated; sadece tarihsel referans.

## Karar günlüğü

| Konu | Karar | Sebep |
|------|-------|-------|
| Identity model | 1:N (bir müşteri çoklu yayıncı takip edebilir) | Müşteri zaten birden fazla mağazadan alışveriş yapıyor; tek hesap UX'i basit |
| Yayıncı keşfi | YOK | Vizyon: bir mağaza müşterisi diğer mağazayı görmesin (rekabet hassasiyeti) |
| Eşleşme yöntemi | Yayıncının belirlediği sabit kod (branded handle, örn. `royal`) | SMS OTP maliyetini sıfırlamak; marka kimliği |
| Auth yöntemi | Telefon + parola | SMS maliyeti yok; telefon zaten sipariş eşleşmesi için toplanıyor; TR pazarında tanıdık |
| Email | Opsiyonel | Yaşlı müşteriler email kullanmıyor; varsa parola recovery için ikincil kanal |
| TC | Opsiyonel (form'da), dekont > 9.990 ₺ koşullu zorunlu | MASAK/AML kuralı; KVKK riski olduğu için yalnız gerektiğinde toplanır |
| Sipariş eşleşmesi | (LicenseId, Platform, Username) match | WPF zaten bu üçlüyle siparişi müşteriye bağlıyor; aynı join'i shopper'a uygula |
| Dekont format | Sadece PDF | Banka uygulamasından gelen PDF, screenshot değil; fraud azaltır + parser hazır |
| PDF parse yeri | Server-side | Tek parser kütüphanesi; yayıncı her iki panel'den (WPF + mobile) approve edebilir; mobile bundle bloat yok |
| PDF retention | Yayıncı kararına kadar + 30 gün anlaşmazlık penceresi | Kanıt saklama; 30 sonrası Hangfire job ile sil |
| Fraud kontrolü | Soft flag — yayıncı son söz | False positive müsamahası (banka format farkları) |
| Repo | Yeni `OrderDeck-Shopper` | Tam izolasyon; yayıncı + müşteri UX'i farklı evrim; cross-repo bağımlılık DX karmaşıklığı yaratmasın |
| Entity isimleri | `Shopper`, `ShopperBroadcasterLink`, vs | Mevcut `Customer` entity'si yayıncı; collision kalmasın |
| JWT scheme | `Bearer-Shopper` | Mevcut `Bearer-Customer` dokunulmuyor |
| API prefix | `/api/v1/shopper/*` | Versioned; net ayrım |

## MVP scope

✅ **Dahil**

1. Onboarding + auth (yayıncı kodu + form + telefon/parola)
2. Broadcast feed (mevcut `BroadcastPost` tablosundan, multi-broadcaster filter)
3. Kendi siparişler (read-only, status badge'li)
4. Kendi dekontlar (read-only, status badge'li)
5. PDF dekont yükleme (server-side parse, 6 katmanlı fraud koruması)
6. Push bildirim (3 tür: broadcast / dekont kararı / yeni sipariş)
7. Multi-broadcaster (ek yayıncı ekleme akışı)
8. Profil yönetimi (ad/adres/telefon/email güncelleme, parola değiştirme)
9. Yayıncı tarafı: kod yönetimi UI (WPF Ayarlar + mobile panel DahaFazla)
10. Yayıncı tarafı: IBAN + AccountHolder server'a sync (yeni endpoint)
11. Yayıncı tarafı: WPF lokal Customer kayıtlarının server'a sync'i (sipariş eşleşmesi için)

❌ **Dışarıda** (ileri faz)

- Yayıncı keşfi / arama
- Yorum, like, paylaş feature'ları
- Online ödeme (Stripe/iyzico entegrasyonu)
- Çoklu dil (TR-only)
- Yayıncı üzerinden online sipariş başlatma (broadcast'ten direkt sepete ekleme)
- Müşterilere arası mesajlaşma

## Mimari

### Yeni server entity'leri

```csharp
// Müşteri ana kimliği — telefon ile global unique
public sealed class Shopper
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";        // E.164 (+90...), global unique
    public string PasswordHash { get; set; } = ""; // bcrypt
    public string Address { get; set; } = "";
    public string? Email { get; set; }              // opsiyonel
    public string? Tc { get; set; }                 // opsiyonel; AES at-rest
    public bool NotificationsEnabledBroadcast { get; set; } = true;
    public bool NotificationsEnabledOrders { get; set; } = true;
    public bool NotificationsEnabledPayments { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }  // soft delete + 30g grace
}

// Müşteri ↔ yayıncı bağlantısı (N:N pivot)
public sealed class ShopperBroadcasterLink
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string Platform { get; set; } = "";       // instagram/tiktok/facebook/youtube/web
    public string Username { get; set; } = "";       // platformdaki handle
    public Guid? WpfCustomerId { get; set; }         // (LicenseId, Platform, Username) match
    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }      // soft leave
}

// WPF lokal Customer kayıtlarının server-side hafif kopyası
// Order/Payment satırlarındaki CustomerId string'i bu tablodaki Id'ye işaret eder
public sealed class WpfCustomerProjection
{
    public Guid Id { get; set; }              // WPF lokal Customer.Id (GUID hex)
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// Shopper push device tokenları
public sealed class ShopperPushDevice
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public string DeviceId { get; set; } = "";       // shopper-app local UUID
    public string Platform { get; set; } = "";        // ios / android
    public string PushToken { get; set; } = "";       // FCM/APNs token
    public DateTimeOffset UpdatedAt { get; set; }
}

// Dekont submission audit (fraud iz takibi)
public sealed class PaymentSubmissionAudit
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Guid ShopperId { get; set; }
    public string IpAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string FraudFlags { get; set; } = "";     // comma-separated
    public string ParserConfidence { get; set; } = ""; // High/Medium/Low/Unknown
    public string? ParserRawText { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

### License entity ek alanları

```csharp
// License entity'sine eklenenler:
public string? ShopperCode { get; set; }              // örn. "royal"; case-insensitive unique
public DateTimeOffset? ShopperCodeUpdatedAt { get; set; }
public string? PaymentIban { get; set; }              // WPF Settings.Iban sync
public string? PaymentAccountHolder { get; set; }     // WPF Settings.AccountHolder sync
public bool ShopperAppEnabled { get; set; } = false;  // feature flag
```

### Payment entity ek alanları

```csharp
// Mevcut Payment entity'sine eklenenler:
public Guid? ShopperId { get; set; }                  // null = WhatsApp/manual akış
public string? MediaObjectKey { get; set; }           // R2 PDF blob key
public string? MediaContentType { get; set; }         // "application/pdf"
public string? PdfHash { get; set; }                  // SHA256 of PDF bytes, global unique
public string? MetadataHash { get; set; }             // SHA256 of canonical metadata
public string? RecipientIban { get; set; }            // parser çıkarımı
public string? RecipientName { get; set; }            // parser çıkarımı
public string FraudFlags { get; set; } = "";          // "iban-mismatch,metadata-duplicate,low-confidence"
public string ParserConfidence { get; set; } = "Unknown"; // High/Medium/Low/Unknown
public DateTimeOffset? PdfPurgedAt { get; set; }      // Hangfire job set ettiğinde
```

### Yeni server endpoint'leri

#### Shopper-facing (`/api/v1/shopper/*`, `Bearer-Shopper` scheme)

```
GET /api/v1/shopper/broadcasters/code-lookup?code=royal
  Anonim (no auth gerekli — onboarding'in ilk adımı)
  → 200 + { licenseId, displayName }
  → 404 if bulunamadı
  Rate limit: IP başına 30/dakika (brute-force bulma koruması)

POST /api/v1/shopper/auth/register
  body: { broadcasterCode, fullName, phone, password, address, platform, username, email?, tc? }
  → 201 + { accessToken, refreshToken, shopperId, broadcasters[] }
  Akış: kod lookup → tenant belirle → telefon collision check (existing varsa
  parola doğrula → mevcut shopper'a yeni link ekle) → yeni shopper veya link
  oluştur → WpfCustomerProjection match (Platform, Username) → JWT issue

POST /api/v1/shopper/auth/login
  body: { phone, password }
  → 200 + { accessToken, refreshToken, shopperId, broadcasters[] }

POST /api/v1/shopper/auth/refresh
  body: { refreshToken }
  → 200 + { accessToken, refreshToken (rotated) }

POST /api/v1/shopper/auth/forgot-password
  body: { phone }
  → 202 (her zaman, enumeration koruması)
  Akış: shopper'ın bağlı olduğu tüm yayıncıların paneline "destek talebi" düşür;
  yayıncı WhatsApp ile geçici parolayı manuel yollar

POST /api/v1/shopper/auth/change-password
  Bearer-Shopper
  body: { currentPassword, newPassword }
  → 204

GET /api/v1/shopper/me
  Bearer-Shopper
  → 200 + { fullName, phone, address, email?, tc?, broadcasters[], notificationPrefs{} }

PATCH /api/v1/shopper/me
  Bearer-Shopper
  body: { fullName?, address?, email?, notificationPrefs?{...} }
  → 200 + updated profile

DELETE /api/v1/shopper/me
  Bearer-Shopper
  → 204 (soft delete + 30g grace)

POST /api/v1/shopper/broadcasters/join
  Bearer-Shopper
  body: { broadcasterCode, platform, username }
  → 200 + { broadcasters[] (updated) }
  Akış: zaten kayıtlı shopper yeni yayıncı ekliyor — kişisel bilgi yeniden istenmez

DELETE /api/v1/shopper/broadcasters/{licenseId}
  Bearer-Shopper
  → 204 (ShopperBroadcasterLink.LeftAt set, soft)

GET /api/v1/shopper/feed?cursor=...&licenseId=...
  Bearer-Shopper
  licenseId opsiyonel: yoksa tüm bağlı yayıncıların post'ları birleşik
  → 200 + { posts[], nextCursor }

GET /api/v1/shopper/broadcasters/{licenseId}/orders?cursor=...&status=...
  Bearer-Shopper
  Akış: ShopperBroadcasterLink.WpfCustomerId üzerinden Order tablosunda join
  → 200 + { orders[], nextCursor }

GET /api/v1/shopper/broadcasters/{licenseId}/payments?cursor=...&status=...
  Bearer-Shopper
  → 200 + { payments[], nextCursor }

POST /api/v1/shopper/broadcasters/{licenseId}/payments
  Bearer-Shopper
  multipart/form-data: pdf (file), amount?, payerName?, paidAt?, referansNo?
  → 201 + { paymentId, parsedMetadata{...}, fraudFlags[] }
  Akış: PDF magic byte check → PdfDekontParser.Parse → metadata fill →
  duplicate check (PdfHash + MetadataHash) → IBAN match → fraud flags →
  R2 upload → Payment row + ShopperPushFanout (yayıncıya push)

POST /api/v1/shopper/devices
  Bearer-Shopper
  body: { deviceId, platform, pushToken }
  → 204 (upsert)

DELETE /api/v1/shopper/devices/{deviceId}
  Bearer-Shopper
  → 204
```

#### Yayıncı-facing (mevcut `Bearer-Customer` scheme, yeni endpoint'ler)

```
GET /api/panel/shopper-code
  → 200 + { code, updatedAt, canChangeAt }

PUT /api/panel/shopper-code
  body: { code }
  → 200 + { code, updatedAt }
  Validation: kod kuralları + cooldown (son değişimden ≥ 7 gün) + global unique
  Bu endpoint hem mobile panel hem WPF tarafından çağrılır

GET /api/panel/payments  (mevcut endpoint — response shape genişler)
  Yeni alanlar: shopperId?, mediaObjectKey?, mediaContentType?, fraudFlags?, parserConfidence?

POST /api/licenses/{licenseId}/payment-account
  Bearer-License (WPF)
  body: { iban, accountHolder }
  → 204
  Yayıncı IBAN/AccountHolder server'a sync edilir (fraud kontrolünde kullanılır)

POST /api/licenses/{licenseId}/wpf-customers/sync
  Bearer-License (WPF)
  body: { customers: [{ id, platform, username, fullName?, phone?, address?, updatedAt }] }
  → 200 + { synced: N }
  WPF lokal Customer kayıtlarının periyodik sync'i (sipariş eşleşmesi için)
```

### JWT scheme

| Scheme | Bearer-Customer (yayıncı) | Bearer-Shopper (müşteri) |
|--------|---------------------------|--------------------------|
| Audience | `orderdeck-customer` (mevcut, dokunulmuyor) | `orderdeck-shopper` |
| Subject | `Customer.Id` (yayıncı kimliği) | `Shopper.Id` |
| Claims | `sub`, `tcid`, `principal=customer`, `email` | `sub`, `principal=shopper`, `phone` |
| Access lifetime | 15 dk | 30 dk |
| Refresh lifetime | 30 gün | 90 gün |
| Refresh rotation | Single-use, rotated on use | Single-use, rotated on use |

**Bearer-Customer rename**: `Bearer-Broadcaster` adına dönüştürme isteği ileri faz. Bu spec'in kapsamında değil — invasive ve mevcut yayıncı app'ini kırma riski yüksek. Convention test (PR #74) kapsamı korur.

### Repo yapısı

Yeni `OrderDeck-Shopper` repo:

```
OrderDeck-Shopper/
├── apps/
│   └── shopper/                    # tek mobile app
│       ├── android/                # Capacitor Android proje
│       ├── ios/                    # Capacitor iOS proje
│       ├── src/
│       │   ├── api/                # axios client (yayıncı panel'inden adapte)
│       │   ├── auth/               # shopper auth store
│       │   ├── components/         # ScreenShell, EmptyState, LoadingView, ErrorView (panel'den kopya)
│       │   ├── lib/
│       │   │   ├── push.ts         # Capacitor PushNotifications (panel'den adapte)
│       │   │   ├── deepLink.ts     # shopper-specific route map
│       │   │   ├── platform.ts     # PLATFORMS (panel ile aynı)
│       │   │   ├── pdfValidation.ts # magic byte + MIME check
│       │   │   ├── phoneNormalize.ts # +90 prefix normalize
│       │   │   ├── pin.ts          # opsiyonel — finansal ekranlar
│       │   │   └── format.ts       # formatTl, formatRelative (panel ile aynı)
│       │   ├── screens/
│       │   │   ├── OnboardingCodeScreen.tsx
│       │   │   ├── OnboardingFormScreen.tsx
│       │   │   ├── LoginScreen.tsx
│       │   │   ├── FeedScreen.tsx
│       │   │   ├── SiparislerimScreen.tsx
│       │   │   ├── DekontlarımScreen.tsx
│       │   │   ├── DekontYukleScreen.tsx
│       │   │   ├── ProfilScreen.tsx
│       │   │   └── YayinciEkleScreen.tsx
│       │   ├── router.tsx
│       │   └── App.tsx
│       ├── vite.config.ts
│       ├── tsconfig.app.json
│       └── package.json
├── packages/                       # ilk fazda boş; ihtiyaç olursa extract edilir
├── package.json                    # workspace root
├── eslint.config.js                # v9 flat config (panel'deki v9 sorununu tekrarlamamak için baştan v9 kuruyoruz)
└── README.md
```

**Drift kabul**: shared paketler yayıncı panel'inden **kopyalanır**, npm package olarak paylaşılmaz. İlk faz cross-repo bağımlılık DX'ini bozar. İleride ortak parçalar belirginleşirse (auth refresh interceptor pattern'i muhtemelen) ortak npm package'a extract edilir.

### Yeni server lib: `OrderDeck.PdfParsing`

Mevcut `OrderDeck.Core/Payments/PdfDekontParser.cs` ve testleri yeni küçük lib'e taşınır:

```
OrderDeck.PdfParsing/
├── PdfDekontParser.cs              # mevcut kod birebir taşınır
├── BankSignatures.cs               # Türk bankaları için regex set (mevcut)
└── OrderDeck.PdfParsing.csproj     # sadece PdfPig dependency
```

`OrderDeck.Core.csproj` → `<ProjectReference Include="OrderDeck.PdfParsing.csproj" />` (parser kullanan WPF lokal kod etkilenmez, namespace değişir).
`OrderDeck.LicenseServer.csproj` → aynı reference. Hem WPF lokal parse hem server-side parse aynı kütüphaneyi kullanır.

## Auth + onboarding akışı

### A. İlk kayıt (yeni shopper)

```
1. App açılır → Welcome ekranı: "Yayıncı kodunu gir"
2. Kod gir: "royal"
3. Server lookup: GET /api/v1/shopper/broadcasters/code-lookup?code=royal
   → 200 + { licenseId, displayName: "Royal Mezat" }
   → 404 if bulunamadı
4. "Royal Mezat'a hoş geldin" ekranı, form:
   - Ad Soyad (zorunlu)
   - Telefon (zorunlu, TR format normalize)
   - Parola (zorunlu, ≥ 8 karakter)
   - Adres (zorunlu)
   - Platform dropdown (Instagram/TikTok/Facebook/YouTube)
   - Sosyal medya kullanıcı adı (zorunlu)
   - Email (opsiyonel)
   - TC (opsiyonel — explain text: "9.990 ₺ üzeri dekont göndereceksen sonradan istenir")
5. "Kaydı tamamla" → POST /api/v1/shopper/auth/register
6. Server:
   - Telefon mevcut shopper'ı vuruyorsa → parola doğrula → mevcut shopper'a yeni link ekle (`ShopperBroadcasterLink`)
   - Aksi halde yeni Shopper + yeni link
   - WpfCustomerProjection match: (LicenseId, Platform, Username) → bulduysa `WpfCustomerId` set
   - Match yoksa null — WPF sync'i yeni Customer'ı ekleyince bir gece cron'u retroactive match yapar
7. 201 → JWT al, ana ekrana (FeedScreen)
```

**Hata durumları**:
- Telefon mevcut + parola yanlış → "Bu telefonla daha önce kaydolmuşsun. Mevcut parolanı doğrula veya 'Parolamı unuttum' kullan."
- Yayıncı kodu yanlış → "Bu kodla bir mağaza bulamadık. Yayıncından doğru kodu iste."
- Validation hataları → field-level mesajlar

### B. İkinci yayıncı eklemek (mevcut shopper)

```
1. Ana ekran (FeedScreen) → "+ Yayıncı ekle" butonu
2. YayinciEkleScreen → kod gir: "antika"
3. Server lookup → "Antika Dükkanı" → onay ekranı
4. Form (kısaltılmış):
   - Platform dropdown
   - Sosyal medya kullanıcı adı
5. POST /api/v1/shopper/broadcasters/join → yeni ShopperBroadcasterLink
6. FeedScreen'e dön, yatay yayıncı seçici güncellenir
```

### C. Login (geri dönen kullanıcı)

```
1. App açılır, JWT yoksa LoginScreen
2. Telefon + parola
3. POST /api/v1/shopper/auth/login → JWT
4. FeedScreen
```

### D. Parola unutma (SMS maliyetsiz)

```
1. LoginScreen → "Parolamı unuttum"
2. Telefon gir → POST /api/v1/shopper/auth/forgot-password
3. Server: shopper'ın bağlı olduğu tüm yayıncıların paneline destek talebi düşür
   (yayıncı panel'de yeni "Şüpheli/Destek talepleri" bölümü)
4. Yayıncı destek talebine bakar, "Geçici parola yolla" butonuna basar
5. WhatsApp template hazırlanır: "Merhaba {Ad}, geçici parolan: {parola}. Lütfen app'ten değiştir."
6. Yayıncı kendi WhatsApp'ından mesajı yollar
7. Shopper geçici parola ile login → app içinden parola değiştir (zorunlu)
```

Yayıncı UX'i ekstra iş yüklüyor — ama SMS sıfır maliyeti karşılığında. İleride paid feature olarak self-service SMS reset eklenebilir.

### E. Profil yönetimi

ProfilScreen:
- Ad/Adres/Email/TC inline edit
- Telefon değişimi → ayrı flow (yeniden auth) — MVP'de YOK, "destek talebi" üzerinden
- Parola değiştir → POST /api/v1/shopper/auth/change-password
- Bildirim toggle'ları (3 kategori)
- Bağlı yayıncılar listesi → her satırın yanında "Ayrıl" butonu (link soft delete)
- Hesap sil (alt'ta tehlikeli butonun) → POST DELETE /api/v1/shopper/me → 30g grace

## MVP feature akışları

### A. Broadcast feed

**Veri**: `GET /api/v1/shopper/feed?cursor=...&licenseId=...`
- `licenseId` yoksa tüm bağlı yayıncıların post'ları zaman sırasıyla karışık
- Server: `BroadcastPost` tablosundan, `DeletedAt IS NULL AND ExpiresAt > now()` filter, License'lar `ShopperBroadcasterLink` join'i ile filtrelenir
- Cursor: `{ExpiresAt|CreatedAt}|{Id}` composite (panel'deki cursor pattern'i)

**UI**: FeedScreen
- Üstte yatay yayıncı seçici chip'ler ("Tümü" / "Royal Mezat" / "Antika Dükkanı")
- Liste: yayıncı panel'indeki DuyurularScreen'in ayna versiyonu
  - Photo/video önizleme, expand modal
  - Pinned post'lar üstte (mevcut server-side sort)
- Pull-to-refresh + infinite scroll
- Lazy media: yayıncı panel'inden `IntersectionObserver` pattern (PR #21)
- Read-only — yorum/like yok

### B. Siparişlerim

**Veri**: `GET /api/v1/shopper/broadcasters/{licenseId}/orders?cursor=...&status=...`
- Server: `Order` tablosunda `LicenseId = X AND CustomerId = ShopperBroadcasterLink.WpfCustomerId`
- `WpfCustomerId` NULL ise sonuç boş — yayıncı sync edince bağlanır

**UI**: SiparislerimScreen
- Üstte yatay yayıncı seçici (multi-broadcaster ise)
- Liste item: tarih, mesaj/açıklama, kod (varsa), tutar, durum badge'i (basılmış/iptal/kargoda)
- Tıklanınca detay modal: tam mesaj, fotograf (varsa), tüm timeline

**Empty state** (henüz match yok):
> "Henüz siparişin görünmüyor. Yayıncı yeni siparişlerini sisteme ekledikçe burada görürsün."

### C. Dekontlarım

**Veri**: `GET /api/v1/shopper/broadcasters/{licenseId}/payments?cursor=...&status=...`
- Server: `Payment` tablosunda `LicenseId = X AND ShopperId = currentShopperId`
- Eski WhatsApp akışından gelen dekontlar (ShopperId NULL) görünmez — sadece app'ten gönderilenler

**UI**: DekontlarımScreen
- Liste item: tarih, tutar, durum badge'i, sebep (rejected ise)
- Rejected'lar üstte highlight
- "Yeni dekont gönder" FAB

### D. Dekont yükle (en kritik feature)

**UI akışı**:

```
1. DekontlarımScreen → FAB veya "Yeni dekont gönder" CTA
2. DekontYukleScreen:
   - Yayıncı seç (multi-broadcaster ise)
   - "PDF dekont seç" butonu → Capacitor Filesystem native picker
     - Android: ACTION_GET_CONTENT MIME=application/pdf
     - iOS: UIDocumentPickerViewController PDF UTI
   - Client validation:
     - Dosya boyutu ≤ 5 MB
     - İlk 5 byte = "%PDF-" magic
     - MIME = application/pdf
   - Form (opsiyonel — server parse'tan otomatik dolar):
     - Tutar
     - Ödeyen ad
     - Referans no
     - Ödeme tarihi
   - "Gönder" butonu (disabled until PDF seçilene kadar)
3. POST /api/v1/shopper/broadcasters/{licenseId}/payments (multipart)
4. Server akışı (aşağıda detay)
5. Response:
   - 201 → "Dekontun gönderildi" + Dekontlarım'a dön (yeni satır pending görünür)
   - 400 invalid-pdf → "Dekont okunamadı. Lütfen banka uygulamandan başka bir PDF dene."
   - 409 duplicate-dekont → "Bu dekontu daha önce göndermişsin."
   - 409 cross-tenant-duplicate → "Bu dekont başka bir yayıncıya gönderilmiş görünüyor."
   - 429 → "Çok hızlı gönderiyorsun. Birazdan tekrar dene."
```

**Server akışı (`POST /api/v1/shopper/broadcasters/{licenseId}/payments`)**:

```
1. Authentication: Bearer-Shopper validate
2. ShopperBroadcasterLink check: shopper bu licenseId'ye bağlı mı? → değilse 403
3. Rate limit check:
   - Shopper başına 5 dekont/saat
   - License başına 150 dekont/saat
   → aşılırsa 429
4. Multipart parse → PDF bytes
5. Magic byte check (ilk 5 byte = "%PDF-") → değilse 400 invalid-pdf
6. PdfDekontParser.Parse(bytes):
   - Throw ederse (geçersiz PDF) → 400 invalid-pdf
   - Result: PayerName, Amount, PaidAt, ReferansNo, RecipientIban, RecipientName, PdfHash, ParserConfidence
7. Duplicate guards:
   - PdfHash global unique check → varsa duplicate:
     - Aynı shopperId + licenseId → 409 duplicate-dekont
     - Farklı tenant → 409 cross-tenant-duplicate + audit
   - MetadataHash (SHA256(amount|payerName|paidAt|referansNo|recipientIban)) check:
     - Aynı tenant'ta varsa → FraudFlag = "metadata-duplicate" (soft, yine kabul)
8. Fraud kontrolleri:
   - License.PaymentIban vs RecipientIban karşılaştırma (normalize: boşluk sil, uppercase):
     - Match → OK
     - Mismatch → FraudFlag += "iban-mismatch"
   - ParserConfidence:
     - Unknown/Low → FraudFlag += "low-confidence"
   - License.PaymentIban NULL (yayıncı henüz sync etmedi) → FraudFlag += "no-iban-baseline"
9. Client form override:
   - Client'tan gelen Amount/PayerName/PaidAt/ReferansNo doluysa client öncelikli
   - Aksi halde parser değerleri kullan
10. TC check (>9.990 ₺ kuralı):
    - Amount > 9990 AND Shopper.Tc IS NULL → 400 tc-required ("Bu tutarda dekont için TC kimlik gerekli, profilden ekle")
11. R2 upload (PDF blob)
12. Payment row insert + PaymentSubmissionAudit insert
13. Push notification fan-out:
    - Yayıncıya: "Yeni dekont — {PayerName}, {Amount}₺" + fraud flag varsa "(uyarılı)"
14. 201 + { paymentId, parsedMetadata, fraudFlags }
```

**Yayıncı panel'inde dekont görüntüleme** (WPF + mobile, mevcut PaymentsScreen genişletilir):
- Dekont satırı `FraudFlags` doluysa sol bordür sarı/kırmızı + chip'ler
- "PDF aç" butonu:
  - Mobile (Capacitor): R2 signed URL → native PDF viewer (Capacitor Browser plugin)
  - WPF: signed URL → system default PDF viewer (Adobe/Edge)
- Approve/reject mevcut endpoint'ler (`PUT /api/panel/payments/{id}/approve|reject`)
- Approve sonrası: 30 gün sayacı başlar → Hangfire job PDF'i R2'dan siler + `Payment.PdfPurgedAt` set eder

## Push subsystem

### 3 bildirim türü

| Tür | Tetikleyici (server) | data.type | Mobile route |
|-----|----------------------|-----------|--------------|
| Broadcast post | Yayıncı yeni BroadcastPost create | `shopper-broadcast` | `/feed/:licenseId` |
| Dekont kararı | Payment.Approve/Reject | `shopper-payment` | `/dekontlar/:paymentId` |
| Yeni sipariş | Order sync ile yeni satır + WpfCustomerId match | `shopper-order` | `/siparislerim/:orderId` |

### Server infrastructure

- `INotificationSender` arayüzü genişletilir: `SendToShopperAsync(shopperId, title, body, data, ct)`
- `FcmNotificationSender` shopper push device'lardan token alır (ayrı tablo: `ShopperPushDevice`)
- Notification preferences kontrolü: `Shopper.NotificationsEnabledX` false ise skip
- Stale token handling: yayıncı panel'indeki ile aynı pattern (Unregistered → DB'den sil)

### Mobile (shopper app) infrastructure

- `lib/push.ts` — yayıncı panel'indeki `push.ts`'in kopyası, sadece endpoint farklı:
  - Register: `POST /api/v1/shopper/devices`
  - Unregister: `DELETE /api/v1/shopper/devices/{deviceId}`
  - Preferences key naming: `orderdeck.shopper.push.enabled.v1` (pin.ts/yayıncı şemasıyla aynı pattern)
- `lib/deepLink.ts` — yeni route map (yayıncı app'ten farklı):

```typescript
const ROUTE_BY_TYPE: Record<string, (data) => string> = {
  "shopper-broadcast": (d) => `/feed/${d.licenseId}`,
  "shopper-payment": (d) => `/dekontlar/${d.paymentId}`,
  "shopper-order": (d) => `/siparislerim/${d.orderId}`,
};
```

- Cold-start tap için pending-deep-link buffer (yayıncı panel'den birebir pattern)

## Yayıncı kodu yönetimi

### Kod kuralları (2026-05-15 spec'inden devralındı)

- 3–20 karakter, sadece `a-z` + `0-9` (case-insensitive)
- DB'de lowercase normalize edilir
- Reserved kelimeler: `admin`, `support`, `login`, `app`, `panel`, `orderdeck`, `dashboard`, `api`, `customer`, `shopper`, `auth`
- Küfür filtresi (TR + İng ortak listesi, server-side)
- Global unique (case-insensitive index)
- Değiştirilebilir, ama **cooldown 7 gün** (`License.ShopperCodeUpdatedAt` kontrolü)
- Eski kod hemen serbest kalır, başkası alabilir (ama not edilir audit log'da)
- `AuditEvents.ShopperCodeChanged` — değişimde audit log

### UI (yayıncı tarafı)

**WPF Ayarlar**: yeni "Müşteri App" sekmesi
- "Müşteri davet kodu" başlığı
- Mevcut kod (varsa) gösterilir
- "Düzenle" butonu → input + canlı validation feedback
- Save → `PUT /api/panel/shopper-code`

**Mobile panel DahaFazlaScreen**: "Hesap" bölümünde yeni satır
- "Müşteri davet kodu" → tıklanınca yeni `ShopperCodeScreen`
- Aynı edit deneyimi WPF ile (input + validation)

## KVKK + güvenlik

| Veri | Hassasiyet | Saklama | Erişim |
|------|------------|---------|--------|
| Ad Soyad | Normal | Plain | Yayıncı + shopper |
| Telefon | Normal | Plain | Yayıncı + shopper |
| Adres | Normal | Plain | Yayıncı + shopper |
| Email | Normal | Plain | Shopper kendisi (yayıncı görür) |
| **TC** | **Hassas** | **AES at-rest (app-level)** | Shopper kendisi + yayıncı (görme yetkisi audit'lenir) |
| PasswordHash | Hassas | bcrypt (cost 12) | Asla read endpoint'inden dönmez |
| PDF dekont | Hassas (finansal) | R2, encrypted-at-rest, retention max 30g post-decision | Yayıncı + shopper |
| ParserRawText | Hassas | DB plain (audit için), retention 30g | Yalnız audit endpoint'i |

### Retention politikaları

- **PDF dekont**: yayıncı kararı sonrası 30 gün → Hangfire job `PdfPurgeJob` (günlük çalışır):
  - `Payment.Status != Pending AND DecisionAt < now - 30d AND PdfPurgedAt IS NULL`
  - R2'dan blob sil, `PdfPurgedAt = now` set
  - `MediaObjectKey` ve `PdfHash` kalır (audit + duplicate guard için)
- **Shopper hesabı silme**: soft delete → 30 gün grace → Hangfire job hard delete (Payment, Order linkleri anonymize)
- **PaymentSubmissionAudit**: 90 gün retention

### KVKK aydınlatma metni

Yeni `docs/shopper-privacy.md` — onboarding'in son ekranında "Kayıt olarak [Gizlilik Politikası]'nı okuduğumu onaylıyorum" checkbox'ı zorunlu. Linke tıklayınca in-app webview ile metin gösterilir.

## Test stratejisi

### Server unit + integration tests

| Test sınıfı | Kapsam |
|-------------|--------|
| `ShopperControllerConventionTests` | Tüm `Controllers.Shopper` namespace'inde `[Authorize("Bearer-Shopper")]` + `[ApiController]` |
| `ShopperAuthControllerTests` | Register (yeni + existing), login, refresh, forgot-password, change-password, duplicate phone, kod yanlış |
| `ShopperFeedControllerTests` | Multi-broadcaster merge, cursor pagination, expired post filter, ShopperBroadcasterLink isolation |
| `ShopperOrdersControllerTests` | Match var/yok senaryoları, cursor, status filter, cross-shopper isolation |
| `ShopperPaymentsControllerTests` | **En kritik**: PDF parse, IBAN match/mismatch, PdfHash duplicate (intra/cross-tenant), MetadataHash duplicate, rate limit (5/saat shopper, 150/saat license), parser confidence, TC > 9990 kuralı |
| `PdfDekontParserIntegrationTests` | `OrderDeck.PdfParsing` lib'inde mevcut testler taşınır + shopper akışı için fixture PDF'ler |
| `ShopperCodeControllerTests` | Kod kuralları (regex, reserved, küfür), uniqueness, 7g cooldown, audit log |
| `WpfCustomerSyncControllerTests` | Sync endpoint upsert, eski kayıt silme, retroactive `ShopperBroadcasterLink.WpfCustomerId` match cron'u |
| `ShopperPushDispatchTests` | 3 notification türünün doğru shopperId'lere fan-out, opt-out filtresi, stale token cleanup |

### Mobile unit tests (vitest)

| Dosya | Kapsam |
|-------|--------|
| `lib/platform.test.ts` | Aynı yayıncı app'tekiyle (kopya) |
| `lib/deepLink.test.ts` | 3 shopper route + unknown type warn |
| `lib/pdfValidation.test.ts` | Magic byte check, size limit, MIME validation |
| `lib/phoneNormalize.test.ts` | TR formatları: +90, 0 prefix, boşluklu/boşluksuz, edge cases |
| `lib/passwordRules.test.ts` | Min 8, complexity, common password reject |

### Manuel smoke checklist (PR sonrası)

1. WPF Settings → IBAN/AccountHolder ayarla → server'a sync olduğunu doğrula
2. Yayıncı WPF Ayarlar → davet kodu belirle ("royal")
3. WPF'te 5-10 müşteri kaydı oluştur → server'a sync olduğunu doğrula
4. Shopper app: kod "royal" gir → form doldur → kayıt → login
5. WPF'teki bir müşteriyle aynı platform+username → orders ekranında geçmiş siparişler görünüyor
6. WPF'te shopper'a yeni sipariş ekle → push gelir → tap → SiparişDetay
7. Multi-broadcaster: ikinci yayıncıya da kayıt → feed iki yayıncıyı birden gösteriyor
8. Broadcast post yayınla → push gelir → tap → feed
9. Shopper PDF dekont yükle (gerçek banka PDF):
   - Parse başarılı, fraud flag yok → yayıncı panel pending görünür
   - Yanlış IBAN'a yapılmış PDF → yayıncı panel iban-mismatch flag görünür
   - Aynı PDF tekrar yükle → 409 duplicate
   - >9990 ₺ + TC boş → 400 tc-required
10. Yayıncı approve → shopper push gelir → DekontlarımScreen approved görünür
11. Approve 30 gün sonra (test'te zamanı manipüle et veya Hangfire job manuel tetikle) → R2'dan PDF silindi, PdfPurgedAt set
12. Yayıncı kodu 7 gün cooldown: hemen değiştir → 429 try
13. Hesap sil → 30 gün sonra hard delete (manuel job tetikle)

## Rollout fazları

### Faz 0 — Server + WPF prep (hiçbir mobile app yok)

- `OrderDeck.PdfParsing` lib extract (mevcut parser taşınır, testler taşınır, OrderDeck.Core + LicenseServer reference)
- LicenseServer entity migrations: `Shopper`, `ShopperBroadcasterLink`, `WpfCustomerProjection`, `ShopperPushDevice`, `PaymentSubmissionAudit`
- License + Payment entity ek alanları
- `Bearer-Shopper` JWT scheme infrastructure
- Tüm `/api/v1/shopper/*` endpoint'leri (stub değil, gerçek implementasyon)
- WPF tarafı: IBAN/AccountHolder sync background task, Customer projection sync
- WPF Ayarlar → Müşteri davet kodu UI
- Mobile panel DahaFazlaScreen → Müşteri davet kodu UI

**Çıktı**: production'a alınabilir; shopper app henüz yok ama yayıncı tarafı hazır.

### Faz 1 — Mobile MVP (ilk dış kullanım)

- Yeni `OrderDeck-Shopper` repo + Capacitor 6 setup
- 4 ekran: OnboardingCode + OnboardingForm + Login + Feed
- Auth flow (register + login + refresh)
- shared-ui kopyaları (LoadingView, ErrorView, EmptyState)
- vitest setup + temel test'ler
- TR-only, dark theme

**Çıktı**: 2-3 beta yayıncıyla closed test (feed görüntüleme).

### Faz 2 — Siparişlerim + Dekontlarım

- Read-only ekranlar
- Multi-broadcaster filter (yayıncı seçici chip'ler)
- Empty state'ler

**Çıktı**: beta'lar geçmiş siparişlerini görüyor.

### Faz 3 — Dekont yükle (en kritik)

- DekontYukleScreen
- 6 fraud katmanı production'da aktif
- Rate limit'ler enable
- PDF retention cron job devreye girer

**Çıktı**: gerçek dekontlar app üzerinden geliyor; yayıncılar mobil + WPF her ikisinden approve edebiliyor.

### Faz 4 — Push + profil + parola sıfırlama

- 3 push türü aktif
- ProfilScreen + bildirim toggle'ları
- Parola sıfırlama akışı (yayıncı destek talebi tarafı)

**Çıktı**: full MVP feature seti.

### Faz 5 — Public rollout

- Play Store + App Store yayın (TestFlight beta önce)
- Mevcut tüm yayıncılara duyuru (WhatsApp template hazır)
- Feature flag `License.ShopperAppEnabled` per-yayıncı kontrol

## Açık sorular

1. **TC alanı için at-rest encryption mekanizması**: pgcrypto vs app-level AES? Decision implementation aşamasında — KVKK uyum + audit log + performance benchmark
2. **Firebase project ayrımı**: shopper app için ayrı Firebase project (`orderdeck-shopper-prod`) mu yoksa aynı project'te bundle ID ile ayrım mı? Güvenlik için ayrı project tercih edilir
3. **PDF retention için R2 lifecycle policy**: Hangfire job vs R2 native object expiration? Hangfire daha esnek (override için)
4. **Email format normalize**: lowercase + Unicode normalization (NFKC) — implementation'da kütüphane seçimi
5. **Yayıncı kodu küfür filtresi listesi**: TR + İng ortak liste nereden gelecek? Profanity-tr npm package var ama .NET tarafı için custom seed lazım
6. **WpfCustomerProjection sync sıklığı**: WPF tarafında hangi tick'te tetikleyecek? Mevcut order sync'i (60sn) ile birlikte mi, ayrı zamanlayıcı mı?
7. **Rate limit altyapısı**: mevcut LicenseServer'da rate limit middleware var mı? Yoksa AspNetCoreRateLimit kütüphanesi mi, custom mı?

## Sonraki adımlar

Bu spec onaylanırsa:
1. `writing-plans` skill → implementation plan üretir (commit-by-commit breakdown, faz bazında)
2. Plan onaylanırsa → execute (faz 0 server PR'larıyla başlanır)
3. Faz 1 başlarken yeni `OrderDeck-Shopper` repo init
