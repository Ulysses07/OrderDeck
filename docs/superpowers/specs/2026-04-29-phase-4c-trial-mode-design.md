# Faz 4c — Trial Mode (Tasarım)

**Hedef:** İlk açılışta otomatik 14 günlük deneme süresi başlat (hardware bind, Instagram-only). Trial state üç farklı disk lokasyonuna yazılır (HKCU registry + ProgramData + LocalAppData) — kullanıcı tek bir lokasyonu silse bile diğerlerinden hatırlanır (OR-logic anti-reset). Trial bittiğinde Phase 4b soft-gate uygulanır.

**Kapsam:** `LiveDeck.Licensing` projesine yeni `Trial/` klasörü + `LicenseStatus` enum'a 2 yeni değer + `LicenseService.InitializeAsync` flow'una trial fork + `ChatBridgeIngestor`'a Instagram-only filter + UI string güncellemeleri (LicenseStatusIndicator, AccountDialog). Yeni proje YOK.

**Pre-Faz-4c state:** Phase 4b HEAD `0962dd1`. 239/239 test geçer (121 LiveDeck + 56 Licensing + 62 LicenseServer). Build 0 error / 0 warning.

---

## 1. Bağlam

**Sorun:** Phase 4a + 4b ile lisanslama çalışıyor — ama satışa hazır olabilmek için müşterinin "denemeden satın al" akışına ihtiyaç var. Kayıt + email confirmation + admin'den lisans isteme gibi engelsiz, fresh install'da hemen kullanılabilen bir trial gerek.

**4c kapsamı (client-only):** Otomatik 14 gün trial, anti-reset (3 lokasyon OR), HW bind (anti-clone), Instagram-only platform kısıtı, expire sonrası Phase 4b soft-gate.

**4c kapsamında DEĞİL:**
- Server-side trial customer kaydı — pure client logic
- Trial uzatma admin paneli (Phase 4d)
- Email reminders (Phase 4e)
- Trial → paid otomatik upgrade flow (kullanıcı manuel olarak register + admin'in lisans ataması bekler)
- Crypto-strong tamper detection (HMAC yeter)
- Localization (sadece Türkçe)

---

## 2. Mimari

### 2.1 Solution etkisi

Yeni proje yok. Mevcut `LiveDeck.Licensing` projesine `Trial/` klasörü + `LicenseStatus` enum'una 2 yeni değer + `LicenseService` ctor'una 1 yeni dep. `LiveDeck.App` AppHost'a yeni 5 DI kaydı. `LiveDeck.Chat`/`LiveDeck.Core` AppPaths'e 1 path eklenir. Phase 4a server'a hiç dokunulmaz.

```
LiveDeck.Licensing/
├─ Trial/                                  (YENİ)
│  ├─ TrialRecord.cs                       (POCO + JSON)
│  ├─ TrialState.cs                        (NoTrial | Active | Expired record hierarchy)
│  ├─ ITrialStorage.cs
│  ├─ HkcuTrialStorage.cs                  (HKCU\Software\LiveDeck\Trial)
│  ├─ ProgramDataTrialStorage.cs           (C:\ProgramData\LiveDeck\trial.dat + HMAC)
│  ├─ LocalAppDataTrialStorage.cs          (DPAPI EncryptedStore reuse)
│  ├─ CompositeTrialStorage.cs             (OR-logic, write fan-out)
│  ├─ TrialHmac.cs                         (internal, embedded key)
│  └─ TrialService.cs                      (GetState/StartNewTrial)
├─ LicenseStatus.cs                        (MODIFIED — +TrialActive, +TrialExpired)
├─ LicensingOptions.cs                     (MODIFIED — +TrialDurationDays, +path opts)
└─ Services/
   └─ LicenseService.cs                    (MODIFIED — trial fork in InitializeAsync)
```

`LiveDeck.Licensing` hâlâ Windows-only (`net10.0-windows`) — Microsoft.Win32.Registry zaten kullanılıyor (Phase 4b HardwareIdProvider).

### 2.2 LicenseStatus enum genişlemesi

```csharp
public enum LicenseStatus
{
    Initializing,
    Active,           // Phase 4b
    OfflineGrace,     // Phase 4b
    OfflineExpired,   // Phase 4b
    ExpiredOnline,    // Phase 4b
    Revoked,          // Phase 4b
    NoLicense,        // Phase 4b
    TrialActive,      // YENİ — write-enabled, Instagram-only
    TrialExpired      // YENİ — soft-gate
}

public static class LicenseStatusExtensions
{
    public static bool IsWritable(this LicenseStatus status) =>
        status is LicenseStatus.Active
             or LicenseStatus.OfflineGrace
             or LicenseStatus.TrialActive;

    /// <summary>True when app should drop non-Instagram chat platforms.</summary>
    public static bool IsTrialMode(this LicenseStatus status) =>
        status is LicenseStatus.TrialActive or LicenseStatus.TrialExpired;
}
```

### 2.3 State Machine (Phase 4b + 4c kombine akışı)

```
InitializeAsync:
┌─ AuthStore.Load = null?
│   yes → InitializeTrialPath()
│         TrialService.GetState():
│           NoTrial → StartNewTrial() → TrialActive
│           Active  → TrialActive
│           Expired → TrialExpired
│
└─ AuthStore.Load var
   ┌─ TokenExpiresAt geçmiş?
   │  yes → AuthStore.Clear() → InitializeTrialPath()  (Auth temizledik, fresh trial path)
   │
   └─ Token geçerli
      LicenseStateStore.Load = null?
        yes → TrialService.GetState():
              NoTrial → NoLicense  (login yapmış kullanıcıya trial başlatma)
              Active  → TrialActive
              Expired → TrialExpired
        no  → RefreshAsync() — Phase 4b normal akış
```

`TrialService.StartNewTrial()` SADECE `auth is null` + `NoTrial` durumunda çağrılır. Login yapmış kullanıcı için yeni trial yaratılmaz.

### 2.4 Login öncelik ve trial state lifecycle

- Login başarılı → license akışı çalışır → `Active`/`OfflineGrace`/etc.
- **Trial state DOKUNULMAZ** (silinmez) — sadece ignore edilir
- Logout → `_authStore.Clear()` + `_licenseStore.Clear()` → trial state'e fallback (3 lokasyondan oku)
- Trial state hâlâ orada → `TrialActive` veya `TrialExpired` (kalan/bitmiş süreye göre)
- Asla yeni trial başlamaz çünkü `NoTrial` değil; ya `Active` ya `Expired`

### 2.5 LicenseService yeni public property'ler

```csharp
public sealed class LicenseService
{
    // Phase 4b mevcut:
    public LicenseStatus CurrentStatus { get; }
    public AuthRecord? CurrentAuth { get; }
    public LicenseRecord? CurrentLicense { get; }
    public event EventHandler<LicenseStatus>? StatusChanged;

    // Phase 4c YENİ:
    public TrialState? CurrentTrial { get; }     // TrialActive / TrialExpired durumunda dolu, diğerlerinde null
    public bool JustStartedTrial { get; }        // Initialize sırasında StartNewTrial çağrıldıysa true; ilk MainWindow Loaded'da banner gösterimi sonrası false yapılır
}
```

UI binding'leri `CurrentTrial` üzerinden kalan gün hesabı yapar (ör. `MainShellViewModel.LicenseStatusText` → "Deneme: {N} gün kaldı").

### 2.6 Anti-clone (HW bind)

`TrialRecord.HardwareFingerprint` Phase 4b `IHardwareIdProvider.GetHardwareId()` ile aynı (SHA-256 of MachineGuid + CPU.ProcessorId + Username). Bu fingerprint trial başlangıcında kaydedilir. `TrialService.GetState()` her okuma sırasında current HW ile karşılaştırır:

```
record.HardwareFingerprint != currentHwId → TrialExpired (treat as tampered)
```

VHD/disk imajı kopyalama saldırısına karşı: yeni makinede HW farklı → trial expired.

---

## 3. Trial State Format ve 3 Lokasyon

### 3.1 TrialRecord

```csharp
public sealed record TrialRecord(
    DateTimeOffset StartedAt,
    DateTimeOffset ExpiresAt,
    string HardwareFingerprint,
    int Version);   // 1 — schema migration için

// ExpiresAt = StartedAt + LicensingOptions.TrialDurationDays (default 14)
```

### 3.2 Lokasyon 1 — HKCU Registry

- Path: `HKEY_CURRENT_USER\Software\LiveDeck\Trial`
- Keys: `StartedAt` (REG_SZ ISO-8601), `ExpiresAt` (REG_SZ), `HardwareFingerprint` (REG_SZ), `Version` (REG_DWORD)
- DPAPI yok — registry zaten user-bound
- HKCU yazma için admin gerekmez
- Test stratejisi: testte gerçek HKCU subkey (`Software\LiveDeckTests\Trial-{Guid}`) kullanılır + cleanup `try { Delete(); } catch { }`

### 3.3 Lokasyon 2 — ProgramData

- Path: `C:\ProgramData\LiveDeck\trial.dat` (env override `LIVEDECK_TRIAL_PROGRAMDATA_PATH`)
- Format: Plain UTF-8 JSON
- Cross-user readable çünkü ProgramData all-users
- DPAPI yok (DPAPI per-user, ProgramData multi-user)
- HMAC tampering detection: 5. field `Hmac` (HMAC-SHA256 hex) — kanonik string `{StartedAt}|{ExpiresAt}|{HardwareFingerprint}|{Version}` üzerinde
- HMAC key: source code'a embedded 32 random byte (obfuscation, gerçek security değil)
- Yazma yetkisi: ProgramData ilk yazma için installer ACL açar; runtime'da fail olabilirse log warning + diğer 2 lokasyonla devam et

JSON örnek:
```json
{
  "startedAt": "2026-04-29T12:00:00Z",
  "expiresAt": "2026-05-13T12:00:00Z",
  "hardwareFingerprint": "abc123...",
  "version": 1,
  "hmac": "deadbeef..."
}
```

### 3.4 Lokasyon 3 — LocalAppData

- Path: `%LOCALAPPDATA%\LiveDeck\trial.dat` (`AppPaths.TrialFile` Core'da, Phase 4b auth/license file pattern ile aynı)
- Phase 4b'nin `EncryptedStore` (DPAPI + JSON) reuse edilir
- DPAPI scope: CurrentUser

### 3.5 Read Logic (OR — herhangi biri ile karar)

```
records = []
foreach storage in [Hkcu, ProgramData, LocalAppData]:
    try { records.append(storage.TryRead()); } catch { /* log warn, continue */ }

if records.IsEmpty: return NoTrial

// En geç ExpiresAt'i al — kullanıcı yedeklemeye çalıştıysa en güçlü kaydı baz al
record = records.OrderByDescending(r => r.ExpiresAt).First()

if record.HardwareFingerprint != currentHwId:
    return TrialExpired  // tampered/cloned

if Now >= record.ExpiresAt:
    return TrialExpired

return TrialActive(remainingDays = (record.ExpiresAt - Now).Days, expiresAt = record.ExpiresAt)
```

### 3.6 Write Logic (yeni trial başlatma)

```
record = new TrialRecord(StartedAt: Now, ExpiresAt: Now + 14d, HardwareFingerprint: hwId, Version: 1)
successCount = 0
foreach storage in [Hkcu, ProgramData, LocalAppData]:
    try { storage.Write(record); successCount++ }
    catch (ex) { _log.LogWarning(ex, "Failed to persist trial state to {Storage}", storage.Name) }

if successCount == 0:
    throw new InvalidOperationException("Trial state could not be persisted to any location.")

return TrialActive(remainingDays = 14, expiresAt = record.ExpiresAt)
```

### 3.7 ITrialStorage interface

```csharp
public interface ITrialStorage
{
    string Name { get; }
    TrialRecord? TryRead();
    void Write(TrialRecord record);
    void Clear();
}
```

`Clear()` testler için. Üretim kodu trial.dat dosyasını silmez (anti-reset).

### 3.8 CompositeTrialStorage

Üç storage'ı birleştirir + OR-logic read + fan-out write + log adapter. `Read()` döndürdüğü `TrialRecord?` "en güçlü" kayıt (en geç `ExpiresAt`). `Write()` 3 lokasyona da yazar, 0 başarılıysa exception.

---

## 4. UI Adaptasyonları

### 4.1 LicenseStatusIndicator (top bar text)

`MainShellViewModel.UpdateLicenseUiFromService()` switch'ine 2 yeni case:

| Status | Brush | Text |
|---|---|---|
| `TrialActive` | `Brushes.DodgerBlue` | "Deneme: {N} gün kaldı" |
| `TrialExpired` | `Brushes.Crimson` | "Deneme süresi doldu — Lisans gerekli" |

`MainShellViewModel.CurrentLicense` Phase 4b'de set ediliyor. Trial mode'da `null` (license yok). Yeni property: `MainShellViewModel.TrialState : TrialState?` — binding için kalan gün hesaplanır.

### 4.2 AccountDialog mod genişlemesi

`AccountDialogViewModel` ctor'da `LicenseService.CurrentStatus` üzerinden mod belirler:

| Mod | Email/Name | License info | Butonlar |
|---|---|---|---|
| `Active` (Phase 4b) | dolu | dolu | Çıkış yap, Tekrar bağlan |
| `OfflineGrace`/`OfflineExpired` (Phase 4b) | dolu | son bilinen | Çıkış yap, Tekrar bağlan |
| `Revoked`/`ExpiredOnline` (Phase 4b) | dolu | bitmiş kayıt | Çıkış yap |
| `TrialActive` (YENİ) | "—" | "Deneme süresi: {N} gün kaldı" | "Hesap oluştur / Giriş yap" |
| `TrialExpired` (YENİ) | "—" | "Deneme süresi doldu ({DD.MM.YYYY})" | "Hesap oluştur / Giriş yap" |
| `NoLicense` (auth var, license yok) | dolu | "Lisans atanmamış" | Çıkış yap, Yenile |

Yeni komut: `OpenLoginCommand` → LoginDialog modal açar. Dialog `DialogResult==true` ise AccountDialog `RequestClose`.

### 4.3 LoginDialog değişiklik YOK

Mevcut 4 mod (Login/Register/ConfirmPending/LicenseSelection) korunur. "Trial başlat" butonu yok (otomatik). Trial→paid akışı: Register → confirm → login → AccountDialog "Lisans atanmamış" → admin lisans verir → app restart → `Active`.

### 4.4 İlk açılış banner

Trial yeni başladığında MainWindow `Loaded` event'inde 1 kez `MessageBox.Show()`:

```
"Deneme süresi başladı. 14 gün boyunca Instagram chat ile tüm özellikleri ücretsiz kullanabilirsiniz."
```

`LicenseService.JustStartedTrial` boolean flag — Initialize sırasında `StartNewTrial` çağrılırsa true, sonra ilk MainWindow Loaded'da gösterilir + flag false yapılır. Sonraki açılışlarda gösterilmez.

### 4.5 Soft-gate (Phase 4b ile aynı)

`MainShellViewModel.IsLicenseWritable` → `CurrentStatus.IsWritable()` zaten kuruldu (Phase 4b). `TrialActive` true döner (yeni `IsWritable` extension), `TrialExpired` false. 11 write komut otomatik doğru davranır — ek değişiklik gerekmez.

---

## 5. ChatBridgeIngestor — Instagram-Only Enforcement

### 5.1 Filter konum

`LiveDeck.Chat.Ingestors.ChatBridgeIngestor.Process(message)` metoduna 1 satır filter:

```csharp
if (_license.CurrentStatus.IsTrialMode() &&
    !string.Equals(msg.Platform, "instagram", StringComparison.OrdinalIgnoreCase))
{
    _log.LogDebug("Trial mode: dropping non-Instagram chat from {Platform}", msg.Platform);
    return;
}
```

### 5.2 Dependency injection

`ChatBridgeIngestor` ctor'una yeni param: `LicenseService license`. AppHost'taki kayıt `services.AddSingleton<ChatBridgeIngestor>()` — DI otomatik resolve eder.

### 5.3 Performance / lock-free

`LicenseService.CurrentStatus` Phase 4b'de event-driven property (volatile-ish, race-tolerant). Filter her mesajda okur — extra cost negligible (single enum compare + string compare).

### 5.4 Settings dialog

Phase 2a SettingsDialog'da TikTok-spesifik bir alan yoksa, runtime drop yeter — kullanıcıya görünür ipucu yok ama OK. Eğer TikTok config alanı varsa: `IsTrialMode` true ise alan disable + tooltip "Bu özellik trial sürümünde kullanılamaz". (Implementation phase'de SettingsDialog re-check edilecek; spec şimdilik runtime drop yeterli sayar.)

---

## 6. Konfigürasyon + DI

### 6.1 LicensingOptions genişlemesi

```csharp
public sealed class LicensingOptions
{
    // Phase 4b
    public string ServerBaseUrl { get; set; } = "https://license.livedeck.app";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int OfflineGraceDays { get; set; } = 14;
    public int HeartbeatIntervalHours { get; set; } = 24;

    // Phase 4c
    public int TrialDurationDays { get; set; } = 14;
    public string TrialRegistrySubKey { get; set; } = @"Software\LiveDeck\Trial";
    public string TrialProgramDataPath { get; set; } = @"C:\ProgramData\LiveDeck\trial.dat";
}
```

Env override (AppHost.BuildLicensingOptions extension):
- `LIVEDECK_TRIAL_DURATION_DAYS` (test)
- `LIVEDECK_TRIAL_PROGRAMDATA_PATH` (CI / non-admin dev)
- `LIVEDECK_TRIAL_REGISTRY_SUBKEY` (test isolation)

### 6.2 AppPaths

`LiveDeck.Core/AppPaths.cs`:
```csharp
public static string TrialFile => Path.Combine(DataFolder, "trial.dat");
```

(Phase 4b `AuthFile` / `LicenseFile` ile aynı pattern — Documents/LiveDeck/data altında.)

### 6.3 AppHost DI

```csharp
// Trial (Phase 4c)
services.AddSingleton<HkcuTrialStorage>();      // ctor: IOptions<LicensingOptions>, IHardwareIdProvider, ILogger
services.AddSingleton<ProgramDataTrialStorage>();
services.AddSingleton<LocalAppDataTrialStorage>(); // ctor: EncryptedStore, AppPaths.TrialFile
services.AddSingleton<ITrialStorage>(sp => new CompositeTrialStorage(
    sp.GetRequiredService<HkcuTrialStorage>(),
    sp.GetRequiredService<ProgramDataTrialStorage>(),
    sp.GetRequiredService<LocalAppDataTrialStorage>(),
    sp.GetRequiredService<ILogger<CompositeTrialStorage>>()));
services.AddSingleton<TrialService>();           // ctor: ITrialStorage, IHardwareIdProvider, IOptions<LicensingOptions>, IClock, ILogger
```

`LicenseService` ctor'a 1 yeni param eklenir: `TrialService trialService`. DI otomatik resolve eder.

`ChatBridgeIngestor` ctor'a 1 yeni param: `LicenseService license`.

---

## 7. Test Stratejisi

### 7.1 LiveDeck.Licensing.Tests/Trial/

| Test dosyası | Test sayısı | Kapsam |
|---|---|---|
| `TrialRecordTests.cs` | 2 | record equality, JSON roundtrip |
| `HkcuTrialStorageTests.cs` | 4 | write/read/clear/missing-key |
| `ProgramDataTrialStorageTests.cs` | 5 | roundtrip, HMAC verify, HMAC tampered, missing file, malformed JSON |
| `LocalAppDataTrialStorageTests.cs` | 3 | roundtrip, missing file, decrypt fail (Phase 4b EncryptedStore reuse) |
| `CompositeTrialStorageTests.cs` | 5 | OR read latest, write fan-out, write tolerates partial failure, all-fail throws, clear all |
| `TrialServiceTests.cs` | 6 | NoTrial→StartNew, Active continues, Expired by time, Expired by HW mismatch, StartNew on expired throws/no-op, idempotent reads |

**Alt toplam:** 25 yeni Trial test.

### 7.2 LiveDeck.Licensing.Tests/Services/LicenseServiceTrialTests.cs

| Senaryo | |
|---|---|
| Auth yok + NoTrial | TrialActive (yeni başlar) |
| Auth yok + Trial Active kayıtlı | TrialActive (continue) |
| Auth yok + Trial Expired | TrialExpired |
| Auth var (token geçerli) + license yok + NoTrial | NoLicense (trial başlamaz) |
| Auth var + license yok + Trial Active | TrialActive |
| Auth var + license yok + Trial Expired | TrialExpired |
| Auth var + license var + valid | Phase 4b Active path (regression) |
| Logout | Trial state preserve, fallback olarak yeniden değerlendirilir |

**Alt toplam:** 8 yeni LicenseService trial test.

### 7.3 LiveDeck.Tests/Chat/ChatBridgeTrialFilterTests.cs

| Senaryo | |
|---|---|
| TrialActive + Instagram msg | Geçer (Process called) |
| TrialActive + TikTok msg | Drop (Process NOT called) |
| TrialExpired + TikTok msg | Drop |
| Active license + TikTok msg | Geçer (filter bypass) |

**Alt toplam:** 4 yeni chat filter test.

### 7.4 Toplam

- Mevcut: 121 LiveDeck + 56 Licensing + 62 LicenseServer = **239**
- Yeni: 25 (Trial) + 8 (LicenseService trial) + 4 (Chat filter) = **37**
- Phase 4c sonrası hedef: **~276** (tahmini ±2)

### 7.5 LicensingOptions.TrialRegistrySubKey isolation

Testlerin gerçek HKCU\Software\LiveDeck\Trial'ı kullanmaması için her test class'ında unique subkey:
```csharp
opts.TrialRegistrySubKey = $"Software\\LiveDeckTests\\{Guid.NewGuid():N}";
```
`Dispose()` ile cleanup.

`LIVEDECK_TRIAL_PROGRAMDATA_PATH` env var test temp dir kullanır. `AppPaths.TrialFile` test'te direct override edilemez — testlerde `LocalAppDataTrialStorage` ctor'u explicit path alır.

---

## 8. Manuel Smoke Plan

Phase 4a server local'de çalışıyor olmalı (test 8 için).

1. **Fresh state:** Sil:
   - `%LOCALAPPDATA%\LiveDeck\` klasörü (auth.dat, license.dat, trial.dat)
   - `C:\ProgramData\LiveDeck\trial.dat`
   - HKCU\Software\LiveDeck\Trial registry subkey
2. **Trial start:** App başlat → MessageBox "Deneme süresi başladı, 14 gün". Indicator mavi "Deneme: 14 gün kaldı".
3. **Write yetkileri:** Print, Customer create, Giveaway create — hepsi çalışır.
4. **Instagram-only:** Browser extension ile TikTok'tan mesaj gönder → log'da DEBUG "dropping non-Instagram chat". Instagram mesajı normal akar.
5. **Anti-reset 1:** App kapat. HKCU\Software\LiveDeck\Trial subkey'i sil (regedit). App başlat → trial state hâlâ aktif (ProgramData + LocalAppData hayatta). Indicator "Deneme: N gün kaldı".
6. **Anti-reset 2:** App kapat. `C:\ProgramData\LiveDeck\trial.dat` sil. App başlat → trial state hâlâ aktif (LocalAppData hayatta).
7. **Anti-reset 3 (silmeli):** App kapat. `%LOCALAPPDATA%\LiveDeck\trial.dat` da sil. Şimdi 2/3 lokasyon yok. App başlat → restored from registry (oh wait, registry de silmiştik adım 5'te). Ekstra adım: registry geri yazılmıyor. Hmm bu test düşürür.

   **Düzeltme:** Adım 5+6+7 sırasıyla yapılırsa anti-reset bozulur (sadece registry kalır, ama o silindi). Testleri ayrı çalıştır: önce 5'i (HKCU sil) → continue OK → state restore (HKCU yeniden yazılmıyor — Write sadece `StartNewTrial`'da çağrılır). Sonra fresh state'le başla, sadece 6'yı dene. Sonra 7'yi.

   **Net davranış:** OR-logic sayesinde 1 lokasyon yetiyor; ama silinen lokasyonlar **geri yazılmıyor**. Anti-reset şu kadar güçlü: 3 lokasyonu **aynı oturumda** silmeyi gerektirir.
8. **Anti-clone:** `LIVEDECK_TRIAL_DURATION_DAYS=14` + farklı `LIVEDECK_HWID_OVERRIDE` (tabii bu env yok ama testler için) — gerçek smoke için: trial.dat'ı manuel düzenle, `hardwareFingerprint`'i farklı bir hash yap → app başlat → `TrialExpired` (HW mismatch).
9. **Trial expire:** `LIVEDECK_TRIAL_DURATION_DAYS=0` env ile başlat → `TrialExpired`, indicator kırmızı, soft-gate aktif (Print disabled).
10. **Trial → paid akışı:** TrialActive iken AccountDialog → "Hesap oluştur" → LoginDialog Register → confirm-email → login. Postman ile admin lisans ata. App restart → `Active`. Indicator yeşil.
11. **Logout sonrası trial fallback:** Login + Active iken AccountDialog → "Çıkış yap". Trial state hâlâ kayıtlı (3 lokasyon) → indicator mavi/kırmızı (kalan/biten süreye göre).

---

## 9. YAGNI (yapılmayanlar)

- Server-side trial customer kaydı
- Trial uzatma (admin tool, Phase 4d)
- Trial reset alarmı server'a (telemetri)
- Multi-tier trial (Pro 30g, Standart 14g — sadece sabit 14g)
- Disposable email blacklist
- Crypto-strong tampering (ECDSA imzalı record vb.)
- Trial countdown push notification ("3 gün kaldı")
- Trial banner Settings'den kapatma seçeneği
- HW change tolerance (1 component değişse continue) — strict mismatch yeter
- Trial başlatma için kullanıcı consent ekranı (otomatik)
- Localization
- Auto-update / installer ProgramData ACL ayarı (deploy notu olarak ileride ele alınır)

---

## 10. Phase 4c Sonrası Açık Bırakılanlar

- **Phase 4d:** Admin web UI — trial kullanıcı listesi, manuel trial uzatma, audit log
- **Phase 4e:** Email otomasyonu — yenileme/expire reminder; trial expire 1g öncesi reminder buraya entegre olabilir
- **Phase 5:** Stripe/PayTR webhook → otomatik lisans

---

## 11. Kabul Kriterleri

- ✅ Build temiz: 0 error / 0 warning (production code)
- ✅ ~276 test pass (239 baseline + ~37 yeni)
- ✅ Fresh install → app açılışta otomatik trial başlatma + bilgilendirme banner
- ✅ Trial Active iken Print/Customer/Giveaway/Blacklist write komutları çalışır
- ✅ Trial Active iken TikTok platformu chat mesajları drop edilir (Instagram geçer)
- ✅ Trial Active iken `LicenseStatus.IsWritable()` true, soft-gate açık
- ✅ Anti-reset: 1 lokasyon (HKCU veya ProgramData veya LocalAppData) silinince state diğerlerinden continue
- ✅ Anti-clone: `TrialRecord.HardwareFingerprint` ≠ current → `TrialExpired`
- ✅ Trial 14 gün doldu → `TrialExpired`, soft-gate (Phase 4b ile aynı)
- ✅ AccountDialog `TrialActive` mode → email/name "—", "Hesap oluştur / Giriş yap" butonu LoginDialog açar
- ✅ Login → trial state ignore, license precedence; logout → trial state restore
- ✅ Phase 4b regression: 121 LiveDeck + 56 Licensing + 62 LicenseServer test bozulmamış
- ✅ HMAC tampering: ProgramData JSON'unda `expiresAt` elle değiştirilse → o lokasyon ignore edilir (HMAC mismatch), diğer 2'den okur
