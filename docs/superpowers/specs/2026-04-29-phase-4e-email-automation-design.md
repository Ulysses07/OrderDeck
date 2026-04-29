# Faz 4e — Email Otomasyonu (Tasarım)

**Hedef:** `LiveDeck.LicenseServer` projesine email otomasyonu ekle: Hangfire ile günlük yenileme reminder'ları (14/7/3/0 gün önce + expired+1g), password reset flow (forgot password), admin action notifications (license issue/revoke/extend → customer email), HMAC-signed unsubscribe mekanizması. EmailLog tablosu ile send dedup. Phase 4a `IEmailSender` + `EmailTemplates` reuse (genişletilir).

**Kapsam:** `LiveDeck.LicenseServer` projesine: Hangfire entegrasyonu + 5 recurring job + 7 yeni email template + 3 yeni service (EmailSendCoordinator, PasswordResetService, AdminActionEmailService) + 2 yeni controller endpoint + 2 yeni Razor sayfa (Pages/Public/) + EF migration 006. Phase 4a/4b/4c/4d code dokunulmaz (sadece Phase 4d Razor handlers + 4a Controllers admin action notify çağrısı eklenir).

**Pre-Faz-4e state:** Phase 4d HEAD `497a3f5` (master). 322/322 test (128 LiveDeck + 104 Licensing + 90 LicenseServer). Build 0/0.

---

## 1. Bağlam

**Sorun:** Phase 4a-4d ile lisanslama + admin paneli + trial mod hazır. Ama satışa hazır olabilmek için kullanıcı ile temas otomasyonu eksik:
- Customer lisansının yakında bitiyor — "yenile" hatırlatması
- Customer şifresini unuttu — admin müdahalesi gerekmemeli (self-service reset)
- Admin lisans ihraç/iptal/uzatınca customer haberdar olsun
- E-posta abonelik kontrolü (unsubscribe)

**4e kapsamı (server-side automation):** Hangfire-based günlük scheduler, 7 yeni email template, password reset endpoint'leri + Razor sayfa, admin action triggers, unsubscribe Razor sayfa.

**4e kapsamında DEĞİL:**
- Open/click tracking (pixel/redirect)
- Email A/B testing
- Çoklu dil (sadece TR)
- Bulk send / batching ötesi (Hangfire single jobs yeter)
- AccountDialog "yeniden abone ol" (Phase 4b client değişikliği — Phase 4f)
- Email preview admin UI
- SKU bazlı template (sadece generic)
- DKIM/SPF/SES bounce handling (deploy-time concern)
- Unsubscribe audit log (customer self-service, admin tracking yok)

---

## 2. Mimari

### 2.1 Solution etkisi

Yeni proje yok. Mevcut `LiveDeck.LicenseServer` projesine eklemeler + 1 EF migration. Test projesi 1 ek paket (Hangfire.MemoryStorage testlerde).

```
LiveDeck.LicenseServer/
├─ Domain/
│  ├─ EmailLog.cs                          (YENİ)
│  ├─ PasswordResetToken.cs                (YENİ)
│  └─ Customer.cs                          (MODIFIED — +Unsubscribed)
├─ Data/
│  ├─ LicenseDbContext.cs                  (MODIFIED — 2 DbSet + Customer config)
│  └─ Migrations/006_AddEmailInfra.cs      (YENİ — auto-generated)
├─ Services/
│  ├─ Email/
│  │  ├─ EmailTemplates.cs                 (MODIFIED — +7 template metot + footer helper)
│  │  ├─ EmailSendCoordinator.cs           (YENİ)
│  │  ├─ ReminderJobs.cs                   (YENİ — 5 method)
│  │  ├─ AdminActionEmailService.cs        (YENİ)
│  │  ├─ EmailReminderOptions.cs           (YENİ)
│  │  ├─ UnsubscribeTokenSigner.cs         (YENİ)
│  │  └─ HangfireDashboardAuthFilter.cs    (YENİ)
│  └─ Auth/
│     └─ PasswordResetService.cs           (YENİ)
├─ Controllers/Auth/
│  ├─ AuthController.cs                    (MODIFIED — +2 password-reset endpoint)
│  └─ UnsubscribeController.cs             (YENİ — alternatif API endpoint, Razor sayfa primary)
├─ Pages/Public/                           (YENİ klasör — anonymous Razor sayfalar)
│  ├─ _ViewImports.cshtml
│  ├─ PasswordReset.cshtml + .cs
│  └─ Unsubscribe.cshtml + .cs
├─ Pages/Admin/Licenses/
│  ├─ Issue.cshtml.cs                      (MODIFIED — admin notify çağrısı)
│  └─ Detail.cshtml.cs                     (MODIFIED — Revoke/Extend handlers admin notify)
├─ Controllers/Licenses/
│  └─ AdminLicensesController.cs           (MODIFIED — Issue/Revoke/Extend admin notify)
├─ LiveDeck.LicenseServer.csproj           (MODIFIED — +Hangfire.AspNetCore + Hangfire.SqlServer)
└─ Program.cs                              (MODIFIED — Hangfire setup + DI + dashboard middleware)

LiveDeck.LicenseServer.Tests/
├─ LiveDeck.LicenseServer.Tests.csproj     (MODIFIED — +Hangfire.MemoryStorage)
├─ TestHelpers/
│  └─ ApiFactory.cs                        (MODIFIED — Hangfire MemoryStorage override)
└─ ... (yeni test klasörleri Section 7'de)
```

### 2.2 Stack

| Bileşen | Seçim | Gerekçe |
|---|---|---|
| Background job | Hangfire.AspNetCore + Hangfire.SqlServer 1.8.14 | Enterprise-grade, persistent, dashboard, retry, distributed |
| Email send | Mevcut Phase 4a `IEmailSender` (MailKit + DiskEmailSender + SmtpEmailSender) | Reuse, dokunulmaz |
| Email template | Mevcut Phase 4a `EmailTemplates` static class genişletilir | String interpolation, no new dep |
| Email log dedup | Yeni `EmailLog` SQL tablosu | EF Core entity, composite index |
| Password reset token | Yeni `PasswordResetToken` SQL tablosu | EmailConfirmationToken pattern |
| Unsubscribe token | HMAC-SHA256 signed (Jwt:SecretKey reuse) | Stateless, no DB lookup |
| Razor anonymous pages | Pages/Public/ klasörü | AuthorizeFolder("/Admin") kapsamı dışında |
| Hangfire test | Hangfire.MemoryStorage 1.8.x | InMemory fallback, ApiFactory override |

### 2.3 Yeni endpoint'ler ve sayfalar

**REST endpoints (AuthController):**
- `POST /api/v1/auth/password-reset-request` (anonymous, rate-limit auth-register 3/min)
- `POST /api/v1/auth/password-reset` (anonymous, rate-limit auth-register)

**Razor pages (Pages/Public/, anonymous):**
- `/password-reset` GET + POST (token query param, form)
- `/unsubscribe` GET + POST (token query param, onay formu)

**Hangfire dashboard:**
- `/hangfire` (AdminCookie zorunlu via `IDashboardAuthorizationFilter`)

### 2.4 Auth scheme overlap'i

Phase 4d'den iki scheme:
- `Bearer-Customer` — REST API customer
- `Bearer-Admin` — REST API admin
- `AdminCookie` — Razor admin pages

Yeni eklenenler hepsi anonymous (password reset + unsubscribe customer self-service). Hangfire dashboard AdminCookie kullanır.

### 2.5 Razor Pages convention'ı

Phase 4d'de `AuthorizeFolder("/Admin", "AdminOnly")` Pages/Admin/* için aktif. Pages/Public/ klasörü AnonymousByDefault — `AllowAnonymousToFolder("/Public")` Program.cs'e eklenir. AntiForgery POST'larda zorunlu.

---

## 3. Email Templates ve Send Pipeline

### 3.1 EmailTemplates.cs genişlemesi

Phase 4a'da `ConfirmEmail` var. 7 yeni metot eklenir (tümü `(Subject, Html, Plain)` tuple döner, Türkçe içerik):

```csharp
public static (string Subject, string Html, string Plain) Renewal14d(string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl);
public static (string Subject, string Html, string Plain) Renewal7d(...);
public static (string Subject, string Html, string Plain) Renewal3d(...);
public static (string Subject, string Html, string Plain) Renewal0d(...);
public static (string Subject, string Html, string Plain) ExpiredAfter1d(string customerName, string licenseKey, string portalUrl);
public static (string Subject, string Html, string Plain) PasswordReset(string customerName, string resetUrl);
public static (string Subject, string Html, string Plain) LicenseIssued(string customerName, string licenseKey, string skuCode, DateTimeOffset expiresAt);
public static (string Subject, string Html, string Plain) LicenseRevoked(string customerName, string licenseKey, string reason);
public static (string Subject, string Html, string Plain) LicenseExtended(string customerName, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays);
```

**Subject örnekleri:**
- `Renewal14d` → "LiveDeck — Lisansınız 14 gün içinde sona eriyor"
- `ExpiredAfter1d` → "LiveDeck — Lisansınızın süresi doldu"
- `PasswordReset` → "LiveDeck — Şifre sıfırlama bağlantınız"
- `LicenseIssued` → "LiveDeck — Yeni lisansınız hazır"

### 3.2 Unsubscribe footer

Sadece marketing + admin-action emails alır. Footer markup:

```html
<hr><p style="color:#888;font-size:12px;margin-top:24px">
Bu e-postayı LiveDeck hesabınızla ilgili olduğu için aldınız.
<a href="{unsubscribeUrl}">E-posta bildirimlerini durdur</a>
</p>
```

`EmailTemplates` her template metodu, footer parameter alır ya da builder helper kullanır:

```csharp
private static string AppendUnsubscribeFooter(string html, string unsubscribeUrl) =>
    html + "<hr><p style=\"color:#888;font-size:12px;margin-top:24px\">..." + unsubscribeUrl + "...</p>";
```

`EmailSendCoordinator` template builder'a unsubscribe URL geçirir; coordinator karar verir footer eklenecek mi.

**Footer scope:**
| Template | requiresUnsubscribeRespect | footer included |
|---|---|---|
| ConfirmEmail (Phase 4a) | false | hayır |
| PasswordReset | false | hayır |
| Renewal 14/7/3/0d | true | evet |
| ExpiredAfter1d | true | evet |
| LicenseIssued/Revoked/Extended | true | evet |

### 3.3 EmailSendCoordinator

```csharp
public sealed class EmailSendCoordinator
{
    public EmailSendCoordinator(
        LicenseDbContext db,
        IEmailSender sender,
        UnsubscribeTokenSigner unsubSigner,
        IOptions<LicensingOptions> opts,
        ILogger<EmailSendCoordinator> log);

    public async Task<bool> TrySendAsync(
        Guid customerId,
        string templateKey,
        string? contextKey,
        Func<Customer, string?, (string Subject, string Html, string Plain)> templateBuilder,
        bool requiresUnsubscribeRespect,
        CancellationToken ct = default);
}
```

`templateBuilder`'ın 2. parametresi: `unsubscribeUrl` (null ise footer eklenmesin). Coordinator URL'i HMAC ile imzalayıp builder'a verir.

**Internal logic:**
1. Customer yükle (`db.Customers.FindAsync(customerId)`); yoksa false dön + warn log
2. `requiresUnsubscribeRespect && customer.Unsubscribed` → log "skip-unsubscribed", false dön
3. EmailLog dedup: `WHERE CustomerId=X AND TemplateKey=Y AND ContextKey=Z AND Error IS NULL` → satır varsa false dön + log "dedup-skip"
4. Eğer `requiresUnsubscribeRespect`:
   - `unsubscribeToken = unsubSigner.Sign(customerId, DateTimeOffset.UtcNow)`
   - `unsubscribeUrl = "{App.PublicBaseUrl}/unsubscribe?token={url-encoded-token}"`
5. `(subject, html, plain) = templateBuilder(customer, unsubscribeUrl)`
6. `try { await _sender.SendAsync(customer.Email, customer.Name, subject, html, plain, ct); }`
   `catch (Exception ex) { log = error message; success = false; }`
7. EmailLog row insert: `{Id, CustomerId, TemplateKey, ContextKey, SentAt = now, Error = errorMsg ?? null}`
8. SaveChangesAsync
9. return success

### 3.4 EmailLog entity

```csharp
public sealed class EmailLog
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string TemplateKey { get; set; } = "";   // max 64
    public string? ContextKey { get; set; }          // max 64
    public DateTimeOffset SentAt { get; set; }
    public string? Error { get; set; }               // null = success, string = exception.Message
}
```

**Indexes:**
- `(CustomerId, TemplateKey, ContextKey)` — dedup query
- `(SentAt DESC)` — recent logs

### 3.5 TemplateKey / ContextKey karar matrisi

| Email | TemplateKey | ContextKey |
|---|---|---|
| ConfirmEmail (Phase 4a — TempaltKey eklenmez, mevcut akış) | — | — |
| Renewal 14d | `renewal-14d` | licenseKey |
| Renewal 7d | `renewal-7d` | licenseKey |
| Renewal 3d | `renewal-3d` | licenseKey |
| Renewal 0d | `renewal-0d` | licenseKey |
| ExpiredAfter1d | `expired-1d` | licenseKey |
| PasswordReset | `password-reset` | tokenId (Guid string) |
| LicenseIssued | `license-issued` | licenseKey |
| LicenseRevoked | `license-revoked` | licenseKey |
| LicenseExtended | `license-extended` | licenseKey + ":" + ext-action-id |

LicenseExtended ContextKey unique-per-extend (her extend ayrı email): kararı admin handler verir, `licenseKey + ":extend:" + Guid.NewGuid().ToString("N")` veya audit log entry id'si ile birleştirilir.

---

## 4. Reminder Scheduler (Hangfire)

### 4.1 Hangfire setup (Program.cs)

```csharp
builder.Services.AddHangfire(cfg => cfg
    .UseSimpleAssemblyNameTypeSerializer()
    .UseRecommendedSerializerSettings()
    .UseSqlServerStorage(builder.Configuration.GetConnectionString("LicenseDb"),
        new SqlServerStorageOptions
        {
            CommandBatchMaxTimeout = TimeSpan.FromMinutes(5),
            SlidingInvisibilityTimeout = TimeSpan.FromMinutes(5),
            QueuePollInterval = TimeSpan.Zero,
            UseRecommendedIsolationLevel = true,
            DisableGlobalLocks = true
        }));

builder.Services.AddHangfireServer();   // worker server in-process
```

**Hangfire DB tabloları auto-created:** `HangFire.Job`, `HangFire.JobParameter`, `HangFire.JobQueue`, `HangFire.Schema`, `HangFire.Server`, `HangFire.Set`, `HangFire.State`, `HangFire.Hash`, `HangFire.List`, `HangFire.Counter`, `HangFire.AggregatedCounter`, `HangFire.Lock` (~12 tablo, `HangFire` schema). LicenseDbContext'i etkilemez.

### 4.2 Dashboard auth filter

`HangfireDashboardAuthFilter`:
```csharp
public sealed class HangfireDashboardAuthFilter : Hangfire.Dashboard.IDashboardAuthorizationFilter
{
    public bool Authorize(Hangfire.Dashboard.DashboardContext context)
    {
        var http = context.GetHttpContext();
        // AdminCookie scheme ile auth edilmiş mi?
        var result = http.AuthenticateAsync("AdminCookie").GetAwaiter().GetResult();
        return result.Succeeded;
    }
}
```

Pipeline:
```csharp
app.UseHangfireDashboard("/hangfire", new DashboardOptions
{
    Authorization = new[] { new HangfireDashboardAuthFilter() }
});
```

### 4.3 Recurring jobs registration

Program.cs'de `app.Run()` öncesi:
```csharp
using (var scope = app.Services.CreateScope())
{
    var manager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
    var cron = builder.Configuration["EmailReminder:DailyJobCron"] ?? "0 9 * * *";
    manager.AddOrUpdate<ReminderJobs>("renewal-14d", j => j.SendRenewal14dAsync(CancellationToken.None), cron);
    manager.AddOrUpdate<ReminderJobs>("renewal-7d",  j => j.SendRenewal7dAsync(CancellationToken.None), cron);
    manager.AddOrUpdate<ReminderJobs>("renewal-3d",  j => j.SendRenewal3dAsync(CancellationToken.None), cron);
    manager.AddOrUpdate<ReminderJobs>("renewal-0d",  j => j.SendRenewal0dAsync(CancellationToken.None), cron);
    manager.AddOrUpdate<ReminderJobs>("expired-1d",  j => j.SendExpired1dAsync(CancellationToken.None), cron);
}
```

Test ortamında bu blok skip — `app.Environment.IsEnvironment("Testing")` check.

### 4.4 ReminderJobs class

```csharp
public sealed class ReminderJobs
{
    private readonly LicenseDbContext _db;
    private readonly EmailSendCoordinator _coordinator;
    private readonly LicensingOptions _opts;
    private readonly ILogger<ReminderJobs> _log;

    public ReminderJobs(LicenseDbContext db, EmailSendCoordinator coord, IOptions<LicensingOptions> opts, ILogger<ReminderJobs> log)
    {
        _db = db; _coordinator = coord; _opts = opts.Value; _log = log;
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal14dAsync(CancellationToken ct) => SendRenewalAsync(daysBeforeExpiry: 14, templateKey: "renewal-14d", builder: EmailTemplates.Renewal14d, ct);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal7dAsync(CancellationToken ct) => SendRenewalAsync(7, "renewal-7d", EmailTemplates.Renewal7d, ct);

    // ... 3d, 0d benzer

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task SendExpired1dAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddDays(-1.5), now.AddDays(-0.5));
        var candidates = await _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.RevokedAt == null
                     && l.ExpiresAt >= from && l.ExpiresAt < to
                     && l.Customer.EmailConfirmedAt != null)
            .ToListAsync(ct);

        foreach (var license in candidates)
        {
            await _coordinator.TrySendAsync(
                customerId: license.CustomerId,
                templateKey: "expired-1d",
                contextKey: license.LicenseKey,
                templateBuilder: (customer, unsubUrl) => EmailTemplates.ExpiredAfter1d(customer.Name, license.LicenseKey, _opts.PortalUrl, unsubUrl),
                requiresUnsubscribeRespect: true,
                ct);
        }
    }

    private async Task SendRenewalAsync(int daysBeforeExpiry, string templateKey,
        Func<string, string, DateTimeOffset, string, string?, (string,string,string)> builder,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddDays(daysBeforeExpiry - 0.5), now.AddDays(daysBeforeExpiry + 0.5));
        var candidates = await _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.RevokedAt == null
                     && l.ExpiresAt >= from && l.ExpiresAt < to
                     && l.Customer.EmailConfirmedAt != null)
            .ToListAsync(ct);

        foreach (var license in candidates)
        {
            await _coordinator.TrySendAsync(
                customerId: license.CustomerId,
                templateKey: templateKey,
                contextKey: license.LicenseKey,
                templateBuilder: (customer, unsubUrl) => builder(customer.Name, license.LicenseKey, license.ExpiresAt, _opts.PortalUrl, unsubUrl),
                requiresUnsubscribeRespect: true,
                ct);
        }
    }
}
```

### 4.5 Window logic & idempotency

- **Window:** ±12 saat (target ± 0.5 day). 09:00'da çalışan job, ExpiresAt'i bugün 09:00 ± 12h olan lisansları yakalar.
- **Idempotency:** EmailLog dedup. Bir lisans bir templateKey için ömrü boyunca 1 defa email alır.
- **Customer 2 lisansı varsa:** Her ikisi için ayrı reminder gider (contextKey farklı).
- **Window kayması:** Job 1 dakika geç çalışırsa veya 1 saat erken tetiklenirse, 24h içinde dedup engelleyeceği için sorun yok.

---

## 5. Password Reset Flow

### 5.1 PasswordResetToken entity

```csharp
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }                 // PK + email URL token
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }  // single-use
}
```

DbContext config:
```csharp
mb.Entity<PasswordResetToken>(b =>
{
    b.HasKey(t => t.Id);
    b.HasOne(t => t.Customer).WithMany()
        .HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
    b.HasIndex(t => new { t.CustomerId, t.UsedAt });
});
```

### 5.2 PasswordResetService

```csharp
public sealed class PasswordResetService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RequestThrottle = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailSendCoordinator _coordinator;
    private readonly LicensingOptions _opts;
    private readonly ILogger<PasswordResetService> _log;

    public async Task RequestResetAsync(string email, CancellationToken ct);
    public async Task<PasswordResetResult> CompleteResetAsync(Guid token, string newPassword, CancellationToken ct);
}

public enum PasswordResetResult { Success, TokenInvalid, PasswordTooShort }
```

**`RequestResetAsync`:**
1. `customer = _db.Customers.FirstOrDefaultAsync(c => c.Email == email)`
2. `if (customer is null) return;` — silent (enumeration koruma)
3. `if (customer.EmailConfirmedAt is null) return;` — confirm öncesi reset göndermez
4. **Throttle:** `existingFresh = _db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id && t.UsedAt == null && t.CreatedAt > now - 15min).OrderByDescending(t => t.CreatedAt).FirstOrDefault()` — varsa onu reuse et
5. Yoksa: yeni `PasswordResetToken { Id = NewGuid(), CustomerId = ..., CreatedAt = now }`, save
6. URL: `{App.PublicBaseUrl}/password-reset?token={tokenId}`
7. `coordinator.TrySendAsync(customerId, "password-reset", tokenId.ToString(), builder=PasswordReset, requiresUnsubscribeRespect: false)`

**`CompleteResetAsync`:**
1. `token = _db.PasswordResetTokens.Include(t => t.Customer).FirstOrDefaultAsync(t => t.Id == tokenId)`
2. `if (token is null) return TokenInvalid;`
3. `if (token.UsedAt is not null) return TokenInvalid;` (Used → Invalid, enumeration safe)
4. `if (now - token.CreatedAt > TokenLifetime) return TokenInvalid;` (Expired → Invalid)
5. `if (newPassword.Length < 8) return PasswordTooShort;`
6. `token.Customer.PasswordHash = _hasher.Hash(newPassword)`
7. `token.UsedAt = now` (single-use)
8. `await _db.SaveChangesAsync(ct)`
9. return Success

### 5.3 AuthController endpoints

```csharp
public sealed record PasswordResetRequest(string Email);

[HttpPost("password-reset-request")]
[EnableRateLimiting("auth-register")]
public async Task<IActionResult> PasswordResetRequest([FromBody] PasswordResetRequest req, CancellationToken ct)
{
    if (string.IsNullOrWhiteSpace(req.Email)) return StatusCode(202);
    await _resetService.RequestResetAsync(req.Email, ct);
    return StatusCode(202);
}

public sealed record PasswordResetCompleteRequest(Guid Token, string NewPassword);

[HttpPost("password-reset")]
[EnableRateLimiting("auth-register")]
public async Task<IActionResult> PasswordResetComplete([FromBody] PasswordResetCompleteRequest req, CancellationToken ct)
{
    var result = await _resetService.CompleteResetAsync(req.Token, req.NewPassword, ct);
    return result switch
    {
        PasswordResetResult.Success => NoContent(),
        PasswordResetResult.PasswordTooShort => Problem(title: "password-too-short", detail: "En az 8 karakter olmalı.", statusCode: 400),
        _ => Problem(title: "token-invalid", statusCode: 400)
    };
}
```

### 5.4 Pages/Public/PasswordReset Razor sayfa

`@page "/password-reset"` (anonymous). Query string `?token={guid}`.

GET handler: token query'i alıp `Input.Token = token` set eder, formu render eder.
POST handler: form submit `{Token, NewPassword, ConfirmPassword}` → `PasswordResetService.CompleteResetAsync` çağrılır → success ekranı veya error message.

Bootstrap form: token hidden, şifre input, onay input. Server-side validation `NewPassword == ConfirmPassword` + length ≥ 8.

---

## 6. Admin Action Notifications

### 6.1 AdminActionEmailService

```csharp
public sealed class AdminActionEmailService
{
    private readonly EmailSendCoordinator _coordinator;
    private readonly LicensingOptions _opts;

    public async Task NotifyLicenseIssuedAsync(Guid customerId, string licenseKey, string skuCode, DateTimeOffset expiresAt, CancellationToken ct = default);
    public async Task NotifyLicenseRevokedAsync(Guid customerId, string licenseKey, string reason, CancellationToken ct = default);
    public async Task NotifyLicenseExtendedAsync(Guid customerId, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays, CancellationToken ct = default);
}
```

Her metot:
1. `coordinator.TrySendAsync(...)` çağırır
2. `requiresUnsubscribeRespect: true`
3. Template builder ilgili `EmailTemplates.LicenseIssued/LicenseRevoked/LicenseExtended` çağırır
4. ContextKey:
   - Issued: `licenseKey`
   - Revoked: `licenseKey + ":revoke"`
   - Extended: `licenseKey + ":extend:" + Guid.NewGuid().ToString("N")` (her extend yeni email)

### 6.2 Trigger noktaları (mevcut handler'lar genişletilir)

**Phase 4d Razor handlers:**
| Dosya | Handler | Yeni satır |
|---|---|---|
| `Pages/Admin/Licenses/Issue.cshtml.cs` | `OnPostAsync` | success path'te `await _adminEmail.NotifyLicenseIssuedAsync(...)` audit'ten sonra |
| `Pages/Admin/Licenses/Detail.cshtml.cs` | `OnPostRevokeAsync` | success path'te `NotifyLicenseRevokedAsync(...)` audit'ten sonra |
| `Pages/Admin/Licenses/Detail.cshtml.cs` | `OnPostExtendAsync` | success path'te `NotifyLicenseExtendedAsync(...)` audit'ten sonra |

**Phase 4a REST controllers:**
| Dosya | Action | Yeni satır |
|---|---|---|
| `Controllers/Licenses/AdminLicensesController.cs` | `Issue` | success path'te `await _adminEmail.NotifyLicenseIssuedAsync(...)` |
| `Controllers/Licenses/AdminLicensesController.cs` | `Revoke` | success path'te `NotifyLicenseRevokedAsync(...)` |
| `Controllers/Licenses/AdminLicensesController.cs` | `Extend` | success path'te `NotifyLicenseExtendedAsync(...)` |

Constructor parametresi: her sınıfa `AdminActionEmailService _adminEmail` eklenir (DI scoped).

### 6.3 Sync vs async karar

**Sync** seçildi. Sebepleri:
- Phase 4a `SmtpEmailSender` zaten `try/catch` ile yutar — UI thread blok olmaz
- Test edilebilirlik: testte `IEmailSender` mock anında çağrılır, async dispatch yok
- Hangfire queue overhead'i (job persist + worker pickup) bu size'da gereksiz
- Failure semantics basit: email başarısız olursa admin sayfası yine "OK" gösterir, EmailLog'da `Error` doludur — manuel inceleme

**Trade-off:** Email send 5+ saniye sürerse admin sayfası yavaş döner. SmtpEmailSender 10s timeout kullanır (Phase 4a). Worst case: admin 10s bekler. Kabul edilebilir; production SMTP genelde <1s.

---

## 7. Unsubscribe Mekanizması

### 7.1 Customer.Unsubscribed flag

Migration 006'da `Customer.Unsubscribed bool NOT NULL DEFAULT 0` eklenir. Default false.

### 7.2 UnsubscribeTokenSigner

```csharp
public sealed class UnsubscribeTokenSigner
{
    public UnsubscribeTokenSigner(IOptions<LicensingOptions> opts);  // Jwt:SecretKey reuse

    public string Sign(Guid customerId, DateTimeOffset issuedAt);
    public bool TryVerify(string token, out Guid customerId, out DateTimeOffset issuedAt);
}
```

**Format:** `{base64url(customerIdBytes)}.{base64url(unixTimeBytes)}.{base64url(hmac-sha256(payload, key))}`

`issuedAt` audit amaçlı; süresiz token (customer abone-iptalini istediği zaman yapabilir).

**`TryVerify`:** Token tampered (HMAC mismatch) veya format hatası → false dön.

### 7.3 Pages/Public/Unsubscribe Razor sayfa

`@page "/unsubscribe"` (anonymous).

**GET:**
1. Query `?token=...` parse edilir
2. `signer.TryVerify(token, out customerId, out issuedAt)` → false ise "Geçersiz bağlantı" sayfası
3. `customer = _db.Customers.FindAsync(customerId)` → null ise "Geçersiz bağlantı"
4. Render: "E-posta bildirimlerini durdur" mesajı + customer email + "Onayla" butonu (POST form)
5. Eğer `customer.Unsubscribed` zaten true: "Aboneliğiniz zaten kapalı" mesajı

**POST:**
1. AntiForgery validate
2. Token re-verify
3. `customer.Unsubscribed = true`; `_db.SaveChangesAsync()`
4. "Aboneliğiniz durduruldu" sayfası

**Idempotent:** İkinci POST `Unsubscribed` zaten true olur, save no-op.

### 7.4 Unsubscribe footer URL üretimi

`EmailSendCoordinator` template builder'a ne zaman footer URL geçer:
- `requiresUnsubscribeRespect: true` ise:
  - `unsubscribeUrl = $"{App.PublicBaseUrl}/unsubscribe?token={signer.Sign(customerId, now)}"`
  - Builder bu URL'i alır, EmailTemplates HTML'sine ekler

### 7.5 Tekrar abone olma

YAGNI. Customer admin'le iletişime geçer (Customer.Unsubscribed = false manuel SQL veya admin UI'a "Yeniden abone" butonu — ileride). Phase 4d AccountDialog "yeniden abone" butonu Phase 4f client değişikliği olur.

---

## 8. Konfigürasyon + DI

### 8.1 Yeni paketler

`LiveDeck.LicenseServer.csproj`:
```xml
<PackageReference Include="Hangfire.AspNetCore" Version="1.8.14" />
<PackageReference Include="Hangfire.SqlServer" Version="1.8.14" />
```

`LiveDeck.LicenseServer.Tests.csproj`:
```xml
<PackageReference Include="Hangfire.MemoryStorage" Version="1.8.0" />
```

### 8.2 LicensingOptions ek

Phase 4a `LicensingOptions` (server-side, mevcut):
```csharp
public sealed class LicensingOptions
{
    // Phase 4a — mevcut
    public string PortalUrl { get; set; } = "https://license.livedeck.app";
    public string PublicBaseUrl { get; set; } = "https://license.livedeck.app";

    // Phase 4e (yeni — appsettings'ten okunabilir)
    public string DailyJobCron { get; set; } = "0 9 * * *";
}
```

Eğer `LicensingOptions` server'da yoksa `EmailReminderOptions` adında ayrı sınıf eklenir.

### 8.3 Program.cs DI registration ekleri

```csharp
// Email automation (Phase 4e)
services.AddScoped<EmailSendCoordinator>();
services.AddScoped<ReminderJobs>();
services.AddScoped<PasswordResetService>();
services.AddScoped<AdminActionEmailService>();
services.AddSingleton<UnsubscribeTokenSigner>();

// Hangfire
services.AddHangfire(cfg => cfg.UseSimpleAssemblyNameTypeSerializer().UseRecommendedSerializerSettings()
    .UseSqlServerStorage(connStr, new SqlServerStorageOptions { ... }));
services.AddHangfireServer();

// Razor Pages — Public klasörü anonymous
services.AddRazorPages(opt =>
{
    opt.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    opt.Conventions.AllowAnonymousToPage("/Admin/Login");
    opt.Conventions.AllowAnonymousToPage("/Admin/Logout");
    opt.Conventions.AllowAnonymousToFolder("/Public");   // YENİ
});
```

---

## 9. Test Stratejisi

### 9.1 Test paketi yapısı

| Test dosyası | Kategori | Test sayısı |
|---|---|---|
| `Domain/EmailLogTests.cs` | Smoke | 1 |
| `Services/UnsubscribeTokenSignerTests.cs` | Unit | 4 |
| `Services/EmailSendCoordinatorTests.cs` | Integration (ApiFactory) | 6 |
| `Services/PasswordResetServiceTests.cs` | Integration | 7 |
| `Services/AdminActionEmailServiceTests.cs` | Integration | 3 |
| `Services/ReminderJobsTests.cs` | Integration | 6 |
| `Controllers/Auth/PasswordResetEndpointsTests.cs` | Integration | 5 |
| `Pages/Public/PasswordResetPageTests.cs` | Integration | 2 |
| `Pages/Public/UnsubscribePageTests.cs` | Integration | 3 |
| `Pages/HangfireDashboardAuthTests.cs` | Integration | 2 |

**Toplam:** ~39 test (~37-40 hedefi).

### 9.2 ApiFactory Hangfire override

```csharp
// ApiFactory.cs — production cooperatively
services.RemoveAll<IGlobalConfiguration>();
services.AddHangfire(cfg => cfg.UseMemoryStorage());
```

Test ortamı `IsEnvironment("Testing")` → SQL Hangfire setup skip + RecurringJob.AddOrUpdate skip.

### 9.3 EmailSendCoordinator test patterns

- Customer not found → return false
- Customer.Unsubscribed=true + requiresUnsubscribeRespect=true → return false
- Customer.Unsubscribed=true + requiresUnsubscribeRespect=false → send anyway (transactional pass-through)
- Existing EmailLog row (Error null) → dedup, return false
- Send success → EmailLog row insert (Error=null), TestEmailSender captures
- Send fail → EmailLog row insert (Error=ex.Message), return false

### 9.4 ReminderJobs window test

Seed:
- License A ExpiresAt = now + 14d (in window) + Customer confirmed
- License B ExpiresAt = now + 13d (out of window) + Customer confirmed
- License C ExpiresAt = now + 14d + Customer NOT confirmed
- License D ExpiresAt = now + 14d + revoked

Run `SendRenewal14dAsync()`:
- Email: only License A's customer
- Run again → dedup → no second email

### 9.5 Hangfire dashboard auth test

- Anonymous GET `/hangfire` → 401 or redirect to login (filter behavior)
- Logged-in admin GET `/hangfire` → 200

### 9.6 Mevcut test regression

- LiveDeck.Tests: 128/128
- LiveDeck.Licensing.Tests: 104/104
- LiveDeck.LicenseServer.Tests: 90/90 baseline → 90 + 39 = ~129

**Phase 4d test güncellemeleri:** AdminLicensesController + Pages/Admin/Licenses handler'ları artık AdminActionEmailService inject ediyor — mevcut `AdminLicensesIssueTests` + `AdminLicensesDetailTests` testleri yeni dependency için TestEmailSender mock kullanır (ApiFactory zaten Phase 4b'den IEmailSender override ediyor). EmailSendCoordinator gerçek olarak çalışır, EmailLog satırları DB'de oluşur — testler bunu skip eder veya doğrular.

**Toplam hedef:** 322 + 39 = **~361 test** (spec text'inde 359 yazıyordu — küçük varyans plan implementation'da netleşir).

---

## 10. Manuel Smoke Plan

Phase 4a server local'de çalışıyor olmalı (DiskEmailSender ile).

1. Server start; Hangfire dashboard'a `/hangfire` git anonymous → AdminCookie redirect
2. Admin login → `/admin/` → sidebar'a "Hangfire" linki tıkla → dashboard 5 recurring job (renewal-14d/7d/3d/0d, expired-1d) görünür
3. Customer kaydet, admin lisans ihraç et → DiskEmailSender `tmp/emails/` klasöründe `LicenseIssued.eml` görünür (unsubscribe footer dahil)
4. Lisansı revoke et → `LicenseRevoked.eml` (footer dahil)
5. Lisansı extend et → `LicenseExtended.eml`
6. Lisansın ExpiresAt'ini elle 14g'a çek (SQL): `UPDATE Licenses SET ExpiresAt = DATEADD(day, 14, SYSUTCDATETIME()) WHERE LicenseKey = ...`
7. Hangfire dashboard'da "renewal-14d" job → "Trigger now" → log: 1 email gönderildi → `Renewal14d.eml` görünür
8. Aynı job'ı tekrar tetikle → log: 0 email (dedup)
9. Customer "şifremi unuttum": `POST /api/v1/auth/password-reset-request {email}` → 202 → `PasswordReset.eml` URL'ine git → form → yeni şifre → 204 → eski şifreyle login → 401, yeni şifreyle login → OK
10. `Renewal14d.eml`'deki unsubscribe link tıkla → `/unsubscribe?token=...` → form onayla → "Aboneliğiniz durduruldu". DB: `Customer.Unsubscribed = 1`
11. Lisansın ExpiresAt'ini 7g'e çek + Hangfire'da "renewal-7d" tetikle → email yok (Unsubscribed)
12. Yeni customer (unsub yok) → admin lisans uzat → `LicenseExtended.eml` footer'da yeni unsubscribe token

---

## 11. YAGNI

- Open/click tracking (pixel/redirect URL)
- Email A/B testing
- Çoklu dil (sadece TR)
- Bulk send / batching
- "Re-subscribe" butonu (Phase 4f client'ta)
- Admin UI'da email preview
- Custom email per-SKU
- Webhook bounce handling (SES/SendGrid)
- DKIM/SPF setup automation
- Unsubscribe audit log (admin tracking)
- Reset token throttle UI (server-side enforcement yeter)
- Email queue with cross-server distribution (Hangfire single server yeter)
- Custom Hangfire job priority/queues (default queue yeter)
- Per-customer unsubscribe-by-template (sadece global Unsubscribed)

---

## 12. Phase 4e Sonrası Açık Bırakılanlar

- **Phase 4f:** Client app email link handling (PasswordReset URL'ini LiveDeck.App'in handle etmesi yerine browser-default; AccountDialog "Şifremi unuttum" + "Yeniden abone ol" butonları)
- **Phase 5:** Stripe/PayTR webhook → otomatik lisans + invoice email
- **Sonraki:** Open/click tracking, bounce handling, multi-tenant (per-org admin), localization

---

## 13. Kabul Kriterleri

- ✅ Build temiz: 0 error / 0 warning (production)
- ✅ ~361 test pass (322 baseline + ~39 yeni)
- ✅ Hangfire dashboard `/hangfire` AdminCookie ile korunur (anonymous redirect, admin 200)
- ✅ 5 recurring job: renewal-14d/7d/3d/0d + expired-1d cron `0 9 * * *` ile kayıtlı
- ✅ EmailLog dedup: aynı (customerId, templateKey, contextKey) ile 2. send no-op
- ✅ Customer.Unsubscribed=true: marketing/admin-action skip; ConfirmEmail/PasswordReset transactional bypass
- ✅ Password reset: enumeration-safe (silent 202), token TTL 1h + single-use, 15-min throttle, rate-limited (auth-register 3/min)
- ✅ Admin action emails: Issue/Revoke/Extend → 6 trigger noktasından (3 Razor + 3 REST) sync send
- ✅ Unsubscribe: HMAC-signed token, tampered/wrong-customer reject, idempotent
- ✅ Pages/Public/PasswordReset + Pages/Public/Unsubscribe anonymous Razor sayfa, AntiForgery POST
- ✅ EF migration 006: EmailLog + PasswordResetToken + Customer.Unsubscribed apply edilir
- ✅ Phase 4a/4b/4c/4d regression: 322 mevcut test bozulmamış
- ✅ Hangfire SQL tablolar (HangFire schema) auto-created, LicenseDbContext etkilenmez
- ✅ Test ortamında Hangfire MemoryStorage override (SQL connection denenmez)
