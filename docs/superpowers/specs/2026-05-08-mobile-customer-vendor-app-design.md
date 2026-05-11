# OrderDeck Mobil Uygulama — Tasarım

**Tarih**: 2026-05-08
**Aşama**: MVP
**Hedef kitle**: Mezat müşterileri + yayıncı çalışanları (kargo/depo)

## Özet

OrderDeck mobil uygulaması, mezat yayıncılarının müşterilerine **dekont yükleme + bildirim + duyuru** kanalı, ve yayıncı çalışanlarına **sipariş takibi + hazırlık checklist** sağlar. Capacitor wrapper olarak iOS ve Android'de yayınlanır, web codebase'i (Next.js) ile beslenir.

Tek app, giriş ekranında **Alışveriş / Yayıncı** rol seçimi. Multi-tenant: tek müşteri birden fazla yayıncıya bağlanabilir, telefon numarası kimlik olarak kullanılır.

## İş bağlamı

Şu anki müşteri akışı:
1. Yayında alıcı chat'ten kod+fiyat yazar → OrderDeck etiket basar
2. Yayın sonu yayıncı WhatsApp'tan IBAN gönderir (telefon biliniyor)
3. Müşteri havale yapar, dekontu WhatsApp'tan PDF olarak gönderir
4. Yayıncı dekontları manuel kontrol eder, ödendi diye işaretler
5. Yayın bildirimi WhatsApp grup mesajıyla yapılır
6. Kargocu personel siparişleri ayrı tutar, koordinasyon WhatsApp'tan

Mobil app değişiklikleri:
- Müşteri dekontu app içinden yükler (PDF)
- Yayıncı OrderDeck WPF panelinden onaylar
- Yayın bildirimi push notification
- Kargocu personel mobile'dan sipariş listesi + hazırlık durumu güncelleme
- Yayıncı duyuru kanalı (read-only)
- Otomatik aylık dekont raporu email + R2 yedek

WhatsApp tabanlı eski akış paralel kalır — app'i indirmeyen müşteri eski yöntemle devam.

## Kullanıcı kimliği ve rol seçimi

**Telefon numarası = kimlik.** Müşteri app'i indirir, telefon girer, SMS OTP doğrular. Backend bu telefonu hangi yayıncıların müşteri listesinde olduğunu kontrol eder, bulduklarına otomatik bağlar.

**Rol seçimi**: İlk açılışta seçim ekranı:
- **Alışveriş** → müşteri akışı
- **Yayıncı** → personel akışı (kargocu, depo, vb.)

Rol seçimi telefonda kalıcı, settings'ten değiştirilebilir.

### Müşteri tarafı kimliklendirme akışı

- 0 yayıncıda → boş ekran ("Henüz hiçbir mezatçıya kayıtlı değilsin")
- 1 yayıncıda → o yayıncının siparişleri direkt, yayıncı seçim UI'ı yok
- N yayıncıda → siparişler tek listede karışık + yayıncı badge

Davet linki, kayıt token'ı, manuel yayıncı seçim, keşfet ekranı yok. Telefon ile otomatik eşleşme tek mekanizma.

Yayıncıdan ayrılma butonu yok. Yayıncı kendi tarafında müşteriyi bloklarsa app'te de görünmez.

### Yayıncı tarafı kimliklendirme

Yayıncı çalışanı, yayıncı tarafından OrderDeck WPF'inde "Personel ekle" ile telefonu kaydedilir. Çalışan app'i indirir, telefon doğrular, otomatik o yayıncının personeli olarak bağlanır.

Bir telefon hem müşteri hem yayıncı çalışanı olamaz aynı yayıncıda — rol seçimi mutually exclusive.

## Mimari

### Stack

- **Web tarafı**: Next.js (mevcut). `web/app/(tr)/m/` ve `web/app/(en)/en/m/` altında mobile-first sayfalar.
- **Mobile shell**: Capacitor (iOS + Android paralel build)
- **Backend**: License server (mevcut). Yeni endpoint'ler eklenecek.
- **Push**: FCM (Android) + APNS (iOS), Capacitor Push Notifications plugin tek interface
- **Auth**: Telefon + SMS OTP. Sağlayıcı: Netgsm.
- **Storage**: VPS local file system (dekont PDF, duyuru görseli). R2 sadece aylık ZIP yedek için.

### Veri akışı

```
Yayıncı (WPF App)
  ├─ Müşteri/personel telefon kaydı
  ├─ Etiket data + payment status
  ├─ Onay paneli (bekleyen dekontlar)
  ├─ Yayın "açık/kapalı" toggle + push broadcast
  ├─ Duyuru yayınlama
  ├─ Profil düzenleyici (bio, sosyal, takvim)
  └─ Aylık ZIP rapor (otomatik cron)
        │
        ▼ HTTPS sync
┌─────────────────────────────────┐
│  License Server (multi-tenant)  │
│  - Customer (TenantId)          │
│  - Order/Label snapshot         │
│  - PaymentRequests + Receipts   │
│  - PushSubscriptions            │
│  - PhoneVerifications           │
│  - Announcements                │
│  - VendorProfile                │
│  - StaffMembers                 │
└─────────────────────────────────┘
        ▲
        │ HTTPS API
        │
Mobile App (Capacitor + Next.js)
  ├─ Rol seçimi (Alışveriş / Yayıncı)
  ├─ Telefon login + SMS OTP
  ├─ Müşteri akışı: 9 ekran
  ├─ Yayıncı akışı: 4-5 ekran
  └─ Push (FCM/APNS)
```

### Multi-tenant izolasyon

License server tarafında her yayıncı bir **tenant**. Customer/Order/Receipt/Announcement tabloları `TenantId` kolonu ile filtrelenir. Mobile app sorgularında müşteri telefonu ile bağlı olduğu tüm tenant'lardan veri çekilir, ama A tenant'ı B tenant'ının verisini hiç görmez.

EF Core global query filter ile `TenantId == currentUser.TenantId` zorunlu — unutmanın getirdiği güvenlik açığı engellenir.

### Push routing

- Topic per yayıncı: `vendor-{vendorId}` — yayın bildirimi, duyuru
- Bireysel device token bazlı — ödeme onayı, kişiye özel bildirim
- Müşteri yayıncıya bağlanınca otomatik FCM topic'ine subscribe olur

### Storage planı

| Kategori | TTL | Yer |
|---|---|---|
| Dekont PDF | 2 ay | VPS |
| Duyuru görseli | 6 ay | VPS (sıkıştırılmış 1080px Q80) |
| Aylık ZIP rapor | 1 yıl | R2 (yıllık ~$2-3) |
| Etiket/Customer | Sınırsız | VPS |

100 yayıncı için tahmini VPS kullanımı: **~20 GB sabit**.

## MVP scope

### Müşteri tarafı (9 ekran)

1. **Rol seçim** (ortak) — "Alışveriş / Yayıncı"
2. **Telefon doğrulama** (ortak) — telefon gir, SMS OTP, doğrula
3. **Ana ekran** — sipariş listesi (ödeme bekleyen üstte, geçmiş altta), her satırda yayıncı badge + tutar + durum + hazırlık durumu
4. **Sipariş detay** — IBAN/açıklama/tutar (kopyala butonları), dekont yükle (PDF native picker), WhatsApp/telefon shortcut, adres + harita aç, hazırlık durumu read-only
5. **Profil** — telefon, ad, adres, TC kimlik (opsiyonel + uyarı: "9.900 TL üzeri alışverişler için yasal zorunluluk vardır"), push ayarları, tema toggle, müşteri istatistiği ("X yıldır müşterimizsin, Y sipariş, Z TL"), sadık müşteri rozeti
6. **Bildirim merkezi** — son 30 gün push log
7. **Yayıncı profil sayfası** — bio, sosyal medya linkleri, yayın takvimi, "WhatsApp ile iletişim" butonu
8. **Duyurular** — yayıncıların duyuruları kronolojik (en yeni üstte), yayıncı badge'i, görsel + text
9. **Geçmiş siparişler** — tüm siparişler (ödendi, kargolandı, teslim) listesi

### Yayıncı tarafı (4 ekran, mobile)

1. **Sipariş listesi** — tüm aktif siparişler, müşteri bilgisi + adres + telefon + hazırlık durumu
2. **Sipariş detay** — müşteri detayı, "WhatsApp ile mesaj at", "Hazırlandı/Paketlendi/Kargoda" toggle
3. **Profil** — kendi telefonu, çıkış
4. **Bildirim merkezi** — kendi role gelen pushlar

### Yayıncı tarafı (WPF, mevcut yapıya eklemeler)

- **Customer.Phone alanı** + UI
- **Onay paneli** ekranı (bekleyen dekontlar listesi, görsel kontrol, onayla/reddet)
- **Yayın aktif toggle** (push broadcast tetikleyici)
- **Toplu push gönder** butonu — yayın başlatmada
- **Profil düzenleyici** dialog (bio, sosyal medya, yayın takvimi)
- **Duyuru yayınlama** dialog (başlık, metin, opsiyonel görsel + sıkıştırma + push toggle)
- **Personel ekle/çıkar** UI (telefon kaydı)
- **Ürün hazırlık checklist** — sipariş listesinde her etiket için durum dropdown
- **Aylık ZIP rapor** otomatik (cron, email + R2)

### Bedava eklemeler (MVP'ye dahil)

- Adres haritada aç (`geo:` URL)
- WhatsApp tek tık (`https://wa.me/+90...`)
- Telefon arama (`tel:+90...`)
- Native dosya picker (Capacitor Filesystem plugin)
- Pull-to-refresh
- Boş durumlar için iyi metinler
- Biyometrik login (Face ID / parmak izi)
- Tema toggle (light/dark)

### Fatura entegrasyonu

Müşteri kayıtlarında TC kimlik ve adres saklanır. WPF tarafında "Bu Yayının Fatura Excel'i" butonu — yayın sonu o yayının tüm etiketleri tek Excel'de export edilir, yayıncı kendi fatura sistemine (Paraşüt/Bizim Hesap/GİB Portal) yükler ve toplu fatura keser.

API entegrasyonu yok, manuel Excel akışı. Yayın başına 1 kez Excel, vergi mevzuatına uyumlu (7 günlük fatura kesim limiti).

TC opsiyonel:
- Sipariş 9.900 TL altıysa fatura "11111111111" teknik kodu ile kesilir, geçerli
- Sipariş 9.900 TL üstüyse müşteri TC vermek zorunda — kayıt formunda uyarı, ödeme adımında hatırlatma
- WPF'te 9.900 TL üstü sipariş için "TC alındı mı" check'i, alınmamışsa yayıncı manuel WhatsApp'tan ister

TC saklama:
- License server tarafında encrypted at rest
- Mobile app'e gönderilmez (sadece müşteri kendi profilinde okuyabilir)
- Audit log: TC okuma kayıtlı

### Sonraya bırakılanlar (MVP dışı)

- Çoklu yayıncı keşfet sayfası
- In-app mesajlaşma
- Tek tıkla kart ödemesi (iyzico/PayTR)
- Yayın videosu feed'i
- Referans sistemi
- Otomatik indirim akışı (kademe sadece görsel rozet, indirim manuel)
- Kargo barkod tarama (Yurtiçi/Aras API entegrasyonu)
- Tekrar sipariş etme akışı

## Backend değişiklikleri

### Yeni tablolar (license server)

```
Customers
  + Phone (string, nullable, indexed)
  + AppRegisteredAt (datetime, nullable)
  + TenantId (Guid, foreign key)

PaymentRequests
  Id, CustomerId, VendorId, LabelIds (json), Amount,
  Status (Pending/ReceiptUploaded/Approved/Rejected/Expired),
  CreatedAt, ExpiresAt

PaymentReceipts
  Id, PaymentRequestId, FileName, FilePath, ContentType,
  ParsedAmount, ParsedBank, UploadedAt

PushSubscriptions
  Id, UserId (Customer or Staff), DeviceToken, Platform (ios/android),
  CreatedAt, LastSeenAt

PhoneVerifications
  Phone, OtpHash, CreatedAt, ExpiresAt, VerifiedAt

Announcements
  Id, VendorId, Title, Body, ImageUrl (nullable),
  PostedAt, ExpiresAt (6 ay sonra), PushedAt (nullable)

VendorProfiles
  VendorId, Bio, InstagramHandle, YoutubeHandle, TiktokHandle,
  FacebookHandle, WhatsappPhone, LiveSchedule (text)

StaffMembers
  Id, VendorId, Phone, DisplayName, Role (cargo/admin),
  AddedAt, RemovedAt (nullable)

LabelPreparation
  LabelId (FK), Status (NotStarted/InProgress/Packaged/Shipped),
  UpdatedAt, UpdatedBy (StaffId or VendorId)
```

### Yeni endpoint'ler

**Müşteri (auth: customer JWT)**
```
POST /api/v1/auth/phone-otp
POST /api/v1/auth/phone-verify
GET  /api/v1/customer/me
GET  /api/v1/customer/orders
GET  /api/v1/customer/orders/{id}
POST /api/v1/customer/orders/{id}/receipt
GET  /api/v1/customer/announcements
GET  /api/v1/customer/vendors/{id}/profile
POST /api/v1/customer/push/register
PUT  /api/v1/customer/preferences
```

**Yayıncı çalışanı (auth: staff JWT)**
```
GET  /api/v1/staff/orders
GET  /api/v1/staff/orders/{id}
PUT  /api/v1/staff/orders/{id}/preparation
POST /api/v1/staff/push/register
```

**Yayıncı (auth: vendor JWT, mevcut)**
```
POST /api/v1/vendor/customers
PUT  /api/v1/vendor/customers/{id}/phone
POST /api/v1/vendor/orders/{id}/sync
POST /api/v1/vendor/receipts/{id}/approve
POST /api/v1/vendor/receipts/{id}/reject
POST /api/v1/vendor/announcements
GET  /api/v1/vendor/announcements
DELETE /api/v1/vendor/announcements/{id}
PUT  /api/v1/vendor/profile
POST /api/v1/vendor/staff
DELETE /api/v1/vendor/staff/{id}
POST /api/v1/vendor/push/broadcast
GET  /api/v1/vendor/reports/monthly/{year}/{month}
```

### WPF tarafı sync

Mevcut WPF lokal SQLite kalır. Yeni eklenecek:
- Background sync hosted service (lokal değişiklikleri central'a push)
- Sync queue tablosu (offline durum için retry)
- Conflict resolution: lokal son yazılan kazanır

## SMS gateway

**Sağlayıcı**: Netgsm (TR'de yaygın, API kolay).

- API key + sender name kayıt
- ~$0.05/SMS, hacim arttıkça düşer
- 100 yayıncı × ortalama 5 müşteri/gün × OTP = 500 SMS/gün = $25/gün worst case
- Gerçekte çoğu kullanıcı 30 günlük token kullanır, OTP ayda 1 = ~50/gün = $2.50/gün

OTP cooldown: aynı telefondan 60 saniyede 1 SMS, abuse engellemek için.

## Aşamalar

### Phase A — Mobile-first PWA pilot (4-5 hafta yarı-zamanlı)

- Next.js'e `/m/` mobile-first sayfalar (rol seçim, telefon login, müşteri 9 ekran, yayıncı 4 ekran)
- License server backend (yeni tablolar, endpoint'ler, SMS gateway)
- WPF tarafı yeni paneller
- 5-10 gerçek müşteriyle pilot
- Capacitor'a komitman yok — fail olursa Phase B'ye geçilmez

### Phase B — Capacitor wrapper Android (3-4 hafta)

- Capacitor proje
- Aynı `/m/` içeriği wrapper içinde
- FCM push integration
- Native dosya picker, biometric, share
- Play Store Internal Testing → Closed Testing

### Phase C — iOS (3-4 hafta)

- Xcode setup, provisioning profile, APNS key
- Capacitor iOS build
- TestFlight beta
- App Store submission (1-2 rejection cycle tampon)

### Phase D — Production launch (1-2 hafta)

- Play Store + App Store production
- WhatsApp template'lere "App'i indir" linki ekle
- Marketing message yayıncılara

**Toplam: ~12-15 hafta yarı-zamanlı solo dev = ~3-4 ay yarı-zamanlı veya 6-12 ay full-zamanlı sürpriz dahil.**

## Maliyet

### Tek seferlik
- Apple Developer hesabı: $99
- Google Play Developer: $25
- App icon + branding: mevcut logo, ek tasarım yoksa $0
- **Toplam: $124**

### Yıllık
- Apple Developer: $99
- Netgsm SMS: ~$30-100/yıl (kullanıma göre)
- Cloudflare R2 (aylık ZIP'ler): ~$5-10/yıl
- **Toplam: ~$140-210/yıl**

### Sıfır maliyet
- Mac (zaten var)
- Domain (zaten var)
- VPS (zaten var, +20 GB storage kullanımı)

## Açık riskler

1. **App Store rejection** — Capacitor wrapper "minimum native value" reddi (%25-35 ilk submission). Çözüm: birkaç anlamlı native API (push, biometric, camera, file picker).

2. **WebView performance** — TR pazarında orta-segment Android'lerde lag. Liste virtualization gerekirse react-window.

3. **iOS push subtleties** — APNS Android FCM'e göre setup farklı. Critical Alerts kullanmıyoruz, time-sensitive ile yetiniyoruz.

4. **Telefon eşleşme reconciliation** — Yayıncının Customer kaydında telefon yoksa müşteri eşleşemez. Yayıncı UX'i kritik (yayın sonu WhatsApp'tan telefonu öğreniyor; OrderDeck'e kaydetme adımı eklenmeli).

5. **SMS gateway maliyeti ve deliverability** — TR operatörler arasında deliverability farkı, Netgsm bilinen iyi sağlayıcı.

6. **Multi-tenant migration** — License server'ın mevcut Customer tablosu single-tenant. TenantId ekleme migration backwards-compatibility ile yapılmalı.

7. **WPF local DB → central sync** — Eventual consistency, offline çalışma desteklenir.

8. **Sessize alınmış telefon push** — OS limit, kıramayız. Kullanıcının bilinçli kararı.

## Karar verilmesi gerekenler (ileride)

- App icon final versiyonu (mevcut logo MVP için yeterli, sonradan profesyonel tasarım)
- App description metni (App Store + Play Store)
- Privacy policy güncellemesi (mobil app data collection için ek bölüm)
- TestFlight beta kullanıcı seçimi (5-10 müşteri)
- Apple Developer şirket vs bireysel kayıt

## Sıradaki adım

Bu spec onaylanırsa, **Phase A (PWA pilot)** için detaylı implementation plan yazılacak.
