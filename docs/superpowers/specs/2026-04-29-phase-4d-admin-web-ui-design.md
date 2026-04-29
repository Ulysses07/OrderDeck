# Faz 4d — Admin Web UI (Tasarım)

**Hedef:** Mevcut `LiveDeck.LicenseServer` (Phase 4a) projesine Razor Pages tabanlı admin paneli ekle. Bootstrap 5 CDN ile minimal styling. Cookie auth (8 saat sliding) browser session için, mevcut Bearer-Admin JWT REST API için korunur. Tam MVP: login, dashboard, customers, licenses (issue/revoke/extend), activations (force-deactivate), skus (read-only), audit log.

**Kapsam:** `LiveDeck.LicenseServer` projesine `Pages/Admin/*` klasörü + `Domain/AuditLogEntry.cs` + `Services/Audit/AuditService.cs` + EF Core migration `005_add_audit_log` + cookie auth scheme + Razor Pages middleware. Yeni proje YOK. LiveDeck.* projeleri (Core/Chat/App/Licensing) dokunulmaz.

**Pre-Faz-4d state:** Phase 4c HEAD `ac280aa` (master). 294/294 test (128 LiveDeck + 104 Licensing + 62 LicenseServer). Build 0/0.

---

## 1. Bağlam

**Sorun:** Phase 4a/4b/4c ile lisans server + client + trial hazır. Lisans yönetimi şu an manuel (Postman/curl) — admin web UI olmadan müşteri operasyonu zor. Customer kaydı, lisans ihraç, iptal, uzatma, hardware re-bind (force-deactivate via existing API) için kullanıcı dostu panel gerek. Audit log ile kim ne zaman ne yaptı izlenebilir olmalı.

**4d kapsamı (server-side admin UI):** 14 route Razor Page + 1 cookie auth scheme + audit log table + AuditService + Bootstrap 5 styling. REST API katmanı (Phase 4a) tamamen reuse — yeni endpoint YOK, sadece UI handlers REST endpoint'leri çağırmak yerine internal service'leri (LicenseIssuer, ActivationManager, vs.) doğrudan çağırır (testability + perf).

**4d kapsamında DEĞİL:**
- Multi-admin yönetimi (admin oluşturma/silme UI)
- Admin password reset
- Audit retention/cleanup automation
- Audit export (CSV/Excel)
- Bulk operations
- Lisans transfer
- HW re-bind UI (force-deactivate yeter)
- Trial uzatma (client-side trial; server override imkansız)
- Email automation (Phase 4e)
- Real-time UI (SignalR)
- Dashboard chart/graph
- Customer notes editing UI
- Localization (sadece Türkçe)
- Dark mode
- Audit log REST API (sadece UI; ileride gerekirse)

---

## 2. Mimari

### 2.1 Solution etkisi

`LiveDeck.LicenseServer` projesine yeni dosyalar + 1 paket. Yeni proje yok. Test projesi (`LiveDeck.LicenseServer.Tests`) 1 yeni paket (AngleSharp).

```
LiveDeck.LicenseServer/
├─ Pages/                                       (YENİ klasör)
│  ├─ _ViewImports.cshtml
│  ├─ _ViewStart.cshtml
│  ├─ Shared/_Layout.cshtml                     Public layout (404 vs için minimal)
│  └─ Admin/
│     ├─ _ViewImports.cshtml                    @namespace + @addTagHelper
│     ├─ _ViewStart.cshtml                      Layout = AdminLayout
│     ├─ Shared/
│     │  ├─ _AdminLayout.cshtml                 Sidebar + navbar + content
│     │  ├─ _ValidationScriptsPartial.cshtml
│     │  └─ _ToastPartial.cshtml                TempData success/error rendering
│     ├─ Login.cshtml + .cshtml.cs              Anonymous, GET form / POST sign-in
│     ├─ Logout.cshtml.cs                       POST handler-only (no view)
│     ├─ Index.cshtml + .cshtml.cs              Dashboard
│     ├─ Customers/
│     │  ├─ Index.cshtml + .cshtml.cs           Liste + search + pagination
│     │  └─ Detail.cshtml + .cshtml.cs          Profil + lisanslar + audit
│     ├─ Licenses/
│     │  ├─ Issue.cshtml + .cshtml.cs           Form
│     │  └─ Detail.cshtml + .cshtml.cs          Detay + revoke/extend formları
│     ├─ Activations/
│     │  └─ Index.cshtml + .cshtml.cs           License başına aktivasyon listesi
│     ├─ Skus/
│     │  └─ Index.cshtml + .cshtml.cs           Read-only liste
│     └─ Audit/
│        └─ Index.cshtml + .cshtml.cs           Audit log liste + filter
├─ Domain/
│  └─ AuditLogEntry.cs                          (YENİ)
├─ Data/
│  ├─ LicenseDbContext.cs                       (MODIFIED — AuditLogs DbSet + OnModelCreating)
│  └─ Migrations/
│     ├─ {timestamp}_AddAuditLog.cs             (YENİ — auto-generated)
│     └─ {timestamp}_AddAuditLog.Designer.cs
├─ Services/
│  └─ Audit/
│     ├─ IAuditService.cs                       (YENİ)
│     ├─ AuditService.cs                        (YENİ)
│     └─ AuditEvents.cs                         (YENİ — string sabitler)
├─ Program.cs                                   (MODIFIED — cookie scheme, RazorPages, AuthorizeFolder)
├─ LiveDeck.LicenseServer.csproj                (MODIFIED — +Microsoft.AspNetCore.Authentication.Cookies)
└─ wwwroot/
   ├─ css/admin.css                             Minimal custom (sidebar, table tweaks)
   └─ favicon.ico

LiveDeck.LicenseServer.Tests/
├─ LiveDeck.LicenseServer.Tests.csproj          (MODIFIED — +AngleSharp)
├─ TestHelpers/
│  ├─ ApiFactory.cs                             (MODIFIED — CookieContainer for HttpClient)
│  └─ AdminLoginHelper.cs                       (YENİ — login + cookie capture utility)
├─ Pages/
│  ├─ AdminAuthFlowTests.cs                     5 tests
│  ├─ AdminDashboardTests.cs                    2 tests
│  ├─ AdminCustomersPageTests.cs                4 tests
│  ├─ AdminLicensesPageTests.cs                 5 tests
│  ├─ AdminActivationsPageTests.cs              2 tests
│  └─ AdminAuditPageTests.cs                    3 tests
├─ Services/
│  └─ AuditServiceTests.cs                      4 tests
└─ Data/
   └─ AuditMigrationTests.cs                    1 test
```

### 2.2 Stack

| Bileşen | Seçim | Gerekçe |
|---|---|---|
| UI framework | Razor Pages | Server-rendered, en hafif, ASP.NET Core 10 native |
| CSS | Bootstrap 5 (CDN) | Sıfır build step, mature, ~30KB gzipped |
| Cookie auth | `Microsoft.AspNetCore.Authentication.Cookies` 10.0.0 | BCL'de değil, yeni paket. Cookie HttpOnly + SameSite=Lax |
| HTML test asserter | AngleSharp 1.x | DOM parsing for "5 satır var mı" tarzı assert |
| ORM/migration | Mevcut EF Core 9 + SqlServer | Phase 4a aynı |
| Anti-forgery | Razor Pages built-in | POST'larda otomatik token, GET form'larda hidden field |

### 2.3 İki auth şeması yan yana

**`Bearer-Admin`** (Phase 4a, JWT 1h) — REST API:
- `[Authorize(AuthenticationSchemes = "Bearer-Admin")]` mevcut Controllers/Auth/AdminAuthController + Customers + Licenses + Activations + Skus
- Postman/curl/admin tooling için
- `/api/v1/admin/*` route'lar

**`AdminCookie`** (Phase 4d YENİ, cookie 8h sliding) — Razor Pages:
- Razor Pages convention: `AuthorizeFolder("/Admin", "AdminOnly")` ile tüm `/admin/*` cookie gerektirir
- `AllowAnonymousToPage("/Admin/Login")` istisnası
- HttpOnly + SameSite=Lax + SecurePolicy=SameAsRequest (dev HTTP, prod HTTPS)

**Ortak:** `AdminUser` tablosu (Phase 4a) + `PasswordHasher` (Argon2id). Aynı admin user her iki şemayla giriş yapabilir.

**Cross-scheme bypass yok:** Bearer-Admin cookie'yi auth'lamaz; AdminCookie REST'i auth'lamaz. Scheme isimleri farklı, izole.

### 2.4 Internal services vs REST controllers

Razor Pages handlers (`OnPostAsync`) mevcut Phase 4a internal services'i (`LicenseIssuer`, `ActivationManager`, `EmailConfirmationService`, vs.) **doğrudan** çağırır — REST controller'a HTTP yapmaz. Avantajlar:
- Tek SQL transaction sınırı
- HTTP latency yok
- Test daha sade (HTTP mock gerekmez)
- Type safety (request/response DTO'ya gerek yok, doğrudan domain methods)

REST controllers paralelde durur, postman flow'u bozulmaz.

---

## 3. Sayfa Haritası ve Davranış

| Route | Method | Auth | İşlev |
|---|---|---|---|
| `GET /admin/login` | Page | Anon | Login formu (username + password + AntiForgery) |
| `POST /admin/login` | Handler | Anon | Verify → SignInAsync (cookie) → audit `admin.login` → redirect (returnUrl veya /admin/) |
| `POST /admin/logout` | Handler | Cookie | audit `admin.logout` → SignOutAsync → redirect /admin/login |
| `GET /admin/` (Index) | Page | Cookie | Dashboard: 4 sayım (Customers, Active Licenses, Expired Licenses, Active Activations) |
| `GET /admin/customers` | Page | Cookie | Customer listesi (default 25/sayfa, ?search=email substring, ?page=N) |
| `GET /admin/customers/{id:guid}` | Page | Cookie | Profil + license listesi + son 20 audit entry (target_type=customer/license filter) |
| `POST /admin/customers/{id}/confirm-email` | Handler | Cookie | EmailConfirmedAt set + audit `customer.confirm-email` |
| `GET /admin/licenses/issue` | Page | Cookie | Form: customer email (text), sku (select STD/PRO), durationDaysOverride (optional int), slotsOverride (optional int) |
| `POST /admin/licenses/issue` | Handler | Cookie | LicenseIssuer.IssueAsync + audit `license.issue` (details: email, sku, overrides) → redirect license detail |
| `GET /admin/licenses/{key}` | Page | Cookie | Lisans detayı + activations list + revoke form (reason) + extend form (additionalDays) + audit list |
| `POST /admin/licenses/{key}/revoke` | Handler | Cookie | RevokedAt + RevokeReason set + audit `license.revoke` (details: reason) → redirect detail |
| `POST /admin/licenses/{key}/extend` | Handler | Cookie | ExpiresAt += days + audit `license.extend` (details: additionalDays) → redirect detail |
| `POST /admin/activations/{id:guid}/deactivate` | Handler | Cookie | ActivationManager.ForceDeactivateAsync + audit `activation.force-deactivate` (details: HW fingerprint) |
| `GET /admin/skus` | Page | Cookie | Read-only Sku listesi |
| `GET /admin/audit` | Page | Cookie | Audit liste: ?eventType + ?adminUsername + ?from + ?to + ?page (default last 7d) |

**Dashboard sayıları (LicenseDbContext queries):**
```csharp
TotalCustomers     = await db.Customers.CountAsync()
ActiveLicenses     = await db.Licenses.CountAsync(l => l.RevokedAt == null && l.ExpiresAt > now)
ExpiredLicenses    = await db.Licenses.CountAsync(l => l.RevokedAt != null || l.ExpiresAt <= now)
ActiveActivations  = await db.Activations.CountAsync(a => a.DeactivatedAt == null)
```

Tek round-trip değil; 4 query. Cache yok (gerçek zamanlı).

**Pagination:** Plain query string `?page=N&size=25`. Linkler "Önceki" / "Sonraki". Toplam count + sayfa hesaplaması basit.

**Form validation:** `[BindProperty]` + DataAnnotations (`[Required]`, `[EmailAddress]`, `[Range]`). `<asp:validation-summary>` ve `<span asp-validation-for>` tag helpers.

**Toast pattern:** `TempData["Success"] = "..."` veya `TempData["Error"] = "..."`. `_ToastPartial.cshtml` _AdminLayout'tan render edilir; gösterimden sonra TempData otomatik silinir.

**Customer detail audit kapsamı:** 
```csharp
audits = db.AuditLogs
    .Where(a => (a.TargetType == "customer" && a.TargetId == customerId.ToString())
             || (a.TargetType == "license" && licenseKeysOfCustomer.Contains(a.TargetId)))
    .OrderByDescending(a => a.OccurredAt)
    .Take(20)
    .ToList()
```

### 3.1 _AdminLayout sidebar yapısı

```
┌─────────────┬───────────────────────────┐
│ LiveDeck    │  [TempData toast]         │
│ Admin       │                           │
├─────────────┤  <Page content>           │
│ Dashboard   │                           │
│ Customers   │                           │
│ Licenses    │                           │
│   Issue New │                           │
│ Activations │                           │
│ Skus        │                           │
│ Audit Log   │                           │
├─────────────┤                           │
│ admin@...   │                           │
│ [Logout]    │                           │
└─────────────┴───────────────────────────┘
```

Bootstrap 5 grid: `col-md-2` sidebar + `col-md-10` content. Sidebar nav `<ul class="nav flex-column">`.

### 3.2 Bootstrap 5 CDN

`_Layout.cshtml` içinde `<head>`:
```html
<link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css">
```

Bootstrap JS sadece dropdown/collapse için yüklenir (ileride lazım olunca). Şimdilik sadece CSS.

`wwwroot/css/admin.css` ek custom:
- `.sidebar { min-height: 100vh; background: #f8f9fa; }`
- `.sidebar a.active { font-weight: bold; }`
- `.toast-container { position: fixed; top: 1rem; right: 1rem; z-index: 1080; }`

---

## 4. Audit Log

### 4.1 AuditLogEntry entity

```csharp
public sealed class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid AdminId { get; set; }
    public string AdminUsername { get; set; } = "";   // denormalize: hızlı liste için (admin silinince hâlâ okunabilir)
    public string EventType { get; set; } = "";       // AuditEvents sabitleri
    public string TargetType { get; set; } = "";      // "license" | "customer" | "activation" | "admin"
    public string? TargetId { get; set; }             // license key | customer/activation/admin guid
    public string? Details { get; set; }              // JSON: {reason, durationDaysOverride, etc.}
    public string? IpAddress { get; set; }            // request RemoteIpAddress (nullable for headless tests)
}
```

### 4.2 EF Core Configuration (LicenseDbContext.OnModelCreating)

```csharp
mb.Entity<AuditLogEntry>(b =>
{
    b.HasKey(a => a.Id);
    b.Property(a => a.AdminUsername).HasMaxLength(64).IsRequired();
    b.Property(a => a.EventType).HasMaxLength(64).IsRequired();
    b.Property(a => a.TargetType).HasMaxLength(32).IsRequired();
    b.Property(a => a.TargetId).HasMaxLength(64);
    b.Property(a => a.Details).HasMaxLength(4000);   // nvarchar(4000)
    b.Property(a => a.IpAddress).HasMaxLength(64);
    b.HasIndex(a => a.OccurredAt);
    b.HasIndex(a => new { a.AdminId, a.OccurredAt });
    b.HasIndex(a => new { a.TargetType, a.TargetId });
});
```

`DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();`

Migration: `dotnet ef migrations add AddAuditLog --output-dir Data/Migrations` → `005_AddAuditLog`.

### 4.3 AuditEvents constants

```csharp
public static class AuditEvents
{
    public const string AdminLogin = "admin.login";
    public const string AdminLogout = "admin.logout";
    public const string CustomerConfirmEmail = "customer.confirm-email";
    public const string LicenseIssue = "license.issue";
    public const string LicenseRevoke = "license.revoke";
    public const string LicenseExtend = "license.extend";
    public const string ActivationForceDeactivate = "activation.force-deactivate";
}
```

7 event tipi (login + logout + 5 işlem). Customer.create UI'da yok (Phase 4a admin REST endpoint var; UI scope dışı bırakıldı — issue page yeter).

### 4.4 IAuditService + AuditService

```csharp
public interface IAuditService
{
    Task LogAsync(string eventType, string targetType, string? targetId, object? details = null, CancellationToken ct = default);
    Task LogLoginAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default);
    Task LogLogoutAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default);
}

public sealed class AuditService : IAuditService
{
    private readonly LicenseDbContext _db;
    private readonly IHttpContextAccessor _httpContext;

    public AuditService(LicenseDbContext db, IHttpContextAccessor httpContext) { ... }

    // LogAsync: HttpContext.User claims'den AdminId/Username + RemoteIpAddress al, JSON serialize details, _db.AuditLogs.Add + SaveChanges.
    // LogLoginAsync / LogLogoutAsync: User henüz set yok / silinmiş, parametre olarak adminId/username/ip alır.
}
```

### 4.5 Audit yazma noktaları (per page handler)

| Page handler | Event | Target | Details |
|---|---|---|---|
| `Login.OnPostAsync` (success) | `admin.login` | `admin` | adminId | (none) |
| `Logout.OnPostAsync` | `admin.logout` | `admin` | adminId | (none) |
| `Customers.Detail.OnPostConfirmEmailAsync` | `customer.confirm-email` | `customer` | customerId | (none) |
| `Licenses.Issue.OnPostAsync` | `license.issue` | `license` | licenseKey | `{customerEmail, skuCode, durationDaysOverride, slotsOverride}` |
| `Licenses.Detail.OnPostRevokeAsync` | `license.revoke` | `license` | licenseKey | `{reason}` |
| `Licenses.Detail.OnPostExtendAsync` | `license.extend` | `license` | licenseKey | `{additionalDays}` |
| `Activations.Index.OnPostDeactivateAsync` | `activation.force-deactivate` | `activation` | activationId | `{hardwareFingerprint, licenseKey}` |

### 4.6 Audit list page

`/admin/audit?eventType=&adminUsername=&from=&to=&page=`:
- Default: from = now - 7 day, to = now, page = 1 (size 50)
- Filter: EventType select (8 option + ""), AdminUsername text input, DateRange (2 date inputs)
- Tablo: OccurredAt | AdminUsername | EventType | TargetType | TargetId | Details (truncated 60 char + tooltip full JSON) | IpAddress
- Pagination: ön/sonraki

### 4.7 Failed login NOT logged

Brute-force log spam ve Phase 4a rate limiter (5/min/IP) zaten korur. Failed login UI'da generic "geçersiz" mesajı + log warning (Serilog) yeterli.

### 4.8 Retention

YAGNI. Production'da admin manuel:
```sql
DELETE FROM AuditLogs WHERE OccurredAt < DATEADD(month, -6, GETUTCDATE());
```

---

## 5. Konfigürasyon + DI

### 5.1 Yeni paketler

`LiveDeck.LicenseServer.csproj`:
```xml
<PackageReference Include="Microsoft.AspNetCore.Authentication.Cookies" Version="10.0.0" />
```

`LiveDeck.LicenseServer.Tests.csproj`:
```xml
<PackageReference Include="AngleSharp" Version="1.1.2" />
```

### 5.2 Program.cs değişiklikleri

```csharp
// Mevcut Bearer-Customer + Bearer-Admin'in YANINDA cookie scheme:
builder.Services.AddAuthentication()
    .AddJwtBearer("Bearer-Customer", ...)
    .AddJwtBearer("Bearer-Admin", ...)
    .AddCookie("AdminCookie", o =>
    {
        o.LoginPath = "/admin/login";
        o.AccessDeniedPath = "/admin/login";
        o.LogoutPath = "/admin/logout";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = true;
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
        o.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        o.Cookie.Name = "LiveDeckAdmin";
    });

builder.Services.AddAuthorization(opt =>
{
    opt.AddPolicy("AdminOnly", p => p
        .AddAuthenticationSchemes("AdminCookie")
        .RequireAuthenticatedUser());
});

builder.Services.AddRazorPages(opt =>
{
    opt.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    opt.Conventions.AllowAnonymousToPage("/Admin/Login");
    opt.Conventions.AllowAnonymousToPage("/Admin/Logout"); // logout'a anonymous eri\u015fim hata vermez
});

builder.Services.AddHttpContextAccessor();      // AuditService için
builder.Services.AddScoped<IAuditService, AuditService>();

// Pipeline (mevcut UseHttpsRedirection sonrası):
app.UseStaticFiles();           // wwwroot/
app.UseRouting();               // (zaten var olabilir)
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();
app.MapRazorPages();            // YENİ
```

### 5.3 Cookie scheme test environment

`ApiFactory.cs`'de `HttpClient` default `HandleCookies = true`. Test'lerde:
```csharp
var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });
```

Login sonrası cookie otomatik saklanır, sonraki requestlerde gönderilir.

### 5.4 Anti-forgery in tests

Razor Pages `<form>` POST'larda anti-forgery token zorunlu. Test pattern:
1. GET `/admin/login` → response HTML'inden hidden `__RequestVerificationToken` parse et (AngleSharp)
2. POST `/admin/login` form data + token + Cookie header (anti-forgery cookie set on GET)
3. Sonraki POST'lar için login ettikten sonra GET sayfa al → token + cookie güncel → POST

Helper: `LiveDeck.LicenseServer.Tests/TestHelpers/AdminLoginHelper.cs`:
```csharp
public static async Task<HttpClient> CreateLoggedInAdminClientAsync(this ApiFactory factory, string username, string password) { ... }
public static async Task<(string token, string cookie)> ExtractAntiForgeryAsync(this HttpClient client, string getUrl) { ... }
```

---

## 6. Test Stratejisi

### 6.1 Kategoriler

| Test dosyası | Tip | Test sayısı | Kapsam |
|---|---|---|---|
| `Pages/AdminAuthFlowTests.cs` | Integration | 5 | login form GET, POST success → cookie, POST wrong creds → "geçersiz", logout, anonymous → redirect |
| `Pages/AdminDashboardTests.cs` | Integration | 2 | Index sayfası 200 + 4 sayım, requires auth |
| `Pages/AdminCustomersPageTests.cs` | Integration | 4 | List sayfa, detail sayfa, search filter, pagination |
| `Pages/AdminLicensesPageTests.cs` | Integration | 5 | Issue form, Issue POST → license, Detail page, Revoke POST, Extend POST |
| `Pages/AdminActivationsPageTests.cs` | Integration | 2 | List for license, Force-deactivate handler |
| `Pages/AdminAuditPageTests.cs` | Integration | 3 | List load, eventType filter, dateRange filter |
| `Services/AuditServiceTests.cs` | Unit | 4 | LogAsync entity create, IpAddress capture, JSON details serialize, LogLoginAsync without HttpContext.User |
| `Data/AuditMigrationTests.cs` | Smoke | 1 | EF Core EnsureCreated → AuditLogs DbSet erişilebilir |

**Tahmini yeni test:** 26 LicenseServer test.

### 6.2 Mevcut test regression

REST API test'leri (62) bozulmaz. Hatta `MeTests` + AdminAuthController testleri Bearer-Admin scheme kullanır; cookie scheme ayrı.

`LiveDeck.Tests` (128) ve `LiveDeck.Licensing.Tests` (104) dokunulmaz.

### 6.3 Toplam hedef

- Mevcut: 128 + 104 + 62 = **294**
- Yeni: 26 LicenseServer
- Phase 4d sonrası: **~320** (294 + 26)

### 6.4 AngleSharp kullanım örneği

```csharp
var html = await response.Content.ReadAsStringAsync();
var doc = await new BrowsingContext(Configuration.Default).OpenAsync(req => req.Content(html));
var rows = doc.QuerySelectorAll("table.customer-list tbody tr");
rows.Should().HaveCount(5);
var firstEmail = doc.QuerySelector("table.customer-list tbody tr td.email")!.TextContent.Trim();
firstEmail.Should().Be("user@example.com");
```

---

## 7. Manuel Smoke Plan

Phase 4a server'ın `appsettings.json`'unda `Admin:InitialUsername=admin` + `Admin:InitialPasswordHash=<argon2 hash>` set edili olmalı. Dev'de helper script (PowerShell) ile hash üretilir.

1. `dotnet run --project LiveDeck.LicenseServer` → server localhost:5001
2. Tarayıcı: `https://localhost:5001/admin/login` → form görünür (Bootstrap styling)
3. Yanlış password → "Geçersiz kullanıcı adı veya şifre" hata, formda kalır
4. Doğru creds → 302 → `/admin/` Dashboard. 4 sayım görünür (initial state: 0/0/0/0)
5. Sidebar "Customers" → liste boş ("Henüz müşteri yok")
6. Test customer Postman ile create et (REST), `/admin/customers` refresh → 1 satır
7. Customer detail sayfası açılır → "Lisans yok" altında "Yeni lisans" link
8. Sidebar "Licenses → Issue New" → form → customer email + STD seç → POST → 302 → license detail. Toast "Lisans oluşturuldu: LDK-..."
9. Detail'de "Extend" form → 30 gün → POST → ExpiresAt güncellenir, toast
10. "Revoke" form → reason "Test iptal" → POST → durum "Revoked" görünür, toast
11. Phase 4b client app'iyle bu license aktivate ettikten sonra `/admin/activations?licenseKey=LDK-...` → 1 row → "Force deactivate" → POST → DeactivatedAt set, satır gri
12. Sidebar "Audit Log" → liste → en son 7 event görünür (admin.login, customer.confirm-email, license.issue, license.extend, license.revoke, activation.force-deactivate, henüz logout değil)
13. EventType filter `license.revoke` → 1 satır
14. Sidebar "Logout" → cookie silinir → /admin/login redirect
15. Anonymous'ken `/admin/customers` aç → `/admin/login?returnUrl=/admin/customers` redirect
16. Login sonrası returnUrl preserve → /admin/customers'a otomatik gidiş

---

## 8. YAGNI

- Multi-admin yönetimi (admin oluştur/sil UI)
- Admin password reset flow
- Audit retention/cleanup automation
- Audit export (CSV/Excel)
- Customer search advanced filter (sadece email substring)
- Bulk operations
- Lisans transfer
- HW re-bind UI (force-deactivate yeter)
- Trial uzatma
- Email notification on admin actions
- Real-time updates (SignalR)
- Dashboard chart/graph
- Customer notes editing UI
- License key partial search
- Localization (sadece TR)
- Dark mode
- Audit log REST API
- Admin rol granularity (super-admin/support)
- 2FA/MFA admin
- Session list & revoke
- IP allowlist for admin

---

## 9. Phase 4d Sonrası Açık Bırakılanlar

- **Phase 4e:** Email otomasyonu — yenileme uyarıları, password reset, admin action notify
- **Phase 5:** Stripe/PayTR webhook → otomatik lisans
- **Sonraki:** Multi-admin + roller, 2FA, audit export

---

## 10. Kabul Kriterleri

- ✅ Build temiz: 0 error / 0 warning (production code)
- ✅ ~320 test pass (294 baseline + ~26 yeni)
- ✅ Cookie auth çalışır: `/admin/login` POST cookie döndürür, `/admin/*` sayfalarına 8 saat erişim
- ✅ Anonymous + `/admin/customers` → `/admin/login?returnUrl=...` 302 redirect
- ✅ Cookie + Bearer-Admin scheme'leri izole; REST API testleri bozulmaz
- ✅ Dashboard 4 sayım gerçek SQL'den
- ✅ Customer/License/Activation/Audit listeleri pagination + filter ile çalışır
- ✅ License issue → revoke → extend → force-deactivate akışı UI'dan end-to-end
- ✅ AuditLog 7 event tipi tüm handler'larda yazılır
- ✅ Bootstrap 5 CDN ile sidebar layout düzgün render
- ✅ Anti-forgery POST'larda aktif (test'ler token alır)
- ✅ EF Core migration `005_AddAuditLog` apply edilir
- ✅ AngleSharp ile HTML assertion testleri çalışır
- ✅ Mevcut Phase 4a/4b/4c (294 test) regression yok
