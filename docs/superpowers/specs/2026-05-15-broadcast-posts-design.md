# OrderDeck Broadcast Posts — Yayıncı Tarafı (Spec 2)

**Tarih:** 2026-05-15
**Durum:** Brainstorming tamamlandı, review bekliyor
**Sahibi:** Burak

## Bağlam

Müşteri app (Spec 1) onaylandı ama implementation **ertelendi**. Yayıncı önce kendi tarafından post oluşturma + yönetim özelliğine sahip olacak; müşteri tarafı sonra Spec 1'le birlikte uçtan uca aktive olacak.

Bu ara dönemde post'lar server'da depolanır, yayıncı kendi panelinde görür ve "müşteriler nasıl görür" preview eder. Müşteri app çıkana kadar **public erişim yoktur**.

## Scope

### Dahil

- ✅ Server entity: `BroadcastPost` + Cloudflare R2 media storage
- ✅ Yayıncı endpoint'leri: post CRUD + sabit (pin) + R2 pre-signed upload
- ✅ Mobile Panel UI: post oluşturma + listeleme + edit + sil + sabitle + müşteri preview
- ✅ Auto-delete hosted service: 30 gün geçenleri sil (pinned hariç)
- ✅ WPF tarafı: değişiklik yok (Compose only mobile panel)

### Dışarıda (sonraki spec'lerde)

- ❌ Müşteri app feed view (Spec 1 implementation + bu)
- ❌ Push notification on yeni post (Spec 4)
- ❌ Public web URL preview (`orderdeck.app/u/{code}`) — vizyon kararıyla yok
- ❌ Yorum / beğeni / reaction (vizyon: tek yönlü broadcast)
- ❌ Server-side video transcoding (MVP: client direkt yükler, format kuralları zorlanır)
- ❌ Multi-foto carousel post (sadece tek media per post)

## Karar günlüğü

| Konu | Karar | Sebep |
|------|-------|-------|
| Storage | **Cloudflare R2** | $0 egress, $0.015/GB, S3-compatible API |
| Yıllık maliyet (50 yayıncı) | ~$10/yıl | 30 gün auto-delete → sürekli ~47 GB rolling |
| Upload limitleri | Photo 10 MB, Video 60 MB / 1080p / 45 sn | iPhone HEIF photo + HEVC video standart çıktısı |
| Post ömrü | **30 gün auto-delete + Pin opsiyonu** | WhatsApp Status mantığı + önemli duyurular için pin |
| Compose UI | **Sadece Mobile Panel** | Telefonda foto/video çekme/seçme daha doğal |
| Public erişim | YOK | Müşteri vizyonu app üzerinden, public URL "yayıncı app indir" gibi olmaz |
| Server transcoding | Yok (MVP) | Yayıncı format kuralına uyar; ileride ihtiyaç olursa eklenir |
| Multi-media | Tek media per post | UI/UX basit; carousel ileride |

## Mimari

### 1. Server entity: `BroadcastPost`

```csharp
public sealed class BroadcastPost
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }       // yayıncı tenant
    public License License { get; set; } = null!;

    public BroadcastPostType Type { get; set; }   // Text | Photo | Video
    public string? TextBody { get; set; }         // max 2000 char (text + caption ortak)

    // Media (Type != Text ise dolu)
    public string? MediaObjectKey { get; set; }   // R2 object key: "{licenseId}/{postId}/{filename}"
    public string? MediaContentType { get; set; } // "image/jpeg", "video/mp4"
    public long? MediaSizeBytes { get; set; }
    public int? MediaDurationSec { get; set; }    // video için
    public int? MediaWidth { get; set; }
    public int? MediaHeight { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; } // = CreatedAt + 30 days; Pinned post far future (9999)
    public bool IsPinned { get; set; }
    public DateTimeOffset? DeletedAt { get; set; } // soft delete
}

public enum BroadcastPostType
{
    Text = 0,
    Photo = 1,
    Video = 2
}
```

**Indexler:**
- `(LicenseId, CreatedAt DESC)` — yayıncının feed listesi
- `(ExpiresAt, IsPinned)` — auto-delete job hızlı tarar

**Pin/unpin davranışı:**
- `IsPinned = true` → `ExpiresAt = 9999-12-31` (etkili olarak süresiz)
- `IsPinned = false` → `ExpiresAt = CreatedAt + 30 gün` (yeni hesap edilir)
- Pin/unpin **CreatedAt'i değiştirmez** — sıralama tutarlı kalır

### 2. R2 storage

**Bucket:** `orderdeck-broadcast-posts`
**Region:** EU (auto, Cloudflare global)
**Object key formatı:** `{licenseId}/{postId}/{filename}`

Örnek:
```
9e5585b4-1326-4b40-b2c6-8e1d318b5009/  ← License
  a1b2c3d4-.../  ← Post
    image.jpg
```

**Lifecycle policy:**
- Auto-delete server tarafından yapılır (Hangfire job) — R2 lifecycle rule'u **kullanmıyoruz** çünkü pin/unpin'i bilemez.

**Pre-signed URL flow:**
```
Mobile Panel                Server                R2
    │                          │                  │
    │  POST /upload-url        │                  │
    │  { type, size, mime }    │                  │
    ├─────────────────────────►│                  │
    │                          │ generate         │
    │                          │ pre-signed PUT   │
    │                          │ url (10 min TTL) │
    │   { url, objectKey }     │                  │
    │◄─────────────────────────┤                  │
    │                          │                  │
    │  PUT file binary         │                  │
    ├──────────────────────────┼─────────────────►│
    │                          │                  │
    │  POST /posts             │                  │
    │  { type, textBody,       │                  │
    │    mediaObjectKey,       │                  │
    │    width, height, ... }  │                  │
    ├─────────────────────────►│                  │
    │                          │ create row       │
    │                          │ (R2 HEAD check)  │
    │   201 { post }           │                  │
    │◄─────────────────────────┤                  │
```

Server R2 HEAD check yapar (`mediaObjectKey` gerçekten var mı, beklenen boyut/mime). Yoksa post oluşturulmaz, 400 döner. Bu sayede orphan record kalmaz.

**R2 read flow (yayıncı kendi preview için):**
- Server `GET /api/panel/posts/{id}/media-url` → 5 dakika geçerli pre-signed GET URL döner
- Mobile Panel direkt R2'den medyayı yükler

### 3. Endpoint'ler

**Auth:** Bearer-Customer (mevcut yayıncı scheme'i — Spec 1 implement edilince Bearer-Broadcaster'a rename)

```
POST /api/panel/posts/upload-url
  body: { type: "photo"|"video", sizeBytes, contentType }
  → 200 + { uploadUrl, objectKey, expiresAt }
  Validation:
    - type=photo → contentType in [image/jpeg, image/heic, image/png, image/webp], size ≤ 10 MB
    - type=video → contentType in [video/mp4, video/quicktime, video/x-m4v], size ≤ 60 MB
  Rate limit: yayıncı başına 100 upload-url/saat (anti-abuse)

POST /api/panel/posts
  body: {
    type: "text"|"photo"|"video",
    textBody?: string (max 2000),
    media?: { objectKey, contentType, sizeBytes, durationSec?, width, height }
  }
  → 201 + { post }
  Validation:
    - type=text → textBody zorunlu
    - type=photo|video → media + objectKey zorunlu, R2 HEAD check geçmeli
    - video duration > 45 sn → 400

GET /api/panel/posts?cursor=...&limit=20
  → 200 + { posts: [...], nextCursor }
  Filter: License'a ait + DeletedAt null
  Sort: IsPinned DESC, CreatedAt DESC (sabitlenmiş üstte)

GET /api/panel/posts/{id}
  → 200 + { post } veya 404

GET /api/panel/posts/{id}/media-url
  → 200 + { url, expiresAt }  (5 dakika TTL)
  Tenant izolasyonu: yayıncı sadece kendi post'unun media URL'ini alır

PUT /api/panel/posts/{id}
  body: { textBody?: string }  // sadece caption edit edilir
  → 200 + { post }

POST /api/panel/posts/{id}/pin
  → 200 + { post (IsPinned=true, ExpiresAt güncellendi) }
  Limit: yayıncı başına max 5 sabitlenmiş post (UI'da overflow göstermek pratik değil)

DELETE /api/panel/posts/{id}/pin
  → 200 + { post (IsPinned=false, ExpiresAt = CreatedAt + 30 gün, eğer geçmişse hemen silinir) }

DELETE /api/panel/posts/{id}
  → 204
  Akış: DeletedAt set + R2 object delete (best effort). Soft delete sayesinde audit log için kayıt kalır.
```

### 4. Auto-delete hosted service

`BroadcastPostCleanupHostedService` — Hangfire job, **her gün 03:00 UTC** çalışır:

```sql
SELECT Id, LicenseId, MediaObjectKey
FROM BroadcastPosts
WHERE ExpiresAt < UtcNow
  AND IsPinned = false
  AND DeletedAt IS NULL
LIMIT 1000
```

Her satır için:
1. R2 object delete (varsa)
2. `DeletedAt = UtcNow` set (soft delete) ya da hard delete (TBD: 90 gün sonra hard delete cleanup ayrı job)

**Soft vs hard delete:**
- İlk silmede `DeletedAt` set + R2 silinir (storage'da yer kalmaz)
- 90 gün sonra ikinci job ile DB satırı hard delete (audit trail için ara dönem)

### 5. Mobile Panel UI

**Yeni route:** `/posts`
**BottomNav'da konumu:** "Daha fazla" altında değil — kendi ikonu olsun. Mevcut tab'lar: Ana, Dekontlar, Siparişler, Daha fazla. **Yeni tab eklemek BottomNav'ı kalabalıklaştırır** — alternatifler:

| Seçenek | Etki |
|---------|------|
| Yeni tab "Duyurular" 5. olarak | BottomNav 5 tab'a çıkar (mevcut 4, dar olabilir) |
| Mevcut "Daha fazla" altında nav row | "Daha fazla → Duyurular" — bir tık fazla ama sade BottomNav |
| Ana ekranda üst kart | Ana ekrandaki "Aktif yayın" yanına "Son duyurun" |

**Önerim**: **"Daha fazla → Duyurular"** + Ana ekranda küçük teaser card. BottomNav 4 tab'lı kalır.

#### Ekranlar

**A. Duyurular listesi** (`/duyurular`)
- Üstte "+ Yeni duyuru" butonu
- Sabitlenmiş post'lar başta (📌 ikon)
- Sonra createdAt DESC sıralı normal post'lar
- Her kart: thumbnail (varsa) + ilk 100 char text + tarih + actions (•••)
- Action menu: Sabitle/Kaldır, Düzenle (caption), Sil
- Pagination: cursor-based, scroll to load

**B. Yeni duyuru ekranı** (`/duyurular/yeni`)
- Tip seçici tabs: Metin / Foto / Video
- Text mode: textarea (2000 char limit, counter)
- Photo mode: 
  - Capacitor `Camera.getPhoto({ resultType: 'uri' })` → galeriden seç veya çek
  - Önizleme + textarea (caption)
  - Otomatik client-side resize (max 2048×2048, JPEG 85%)
- Video mode:
  - Capacitor `Camera.getPhoto({ mediaType: 'video' })` → galeriden seç (kayıt için 3rd party)
  - Süre kontrol: > 45 sn → "Video çok uzun, kısalt" hata
  - Boyut kontrol: > 60 MB → hata
  - Format kontrol: mp4/mov dışı reddet
  - Önizleme thumbnail + textarea (caption)
- Sabitle toggle (default off)
- "Yayınla" butonu → upload-url → R2 upload → POST /posts → liste ekranına dön

**C. Duyuru detay/preview** (`/duyurular/{id}`)
- Tam media (foto büyük göster, video player)
- Full text body
- "Müşterilere şöyle görünür" notu
- Edit butonu (sadece caption) + Sabit toggle + Sil

**D. Ana ekran teaser**
- Mevcut "Aktif yayın" kartı altında "Son duyuru" kartı
- Yayıncının en son post'unun thumbnail + ilk 50 char text
- "Tüm duyuruları gör" linki → `/duyurular`

### 6. WPF değişikliği

**Hiç değişiklik yok.** Mobile-first karar. WPF tarafında sadece "Duyurular Mobile'da" notu eklenebilir Settings'te (opsiyonel, MVP'de yok).

İleride yayıncı isterse WPF'te ayrıca compose UI eklenir (Spec 6 veya 7 olabilir).

### 7. Cloudflare R2 setup

**Env değişkenleri (LicenseServer config):**
```
R2__AccountId=...
R2__AccessKeyId=...
R2__SecretAccessKey=...
R2__BucketName=orderdeck-broadcast-posts
R2__PublicReadAccess=false  (her zaman pre-signed)
```

**Setup adımları (deploy doc'da yazılacak):**
1. Cloudflare hesabı + R2 etkinleştirme (kredi kartı doğrulama ücretsiz)
2. Bucket oluştur: `orderdeck-broadcast-posts`
3. R2 API token üret (Object Read & Write izinli)
4. VPS .env'ye access key + secret koyma
5. CORS rule'u: mobile panel domain'inden PUT/GET izinli (`https://license.orderdeckapp.com`, `capacitor://localhost`)

**Mevcut AWSSDK.S3 paketi yeniden kullanılır** — R2 S3-compatible. `ServiceUrl`'i Cloudflare endpoint'ine set ederek tek `AmazonS3Client` ile çalışır:
```csharp
new AmazonS3Client(accessKey, secretKey, new AmazonS3Config {
    ServiceURL = "https://<accountId>.r2.cloudflarestorage.com",
    ForcePathStyle = true
});
```

## Test stratejisi

| Test | Kapsam |
|------|--------|
| Unit | Upload validation (boyut, mime, süre); pin/unpin ExpiresAt hesap |
| Integration | Endpoint CRUD; tenant izolasyonu; R2 stub'la mock'lanır |
| Auto-delete | Hangfire job → 30 gün geçmiş post silinir; pinned korunur |
| Mobile E2E (manuel) | Photo/video upload + post create + preview + delete |
| Storage stub | Test'lerde R2 yerine in-memory mock; gerçek R2 sadece manuel smoke'da |

## Migration

Tek EF migration: `AddBroadcastPosts`. Yeni `BroadcastPosts` tablosu. Mevcut data etkilenmez.

## Açık sorular (implementation aşamasında)

1. **R2 hesabı**: kim açacak? Cloudflare credit card varsa hızlı, yoksa setup.
2. **Auto-delete batch size**: 1000 OK mi, yoksa daha küçük (paged)?
3. **Video format reddi**: `video/x-msvideo` (AVI), `video/webm` desteklenecek mi? MVP'de sadece MP4/MOV.
4. **Caption düzenleme audit**: edit yapılınca old vs new body audit log'una mı yazılsın? KVKK gerek yok (yayıncının kendi içeriği).

## Sonraki adımlar

Bu spec onaylanırsa:
1. **writing-plans skill** → implementation plan (commit-by-commit)
2. **Execute** → kod yazılır
3. Müşteri app (Spec 1) implementation'a geçtikçe `/api/customer/feed` endpoint'i de bu altyapıya eklenir
