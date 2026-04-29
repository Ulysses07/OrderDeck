# Faz 4a — LicenseServer İskelet (Tasarım)

**Hedef:** Bağımsız ASP.NET Core REST API + SQL Server schema + email/şifre customer auth + self-signup + email doğrulama + lisans CRUD + N-slot aktivasyon yönetimi. Client-side entegrasyon Phase 4b'de.

**Kapsam:** 2 yeni proje (`LiveDeck.LicenseServer` + `LiveDeck.LicenseServer.Tests`). Mevcut LiveDeck.* projelerine **dokunulmaz**.

**Pre-Faz-4a state:** Phase 3b-2 HEAD `4cd6add`. 117/117 test geçer.

---

## 1. Bağlam

**Sorun:** LiveDeck dış müşterilere satılmak için lisanslama gerek. Şu an "geliştirici dogfood + manuel anahtar dağıtımı" yeterli ama ölçeklenmez. Server-side bir ürün — kayıt, doğrulama, aktivasyon yönetimi, yenileme kontrolü.

**4a kapsamı (server-only):** REST API + DB schema + auth + email gönderim altyapısı + lisans/aktivasyon endpoint'leri. Client-side modül (`LiveDeck.Licensing`) Phase 4b. Trial mode 4c. Admin web UI 4d. Yenileme email'leri 4e.

---

## 2. Mimari

### 2.1 Solution etkisi

Mevcut 5 proje + 1 test projesi DOKUNULMAZ. 2 yeni proje eklenir:

```
LiveDeck.sln
├─ LiveDeck.Core             (mevcut)
├─ LiveDeck.Chat             (mevcut)
├─ LiveDeck.Overlay          (mevcut)
├─ LiveDeck.Labeling         (mevcut)
├─ LiveDeck.App              (mevcut)
├─ LiveDeck.Tests            (mevcut)
├─ LiveDeck.LicenseServer        (YENİ)
└─ LiveDeck.LicenseServer.Tests  (YENİ)
```

LicenseServer **bağımsız** — diğer LiveDeck projelerine reference YOK. Tamamen ayrı deployable.

### 2.2 Stack

| Bileşen | Seçim | Gerekçe |
|---|---|---|
| Web framework | ASP.NET Core 10 (Controllers) | Kullanıcı tercihi |
| ORM | EF Core 9 + SqlServer | Kullanıcı tercihi |
| DB | SQL Server (LocalDB dev, Docker mssql prod) | Kullanıcı tercihi |
| Password hash | Argon2id (`Konscious.Security.Cryptography.Argon2`) | OWASP 2024 önerisi |
| Auth | JWT Bearer (HS256), iki ayrı scheme (Customer/Admin) | Stateless, kolay client integration |
| Email | MailKit + IEmailSender abstraction | Test edilebilirlik (DiskEmailSender dev, SmtpEmailSender prod) |
| Rate limiting | ASP.NET Core 10 native rate limiter | Native, ekstra paket yok |
| Test | xUnit + FluentAssertions + WebApplicationFactory | Mevcut codebase convention |

### 2.3 Project yapısı

```
LiveDeck.LicenseServer/
├─ Controllers/
│  ├─ Auth/
│  │  ├─ AuthController.cs              (register, login, confirm-email, resend, /me)
│  │  └─ AdminAuthController.cs         (admin login)
│  ├─ Licenses/
│  │  ├─ LicensesController.cs          (validate, activate, deactivate, heartbeat — JWT customer)
│  │  └─ AdminLicensesController.cs     (issue, revoke, extend, list — JWT admin)
│  ├─ Customers/
│  │  └─ AdminCustomersController.cs    (list, get, manual confirm)
│  ├─ Activations/
│  │  └─ AdminActivationsController.cs  (force-deactivate)
│  └─ Skus/
│     └─ AdminSkusController.cs         (list)
├─ Domain/
│  ├─ Customer.cs
│  ├─ AdminUser.cs
│  ├─ Sku.cs
│  ├─ License.cs
│  ├─ Activation.cs
│  └─ EmailConfirmationToken.cs
├─ Data/
│  ├─ LicenseDbContext.cs
│  └─ Migrations/
├─ Services/
│  ├─ Auth/
│  │  ├─ PasswordHasher.cs
│  │  ├─ JwtTokenService.cs
│  │  └─ EmailConfirmationService.cs
│  ├─ Licensing/
│  │  ├─ LicenseIssuer.cs
│  │  ├─ LicenseValidator.cs
│  │  └─ ActivationManager.cs
│  └─ Email/
│     ├─ IEmailSender.cs
│     ├─ SmtpEmailSender.cs
│     ├─ DiskEmailSender.cs
│     └─ EmailTemplates.cs
├─ Auth/
│  ├─ JwtCustomerScheme.cs
│  └─ JwtAdminScheme.cs
├─ Program.cs
├─ appsettings.json
├─ appsettings.Development.json
├─ Dockerfile
└─ LiveDeck.LicenseServer.csproj

LiveDeck.LicenseServer.Tests/
├─ Auth/
├─ Licenses/
├─ Activations/
├─ Services/
├─ TestHelpers/
│  ├─ ApiFactory.cs
│  ├─ TestDbContext.cs
│  └─ TestEmailSender.cs
└─ LiveDeck.LicenseServer.Tests.csproj
```

---

## 3. Domain Modeli

### 3.1 Entity tabloları

```csharp
public sealed class Customer
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";              // unique
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";       // Argon2id
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }                   // admin notları
    public ICollection<License> Licenses { get; } = new List<License>();
}

public sealed class AdminUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";           // unique
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class Sku
{
    public string Code { get; set; } = "";               // PK ("STD", "PRO")
    public string DisplayName { get; set; } = "";
    public int DefaultDurationDays { get; set; }
    public int DefaultActivationSlots { get; set; }
    public string? Description { get; set; }
}

public sealed class License
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = "";         // unique "LDK-{32hex}"
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string SkuCode { get; set; } = "";
    public Sku Sku { get; set; } = null!;
    public int ActivationSlots { get; set; }             // SKU default'tan kopya
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }
    public ICollection<Activation> Activations { get; } = new List<Activation>();
}

public sealed class Activation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string HardwareFingerprint { get; set; } = "";   // SHA-256 hex
    public string? MachineName { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
}

public sealed class EmailConfirmationToken
{
    public Guid Token { get; set; }                      // PK, kullanıcıya link'te gider
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }          // single-use
}
```

### 3.2 Indexler & constraint'ler

- `Customer.Email` UNIQUE (case-insensitive)
- `AdminUser.Username` UNIQUE
- `License.LicenseKey` UNIQUE
- `Activation` UNIQUE INDEX `(LicenseId, HardwareFingerprint) WHERE DeactivatedAt IS NULL` — aynı cihaz aktif iken tekil
- `EmailConfirmationToken.Token` PK, INDEX `(CustomerId, UsedAt)` resend için
- FK cascade: `Customer DELETE → CASCADE Licenses → CASCADE Activations`

### 3.3 Seed (migration ile)

- `Skus`: `STD` (365 gün, 1 slot), `PRO` (365 gün, 3 slot)
- `AdminUsers`: tek admin — `appsettings.json` `Admin:InitialUsername` + `Admin:InitialPasswordHash` varsa seed; yoksa skip (development için ortam değişkeni)

### 3.4 Lisans anahtar formatı

`LDK-{32-char hex}` örn. `LDK-7F2A89C4D1E6B34F8A91E0D7C3B56A2F`. Üretim: `Guid.NewGuid().ToString("N").ToUpperInvariant()`. Prefix format validation için, gözle ayırt edilebilir.

---

## 4. REST API Envanteri

### 4.1 Public (anonim) — 4 endpoint

| Method | Path | Body | Response |
|---|---|---|---|
| POST | `/api/v1/auth/register` | `{email, name, password}` | `201` (confirmation email gönderilir) |
| GET | `/api/v1/auth/confirm-email/{token}` | — | `200 {ok: true}` veya `400 {error}` |
| POST | `/api/v1/auth/resend-confirmation` | `{email}` | `202` (sessizce, enumeration koruması) |
| POST | `/api/v1/auth/login` | `{email, password}` | `200 {token, expiresAt}` veya `401`/`403 email-not-confirmed` |

### 4.2 Customer JWT (Bearer-Customer scheme, 7 gün) — 7 endpoint

| Method | Path | Açıklama |
|---|---|---|
| GET | `/api/v1/me` | `{id, email, name, emailConfirmedAt, createdAt}` |
| GET | `/api/v1/me/licenses` | Lisans listesi + her birinin aktivasyonları |
| POST | `/api/v1/me/password` | `{currentPassword, newPassword}` → `204` |
| POST | `/api/v1/licenses/validate` | `{licenseKey, hardwareFingerprint}` → status |
| POST | `/api/v1/licenses/activate` | `{licenseKey, hardwareFingerprint, machineName?}` |
| POST | `/api/v1/licenses/deactivate` | `{licenseKey, hardwareFingerprint}` |
| POST | `/api/v1/licenses/heartbeat` | `{licenseKey, hardwareFingerprint}` (LastSeenAt günceller) |

### 4.3 Admin JWT (Bearer-Admin scheme, 1 saat) — 12 endpoint

| Method | Path | Açıklama |
|---|---|---|
| POST | `/api/v1/admin/auth/login` | `{username, password}` → admin token |
| GET | `/api/v1/admin/customers` | Liste (sayfalı) |
| GET | `/api/v1/admin/customers/{id}` | Detay + lisansları |
| POST | `/api/v1/admin/customers` | `{email, name, initialPassword?, autoConfirm?}` (test seed) |
| POST | `/api/v1/admin/customers/{id}/confirm-email` | Manuel confirm (support) |
| POST | `/api/v1/admin/licenses` | `{customerEmail, skuCode, durationDaysOverride?, slotsOverride?}` |
| GET | `/api/v1/admin/licenses/{key}` | Detay + aktivasyonlar |
| POST | `/api/v1/admin/licenses/{key}/revoke` | `{reason}` |
| POST | `/api/v1/admin/licenses/{key}/extend` | `{additionalDays}` |
| GET | `/api/v1/admin/licenses` | `?customer={email}&status={...}` filter |
| DELETE | `/api/v1/admin/activations/{id}` | Force-deactivate |
| GET | `/api/v1/admin/skus` | SKU listesi |

**Toplam: 23 endpoint.**

### 4.4 Validate response detayı

```json
{
  "status": "active",
  "expiresAt": "2027-04-28T00:00:00+00:00",
  "remainingDays": 365,
  "sku": "STD",
  "slotInfo": {
    "used": 1,
    "total": 1,
    "thisDeviceActive": true
  }
}
```

`status` enum: `active` | `expired` | `revoked` | `not-activated` | `slot-mismatch`.

---

## 5. Auth & Güvenlik

### 5.1 İki JWT scheme

| Scheme | Audience | Issuer | Süre | Claims |
|---|---|---|---|---|
| `Bearer-Customer` | `livedeck-customer` | `livedeck-license-server` | 7g | `sub` (customer Guid), `email` |
| `Bearer-Admin` | `livedeck-admin` | aynı | 1s | `sub` (admin Guid), `username` |

HS256 simetrik, secret `appsettings.json:Jwt:SecretKey` (env override). 256-bit random.

### 5.2 Password hashing

Argon2id parametreleri (OWASP 2024):
- Memory: 64 MB
- Iterations: 4
- Parallelism: 2
- Salt: 16 byte random
- Output: 32 byte

Format: `$argon2id$v=19$m=65536,t=4,p=2${salt-base64}${hash-base64}` — kendi serialization (paket yardımcı verir).

### 5.3 Email enumeration koruması

- `/auth/login` — email yoksa 401 (404 yerine), aynı timing
- `/auth/resend-confirmation` — her zaman 202, kullanıcı yoksa email gitmez
- `/auth/register` — email zaten varsa 409 *değil*, 202 (sessiz silent success); kullanıcının dış gözlemden hesap varlığını anlama yolu yok

### 5.4 Rate limiting (ASP.NET Core native)

Per IP:
- `/auth/login` — 5 deneme / dakika
- `/auth/register` — 3 / dakika
- `/auth/resend-confirmation` — 3 / dakika
- `/auth/confirm-email/{token}` — 10 / dakika
- Tüm diğer endpoints — global 100 / dakika

`429 Too Many Requests` yanıtı standart RFC 6585.

### 5.5 HTTPS

Production'da HSTS + redirect HTTP→HTTPS (Program.cs). Development'ta HTTPS dev cert.

### 5.6 CORS

Customer JWT endpoint'leri için CORS open (client = WPF app, browser değil — `Access-Control-Allow-Origin: *` güvenli). Admin endpoint'leri için web UI domain'ine restrict (4d phase'inde sıkılaştırılır; 4a için all-allow).

---

## 6. Email Altyapısı

### 6.1 IEmailSender abstraction

```csharp
public interface IEmailSender
{
    Task SendAsync(string toEmail, string toName, string subject, string htmlBody, string plainBody);
}
```

### 6.2 İmplementasyonlar

**SmtpEmailSender** (production):
- MailKit + `SmtpClient`
- Config: `Smtp:Host`, `:Port`, `:UseSsl`, `:Username`, `:Password`, `:FromAddress`, `:FromName`
- Connection retry: 1, fail-fast (background queue YAGNI)

**DiskEmailSender** (development):
- `Smtp:DiskOutputDirectory` (örn. `./tmp/emails/`) altına `.eml` dosyası yazar
- RFC 822 formatı, manuel inceleme için
- Test'lerde `TestEmailSender` (in-memory list capture) kullanılır

**Selection:** `appsettings.json:Email:Provider` — `"smtp"` veya `"disk"`. DI registration ona göre.

### 6.3 Email template'leri

Türkçe, basit HTML + plaintext fallback. Konular:

```csharp
public static class EmailTemplates
{
    public static (string Subject, string Html, string Plain) ConfirmEmail(
        string customerName, string confirmUrl)
    {
        var subject = "LiveDeck — Email adresinizi doğrulayın";
        var plain = $@"Merhaba {customerName},

LiveDeck hesabını doğrulamak için aşağıdaki bağlantıya tıkla:
{confirmUrl}

Bu link 24 saat geçerli.

Sen yapmadıysan bu mesajı görmezden gel.
— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>LiveDeck hesabını doğrulamak için <a href=""{confirmUrl}"">tıkla</a>.</p>
<p>Bu link 24 saat geçerli.</p>
<p style=""color:#888"">Sen yapmadıysan bu mesajı görmezden gel.</p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, html, plain);
    }
}
```

### 6.4 Confirmation URL inşası

`{baseUrl}/api/v1/auth/confirm-email/{token}` — `baseUrl` `appsettings.json:App:PublicBaseUrl`. Click ile direct API call (basit), JSON yanıt; gerçek frontend gelince (4d veya Phase 5) HTML success/error sayfasına redirect edilebilir.

---

## 7. Hata Yönetimi

| Senaryo | Davranış |
|---|---|
| Register — email zaten var | 202 sessiz (enumeration koruması). Yeni email gönderilmez. |
| Register — şifre çok kısa (< 8 char) | 400 ProblemDetails `password-too-short` |
| Login — email yok / şifre yanlış | 401 (aynı message, timing-safe compare) |
| Login — email confirmed değil | 403 `email-not-confirmed` |
| Login — rate limit | 429 |
| Confirm-email — token yok / kullanılmış / 24s geçmiş | 400 `token-invalid` |
| Validate — JWT customer'ın email'i lisans sahibi değil | 403 `not-license-owner` |
| Activate — slot dolu | 409 `slot-full` |
| Activate — license expired | 409 `license-expired` |
| Activate — license revoked | 409 `license-revoked` |
| Heartbeat — activation yok | 404 `not-activated` (client retry → activate çağırır) |
| Admin issue — customer email yok | 400 `customer-not-found` |
| DB connection lost | 500 (default exception handler), Serilog'a yazılır |
| SMTP send fail | Email confirmation → 202 yanıtla beraber arka planda log warning; kullanıcı resend isteyebilir |

Tüm hata yanıtları RFC 7807 ProblemDetails formatında: `{type, title, status, detail, traceId}`.

---

## 8. Test Stratejisi

### 8.1 Test piramidi

- **Integration tests (ana yöntem)**: `WebApplicationFactory<Program>` + EF Core InMemory veya LocalDB. Her endpoint için happy path + 2-3 error case.
- **Service unit tests**: pure logic — `PasswordHasher`, `LicenseValidator` (status hesabı), `ActivationManager` (slot count), `LicenseIssuer` (key format + duration math).
- **Fake `IEmailSender`**: `TestEmailSender` in-memory liste, register/resend test'leri "1 email gönderildi, içeriği token içeriyor" assert eder.

### 8.2 Hedef test sayısı

- Auth endpoint testleri: 12 (4 endpoint × 3 case)
- Licenses (customer): 12 (4 endpoint × 3 case)
- Licenses (admin): 18 (6 endpoint × 3 case)
- Customers (admin): 6
- Activations (admin): 3
- Skus: 2
- Service unit: ~10 (PasswordHasher 3, Validator 4, ActivationManager 3, Issuer 3)
- **Toplam: ~63 test** (mevcut 117 + ~63 = ~180)

### 8.3 ApiFactory pattern

```csharp
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // EF Core: replace SQL Server with InMemory
            services.RemoveAll<DbContextOptions<LicenseDbContext>>();
            services.AddDbContext<LicenseDbContext>(opt =>
                opt.UseInMemoryDatabase(Guid.NewGuid().ToString()));

            // IEmailSender: replace with TestEmailSender
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<TestEmailSender>();
            services.AddSingleton<IEmailSender>(sp => sp.GetRequiredService<TestEmailSender>());

            // Seed admin user + skus
            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            // Seed code...
        });
    }
}
```

---

## 9. Configuration

`appsettings.json`:

```json
{
  "ConnectionStrings": {
    "LicenseDb": "Server=(localdb)\\mssqllocaldb;Database=LiveDeckLicense;Trusted_Connection=true;"
  },
  "Jwt": {
    "SecretKey": "REPLACE-WITH-256-BIT-RANDOM-SECRET-IN-PRODUCTION",
    "Issuer": "livedeck-license-server"
  },
  "Email": {
    "Provider": "smtp"
  },
  "Smtp": {
    "Host": "smtp.example.com",
    "Port": 587,
    "UseSsl": true,
    "Username": "",
    "Password": "",
    "FromAddress": "noreply@livedeck.app",
    "FromName": "LiveDeck"
  },
  "App": {
    "PublicBaseUrl": "https://license.livedeck.app"
  },
  "Admin": {
    "InitialUsername": "admin",
    "InitialPasswordHash": ""
  },
  "Logging": { "LogLevel": { "Default": "Information" } },
  "AllowedHosts": "*"
}
```

`appsettings.Development.json` override:

```json
{
  "Email": { "Provider": "disk" },
  "Smtp": { "DiskOutputDirectory": "./tmp/emails" },
  "App": { "PublicBaseUrl": "https://localhost:5001" }
}
```

---

## 10. Docker (deploy 4a kapsam dışı)

`Dockerfile` (multi-stage, .NET 10 SDK + ASP.NET runtime). `docker-compose.yml` (Postgres yerine SQL Server image — `mcr.microsoft.com/mssql/server:2022-latest`). Build edilebilir ama deploy edilmez bu fazda.

---

## 11. YAGNI (yapılmayanlar)

- Refresh token (login zaten 7g)
- Password reset (4e'ye)
- Multi-admin user management (single admin)
- Audit log (4d'ye)
- Multi-tier auth (sadece STD/PRO SKU)
- 2FA / MFA
- API rate limiting per-customer (sadece IP-based)
- Stripe/PayTR webhook (Phase 5'e)
- Webhook'lar (lisans değişimi → external bildirim)
- API versioning (v2 yok)
- GDPR data export endpoint
- Audit trail (kim ne zaman lisans değiştirdi)
- IP allowlist for admin
- Backup/restore script

---

## 12. Phase 4a Sonrası Açık Bırakılanlar

- **Phase 4b:** `LiveDeck.Licensing` client modülü, DPAPI auth.dat storage, hardware fingerprint, login dialog, `LicenseService` DI'a entegre, lisans expired/missing → read-only mode gate
- **Phase 4c:** Trial mode (Instagram-only, 14g, anti-reset)
- **Phase 4d:** Admin web UI (Razor Pages veya Blazor) — kullanıcı listesi, lisans manuel ihraç, audit log
- **Phase 4e:** Email otomasyonu — yenileme uyarıları (14/7/3/0 gün), expired sonrası, Hangfire/HostedService, password reset email akışı
- **Phase 5:** Stripe/PayTR webhook → otomatik lisans ihraç, sat-hazır cila

---

## 13. Kabul Kriterleri

- ✅ Build temiz, 0 warning, 0 error
- ✅ ~180 test pass (117 baseline + ~63 yeni)
- ✅ 23 endpoint Swagger UI'da görünür ve schema doğru
- ✅ Self-signup → email confirmation → login → lisans aktivasyon akışı end-to-end manuel test (DiskEmailSender ile dev'de)
- ✅ Slot enforcement: 1-slot SKU'da 2. cihaz aktivasyonu 409 döner; deactivate → tekrar aktivate edilebilir
- ✅ Rate limiting: 6. login denemesi 1 dakika içinde 429 alır
- ✅ Admin login + license issue + customer detail flow Postman/curl ile testlenebilir
- ✅ Docker build başarılı (`docker build -t livedeck-license-server .`)
- ✅ Mevcut LiveDeck.* projeleri etkilenmemiş (117 mevcut test hâlâ pass, 0 değişiklik)
