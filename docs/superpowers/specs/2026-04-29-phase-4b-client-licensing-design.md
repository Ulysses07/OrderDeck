# Faz 4b — Client Licensing Modülü (Tasarım)

**Hedef:** `LiveDeck.Licensing` adında yeni client modülü ekle — DPAPI ile şifrelenmiş local auth/license storage, hardware fingerprint üretimi, login/register dialog, Phase 4a license server'ı ile entegrasyon, lisans expired/missing → soft-gate read-only mode.

**Kapsam:** 1 yeni library projesi (`LiveDeck.Licensing`) + 1 yeni test projesi (`LiveDeck.Licensing.Tests`) + Phase 4a server'a 1 endpoint patch (`GET /api/v1/me/licenses`) + LiveDeck.App'e DI/UI entegrasyonu.

**Pre-Faz-4b state:** Phase 4a HEAD `f14f0ae` (master'a merge edildi). LiveDeck.LicenseServer.Tests 61/61, LiveDeck.Tests 117/117. Solution build 0 error / 0 warning.

---

## 1. Bağlam

**Sorun:** Phase 4a server hazır — register/login/license/activate endpoint'leri çalışıyor. Ama LiveDeck.App hâlâ "lisans bilinci olmayan" dogfood durumunda. Müşteriye satışa hazır olabilmek için client tarafının lisansı tanıması, doğrulaması, ve geçersizse uygulama özelliklerini soft-gate ile kısıtlaması gerek.

**4b kapsamı (client-only):** Auth + license storage (DPAPI), hardware ID, server validation, offline grace, login/register dialog, soft-gate read-only mode entegrasyonu. Trial mode 4c'de. Admin web UI 4d'de. Email otomasyonu (yenileme uyarıları) 4e'de.

**4b kapsamında DEĞİL:** Trial, password reset, token refresh, multi-account, lisans transfer dialog, localization (yalnızca Türkçe).

---

## 2. Mimari

### 2.1 Solution etkisi

Mevcut 8 proje (5 LiveDeck.* + 3 LicenseServer.*) sabit. 2 yeni proje eklenir + LiveDeck.App genişletilir + Phase 4a server'a 1 metot eklenir:

```
LiveDeck.sln
├─ LiveDeck.Core             (mevcut, dokunulmaz)
├─ LiveDeck.Chat             (mevcut, dokunulmaz)
├─ LiveDeck.Overlay          (mevcut, dokunulmaz)
├─ LiveDeck.Labeling         (mevcut, dokunulmaz)
├─ LiveDeck.App              (mevcut, +LoginDialog/AccountDialog/MainShell binding değişiklikleri)
├─ LiveDeck.Tests            (mevcut, +DI registration testleri)
├─ LiveDeck.LicenseServer    (mevcut, +GET /me/licenses endpoint)
├─ LiveDeck.LicenseServer.Tests (mevcut, +1 test)
├─ LiveDeck.Licensing        (YENİ — net10.0-windows class library)
└─ LiveDeck.Licensing.Tests  (YENİ — xunit, net10.0-windows)
```

Bağımlılık akışı:
```
LiveDeck.App → LiveDeck.Licensing → System.Security.Cryptography (DPAPI) +
                                    System.Management (WMI) +
                                    Microsoft.Win32 (Registry) +
                                    HttpClient
LiveDeck.App → LiveDeck.Core (mevcut)
LiveDeck.Licensing  ↛  LiveDeck.Core   (referans yok, izole)
```

`LiveDeck.Licensing` **Windows-only** (`net10.0-windows`) — DPAPI ve WMI Windows'a özgü. LiveDeck.App zaten Windows-only, çakışma yok.

### 2.2 Stack

| Bileşen | Seçim | Gerekçe |
|---|---|---|
| Encryption | `System.Security.Cryptography.ProtectedData` (DPAPI), `DataProtectionScope.CurrentUser` | OS-level key, kullanıcıya bağlı, ek key management yok |
| Hardware ID | SHA-256(MachineGuid + CPU.ProcessorId + Username) | Reinstall + clone-restore'a duyarlı, OEM placeholder problemi yok |
| HTTP client | `HttpClient` + `IHttpClientFactory` (named) | Standart, retry policy eklenebilir |
| JSON | `System.Text.Json` | BCL, ek paket yok |
| Background timer | `IHostedService` (BackgroundService) | Mevcut ChatBridge ingestor pattern ile aynı |
| Test framework | xUnit + FluentAssertions + WireMock.Net | Diğer test projeleriyle uyumlu |

`WireMock.Net` yeni paket — `LicenseApiClient` HTTP testleri için. Alternatif `HttpMessageHandler` mock'u idi ama WireMock daha temiz ve çoklu endpoint senaryolarında okunabilir.

### 2.3 Proje yapısı

```
LiveDeck.Licensing/
├─ HardwareIdProvider.cs              IHardwareIdProvider impl, SHA-256 composite
├─ IHardwareIdProvider.cs             Mock'lanabilir interface
├─ LicenseStatus.cs                   enum: Initializing, Active, OfflineGrace,
│                                     OfflineExpired, ExpiredOnline, Revoked, NoLicense
├─ Storage/
│  ├─ EncryptedStore.cs               DPAPI Protect/Unprotect + JSON serialize
│  ├─ AuthStore.cs                    auth.dat (token + customerId + email + tokenExpiresAt)
│  ├─ LicenseStateStore.cs            license.dat (key + lastValidatedAt + lastSuccessfulOnlineAt)
│  └─ AuthRecord.cs / LicenseRecord.cs (POCO records)
├─ Api/
│  ├─ LicenseApiClient.cs             HttpClient wrapper, all endpoint methods
│  ├─ LicenseApiOptions.cs            BaseUrl + Timeout
│  ├─ LicenseApiException.cs          + concrete subtypes (InvalidCredentials,
│  │                                    EmailNotConfirmed, LicenseRevoked,
│  │                                    LicenseExpired, SlotFull, Network, Unknown)
│  └─ Models/                         Request/Response DTOs (records)
├─ Services/
│  ├─ LoginService.cs                 Login + register + resend + me/licenses orchestration
│  ├─ LicenseService.cs               State machine controller, CurrentStatus property
│  └─ HeartbeatHostedService.cs       BackgroundService: 24h timer + onstart validate
└─ LicensingOptions.cs                Public options record
```

### 2.4 State Machine

```
                              ┌────────────────┐
       app start              │  Initializing  │
       ────────────────►      └───────┬────────┘
                                      │
                              auth.dat var?
                              ┌──────┴──────┐
                          yes │             │ no
                              ▼             ▼
                       Validate(server)   NoLicense
                          ┌──────┴──────────────┐
              network OK  │                     │ network fail
              ┌───────────┼───────────┐         ▼
        status="active"  status=  status=     check (now - lastSuccessfulOnlineAt)
              │          "revoked"  "expired"  ┌─────────┴─────────┐
              ▼            │           │       ≤ 14 d        > 14 d
            Active       Revoked  ExpiredOnline   ▼              ▼
                                              OfflineGrace   OfflineExpired
```

Writable status'ler: `Active`, `OfflineGrace`. Diğer hepsi soft-gate.

---

## 3. Hardware Fingerprint

### 3.1 Algoritma

```
machineGuid := registry HKLM\SOFTWARE\Microsoft\Cryptography\MachineGuid (string)
cpuId       := WMI Win32_Processor.ProcessorId (string, ilk işlemci)
username    := Environment.UserName (lowercase)

raw         := $"{machineGuid}|{cpuId}|{username}"
hwId        := SHA-256(raw) → 64-char hex lowercase
```

### 3.2 Kararlılık

- Aynı kullanıcı, aynı makinede: deterministik
- Windows reinstall: `MachineGuid` değişir → yeni hwId
- Aynı makinede başka kullanıcı login olursa: `username` değişir → yeni hwId (kasıtlı — multi-user separation)
- Sanal makineler: `MachineGuid` clone-VM'de aynı kalır ama `cpuId` host CPU'su olabilir → çoğunlukla eşsiz

### 3.3 Hata yönetimi

- WMI çağrısı timeout (5s) — fail olursa: `cpuId = "unknown-cpu"` ile devam (fingerprint hâlâ üretilir, ama machineGuid+username yeterli)
- Registry erişimi fail: `IOException` fırlatır — license akışı boots dialog ile başlar, "donanım kimliği okunamadı" mesajı

### 3.4 Test edilebilirlik

`IHardwareIdProvider` interface, prod'da `HardwareIdProvider`, testlerde sabit "test-hw-fp-{Guid}" döndüren fake. `LicenseService`/`LicenseApiClient` testleri HW okumaz.

---

## 4. Encrypted Storage (DPAPI)

### 4.1 Konum

```
%LOCALAPPDATA%\LiveDeck\auth.dat
%LOCALAPPDATA%\LiveDeck\license.dat
```

`Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + `LiveDeck` alt-klasörü.

### 4.2 DPAPI scope

`DataProtectionScope.CurrentUser` — sadece o kullanıcı decrypt eder.

```csharp
byte[] cipher = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
byte[] plain  = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
```

`optionalEntropy` kullanılmaz — basitlik. (Alternatif olarak hwId entropy verilebilir ama HW değişirse decrypt fail olur, gereksiz koruma; DPAPI kullanıcı bağı zaten yeterli.)

### 4.3 Format

`auth.dat` (decrypt sonrası UTF-8 JSON):
```json
{
  "customerId": "guid",
  "email": "user@example.com",
  "name": "User Name",
  "token": "eyJhbG...",
  "tokenExpiresAt": "2026-05-06T12:00:00Z"
}
```

`license.dat` (decrypt sonrası UTF-8 JSON):
```json
{
  "licenseKey": "LDK-XXXX...",
  "skuCode": "STD",
  "expiresAt": "2027-04-29T...",
  "remainingDaysAtLastCheck": 364,
  "lastValidatedAt": "2026-04-29T12:00:00Z",
  "lastSuccessfulOnlineAt": "2026-04-29T12:00:00Z",
  "lastKnownStatus": "Active"
}
```

### 4.4 Tamper / clone davranışı

- Başka makineye dosya kopyalanırsa: DPAPI decrypt `CryptographicException` fırlatır
- `EncryptedStore.TryLoad<T>()` exception'ı yutarak `null` döner ve dosyayı siler
- `LicenseService` null gördüğünde: `NoLicense` state — fresh login akışı

### 4.5 Logout

- `AuthStore.Clear()` → `auth.dat` silinir
- `license.dat` silinmez — bir sonraki login'de aynı customer ise lisans state'i hatırda
- Farklı customer ile login → server validate fail → license.dat silinir, fresh activate

---

## 5. API Client + Error Handling

### 5.1 LicenseApiClient endpoint'leri

| Metot | HTTP | Path | Auth | Phase 4a'da var mı? |
|---|---|---|---|---|
| `RegisterAsync` | POST | `/api/v1/auth/register` | anon | ✓ |
| `ResendConfirmationAsync` | POST | `/api/v1/auth/resend-confirmation` | anon | ✓ |
| `LoginAsync` | POST | `/api/v1/auth/login` | anon | ✓ |
| `GetMeAsync` | GET | `/api/v1/me` | Bearer-Customer | ✓ |
| `ChangePasswordAsync` | POST | `/api/v1/me/password` | Bearer-Customer | ✓ |
| `GetMyLicensesAsync` | GET | `/api/v1/me/licenses` | Bearer-Customer | ✗ → **4a patch** |
| `ValidateAsync` | POST | `/api/v1/licenses/validate` | Bearer-Customer | ✓ |
| `ActivateAsync` | POST | `/api/v1/licenses/activate` | Bearer-Customer | ✓ |
| `DeactivateAsync` | POST | `/api/v1/licenses/deactivate` | Bearer-Customer | ✓ |
| `HeartbeatAsync` | POST | `/api/v1/licenses/heartbeat` | Bearer-Customer | ✓ |

### 5.2 Hata mapping

| HTTP / payload | Exception | UI handling |
|---|---|---|
| 200/201/202/204 | — | success |
| 400 (validation) | `ValidationException` | dialog'da inline error |
| 401 (login) | `InvalidCredentialsException` | dialog: "E-posta veya şifre yanlış" |
| 401 (otomatik validate) | token expired → `AuthStore.Clear()`, state=NoLicense | sessizce login dialog |
| 403 + title="email-not-confirmed" | `EmailNotConfirmedException` | dialog: "E-postanı doğrula. Tekrar yolla?" |
| 404 (validate) | `LicenseNotFoundException` | state=NoLicense |
| 409 + title="license-revoked" | `LicenseRevokedException` | state=Revoked |
| 409 + title="license-expired" | `LicenseExpiredException` | state=ExpiredOnline |
| 409 + title="slot-full" | `SlotFullException` | dialog: "Tüm cihaz slotları dolu. Diğer cihazda çıkış yap" |
| Network timeout / DNS / 5xx | `LicenseApiNetworkException` | offline grace decision |
| Other 4xx | `LicenseApiUnknownException` | log + dialog: "beklenmeyen hata, log'a bak" |

### 5.3 Network policy

- Default timeout: 10s (LicensingOptions.RequestTimeoutSeconds)
- Heartbeat retry: 3 deneme, exponential backoff 5s/15s/45s, son denemede başarısız ise NetworkException — `lastSuccessfulOnlineAt` güncellenmez
- Login/register/activate: tek deneme, fail durumunda kullanıcıya manuel retry

### 5.4 Response model'ler

`Models/` klasörü altında records:
```csharp
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record MeResponse(Guid Id, string Email, string Name, DateTimeOffset? EmailConfirmedAt);
public sealed record LicenseSummary(string LicenseKey, string SkuCode, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
public sealed record ValidateRequest(string LicenseKey, string HardwareFingerprint);
public sealed record ValidateResponse(string Status, DateTimeOffset? ExpiresAt, int? RemainingDays, string? Sku, SlotInfoDto? SlotInfo);
public sealed record SlotInfoDto(int Used, int Total, bool ThisDeviceActive);
public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName);
public sealed record ActivateResponse(Guid ActivationId);
// ... vs.
```

---

## 6. UI Entegrasyonu

### 6.1 Yeni dialog'lar

`LiveDeck.App/Views/`:

**`LoginDialog.xaml`** — 3 mod state machine'i tek dialog'da:
- **Login mode** (default): Email + password TextBox, "Giriş yap" butonu, "Hesap oluştur" link, "Şifremi unuttum" (4e'de aktif olacak, şimdilik disabled).
- **Register mode**: Email + name + password + password confirm, "Kayıt ol" butonu, "Giriş ekranına dön" link.
- **Confirm-pending mode** (register sonrası): "Doğrulama linki yolladık. E-postanı kontrol et." + "Tekrar yolla" butonu + "Giriş ekranına dön" link.
- **License selection mode** (login sonrası, customer'ın >1 aktif lisansı varsa): Liste + "Bu makineye aktive et" butonu.

`LoginDialogViewModel` — state property + commands. Failure mesajları property olarak (binding + Visibility converter).

**`AccountDialog.xaml`** — Login state'inde, "Hesap" menü item'ından açılır:
- Email + name (read-only)
- Aktif lisans bilgisi (key, sku, expires)
- "Çıkış yap" butonu (auth.dat sil + state=NoLicense + close)
- "Yeni cihaza geç" butonu (mevcut cihazı deactivate, login dialog yeniden açılır) — slot transfer için

### 6.2 MainShell entegrasyonu

**Top bar — yeni `LicenseStatusIndicator`:**
- `LicenseService.CurrentStatus` binding
- Renk kodu:
  - `Active` (yeşil): "Lisans aktif — N gün kaldı"
  - `OfflineGrace` (sarı): "Çevrimdışı — N gün kaldı"
  - `OfflineExpired` / `ExpiredOnline` / `Revoked` (kırmızı): "Lisans gerekli"
  - `NoLicense` (gri): "Lisans yok"

**⋮ menü — yeni "Hesap" item'ı** AccountDialog'u açar. NoLicense state'inde label "Giriş yap" olur.

### 6.3 Soft-gate (read-only mode)

`MainShellViewModel.IsLicenseWritable` boolean property — `LicenseService.CurrentStatus` çevrildi: `status == Active || status == OfflineGrace`.

Disable edilen komutlar (`CanExecute` ile bind):
- `PrintCommand`, `MultiPrintCommand` (etiket basma)
- "Müşteri ekle" akışları (CustomerSearchDialog'dan create butonu)
- Chat ingest start/restart butonları
- `NewGiveawayDialog` (giveaway create)
- `AddToBlacklistDialog` (kara liste ekleme)
- Settings dialog: "Kaydet" butonu disable, "Kapat" açık

Açık kalan:
- Customer search/detail (görüntüleme)
- Stream history / report (görüntüleme)
- Settings dialog (sadece okuma)
- Account dialog (logout + re-login)
- BlacklistDialog (görüntüleme, "Çıkar" disable)
- StreamHistoryDialog, ShortcutHelpDialog (tüm görüntüleme dialog'ları)

`LicenseStatusIndicator`'a tıklanırsa AccountDialog açılır.

### 6.4 Startup akışı

`App.xaml.cs.OnStartup`:
```
1. AppHost.Build()
2. LicenseService = host.GetRequiredService<LicenseService>()
3. await LicenseService.InitializeAsync()
   ├─ AuthStore.Load → auth.dat var mı?
   │  ├─ yok → CurrentStatus = NoLicense
   │  └─ var → token expired mi?
   │           ├─ evet → AuthStore.Clear, CurrentStatus = NoLicense
   │           └─ hayır → LicenseStateStore.Load → key var mı?
   │                       ├─ var → ValidateAsync → state'e göre
   │                       └─ yok → CurrentStatus = NoLicense (login var ama lisans yok)
4. CurrentStatus.IsModalRequired (NoLicense) ise:
   ├─ LoginDialog.ShowDialog()
   │  ├─ login OK → activate akışı çalışır → CurrentStatus güncellenir
   │  └─ user iptal → app.Shutdown()
5. MainShell.Show()
6. HeartbeatHostedService start → 24h timer
```

`IsModalRequired` mapping:
- `NoLicense` → modal (uygulama lisanssız çalışmaz)
- `Active`, `OfflineGrace`, `OfflineExpired`, `ExpiredOnline`, `Revoked` → MainShell yine açılır, soft-gate uygulanır

`OfflineExpired` özel: app açılır, top bar kırmızı, AccountDialog açıldığında "Tekrar bağlan + login" butonu görünür → online validate dener.

---

## 7. Konfigürasyon + DI

### 7.1 LicensingOptions

`LiveDeck.Licensing/LicensingOptions.cs`:
```csharp
public sealed class LicensingOptions
{
    public string ServerBaseUrl { get; set; } = "https://license.livedeck.app";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int OfflineGraceDays { get; set; } = 14;
    public int HeartbeatIntervalHours { get; set; } = 24;
}
```

### 7.2 appsettings.json (LiveDeck.App)

Mevcut `appsettings.json`'a yeni section:
```json
{
  "Licensing": {
    "ServerBaseUrl": "https://license.livedeck.app",
    "RequestTimeoutSeconds": 10,
    "OfflineGraceDays": 14,
    "HeartbeatIntervalHours": 24
  }
}
```

`appsettings.Development.json` (yoksa oluşturulur):
```json
{
  "Licensing": {
    "ServerBaseUrl": "https://localhost:5001"
  }
}
```

### 7.3 AppHost DI

`LiveDeck.App/AppHost.cs`'e eklenecekler (configurations and services bloğu):
```csharp
services.Configure<LicensingOptions>(config.GetSection("Licensing"));
services.AddSingleton<IHardwareIdProvider, HardwareIdProvider>();
services.AddSingleton<EncryptedStore>();
services.AddSingleton<AuthStore>();
services.AddSingleton<LicenseStateStore>();
services.AddHttpClient<LicenseApiClient>((sp, http) =>
{
    var opt = sp.GetRequiredService<IOptions<LicensingOptions>>().Value;
    http.BaseAddress = new Uri(opt.ServerBaseUrl);
    http.Timeout = TimeSpan.FromSeconds(opt.RequestTimeoutSeconds);
});
services.AddSingleton<LoginService>();
services.AddSingleton<LicenseService>();
services.AddHostedService<HeartbeatHostedService>();
```

### 7.4 ChangePassword scope karar

Phase 4a'da `POST /me/password` endpoint'i var. Phase 4b'de bunu LicenseApiClient'a method olarak ekleriz ama UI'da kullanmayız (AccountDialog'da görünmez). Kullanım Phase 4d veya kullanıcı talebi geldiğinde aktive edilir. Endpoint metot olarak hazır olsun, UI yok.

---

## 8. Server-Side Patch (Phase 4a Uzantısı)

Phase 4a master'a merge edildi. Phase 4b'nin ilk task'ı olarak küçük bir patch:

### 8.1 Yeni endpoint

`LiveDeck.LicenseServer/Controllers/Auth/MeController.cs`:
```csharp
[HttpGet("licenses")]
public async Task<IActionResult> GetMyLicenses(CancellationToken ct)
{
    var id = GetCustomerId();
    var rows = await _db.Licenses
        .Where(l => l.CustomerId == id && l.RevokedAt == null && l.ExpiresAt > DateTimeOffset.UtcNow)
        .OrderByDescending(l => l.IssuedAt)
        .Select(l => new
        {
            licenseKey = l.LicenseKey,
            skuCode = l.SkuCode,
            expiresAt = l.ExpiresAt,
            revokedAt = (DateTimeOffset?)null  // her zaman null filter sebebiyle
        })
        .ToListAsync(ct);
    return Ok(rows);
}
```

`LicenseDbContext` injection MeController'a eklenir. Mevcut PasswordHasher injection bozulmaz.

### 8.2 Test

`LiveDeck.LicenseServer.Tests/Auth/MeTests.cs`'e yeni test:
```csharp
[Fact]
public async Task Get_my_licenses_returns_active_only()
```
Senaryo: customer kaydol/confirm/login → admin 2 lisans ihraç et (1 aktif, 1 revoke) → GET /me/licenses → response 1 satır, aktif olan.

### 8.3 Etki

LicenseServer test count: 61 → 62.
LiveDeck.LicenseServer.csproj'a değişiklik yok.

---

## 9. Test Stratejisi

### 9.1 LiveDeck.Licensing.Tests yeni proje

`net10.0-windows`, FluentAssertions + WireMock.Net + xUnit + Microsoft.NET.Test.Sdk.

```
LiveDeck.Licensing.Tests/
├─ Storage/
│  ├─ EncryptedStoreTests.cs           DPAPI roundtrip + tamper detection
│  ├─ AuthStoreTests.cs                save/load/clear semantics
│  └─ LicenseStateStoreTests.cs        save/load/clear semantics
├─ HardwareIdProviderTests.cs          deterministic hash (mock'lı registry/WMI)
├─ Api/
│  └─ LicenseApiClientTests.cs         WireMock fixture, all endpoints + error mapping
├─ Services/
│  ├─ LicenseServiceStateTests.cs      State machine: Active → ExpiredOnline → OfflineGrace
│  ├─ LicenseServiceInitializeTests.cs auth/license combinations on startup
│  ├─ LoginServiceTests.cs             register/confirm/login orchestration
│  └─ HeartbeatHostedServiceTests.cs   timer fires, retry on network fail
└─ TestHelpers/
   ├─ FakeHardwareIdProvider.cs
   ├─ TempDirectoryFixture.cs          (her test ayrı temp dir)
   └─ WireMockFixture.cs
```

**Tahmini test sayısı:** 30 (≈)
- EncryptedStore: 4 (roundtrip, tamper, clone, missing file)
- AuthStore + LicenseStateStore: 6 (save/load/clear/null fields)
- HardwareIdProvider: 3 (deterministic, WMI fail fallback, registry fail throws)
- LicenseApiClient: 10 (her endpoint + error scenarios)
- LicenseService state machine: 5 (state transitions)
- LicenseService.InitializeAsync: 4 (auth+license combinations)
- LoginService: 3 (login, register confirm-pending, license selection)
- HeartbeatHostedService: 2 (timer fire, network retry)

### 9.2 LiveDeck.Tests (mevcut) etkisi

DI registration testleri:
- LicenseService DI'dan resolve edilebiliyor mu?
- HardwareIdProvider singleton mı?

Mevcut 117 test bozulmamalı. AppHost değişikliği test edilmeden: ~3 yeni test.

### 9.3 LicenseServer.Tests (mevcut) etkisi

`/me/licenses` endpoint testi: +1 test → 62 toplam.

### 9.4 Toplam test count

- Mevcut: 117 + 61 = 178
- Yeni: ~30 (Licensing) + ~3 (App registration) + 1 (server) = ~34
- Hedef: ~212 test geçer.

---

## 10. Manuel Smoke Plan

Phase 4a server local'de DiskEmailSender ile çalışıyor olmalı.

1. **Fresh state:** `%LOCALAPPDATA%\LiveDeck\` klasöründen auth.dat / license.dat sil. App başlat.
2. **Register:** LoginDialog → "Hesap oluştur" → email + name + password gir → "Kayıt ol" → "Doğrulama linki yolladık" mesajı.
3. **Confirm:** Server'ın `tmp/emails/` klasöründeki `.eml` dosyasını aç → confirm URL'ini Browser'da aç → "OK" gör.
4. **Login:** "Giriş ekranına dön" → email + password → "Giriş yap" → "Lisans yok" mesajı (henüz lisans atanmamış).
5. **Admin lisans ihraç:** Postman/curl ile admin login → POST `/api/v1/admin/licenses` body `{customerEmail, skuCode:"STD"}`.
6. **App restart:** auto-login → tek lisans var, otomatik aktivate → MainShell açılır, indicator yeşil "Lisans aktif — 365 gün kaldı".
7. **Heartbeat görünür:** `tail -f` ile server logları → 24 saat beklenmez ama HostedService başlangıç logu görünür.
8. **Server kapat:** `Ctrl+C` ile durdur → app restart → InitializeAsync ValidateAsync fail → OfflineGrace, indicator sarı.
9. **OfflineExpired simülasyonu:** `license.dat`'i geliştirici aracıyla (küçük PowerShell `[System.Security.Cryptography.ProtectedData]` çağırıcısı) decrypt et, `lastSuccessfulOnlineAt` -15g olacak şekilde değiştir, tekrar encrypt et, dosyayı yaz → app restart → OfflineExpired, indicator kırmızı, soft-gate.
10. **Server geri aç:** app restart → ValidateAsync OK → Active state'e dön.
11. **Soft-gate test:** OfflineExpired/Revoked state'inde Print butonu disable, Customer search açık, Settings dialog "Kaydet" disable.
12. **Logout:** AccountDialog → "Çıkış yap" → auth.dat silinir → LoginDialog modal.
13. **Slot dolu:** İkinci PC'de aynı hesap login → otomatik activate → SlotFull → dialog "Tüm slotlar dolu". İlk PC'de logout/deactivate → ikinci PC retry → success.
14. **Force-deactivate:** Admin DELETE `/api/v1/admin/activations/{id}` → ilk PC'nin sonraki heartbeat'inde NotActivated → otomatik re-activate dener (slot uygunsa) → state Active.

---

## 11. YAGNI (yapılmayanlar)

- Trial mode (Phase 4c)
- Password reset / forgot password (Phase 4e)
- Token refresh — login zaten 7g, expire olunca login dialog
- Multi-account — tek aktif hesap
- Lisans transfer UI (force-deactivate başka cihaz) — admin web UI (Phase 4d)
- Offline mode'da daha gelişmiş (heartbeat queue, sonra senkron) — gereksiz
- Hangfire / event-driven heartbeat — basit Timer yeter
- Encrypted entropy in DPAPI (HW bound) — gereksiz, kullanıcı bağı yeter
- Hardware fingerprint v2 (BIOS UUID, motherboard serial) — fail rate yüksek
- Crash recovery / state migration
- Localization — sadece Türkçe
- Auto-update / installer — out of scope
- Network proxy / corporate firewall handling
- Audit log (kim ne zaman login oldu) — server tarafında zaten LastSeenAt var

---

## 12. Phase 4b Sonrası Açık Bırakılanlar

- **Phase 4c:** Trial mode (Instagram-only, 14g, anti-reset)
- **Phase 4d:** Admin web UI (Razor Pages veya Blazor) — kullanıcı listesi, lisans manuel ihraç, audit log
- **Phase 4e:** Email otomasyonu — yenileme uyarıları (14/7/3/0 gün), expired sonrası, password reset email akışı, Hangfire/HostedService
- **Phase 5:** Stripe/PayTR webhook → otomatik lisans ihraç

---

## 13. Kabul Kriterleri

- ✅ Build temiz: 0 error, 0 warning
- ✅ ~212 test pass (178 baseline + ~34 yeni)
- ✅ `LiveDeck.Licensing` projesi `net10.0-windows`, LiveDeck.Core'a referans yok
- ✅ App ilk açılışta auth.dat yoksa LoginDialog modal
- ✅ Register → email confirm → login → otomatik activate akışı end-to-end manuel test geçer
- ✅ Server kapatıldığında 14 gün boyunca app çalışmaya devam eder (OfflineGrace)
- ✅ 14 gün dolduğunda OfflineExpired, soft-gate (Print, Customer create, Chat start, Giveaway, Blacklist disable)
- ✅ Force-deactivate sonrası heartbeat ile yeniden aktivate dener
- ✅ DPAPI scope=CurrentUser → dosya başka makineye kopyalanırsa fresh-login akışı tetiklenir
- ✅ HW fingerprint deterministik, aynı kullanıcı/makinede hep aynı string
- ✅ Phase 4a 117 + 61 mevcut test bozulmamış (regression sentinel)
- ✅ `GET /api/v1/me/licenses` endpoint çalışır, sadece aktif (revoke olmayan + expired olmayan) lisansları döner
