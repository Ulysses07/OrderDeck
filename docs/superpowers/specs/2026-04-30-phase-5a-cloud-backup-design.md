# Phase 5a — Cloud Backup with Admin Deep-View Design

**Date:** 2026-04-30
**Phase:** 5a (post-Phase 4 epic, first feature beyond licensing core)
**Status:** Spec — awaiting user review before plan
**Depends on:** Phase 4a-g (licensing infrastructure complete, JWT customer auth, admin Razor pages, audit log)

---

## 1. Goal

Müşterinin local SQLite veritabanı (`orderdeck.db`) yayın bittikten sonra OrderDeck sunucusuna **otomatik, şifreli, sessiz** yedeklensin. Bilgisayar bozulursa veya yenilenirse müşteri yeni cihazda login olunca yedeklerinden geri yükleyebilsin. Admin (uygulama sahibi) teknik destek için her müşterinin yedek içeriğini tarayıcı üzerinden detaylı görüntüleyebilsin.

---

## 2. User Story

> **Yayıncı (mezatçı):** Yayını bitiriyorum, hiçbir şey yapmama gerek yok — müşteri/etiket/ödeme verim arka planda buluta yedekleniyor. Bilgisayarım çalınırsa yenisinde aynı email/parola ile login olunca "Cloud yedeğinden geri yüklemek ister misiniz?" sorusuyla kaldığım yerden devam edebiliyorum.
>
> **Admin (Burak):** Bir kullanıcı destek talebi atıyor, "verilerim kayboldu" diyor. `/admin/customers/{id}/backups` sayfasını açıyorum. Son 5 yedek + aylık milestone'lar listeli. Tıklıyorum, yedeğin Özet sayfası: toplam ciro, yayın sayısı, en çok harcayan müşteri. Detay tab'larında müşteri/yayın/etiket tabloları paginated. Hiç indirmeden, tarayıcıda her şeyi görüyorum.

---

## 3. Design Decisions (Owner Choices)

### 3.1 Storage backend
**VPS filesystem** (`/opt/orderdeck/backups/{customerId}/`). 100 kullanıcı niş app, max 25GB worst case. VPS 37GB boş. S3-uyumlu storage YAGNI.

### 3.2 Backup trigger
**Otomatik, sadece `StreamSession.End` event'inde.** Manuel "Şimdi yedekle" butonu **yok** (UI'da hiçbir şey görünmesin owner kararı). Yayın yapılmazsa yedek de oluşmaz (veri değişmedi → sigorta gereksiz).

### 3.3 Encryption
**TLS in transit + AES-256-GCM at rest.** Server master key (`BACKUP_MASTER_KEY` env var, 32 byte hex). End-to-end değil — admin'in destek için decrypt yetkisi olmalı.

### 3.4 Retention
**Son 5 non-milestone + her ayın ilk yedeği milestone (sürekli korunur).**
- Aynı ay içinde 6+ yedek olabilir; ilki milestone, gerisi rolling-5
- Her customer için max ~600MB cap (sigorta)

### 3.5 Backup scope
**Yalnız `orderdeck.db`.** Settings cihaza özgü (printer adı, font), `auth.dat`/`license.dat` DPAPI ile makineye bağlı — taşıma anlamsız. ZIP compression (50-80% reduction).

### 3.6 Restore UX
**Yalnızca otomatik prompt** — yeni cihazda login sonrası, DB boş/eksik tespit edilirse `RestoreDialog` otomatik açılır. Settings'te manuel restore butonu **yok** (§3.9 owner UI silent kararıyla tutarlı). DB var olan kullanıcı zaten veri kaybetmediğine göre manuel restore'a ihtiyacı yok; veri kaybeden kullanıcının DB'si boş olur ve auto-prompt yakalar.

### 3.7 Client architecture
**Fire-and-forget `Task.Run()`** — UI bloklanmaz, retry yok. Yedek başarısız olursa sonraki yayın bitiminde otomatik yeni deneme. 100-user app için yeterli sağlamlık.

### 3.8 Admin viewer depth
**Deep view, in-browser, indirme yok.** Server backup blob'unu decrypt eder, SQLite'ı parse eder, Razor sayfaları olarak gösterir:
- **Özet** (default landing): toplam ciro, yayın/etiket/müşteri sayısı, ortalamalar, en iyiler
- **Müşteriler** tab: paginated table
- **Yayınlar** tab: session listesi
- **Etiketler** tab: label listesi (filter)
- **Çekilişler** tab: giveaway listesi

### 3.9 Privacy / disclosure
**Müşteri tarafında hiçbir disclosure yok.** Owner explicit choice: Settings'te de yazmaz, ToS'da da yazmaz, ilk yedekte toast da yok. Owner'ın bilinçli kararı; KVKK md.10 risk owner ile (bu spec'te kayıt altına alınmıştır).

Admin tarafında her yedek erişimi **AuditLog**'a kaydedilir — gelecekte gerekirse hesap verilebilir.

---

## 4. Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                      DESKTOP (OrderDeck.App)                          │
│                                                                        │
│  StreamSessionService.End()  ──fires──>  SessionEnded event           │
│         │                                                              │
│  AppHost subscribed: BackupService.QueueBackup("stream-end")          │
│         │                                                              │
│         ▼  Task.Run(fire-and-forget)                                  │
│  BackupService.RunBackupNowAsync():                                   │
│    1. PRAGMA wal_checkpoint(FULL)                                     │
│    2. File.Copy(DatabaseFile, tempCopy)                               │
│    3. ZipArchive(tempCopy, level=Optimal) → bytes                     │
│    4. SHA256 → hex                                                    │
│    5. await BackupClient.UploadAsync(bytes, sha256, MachineName)      │
│    6. Cleanup tempCopy                                                │
└──────────────────────────────────────────────────────────────────────┘
                                │ HTTPS / JWT Bearer-Customer
                                ▼
┌──────────────────────────────────────────────────────────────────────┐
│            VPS (OrderDeck.LicenseServer + Caddy)                      │
│                                                                        │
│  POST /api/v1/me/backups (octet-stream + X-Backup-Sha256 header)      │
│         │                                                              │
│  MeBackupsController                                                   │
│    1. Verify JWT customer claim                                       │
│    2. Verify SHA256 of received bytes                                 │
│    3. BackupStorageService.Encrypt(bytes, masterKey) → encryptedBytes │
│    4. Write to /app/Backups/{customerId}/{ts}.bin                     │
│    5. INSERT INTO CustomerBackups                                     │
│    6. BackupRetentionService.EnforceAfterInsert(customerId)           │
│         ├─ Mark this as milestone if first this month                 │
│         └─ Delete oldest non-milestone if count > 5 (cascade)         │
│    7. AuditLog: BackupCreated                                         │
│         │                                                              │
│         ▼                                                              │
│  Return 201 + { id, sizeBytes, createdAt, isMonthlyMilestone }        │
└──────────────────────────────────────────────────────────────────────┘

──────── ADMIN VIEWER PATH ──────────
GET /admin/customers/{id}/backups/{backupId}/summary
   └─> BackupViewerService:
        1. Read blob → AES decrypt → temp dir
        2. Open SQLite read-only
        3. Run 6 aggregate queries (ciro, count, ortalama, en iyiler)
        4. Audit: BackupAccessed{viewType: "summary"}
        5. Return Razor view
        6. finally: cleanup temp dir
```

---

## 5. Data Model

### 5.1 New entity (server)

```csharp
namespace OrderDeck.LicenseServer.Domain;

public sealed class CustomerBackup
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string BlobPath { get; set; } = "";        // /app/Backups/{customerId}/{ts}.bin
    public long SizeBytes { get; set; }                // encrypted blob size on disk
    public string ChecksumSha256 { get; set; } = "";   // pre-encrypt SHA256 of plaintext zip
    public bool IsMonthlyMilestone { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? UserAgent { get; set; }
    public string? MachineName { get; set; }
}
```

### 5.2 EF Migration 010 (`AddCustomerBackups`)

- New table `CustomerBackups` with FK to Customers (cascade delete: customer silinirse yedekleri de gider)
- Index `IX_CustomerBackups_CustomerId_CreatedAt` (DESC) — list query support

### 5.3 Filesystem layout

```
/opt/orderdeck/
├── backups/                          (host mount)
│   ├── {customer-guid}/
│   │   ├── 20260430-152030.bin       (encrypted blob)
│   │   └── 20260501-180012.bin
│   └── ...
└── docker-compose.yml volume mount: ./backups:/app/Backups
```

Permissions: `chmod 700`, owner root.

### 5.4 Encryption format

```
[12 bytes: nonce][16 bytes: auth tag][N bytes: ciphertext]
```

- AES-256-GCM (System.Security.Cryptography.AesGcm — BCL)
- Key: 32 bytes, server master key from env `BACKUP_MASTER_KEY`
- Nonce: random 12 bytes per backup (RandomNumberGenerator.GetBytes)
- Plaintext: ZIP archive containing single entry `orderdeck.db`

### 5.5 New env vars

- `BACKUP_MASTER_KEY` (required, 64 hex chars = 32 bytes)
- `BACKUP_STORAGE_ROOT` (default `/app/Backups`)
- `BACKUP_MAX_BLOB_SIZE_MB` (default 200)

---

## 6. REST API

### 6.1 Customer endpoints (`Bearer-Customer` JWT)

| Method | Path | Body / Header | Response |
|--|--|--|--|
| `POST` | `/api/v1/me/backups` | Octet-stream body (zip bytes), `X-Backup-Sha256: {hex}` | `201 Created` + metadata |
| `GET` | `/api/v1/me/backups` | — | `200 OK` array of metadata |
| `GET` | `/api/v1/me/backups/{id}/download` | — | `200 OK` octet-stream (decrypted plaintext zip) |
| `DELETE` | `/api/v1/me/backups/{id}` | — | `204 No Content` |

**Rate limits:**
- `POST` 6/hour per customer
- `DELETE` 30/hour
- `GET` no limit (cheap)

**Validation:**
- `POST` body size ≤ `BACKUP_MAX_BLOB_SIZE_MB`
- `X-Backup-Sha256` 64 hex chars exactly
- SHA256 of received bytes must match header

### 6.2 Admin endpoints (`Bearer-Admin` + Cookie scheme — Phase 4d auth)

| Method | Path | Description |
|--|--|--|
| `GET` | `/admin/customers/{id}/backups` | Razor page — backup listesi (5 + monthly), her satır "Görüntüle ▼" dropdown |
| `GET` | `/admin/customers/{id}/backups/{backupId}/summary` | Default landing — toplam ciro/yayın/müşteri özeti |
| `GET` | `/admin/customers/{id}/backups/{backupId}/customers?page=1` | Paginated Customer table (50/page) |
| `GET` | `/admin/customers/{id}/backups/{backupId}/sessions?page=1` | Paginated Session table |
| `GET` | `/admin/customers/{id}/backups/{backupId}/labels?page=1&sessionId=...` | Paginated Label table (filter) |
| `GET` | `/admin/customers/{id}/backups/{backupId}/giveaways?page=1` | Paginated Giveaway table |
| `DELETE` | `/admin/customers/{id}/backups/{backupId}` | Manual delete (audit log + soft confirmation) |

**Each admin view request:** decrypt blob → extract zip to `/tmp/{requestGuid}/orderdeck.db` → open SQLite read-only → query → render → finally clean up `/tmp/{requestGuid}`.

---

## 7. Server services

### 7.1 `BackupStorageService`
```csharp
public sealed class BackupStorageService
{
    public byte[] Encrypt(byte[] plaintext);
    public byte[] Decrypt(byte[] ciphertext);
    public Task<string> WriteBlobAsync(Guid customerId, byte[] encrypted, CancellationToken ct);
    public Task<byte[]> ReadBlobAsync(string blobPath, CancellationToken ct);
    public void DeleteBlob(string blobPath);
}
```

### 7.2 `BackupRetentionService`
```csharp
public sealed class BackupRetentionService
{
    /// <summary>After insert: mark monthly if first-of-month; trim non-milestones to 5.</summary>
    public Task EnforceAfterInsertAsync(Guid customerId, Guid newBackupId, CancellationToken ct);
}
```

Per-customer `SemaphoreSlim` lock — concurrent uploads serialize retention.

### 7.3 `BackupViewerService`
```csharp
public sealed class BackupViewerService : IDisposable
{
    public Task<BackupSession> OpenAsync(Guid backupId, CancellationToken ct);
}

public sealed class BackupSession : IDisposable  // owns temp dir + sqlite connection
{
    public Task<BackupSummary> GetSummaryAsync(CancellationToken ct);
    public Task<PagedResult<CustomerRow>> GetCustomersAsync(int page, string? search, CancellationToken ct);
    public Task<PagedResult<SessionRow>> GetSessionsAsync(int page, CancellationToken ct);
    public Task<PagedResult<LabelRow>> GetLabelsAsync(int page, string? sessionId, CancellationToken ct);
    public Task<PagedResult<GiveawayRow>> GetGiveawaysAsync(int page, CancellationToken ct);
    public void Dispose();  // close SQLite, delete temp dir
}
```

`BackupSession` per-request — `using` block in Razor page handler. No cross-request cache (KISS).

### 7.4 Razor PageModel pattern (Phase 4d compatible)

```csharp
public class CustomerBackupsModel : AdminPageModel  // existing base
{
    public List<CustomerBackup> Backups { get; private set; } = new();
    public Customer? Customer { get; private set; }

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct) { ... }
}

public class CustomerBackupSummaryModel : AdminPageModel
{
    public BackupSummary Summary { get; private set; } = null!;
    public CustomerBackup Backup { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(Guid id, Guid backupId, CancellationToken ct) { ... }
}
// + Customers/Sessions/Labels/Giveaways model classes (similar pattern)
```

---

## 8. Client SDK + App services

### 8.1 `OrderDeck.Licensing.Backup.IBackupClient`

```csharp
public interface IBackupClient
{
    Task<BackupMetadata> UploadAsync(byte[] zipPayload, string sha256Hex, string? machineName, CancellationToken ct);
    Task<IReadOnlyList<BackupMetadata>> ListAsync(CancellationToken ct);
    Task<byte[]> DownloadAsync(Guid backupId, CancellationToken ct);
    Task DeleteAsync(Guid backupId, CancellationToken ct);
}

public sealed record BackupMetadata(
    Guid Id, long SizeBytes, DateTimeOffset CreatedAt,
    bool IsMonthlyMilestone, string? MachineName);
```

`BackupClient` HttpClient wrapper; JWT bearer header from existing `LicenseApiClient` patterns. Throws `LicenseApiException` on non-2xx.

### 8.2 `OrderDeck.App.Services.BackupService`

```csharp
public sealed class BackupService
{
    /// <summary>Fire-and-forget. Returns immediately, runs Task in background.</summary>
    public void QueueBackup(string reason);

    /// <summary>For RestoreDialog "Şimdi yedekle" — awaitable, returns result.</summary>
    public Task<BackupResult> RunBackupNowAsync(CancellationToken ct);
}

public sealed record BackupResult(bool Success, string? Error, BackupMetadata? Metadata);
```

**Concurrency:** Single `SemaphoreSlim(1)` field — only one upload at a time per app instance. Fire-and-forget calls during active upload skip silently (logged warning).

### 8.3 `OrderDeck.App.Services.RestoreService`

```csharp
public sealed class RestoreService
{
    public Task<IReadOnlyList<BackupMetadata>> ListAvailableAsync(CancellationToken ct);
    public Task<RestoreResult> RestoreAsync(Guid backupId, CancellationToken ct);
}
```

Restore akışı:
1. Confirm dialog (`RestoreDialog` UI tarafında)
2. Download zip bytes via `BackupClient`
3. SHA256 verify
4. Stop hosted services + close DB connections (`AppHost.ShutdownDataServicesAsync`)
5. Copy current DB → `.pre-restore.bak` (rollback hedge)
6. Extract zip → overwrite `AppPaths.DatabaseFile`
7. Run `MigrationRunner` (eski schema yeni'ye migrate)
8. `Application.Current.Shutdown()` + dialog "Yeniden başlatın"

Startup hook (`RestoreRecoveryService`) → app start'ta `.pre-restore.bak` varsa "İptal/devam" sor, yarım kalmış restore detect.

### 8.4 Trigger wiring

`OrderDeck.Core/Sessions/StreamSessionService.cs` (mevcut):
```csharp
public event EventHandler<SessionEndedEventArgs>? SessionEnded;

public void End(string sessionId, long endedAt)
{
    _repo.End(sessionId, endedAt);
    SessionEnded?.Invoke(this, new SessionEndedEventArgs(sessionId, endedAt));
}
```

`OrderDeck.App.AppHost` startup:
```csharp
var streamSessionService = sp.GetRequiredService<StreamSessionService>();
var backupService = sp.GetRequiredService<BackupService>();
streamSessionService.SessionEnded += (s, e) => backupService.QueueBackup("stream-end");
```

Decoupled — Core doesn't reference App/Backup.

---

## 9. UI

### 9.1 Customer-side (OrderDeck.App)

**Settings'te yedekleme sekmesi YOK.** Sessiz feature (owner kararı, §3.9).

**RestoreDialog — yalnızca login sonrası DB boş senaryoda otomatik tetiklenir:**

`AppHost` startup'ta, license aktivasyon sonrası:
```csharp
var dbFile = AppPaths.DatabaseFile;
if (!File.Exists(dbFile) || new FileInfo(dbFile).Length < 10240) // <10KB = empty/fresh
{
    var available = await restoreService.ListAvailableAsync();
    if (available.Count > 0)
    {
        var dlg = sp.GetRequiredService<RestoreDialog>();
        dlg.PopulateOptions(available);
        dlg.ShowDialog();
    }
}
```

**RestoreDialog.xaml:**
- Başlık: "Yedek Bulundu"
- Mesaj: "Hesabınızda {N} cloud yedek var. Geri yüklemek ister misiniz?"
- ListBox: tarih/boyut/milestone badge
- Buttons: **[En son yedeği kullan]** **[Seçileni geri yükle]** **[Hayır, yeni başlat]**

**Manual restore from Settings:** Mevcut Settings'e dokunulmaz (owner kararı). Manuel restore kapsamı **olmaz** — yeni cihazda otomatik prompt yeterli.

**Düzeltme — Bölüm 3.6 ile uyum:** "Hibrit" dedik ama "settings'te bile yazmasın" geliyordu. Bu spec'te resmi karar: **sadece otomatik prompt** (DB boş senaryosu). Mevcut DB ile çalışan kullanıcı için manuel restore yok. Bu YAGNI: kullanıcı veri kaybetmedikçe restore'a ihtiyacı yok; veri kaybettiğinde DB zaten boş → otomatik prompt yakalar.

### 9.2 Admin-side (OrderDeck.LicenseServer/Pages/Admin)

**Mevcut `Customers/Detail.cshtml` sayfasına "Yedekler" tab (link/section):**

```
┌────────────────────────────────────────────────────────┐
│ Müşteri: musasevinc.007@gmail.com (Musa Sevinc)        │
│                                                          │
│ [Hesap] [Lisanslar] [Aktivasyonlar] [Yedekler ◄]       │
│ ─────────────────────────────────────────────────────  │
│ Yedekler (5/5 + 1 aylık)                               │
│                                                          │
│ # | Tarih           | Boyut | Aylık | Makine    | İşlem │
│ ──┼─────────────────┼───────┼───────┼───────────┼──────│
│ 6 │ 30 Nis 16:45    │ 45MB  │       │ BURAKS    │ [Görüntüle ▼] [Sil] │
│ 5 │ 28 Nis 22:10    │ 42MB  │       │ BURAKS    │ ... │
│ 4 │ 25 Nis 19:33    │ 41MB  │       │ BURAKS    │ ... │
│ 3 │ 20 Nis 14:20    │ 39MB  │       │ BURAKS    │ ... │
│ 2 │ 15 Nis 11:05    │ 38MB  │       │ BURAKS    │ ... │
│ 1 │  3 Nis 09:50    │ 35MB  │  ✓   │ BURAKS    │ ... │
└────────────────────────────────────────────────────────┘
```

**[Görüntüle ▼] dropdown items:**
- Özet (default — direkt tıklanırsa açılır)
- Müşteriler
- Yayınlar
- Etiketler
- Çekilişler

### 9.3 Özet (default landing)

```
┌────────────────────────────────────────────────────────┐
│ Yedek #6 — 30 Nis 2026 16:45                          │
│ ◄ Geri | [Müşteriler] [Yayınlar] [Etiketler] [Çekilişler]│
│                                                          │
│ ÖZET                                                    │
│ Toplam Yayın:        145                                │
│ Toplam Etiket:       8,234                              │
│ Toplam Tekil Müşteri: 412                               │
│ Toplam Ciro:         245,678 TL                         │
│                                                          │
│ ORTALAMALAR                                             │
│ Yayın başına:        1,694 TL                           │
│ Müşteri başına:      596 TL                             │
│                                                          │
│ EN İYİLER                                               │
│ En yüksek yayın:    12,450 TL  (15 Mar 2026)          │
│ En çok harcayan:    ali_2024 — 8,567 TL (47 etiket)   │
└────────────────────────────────────────────────────────┘
```

### 9.4 Detail tabs

**Müşteriler tab** — paginated table 50 satır/sayfa, search by Username/Email, sort by columns:

```
| Username | Platform | Display Name | Address    | Phone        | Total | LastSeen   |
```

**Yayınlar tab:**
```
| Title | StartedAt | EndedAt | Duration | Labels | Total Amount |
```

**Etiketler tab** (filter by sessionId):
```
| Session | Username | Code | Price | AddedAt | PrintedAt |
```

**Çekilişler tab:**
```
| Keyword | StartedAt | Participants | Winners | EndedAt |
```

---

## 10. Retention Logic

```csharp
async Task EnforceAfterInsertAsync(Guid customerId, Guid newBackupId, CancellationToken ct)
{
    using var lockHandle = await _customerLocks.WaitAsync(customerId, ct);

    var newBackup = await _db.CustomerBackups.FindAsync(newBackupId);
    var month = new DateTime(newBackup.CreatedAt.Year, newBackup.CreatedAt.Month, 1);

    // Step 1: Monthly milestone marker
    var existingThisMonth = await _db.CustomerBackups
        .Where(b => b.CustomerId == customerId
                 && b.CreatedAt >= month
                 && b.CreatedAt < month.AddMonths(1)
                 && b.Id != newBackupId)
        .AnyAsync(ct);
    if (!existingThisMonth)
    {
        newBackup.IsMonthlyMilestone = true;
        await _db.SaveChangesAsync(ct);
    }

    // Step 2: Trim non-milestones to 5 most recent
    var nonMilestones = await _db.CustomerBackups
        .Where(b => b.CustomerId == customerId && !b.IsMonthlyMilestone)
        .OrderByDescending(b => b.CreatedAt)
        .ToListAsync(ct);
    if (nonMilestones.Count > 5)
    {
        foreach (var old in nonMilestones.Skip(5))
        {
            _storage.DeleteBlob(old.BlobPath);
            _db.CustomerBackups.Remove(old);
            _audit.Log("BackupDeleted", new { customerId, backupId = old.Id, reason = "retention" });
        }
        await _db.SaveChangesAsync(ct);
    }
}
```

Worst case: 100 users × (5 non-milestone + ~12 monthly per year) ≈ 1700 backups × 50MB = 85GB after 1 year. Bu noktada VPS kapasitesi sıkışır — Phase 5b'de monthly milestone'ları da rotate eden 12-month cap ekleriz. **Phase 5a için**: 5 + monthly grow korkutucu değil çünkü 100 user × ~50MB × 12 ay ≈ 60GB → still under VPS 200GB upgrade path.

---

## 11. Error Handling

| Senaryo | Davranış |
|--|--|
| Upload network fail | Fire-and-forget catch + log. Manuel retry yok; sonraki yayın bitiminde otomatik. |
| Upload 401 | Re-login flow Phase 4b'de; backup skip. |
| Upload 413 | Server max 200MB. >200MB = log error + skip (imkansız case'e karşı). |
| Upload 507 quota | Olmaz (retention auto-cleans). Olursa log + skip. |
| Server disk full | Response 507 → log. v1: alarm yok. |
| AES decrypt fail (admin viewer) | "Bu yedek farklı master key ile şifrelendi (key rotated). Decrypt edilemiyor." sayfası. |
| SQLite parse fail (admin viewer) | "Bu yedek bu sürümle uyumsuz (eski client schema)." |
| Restore: app crash mid-restore | `.pre-restore.bak` dosyası → startup'ta `RestoreRecoveryService` detect → "Yarım kalmış restore — geri al / tamamla / iptal et" |
| SQLite WAL + open during backup | `PRAGMA wal_checkpoint(FULL)` + `File.Copy` (SQLite spec'e göre güvenli) |
| Concurrent upload + restore | `BackupService` SemaphoreSlim(1); restore zaten app shutdown gerektirir, race yok |

---

## 12. Audit Log

Phase 4d `AuditLog` tablosuna yeni 4 action type:

| Action | Subject | Detail |
|--|--|--|
| `BackupCreated` | customerId | `{ backupId, sizeBytes, isMonthlyMilestone }` |
| `BackupDeleted` | customerId | `{ backupId, reason: "retention" \| "manual" \| "admin" }` |
| `BackupAccessed` | adminUsername | `{ customerId, backupId, viewType: "summary" \| "customers" \| ... }` |
| `RestoreInitiated` | customerId | `{ backupId, machineName }` |

Admin'in **her** yedek view erişimi audit'lenir.

---

## 13. Testing Strategy

### 13.1 Server tests (`OrderDeck.LicenseServer.Tests`)

| Test sınıfı | Yeni test | Kapsam |
|--|--|--|
| `BackupStorageServiceTests` | 5 | Encrypt/decrypt round-trip, nonce uniqueness, tamper detection (auth tag fail), file write/read, key length validation |
| `BackupRetentionServiceTests` | 6 | Last-5 rule, monthly milestone preservation, 1-backup edge, 6-in-month edge, deletion cascades to filesystem, concurrent upload safety |
| `MeBackupsControllerTests` | 8 | POST happy path, GET list, GET download decrypts correctly, DELETE, 401, 413, retention triggered, SHA256 verification |
| `AdminBackupsControllerTests` | 6 | Summary stats accuracy, customers paginated/searched, decrypt failure shows error page, SQLite open failure handled, audit log written, admin auth required |
| `BackupViewerServiceTests` | 4 | Decrypt-to-temp lifecycle, query layer pagination, cleanup on exception, summary aggregation correctness |

### 13.2 Client tests (`OrderDeck.Tests`)

| Test sınıfı | Yeni test |
|--|--|
| `BackupServiceTests` | 4 (zip+SHA256, fire-and-forget exception swallow, concurrent skip, manual returns result) |
| `RestoreServiceTests` | 4 (download+verify checksum, .pre-restore.bak created, app shutdown signal, rollback on extract failure) |
| `BackupClientTests` (SDK) | 4 (HTTP serialization, JWT bearer, error mapping, retry off) |
| `RestoreRecoveryServiceTests` | 2 (.pre-restore.bak detection, prompt flow) |

### 13.3 Integration test

`MeBackupsRoundTripTests` (LicenseServer.Tests): WebApplicationFactory ile customer auth → POST upload → GET download → SHA256 + bytes equal. End-to-end.

**Total:** ~43 yeni test. Mevcut 499 → ~542.

---

## 14. Performance Targets

| Operation | Target |
|--|--|
| Backup upload (50MB DB → 15MB ZIP → 16MB encrypted) | < 30s on typical Turkish residential ADSL (5+ Mbps upload) |
| Admin viewer summary | < 500ms (decrypt + 6 SQLite aggregate queries) |
| Admin viewer paginated table | < 300ms (single SELECT with LIMIT/OFFSET) |
| Restore download + extract + migrate | < 60s |
| Retention enforcement | < 100ms (1 INSERT + ≤6 DELETE in DB + filesystem) |

---

## 15. Out of Scope (YAGNI)

- Manual "Şimdi yedekle" butonu (UI silent owner choice)
- Settings → Backup tab (UI silent)
- Backup encryption with customer-derived key (admin görmek istiyor — E2E iptal)
- Incremental / diff backup (full SQLite her seferinde, küçük dosya)
- Multi-region replication (single VPS yeterli)
- Mobil restore (sadece desktop)
- Webhook notifications (no real-time alerts in v1)
- Auto-schedule (sadece stream-end trigger)
- Backup compression algoritması optimization (ZIP Optimal yeterli)
- 12-month milestone cap (Phase 5b — daha sonra)
- KVKK consent UI (owner explicit choice §3.9)
- Bandwidth throttling (TLS native)

---

## 16. File Manifest

**Yeni dosyalar (server):**
- `OrderDeck.LicenseServer/Domain/CustomerBackup.cs`
- `OrderDeck.LicenseServer/Data/Migrations/{ts}_AddCustomerBackups.cs` (auto-gen)
- `OrderDeck.LicenseServer/Services/Backup/BackupStorageService.cs`
- `OrderDeck.LicenseServer/Services/Backup/BackupRetentionService.cs`
- `OrderDeck.LicenseServer/Services/Backup/BackupViewerService.cs`
- `OrderDeck.LicenseServer/Services/Backup/BackupSummary.cs` (DTO record)
- `OrderDeck.LicenseServer/Services/Backup/PagedResult.cs` (generic DTO)
- `OrderDeck.LicenseServer/Controllers/Backups/MeBackupsController.cs`
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Index.cshtml` + `.cs`
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Summary.cshtml` + `.cs`
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Customers.cshtml` + `.cs`
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Sessions.cshtml` + `.cs`
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Labels.cshtml` + `.cs`
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Backups/Giveaways.cshtml` + `.cs`
- 5 server test files

**Yeni dosyalar (client SDK):**
- `OrderDeck.Licensing/Backup/IBackupClient.cs`
- `OrderDeck.Licensing/Backup/BackupClient.cs`
- `OrderDeck.Licensing/Backup/BackupMetadata.cs`

**Yeni dosyalar (App):**
- `OrderDeck.App/Services/BackupService.cs`
- `OrderDeck.App/Services/RestoreService.cs`
- `OrderDeck.App/Services/RestoreRecoveryService.cs` (HostedService)
- `OrderDeck.App/Views/RestoreDialog.xaml` + `.cs`
- `OrderDeck.App/ViewModels/RestoreDialogViewModel.cs`

**Yeni dosyalar (Core, minimal):**
- `OrderDeck.Core/Sessions/SessionEndedEventArgs.cs`

**Değişen dosyalar:**
- `OrderDeck.Core/Sessions/StreamSessionService.cs` — SessionEnded event eklenir
- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — DbSet<CustomerBackup>
- `OrderDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml` — "Yedekler" tab/link
- `OrderDeck.LicenseServer/appsettings.json` — Backup config block
- `deploy/docker-compose.yml` — `BACKUP_MASTER_KEY`, `BACKUP_STORAGE_ROOT` env vars + `./backups:/app/Backups` volume
- `deploy/setup-backup-key.sh` (yeni) — master key bootstrap script (analog to setup-smtp.sh)
- `OrderDeck.App/AppHost.cs` — DI registration + SessionEnded subscription + RestoreRecoveryService
- 3 test file updates (existing repository tests, schema changes ripple)

---

## 17. Migration & Rollout

### 17.1 First-time deploy
1. EF migration 010 generated locally → committed
2. Deploy to VPS: `docker compose up -d --build`
3. EF migrations auto-applied at startup (Phase 4a pattern)
4. New volume `./backups:` created on first run
5. `BACKUP_MASTER_KEY` set via `setup-backup-key.sh` interactive (analog to setup-smtp.sh): generates 32-byte random key, writes to `.env`, restart container.
6. Existing customers: zero data initially; first stream-end after deploy creates first backup.

### 17.2 Master key rotation (future)
- Re-encrypt all blobs with new key (offline batch) — not in v1, but key in env makes rotation possible.

### 17.3 Backwards compat
- Existing 499 tests should remain green
- New env vars optional in dev (Email:Provider="disk" pattern: backup behavior gracefully degraded if BACKUP_MASTER_KEY missing — log warning, controller returns 503 "backup-disabled")

---

## 18. Future Considerations (Phase 5b+)

- **Phase 5b:** 12-month milestone cap, age-based retention
- **Phase 5c:** Customer-side disclosure UI (if KVKK risk realized)
- **Phase 5d:** End-to-end encryption option (admin viewer disabled per backup)
- **Phase 6:** Stripe/PayTR webhook (orijinal Phase 5 plan)
- **Operational:** S3-compatible storage (Backblaze B2 ~$5/100GB/year) when VPS storage filled

---

**End of Phase 5a Spec.**
