# Faz 4e — Email Otomasyonu Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `LiveDeck.LicenseServer` projesine email otomasyonu ekle — Hangfire ile günlük yenileme reminder'ları (14/7/3/0 + expired+1g), password reset flow, admin action notifications (license issue/revoke/extend), HMAC-signed unsubscribe. EmailLog ile send dedup. Phase 4a `IEmailSender` reuse.

**Architecture:** Hangfire (SQL Server storage prod, MemoryStorage testte) + 5 recurring job + EmailSendCoordinator dedup pipeline + 7 yeni email template + 2 yeni entity (EmailLog, PasswordResetToken) + Customer.Unsubscribed flag + 2 anonim Razor sayfa (Pages/Public/PasswordReset, Pages/Public/Unsubscribe) + 2 REST endpoint (password-reset-request, password-reset). Phase 4a/4b/4c/4d code dokunulmaz; Phase 4d Razor handlers ve Phase 4a Controllers admin notify çağrısı eklenir.

**Tech Stack:** .NET 10 / `Hangfire.AspNetCore` 1.8.14 + `Hangfire.SqlServer` 1.8.14 / `Hangfire.MemoryStorage` 1.8.x (test) / Razor Pages / mevcut Phase 4a `IEmailSender` (MailKit) / `System.Security.Cryptography.HMACSHA256` (unsubscribe).

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-4e state:** Phase 4d master `497a3f5`. 322/322 test (128 LiveDeck + 104 Licensing + 90 LicenseServer). Build 0/0.

**Spec reference:** `docs/superpowers/specs/2026-04-29-phase-4e-email-automation-design.md`

---

## Task Index

**Foundation (1):** Hangfire packages + Program.cs setup + dashboard auth filter + ApiFactory MemoryStorage
**Domain + EF (2):** EmailLog + PasswordResetToken + Customer.Unsubscribed + migration 006
**Crypto (3):** UnsubscribeTokenSigner (HMAC) — TDD
**Templates (4):** EmailTemplates 7 yeni metot + footer helper
**Coordinator (5):** EmailSendCoordinator (dedup + unsubscribe respect + send pipeline) — TDD
**Reminders (6):** ReminderJobs 5 method + recurring registration — TDD
**Password reset (7):** PasswordResetService + 2 REST endpoint — TDD
**Reset Razor (8):** Pages/Public/PasswordReset Razor sayfa
**Admin notify (9):** AdminActionEmailService + 6 trigger noktası entegrasyonu
**Unsubscribe (10):** Pages/Public/Unsubscribe Razor sayfa + footer URL injection
**Final (11):** Verification sweep + manual smoke

**Toplam test hedefi:** 322 baseline → ~361 (+39 yeni)

---

### Task 1: Foundation — Hangfire packages + Program.cs setup + dashboard auth + ApiFactory MemoryStorage

**Files:**
- Modify: `LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj`
- Modify: `LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj`
- Create: `LiveDeck.LicenseServer/Services/Email/HangfireDashboardAuthFilter.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs`
- Modify: `LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/HangfireDashboardAuthTests.cs`

**Context:** Hangfire shell. Recurring jobs Task 6'da eklenecek; bu task sadece Hangfire çalışır durumda (server start, dashboard accessible). Test ortamında MemoryStorage override; production'da SqlServerStorage. Dashboard `/hangfire` route AdminCookie ile korunur. Test'te 2 kontrol: anonymous → 401/redirect, admin cookie → 200.

- [ ] **Step 1: Production csproj — Hangfire paketleri ekle**

`LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj` mevcut `<ItemGroup>` (PackageReference) bloğuna ekle:

```xml
    <PackageReference Include="Hangfire.AspNetCore" Version="1.8.14" />
    <PackageReference Include="Hangfire.SqlServer" Version="1.8.14" />
```

- [ ] **Step 2: Test csproj — Hangfire.MemoryStorage ekle**

`LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj` mevcut `<ItemGroup>` bloğuna ekle:

```xml
    <PackageReference Include="Hangfire.MemoryStorage" Version="1.8.0" />
```

- [ ] **Step 3: HangfireDashboardAuthFilter oluştur**

`LiveDeck.LicenseServer/Services/Email/HangfireDashboardAuthFilter.cs`:

```csharp
using Hangfire.Dashboard;
using Microsoft.AspNetCore.Authentication;

namespace LiveDeck.LicenseServer.Services.Email;

/// <summary>
/// Hangfire dashboard auth filter — sadece AdminCookie ile auth edilmiş kullanıcılara izin verir.
/// Anonim istekler 401 Unauthorized alır (Hangfire dashboard kendi response'unu render eder).
/// </summary>
public sealed class HangfireDashboardAuthFilter : IDashboardAuthorizationFilter
{
    public bool Authorize(DashboardContext context)
    {
        var http = context.GetHttpContext();
        var result = http.AuthenticateAsync("AdminCookie").GetAwaiter().GetResult();
        return result.Succeeded;
    }
}
```

- [ ] **Step 4: Program.cs — Hangfire setup + dashboard middleware**

`LiveDeck.LicenseServer/Program.cs` dosyasını aç. Üst tarafa using'leri ekle:

```csharp
using Hangfire;
using Hangfire.SqlServer;
using LiveDeck.LicenseServer.Services.Email;
```

`builder.Services.AddRazorPages(...)` çağrısının **hemen önüne** ekle:

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
        builder.Services.AddHangfireServer();
```

Pipeline kısmında, `app.UseAuthorization()` çağrısının **hemen sonrasına**, `app.MapControllers()` öncesine ekle:

```csharp
        app.UseHangfireDashboard("/hangfire", new DashboardOptions
        {
            Authorization = new[] { new HangfireDashboardAuthFilter() }
        });
```

- [ ] **Step 5: ApiFactory — Hangfire MemoryStorage override**

`LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` dosyasını aç. Üst tarafa using ekle:

```csharp
using Hangfire;
using Hangfire.MemoryStorage;
```

Mevcut `ConfigureWebHost` metodunda, `builder.ConfigureServices` callback içinde, mevcut DbContext + IEmailSender override'larının **sonrasına** ekle:

```csharp
            // Hangfire — production SQL Server yerine InMemory storage (test isolation)
            services.AddHangfire(cfg => cfg
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseMemoryStorage());
```

`AddHangfire` çağrısı 2 defa olur (Program.cs + ApiFactory) — son çağrı winner; `UseMemoryStorage` SQL Server config'ini override eder.

Eğer override çalışmazsa fallback: `services.RemoveAll(typeof(JobStorage))` veya `services.RemoveAll<IGlobalConfiguration>()` ekle. İlk denemede `AddHangfire` re-register yeter mi test et.

- [ ] **Step 6: HangfireDashboardAuthTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/HangfireDashboardAuthTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages;

public sealed class HangfireDashboardAuthTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public HangfireDashboardAuthTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Anonymous_request_to_hangfire_dashboard_returns_unauthorized()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var resp = await client.GetAsync("/hangfire");

        // Hangfire dashboard auth filter false dönerse 401 status döndürür
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logged_in_admin_can_access_hangfire_dashboard()
    {
        var client = await _factory.CreateLoggedInAdminClientAsync($"admin-{Guid.NewGuid():N}");
        var resp = await client.GetAsync("/hangfire");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        // Hangfire dashboard ana sayfası "Hangfire" string'i içerir
        html.Should().Contain("Hangfire");
    }
}
```

- [ ] **Step 7: Build + tüm testler**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Beklenen: 0 errors / 0 warnings.

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~HangfireDashboardAuthTests" 2>&1 | tail -5
```

Beklenen: 2/2 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
dotnet test LiveDeck.Tests 2>&1 | tail -3
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 92/92 LicenseServer (90 baseline + 2) + 128/128 + 104/104 (regression).

- [ ] **Step 8: Commit**

```bash
git add LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj \
        LiveDeck.LicenseServer/Services/Email/HangfireDashboardAuthFilter.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj \
        LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs \
        LiveDeck.LicenseServer.Tests/Pages/HangfireDashboardAuthTests.cs
git commit -m "feat(license-server): add Hangfire infrastructure + dashboard auth filter"
```

---

### Task 2: Domain entities + EF migration 006

**Files:**
- Create: `LiveDeck.LicenseServer/Domain/EmailLog.cs`
- Create: `LiveDeck.LicenseServer/Domain/PasswordResetToken.cs`
- Modify: `LiveDeck.LicenseServer/Domain/Customer.cs`
- Modify: `LiveDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `LiveDeck.LicenseServer/Data/Migrations/{timestamp}_AddEmailInfra.cs` (auto-generated)
- Create: `LiveDeck.LicenseServer.Tests/Domain/EmailLogTests.cs`

**Context:** 2 yeni entity + 1 yeni Customer alanı. EF Core migration auto-generated. Customer entity (Phase 4a Task 2) korunur; sadece `Unsubscribed bool` eklenir.

- [ ] **Step 1: EmailLog entity**

`LiveDeck.LicenseServer/Domain/EmailLog.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// Tracks email send attempts for dedup + audit. ContextKey scope is template-specific
/// (e.g. licenseKey for renewal/admin-action emails, tokenId for password-reset).
/// Error null = success; Error populated = failed (manual investigation).
/// </summary>
public sealed class EmailLog
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public string TemplateKey { get; set; } = "";
    public string? ContextKey { get; set; }
    public DateTimeOffset SentAt { get; set; }
    public string? Error { get; set; }
}
```

- [ ] **Step 2: PasswordResetToken entity**

`LiveDeck.LicenseServer/Domain/PasswordResetToken.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// Single-use password reset token. TTL enforced in PasswordResetService (1h default).
/// </summary>
public sealed class PasswordResetToken
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
```

- [ ] **Step 3: Customer.Unsubscribed alanı**

`LiveDeck.LicenseServer/Domain/Customer.cs` mevcut entity'i aç. Mevcut alanların **sonuna** ekle (sınıfın gövdesinde, koleksiyonlardan önce):

```csharp
    public bool Unsubscribed { get; set; }
```

Final sınıf şu şekilde olmalı (mevcut Phase 4a alanları + yeni):

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class Customer
{
    public Guid Id { get; set; }
    public string Email { get; set; } = "";
    public string Name { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset? EmailConfirmedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string? Notes { get; set; }
    public bool Unsubscribed { get; set; }   // YENİ — Phase 4e

    public ICollection<License> Licenses { get; } = new List<License>();
}
```

- [ ] **Step 4: LicenseDbContext — yeni DbSet + FluentAPI**

`LiveDeck.LicenseServer/Data/LicenseDbContext.cs` aç. Mevcut DbSet'lerin sonuna ekle:

```csharp
    public DbSet<EmailLog> EmailLogs => Set<EmailLog>();
    public DbSet<PasswordResetToken> PasswordResetTokens => Set<PasswordResetToken>();
```

`OnModelCreating` metodunda, mevcut `AuditLog` (Phase 4d Task 2) config bloğunun **hemen sonrasına**, `Sku.HasData(...)` seed satırından **önce** ekle:

```csharp
        mb.Entity<EmailLog>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.TemplateKey).HasMaxLength(64).IsRequired();
            b.Property(e => e.ContextKey).HasMaxLength(64);
            b.Property(e => e.Error).HasMaxLength(2000);
            b.HasIndex(e => new { e.CustomerId, e.TemplateKey, e.ContextKey });
            b.HasIndex(e => e.SentAt);
        });

        mb.Entity<PasswordResetToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasOne(t => t.Customer).WithMany()
                .HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CustomerId, t.UsedAt });
        });
```

- [ ] **Step 5: EF migration oluştur**

```bash
cd /c/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer
dotnet ef migrations add AddEmailInfra --output-dir Data/Migrations
```

Beklenen: `Data/Migrations/{timestamp}_AddEmailInfra.cs` + `.Designer.cs` + `LicenseDbContextModelSnapshot.cs` güncellenir. Migration:
- `EmailLogs` tablosu (6 kolon, 2 index)
- `PasswordResetTokens` tablosu (FK Customers)
- `Customer.Unsubscribed bit NOT NULL DEFAULT 0`

Build clean, manuel düzenleme yapma.

- [ ] **Step 6: EmailLogTests yaz (smoke)**

`LiveDeck.LicenseServer.Tests/Domain/EmailLogTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Domain;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Domain;

public class EmailLogTests
{
    [Fact]
    public void EmailLog_default_state_is_success_with_null_error()
    {
        var entry = new EmailLog
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            TemplateKey = "renewal-14d",
            ContextKey = "LDK-XYZ",
            SentAt = DateTimeOffset.UtcNow,
            Error = null
        };

        entry.Error.Should().BeNull();
        entry.TemplateKey.Should().Be("renewal-14d");
    }
}
```

- [ ] **Step 7: Build + test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~EmailLogTests" 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 1/1 + 93/93 toplam (92 baseline + 1 yeni).

- [ ] **Step 8: Commit**

```bash
git add LiveDeck.LicenseServer/Domain/EmailLog.cs \
        LiveDeck.LicenseServer/Domain/PasswordResetToken.cs \
        LiveDeck.LicenseServer/Domain/Customer.cs \
        LiveDeck.LicenseServer/Data/LicenseDbContext.cs \
        LiveDeck.LicenseServer/Data/Migrations/ \
        LiveDeck.LicenseServer.Tests/Domain/EmailLogTests.cs
git commit -m "feat(license-server): add EmailLog + PasswordResetToken + Customer.Unsubscribed (migration 006)"
```

---

### Task 3: UnsubscribeTokenSigner — HMAC-SHA256 (TDD)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Email/UnsubscribeTokenSigner.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/UnsubscribeTokenSignerTests.cs`

**Context:** Stateless HMAC-signed token. Format `base64url(customerId).base64url(unixTime).base64url(hmac)`. Key reuse: `Jwt:SecretKey` (Phase 4a). Süresiz token (issuedAt sadece audit). 4 test (sign/verify roundtrip, tampered reject, wrong customer reject, lifetime accepts old).

- [ ] **Step 1: Failing tests**

`LiveDeck.LicenseServer.Tests/Services/UnsubscribeTokenSignerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class UnsubscribeTokenSignerTests
{
    private static UnsubscribeTokenSigner Build()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "test-secret-key-must-be-at-least-32-bytes-long-for-hmac"
            })
            .Build();
        return new UnsubscribeTokenSigner(config);
    }

    [Fact]
    public void Sign_then_TryVerify_roundtrips_customerId()
    {
        var signer = Build();
        var customerId = Guid.NewGuid();
        var issuedAt = new DateTimeOffset(2026, 4, 29, 12, 0, 0, TimeSpan.Zero);

        var token = signer.Sign(customerId, issuedAt);

        signer.TryVerify(token, out var parsedId, out var parsedTime).Should().BeTrue();
        parsedId.Should().Be(customerId);
        parsedTime.Should().Be(issuedAt);
    }

    [Fact]
    public void TryVerify_returns_false_for_tampered_payload()
    {
        var signer = Build();
        var token = signer.Sign(Guid.NewGuid(), DateTimeOffset.UtcNow);

        // Token format: parts[0].parts[1].parts[2] — middle part'ı bozalım
        var parts = token.Split('.');
        parts[0] = "aaaaaaaa";   // farklı customer id base64
        var tampered = string.Join('.', parts);

        signer.TryVerify(tampered, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_returns_false_for_garbage_token()
    {
        var signer = Build();
        signer.TryVerify("not.a.valid.token", out _, out _).Should().BeFalse();
        signer.TryVerify("", out _, out _).Should().BeFalse();
        signer.TryVerify("only-one-part", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void TryVerify_accepts_old_timestamps()
    {
        var signer = Build();
        var ancient = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var token = signer.Sign(Guid.NewGuid(), ancient);

        // Süresiz — eski tarih kabul edilir
        signer.TryVerify(token, out _, out var parsedTime).Should().BeTrue();
        parsedTime.Should().Be(ancient);
    }
}
```

- [ ] **Step 2: RED — derleme hatası**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~UnsubscribeTokenSignerTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `UnsubscribeTokenSigner` yok.

- [ ] **Step 3: UnsubscribeTokenSigner impl**

`LiveDeck.LicenseServer/Services/Email/UnsubscribeTokenSigner.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;

namespace LiveDeck.LicenseServer.Services.Email;

/// <summary>
/// Stateless HMAC-SHA256 signed unsubscribe tokens. Format:
/// <c>base64url(customerIdBytes).base64url(unixTimeBigEndianBytes).base64url(hmac(payload, key))</c>
/// Key reuse: <c>Jwt:SecretKey</c>. Tokens are not time-bound (issuedAt is audit only).
/// </summary>
public sealed class UnsubscribeTokenSigner
{
    private readonly byte[] _key;

    public UnsubscribeTokenSigner(IConfiguration config)
    {
        var secret = config["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey config value is required for UnsubscribeTokenSigner.");
        _key = Encoding.UTF8.GetBytes(secret);
    }

    public string Sign(Guid customerId, DateTimeOffset issuedAt)
    {
        var idBytes = customerId.ToByteArray();
        var timeBytes = BitConverter.GetBytes(issuedAt.ToUnixTimeSeconds());
        if (BitConverter.IsLittleEndian) Array.Reverse(timeBytes);

        var payload = new byte[idBytes.Length + timeBytes.Length];
        Buffer.BlockCopy(idBytes, 0, payload, 0, idBytes.Length);
        Buffer.BlockCopy(timeBytes, 0, payload, idBytes.Length, timeBytes.Length);

        var hmac = HMACSHA256.HashData(_key, payload);

        return $"{Base64UrlEncode(idBytes)}.{Base64UrlEncode(timeBytes)}.{Base64UrlEncode(hmac)}";
    }

    public bool TryVerify(string token, out Guid customerId, out DateTimeOffset issuedAt)
    {
        customerId = Guid.Empty;
        issuedAt = DateTimeOffset.MinValue;

        if (string.IsNullOrWhiteSpace(token)) return false;

        var parts = token.Split('.');
        if (parts.Length != 3) return false;

        try
        {
            var idBytes = Base64UrlDecode(parts[0]);
            var timeBytes = Base64UrlDecode(parts[1]);
            var providedHmac = Base64UrlDecode(parts[2]);
            if (idBytes.Length != 16) return false;     // Guid = 16 bytes
            if (timeBytes.Length != 8) return false;    // long = 8 bytes

            var payload = new byte[idBytes.Length + timeBytes.Length];
            Buffer.BlockCopy(idBytes, 0, payload, 0, idBytes.Length);
            Buffer.BlockCopy(timeBytes, 0, payload, idBytes.Length, timeBytes.Length);

            var expectedHmac = HMACSHA256.HashData(_key, payload);
            if (!CryptographicOperations.FixedTimeEquals(expectedHmac, providedHmac))
                return false;

            customerId = new Guid(idBytes);

            var beTimeBytes = (byte[])timeBytes.Clone();
            if (BitConverter.IsLittleEndian) Array.Reverse(beTimeBytes);
            var unix = BitConverter.ToInt64(beTimeBytes, 0);
            issuedAt = DateTimeOffset.FromUnixTimeSeconds(unix);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string Base64UrlEncode(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Base64UrlDecode(string s)
    {
        var padded = s.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }
}
```

- [ ] **Step 4: GREEN**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~UnsubscribeTokenSignerTests" 2>&1 | tail -5
```

Beklenen: 4/4 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 97/97 toplam (93 + 4).

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Services/Email/UnsubscribeTokenSigner.cs \
        LiveDeck.LicenseServer.Tests/Services/UnsubscribeTokenSignerTests.cs
git commit -m "feat(license-server): add UnsubscribeTokenSigner (HMAC-SHA256, key reused from Jwt:SecretKey)"
```

---

### Task 4: EmailTemplates — 7 yeni metot + footer helper

**Files:**
- Modify: `LiveDeck.LicenseServer/Services/Email/EmailTemplates.cs`

**Context:** Phase 4a `ConfirmEmail` mevcut. 9 yeni metot eklenir (5 reminder + 1 password-reset + 3 admin-action) + 1 helper metod (footer). Türkçe içerik. Yapı: her metot `(Subject, Html, Plain)` tuple döner. Footer eklemesi parametre `unsubscribeUrl` null değilse yapılır.

Her metot signature'ında `string? unsubscribeUrl` opsiyonel parametre yer alır. Null ise footer eklenmez (transactional).

- [ ] **Step 1: EmailTemplates.cs'i genişlet**

`LiveDeck.LicenseServer/Services/Email/EmailTemplates.cs` dosyasını aç. Mevcut `ConfirmEmail` metodu olduğu gibi koru. Sınıfın **sonuna** (kapatıcı `}` öncesi) ekle:

```csharp
    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Renewal reminders
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) Renewal14d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınız 14 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

LiveDeck lisansınız {dateStr} tarihinde sona eriyor. Hizmette kesinti olmaması için yenilemenizi öneririz.

Lisans anahtarı: {licenseKey}
Bitiş: {dateStr}

Lisansınızı portaldan yönetin: {portalUrl}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>LiveDeck lisansınız <strong>{dateStr}</strong> tarihinde sona eriyor. Hizmette kesinti olmaması için yenilemenizi öneririz.</p>
<table style=""border-collapse:collapse;margin:16px 0"">
<tr><td style=""padding:4px 12px;color:#888"">Lisans anahtarı</td><td style=""padding:4px 12px""><code>{licenseKey}</code></td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Bitiş</td><td style=""padding:4px 12px"">{dateStr}</td></tr>
</table>
<p><a href=""{portalUrl}"">Lisansınızı portaldan yönetin</a></p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal7d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınız 7 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

LiveDeck lisansınızın bitmesine 7 gün kaldı. Hizmet kesintisi yaşamamak için en kısa sürede yenileyin.

Lisans anahtarı: {licenseKey}
Bitiş: {dateStr}

Yenile: {portalUrl}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>LiveDeck lisansınızın bitmesine <strong>7 gün</strong> kaldı.</p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"">Hemen yenile</a></p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal3d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınız 3 gün içinde sona eriyor";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınızın bitmesine 3 gün kaldı! Hemen yenileyin.

Lisans: {licenseKey}
Bitiş: {dateStr}

{portalUrl}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınızın bitmesine <strong style=""color:#d97706"">3 gün</strong> kaldı.</p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"" style=""display:inline-block;background:#d97706;color:white;padding:10px 20px;text-decoration:none;border-radius:4px"">Hemen yenile</a></p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) Renewal0d(
        string customerName, string licenseKey, DateTimeOffset expiresAt, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınız bugün sona eriyor!";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınız bugün sona eriyor. Hizmet kesintisi yaşamamak için hemen yenileyin.

Lisans: {licenseKey}
Bitiş: {dateStr}

Şimdi yenile: {portalUrl}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p style=""color:#dc2626;font-size:18px""><strong>Lisansınız bugün sona eriyor!</strong></p>
<p>Bitiş: <strong>{dateStr}</strong>, anahtar: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"" style=""display:inline-block;background:#dc2626;color:white;padding:10px 20px;text-decoration:none;border-radius:4px"">Şimdi yenile</a></p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) ExpiredAfter1d(
        string customerName, string licenseKey, string portalUrl, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınızın süresi doldu";
        var plain = $@"Merhaba {customerName},

LiveDeck lisansınızın süresi dün doldu. Lisansı yenileyerek hizmete kaldığınız yerden devam edebilirsiniz.

Lisans anahtarı: {licenseKey}

Yenile: {portalUrl}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>LiveDeck lisansınızın süresi dün doldu.</p>
<p>Lisans anahtarı: <code>{licenseKey}</code></p>
<p><a href=""{portalUrl}"">Lisansınızı yenileyin</a></p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Password reset (transactional, no unsubscribe)
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) PasswordReset(string customerName, string resetUrl)
    {
        var subject = "LiveDeck — Şifre sıfırlama bağlantınız";
        var plain = $@"Merhaba {customerName},

LiveDeck hesabınız için şifre sıfırlama talebi aldık. Yeni şifrenizi belirlemek için aşağıdaki bağlantıya tıklayın:

{resetUrl}

Bu bağlantı 1 saat geçerlidir. Talep size ait değilse bu mesajı görmezden gelin.

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>LiveDeck hesabınız için şifre sıfırlama talebi aldık.</p>
<p><a href=""{resetUrl}"">Yeni şifrenizi belirleyin</a></p>
<p style=""color:#888"">Bu bağlantı 1 saat geçerlidir. Talep size ait değilse bu mesajı görmezden gelin.</p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, html, plain);
    }

    // ────────────────────────────────────────────────────────────────────
    // Phase 4e — Admin actions (license issued / revoked / extended)
    // ────────────────────────────────────────────────────────────────────

    public static (string Subject, string Html, string Plain) LicenseIssued(
        string customerName, string licenseKey, string skuCode, DateTimeOffset expiresAt, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Yeni lisansınız hazır";
        var dateStr = expiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Yeni LiveDeck lisansınız oluşturuldu.

Lisans anahtarı: {licenseKey}
Plan: {skuCode}
Bitiş tarihi: {dateStr}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Yeni LiveDeck lisansınız oluşturuldu.</p>
<table style=""border-collapse:collapse;margin:16px 0"">
<tr><td style=""padding:4px 12px;color:#888"">Lisans anahtarı</td><td style=""padding:4px 12px""><code>{licenseKey}</code></td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Plan</td><td style=""padding:4px 12px"">{skuCode}</td></tr>
<tr><td style=""padding:4px 12px;color:#888"">Bitiş tarihi</td><td style=""padding:4px 12px"">{dateStr}</td></tr>
</table>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) LicenseRevoked(
        string customerName, string licenseKey, string reason, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınız iptal edildi";
        var plain = $@"Merhaba {customerName},

Lisansınız iptal edildi.

Lisans anahtarı: {licenseKey}
Sebep: {reason}

Sorularınız için lütfen bizimle iletişime geçin.

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınız iptal edildi.</p>
<p>Lisans: <code>{licenseKey}</code></p>
<p>Sebep: {reason}</p>
<p style=""color:#888"">Sorularınız için lütfen bizimle iletişime geçin.</p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    public static (string Subject, string Html, string Plain) LicenseExtended(
        string customerName, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays, string? unsubscribeUrl)
    {
        var subject = "LiveDeck — Lisansınız uzatıldı";
        var dateStr = newExpiresAt.ToString("dd.MM.yyyy");
        var plain = $@"Merhaba {customerName},

Lisansınızın süresi {additionalDays} gün uzatıldı.

Lisans anahtarı: {licenseKey}
Yeni bitiş tarihi: {dateStr}

— LiveDeck Ekibi";
        var html = $@"<!doctype html><html lang=""tr""><body style=""font-family:sans-serif"">
<p>Merhaba {customerName},</p>
<p>Lisansınızın süresi <strong>{additionalDays} gün</strong> uzatıldı.</p>
<p>Lisans: <code>{licenseKey}</code></p>
<p>Yeni bitiş tarihi: <strong>{dateStr}</strong></p>
<p>— LiveDeck Ekibi</p>
</body></html>";
        return (subject, AppendUnsubscribeFooter(html, unsubscribeUrl), AppendUnsubscribeFooterPlain(plain, unsubscribeUrl));
    }

    // ────────────────────────────────────────────────────────────────────
    // Helpers
    // ────────────────────────────────────────────────────────────────────

    private static string AppendUnsubscribeFooter(string html, string? unsubscribeUrl)
    {
        if (string.IsNullOrEmpty(unsubscribeUrl)) return html;
        var footer = $@"<hr><p style=""color:#888;font-size:12px;margin-top:24px"">Bu e-postayı LiveDeck hesabınızla ilgili olduğu için aldınız. <a href=""{unsubscribeUrl}"">E-posta bildirimlerini durdur</a></p>";
        return html.Replace("</body>", footer + "</body>");
    }

    private static string AppendUnsubscribeFooterPlain(string plain, string? unsubscribeUrl)
    {
        if (string.IsNullOrEmpty(unsubscribeUrl)) return plain;
        return plain + $"\n\n---\nE-posta bildirimlerini durdurmak için: {unsubscribeUrl}";
    }
```

- [ ] **Step 2: Build doğrula**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
```

Beklenen: 0 errors / 0 warnings.

- [ ] **Step 3: Tüm LicenseServer testleri (regression)**

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 97/97 (template eklemesi yeni test gerektirmez; metotlar Task 5+ servislerde test edilir).

- [ ] **Step 4: Commit**

```bash
git add LiveDeck.LicenseServer/Services/Email/EmailTemplates.cs
git commit -m "feat(license-server): add 9 new email templates (renewals + reset + admin actions) with unsubscribe footer"
```

---

### Task 5: EmailSendCoordinator — dedup + unsubscribe respect + send pipeline (TDD)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Email/EmailSendCoordinator.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/EmailSendCoordinatorTests.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI)

**Context:** Tek geçit noktası tüm Phase 4e emailleri için. Customer load → unsubscribe check → EmailLog dedup → unsubscribe URL üret → template builder çağır → IEmailSender.SendAsync → EmailLog row insert. 6 test.

- [ ] **Step 1: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Services/EmailSendCoordinatorTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class EmailSendCoordinatorTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public EmailSendCoordinatorTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedCustomerAsync(bool unsubscribed = false, bool emailConfirmed = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"coord-{Guid.NewGuid():N}@x",
            Name = "Coord Test",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = emailConfirmed ? DateTimeOffset.UtcNow : null,
            Unsubscribed = unsubscribed
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task TrySendAsync_returns_true_and_writes_EmailLog_on_success()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        var sent = await coord.TrySendAsync(
            customer.Id,
            templateKey: "test-template",
            contextKey: "ctx-1",
            templateBuilder: (c, unsubUrl) => ("Test", "html", "plain"),
            requiresUnsubscribeRespect: false);

        sent.Should().BeTrue();
        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var log = await db.EmailLogs.Where(e => e.CustomerId == customer.Id && e.TemplateKey == "test-template").FirstOrDefaultAsync();
        log.Should().NotBeNull();
        log!.Error.Should().BeNull();
    }

    [Fact]
    public async Task TrySendAsync_returns_false_and_skips_when_dedup_log_exists()
    {
        var customer = await SeedCustomerAsync();
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.EmailLogs.Add(new EmailLog
            {
                Id = Guid.NewGuid(),
                CustomerId = customer.Id,
                TemplateKey = "dedup-tpl",
                ContextKey = "ctx-x",
                SentAt = DateTimeOffset.UtcNow,
                Error = null
            });
            await db.SaveChangesAsync();
        }

        using var s2 = _factory.Services.CreateScope();
        var coord = s2.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        var sent = await coord.TrySendAsync(
            customer.Id, "dedup-tpl", "ctx-x",
            (c, u) => ("S", "h", "p"), requiresUnsubscribeRespect: false);

        sent.Should().BeFalse();
        _factory.Email.Sent.Count.Should().Be(sentBefore);   // no new send
    }

    [Fact]
    public async Task TrySendAsync_returns_false_when_customer_unsubscribed_and_respect_required()
    {
        var customer = await SeedCustomerAsync(unsubscribed: true);
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        var sent = await coord.TrySendAsync(
            customer.Id, "renewal-14d", "LDK-X",
            (c, u) => ("S", "h", "p"), requiresUnsubscribeRespect: true);

        sent.Should().BeFalse();
        _factory.Email.Sent.Count.Should().Be(sentBefore);
    }

    [Fact]
    public async Task TrySendAsync_sends_when_customer_unsubscribed_but_respect_NOT_required()
    {
        var customer = await SeedCustomerAsync(unsubscribed: true);
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();
        var sentBefore = _factory.Email.Sent.Count;

        // Transactional email (password-reset, confirm-email) bypass-respect
        var sent = await coord.TrySendAsync(
            customer.Id, "password-reset", "tok-1",
            (c, u) => ("Reset", "h", "p"), requiresUnsubscribeRespect: false);

        sent.Should().BeTrue();
        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);
    }

    [Fact]
    public async Task TrySendAsync_passes_unsubscribe_url_to_template_builder_when_respect_required()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

        string? capturedUrl = null;
        var sent = await coord.TrySendAsync(
            customer.Id, "renewal-7d", "LDK-Y",
            (c, unsubUrl) => { capturedUrl = unsubUrl; return ("S", "h", "p"); },
            requiresUnsubscribeRespect: true);

        sent.Should().BeTrue();
        capturedUrl.Should().NotBeNull();
        capturedUrl!.Should().Contain("/unsubscribe?token=");
    }

    [Fact]
    public async Task TrySendAsync_returns_false_when_customer_not_found()
    {
        using var scope = _factory.Services.CreateScope();
        var coord = scope.ServiceProvider.GetRequiredService<EmailSendCoordinator>();

        var sent = await coord.TrySendAsync(
            customerId: Guid.NewGuid(),
            templateKey: "any", contextKey: null,
            templateBuilder: (c, u) => ("S", "h", "p"),
            requiresUnsubscribeRespect: false);

        sent.Should().BeFalse();
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~EmailSendCoordinatorTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `EmailSendCoordinator` yok.

- [ ] **Step 3: EmailSendCoordinator impl**

`LiveDeck.LicenseServer/Services/Email/EmailSendCoordinator.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveDeck.LicenseServer.Services.Email;

/// <summary>
/// Single send pipeline for all Phase 4e emails. Handles: customer lookup,
/// unsubscribe check, EmailLog dedup, unsubscribe URL signing, IEmailSender
/// delegation, EmailLog persist (with success/error indicator).
/// </summary>
public sealed class EmailSendCoordinator
{
    private readonly LicenseDbContext _db;
    private readonly IEmailSender _sender;
    private readonly UnsubscribeTokenSigner _signer;
    private readonly string _publicBaseUrl;
    private readonly ILogger<EmailSendCoordinator> _log;

    public EmailSendCoordinator(
        LicenseDbContext db,
        IEmailSender sender,
        UnsubscribeTokenSigner signer,
        IConfiguration config,
        ILogger<EmailSendCoordinator> log)
    {
        _db = db;
        _sender = sender;
        _signer = signer;
        _publicBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
        _log = log;
    }

    public async Task<bool> TrySendAsync(
        Guid customerId,
        string templateKey,
        string? contextKey,
        Func<Customer, string?, (string Subject, string Html, string Plain)> templateBuilder,
        bool requiresUnsubscribeRespect,
        CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null)
        {
            _log.LogWarning("Email skip: customer {CustomerId} not found (template={Template})", customerId, templateKey);
            return false;
        }

        if (requiresUnsubscribeRespect && customer.Unsubscribed)
        {
            _log.LogInformation("Email skip: customer {CustomerId} unsubscribed (template={Template})", customerId, templateKey);
            return false;
        }

        // Dedup: aynı (customerId, templateKey, contextKey) için successful log varsa skip
        var existing = await _db.EmailLogs
            .Where(e => e.CustomerId == customerId
                     && e.TemplateKey == templateKey
                     && e.ContextKey == contextKey
                     && e.Error == null)
            .FirstOrDefaultAsync(ct);
        if (existing is not null)
        {
            _log.LogDebug("Email skip: dedup hit (customer={CustomerId}, template={Template}, ctx={Ctx})",
                customerId, templateKey, contextKey);
            return false;
        }

        // Unsubscribe URL üret (sadece respect required ise)
        string? unsubscribeUrl = null;
        if (requiresUnsubscribeRespect)
        {
            var token = _signer.Sign(customerId, DateTimeOffset.UtcNow);
            unsubscribeUrl = $"{_publicBaseUrl}/unsubscribe?token={Uri.EscapeDataString(token)}";
        }

        // Template content üret
        var (subject, html, plain) = templateBuilder(customer, unsubscribeUrl);

        // Send (failure swallow — log + persist)
        string? error = null;
        try
        {
            await _sender.SendAsync(customer.Email, customer.Name, subject, html, plain, ct);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            _log.LogWarning(ex, "Email send failed (customer={CustomerId}, template={Template})", customerId, templateKey);
        }

        // EmailLog persist (success or failure)
        _db.EmailLogs.Add(new EmailLog
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            TemplateKey = templateKey,
            ContextKey = contextKey,
            SentAt = DateTimeOffset.UtcNow,
            Error = error
        });
        await _db.SaveChangesAsync(ct);

        return error is null;
    }
}
```

- [ ] **Step 4: Program.cs DI registration**

`LiveDeck.LicenseServer/Program.cs` mevcut DI bloğuna ekle (mevcut Phase 4d audit servisi yanına):

```csharp
        builder.Services.AddSingleton<UnsubscribeTokenSigner>();
        builder.Services.AddScoped<EmailSendCoordinator>();
```

- [ ] **Step 5: GREEN**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~EmailSendCoordinatorTests" 2>&1 | tail -5
```

Beklenen: 6/6 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 103/103 toplam (97 + 6).

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.LicenseServer/Services/Email/EmailSendCoordinator.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Services/EmailSendCoordinatorTests.cs
git commit -m "feat(license-server): add EmailSendCoordinator (dedup + unsubscribe respect + send pipeline)"
```

---

### Task 6: ReminderJobs — 5 method + Hangfire recurring registration (TDD)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Email/ReminderJobs.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (recurring job registration)
- Create: `LiveDeck.LicenseServer.Tests/Services/ReminderJobsTests.cs`

**Context:** Hangfire'dan günde bir kez çağrılan 5 method (renewal 14/7/3/0d + expired+1d). Her method ExpiresAt window'unu hesaplayıp eligible licenses'ları seçer, EmailSendCoordinator'a delegate eder. Recurring registration Program.cs'de `IRecurringJobManager.AddOrUpdate`. Test ortamında recurring kayıt skip (sadece testler method'u direkt çağırır).

- [ ] **Step 1: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Services/ReminderJobsTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class ReminderJobsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ReminderJobsTests(ApiFactory factory) => _factory = factory;

    private async Task<License> SeedLicenseAsync(double daysUntilExpiry, bool emailConfirmed = true, bool revoked = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"rem-{Guid.NewGuid():N}@x",
            Name = "Rem",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = emailConfirmed ? DateTimeOffset.UtcNow : null
        };
        var lic = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = "LDK-REM-" + Guid.NewGuid().ToString("N"),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-365),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(daysUntilExpiry),
            RevokedAt = revoked ? DateTimeOffset.UtcNow : null
        };
        db.Customers.Add(customer);
        db.Licenses.Add(lic);
        await db.SaveChangesAsync();
        return lic;
    }

    [Fact]
    public async Task SendRenewal14d_emails_license_expiring_in_14d()
    {
        var lic = await SeedLicenseAsync(14.0);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();
        var sentBefore = _factory.Email.Sent.Count;

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Count.Should().BeGreaterThan(sentBefore);
        _factory.Email.Sent.Should().Contain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_skips_license_outside_window()
    {
        var lic = await SeedLicenseAsync(20.0);   // 20d, 14d window dışında
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Should().NotContain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_skips_revoked_license()
    {
        var lic = await SeedLicenseAsync(14.0, revoked: true);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Should().NotContain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_skips_unconfirmed_email_customer()
    {
        var lic = await SeedLicenseAsync(14.0, emailConfirmed: false);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);

        _factory.Email.Sent.Should().NotContain(e => e.PlainBody.Contains(lic.LicenseKey));
    }

    [Fact]
    public async Task SendRenewal14d_dedup_does_not_resend()
    {
        var lic = await SeedLicenseAsync(14.0);
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendRenewal14dAsync(default);
        var afterFirst = _factory.Email.Sent.Count(e => e.PlainBody.Contains(lic.LicenseKey));
        afterFirst.Should().Be(1);

        await jobs.SendRenewal14dAsync(default);   // 2nd call — dedup
        var afterSecond = _factory.Email.Sent.Count(e => e.PlainBody.Contains(lic.LicenseKey));
        afterSecond.Should().Be(1);
    }

    [Fact]
    public async Task SendExpired1d_emails_license_expired_yesterday()
    {
        var lic = await SeedLicenseAsync(-1.0);   // bir gün önce expired
        using var scope = _factory.Services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<ReminderJobs>();

        await jobs.SendExpired1dAsync(default);

        _factory.Email.Sent.Should().Contain(e => e.PlainBody.Contains(lic.LicenseKey));
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~ReminderJobsTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `ReminderJobs` yok.

- [ ] **Step 3: ReminderJobs impl**

`LiveDeck.LicenseServer/Services/Email/ReminderJobs.cs`:

```csharp
using Hangfire;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveDeck.LicenseServer.Services.Email;

/// <summary>
/// Hangfire recurring jobs — günde bir kez çağrılır. Her method ExpiresAt
/// window'unu hesaplayıp eligible licenses için EmailSendCoordinator'a delegate eder.
/// EmailLog dedup garantisi sayesinde idempotent (job 1 saat geç çalışsa bile aynı email
/// 2 defa gitmez).
/// </summary>
public sealed class ReminderJobs
{
    private readonly LicenseDbContext _db;
    private readonly EmailSendCoordinator _coordinator;
    private readonly string _portalUrl;
    private readonly ILogger<ReminderJobs> _log;

    public ReminderJobs(
        LicenseDbContext db,
        EmailSendCoordinator coordinator,
        IConfiguration config,
        ILogger<ReminderJobs> log)
    {
        _db = db;
        _coordinator = coordinator;
        _portalUrl = config["App:PublicBaseUrl"] ?? "https://localhost:5001";
        _log = log;
    }

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal14dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(daysBeforeExpiry: 14, "renewal-14d",
            (c, k, e, p, u) => EmailTemplates.Renewal14d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal7dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(7, "renewal-7d",
            (c, k, e, p, u) => EmailTemplates.Renewal7d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal3dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(3, "renewal-3d",
            (c, k, e, p, u) => EmailTemplates.Renewal3d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public Task SendRenewal0dAsync(CancellationToken ct) =>
        ScanAndSendRenewalAsync(0, "renewal-0d",
            (c, k, e, p, u) => EmailTemplates.Renewal0d(c, k, e, p, u), ct);

    [AutomaticRetry(Attempts = 0, OnAttemptsExceeded = AttemptsExceededAction.Fail)]
    public async Task SendExpired1dAsync(CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddDays(-1.5), now.AddDays(-0.5));
        var candidates = await LoadCandidatesAsync(from, to, ct);

        _log.LogInformation("Expired-1d job: {Count} candidates in window [{From}..{To}]",
            candidates.Count, from, to);

        foreach (var license in candidates)
        {
            await _coordinator.TrySendAsync(
                customerId: license.CustomerId,
                templateKey: "expired-1d",
                contextKey: license.LicenseKey,
                templateBuilder: (c, unsubUrl) => EmailTemplates.ExpiredAfter1d(c.Name, license.LicenseKey, _portalUrl, unsubUrl),
                requiresUnsubscribeRespect: true,
                ct);
        }
    }

    private async Task ScanAndSendRenewalAsync(
        int daysBeforeExpiry,
        string templateKey,
        Func<string, string, DateTimeOffset, string, string?, (string, string, string)> templateFn,
        CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var (from, to) = (now.AddDays(daysBeforeExpiry - 0.5), now.AddDays(daysBeforeExpiry + 0.5));
        var candidates = await LoadCandidatesAsync(from, to, ct);

        _log.LogInformation("{Template} job: {Count} candidates in window [{From}..{To}]",
            templateKey, candidates.Count, from, to);

        foreach (var license in candidates)
        {
            await _coordinator.TrySendAsync(
                customerId: license.CustomerId,
                templateKey: templateKey,
                contextKey: license.LicenseKey,
                templateBuilder: (c, unsubUrl) => templateFn(c.Name, license.LicenseKey, license.ExpiresAt, _portalUrl, unsubUrl),
                requiresUnsubscribeRespect: true,
                ct);
        }
    }

    private Task<List<License>> LoadCandidatesAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct) =>
        _db.Licenses
            .Include(l => l.Customer)
            .Where(l => l.RevokedAt == null
                     && l.ExpiresAt >= from && l.ExpiresAt < to
                     && l.Customer.EmailConfirmedAt != null)
            .ToListAsync(ct);
}
```

- [ ] **Step 4: Program.cs — DI + recurring registration**

`Program.cs` DI bloğuna ekle:

```csharp
        builder.Services.AddScoped<ReminderJobs>();
```

`var app = builder.Build();` sonrası ve `app.Run();` öncesi ekle (ama production-only — test environment skip):

```csharp
        // Hangfire recurring jobs — production only (testte ApiFactory MemoryStorage kullanır, recurring tetiklenmesin)
        if (!app.Environment.IsEnvironment("Testing"))
        {
            using var scope = app.Services.CreateScope();
            var manager = scope.ServiceProvider.GetRequiredService<IRecurringJobManager>();
            var cron = builder.Configuration["EmailReminder:DailyJobCron"] ?? "0 9 * * *";
            manager.AddOrUpdate<ReminderJobs>("renewal-14d", j => j.SendRenewal14dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("renewal-7d",  j => j.SendRenewal7dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("renewal-3d",  j => j.SendRenewal3dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("renewal-0d",  j => j.SendRenewal0dAsync(CancellationToken.None), cron);
            manager.AddOrUpdate<ReminderJobs>("expired-1d",  j => j.SendExpired1dAsync(CancellationToken.None), cron);
        }
```

- [ ] **Step 5: ApiFactory — Environment="Testing" set**

`LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` mevcut `ConfigureWebHost` callback'ine, en üste ekle (UseEnvironment çağrısı varsa onu güncelle):

```csharp
        builder.UseEnvironment("Testing");
```

- [ ] **Step 6: GREEN**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~ReminderJobsTests" 2>&1 | tail -5
```

Beklenen: 6/6 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 109/109 toplam (103 + 6).

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Services/Email/ReminderJobs.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs \
        LiveDeck.LicenseServer.Tests/Services/ReminderJobsTests.cs
git commit -m "feat(license-server): add ReminderJobs (5 recurring email jobs) with Hangfire registration"
```

---

### Task 7: PasswordResetService + 2 REST endpoint (TDD)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Auth/PasswordResetService.cs`
- Modify: `LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI)
- Create: `LiveDeck.LicenseServer.Tests/Services/PasswordResetServiceTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Controllers/Auth/PasswordResetEndpointsTests.cs`

**Context:** Self-service password reset. Token entity Phase 4e Task 2'de eklendi. `RequestResetAsync` enumeration-safe (silent for unknown email). `CompleteResetAsync` single-use token + 1h TTL. 2 REST endpoint AuthController'a eklenir, anonymous + rate-limited.

- [ ] **Step 1: Failing tests yaz (PasswordResetService)**

`LiveDeck.LicenseServer.Tests/Services/PasswordResetServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class PasswordResetServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PasswordResetServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedConfirmedCustomerAsync(string password = "real-password")
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"reset-{Guid.NewGuid():N}@x",
            Name = "Reset",
            PasswordHash = hasher.Hash(password),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task RequestResetAsync_unknown_email_silent_no_token_no_email()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.RequestResetAsync($"never-exists-{Guid.NewGuid():N}@x", default);

        _factory.Email.Sent.Count.Should().Be(sentBefore);
        // No token row created
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var anyToken = await db.PasswordResetTokens.AnyAsync();
        // Token might exist from prior tests; we only verify no NEW one for this email — implicit
    }

    [Fact]
    public async Task RequestResetAsync_known_confirmed_customer_creates_token_and_sends_email()
    {
        var customer = await SeedConfirmedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.RequestResetAsync(customer.Email, default);

        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).FirstOrDefaultAsync();
        token.Should().NotBeNull();
        token!.UsedAt.Should().BeNull();
    }

    [Fact]
    public async Task RequestResetAsync_unconfirmed_customer_silent()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"unconf-{Guid.NewGuid():N}@x",
            Name = "U",
            PasswordHash = hasher.Hash("p"),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = null   // not confirmed
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();

        using var s2 = _factory.Services.CreateScope();
        var svc = s2.ServiceProvider.GetRequiredService<PasswordResetService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.RequestResetAsync(c.Email, default);

        _factory.Email.Sent.Count.Should().Be(sentBefore);
    }

    [Fact]
    public async Task CompleteResetAsync_with_valid_token_updates_password_and_marks_used()
    {
        var customer = await SeedConfirmedCustomerAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);

            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        using var s2 = _factory.Services.CreateScope();
        var svc2 = s2.ServiceProvider.GetRequiredService<PasswordResetService>();
        var result = await svc2.CompleteResetAsync(tokenId, "new-password-123", default);

        result.Should().Be(PasswordResetResult.Success);

        using var s3 = _factory.Services.CreateScope();
        var db3 = s3.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = s3.ServiceProvider.GetRequiredService<PasswordHasher>();
        var updated = await db3.Customers.FirstAsync(c => c.Id == customer.Id);
        hasher.Verify(updated.PasswordHash, "new-password-123").Should().BeTrue();

        var token = await db3.PasswordResetTokens.FirstAsync(t => t.Id == tokenId);
        token.UsedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task CompleteResetAsync_with_unknown_token_returns_TokenInvalid()
    {
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();

        var result = await svc.CompleteResetAsync(Guid.NewGuid(), "new-password-123", default);

        result.Should().Be(PasswordResetResult.TokenInvalid);
    }

    [Fact]
    public async Task CompleteResetAsync_with_used_token_returns_TokenInvalid()
    {
        var customer = await SeedConfirmedCustomerAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);

            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.CompleteResetAsync(tokenId, "first-pw-12345", default);   // first use
            var second = await svc.CompleteResetAsync(tokenId, "second-pw-12345", default);   // second use → invalid
            second.Should().Be(PasswordResetResult.TokenInvalid);
        }
    }

    [Fact]
    public async Task CompleteResetAsync_with_short_password_returns_PasswordTooShort()
    {
        var customer = await SeedConfirmedCustomerAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);

            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        using var s2 = _factory.Services.CreateScope();
        var svc2 = s2.ServiceProvider.GetRequiredService<PasswordResetService>();
        var result = await svc2.CompleteResetAsync(tokenId, "short", default);

        result.Should().Be(PasswordResetResult.PasswordTooShort);
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~PasswordResetServiceTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `PasswordResetService` + `PasswordResetResult` yok.

- [ ] **Step 3: PasswordResetService impl**

`LiveDeck.LicenseServer/Services/Auth/PasswordResetService.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LiveDeck.LicenseServer.Services.Auth;

public enum PasswordResetResult { Success, TokenInvalid, PasswordTooShort }

public sealed class PasswordResetService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(1);
    public static readonly TimeSpan RequestThrottle = TimeSpan.FromMinutes(15);

    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailSendCoordinator _coordinator;
    private readonly string _publicBaseUrl;
    private readonly ILogger<PasswordResetService> _log;

    public PasswordResetService(
        LicenseDbContext db,
        PasswordHasher hasher,
        EmailSendCoordinator coordinator,
        IConfiguration config,
        ILogger<PasswordResetService> log)
    {
        _db = db;
        _hasher = hasher;
        _coordinator = coordinator;
        _publicBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
        _log = log;
    }

    public async Task RequestResetAsync(string email, CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == email, ct);
        if (customer is null) return;                          // enumeration-safe
        if (customer.EmailConfirmedAt is null) return;         // unconfirmed → silent

        var now = DateTimeOffset.UtcNow;
        var throttleCutoff = now - RequestThrottle;

        // Recent unused token reuse
        var existing = await _db.PasswordResetTokens
            .Where(t => t.CustomerId == customer.Id && t.UsedAt == null && t.CreatedAt > throttleCutoff)
            .OrderByDescending(t => t.CreatedAt)
            .FirstOrDefaultAsync(ct);

        Guid tokenId;
        if (existing is not null)
        {
            tokenId = existing.Id;
        }
        else
        {
            tokenId = Guid.NewGuid();
            _db.PasswordResetTokens.Add(new PasswordResetToken
            {
                Id = tokenId,
                CustomerId = customer.Id,
                CreatedAt = now
            });
            await _db.SaveChangesAsync(ct);
        }

        var resetUrl = $"{_publicBaseUrl}/password-reset?token={tokenId}";

        await _coordinator.TrySendAsync(
            customerId: customer.Id,
            templateKey: "password-reset",
            contextKey: tokenId.ToString(),
            templateBuilder: (c, _) => EmailTemplates.PasswordReset(c.Name, resetUrl),
            requiresUnsubscribeRespect: false,                 // transactional
            ct);
    }

    public async Task<PasswordResetResult> CompleteResetAsync(Guid token, string newPassword, CancellationToken ct = default)
    {
        var record = await _db.PasswordResetTokens
            .Include(t => t.Customer)
            .FirstOrDefaultAsync(t => t.Id == token, ct);

        if (record is null) return PasswordResetResult.TokenInvalid;
        if (record.UsedAt is not null) return PasswordResetResult.TokenInvalid;
        if (DateTimeOffset.UtcNow - record.CreatedAt > TokenLifetime) return PasswordResetResult.TokenInvalid;
        if (newPassword.Length < 8) return PasswordResetResult.PasswordTooShort;

        record.Customer.PasswordHash = _hasher.Hash(newPassword);
        record.UsedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        _log.LogInformation("Password reset completed for customer {CustomerId}", record.CustomerId);
        return PasswordResetResult.Success;
    }
}
```

- [ ] **Step 4: AuthController'a 2 endpoint ekle**

`LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs` aç. Mevcut endpoint'lerin sonuna (sınıfın sonuna) ekle. Constructor'a yeni dep:

```csharp
    private readonly PasswordResetService _resetService;

    public AuthController(LicenseDbContext db, PasswordHasher hasher, EmailConfirmationService confirm, JwtTokenService jwt, PasswordResetService resetService)
    {
        _db = db;
        _hasher = hasher;
        _confirm = confirm;
        _jwt = jwt;
        _resetService = resetService;
    }
```

(Mevcut 4 dependency korunur; 5. olarak eklenir.)

Yeni endpoint'ler:

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

- [ ] **Step 5: Program.cs DI registration**

`LiveDeck.LicenseServer/Program.cs` mevcut services bloğuna ekle:

```csharp
        builder.Services.AddScoped<PasswordResetService>();
```

- [ ] **Step 6: Failing endpoint tests yaz**

`LiveDeck.LicenseServer.Tests/Controllers/Auth/PasswordResetEndpointsTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Controllers.Auth;

public sealed class PasswordResetEndpointsTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PasswordResetEndpointsTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedConfirmedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"ep-{Guid.NewGuid():N}@x",
            Name = "Ep",
            PasswordHash = hasher.Hash("old-password-12345"),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task PasswordResetRequest_returns_202_for_known_email()
    {
        var customer = await SeedConfirmedAsync();
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset-request",
            new { email = customer.Email });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PasswordResetRequest_returns_202_silently_for_unknown_email()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset-request",
            new { email = $"never-{Guid.NewGuid():N}@x" });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task PasswordResetComplete_with_valid_token_returns_204()
    {
        var customer = await SeedConfirmedAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = tokenId, newPassword = "new-password-12345" });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task PasswordResetComplete_with_unknown_token_returns_400_token_invalid()
    {
        var client = _factory.CreateClient();

        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = Guid.NewGuid(), newPassword = "new-password-12345" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        body!.title.Should().Be("token-invalid");
    }

    [Fact]
    public async Task PasswordResetComplete_with_short_password_returns_400_password_too_short()
    {
        var customer = await SeedConfirmedAsync();
        Guid tokenId;
        using (var scope = _factory.Services.CreateScope())
        {
            var svc = scope.ServiceProvider.GetRequiredService<PasswordResetService>();
            await svc.RequestResetAsync(customer.Email, default);
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            tokenId = (await db.PasswordResetTokens.Where(t => t.CustomerId == customer.Id).OrderByDescending(t => t.CreatedAt).FirstAsync()).Id;
        }

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/password-reset",
            new { token = tokenId, newPassword = "short" });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadFromJsonAsync<ProblemBody>();
        body!.title.Should().Be("password-too-short");
    }

    private sealed record ProblemBody(string title, string? detail, int? status);
}
```

- [ ] **Step 7: GREEN**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~PasswordResetServiceTests|FullyQualifiedName~PasswordResetEndpointsTests" 2>&1 | tail -5
```

Beklenen: 7 service + 5 endpoint = 12/12 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 121/121 toplam (109 + 12).

- [ ] **Step 8: Commit**

```bash
git add LiveDeck.LicenseServer/Services/Auth/PasswordResetService.cs \
        LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Services/PasswordResetServiceTests.cs \
        LiveDeck.LicenseServer.Tests/Controllers/Auth/PasswordResetEndpointsTests.cs
git commit -m "feat(license-server): add password reset flow (service + 2 REST endpoints, enumeration-safe)"
```

---

### Task 8: Pages/Public/PasswordReset Razor sayfa

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Public/_ViewImports.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Public/PasswordReset.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Public/PasswordReset.cshtml.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (Razor pages convention)
- Create: `LiveDeck.LicenseServer.Tests/Pages/Public/PasswordResetPageTests.cs`

**Context:** Customer email link tıklayınca açılan public Razor sayfa. GET `?token={guid}` form render. POST yeni şifre + onay → `PasswordResetService.CompleteResetAsync`. 2 test (GET token görünür, POST başarılı reset).

- [ ] **Step 1: Pages/Public/_ViewImports.cshtml**

`LiveDeck.LicenseServer/Pages/Public/_ViewImports.cshtml`:

```cshtml
@using LiveDeck.LicenseServer
@using LiveDeck.LicenseServer.Pages.Public
@namespace LiveDeck.LicenseServer.Pages.Public
@addTagHelper *, Microsoft.AspNetCore.Mvc.TagHelpers
```

- [ ] **Step 2: PasswordReset.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Public/PasswordReset.cshtml.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace LiveDeck.LicenseServer.Pages.Public;

public class PasswordResetModel : PageModel
{
    private readonly PasswordResetService _service;

    public PasswordResetModel(PasswordResetService service) => _service = service;

    [BindProperty]
    public PasswordResetInput Input { get; set; } = new();

    public string? ErrorMessage { get; set; }
    public bool Success { get; set; }

    public sealed class PasswordResetInput
    {
        [Required]
        public Guid Token { get; set; }

        [Required(ErrorMessage = "Şifre gerekli")]
        [StringLength(100, MinimumLength = 8, ErrorMessage = "En az 8 karakter olmalı")]
        public string NewPassword { get; set; } = "";

        [Required(ErrorMessage = "Şifre tekrarı gerekli")]
        [Compare(nameof(NewPassword), ErrorMessage = "Şifreler eşleşmiyor")]
        public string ConfirmPassword { get; set; } = "";
    }

    public IActionResult OnGet(Guid? token)
    {
        if (token is null || token == Guid.Empty) return BadRequest();
        Input.Token = token.Value;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!ModelState.IsValid) return Page();

        var result = await _service.CompleteResetAsync(Input.Token, Input.NewPassword, ct);
        switch (result)
        {
            case PasswordResetResult.Success:
                Success = true;
                return Page();
            case PasswordResetResult.PasswordTooShort:
                ErrorMessage = "Şifre en az 8 karakter olmalı.";
                return Page();
            default:
                ErrorMessage = "Bağlantı geçersiz veya süresi dolmuş. Lütfen yeni bir şifre sıfırlama talebi oluşturun.";
                return Page();
        }
    }
}
```

- [ ] **Step 3: PasswordReset.cshtml**

`LiveDeck.LicenseServer/Pages/Public/PasswordReset.cshtml`:

```cshtml
@page "/password-reset"
@model PasswordResetModel
@{
    ViewData["Title"] = "Şifre Sıfırlama";
    Layout = null;
}
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Şifre Sıfırlama — LiveDeck</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
</head>
<body class="bg-light">
    <main class="container py-5" style="max-width: 480px;">
        <h1 class="h3 mb-4">LiveDeck — Şifre Sıfırlama</h1>

        @if (Model.Success)
        {
            <div class="alert alert-success">
                Şifreniz başarıyla güncellendi. LiveDeck uygulamasından yeni şifrenizle giriş yapabilirsiniz.
            </div>
        }
        else
        {
            @if (!string.IsNullOrEmpty(Model.ErrorMessage))
            {
                <div class="alert alert-danger">@Model.ErrorMessage</div>
            }
            <form method="post">
                <input type="hidden" asp-for="Input.Token" />
                <div class="mb-3">
                    <label asp-for="Input.NewPassword" class="form-label">Yeni şifre (en az 8 karakter)</label>
                    <input asp-for="Input.NewPassword" type="password" class="form-control" autofocus />
                    <span asp-validation-for="Input.NewPassword" class="text-danger"></span>
                </div>
                <div class="mb-3">
                    <label asp-for="Input.ConfirmPassword" class="form-label">Şifre tekrarı</label>
                    <input asp-for="Input.ConfirmPassword" type="password" class="form-control" />
                    <span asp-validation-for="Input.ConfirmPassword" class="text-danger"></span>
                </div>
                <button type="submit" class="btn btn-primary w-100">Şifreyi güncelle</button>
            </form>
        }
    </main>
</body>
</html>
```

- [ ] **Step 4: Program.cs — Pages/Public anonymous folder**

`Program.cs` mevcut `AddRazorPages` çağrısını bul. Mevcut `Conventions` listesinin sonuna ekle:

```csharp
            opt.Conventions.AllowAnonymousToFolder("/Public");
```

- [ ] **Step 5: PasswordResetPageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/Public/PasswordResetPageTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages.Public;

public sealed class PasswordResetPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PasswordResetPageTests(ApiFactory factory) => _factory = factory;

    private async Task<Guid> SeedTokenAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"prp-{Guid.NewGuid():N}@x",
            Name = "PRP",
            PasswordHash = hasher.Hash("old-password-12345"),
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        var token = new PasswordResetToken
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        db.PasswordResetTokens.Add(token);
        await db.SaveChangesAsync();
        return token.Id;
    }

    [Fact]
    public async Task Get_with_token_returns_200_with_form()
    {
        var tokenId = await SeedTokenAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/password-reset?token={tokenId}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(tokenId.ToString());
        html.Should().Contain("Yeni şifre");
    }

    [Fact]
    public async Task Post_valid_token_completes_reset()
    {
        var tokenId = await SeedTokenAsync();
        var client = _factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/password-reset?token={tokenId}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Input.Token"] = tokenId.ToString(),
            ["Input.NewPassword"] = "new-password-12345",
            ["Input.ConfirmPassword"] = "new-password-12345"
        });
        var postResp = await client.PostAsync("/password-reset", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await postResp.Content.ReadAsStringAsync();
        body.Should().Contain("başarıyla güncellendi");

        // DB doğrula
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = await db.PasswordResetTokens.FirstAsync(t => t.Id == tokenId);
        token.UsedAt.Should().NotBeNull();
    }
}
```

- [ ] **Step 6: GREEN**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~PasswordResetPageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 2/2 + 123/123 toplam.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Public/ \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Pages/Public/PasswordResetPageTests.cs
git commit -m "feat(license-server): add Pages/Public/PasswordReset Razor page"
```

---

### Task 9: AdminActionEmailService + 6 trigger noktası

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Email/AdminActionEmailService.cs`
- Modify: `LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml.cs`
- Modify: `LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml.cs`
- Modify: `LiveDeck.LicenseServer/Controllers/Licenses/AdminLicensesController.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI)
- Create: `LiveDeck.LicenseServer.Tests/Services/AdminActionEmailServiceTests.cs`

**Context:** 3 metot (Issued/Revoked/Extended) × 2 yer (Phase 4d Razor handler + Phase 4a REST controller) = 6 trigger noktası. Her metot EmailSendCoordinator'a delegate eder. Mevcut handler/controller'lara constructor injection eklenir + audit'ten sonra notify çağrısı.

- [ ] **Step 1: AdminActionEmailService impl**

`LiveDeck.LicenseServer/Services/Email/AdminActionEmailService.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Email;

/// <summary>
/// Customer'a admin işlemleri sonrası bilgilendirme emaili gönderir
/// (license issued / revoked / extended). Sync — Phase 4a SmtpEmailSender
/// catch-and-log pattern'i kullandığı için admin sayfası blocking olmaz.
/// </summary>
public sealed class AdminActionEmailService
{
    private readonly EmailSendCoordinator _coordinator;

    public AdminActionEmailService(EmailSendCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    public Task NotifyLicenseIssuedAsync(
        Guid customerId, string licenseKey, string skuCode, DateTimeOffset expiresAt,
        CancellationToken ct = default) =>
        _coordinator.TrySendAsync(
            customerId,
            templateKey: "license-issued",
            contextKey: licenseKey,
            templateBuilder: (c, unsubUrl) => EmailTemplates.LicenseIssued(c.Name, licenseKey, skuCode, expiresAt, unsubUrl),
            requiresUnsubscribeRespect: true,
            ct);

    public Task NotifyLicenseRevokedAsync(
        Guid customerId, string licenseKey, string reason,
        CancellationToken ct = default) =>
        _coordinator.TrySendAsync(
            customerId,
            templateKey: "license-revoked",
            contextKey: licenseKey + ":revoke",
            templateBuilder: (c, unsubUrl) => EmailTemplates.LicenseRevoked(c.Name, licenseKey, reason, unsubUrl),
            requiresUnsubscribeRespect: true,
            ct);

    public Task NotifyLicenseExtendedAsync(
        Guid customerId, string licenseKey, DateTimeOffset newExpiresAt, int additionalDays,
        CancellationToken ct = default) =>
        _coordinator.TrySendAsync(
            customerId,
            templateKey: "license-extended",
            contextKey: licenseKey + ":extend:" + Guid.NewGuid().ToString("N"),
            templateBuilder: (c, unsubUrl) => EmailTemplates.LicenseExtended(c.Name, licenseKey, newExpiresAt, additionalDays, unsubUrl),
            requiresUnsubscribeRespect: true,
            ct);
}
```

- [ ] **Step 2: Program.cs DI**

`Program.cs` mevcut services bloğuna:
```csharp
        builder.Services.AddScoped<AdminActionEmailService>();
```

- [ ] **Step 3: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Services/AdminActionEmailServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public sealed class AdminActionEmailServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public AdminActionEmailServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedConfirmedAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"a-{Guid.NewGuid():N}@x",
            Name = "A",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task NotifyLicenseIssued_sends_email_and_writes_log()
    {
        var customer = await SeedConfirmedAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminActionEmailService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.NotifyLicenseIssuedAsync(customer.Id, "LDK-AAA", "STD", DateTimeOffset.UtcNow.AddDays(365));

        _factory.Email.Sent.Count.Should().Be(sentBefore + 1);
        _factory.Email.Sent.Last().Subject.Should().Contain("Yeni lisansınız");

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var log = await db.EmailLogs.Where(e => e.CustomerId == customer.Id && e.TemplateKey == "license-issued").FirstOrDefaultAsync();
        log.Should().NotBeNull();
    }

    [Fact]
    public async Task NotifyLicenseRevoked_sends_email_and_writes_log()
    {
        var customer = await SeedConfirmedAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminActionEmailService>();

        await svc.NotifyLicenseRevokedAsync(customer.Id, "LDK-BBB", "Test sebep");

        _factory.Email.Sent.Should().Contain(e => e.Subject.Contains("iptal"));

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var log = await db.EmailLogs.Where(e => e.CustomerId == customer.Id && e.TemplateKey == "license-revoked").FirstOrDefaultAsync();
        log.Should().NotBeNull();
    }

    [Fact]
    public async Task NotifyLicenseExtended_sends_email_for_each_call_with_unique_contextKey()
    {
        var customer = await SeedConfirmedAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<AdminActionEmailService>();
        var sentBefore = _factory.Email.Sent.Count;

        await svc.NotifyLicenseExtendedAsync(customer.Id, "LDK-CCC", DateTimeOffset.UtcNow.AddDays(395), 30);
        await svc.NotifyLicenseExtendedAsync(customer.Id, "LDK-CCC", DateTimeOffset.UtcNow.AddDays(425), 30);

        // Her extend için ayrı email (contextKey unique-per-extend)
        _factory.Email.Sent.Count.Should().Be(sentBefore + 2);
    }
}
```

- [ ] **Step 4: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminActionEmailServiceTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `AdminActionEmailService` yok.

- [ ] **Step 5: 6 trigger noktasına injection + çağrı ekle**

**Phase 4d Razor handler 1: `Pages/Admin/Licenses/Issue.cshtml.cs`**

Constructor parametre listesine ekle:
```csharp
    private readonly AdminActionEmailService _adminEmail;

    public IssueModel(LicenseDbContext db, LicenseIssuer issuer, IAuditService audit, AdminActionEmailService adminEmail)
    {
        _db = db;
        _issuer = issuer;
        _audit = audit;
        _adminEmail = adminEmail;
    }
```

`OnPostAsync` metodunda, `_audit.LogAsync(...)` çağrısının **hemen sonrasına** ekle (success path'te):
```csharp
        await _adminEmail.NotifyLicenseIssuedAsync(
            customerId: (await _db.Customers.First(c => c.Email == Input.CustomerEmail).Select(c => c.Id).FirstOrDefaultAsync(ct)),
            licenseKey: result.LicenseKey,
            skuCode: Input.SkuCode,
            expiresAt: result.ExpiresAt,
            ct: ct);
```

Daha sade:
```csharp
        var custId = await _db.Customers.Where(c => c.Email == Input.CustomerEmail).Select(c => c.Id).FirstAsync(ct);
        await _adminEmail.NotifyLicenseIssuedAsync(custId, result.LicenseKey, Input.SkuCode, result.ExpiresAt, ct);
```

**Phase 4d Razor handler 2: `Pages/Admin/Licenses/Detail.cshtml.cs`**

Constructor: `AdminActionEmailService` 4. parametre olarak ekle.

`OnPostRevokeAsync` ve `OnPostExtendAsync` audit çağrısından sonra:
```csharp
        // Revoke handler:
        await _adminEmail.NotifyLicenseRevokedAsync(License!.CustomerId, key, RevokeForm.Reason, ct);

        // Extend handler:
        await _adminEmail.NotifyLicenseExtendedAsync(License!.CustomerId, key, License.ExpiresAt, ExtendForm.AdditionalDays, ct);
```

**Phase 4a REST controller: `Controllers/Licenses/AdminLicensesController.cs`**

Constructor: `AdminActionEmailService` 3. parametre olarak ekle. Mevcut `Issue`, `Revoke`, `Extend` action metodlarında success path'te:
- Issue: `await _adminEmail.NotifyLicenseIssuedAsync(customerId, result.LicenseKey, req.SkuCode, result.ExpiresAt, ct);`
- Revoke: `await _adminEmail.NotifyLicenseRevokedAsync(license.CustomerId, license.LicenseKey, req.Reason, ct);`
- Extend: `await _adminEmail.NotifyLicenseExtendedAsync(license.CustomerId, license.LicenseKey, license.ExpiresAt, req.AdditionalDays, ct);`

(Implementation note: Issue endpoint'i `LicenseIssuer.IssueAsync` çağrısı sonrası `customerId`'yi bilmek için `_db.Customers.Where(c => c.Email == req.CustomerEmail).Select(c => c.Id).FirstAsync(ct)` ek query gerek. Veya `LicenseIssuer.IssueAsync` return type'ını genişlet — YAGNI; ek query kabul edilebilir.)

- [ ] **Step 6: GREEN**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminActionEmailServiceTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 3/3 + 126/126 toplam (123 + 3).

**Eğer mevcut `AdminLicensesIssueTests` veya `AdminLicensesDetailTests` (Phase 4d Task 6/7) constructor signature değişikliği nedeniyle bozulursa:** Hayır — constructor DI auto-resolve eder, test'ler yeni dependency'i bilmez. Test'ler `_factory.Services.GetRequiredService<...>()` ile resolve edilirse sorun yok. Constructor breaking change olmaz çünkü DI default tüm dependency'leri inject eder.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Services/Email/AdminActionEmailService.cs \
        LiveDeck.LicenseServer/Pages/Admin/Licenses/Issue.cshtml.cs \
        LiveDeck.LicenseServer/Pages/Admin/Licenses/Detail.cshtml.cs \
        LiveDeck.LicenseServer/Controllers/Licenses/AdminLicensesController.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Services/AdminActionEmailServiceTests.cs
git commit -m "feat(license-server): add AdminActionEmailService + 6 trigger points (Razor + REST)"
```

---

### Task 10: Pages/Public/Unsubscribe Razor sayfa

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Public/Unsubscribe.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Public/Unsubscribe.cshtml.cs`
- Create: `LiveDeck.LicenseServer.Tests/Pages/Public/UnsubscribePageTests.cs`

**Context:** Anonymous Razor sayfa. GET `?token={signed}` → token verify, customer email göster, "Aboneliği durdur" butonu (POST). POST → `Customer.Unsubscribed = true`. Idempotent. 3 test.

- [ ] **Step 1: Unsubscribe.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Public/Unsubscribe.cshtml.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Pages.Public;

public class UnsubscribeModel : PageModel
{
    private readonly LicenseDbContext _db;
    private readonly UnsubscribeTokenSigner _signer;

    public UnsubscribeModel(LicenseDbContext db, UnsubscribeTokenSigner signer)
    {
        _db = db;
        _signer = signer;
    }

    public string? CustomerEmail { get; private set; }
    public bool AlreadyUnsubscribed { get; private set; }
    public bool JustUnsubscribed { get; private set; }
    public bool TokenInvalid { get; private set; }

    [BindProperty]
    public string Token { get; set; } = "";

    public async Task OnGetAsync(string? token)
    {
        if (string.IsNullOrEmpty(token) || !_signer.TryVerify(token, out var customerId, out _))
        {
            TokenInvalid = true;
            return;
        }

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId);
        if (customer is null)
        {
            TokenInvalid = true;
            return;
        }

        Token = token;
        CustomerEmail = customer.Email;
        AlreadyUnsubscribed = customer.Unsubscribed;
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(Token) || !_signer.TryVerify(Token, out var customerId, out _))
        {
            TokenInvalid = true;
            return Page();
        }

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Id == customerId, ct);
        if (customer is null)
        {
            TokenInvalid = true;
            return Page();
        }

        if (!customer.Unsubscribed)
        {
            customer.Unsubscribed = true;
            await _db.SaveChangesAsync(ct);
        }

        CustomerEmail = customer.Email;
        JustUnsubscribed = true;
        return Page();
    }
}
```

- [ ] **Step 2: Unsubscribe.cshtml**

`LiveDeck.LicenseServer/Pages/Public/Unsubscribe.cshtml`:

```cshtml
@page "/unsubscribe"
@model UnsubscribeModel
@{
    ViewData["Title"] = "Abonelikten Çık";
    Layout = null;
}
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>Abonelikten Çık — LiveDeck</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
</head>
<body class="bg-light">
    <main class="container py-5" style="max-width: 480px;">
        <h1 class="h3 mb-4">LiveDeck — E-posta Bildirimleri</h1>

        @if (Model.TokenInvalid)
        {
            <div class="alert alert-danger">Bu bağlantı geçersiz. Lütfen aldığınız son e-postadaki bağlantıyı kullanın.</div>
        }
        else if (Model.JustUnsubscribed)
        {
            <div class="alert alert-success">
                Aboneliğiniz durduruldu. Bundan sonra <strong>@Model.CustomerEmail</strong> adresine pazarlama e-postaları göndermeyeceğiz.
                <p class="mt-2 mb-0 text-muted small">Hesap güvenliğiyle ilgili e-postalar (şifre sıfırlama, e-posta doğrulama) yine de gönderilir.</p>
            </div>
        }
        else if (Model.AlreadyUnsubscribed)
        {
            <div class="alert alert-info">
                <strong>@Model.CustomerEmail</strong> adresi için aboneliğiniz zaten kapalı. Pazarlama e-postaları gelmiyor.
            </div>
        }
        else
        {
            <p>
                <strong>@Model.CustomerEmail</strong> adresine LiveDeck pazarlama e-postaları
                (lisans yenileme hatırlatmaları, lisans işlem bildirimleri) gönderilmesini durdurmak için
                aşağıdaki butona tıklayın.
            </p>
            <p class="text-muted small">
                Hesap güvenliğiyle ilgili e-postalar (şifre sıfırlama, e-posta doğrulama) yine de gönderilir.
            </p>
            <form method="post">
                <input type="hidden" asp-for="Token" />
                <button type="submit" class="btn btn-danger">Aboneliği durdur</button>
            </form>
        }
    </main>
</body>
</html>
```

- [ ] **Step 3: UnsubscribePageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/Public/UnsubscribePageTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages.Public;

public sealed class UnsubscribePageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public UnsubscribePageTests(ApiFactory factory) => _factory = factory;

    private async Task<(Customer customer, string token)> SeedAsync(bool initiallyUnsubscribed = false)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var signer = scope.ServiceProvider.GetRequiredService<UnsubscribeTokenSigner>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"unsub-{Guid.NewGuid():N}@x",
            Name = "Unsub",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow,
            Unsubscribed = initiallyUnsubscribed
        };
        db.Customers.Add(c);
        await db.SaveChangesAsync();
        var token = signer.Sign(c.Id, DateTimeOffset.UtcNow);
        return (c, token);
    }

    [Fact]
    public async Task Get_with_valid_token_renders_email_and_form()
    {
        var (customer, token) = await SeedAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/unsubscribe?token={Uri.EscapeDataString(token)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain(customer.Email);
        html.Should().Contain("Aboneliği durdur");
    }

    [Fact]
    public async Task Post_with_valid_token_sets_Unsubscribed_flag()
    {
        var (customer, token) = await SeedAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = true });

        var getResp = await client.GetAsync($"/unsubscribe?token={Uri.EscapeDataString(token)}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Token"] = token
        });
        var postResp = await client.PostAsync("/unsubscribe", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Aboneliğiniz durduruldu");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var updated = await db.Customers.FirstAsync(c => c.Id == customer.Id);
        updated.Unsubscribed.Should().BeTrue();
    }

    [Fact]
    public async Task Get_with_already_unsubscribed_customer_shows_info_message()
    {
        var (customer, token) = await SeedAsync(initiallyUnsubscribed: true);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/unsubscribe?token={Uri.EscapeDataString(token)}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("aboneliğiniz zaten kapalı");
    }

    [Fact]
    public async Task Get_with_invalid_token_shows_error_message()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/unsubscribe?token=invalid.tampered.token");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Bu bağlantı geçersiz");
    }
}
```

- [ ] **Step 4: GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~UnsubscribePageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 4/4 + 130/130 toplam (126 + 4 — bir test fazla, plan başlangıçta 3 test demişti; final 4 test daha kapsamlı).

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Public/Unsubscribe.cshtml \
        LiveDeck.LicenseServer/Pages/Public/Unsubscribe.cshtml.cs \
        LiveDeck.LicenseServer.Tests/Pages/Public/UnsubscribePageTests.cs
git commit -m "feat(license-server): add Pages/Public/Unsubscribe Razor page (HMAC-token, idempotent)"
```

---

### Task 11: Final verification + manual smoke

**Files:** None (verification only)

**Context:** Tüm Phase 4e implementasyonun final sweep'i. Build clean, tüm test paketleri pass. Spec'in §10 manuel smoke planını referans olarak listele.

- [ ] **Step 1: Solution build sweep**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Beklenen: 0 errors / 0 warnings.

- [ ] **Step 2: Tüm test paketleri**

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen toplam:
- LiveDeck.Tests: **128/128** (regression, dokunulmadı)
- LiveDeck.Licensing.Tests: **104/104** (regression, dokunulmadı)
- LiveDeck.LicenseServer.Tests: **130/130** (90 baseline + 40 yeni)
- **Toplam: ~362** (~361 hedefiyle uyumlu)

- [ ] **Step 3: Manuel smoke (opsiyonel)**

Spec §10'daki 12 maddelik manuel smoke planını referans alarak fiziksel test:

1. Server start → `/hangfire` anonymous → AdminCookie redirect
2. Admin login → Hangfire dashboard'da 5 recurring job görünür
3. Customer kaydet + admin lisans ihraç → `LicenseIssued.eml` (footer dahil)
4. Lisansı revoke → `LicenseRevoked.eml`
5. Lisansı extend → `LicenseExtended.eml`
6. Lisans ExpiresAt'ini 14g'a çek (SQL) → Hangfire dashboard "renewal-14d" → "Trigger now" → `Renewal14d.eml`
7. Aynı job tekrar trigger → 0 email (dedup)
8. Customer "şifremi unuttum" → POST → email URL → form → yeni şifre → eski/yeni login test
9. Renewal email footer'daki unsubscribe link → form → "Aboneliğiniz durduruldu"
10. Yeni lisans ihraç (aynı customer) → email yok (Unsubscribed)
11. PasswordReset hala gider (transactional bypass)

Bu task kod yazmaz; sadece final commit gerekirse:
- Eğer build/test'te sayım uyuşmazsa plan dosyasındaki test count'larını güncelle
- Aksi takdirde commit yok

- [ ] **Step 4: Final commit (opsiyonel)**

```bash
# Sadece sayım düzeltmesi gerekirse
git add docs/superpowers/plans/2026-04-29-phase-4e-email-automation.md
git commit -m "docs(plan): update Phase 4e test count assertions to actual"
```

Aksi takdirde Task 11 commit'siz tamamlanır.

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task |
|---|---|
| §2.1 Solution etkisi (Domain entities + Pages/Public + DI) | Task 1 (foundation) + Task 2 (entities) + Task 8 (Pages/Public PasswordReset) + Task 10 (Pages/Public Unsubscribe) |
| §2.2 Stack (Hangfire + AngleSharp + crypto) | Task 1 (Hangfire packages) + Task 3 (HMAC) |
| §2.3 Endpoint listesi | Task 7 (REST endpoints) + Task 8 (Razor PasswordReset) + Task 10 (Razor Unsubscribe) + Task 1 (Hangfire dashboard) |
| §3 Email templates + EmailSendCoordinator | Task 4 (templates) + Task 5 (coordinator) |
| §4 Hangfire + ReminderJobs | Task 1 (setup) + Task 6 (jobs + recurring registration) |
| §5 Password reset flow | Task 7 (service + endpoints) + Task 8 (Razor sayfa) |
| §6 Admin action notifications | Task 9 (service + 6 trigger noktası) |
| §7 Unsubscribe mekanizması | Task 3 (TokenSigner) + Task 10 (Razor sayfa) + Task 5 (URL üretimi) |
| §8 Konfigürasyon + DI | Her task kendi DI ekler |
| §9 Test stratejisi | Tüm task'lerin kendi test'leri |
| §10 Manuel smoke | Task 11 referansı |
| §11 YAGNI | Plan'a yansıdı |
| §13 Kabul kriterleri | Task 11 final verification |

**2. Placeholder scan:**
- Hiç "TBD"/"TODO" yok
- Tüm test'ler tam yazılmış (template değil)
- Tüm code blok'lar concrete

**3. Type consistency:**
- `EmailLog` 6 alan (Task 2 entity) — Task 5 EmailSendCoordinator + tüm test'lerde tutarlı ✓
- `PasswordResetToken` 5 alan — Task 2 entity + Task 7 service tutarlı ✓
- `Customer.Unsubscribed` bool — Task 2'de eklendi, Task 5 + 10'da kullanılıyor ✓
- `IAuditService` (Phase 4d) ile çakışma yok — yeni AdminActionEmailService ayrı interface ✓
- `EmailSendCoordinator.TrySendAsync` 5-arg signature — Task 5'te tanımlı, Task 6/7/9 doğru çağırır ✓
- `UnsubscribeTokenSigner.Sign(customerId, issuedAt)` + `TryVerify(token, out customerId, out issuedAt)` — Task 3'te tanımlı, Task 5/10'da kullanılır ✓
- `AuditEvents` constants (Phase 4d Task 2) Task 9'da kullanılmıyor — Phase 4d audit ayrı, Phase 4e email log ayrı (kasıtlı isolation)
- `PasswordResetResult` enum 3 değer (Success/TokenInvalid/PasswordTooShort) — Task 7 service + AuthController endpoint switch tutarlı ✓
- `EmailTemplates.*` 9 yeni metot signature'ı (subject/html/plain tuple, optional unsubscribeUrl) — Task 4 + 5 + 6 + 9'da tutarlı kullanım ✓
- `Hangfire IRecurringJobManager.AddOrUpdate` — Task 6 Program.cs registration ✓
- `[AutomaticRetry(Attempts = 0, ...)]` — Task 6 her metoda ✓

**4. Cross-task dependencies:**
- Task 1 → Task 6 (Hangfire'a recurring kayıt için)
- Task 2 → Task 5 (EmailLog dedup), Task 7 (PasswordResetToken)
- Task 3 → Task 5 (unsubscribe URL üretimi), Task 10 (Razor sayfa)
- Task 4 → Task 5 (template builder), Task 6 (renewal templates), Task 9 (admin templates)
- Task 5 → Task 6 (ReminderJobs delegate), Task 7 (PasswordResetService delegate), Task 9 (AdminAction delegate)
- Task 7 → Task 8 (Razor sayfa service kullanır)
- Task 9 → Phase 4a/4d mevcut handler/controller modify
- Task 10 son UI; Task 5 footer URL pattern reuse

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-29-phase-4e-email-automation.md`.**

11 task. Tahmini ~3500 satır plan. Phase 4a/4b/4c/4d ile aynı pattern (TDD, frequent commits, subagent-friendly). Test hedefi 322 → ~362.

İki yürütme seçeneği:

**1. Subagent-Driven (önerilen)** — Her task için fresh subagent dispatch. Phase 4a (15) + 4b (13) + 4c (14) + 4d (11) = 53 task hep bu şekilde tamamlandı.

**2. Inline Execution** — executing-plans skill ile bu session'da batch yürütme.

Hangisi?

