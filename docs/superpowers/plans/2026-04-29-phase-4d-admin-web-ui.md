# Faz 4d — Admin Web UI Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `LiveDeck.LicenseServer` (Phase 4a) projesine Razor Pages tabanlı admin paneli ekle (login, dashboard, customers, licenses issue/revoke/extend, activations force-deactivate, skus, audit log). Bootstrap 5 CDN styling. Cookie auth (8h sliding) browser session için, mevcut Bearer-Admin JWT REST API için korunur.

**Architecture:** Razor Pages mevcut `LiveDeck.LicenseServer` projesine entegre edilir — yeni proje yok. Pages handlers Phase 4a internal services'i (`LicenseIssuer`, `ActivationManager`) doğrudan çağırır. Yeni `AuditLogEntry` entity + EF migration + `AuditService`. 2 auth scheme yan yana: `Bearer-Admin` (REST) + `AdminCookie` (Pages).

**Tech Stack:** ASP.NET Core 10 Razor Pages / `Microsoft.AspNetCore.Authentication.Cookies` 10.0.0 / Bootstrap 5.3.3 (CDN) / `AngleSharp` 1.1.2 (test HTML assertion) / mevcut EF Core 9 + SqlServer.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-4d state:** Phase 4c master `ac280aa`. 294/294 test (128 LiveDeck + 104 Licensing + 62 LicenseServer). Build 0/0.

**Spec reference:** `docs/superpowers/specs/2026-04-29-phase-4d-admin-web-ui-design.md`

---

## Task Index

**Foundation (1):** Package + Program.cs cookie scheme + Razor Pages middleware + base layout shells
**Audit (2):** AuditLogEntry entity + EF migration + IAuditService + AuditService (TDD)
**Auth pages (3):** Login + Logout pages + AdminLoginHelper test infra
**Dashboard (4):** Index page (4 sayım)
**Customers (5):** Index + Detail + ConfirmEmail handler
**Licenses (6-7):** Issue page · Detail + Revoke/Extend handlers
**Activations (8):** Index + Force-deactivate handler
**Skus (9):** Read-only liste
**Audit Page (10):** Index + filter + pagination
**Polish (11):** AdminLayout sidebar + admin.css + ToastPartial + manual smoke

---

### Task 1: Foundation — packages + cookie scheme + Razor Pages middleware + base layout

**Files:**
- Modify: `LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj`
- Modify: `LiveDeck.LicenseServer/Program.cs`
- Create: `LiveDeck.LicenseServer/Pages/_ViewImports.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/_ViewStart.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Shared/_Layout.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/_ViewImports.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/_ViewStart.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Shared/_AdminLayout.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Login.cshtml` (placeholder shell, real impl Task 3)
- Create: `LiveDeck.LicenseServer/Pages/Admin/Login.cshtml.cs` (placeholder shell)
- Create: `LiveDeck.LicenseServer/wwwroot/css/admin.css` (boş başlangıç)
- Modify: `LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj`
- Modify: `LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` (cookie handling)

**Context:** Razor Pages altyapısı + cookie auth scheme. Login.cshtml SADECE `@page` direktifi + boş gövde — Task 3'te dolacak. Bu task'ın amacı: 62/62 mevcut REST testleri bozulmasın + tarayıcıda `/admin/login` 200 dönsün.

- [ ] **Step 1: csproj — Cookie auth paketi ekle**

`LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj` mevcut `<ItemGroup>` (PackageReference) bloğuna ekle:

```xml
    <PackageReference Include="Microsoft.AspNetCore.Authentication.Cookies" Version="10.0.0" />
```

- [ ] **Step 2: Test csproj — AngleSharp ekle**

`LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj` `<ItemGroup>` (PackageReference) bloğuna ekle:

```xml
    <PackageReference Include="AngleSharp" Version="1.1.2" />
```

- [ ] **Step 3: Program.cs — cookie scheme + RazorPages + middleware**

`LiveDeck.LicenseServer/Program.cs` dosyasını aç. Mevcut `AddJwtBearer("Bearer-Customer"...)` ve `AddJwtBearer("Bearer-Admin"...)` chain'inin sonuna `.AddCookie(...)` ekle. Final auth bölümü şu şekilde olmalı:

```csharp
        builder.Services.AddAuthentication()
            .AddJwtBearer("Bearer-Customer", o => { /* mevcut config */ })
            .AddJwtBearer("Bearer-Admin", o => { /* mevcut config */ })
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
```

(Mevcut JwtBearer config bloklarını OLDUĞU GİBİ KORU — sadece chain'in sonuna AddCookie ekle.)

Mevcut `AddAuthorization()` çağrısının yanına/sonrasına ekle:

```csharp
        builder.Services.AddAuthorization(opt =>
        {
            opt.AddPolicy("AdminOnly", p => p
                .AddAuthenticationSchemes("AdminCookie")
                .RequireAuthenticatedUser());
        });
```

(Mevcut `AddAuthorization()` parameter-less çağrısı varsa, opt callback alan versiyonla değiştir; mevcut policy'lere ek olarak.)

Sonra `builder.Services.AddControllers()` satırından sonra ekle:

```csharp
        builder.Services.AddRazorPages(opt =>
        {
            opt.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
            opt.Conventions.AllowAnonymousToPage("/Admin/Login");
            opt.Conventions.AllowAnonymousToPage("/Admin/Logout");
        });
```

Pipeline kısmında, `app.MapControllers()` satırından **önce** ekle:

```csharp
        app.UseStaticFiles();
```

Ve `app.MapControllers()` satırından **hemen sonra** ekle:

```csharp
        app.MapRazorPages();
```

(`UseRouting`/`UseAuthentication`/`UseAuthorization` mevcutta zaten doğru konumda; dokunma.)

- [ ] **Step 4: Pages/_ViewImports.cshtml**

`LiveDeck.LicenseServer/Pages/_ViewImports.cshtml`:

```cshtml
@using LiveDeck.LicenseServer
@using LiveDeck.LicenseServer.Pages
@namespace LiveDeck.LicenseServer.Pages
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

- [ ] **Step 5: Pages/_ViewStart.cshtml**

`LiveDeck.LicenseServer/Pages/_ViewStart.cshtml`:

```cshtml
@{
    Layout = "_Layout";
}
```

- [ ] **Step 6: Pages/Shared/_Layout.cshtml**

`LiveDeck.LicenseServer/Pages/Shared/_Layout.cshtml`:

```cshtml
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] — LiveDeck</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
</head>
<body>
    @RenderBody()
</body>
</html>
```

- [ ] **Step 7: Pages/Admin/_ViewImports.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/_ViewImports.cshtml`:

```cshtml
@using LiveDeck.LicenseServer
@using LiveDeck.LicenseServer.Pages.Admin
@namespace LiveDeck.LicenseServer.Pages.Admin
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

- [ ] **Step 8: Pages/Admin/_ViewStart.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/_ViewStart.cshtml`:

```cshtml
@{
    Layout = "_AdminLayout";
}
```

- [ ] **Step 9: Pages/Admin/Shared/_AdminLayout.cshtml — minimal sidebar shell**

Bu task'ta layout SADECE iskelet — Task 11'de stilize edilecek. `LiveDeck.LicenseServer/Pages/Admin/Shared/_AdminLayout.cshtml`:

```cshtml
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] — LiveDeck Admin</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
    <link rel="stylesheet" href="~/css/admin.css" />
</head>
<body>
    <div class="container-fluid">
        @RenderBody()
    </div>
</body>
</html>
```

- [ ] **Step 10: wwwroot/css/admin.css — boş başlangıç**

`LiveDeck.LicenseServer/wwwroot/css/admin.css`:

```css
/* Admin styling — Task 11'de doldurulur. */
```

- [ ] **Step 11: Pages/Admin/Login.cshtml — placeholder**

`LiveDeck.LicenseServer/Pages/Admin/Login.cshtml`:

```cshtml
@page
@model LoginModel
@{
    ViewData["Title"] = "Giriş";
    Layout = null;  // Login sayfası AdminLayout sidebar'ını kullanmaz
}
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <title>Giriş — LiveDeck Admin</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
</head>
<body class="bg-light">
    <main class="container py-5">
        <h1>LiveDeck Admin — Giriş</h1>
        <p>Login formu Task 3'te dolacak.</p>
    </main>
</body>
</html>
```

`LiveDeck.LicenseServer/Pages/Admin/Login.cshtml.cs`:

```csharp
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiveDeck.LicenseServer.Pages.Admin;

public class LoginModel : PageModel
{
    public void OnGet()
    {
    }
}
```

- [ ] **Step 12: ApiFactory — cookie handling enable**

`LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` mevcut sınıfa **henüz dokunma** — `WebApplicationFactory<Program>.CreateClient()` default olarak cookie handling yapar (`HandleCookies = true`). Mevcut testler etkilenmez. Task 3'te login helper bu davranışı kullanacak.

- [ ] **Step 13: Smoke test ekle (login sayfası 200 dönüyor mu?)**

`LiveDeck.LicenseServer.Tests/Pages/AdminLoginPlaceholderTests.cs` oluştur:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public class AdminLoginPlaceholderTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminLoginPlaceholderTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_admin_login_returns_200_anonymously()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/login");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("LiveDeck Admin");
    }

    [Fact]
    public async Task Get_admin_index_redirects_to_login_for_anonymous()
    {
        var options = new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        };
        var client = _factory.CreateClient(options);
        var resp = await client.GetAsync("/admin");
        // Cookie auth scheme: anonymous → 302 Redirect to /admin/login
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().StartWith("/admin/login");
    }
}
```

`/admin` (Index sayfası) henüz yok → 404 olabilir. Bu test ileride Task 4'te Index eklendiğinde 302 olacak. Şimdilik testi ekle ama beklenen 404 olabilir; eğer 404 dönerse bu test'i `[Fact(Skip = "Index page added in Task 4")]` yap.

Pratik: ilk test (Get_admin_login_returns_200) bu task'ta pass olmalı; ikinci test'i şimdiden skip ile koy:

```csharp
    [Fact(Skip = "Index page added in Task 4 — re-enable then")]
    public async Task Get_admin_index_redirects_to_login_for_anonymous()
    {
        // ...
    }
```

- [ ] **Step 14: Build + tüm testler**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Beklenen: 0 errors / 0 warnings.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -5
```

Beklenen: 63 pass + 1 skip = 64 total (62 baseline + 1 yeni active + 1 skip).

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 128/128 + 104/104 (regression).

- [ ] **Step 15: Commit**

```bash
git add LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer/Pages/ \
        LiveDeck.LicenseServer/wwwroot/ \
        LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj \
        LiveDeck.LicenseServer.Tests/Pages/AdminLoginPlaceholderTests.cs
git commit -m "feat(license-server): scaffold Razor Pages + cookie auth scheme"
```

---

### Task 2: AuditLogEntry entity + DbContext + EF migration + IAuditService + AuditService

**Files:**
- Create: `LiveDeck.LicenseServer/Domain/AuditLogEntry.cs`
- Create: `LiveDeck.LicenseServer/Services/Audit/IAuditService.cs`
- Create: `LiveDeck.LicenseServer/Services/Audit/AuditService.cs`
- Create: `LiveDeck.LicenseServer/Services/Audit/AuditEvents.cs`
- Modify: `LiveDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `LiveDeck.LicenseServer/Data/Migrations/{timestamp}_AddAuditLog.cs` (auto-generated)
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI registration)
- Create: `LiveDeck.LicenseServer.Tests/Services/AuditServiceTests.cs`

**Context:** Pure data + service layer. Hiç Razor Page yok. AuditService HttpContext.User claims'inden AdminId/Username çeker; LogLoginAsync/LogLogoutAsync overload'ları User henüz yok/silinmiş senaryolar için. EF migration command'i çalıştır + auto-generated dosyayı commit'e ekle.

- [ ] **Step 1: AuditLogEntry entity oluştur**

`LiveDeck.LicenseServer/Domain/AuditLogEntry.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class AuditLogEntry
{
    public Guid Id { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public Guid AdminId { get; set; }
    public string AdminUsername { get; set; } = "";
    public string EventType { get; set; } = "";
    public string TargetType { get; set; } = "";
    public string? TargetId { get; set; }
    public string? Details { get; set; }
    public string? IpAddress { get; set; }
}
```

- [ ] **Step 2: AuditEvents constants oluştur**

`LiveDeck.LicenseServer/Services/Audit/AuditEvents.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Audit;

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

public static class AuditTargets
{
    public const string Admin = "admin";
    public const string Customer = "customer";
    public const string License = "license";
    public const string Activation = "activation";
}
```

- [ ] **Step 3: IAuditService interface oluştur**

`LiveDeck.LicenseServer/Services/Audit/IAuditService.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Audit;

public interface IAuditService
{
    /// <summary>Logs an event using HttpContext.User for admin identity. Used in handlers after auth.</summary>
    Task LogAsync(string eventType, string targetType, string? targetId, object? details = null, CancellationToken ct = default);

    /// <summary>Login flow — User claims not yet set; pass admin info explicitly.</summary>
    Task LogLoginAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default);

    /// <summary>Logout flow — log before SignOutAsync clears claims.</summary>
    Task LogLogoutAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default);
}
```

- [ ] **Step 4: AuditServiceTests yaz (failing)**

`LiveDeck.LicenseServer.Tests/Services/AuditServiceTests.cs`:

```csharp
using System.Security.Claims;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Audit;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class AuditServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AuditServiceTests(ApiFactory factory) => _factory = factory;

    private (LicenseDbContext db, AuditService svc, DefaultHttpContext httpContext) Build()
    {
        var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var ctx = new DefaultHttpContext();
        var accessor = new HttpContextAccessor { HttpContext = ctx };
        var svc = new AuditService(db, accessor);
        return (db, svc, ctx);
    }

    [Fact]
    public async Task LogAsync_creates_entry_with_user_claims()
    {
        var (db, svc, ctx) = Build();
        var adminId = Guid.NewGuid();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", adminId.ToString()),
            new Claim("username", "alice")
        }, "AdminCookie"));
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.5");

        await svc.LogAsync(AuditEvents.LicenseRevoke, AuditTargets.License, "LDK-XYZ",
            new { reason = "test" });

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.AdminId.Should().Be(adminId);
        entry.AdminUsername.Should().Be("alice");
        entry.EventType.Should().Be("license.revoke");
        entry.TargetType.Should().Be("license");
        entry.TargetId.Should().Be("LDK-XYZ");
        entry.Details.Should().Contain("test");
        entry.IpAddress.Should().Be("10.0.0.5");
    }

    [Fact]
    public async Task LogAsync_serializes_details_as_json()
    {
        var (db, svc, ctx) = Build();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("username", "bob")
        }, "AdminCookie"));

        await svc.LogAsync(AuditEvents.LicenseExtend, AuditTargets.License, "LDK-EXT",
            new { additionalDays = 30 });

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.Details.Should().Contain("\"additionalDays\":30");
    }

    [Fact]
    public async Task LogAsync_with_null_details_writes_null_field()
    {
        var (db, svc, ctx) = Build();
        ctx.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim("sub", Guid.NewGuid().ToString()),
            new Claim("username", "carol")
        }, "AdminCookie"));

        await svc.LogAsync(AuditEvents.CustomerConfirmEmail, AuditTargets.Customer, "cust-1");

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.Details.Should().BeNull();
    }

    [Fact]
    public async Task LogLoginAsync_writes_entry_without_user_claims()
    {
        var (db, svc, _) = Build();
        var adminId = Guid.NewGuid();

        await svc.LogLoginAsync(adminId, "dave", "192.168.1.1");

        var entry = db.AuditLogs.OrderByDescending(a => a.OccurredAt).First();
        entry.AdminId.Should().Be(adminId);
        entry.AdminUsername.Should().Be("dave");
        entry.EventType.Should().Be("admin.login");
        entry.TargetType.Should().Be("admin");
        entry.TargetId.Should().Be(adminId.ToString());
        entry.IpAddress.Should().Be("192.168.1.1");
    }
}
```

- [ ] **Step 5: RED — derleme hatası bekle**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AuditServiceTests" 2>&1 | tail -5
```

Beklenen: derleme hatası — `AuditService`/`AuditEvents`/`AuditTargets`/`AuditLogs` (DbSet) yok.

- [ ] **Step 6: AuditService impl**

`LiveDeck.LicenseServer/Services/Audit/AuditService.cs`:

```csharp
using System.Security.Claims;
using System.Text.Json;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Http;

namespace LiveDeck.LicenseServer.Services.Audit;

public sealed class AuditService : IAuditService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly LicenseDbContext _db;
    private readonly IHttpContextAccessor _httpContext;

    public AuditService(LicenseDbContext db, IHttpContextAccessor httpContext)
    {
        _db = db;
        _httpContext = httpContext;
    }

    public Task LogAsync(string eventType, string targetType, string? targetId, object? details = null, CancellationToken ct = default)
    {
        var ctx = _httpContext.HttpContext
            ?? throw new InvalidOperationException("LogAsync requires HttpContext (use LogLoginAsync/LogLogoutAsync outside request scope).");

        var sub = ctx.User.FindFirst("sub")?.Value
            ?? ctx.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("Audit LogAsync requires authenticated User with 'sub' claim.");
        var username = ctx.User.FindFirst("username")?.Value
            ?? ctx.User.FindFirst(ClaimTypes.Name)?.Value
            ?? "(unknown)";
        var ip = ctx.Connection.RemoteIpAddress?.ToString();

        return WriteAsync(Guid.Parse(sub), username, eventType, targetType, targetId, details, ip, ct);
    }

    public Task LogLoginAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default) =>
        WriteAsync(adminId, username, AuditEvents.AdminLogin, AuditTargets.Admin, adminId.ToString(), null, ipAddress, ct);

    public Task LogLogoutAsync(Guid adminId, string username, string? ipAddress, CancellationToken ct = default) =>
        WriteAsync(adminId, username, AuditEvents.AdminLogout, AuditTargets.Admin, adminId.ToString(), null, ipAddress, ct);

    private async Task WriteAsync(
        Guid adminId, string username,
        string eventType, string targetType, string? targetId,
        object? details, string? ip, CancellationToken ct)
    {
        var entry = new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            AdminId = adminId,
            AdminUsername = username,
            EventType = eventType,
            TargetType = targetType,
            TargetId = targetId,
            Details = details is null ? null : JsonSerializer.Serialize(details, JsonOpts),
            IpAddress = ip
        };
        _db.AuditLogs.Add(entry);
        await _db.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 7: LicenseDbContext'e AuditLogs DbSet + FluentAPI**

`LiveDeck.LicenseServer/Data/LicenseDbContext.cs` mevcut DbContext'i aç. Mevcut `DbSet<EmailConfirmationToken> EmailConfirmationTokens` satırının altına ekle:

```csharp
    public DbSet<AuditLogEntry> AuditLogs => Set<AuditLogEntry>();
```

`OnModelCreating` metodunun **sonuna**, mevcut `mb.Entity<Sku>().HasData(...)` satırından **önce** ekle:

```csharp
        mb.Entity<AuditLogEntry>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.AdminUsername).HasMaxLength(64).IsRequired();
            b.Property(a => a.EventType).HasMaxLength(64).IsRequired();
            b.Property(a => a.TargetType).HasMaxLength(32).IsRequired();
            b.Property(a => a.TargetId).HasMaxLength(64);
            b.Property(a => a.Details).HasMaxLength(4000);
            b.Property(a => a.IpAddress).HasMaxLength(64);
            b.HasIndex(a => a.OccurredAt);
            b.HasIndex(a => new { a.AdminId, a.OccurredAt });
            b.HasIndex(a => new { a.TargetType, a.TargetId });
        });
```

`using LiveDeck.LicenseServer.Domain;` var olduğunu doğrula (Phase 4a Task 2'de eklenmiş olmalı).

- [ ] **Step 8: EF migration oluştur**

```bash
cd /c/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer
dotnet ef migrations add AddAuditLog --output-dir Data/Migrations
```

Beklenen: `Data/Migrations/{timestamp}_AddAuditLog.cs`, `.Designer.cs`, ve `LicenseDbContextModelSnapshot.cs` güncellenir. Build clean.

`AuditLogs` tablosu, 9 kolon, 3 index migration'da görünmeli. Auto-generated kodu manuel düzenleme.

- [ ] **Step 9: Program.cs DI registration**

`LiveDeck.LicenseServer/Program.cs`'in `// Services` bloğuna (Phase 4a kayıtlarının yanına) ekle:

```csharp
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Audit.IAuditService,
                                    LiveDeck.LicenseServer.Services.Audit.AuditService>();
```

- [ ] **Step 10: GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AuditServiceTests" 2>&1 | tail -5
```

Beklenen: 0 errors / 0 warnings. 4/4 PASS.

- [ ] **Step 11: Tüm LicenseServer testleri (regression)**

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 67 pass + 1 skip = 68 total (63 baseline + 4 yeni + 1 skip placeholder).

- [ ] **Step 12: Commit**

```bash
git add LiveDeck.LicenseServer/Domain/AuditLogEntry.cs \
        LiveDeck.LicenseServer/Services/Audit/ \
        LiveDeck.LicenseServer/Data/LicenseDbContext.cs \
        LiveDeck.LicenseServer/Data/Migrations/ \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Services/AuditServiceTests.cs
git commit -m "feat(license-server): add AuditLogEntry + AuditService + EF migration"
```

---

### Task 3: Login + Logout pages + AntiForgery test helper

**Files:**
- Modify: `LiveDeck.LicenseServer/Pages/Admin/Login.cshtml`
- Modify: `LiveDeck.LicenseServer/Pages/Admin/Login.cshtml.cs`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Logout.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/TestHelpers/AdminLoginHelper.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminAuthFlowTests.cs`

**Context:** Login form: username + password + AntiForgery token. POST handler verify (PasswordHasher) → SignInAsync (cookie) → LogLoginAsync → redirect (returnUrl veya `/admin/`). Logout handler: LogLogoutAsync → SignOutAsync → redirect login. Test helper: anti-forgery token + cookie capture pattern.

- [ ] **Step 1: Login.cshtml — gerçek form**

`LiveDeck.LicenseServer/Pages/Admin/Login.cshtml` dosyasını **tamamen** değiştir:

```cshtml
@page
@model LoginModel
@{
    ViewData["Title"] = "Giriş";
    Layout = null;
}
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Giriş — LiveDeck Admin</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
</head>
<body class="bg-light">
    <main class="container py-5" style="max-width: 400px;">
        <h1 class="h3 mb-4">LiveDeck Admin</h1>
        @if (!string.IsNullOrEmpty(Model.ErrorMessage))
        {
            <div class="alert alert-danger">@Model.ErrorMessage</div>
        }
        <form method="post">
            <input type="hidden" asp-for="ReturnUrl" />
            <div class="mb-3">
                <label asp-for="Input.Username" class="form-label">Kullanıcı adı</label>
                <input asp-for="Input.Username" class="form-control" autofocus />
                <span asp-validation-for="Input.Username" class="text-danger"></span>
            </div>
            <div class="mb-3">
                <label asp-for="Input.Password" class="form-label">Şifre</label>
                <input asp-for="Input.Password" type="password" class="form-control" />
                <span asp-validation-for="Input.Password" class="text-danger"></span>
            </div>
            <button type="submit" class="btn btn-primary w-100">Giriş yap</button>
        </form>
    </main>
</body>
</html>
```

- [ ] **Step 2: Login.cshtml.cs — gerçek handler**

`LiveDeck.LicenseServer/Pages/Admin/Login.cshtml.cs` dosyasını **tamamen** değiştir:

```csharp
using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin;

public class LoginModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly IAuditService _audit;

    public LoginModel(LicenseDbContext db, PasswordHasher hasher, IAuditService audit)
    {
        _db = db;
        _hasher = hasher;
        _audit = audit;
    }

    [BindProperty]
    public LoginInput Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string? ErrorMessage { get; set; }

    public sealed class LoginInput
    {
        [Required(ErrorMessage = "Kullanıcı adı gerekli")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Şifre gerekli")]
        public string Password { get; set; } = "";
    }

    public void OnGet() { }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var admin = await _db.AdminUsers.FirstOrDefaultAsync(a => a.Username == Input.Username, ct);
        if (admin is null || !_hasher.Verify(admin.PasswordHash, Input.Password))
        {
            ErrorMessage = "Geçersiz kullanıcı adı veya şifre.";
            return Page();
        }

        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var claims = new[]
        {
            new Claim("sub", admin.Id.ToString()),
            new Claim("username", admin.Username)
        };
        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
        var principal = new ClaimsPrincipal(identity);
        await HttpContext.SignInAsync("AdminCookie", principal);

        var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
        await _audit.LogLoginAsync(admin.Id, admin.Username, ip, ct);

        if (!string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl))
            return LocalRedirect(ReturnUrl);
        return RedirectToPage("/Admin/Index");
    }
}
```

- [ ] **Step 3: Logout handler**

`LiveDeck.LicenseServer/Pages/Admin/Logout.cshtml.cs`:

```csharp
using System.Security.Claims;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiveDeck.LicenseServer.Pages.Admin;

public class LogoutModel : PageModel
{
    private readonly IAuditService _audit;

    public LogoutModel(IAuditService audit) => _audit = audit;

    public IActionResult OnGet() => RedirectToPage("/Admin/Login");

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        var sub = User.FindFirst("sub")?.Value;
        var username = User.FindFirst("username")?.Value;
        if (Guid.TryParse(sub, out var adminId) && !string.IsNullOrEmpty(username))
        {
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString();
            await _audit.LogLogoutAsync(adminId, username, ip, ct);
        }
        await HttpContext.SignOutAsync("AdminCookie");
        return RedirectToPage("/Admin/Login");
    }
}
```

`Logout.cshtml` (handler-only sayfaya GET için boş razor file):

`LiveDeck.LicenseServer/Pages/Admin/Logout.cshtml`:

```cshtml
@page
@model LogoutModel
```

- [ ] **Step 4: AdminLoginHelper test infra**

`LiveDeck.LicenseServer.Tests/TestHelpers/AdminLoginHelper.cs`:

```csharp
using System.Net.Http.Headers;
using AngleSharp;
using AngleSharp.Html.Dom;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public static class AdminLoginHelper
{
    /// <summary>
    /// Creates an admin user (if missing) with a known password, then performs an
    /// anti-forgery-aware login POST. Returns an HttpClient with the auth cookie set.
    /// </summary>
    public static async Task<HttpClient> CreateLoggedInAdminClientAsync(
        this ApiFactory factory,
        string username = "admin",
        string password = "admin-password")
    {
        await EnsureAdminSeededAsync(factory, username, password);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // GET login → grab anti-forgery token
        var getResp = await client.GetAsync("/admin/login");
        var html = await getResp.Content.ReadAsStringAsync();
        var token = ExtractAntiForgeryToken(html);

        // POST login
        var formData = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = password
        });
        var postResp = await client.PostAsync("/admin/login", formData);
        if (postResp.StatusCode != System.Net.HttpStatusCode.Redirect)
        {
            var body = await postResp.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Login failed (status={postResp.StatusCode}): {body}");
        }

        return client;
    }

    public static string ExtractAntiForgeryToken(string html)
    {
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = ctx.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        var input = doc.QuerySelector("input[name='__RequestVerificationToken']") as IHtmlInputElement;
        return input?.Value ?? throw new InvalidOperationException("No anti-forgery token found in HTML.");
    }

    /// <summary>Idempotently creates an admin user with the given password.</summary>
    public static async Task EnsureAdminSeededAsync(ApiFactory factory, string username, string password)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var existing = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (existing is not null) return;
        db.AdminUsers.Add(new AdminUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hasher.Hash(password),
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
}
```

- [ ] **Step 5: AdminAuthFlowTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminAuthFlowTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminAuthFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminAuthFlowTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_login_returns_200_with_form()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/admin/login");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("__RequestVerificationToken");
        html.Should().Contain("Input.Username");
        html.Should().Contain("Input.Password");
    }

    [Fact]
    public async Task Post_login_with_valid_credentials_sets_cookie_and_redirects()
    {
        var username = $"admin-{Guid.NewGuid():N}";
        await AdminLoginHelper.EnsureAdminSeededAsync(_factory, username, "admin-password");

        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = "admin-password"
        });
        var postResp = await client.PostAsync("/admin/login", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Post_login_with_wrong_password_returns_200_with_error()
    {
        var username = $"admin-{Guid.NewGuid():N}";
        await AdminLoginHelper.EnsureAdminSeededAsync(_factory, username, "real-password");

        var client = _factory.CreateClient();
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.Username"] = username,
            ["Input.Password"] = "WRONG"
        });
        var postResp = await client.PostAsync("/admin/login", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Geçersiz");
    }

    [Fact]
    public async Task Anonymous_request_to_admin_index_redirects_to_login()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/admin");
        // 404 if Index page yet (Task 4); accept either 302 OR 404 for now.
        // After Task 4: must be 302 with Location starting "/admin/login"
        if (resp.StatusCode == HttpStatusCode.Redirect)
        {
            resp.Headers.Location!.ToString().Should().StartWith("/admin/login");
        }
        else
        {
            resp.StatusCode.Should().Be(HttpStatusCode.NotFound);   // Index page coming in Task 4
        }
    }

    [Fact]
    public async Task Successful_login_then_logout_writes_audit_entries()
    {
        var username = $"admin-{Guid.NewGuid():N}";
        var client = await _factory.CreateLoggedInAdminClientAsync(username, "admin-password");

        // After login, audit log should have 'admin.login'
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var loginEntry = await db.AuditLogs
                .Where(a => a.AdminUsername == username && a.EventType == "admin.login")
                .OrderByDescending(a => a.OccurredAt)
                .FirstOrDefaultAsync();
            loginEntry.Should().NotBeNull();
        }

        // POST logout (need anti-forgery token from any GET that renders form — re-use login GET)
        var getResp = await client.GetAsync("/admin/login");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
        var logoutResp = await client.PostAsync("/admin/logout", form);
        logoutResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var logoutEntry = await db.AuditLogs
                .Where(a => a.AdminUsername == username && a.EventType == "admin.logout")
                .OrderByDescending(a => a.OccurredAt)
                .FirstOrDefaultAsync();
            logoutEntry.Should().NotBeNull();
        }
    }
}
```

- [ ] **Step 6: AdminLoginPlaceholderTests temizle**

Task 1'de eklediğimiz `AdminLoginPlaceholderTests.cs` artık AdminAuthFlowTests tarafından replace edildi. Sil:

```bash
rm LiveDeck.LicenseServer.Tests/Pages/AdminLoginPlaceholderTests.cs
```

- [ ] **Step 7: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminAuthFlowTests" 2>&1 | tail -5
```

Beklenen: 0 errors. 5/5 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 71/71 (62 baseline + 4 audit + 5 auth). Skip placeholder kalktı.

- [ ] **Step 8: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Login.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Login.cshtml.cs \
        LiveDeck.LicenseServer/Pages/Admin/Logout.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Logout.cshtml.cs \
        LiveDeck.LicenseServer.Tests/TestHelpers/AdminLoginHelper.cs \
        LiveDeck.LicenseServer.Tests/Pages/AdminAuthFlowTests.cs
git rm LiveDeck.LicenseServer.Tests/Pages/AdminLoginPlaceholderTests.cs
git commit -m "feat(license-server): add Login + Logout pages with cookie auth + audit"
```

---

### Task 4: Dashboard (Index) page

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Index.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Index.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminDashboardTests.cs`

**Context:** 4 sayım: total customers, active licenses, expired licenses, active activations. Tek sayfa, formless. AdminLayout sidebar'a sahip (Task 11 stilizasyonundan önce minimal görünüm).

- [ ] **Step 1: Index.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Index.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;

    public IndexModel(LicenseDbContext db) => _db = db;

    public int TotalCustomers { get; private set; }
    public int ActiveLicenses { get; private set; }
    public int ExpiredOrRevokedLicenses { get; private set; }
    public int ActiveActivations { get; private set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        TotalCustomers = await _db.Customers.CountAsync(ct);
        ActiveLicenses = await _db.Licenses.CountAsync(l => l.RevokedAt == null && l.ExpiresAt > now, ct);
        ExpiredOrRevokedLicenses = await _db.Licenses.CountAsync(l => l.RevokedAt != null || l.ExpiresAt <= now, ct);
        ActiveActivations = await _db.Activations.CountAsync(a => a.DeactivatedAt == null, ct);
    }
}
```

- [ ] **Step 2: Index.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Index.cshtml`:

```cshtml
@page
@model IndexModel
@{
    ViewData["Title"] = "Dashboard";
}
<h1 class="h3 mb-4">Dashboard</h1>
<div class="row g-3">
    <div class="col-md-3">
        <div class="card text-bg-primary">
            <div class="card-body">
                <h6 class="card-subtitle mb-2 text-white-50">Toplam Müşteri</h6>
                <p class="card-text display-6" data-stat="customers">@Model.TotalCustomers</p>
            </div>
        </div>
    </div>
    <div class="col-md-3">
        <div class="card text-bg-success">
            <div class="card-body">
                <h6 class="card-subtitle mb-2 text-white-50">Aktif Lisans</h6>
                <p class="card-text display-6" data-stat="active-licenses">@Model.ActiveLicenses</p>
            </div>
        </div>
    </div>
    <div class="col-md-3">
        <div class="card text-bg-warning">
            <div class="card-body">
                <h6 class="card-subtitle mb-2 text-white-50">İptal/Süresi Dolmuş</h6>
                <p class="card-text display-6" data-stat="expired-licenses">@Model.ExpiredOrRevokedLicenses</p>
            </div>
        </div>
    </div>
    <div class="col-md-3">
        <div class="card text-bg-info">
            <div class="card-body">
                <h6 class="card-subtitle mb-2 text-white-50">Aktif Aktivasyon</h6>
                <p class="card-text display-6" data-stat="active-activations">@Model.ActiveActivations</p>
            </div>
        </div>
    </div>
</div>
```

- [ ] **Step 3: AdminDashboardTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminDashboardTests.cs`:

```csharp
using System.Net;
using AngleSharp;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminDashboardTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminDashboardTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_redirects_to_login()
    {
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/admin");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().StartWith("/admin/login");
    }

    [Fact]
    public async Task Logged_in_dashboard_renders_four_counters_with_real_values()
    {
        // Seed: 2 customers, 1 active license, 1 revoked license, 0 active activations
        var customer1Id = Guid.NewGuid();
        var customer2Id = Guid.NewGuid();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Customers.AddRange(
                new Customer { Id = customer1Id, Email = $"c1-{Guid.NewGuid():N}@x", Name = "C1", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow },
                new Customer { Id = customer2Id, Email = $"c2-{Guid.NewGuid():N}@x", Name = "C2", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
            db.Licenses.AddRange(
                new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DASH-A-" + Guid.NewGuid().ToString("N"), CustomerId = customer1Id, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) },
                new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DASH-B-" + Guid.NewGuid().ToString("N"), CustomerId = customer1Id, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow.AddDays(-100), ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), RevokedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = await ctx.OpenAsync(req => req.Content(html));

        // Counter assertions are >= 1 because tests share the InMemory DB across the class fixture
        var customers = int.Parse(doc.QuerySelector("[data-stat='customers']")!.TextContent.Trim());
        var active = int.Parse(doc.QuerySelector("[data-stat='active-licenses']")!.TextContent.Trim());
        var expired = int.Parse(doc.QuerySelector("[data-stat='expired-licenses']")!.TextContent.Trim());

        customers.Should().BeGreaterThanOrEqualTo(2);
        active.Should().BeGreaterThanOrEqualTo(1);
        expired.Should().BeGreaterThanOrEqualTo(1);
    }
}
```

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminDashboardTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 2/2 + 73/73 toplam.

- [ ] **Step 5: AdminAuthFlowTests'in skip kalkmış varyasyonu**

Şimdi `/admin` Index sayfası var → `Anonymous_request_to_admin_index_redirects_to_login` testindeki "404 fallback" kolu artık gerek yok. Test'i sadeleştir:

`LiveDeck.LicenseServer.Tests/Pages/AdminAuthFlowTests.cs` içinde `Anonymous_request_to_admin_index_redirects_to_login` test'ini bul ve gövdesini şununla değiştir:

```csharp
    [Fact]
    public async Task Anonymous_request_to_admin_index_redirects_to_login()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/admin");
        resp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        resp.Headers.Location!.ToString().Should().StartWith("/admin/login");
    }
```

Test'leri tekrar çalıştır:
```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminAuthFlowTests" 2>&1 | tail -5
```
Beklenen: 5/5 yine geçer.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Index.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Index.cshtml.cs \
        LiveDeck.LicenseServer.Tests/Pages/AdminDashboardTests.cs \
        LiveDeck.LicenseServer.Tests/Pages/AdminAuthFlowTests.cs
git commit -m "feat(license-server): add admin dashboard with 4 counters"
```

---

### Task 5: Customers — Index + Detail + ConfirmEmail handler

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Customers/Index.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Customers/Index.cshtml.cs`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminCustomersPageTests.cs`

**Context:** Index: customer listesi, ?search=email, ?page=N (pageSize=25). Detail: profil + lisanslar + son 20 audit. ConfirmEmail handler EmailConfirmedAt set + audit.

- [ ] **Step 1: Customers/Index.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Customers/Index.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Customers;

public class IndexModel : PageModel
{
    private const int PageSize = 25;
    private readonly LicenseDbContext _db;

    public IndexModel(LicenseDbContext db) => _db = db;

    public List<Customer> Customers { get; private set; } = new();
    public string? Search { get; private set; }
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }

    public async Task OnGetAsync(string? search, int page, CancellationToken ct)
    {
        Search = search;
        CurrentPage = page < 1 ? 1 : page;

        var query = _db.Customers.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            query = query.Where(c => c.Email.Contains(search));

        var total = await query.CountAsync(ct);
        TotalPages = (int)Math.Ceiling(total / (double)PageSize);

        Customers = await query
            .OrderByDescending(c => c.CreatedAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 2: Customers/Index.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Customers/Index.cshtml`:

```cshtml
@page
@model IndexModel
@{
    ViewData["Title"] = "Müşteriler";
}
<h1 class="h3 mb-4">Müşteriler</h1>

<form method="get" class="mb-3">
    <div class="input-group" style="max-width: 400px;">
        <input type="text" name="search" class="form-control" placeholder="E-posta ara..." value="@Model.Search" />
        <button type="submit" class="btn btn-outline-primary">Ara</button>
    </div>
</form>

@if (!Model.Customers.Any())
{
    <p class="text-muted">Müşteri bulunamadı.</p>
}
else
{
    <table class="table table-hover" data-table="customers">
        <thead>
            <tr>
                <th>E-posta</th>
                <th>Ad Soyad</th>
                <th>Doğrulanmış</th>
                <th>Kayıt Tarihi</th>
                <th></th>
            </tr>
        </thead>
        <tbody>
            @foreach (var c in Model.Customers)
            {
                <tr>
                    <td class="email">@c.Email</td>
                    <td>@c.Name</td>
                    <td>@(c.EmailConfirmedAt is null ? "Hayır" : "Evet")</td>
                    <td>@c.CreatedAt.ToString("dd.MM.yyyy")</td>
                    <td><a asp-page="./Detail" asp-route-id="@c.Id" class="btn btn-sm btn-outline-secondary">Detay</a></td>
                </tr>
            }
        </tbody>
    </table>

    @if (Model.TotalPages > 1)
    {
        <nav>
            <ul class="pagination">
                @if (Model.CurrentPage > 1)
                {
                    <li class="page-item"><a class="page-link" asp-route-search="@Model.Search" asp-route-page="@(Model.CurrentPage - 1)">Önceki</a></li>
                }
                <li class="page-item active"><span class="page-link">@Model.CurrentPage / @Model.TotalPages</span></li>
                @if (Model.CurrentPage < Model.TotalPages)
                {
                    <li class="page-item"><a class="page-link" asp-route-search="@Model.Search" asp-route-page="@(Model.CurrentPage + 1)">Sonraki</a></li>
                }
            </ul>
        </nav>
    }
}
```

- [ ] **Step 3: Customers/Detail.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Customers;

public class DetailModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly IAuditService _audit;

    public DetailModel(LicenseDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public Customer? Customer { get; private set; }
    public List<License> Licenses { get; private set; } = new();
    public List<AuditLogEntry> AuditEntries { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(Guid id, CancellationToken ct)
    {
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (Customer is null) return NotFound();

        Licenses = await _db.Licenses
            .Where(l => l.CustomerId == id)
            .OrderByDescending(l => l.IssuedAt)
            .ToListAsync(ct);

        var licenseKeys = Licenses.Select(l => l.LicenseKey).ToList();
        AuditEntries = await _db.AuditLogs
            .Where(a => (a.TargetType == "customer" && a.TargetId == id.ToString())
                     || (a.TargetType == "license" && licenseKeys.Contains(a.TargetId)))
            .OrderByDescending(a => a.OccurredAt)
            .Take(20)
            .ToListAsync(ct);

        return Page();
    }

    public async Task<IActionResult> OnPostConfirmEmailAsync(Guid id, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (customer is null) return NotFound();
        if (customer.EmailConfirmedAt is null)
        {
            customer.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            await _audit.LogAsync(AuditEvents.CustomerConfirmEmail, AuditTargets.Customer, id.ToString(), null, ct);
            TempData["Success"] = "Müşteri e-posta adresi doğrulandı.";
        }
        return RedirectToPage("./Detail", new { id });
    }
}
```

- [ ] **Step 4: Customers/Detail.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml`:

```cshtml
@page "{id:guid}"
@model DetailModel
@{
    ViewData["Title"] = "Müşteri Detayı";
}
@if (TempData["Success"] is string okMsg)
{
    <div class="alert alert-success">@okMsg</div>
}
<h1 class="h3 mb-4">@Model.Customer!.Name</h1>

<div class="row mb-4">
    <div class="col-md-6">
        <h5>Profil</h5>
        <table class="table">
            <tr><th>E-posta</th><td>@Model.Customer.Email</td></tr>
            <tr><th>Ad Soyad</th><td>@Model.Customer.Name</td></tr>
            <tr>
                <th>Doğrulama</th>
                <td>
                    @if (Model.Customer.EmailConfirmedAt is null)
                    {
                        <form method="post" asp-page-handler="ConfirmEmail" asp-route-id="@Model.Customer.Id" class="d-inline">
                            <button type="submit" class="btn btn-sm btn-warning">E-postayı doğrula</button>
                        </form>
                    }
                    else
                    {
                        <span>Evet (@Model.Customer.EmailConfirmedAt.Value.ToString("dd.MM.yyyy"))</span>
                    }
                </td>
            </tr>
            <tr><th>Kayıt</th><td>@Model.Customer.CreatedAt.ToString("dd.MM.yyyy HH:mm")</td></tr>
        </table>
    </div>
</div>

<h5>Lisanslar</h5>
@if (!Model.Licenses.Any())
{
    <p class="text-muted">Bu müşteriye henüz lisans verilmedi.</p>
    <a asp-page="/Admin/Licenses/Issue" asp-route-customerEmail="@Model.Customer.Email" class="btn btn-primary btn-sm">Yeni lisans ihraç et</a>
}
else
{
    <table class="table" data-table="licenses">
        <thead><tr><th>Anahtar</th><th>SKU</th><th>Bitiş</th><th>Durum</th><th></th></tr></thead>
        <tbody>
            @foreach (var l in Model.Licenses)
            {
                <tr>
                    <td><code>@l.LicenseKey</code></td>
                    <td>@l.SkuCode</td>
                    <td>@l.ExpiresAt.ToString("dd.MM.yyyy")</td>
                    <td>
                        @if (l.RevokedAt is not null) { <span class="badge text-bg-danger">İptal</span> }
                        else if (l.ExpiresAt < DateTimeOffset.UtcNow) { <span class="badge text-bg-warning">Süresi Doldu</span> }
                        else { <span class="badge text-bg-success">Aktif</span> }
                    </td>
                    <td><a asp-page="/Admin/Licenses/Detail" asp-route-key="@l.LicenseKey" class="btn btn-sm btn-outline-secondary">Detay</a></td>
                </tr>
            }
        </tbody>
    </table>
}

<h5 class="mt-4">Son Audit Olayları</h5>
@if (!Model.AuditEntries.Any())
{
    <p class="text-muted">Henüz audit kaydı yok.</p>
}
else
{
    <table class="table table-sm">
        <thead><tr><th>Zaman</th><th>Olay</th><th>Hedef</th><th>Admin</th></tr></thead>
        <tbody>
            @foreach (var a in Model.AuditEntries)
            {
                <tr>
                    <td>@a.OccurredAt.ToString("dd.MM HH:mm")</td>
                    <td>@a.EventType</td>
                    <td>@a.TargetType / @a.TargetId</td>
                    <td>@a.AdminUsername</td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 5: AdminCustomersPageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminCustomersPageTests.cs`:

```csharp
using System.Net;
using AngleSharp;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminCustomersPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminCustomersPageTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedCustomerAsync(string email, string name = "Test Customer")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer { Id = Guid.NewGuid(), Email = email, Name = name, PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c.Id;
    }

    [Fact]
    public async Task Index_lists_customers()
    {
        var email = $"list-{Guid.NewGuid():N}@x";
        await SeedCustomerAsync(email);
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var resp = await client.GetAsync("/admin/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(email);
    }

    [Fact]
    public async Task Index_search_filters_to_matching_email()
    {
        var unique = "find-" + Guid.NewGuid().ToString("N");
        await SeedCustomerAsync(unique + "@x");
        await SeedCustomerAsync($"other-{Guid.NewGuid():N}@x");
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var resp = await client.GetAsync($"/admin/customers?search={unique}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
        var rows = doc.QuerySelectorAll("table[data-table='customers'] tbody tr");
        rows.Length.Should().Be(1);
    }

    [Fact]
    public async Task Detail_shows_customer_with_licenses()
    {
        var custId = await SeedCustomerAsync($"detail-{Guid.NewGuid():N}@x", "Detail Customer");
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Licenses.Add(new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DET-" + Guid.NewGuid().ToString("N"), CustomerId = custId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) });
            await db.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync($"/admin/customers/{custId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Detail Customer");
        html.Should().Contain("LDK-DET-");
    }

    [Fact]
    public async Task ConfirmEmail_sets_EmailConfirmedAt_and_writes_audit()
    {
        var custId = await SeedCustomerAsync($"confirm-{Guid.NewGuid():N}@x");
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        // GET detail to grab anti-forgery token
        var getResp = await client.GetAsync($"/admin/customers/{custId}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
        var postResp = await client.PostAsync($"/admin/customers/{custId}?handler=ConfirmEmail", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = await db.Customers.FirstAsync(c => c.Id == custId);
        customer.EmailConfirmedAt.Should().NotBeNull();

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "customer.confirm-email" && a.TargetId == custId.ToString())
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
    }
}
```

- [ ] **Step 6: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminCustomersPageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 4/4 + 77/77 toplam.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Customers/ \
        LiveDeck.LicenseServer.Tests/Pages/AdminCustomersPageTests.cs
git commit -m "feat(license-server): add admin customers list + detail + confirm-email"
```

---

### Task 6: Licenses — Issue page (form + handler + audit)

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminLicensesIssueTests.cs`

**Context:** Form: customer email + sku (select STD/PRO) + override duration/slots. POST: `LicenseIssuer.IssueAsync` (Phase 4a internal service) → audit `license.issue` → redirect detail. Validation errors form üstünde gösterilir.

- [ ] **Step 1: Issue.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using LiveDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Licenses;

public class IssueModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly LicenseIssuer _issuer;
    private readonly IAuditService _audit;

    public IssueModel(LicenseDbContext db, LicenseIssuer issuer, IAuditService audit)
    {
        _db = db;
        _issuer = issuer;
        _audit = audit;
    }

    [BindProperty]
    public IssueInput Input { get; set; } = new();

    public List<Sku> Skus { get; private set; } = new();
    public string? ErrorMessage { get; set; }

    public sealed class IssueInput
    {
        [Required(ErrorMessage = "E-posta gerekli")]
        [EmailAddress(ErrorMessage = "Geçerli e-posta gir")]
        public string CustomerEmail { get; set; } = "";

        [Required(ErrorMessage = "SKU seç")]
        public string SkuCode { get; set; } = "";

        [Range(1, 3650, ErrorMessage = "1-3650 arası gün")]
        public int? DurationDaysOverride { get; set; }

        [Range(1, 100, ErrorMessage = "1-100 arası slot")]
        public int? SlotsOverride { get; set; }
    }

    public async Task OnGetAsync(string? customerEmail, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(customerEmail))
            Input.CustomerEmail = customerEmail;
        Skus = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        Skus = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
        if (!ModelState.IsValid) return Page();

        try
        {
            var result = await _issuer.IssueAsync(
                new LicenseIssuer.IssueRequest(Input.CustomerEmail, Input.SkuCode, Input.DurationDaysOverride, Input.SlotsOverride),
                ct);

            await _audit.LogAsync(AuditEvents.LicenseIssue, AuditTargets.License, result.LicenseKey,
                new { customerEmail = Input.CustomerEmail, skuCode = Input.SkuCode, durationDaysOverride = Input.DurationDaysOverride, slotsOverride = Input.SlotsOverride },
                ct);

            TempData["Success"] = $"Lisans oluşturuldu: {result.LicenseKey}";
            return RedirectToPage("./Detail", new { key = result.LicenseKey });
        }
        catch (LicenseIssuer.IssueException ex)
        {
            ErrorMessage = ex.Message;
            return Page();
        }
    }
}
```

- [ ] **Step 2: Issue.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml`:

```cshtml
@page
@model IssueModel
@{
    ViewData["Title"] = "Yeni Lisans";
}
<h1 class="h3 mb-4">Yeni Lisans İhraç</h1>

@if (!string.IsNullOrEmpty(Model.ErrorMessage))
{
    <div class="alert alert-danger">@Model.ErrorMessage</div>
}

<form method="post" style="max-width: 600px;">
    <div class="mb-3">
        <label asp-for="Input.CustomerEmail" class="form-label">Müşteri E-postası</label>
        <input asp-for="Input.CustomerEmail" class="form-control" />
        <span asp-validation-for="Input.CustomerEmail" class="text-danger"></span>
    </div>

    <div class="mb-3">
        <label asp-for="Input.SkuCode" class="form-label">SKU</label>
        <select asp-for="Input.SkuCode" class="form-select">
            <option value="">— Seç —</option>
            @foreach (var sku in Model.Skus)
            {
                <option value="@sku.Code">@sku.Code — @sku.DisplayName (@sku.DefaultDurationDays gün, @sku.DefaultActivationSlots slot)</option>
            }
        </select>
        <span asp-validation-for="Input.SkuCode" class="text-danger"></span>
    </div>

    <div class="row">
        <div class="col-md-6 mb-3">
            <label asp-for="Input.DurationDaysOverride" class="form-label">Süre (gün, opsiyonel override)</label>
            <input asp-for="Input.DurationDaysOverride" type="number" class="form-control" placeholder="SKU varsayılanını kullan" />
            <span asp-validation-for="Input.DurationDaysOverride" class="text-danger"></span>
        </div>
        <div class="col-md-6 mb-3">
            <label asp-for="Input.SlotsOverride" class="form-label">Slot (opsiyonel override)</label>
            <input asp-for="Input.SlotsOverride" type="number" class="form-control" placeholder="SKU varsayılanını kullan" />
            <span asp-validation-for="Input.SlotsOverride" class="text-danger"></span>
        </div>
    </div>

    <button type="submit" class="btn btn-primary">İhraç Et</button>
    <a asp-page="/Admin/Index" class="btn btn-link">İptal</a>
</form>
```

- [ ] **Step 3: AdminLicensesIssueTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminLicensesIssueTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminLicensesIssueTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminLicensesIssueTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Get_issue_form_lists_skus()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/licenses/issue");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("STD");
        html.Should().Contain("PRO");
    }

    [Fact]
    public async Task Post_issue_creates_license_audit_and_redirects()
    {
        var custEmail = $"issue-{Guid.NewGuid():N}@x";
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Customers.Add(new Customer { Id = Guid.NewGuid(), Email = custEmail, Name = "I", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var getResp = await client.GetAsync("/admin/licenses/issue");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.CustomerEmail"] = custEmail,
            ["Input.SkuCode"] = "STD"
        });
        var postResp = await client.PostAsync("/admin/licenses/issue", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResp.Headers.Location!.ToString().Should().Contain("/admin/licenses/LDK-");

        using var scope2 = _factory.Services.CreateScope();
        var db2 = scope2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = await db2.Customers.FirstAsync(c => c.Email == custEmail);
        var license = await db2.Licenses.FirstAsync(l => l.CustomerId == customer.Id);
        license.SkuCode.Should().Be("STD");

        var audit = await db2.AuditLogs
            .Where(a => a.EventType == "license.issue" && a.TargetId == license.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Details.Should().Contain(custEmail);
    }

    [Fact]
    public async Task Post_issue_with_unknown_customer_shows_error()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var getResp = await client.GetAsync("/admin/licenses/issue");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.CustomerEmail"] = "nope@x.com",
            ["Input.SkuCode"] = "STD"
        });
        var postResp = await client.PostAsync("/admin/licenses/issue", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Email yok");
    }
}
```

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminLicensesIssueTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 3/3 + 80/80 toplam.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml.cs \
        LiveDeck.LicenseServer.Tests/Pages/AdminLicensesIssueTests.cs
git commit -m "feat(license-server): add admin license issue page + audit"
```

---

### Task 7: Licenses — Detail page + Revoke + Extend handlers

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminLicensesDetailTests.cs`

**Context:** Detail: lisans + customer + activations + son audit + Revoke form + Extend form. Handler'lar audit + redirect detail.

- [ ] **Step 1: Detail.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Licenses;

public class DetailModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly IAuditService _audit;

    public DetailModel(LicenseDbContext db, IAuditService audit)
    {
        _db = db;
        _audit = audit;
    }

    public License? License { get; private set; }
    public Customer? Customer { get; private set; }
    public List<Activation> Activations { get; private set; } = new();
    public List<AuditLogEntry> AuditEntries { get; private set; } = new();

    [BindProperty]
    public RevokeInput RevokeForm { get; set; } = new();

    [BindProperty]
    public ExtendInput ExtendForm { get; set; } = new();

    public sealed class RevokeInput
    {
        [Required(ErrorMessage = "Sebep gerekli")]
        [StringLength(500)]
        public string Reason { get; set; } = "";
    }

    public sealed class ExtendInput
    {
        [Required, Range(1, 3650)]
        public int AdditionalDays { get; set; } = 30;
    }

    public async Task<IActionResult> OnGetAsync(string key, CancellationToken ct)
    {
        return await LoadAsync(key, ct);
    }

    public async Task<IActionResult> OnPostRevokeAsync(string key, CancellationToken ct)
    {
        var loadResult = await LoadAsync(key, ct);
        if (License is null) return loadResult;
        if (!ModelState.IsValid) return Page();

        License.RevokedAt = DateTimeOffset.UtcNow;
        License.RevokeReason = RevokeForm.Reason;
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditEvents.LicenseRevoke, AuditTargets.License, key,
            new { reason = RevokeForm.Reason }, ct);

        TempData["Success"] = "Lisans iptal edildi.";
        return RedirectToPage("./Detail", new { key });
    }

    public async Task<IActionResult> OnPostExtendAsync(string key, CancellationToken ct)
    {
        var loadResult = await LoadAsync(key, ct);
        if (License is null) return loadResult;
        if (!ModelState.IsValid) return Page();

        License.ExpiresAt = License.ExpiresAt.AddDays(ExtendForm.AdditionalDays);
        await _db.SaveChangesAsync(ct);
        await _audit.LogAsync(AuditEvents.LicenseExtend, AuditTargets.License, key,
            new { additionalDays = ExtendForm.AdditionalDays }, ct);

        TempData["Success"] = $"Lisans {ExtendForm.AdditionalDays} gün uzatıldı.";
        return RedirectToPage("./Detail", new { key });
    }

    private async Task<IActionResult> LoadAsync(string key, CancellationToken ct)
    {
        License = await _db.Licenses.FirstOrDefaultAsync(l => l.LicenseKey == key, ct);
        if (License is null) return NotFound();
        Customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == License.CustomerId, ct);
        Activations = await _db.Activations
            .Where(a => a.LicenseId == License.Id)
            .OrderByDescending(a => a.ActivatedAt)
            .ToListAsync(ct);
        AuditEntries = await _db.AuditLogs
            .Where(a => a.TargetType == "license" && a.TargetId == key)
            .OrderByDescending(a => a.OccurredAt)
            .Take(20)
            .ToListAsync(ct);
        return Page();
    }
}
```

- [ ] **Step 2: Detail.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml`:

```cshtml
@page "{key}"
@model DetailModel
@{
    ViewData["Title"] = "Lisans Detayı";
    var isActive = Model.License!.RevokedAt is null && Model.License.ExpiresAt > DateTimeOffset.UtcNow;
}
@if (TempData["Success"] is string okMsg)
{
    <div class="alert alert-success">@okMsg</div>
}
<h1 class="h3 mb-4"><code>@Model.License.LicenseKey</code></h1>

<div class="row">
    <div class="col-md-6">
        <h5>Bilgi</h5>
        <table class="table">
            <tr><th>Müşteri</th><td><a asp-page="/Admin/Customers/Detail" asp-route-id="@Model.Customer!.Id">@Model.Customer.Email</a></td></tr>
            <tr><th>SKU</th><td>@Model.License.SkuCode</td></tr>
            <tr><th>Slot</th><td>@Model.License.ActivationSlots</td></tr>
            <tr><th>İhraç</th><td>@Model.License.IssuedAt.ToString("dd.MM.yyyy HH:mm")</td></tr>
            <tr><th>Bitiş</th><td>@Model.License.ExpiresAt.ToString("dd.MM.yyyy HH:mm")</td></tr>
            <tr>
                <th>Durum</th>
                <td>
                    @if (Model.License.RevokedAt is not null)
                    {
                        <span class="badge text-bg-danger">İptal</span> <small class="text-muted">(@Model.License.RevokeReason)</small>
                    }
                    else if (Model.License.ExpiresAt < DateTimeOffset.UtcNow)
                    {
                        <span class="badge text-bg-warning">Süresi Doldu</span>
                    }
                    else
                    {
                        <span class="badge text-bg-success">Aktif</span>
                    }
                </td>
            </tr>
        </table>
    </div>

    <div class="col-md-6">
        @if (isActive)
        {
            <h5>Uzat</h5>
            <form method="post" asp-page-handler="Extend" asp-route-key="@Model.License.LicenseKey" class="mb-4">
                <div class="input-group" style="max-width: 300px;">
                    <input asp-for="ExtendForm.AdditionalDays" type="number" class="form-control" />
                    <span class="input-group-text">gün</span>
                    <button type="submit" class="btn btn-primary">Uzat</button>
                </div>
                <span asp-validation-for="ExtendForm.AdditionalDays" class="text-danger"></span>
            </form>

            <h5>İptal</h5>
            <form method="post" asp-page-handler="Revoke" asp-route-key="@Model.License.LicenseKey">
                <div class="mb-2">
                    <input asp-for="RevokeForm.Reason" class="form-control" placeholder="İptal sebebi" />
                    <span asp-validation-for="RevokeForm.Reason" class="text-danger"></span>
                </div>
                <button type="submit" class="btn btn-danger">İptal Et</button>
            </form>
        }
    </div>
</div>

<h5 class="mt-4">Aktivasyonlar</h5>
@if (!Model.Activations.Any())
{
    <p class="text-muted">Bu lisansa henüz aktivasyon yok.</p>
}
else
{
    <a asp-page="/Admin/Activations/Index" asp-route-licenseKey="@Model.License.LicenseKey" class="btn btn-sm btn-outline-secondary mb-2">Tümünü gör + yönet</a>
    <table class="table table-sm" data-table="activations">
        <thead><tr><th>Cihaz</th><th>HW</th><th>Aktif tarih</th><th>Son görülme</th><th>Durum</th></tr></thead>
        <tbody>
            @foreach (var a in Model.Activations)
            {
                <tr>
                    <td>@(a.MachineName ?? "—")</td>
                    <td><code>@(a.HardwareFingerprint.Length > 16 ? a.HardwareFingerprint[..16] + "…" : a.HardwareFingerprint)</code></td>
                    <td>@a.ActivatedAt.ToString("dd.MM HH:mm")</td>
                    <td>@a.LastSeenAt.ToString("dd.MM HH:mm")</td>
                    <td>@(a.DeactivatedAt is null ? "Aktif" : "İptal")</td>
                </tr>
            }
        </tbody>
    </table>
}

<h5 class="mt-4">Son Audit Olayları</h5>
@if (!Model.AuditEntries.Any())
{
    <p class="text-muted">Henüz audit kaydı yok.</p>
}
else
{
    <table class="table table-sm">
        <thead><tr><th>Zaman</th><th>Olay</th><th>Admin</th><th>Detay</th></tr></thead>
        <tbody>
            @foreach (var a in Model.AuditEntries)
            {
                <tr>
                    <td>@a.OccurredAt.ToString("dd.MM HH:mm")</td>
                    <td>@a.EventType</td>
                    <td>@a.AdminUsername</td>
                    <td><code>@(a.Details?.Length > 60 ? a.Details[..60] + "…" : a.Details)</code></td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 3: AdminLicensesDetailTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminLicensesDetailTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminLicensesDetailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminLicensesDetailTests(ApiFactory factory) => _factory = factory;

    private async Task<License> SeedLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var custId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = custId, Email = $"l-{Guid.NewGuid():N}@x", Name = "L", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
        var lic = new License { Id = Guid.NewGuid(), LicenseKey = "LDK-DET-" + Guid.NewGuid().ToString("N"), CustomerId = custId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
        db.Licenses.Add(lic);
        await db.SaveChangesAsync();
        return lic;
    }

    [Fact]
    public async Task Get_detail_returns_license_info()
    {
        var lic = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(lic.LicenseKey);
        html.Should().Contain("STD");
    }

    [Fact]
    public async Task Post_revoke_marks_license_revoked_and_writes_audit()
    {
        var lic = await SeedLicenseAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["RevokeForm.Reason"] = "Test iptal"
        });
        var postResp = await client.PostAsync($"/admin/licenses/{lic.LicenseKey}?handler=Revoke", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
        updated.RevokedAt.Should().NotBeNull();
        updated.RevokeReason.Should().Be("Test iptal");

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "license.revoke" && a.TargetId == lic.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_extend_updates_expiry_and_writes_audit()
    {
        var lic = await SeedLicenseAsync();
        var originalExpiry = lic.ExpiresAt;
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/licenses/{lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["ExtendForm.AdditionalDays"] = "60"
        });
        var postResp = await client.PostAsync($"/admin/licenses/{lic.LicenseKey}?handler=Extend", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Licenses.FirstAsync(l => l.LicenseKey == lic.LicenseKey);
        updated.ExpiresAt.Should().BeCloseTo(originalExpiry.AddDays(60), TimeSpan.FromSeconds(2));

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "license.extend" && a.TargetId == lic.LicenseKey)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Details.Should().Contain("60");
    }

    [Fact]
    public async Task Get_unknown_key_returns_404()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/licenses/LDK-DOES-NOT-EXIST");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminLicensesDetailTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 4/4 + 84/84 toplam.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml.cs \
        LiveDeck.LicenseServer.Tests/Pages/AdminLicensesDetailTests.cs
git commit -m "feat(license-server): add admin license detail page with revoke + extend"
```

---

### Task 8: Activations — Index page + Force-deactivate handler

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Activations/Index.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Activations/Index.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminActivationsPageTests.cs`

**Context:** `?licenseKey=LDK-...` query → o lisansa ait tüm activation'lar listelenir. POST handler `?handler=Deactivate&id={guid}` → `ActivationManager.ForceDeactivateAsync` + audit + redirect.

- [ ] **Step 1: Activations/Index.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Activations/Index.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using LiveDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Activations;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly ActivationManager _activations;
    private readonly IAuditService _audit;

    public IndexModel(LicenseDbContext db, ActivationManager activations, IAuditService audit)
    {
        _db = db;
        _activations = activations;
        _audit = audit;
    }

    public License? License { get; private set; }
    public List<Activation> Items { get; private set; } = new();

    public async Task<IActionResult> OnGetAsync(string licenseKey, CancellationToken ct)
    {
        return await LoadAsync(licenseKey, ct);
    }

    public async Task<IActionResult> OnPostDeactivateAsync(string licenseKey, Guid id, CancellationToken ct)
    {
        var activation = await _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a => a.Id == id, ct);
        if (activation is null) return NotFound();

        var ok = await _activations.ForceDeactivateAsync(id, ct);
        if (ok)
        {
            await _audit.LogAsync(AuditEvents.ActivationForceDeactivate, AuditTargets.Activation, id.ToString(),
                new { hardwareFingerprint = activation.HardwareFingerprint, licenseKey = activation.License.LicenseKey }, ct);
            TempData["Success"] = "Aktivasyon iptal edildi.";
        }
        return RedirectToPage("./Index", new { licenseKey });
    }

    private async Task<IActionResult> LoadAsync(string licenseKey, CancellationToken ct)
    {
        License = await _db.Licenses.FirstOrDefaultAsync(l => l.LicenseKey == licenseKey, ct);
        if (License is null) return NotFound();
        Items = await _db.Activations
            .Where(a => a.LicenseId == License.Id)
            .OrderByDescending(a => a.ActivatedAt)
            .ToListAsync(ct);
        return Page();
    }
}
```

- [ ] **Step 2: Activations/Index.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Activations/Index.cshtml`:

```cshtml
@page
@model IndexModel
@{
    ViewData["Title"] = "Aktivasyonlar";
}
@if (TempData["Success"] is string okMsg)
{
    <div class="alert alert-success">@okMsg</div>
}
<h1 class="h3 mb-4">Aktivasyonlar — <code>@Model.License!.LicenseKey</code></h1>
<a asp-page="/Admin/Licenses/Detail" asp-route-key="@Model.License.LicenseKey" class="btn btn-link mb-3">← Lisans detayına dön</a>

@if (!Model.Items.Any())
{
    <p class="text-muted">Hiç aktivasyon yok.</p>
}
else
{
    <table class="table" data-table="activations">
        <thead><tr><th>ID</th><th>Cihaz</th><th>HW Fingerprint</th><th>Aktif</th><th>Son Görülme</th><th>Durum</th><th></th></tr></thead>
        <tbody>
            @foreach (var a in Model.Items)
            {
                <tr>
                    <td><code>@a.Id.ToString()[..8]</code></td>
                    <td>@(a.MachineName ?? "—")</td>
                    <td><code>@a.HardwareFingerprint</code></td>
                    <td>@a.ActivatedAt.ToString("dd.MM.yyyy HH:mm")</td>
                    <td>@a.LastSeenAt.ToString("dd.MM.yyyy HH:mm")</td>
                    <td>
                        @if (a.DeactivatedAt is null)
                        {
                            <span class="badge text-bg-success">Aktif</span>
                        }
                        else
                        {
                            <span class="badge text-bg-secondary">İptal</span>
                        }
                    </td>
                    <td>
                        @if (a.DeactivatedAt is null)
                        {
                            <form method="post" asp-page-handler="Deactivate" asp-route-licenseKey="@Model.License.LicenseKey" asp-route-id="@a.Id" class="d-inline">
                                <button type="submit" class="btn btn-sm btn-outline-danger">İptal Et</button>
                            </form>
                        }
                    </td>
                </tr>
            }
        </tbody>
    </table>
}
```

- [ ] **Step 3: AdminActivationsPageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminActivationsPageTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminActivationsPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminActivationsPageTests(ApiFactory factory) => _factory = factory;

    private async Task<(License lic, Activation act)> SeedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var custId = Guid.NewGuid();
        db.Customers.Add(new Customer { Id = custId, Email = $"a-{Guid.NewGuid():N}@x", Name = "A", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow });
        var lic = new License { Id = Guid.NewGuid(), LicenseKey = "LDK-ACT-" + Guid.NewGuid().ToString("N"), CustomerId = custId, SkuCode = "STD", ActivationSlots = 1, IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30) };
        var act = new Activation { Id = Guid.NewGuid(), LicenseId = lic.Id, HardwareFingerprint = "fp-test", MachineName = "PC-1", ActivatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow };
        db.Licenses.Add(lic);
        db.Activations.Add(act);
        await db.SaveChangesAsync();
        return (lic, act);
    }

    [Fact]
    public async Task Get_lists_activations_for_license()
    {
        var (lic, act) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync($"/admin/activations?licenseKey={lic.LicenseKey}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("PC-1");
        html.Should().Contain("fp-test");
    }

    [Fact]
    public async Task Post_force_deactivate_sets_DeactivatedAt_and_audit()
    {
        var (lic, act) = await SeedAsync();
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");

        var getResp = await client.GetAsync($"/admin/activations?licenseKey={lic.LicenseKey}");
        var token = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token
        });
        var postResp = await client.PostAsync($"/admin/activations?handler=Deactivate&licenseKey={lic.LicenseKey}&id={act.Id}", form);
        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Activations.FirstAsync(a => a.Id == act.Id);
        updated.DeactivatedAt.Should().NotBeNull();

        var audit = await db.AuditLogs
            .Where(a => a.EventType == "activation.force-deactivate" && a.TargetId == act.Id.ToString())
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
    }
}
```

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminActivationsPageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 2/2 + 86/86 toplam.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Activations/ \
        LiveDeck.LicenseServer.Tests/Pages/AdminActivationsPageTests.cs
git commit -m "feat(license-server): add admin activations page + force-deactivate"
```

---

### Task 9: Skus — Read-only Index page

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Skus/Index.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Skus/Index.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminSkusPageTests.cs`

**Context:** En basit sayfa. Read-only Sku listesi. SKU yönetimi YAGNI — sadece görünüm.

- [ ] **Step 1: Skus/Index.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Skus/Index.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Skus;

public class IndexModel : PageModel
{
    private readonly LicenseDbContext _db;
    public IndexModel(LicenseDbContext db) => _db = db;

    public List<Sku> Items { get; private set; } = new();

    public async Task OnGetAsync(CancellationToken ct)
    {
        Items = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
    }
}
```

- [ ] **Step 2: Skus/Index.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Skus/Index.cshtml`:

```cshtml
@page
@model IndexModel
@{
    ViewData["Title"] = "SKU'lar";
}
<h1 class="h3 mb-4">SKU'lar</h1>
<table class="table" data-table="skus">
    <thead>
        <tr><th>Kod</th><th>Görünen Ad</th><th>Süre (gün)</th><th>Slot</th><th>Açıklama</th></tr>
    </thead>
    <tbody>
        @foreach (var s in Model.Items)
        {
            <tr>
                <td><code>@s.Code</code></td>
                <td>@s.DisplayName</td>
                <td>@s.DefaultDurationDays</td>
                <td>@s.DefaultActivationSlots</td>
                <td>@s.Description</td>
            </tr>
        }
    </tbody>
</table>
```

- [ ] **Step 3: AdminSkusPageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminSkusPageTests.cs`:

```csharp
using System.Net;
using AngleSharp;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminSkusPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminSkusPageTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Index_lists_seeded_skus()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/skus");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
        var rows = doc.QuerySelectorAll("table[data-table='skus'] tbody tr");
        rows.Length.Should().BeGreaterThanOrEqualTo(2);   // STD + PRO seed minimum
        html.Should().Contain("STD");
        html.Should().Contain("PRO");
    }
}
```

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminSkusPageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 1/1 + 87/87 toplam.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Skus/ \
        LiveDeck.LicenseServer.Tests/Pages/AdminSkusPageTests.cs
git commit -m "feat(license-server): add admin SKUs read-only page"
```

---

### Task 10: Audit — Index page (filter + pagination)

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Admin/Audit/Index.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Audit/Index.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/AdminAuditPageTests.cs`

**Context:** Filter: `?eventType=` select + `?adminUsername=` text + `?from=&to=` date range + `?page=N`. Default: from=now-7d, to=now, size=50.

- [ ] **Step 1: Audit/Index.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Admin/Audit/Index.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Audit;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Admin.Audit;

public class IndexModel : PageModel
{
    private const int PageSize = 50;
    private readonly LicenseDbContext _db;

    public IndexModel(LicenseDbContext db) => _db = db;

    public List<AuditLogEntry> Entries { get; private set; } = new();
    public string? EventType { get; private set; }
    public string? AdminUsername { get; private set; }
    public DateTimeOffset From { get; private set; }
    public DateTimeOffset To { get; private set; }
    public int CurrentPage { get; private set; }
    public int TotalPages { get; private set; }

    public List<string> AvailableEventTypes { get; } = new()
    {
        AuditEvents.AdminLogin,
        AuditEvents.AdminLogout,
        AuditEvents.CustomerConfirmEmail,
        AuditEvents.LicenseIssue,
        AuditEvents.LicenseRevoke,
        AuditEvents.LicenseExtend,
        AuditEvents.ActivationForceDeactivate
    };

    public async Task OnGetAsync(string? eventType, string? adminUsername, DateTimeOffset? from, DateTimeOffset? to, int page, CancellationToken ct)
    {
        EventType = eventType;
        AdminUsername = adminUsername;
        From = from ?? DateTimeOffset.UtcNow.AddDays(-7);
        To = to ?? DateTimeOffset.UtcNow;
        CurrentPage = page < 1 ? 1 : page;

        var query = _db.AuditLogs
            .Where(a => a.OccurredAt >= From && a.OccurredAt <= To);
        if (!string.IsNullOrWhiteSpace(eventType))
            query = query.Where(a => a.EventType == eventType);
        if (!string.IsNullOrWhiteSpace(adminUsername))
            query = query.Where(a => a.AdminUsername.Contains(adminUsername));

        var total = await query.CountAsync(ct);
        TotalPages = (int)Math.Ceiling(total / (double)PageSize);

        Entries = await query
            .OrderByDescending(a => a.OccurredAt)
            .Skip((CurrentPage - 1) * PageSize)
            .Take(PageSize)
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 2: Audit/Index.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Audit/Index.cshtml`:

```cshtml
@page
@model IndexModel
@{
    ViewData["Title"] = "Audit Log";
}
<h1 class="h3 mb-4">Audit Log</h1>

<form method="get" class="row g-2 mb-3">
    <div class="col-md-3">
        <label class="form-label">Olay Tipi</label>
        <select name="eventType" class="form-select">
            <option value="">Tümü</option>
            @foreach (var t in Model.AvailableEventTypes)
            {
                <option value="@t" selected="@(t == Model.EventType)">@t</option>
            }
        </select>
    </div>
    <div class="col-md-3">
        <label class="form-label">Admin</label>
        <input type="text" name="adminUsername" value="@Model.AdminUsername" class="form-control" />
    </div>
    <div class="col-md-2">
        <label class="form-label">Başlangıç</label>
        <input type="date" name="from" value="@Model.From.ToString("yyyy-MM-dd")" class="form-control" />
    </div>
    <div class="col-md-2">
        <label class="form-label">Bitiş</label>
        <input type="date" name="to" value="@Model.To.ToString("yyyy-MM-dd")" class="form-control" />
    </div>
    <div class="col-md-2 d-flex align-items-end">
        <button type="submit" class="btn btn-primary w-100">Filtrele</button>
    </div>
</form>

@if (!Model.Entries.Any())
{
    <p class="text-muted">Hiç kayıt yok.</p>
}
else
{
    <table class="table table-sm" data-table="audit">
        <thead>
            <tr><th>Zaman</th><th>Admin</th><th>Olay</th><th>Hedef</th><th>Detay</th><th>IP</th></tr>
        </thead>
        <tbody>
            @foreach (var a in Model.Entries)
            {
                <tr>
                    <td>@a.OccurredAt.ToString("dd.MM.yyyy HH:mm:ss")</td>
                    <td>@a.AdminUsername</td>
                    <td>@a.EventType</td>
                    <td>@a.TargetType / @a.TargetId</td>
                    <td title="@a.Details"><code>@(a.Details?.Length > 60 ? a.Details[..60] + "…" : a.Details)</code></td>
                    <td>@a.IpAddress</td>
                </tr>
            }
        </tbody>
    </table>

    @if (Model.TotalPages > 1)
    {
        <nav>
            <ul class="pagination">
                @if (Model.CurrentPage > 1)
                {
                    <li class="page-item"><a class="page-link"
                       asp-route-eventType="@Model.EventType"
                       asp-route-adminUsername="@Model.AdminUsername"
                       asp-route-from="@Model.From"
                       asp-route-to="@Model.To"
                       asp-route-page="@(Model.CurrentPage - 1)">Önceki</a></li>
                }
                <li class="page-item active"><span class="page-link">@Model.CurrentPage / @Model.TotalPages</span></li>
                @if (Model.CurrentPage < Model.TotalPages)
                {
                    <li class="page-item"><a class="page-link"
                       asp-route-eventType="@Model.EventType"
                       asp-route-adminUsername="@Model.AdminUsername"
                       asp-route-from="@Model.From"
                       asp-route-to="@Model.To"
                       asp-route-page="@(Model.CurrentPage + 1)">Sonraki</a></li>
                }
            </ul>
        </nav>
    }
}
```

- [ ] **Step 3: AdminAuditPageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/AdminAuditPageTests.cs`:

```csharp
using System.Net;
using AngleSharp;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class AdminAuditPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminAuditPageTests(ApiFactory factory) => _factory = factory;

    private async Task SeedAuditAsync(string adminUsername, string eventType, DateTimeOffset when)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.AuditLogs.Add(new AuditLogEntry
        {
            Id = Guid.NewGuid(),
            OccurredAt = when,
            AdminId = Guid.NewGuid(),
            AdminUsername = adminUsername,
            EventType = eventType,
            TargetType = "license",
            TargetId = "LDK-TEST",
            Details = null,
            IpAddress = "127.0.0.1"
        });
        await db.SaveChangesAsync();
    }

    [Fact]
    public async Task Index_renders_with_default_filter()
    {
        await SeedAuditAsync($"u-{Guid.NewGuid():N}", "license.issue", DateTimeOffset.UtcNow.AddHours(-1));

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/audit");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("license.issue");
    }

    [Fact]
    public async Task Index_eventType_filter_narrows_results()
    {
        var u1 = $"u-{Guid.NewGuid():N}";
        var u2 = $"u-{Guid.NewGuid():N}";
        await SeedAuditAsync(u1, "license.revoke", DateTimeOffset.UtcNow.AddHours(-1));
        await SeedAuditAsync(u2, "license.extend", DateTimeOffset.UtcNow.AddHours(-1));

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/audit?eventType=license.revoke");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        var doc = await BrowsingContext.New(Configuration.Default).OpenAsync(req => req.Content(html));
        var rows = doc.QuerySelectorAll("table[data-table='audit'] tbody tr");
        // All rows should have only license.revoke entries
        foreach (var row in rows)
        {
            row.TextContent.Should().Contain("license.revoke");
            row.TextContent.Should().NotContain("license.extend");
        }
    }

    [Fact]
    public async Task Index_date_range_filter_excludes_old_entries()
    {
        var oldUser = $"old-{Guid.NewGuid():N}";
        var newUser = $"new-{Guid.NewGuid():N}";
        await SeedAuditAsync(oldUser, "license.issue", DateTimeOffset.UtcNow.AddDays(-30));
        await SeedAuditAsync(newUser, "license.issue", DateTimeOffset.UtcNow.AddHours(-1));

        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/admin/audit");   // default: last 7 days
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(newUser);
        html.Should().NotContain(oldUser);
    }
}
```

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminAuditPageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 3/3 + 90/90 toplam.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Audit/ \
        LiveDeck.LicenseServer.Tests/Pages/AdminAuditPageTests.cs
git commit -m "feat(license-server): add admin audit log page with filter + pagination"
```

---

### Task 11: AdminLayout sidebar styling + ToastPartial + final verification

**Files:**
- Modify: `LiveDeck.LicenseServer/Pages/Admin/Shared/_AdminLayout.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Admin/Shared/_ToastPartial.cshtml`
- Modify: `LiveDeck.LicenseServer/wwwroot/css/admin.css`

**Context:** Şimdiye kadar `_AdminLayout` minimal — `<div class="container-fluid">@RenderBody()</div>`. Bu task'ta sidebar nav + user info + toast partial + custom CSS eklenir. Tüm sayfalar daha tutarlı görünür. Kod testleri zaten geçiyor — bu task fonksiyonel değişiklik yapmaz, sadece görünüm.

- [ ] **Step 1: _ToastPartial.cshtml**

`LiveDeck.LicenseServer/Pages/Admin/Shared/_ToastPartial.cshtml`:

```cshtml
@if (TempData["Success"] is string okMsg)
{
    <div class="alert alert-success alert-dismissible fade show" role="alert">
        @okMsg
        <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Close"></button>
    </div>
}
@if (TempData["Error"] is string errMsg)
{
    <div class="alert alert-danger alert-dismissible fade show" role="alert">
        @errMsg
        <button type="button" class="btn-close" data-bs-dismiss="alert" aria-label="Close"></button>
    </div>
}
```

- [ ] **Step 2: _AdminLayout.cshtml — full sidebar**

`LiveDeck.LicenseServer/Pages/Admin/Shared/_AdminLayout.cshtml` dosyasını **tamamen** değiştir:

```cshtml
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"] — LiveDeck Admin</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
    <link rel="stylesheet" href="~/css/admin.css" />
</head>
<body>
    <div class="container-fluid">
        <div class="row">
            <nav class="col-md-2 sidebar bg-light p-3">
                <h5 class="mb-3">LiveDeck Admin</h5>
                <ul class="nav flex-column mb-4">
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/Index">Dashboard</a></li>
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/Customers/Index">Müşteriler</a></li>
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/Licenses/Issue">Yeni Lisans</a></li>
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/Skus/Index">SKU'lar</a></li>
                    <li class="nav-item"><a class="nav-link" asp-page="/Admin/Audit/Index">Audit Log</a></li>
                </ul>

                <hr />

                <div class="user-info">
                    <small class="text-muted d-block mb-2">@User.FindFirst("username")?.Value</small>
                    <form method="post" action="/admin/logout">
                        @Html.AntiForgeryToken()
                        <button type="submit" class="btn btn-sm btn-outline-secondary w-100">Çıkış</button>
                    </form>
                </div>
            </nav>

            <main class="col-md-10 p-4">
                <partial name="_ToastPartial" />
                @RenderBody()
            </main>
        </div>
    </div>

    <script src="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/js/bootstrap.bundle.min.js"></script>
</body>
</html>
```

- [ ] **Step 3: admin.css — sidebar styling**

`LiveDeck.LicenseServer/wwwroot/css/admin.css` dosyasını **tamamen** değiştir:

```css
.sidebar {
    min-height: 100vh;
    border-right: 1px solid #dee2e6;
}

.sidebar .nav-link {
    color: #495057;
    padding: 0.5rem 0;
}

.sidebar .nav-link:hover {
    color: #0d6efd;
}

.sidebar h5 {
    color: #0d6efd;
}

main {
    background-color: #ffffff;
    min-height: 100vh;
}

table code {
    font-size: 0.85em;
    color: #6c757d;
}
```

- [ ] **Step 4: Sayfaların TempData "Success" rendering'i artık _ToastPartial üzerinden**

Mevcut Customers/Detail.cshtml ve Licenses/Detail.cshtml ve Activations/Index.cshtml içindeki:

```cshtml
@if (TempData["Success"] is string okMsg)
{
    <div class="alert alert-success">@okMsg</div>
}
```

Bu duplikat satırlar artık _AdminLayout'taki `<partial name="_ToastPartial" />` ile karşılanır. Tekrar render olmaması için sayfalardan **kaldır** (3 dosya):
- `LiveDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml` (Step 4 of Task 5'te eklendi)
- `LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml` (Step 2 of Task 7'de eklendi)
- `LiveDeck.LicenseServer/Pages/Admin/Activations/Index.cshtml` (Step 2 of Task 8'de eklendi)

Her üçünden de 3-4 satırlık `@if (TempData["Success"]...)` bloğunu sil.

- [ ] **Step 5: Build + tüm test paketleri (final verification sweep)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Beklenen: 0 errors / 0 warnings.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
dotnet test LiveDeck.Tests 2>&1 | tail -3
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen toplam:
- LicenseServer.Tests: 90/90 (62 baseline + 4 audit + 5 auth + 2 dashboard + 4 customers + 3 issue + 4 detail + 2 activations + 1 skus + 3 audit page = 28 yeni → 62+28=90)
- LiveDeck.Tests: 128/128 (regression, dokunulmadı)
- Licensing.Tests: 104/104 (regression, dokunulmadı)
- **Toplam: 322** (294 baseline + 28 yeni)

Spec'in ~320 hedefi aşıldı (spec'te 26 yazıyordu, plan yazılırken 28 oldu — küçük over-delivery).

- [ ] **Step 6: Manuel smoke (opsiyonel)**

Spec §7'deki 16 maddelik manuel smoke planını fiziksel browser'da uygula. Kayıt etme; spec dosyası referans.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Admin/Shared/ \
        LiveDeck.LicenseServer/wwwroot/css/admin.css \
        LiveDeck.LicenseServer/Pages/Admin/Customers/Detail.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml \
        LiveDeck.LicenseServer/Pages/Admin/Activations/Index.cshtml
git commit -m "feat(license-server): polish admin layout (sidebar nav + toast partial + CSS)"
```

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task |
|---|---|
| §2.1 Solution etkisi (Pages klasör yapısı) | Task 1 |
| §2.2 Stack (Bootstrap + cookie auth + AngleSharp) | Task 1 |
| §2.3 İki auth scheme yan yana | Task 1 (cookie scheme) |
| §2.4 Internal services vs REST controllers | Task 6 (LicenseIssuer kullanımı), Task 8 (ActivationManager) |
| §3 Sayfa haritası 14 route | Task 3 (login/logout) + 4 (index) + 5 (customers x2 + handler) + 6 (license issue) + 7 (license detail x2) + 8 (activations) + 9 (skus) + 10 (audit) = 14 ✓ |
| §3.1 _AdminLayout sidebar | Task 11 |
| §3.2 Bootstrap CDN | Task 1 + 11 |
| §4.1-4.4 AuditLogEntry + DbContext + AuditEvents + IAuditService | Task 2 |
| §4.5 Audit yazma noktaları (7 event) | Task 3 (login + logout) + 5 (confirm-email) + 6 (issue) + 7 (revoke + extend) + 8 (force-deactivate) ✓ |
| §4.6 Audit list + filter | Task 10 |
| §4.7 Failed login NOT logged | Task 3 (Login.OnPostAsync wrong creds: ErrorMessage set, hiç audit çağrısı yok) ✓ |
| §5 Konfigürasyon + DI | Task 1 (Program.cs) + Task 2 (AddHttpContextAccessor + AddScoped<IAuditService>) |
| §6 Test stratejisi | Tüm task'lerin kendi test dosyaları |
| §7 Manuel smoke | Task 11 Step 6 referansı |
| §8 YAGNI | Plan'a yansıdı |
| §10 Kabul kriterleri | Task 11 final verification |

**2. Placeholder scan:**
- Hiç "TBD"/"TODO" yok
- Tüm step'ler concrete kod blokları içerir
- Test'ler tam yazılmış (template değil)
- Path'ler tam absolute

**3. Type consistency:**
- `AuditEvents.AdminLogin` / `LicenseIssue` / vb. (Task 2 sabitleri) — Task 3, 5, 6, 7, 8, 10'da aynı isimle kullanılır ✓
- `AuditTargets.Admin` / `Customer` / `License` / `Activation` (Task 2 sabitleri) — handler'larda tutarlı ✓
- `IAuditService.LogAsync(eventType, targetType, targetId, details, ct)` 5-param signature — tüm yazıma noktalarında doğru ✓
- `IAuditService.LogLoginAsync(adminId, username, ip, ct)` ve `LogLogoutAsync` aynı signature — Task 3 Login + Logout handler'da doğru ✓
- `LicenseIssuer.IssueAsync(IssueRequest, ct)` (Phase 4a Task 12) — Task 6 doğru çağrı ✓
- `ActivationManager.ForceDeactivateAsync(activationId, ct)` (Phase 4a Task 12) — Task 8 doğru çağrı ✓
- `AuditLogEntry` 9 alan — Task 2 entity + Task 5/7 sorgulama kullanımı tutarlı ✓
- `IClassFixture<ApiFactory>` test paterni (Phase 4a) — tüm yeni testlerde aynı ✓

**4. Cross-task dependencies:**
- Task 1 → Task 3+ (cookie scheme + Razor Pages middleware altyapı)
- Task 2 → Task 3+ (IAuditService bağımlılık)
- Task 3 → Task 4+ (AdminLoginHelper test infra herkese gerek)
- Task 4 → Task 11 (Index sayfası AdminLayout'u tetikler)
- Task 5+6+7+8+9+10 paralel olabilir (her biri kendi sayfa, sadece common audit + auth gerek)
- Task 11 cosmetic, en sonda

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-29-phase-4d-admin-web-ui.md`.**

11 task. Tahmini ~3000 satır plan. Phase 4a/4b/4c ile aynı pattern (TDD, frequent commits, subagent-friendly). Test hedefi 294 → ~322.

İki yürütme seçeneği:

**1. Subagent-Driven (önerilen)** — Her task için fresh subagent dispatch. Phase 4a (15) + 4b (13) + 4c (14) = 42 task hep bu şekilde tamamlandı.

**2. Inline Execution** — executing-plans skill ile bu session'da batch yürütme.

Hangisi?

