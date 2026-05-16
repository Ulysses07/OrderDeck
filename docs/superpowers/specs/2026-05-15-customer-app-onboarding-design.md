# OrderDeck Müşteri App — Onboarding + Auth (Spec 1)

**Tarih:** 2026-05-15
**Durum:** Brainstorming tamamlandı, review bekliyor
**Sahibi:** Burak (yayıncı/karar verici)

## Vizyon değişikliği

2026-05-13'teki "müşteri self-service yapılmayacak" kararı **iptal edildi** (2026-05-15).

Yeni hat: **iki ayrı mobile app**:

| Ürün | Kullanıcı |
|------|-----------|
| OrderDeck.App (WPF) | Yayıncı (masaüstü) |
| OrderDeck-Mobile Panel | Yayıncı + ekibi |
| **OrderDeck Müşteri App (YENİ)** | **Alışveriş yapan son kullanıcı** |
| Chrome Extension | Yayıncı |

## Spec kapsamı (Spec 1)

Bu spec **sadece müşteri app'inin temel iskeleti + onboarding** akışını kapsar:

- ✅ Yeni Capacitor mobile app projesi
- ✅ Yayıncı kodu ile pair etme (BroadcasterCode)
- ✅ Müşteri profili oluşturma (Ad/Adres/Telefon/Email/TC + per-pair platform/username)
- ✅ Çoklu yayıncı takip etme (limitsiz)
- ✅ JWT auth altyapısı (yayıncı kodu + email OTP ile re-auth)
- ✅ Yayıncı tarafı: BroadcasterCode set/edit UI (mevcut Mobile Panel + WPF Settings)

**Spec 1 dışında bırakılanlar (ileri spec'lerde):**

- ❌ Broadcast feed (Spec 2)
- ❌ Dekont upload (Spec 3) — TC 9,990 TL kuralı burada uygulanacak
- ❌ Push notifications (Spec 4)
- ❌ WhatsApp draft akışı (Spec 5)

## Karar günlüğü

| Konu | Karar | Sebep |
|------|-------|-------|
| Eşleşme yöntemi | **Yayıncı kodu** (BroadcasterCode) | SMS OTP maliyetini sıfırlamak (~1.5k-8.5k ₺/ay tasarruf) |
| Kod sahipliği | Yayıncı kendi belirler | Marka kimliği (örn. `ROYAL`) |
| Çoklu yayıncı | Limit yok | Müşteri birden fazla mağazadan alışveriş yapar |
| Yayıncı keşfi | YOK | Vizyon: bir mağaza müşterisi diğer mağazayı görmemeli |
| Re-auth | Email OTP | Ücretsiz (SMTP zaten kurulu), telefon ya değişebilir ya geçici olabilir |
| TC | Opsiyonel | KVKK riski (hassas veri); dekont akışında koşullu zorunlu (Spec 3'te) |
| Telefon | Zorunlu | Kargo + muhasebe için lazım |

## Mimari

### Yeni mobile app

**Proje yapısı:**
```
OrderDeck-Mobile/
├── apps/
│   ├── panel/        (mevcut — yayıncı + ekibi)
│   └── customer/     (YENİ — müşteri)
├── packages/
│   ├── shared-api/   (genişletilir, müşteri endpoint'leri eklenir)
│   ├── shared-auth/  (genişletilir, customer token format)
│   ├── shared-push/  (mevcut)
│   └── shared-ui/    (genişletilir, müşteri tema)
```

**Tech stack** (panel ile aynı):
- React + Vite + TypeScript
- Capacitor 6 (iOS + Android)
- TanStack Query
- React Router
- Tailwind CSS
- Zustand (auth store)

**App identity:**
- Bundle ID: `com.orderdeck.customer` (panel `com.orderdeck.panel`'den ayrı)
- App name: "OrderDeck" (müşteri için kısa)
- Yeni Firebase app kayıtları (push için, Spec 4)

### Server entity'leri (yeni)

#### 1. `BroadcasterProfile` — yayıncının müşteri-facing kimliği

Mevcut `IntakeFormConfig.Slug` web URL içindir (`/intake-form/{slug}`). Müşteri app için **ayrı bir alan** — vizyonda tek isim hipotezi başarısız oldu (yayıncı slug değiştirmek isteyebilir farklı sebeplerle).

```csharp
public sealed class BroadcasterProfile
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    // Müşteri app'te kod olarak girilir. Unique, case-insensitive.
    public string Code { get; set; } = "";        // örn. "ROYAL"

    // Müşteriye gösterilecek ad (kod değil — kod aslında handle gibi).
    public string DisplayName { get; set; } = ""; // örn. "Royal Mezat"

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

**Kod kuralları:**
- 3–20 karakter, sadece `a-z` + `0-9`
- Case-insensitive uniqueness (DB'de lowercase normalize edilir)
- Reserved kelimeler: `admin`, `support`, `login`, `app`, `panel`, `orderdeck`, `dashboard`, `api`, `customer`, `auth`
- Küfür filtresi (TR + İng listesi)
- **Değiştirilebilir** (her zaman), ama **son değişimden en az 7 gün geçmiş olmalı** (`BroadcasterProfile.UpdatedAt` üzerinden kontrol)
- Eski kod hemen serbest kalır, başkası alabilir
- Audit log: kod değişikliği `AuditEvents.BroadcasterCodeChanged` ile loglanır

#### 2. `CustomerAccount` — müşteri ana profili

Mevcut `Customer` entity yayıncı (lisans sahibi) kimliği taşıyor (kafa karıştırıcı legacy isim). Yeni müşteri için **ayrı bir entity** gerek.

```csharp
public sealed class CustomerAccount
{
    public Guid Id { get; set; }

    // Identity (ortak profil bilgisi — tüm yayıncı bağlantıları için aynı)
    public string FullName { get; set; } = "";
    public string Address { get; set; } = "";
    public string Phone { get; set; } = "";     // E.164 format, +90...
    public string Email { get; set; } = "";     // Re-auth için kritik
    public string? Tc { get; set; }             // Opsiyonel; Spec 3'te koşullu zorunlu

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    // Email değişikliği nadir; ama support flow için audit log gerek
}
```

**Doğrulama kuralları:**
- Email: standart RFC formatı + DB'de lowercase normalize edilir + global unique (case-insensitive index)
- Telefon: TR formatlı normalize edilir (+90 prefix garanti), DB'de unique değil (birden fazla müşteri aynı eve telefon paylaşabilir teorik olarak)
- TC: nullable, 11 hane olduğunda checksum doğrulanır (TR TCKN algoritması)
- **At-rest encryption**: TC alanı KVKK gereği şifreli saklanır (PostgreSQL pgcrypto veya app-level AES — kararı implementation aşamasında)

#### 3. `CustomerBroadcasterLink` — müşteri-yayıncı bağlantısı

```csharp
public sealed class CustomerBroadcasterLink
{
    public Guid Id { get; set; }
    public Guid CustomerAccountId { get; set; }
    public CustomerAccount CustomerAccount { get; set; } = null!;

    public Guid BroadcasterLicenseId { get; set; }
    public License BroadcasterLicense { get; set; } = null!;

    // Per-pair: müşteri bu yayıncıyı hangi platformdan izliyor + chat'te ne adla yazıyor
    public string Platform { get; set; } = "";  // youtube/instagram/tiktok/facebook
    public string Username { get; set; } = "";  // platformdaki handle

    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }  // soft delete (müşteri yayıncıyı sildiyse)
}
```

**Index:**
- `(CustomerAccountId, BroadcasterLicenseId)` — unique (aynı pair iki kez olmaz)
- `(BroadcasterLicenseId, JoinedAt DESC)` — yayıncının "yeni gelen müşteriler" listesi için

### Yeni HTTP endpoint'leri

#### Müşteri-facing (yeni: `/api/customer/...` route prefix)

```
POST /api/customer/auth/join
  body: { broadcasterCode, fullName, address, phone, email, tc?, platform, username }
  → 201 + { accessToken, refreshToken, customerId, broadcasters[] }
  Akış: kodu validate et → müşteri account oluştur (email unique) → link kur → JWT issue

POST /api/customer/auth/add-broadcaster
  Bearer-CustomerAccount auth
  body: { broadcasterCode, platform, username }
  → 200 + { broadcasters[] }
  Akış: zaten kayıtlı müşteri yeni yayıncı ekliyor — sadece platform/username sorulur

POST /api/customer/auth/request-relink
  body: { email }
  → 202 (her zaman, email enumeration koruması)
  Akış: SMTP üzerinden 6 haneli kod gönderilir (15 dk geçerli)

POST /api/customer/auth/relink
  body: { email, code }
  → 200 + { accessToken, refreshToken, customerId, broadcasters[] }
  Akış: kod doğrula → eski customer account bul → JWT issue

POST /api/customer/auth/refresh
  body: { refreshToken }
  → 200 + { accessToken, refreshToken (rotated) }
```

#### Yayıncı-facing (mevcut `/api/panel/...` + `/api/v1/licenses/...`)

```
GET /api/panel/broadcaster-profile
  Bearer-Customer auth (legacy isim — yayıncı kimliği)
  → 200 + { code, displayName, updatedAt }
  Mevcut yayıncının kendi kodunu okur

PUT /api/v1/licenses/{licenseId}/broadcaster-profile
  Bearer-Customer auth
  body: { code, displayName }
  → 200 + { code, displayName, updatedAt }
  Yayıncı kendi kodunu set/update eder
  Validation: kod kuralları + rate limit (haftada 1 değişim, son değişimden 7 gün geçmiş olmalı)
  409 if kod başka yayıncıda var
```

### Auth modeli

**İki ayrı JWT scheme**:

| Scheme | Bearer-Customer (legacy) | **Bearer-CustomerAccount (YENİ)** |
|--------|-------------------------|----------------------------------|
| Audience | `orderdeck-customer` | `orderdeck-customeraccount` |
| Subject | License sahibi (yayıncı) `Customer.Id` | `CustomerAccount.Id` |
| Claims | `sub`, `tcid`, `principal=customer`, `email` | `sub` (account id), `email`, `principal=customer-account`, `broadcasters` (links: licenseId listesi) |
| Lifetime | 15 min access / 30 day refresh | 30 min access / 90 day refresh |
| "Beni hatırla" | N/A | refresh 90 gün → 1 yıl genişler |

**Bearer-Customer scheme adı yanıltıcı** (aslında yayıncı kimliği) — sonraki bir refactor PR'da `Bearer-Broadcaster` adına dönüştürülür. Bu spec kapsamında değil.

**Refresh token strategy:**
- Rotation on every use (mevcut RefreshToken altyapısı genişletilir)
- Single-use token, kullanılınca invalidated

### Akışlar

#### A. İlk kez bir yayıncıya kayıt (Onboarding)

```
1. Müşteri app indirir, açar
2. Welcome screen: "Yayıncı kodunu gir"
3. Kod gir: ROYAL
4. POST /api/customer/auth/lookup-code?code=royal
   → 200 + { broadcasterId, displayName: "Royal Mezat" }
   → 404 if bulunamadı (yanlış kod hatası)
5. "Royal Mezat mağazasına hoş geldin" ekranı, form:
   - Ad Soyad, Adres, Telefon, Email, TC (opsiyonel)
   - Platform dropdown (YouTube/Instagram/TikTok/Facebook)
   - Kullanıcı adı (o platformdaki handle)
6. Kaydı tamamla → POST /api/customer/auth/join
7. 201 → JWT al, ana ekrana geç (Spec 2'de feed)
```

**Hata durumları:**
- Email zaten kullanılıyor → "Bu email ile daha önce kayıt olunmuş, 'Cihazımı kurtar' kullan"
- Yayıncı kodu yanlış → "Bu kodla bir mağaza bulamadık"
- Network hatası → retry önerisi

#### B. İkinci yayıncıyı eklemek

```
1. Müşteri ana ekranda "+ Yayıncı ekle"
2. Kod gir: ANTIKA
3. POST /api/customer/auth/lookup-code?code=antika
   → 200 + { broadcasterId, displayName: "Antika Dükkanı" }
4. Form (kısaltılmış — profil zaten dolu):
   - Platform dropdown
   - Kullanıcı adı
5. Yayıncıyı ekle → POST /api/customer/auth/add-broadcaster (Bearer-CustomerAccount)
6. 200 → broadcasters listesi güncellenir
```

#### C. Cihaz değiştirme / app yeniden yükleme (Re-auth)

```
1. Müşteri yeni cihazda app açar
2. Welcome screen → "Mevcut hesabımı kullan" tıklar
3. Email gir
4. POST /api/customer/auth/request-relink
   → 202 (always — email enumeration koruması)
5. SMTP üzerinden 6 haneli kod gönderilir
6. Müşteri kod gir
7. POST /api/customer/auth/relink
   → 200 + { accessToken, refreshToken, broadcasters[] }
   → 401 if kod yanlış / 15 dk geçti
8. Profil + tüm yayıncı bağlantıları geri yüklenir
```

#### D. Yayıncı tarafı kod set/edit

**Mobile Panel UI:**
- DahaFazla → "Mağaza Kodu" (yeni)
- İlk kez: "Henüz kodun yok. Müşterilerinin app'te girip seni bulabilmesi için bir kod belirle." → input + Save
- Sonrası: mevcut kod gösterilir + "Düzenle" butonu
- Düzenle ekranı: input + validation feedback (kullanılabilir / başka yayıncıda var / kural ihlali / haftada 1 limit aşıldı)

**WPF tarafı:** Settings → Ayarlar → "Mağaza Kodu" sekmesi (mobile ile aynı işlev)

### KVKK koruması

| Veri | Hassasiyet | Saklama | Erişim |
|------|------------|---------|--------|
| Ad Soyad | Normal | Plain text | Yayıncı + müşteri kendisi |
| Adres | Normal | Plain text | Yayıncı + müşteri kendisi |
| Telefon | Normal | Plain text | Yayıncı + müşteri kendisi |
| Email | Normal | Plain text | Müşteri kendisi (yayıncı görür ama nadir kullanılır) |
| **TC** | **Hassas** | **Şifreli at-rest** | Müşteri kendisi + yayıncı (görme yetkisi audit'lenir) |
| Platform + Username | Normal | Plain text | Yayıncı + müşteri |

**TC erişim audit log'u**: yayıncı bir müşterinin TC'sini görüntülediğinde `AuditEvents.CustomerTcViewed` event'i yazılır (Spec 3'te). Spec 1'de TC sadece form tarafında alınır, henüz yayıncı UI'sında gösterilmez.

**Data retention**: Müşteri profilini sildiğinde (Spec 1'in dışında, ileride) 30 gün soft-delete sonra hard-delete. Spec 1'de profil silme yok.

### Test stratejisi

| Test türü | Kapsam |
|-----------|--------|
| Unit tests | Kod validation kuralları (regex, reserved, küfür); TCKN checksum; email format |
| Integration tests | Endpoint'ler (join, add-broadcaster, request-relink, relink, refresh); duplicate email; yanlış kod; rate limit |
| Cross-tenant isolation | Müşteri A'nın yayıncı X bağlantısı, yayıncı Y'nin GET'inde görünmüyor |
| E2E (manuel) | Capacitor build + emulator: kayıt + 2. yayıncı ekle + uninstall + relink |

### Migration stratejisi

**Yeni entity'ler — mevcut data'yı bozmaz:**
- `BroadcasterProfile` — license başına 0 veya 1 satır (lazy init — yayıncı kodunu belirleyene kadar yok)
- `CustomerAccount` — sıfır satır (müşteri kayıt olunca dolar)
- `CustomerBroadcasterLink` — sıfır satır

**Mevcut `Customer` entity'sini değiştirmiyoruz** (legacy yayıncı kimliği).

EF migration: `AddCustomerOnboardingEntities` adıyla tek migration.

## Açık sorular (implementation aşamasında çözülür)

1. **At-rest encryption mekanizması TC için:** pgcrypto vs app-level AES — perf benchmark + KVKK uyum şartları (audit edilebilir olmalı)
2. **Email OTP rate limiting:** mevcut OrderDeck'te benzer endpoint var mı? Aksi durumda yeni rate limit policy
3. **Customer app push notification (Spec 4):** Firebase project ayrı mı (`orderdeck-customer-prod`) yoksa aynı mı? Bundle ID farklı olduğu için ayrı olması daha güvenli
4. **Müşteri tarafı UI dili:** TR / EN switch mi yoksa sadece TR mi MVP'de? (Cevap: sadece TR — hedef kitle TR)

## Onay öncesi soruları

Bu spec yazıldı. Onay için:
- ✅ Mimari (3 yeni entity, 7 yeni endpoint, ayrı JWT scheme) anlaşılır mı?
- ✅ Onboarding akışı (A/B/C/D) UX olarak doğru hissediyor mu?
- ✅ Spec 1 scope'u (sadece iskelet + onboarding) makul mu, yoksa erkenden daraltılmış mı?

## Sonraki adımlar

Bu spec onaylanırsa:
1. **writing-plans skill** → implementation plan üretir (commit-by-commit breakdown)
2. **execute** → kod yazılır
3. **Spec 2** brainstorm: Broadcast feed
