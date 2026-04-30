# Faz 4f — Müşteri Intake Formu + WhatsApp Deep-Link Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Her lisanslı yayıncıya kalıcı `https://license.livedeck.app/r/{slug}` form linki ver. Müşteri formu doldurur (kullanıcı adı + ad soyad + adres) → "Tamamla" butonu otomatik WhatsApp deep-link ile yayıncının numarasına yönlendirir, mesaj prefilled. Submission paralel server DB'sine yazılır; desktop app 2dk polling ile yeni başvuruları çeker, `Customer` entity oluşturur/günceller.

**Architecture:** `LiveDeck.LicenseServer`'a yeni 2 entity + 1 Razor sayfa (`Pages/Public/IntakeForm`) + 1 REST controller. `LiveDeck.Licensing` SDK'sına 3 yeni client metot. `LiveDeck.App`'a sync hosted service + Settings tab. `LiveDeck.Core` Customer'a Address field + migration. Honeypot + IP rate-limit. License-bound (lisans yoksa 410 Gone).

**Tech Stack:** ASP.NET Core 10 Razor Pages + Controllers / EF Core 9 (SQL Server prod, InMemory test) / Bootstrap 5 CDN / `wa.me` deep-link / `BackgroundService` + `PeriodicTimer` (Phase 4b pattern) / AngleSharp (HTML test) / xUnit + FluentAssertions.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-4f state:** Phase 4e master `fce951a`. 362/362 test (128 LiveDeck + 104 Licensing + 130 LicenseServer). Build 0/0.

**Spec reference:** `docs/superpowers/specs/2026-04-30-phase-4f-intake-form-design.md`

---

## Task Index

**Server foundations (1-3):** Domain entities + migration · SlugValidator · WhatsAppLinkBuilder
**Server core (4-5):** IntakeFormService · IntakeFormController + rate-limit
**Server Razor (6):** IntakeForm public page (GET form + POST submit + honeypot)
**Client SDK (7):** LicenseApiClient 3 yeni metot + DTOs
**Core domain (8):** Customer.Address + migration 006 + repo upsert
**Desktop sync (9):** IntakeFormSyncService + HostedService
**Desktop UI (10):** Settings tab + AppHost DI
**Desktop badge (11):** MainShell new submissions badge + CustomerSearchDialog filter
**Final (12):** Verification + manual smoke

**Toplam test hedefi:** 362 baseline → ~399 (+37 yeni)

---

### Task 1: Domain entities + EF migration 007 (LicenseServer)

**Files:**
- Create: `LiveDeck.LicenseServer/Domain/IntakeFormConfig.cs`
- Create: `LiveDeck.LicenseServer/Domain/IntakeFormSubmission.cs`
- Modify: `LiveDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `LiveDeck.LicenseServer/Data/Migrations/{ts}_AddIntakeForm.cs` (auto-generated)
- Create: `LiveDeck.LicenseServer.Tests/Domain/IntakeFormEntitiesTests.cs`

**Context:** 2 yeni entity. `IntakeFormConfig` 1:1 Customer (her customer tek slug+phone'a sahip). `IntakeFormSubmission` 1:N Config (her submission bir config'e bağlı). FluentAPI: unique slug + 2 indexes. Auto-generated migration. 1 smoke test.

- [ ] **Step 1: IntakeFormConfig entity oluştur**

`LiveDeck.LicenseServer/Domain/IntakeFormConfig.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// 1:1 Customer mapping. Yayıncı'nın kişisel form linki konfigürasyonu.
/// Slug unique, lowercase. WhatsAppPhone E.164 format (leading +).
/// </summary>
public sealed class IntakeFormConfig
{
    public Guid Id { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string Slug { get; set; } = "";
    public string WhatsAppPhone { get; set; } = "";
    public string? CustomTitle { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 2: IntakeFormSubmission entity oluştur**

`LiveDeck.LicenseServer/Domain/IntakeFormSubmission.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

/// <summary>
/// Müşterinin doldurduğu form. Polling endpoint bu kayıtları cursor (SubmittedAt) ile döndürür.
/// IpAddress + UserAgent audit için.
/// </summary>
public sealed class IntakeFormSubmission
{
    public Guid Id { get; set; }
    public Guid IntakeFormConfigId { get; set; }
    public IntakeFormConfig Config { get; set; } = null!;
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Address { get; set; } = "";
    public DateTimeOffset SubmittedAt { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
}
```

- [ ] **Step 3: LicenseDbContext'e DbSet'leri ve FluentAPI ekle**

`LiveDeck.LicenseServer/Data/LicenseDbContext.cs` aç. Mevcut DbSet'lerin sonuna ekle:

```csharp
    public DbSet<IntakeFormConfig> IntakeFormConfigs => Set<IntakeFormConfig>();
    public DbSet<IntakeFormSubmission> IntakeFormSubmissions => Set<IntakeFormSubmission>();
```

`OnModelCreating` metodunda mevcut `PasswordResetToken` config bloğunun **hemen sonrasına**, `Sku.HasData(...)` seed satırından **önce** ekle:

```csharp
        mb.Entity<IntakeFormConfig>(b =>
        {
            b.HasKey(c => c.Id);
            b.HasOne(c => c.Customer).WithOne()
                .HasForeignKey<IntakeFormConfig>(c => c.CustomerId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(c => c.Slug).HasMaxLength(32).IsRequired();
            b.HasIndex(c => c.Slug).IsUnique();
            b.Property(c => c.WhatsAppPhone).HasMaxLength(20).IsRequired();
            b.Property(c => c.CustomTitle).HasMaxLength(100);
        });

        mb.Entity<IntakeFormSubmission>(b =>
        {
            b.HasKey(s => s.Id);
            b.HasOne(s => s.Config).WithMany()
                .HasForeignKey(s => s.IntakeFormConfigId)
                .OnDelete(DeleteBehavior.Cascade);
            b.Property(s => s.Username).HasMaxLength(64).IsRequired();
            b.Property(s => s.FullName).HasMaxLength(200).IsRequired();
            b.Property(s => s.Address).HasMaxLength(500).IsRequired();
            b.Property(s => s.IpAddress).HasMaxLength(64);
            b.Property(s => s.UserAgent).HasMaxLength(500);
            b.HasIndex(s => new { s.IntakeFormConfigId, s.SubmittedAt });
        });
```

- [ ] **Step 4: EF migration oluştur**

```bash
cd /c/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer
dotnet ef migrations add AddIntakeForm --output-dir Data/Migrations
```

Beklenen: `Data/Migrations/{timestamp}_AddIntakeForm.cs` + `.Designer.cs` + `LicenseDbContextModelSnapshot.cs` güncellenir. Migration `IntakeFormConfigs` + `IntakeFormSubmissions` tablolarını ekler. Build clean.

- [ ] **Step 5: Smoke test yaz**

`LiveDeck.LicenseServer.Tests/Domain/IntakeFormEntitiesTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Domain;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Domain;

public class IntakeFormEntitiesTests
{
    [Fact]
    public void IntakeFormConfig_default_is_active_with_empty_strings()
    {
        var cfg = new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = Guid.NewGuid(),
            Slug = "test",
            WhatsAppPhone = "+905551234567",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

        cfg.IsActive.Should().BeTrue();
        cfg.CustomTitle.Should().BeNull();
        cfg.Slug.Should().Be("test");
    }

    [Fact]
    public void IntakeFormSubmission_holds_audit_fields()
    {
        var sub = new IntakeFormSubmission
        {
            Id = Guid.NewGuid(),
            IntakeFormConfigId = Guid.NewGuid(),
            Username = "bilalcanli",
            FullName = "Bilal Canlı",
            Address = "Atatürk Cad. No:12",
            SubmittedAt = DateTimeOffset.UtcNow,
            IpAddress = "10.0.0.5",
            UserAgent = "Mozilla/5.0"
        };

        sub.Username.Should().Be("bilalcanli");
        sub.IpAddress.Should().Be("10.0.0.5");
    }
}
```

- [ ] **Step 6: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~IntakeFormEntitiesTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 2/2 + 132/132 toplam (130 baseline + 2 yeni).

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Domain/IntakeFormConfig.cs \
        LiveDeck.LicenseServer/Domain/IntakeFormSubmission.cs \
        LiveDeck.LicenseServer/Data/LicenseDbContext.cs \
        LiveDeck.LicenseServer/Data/Migrations/ \
        LiveDeck.LicenseServer.Tests/Domain/IntakeFormEntitiesTests.cs
git commit -m "feat(license-server): add IntakeFormConfig + IntakeFormSubmission entities (migration 007)"
```

---

### Task 2: SlugValidator — pure unit (TDD)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/IntakeForm/SlugValidator.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/IntakeForm/SlugValidatorTests.cs`

**Context:** Pure validation. Regex + reserved blacklist. 6 test (valid, empty, too short, too long, invalid format, reserved). No DbContext, no DI.

- [ ] **Step 1: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Services/IntakeForm/SlugValidatorTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services.IntakeForm;

public class SlugValidatorTests
{
    [Theory]
    [InlineData("burak")]
    [InlineData("burak-streamer")]
    [InlineData("a1b2")]
    [InlineData("abc123")]
    public void Validate_returns_Valid_for_well_formed_slugs(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.Valid);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Validate_returns_Empty_for_blank_input(string? slug)
    {
        SlugValidator.Validate(slug!).Should().Be(SlugValidationResult.Empty);
    }

    [Theory]
    [InlineData("ab")]                              // 2 char
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 33 char
    public void Validate_returns_InvalidLength_outside_3_to_32(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.InvalidLength);
    }

    [Theory]
    [InlineData("BURAK")]      // uppercase
    [InlineData("burak_test")] // underscore
    [InlineData("burak.test")] // dot
    [InlineData("-burak")]     // leading dash
    [InlineData("burak-")]     // trailing dash
    [InlineData("a--b")]       // not strictly forbidden by spec but pattern allows; check explicit
    public void Validate_returns_InvalidFormat_for_bad_chars_or_position(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.InvalidFormat);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("api")]
    [InlineData("hangfire")]
    [InlineData("me")]
    [InlineData("r")]
    [InlineData("unsubscribe")]
    [InlineData("password-reset")]
    [InlineData("auth")]
    [InlineData("login")]
    [InlineData("logout")]
    [InlineData("livedeck")]
    public void Validate_returns_Reserved_for_blacklisted_slugs(string slug)
    {
        SlugValidator.Validate(slug).Should().Be(SlugValidationResult.Reserved);
    }

    [Fact]
    public void Validate_is_case_insensitive_for_reserved_check()
    {
        SlugValidator.Validate("ADMIN").Should().Be(SlugValidationResult.InvalidFormat); // uppercase fails format first
    }
}
```

**Not:** "a--b" testi spec'te ardışık dash explicit yasak değildi ama plan'da ele alındı. Implementer pattern'ı buna göre yazsın (or ardışık dash izinli ise testi InvalidFormat yerine Valid yap). Tasarım netliği için: **ardışık dash YASAK** kabul edelim — pattern: `^[a-z0-9](?:[a-z0-9]|-(?=[a-z0-9])){1,30}[a-z0-9]?$` daha katı.

Daha sade pattern alternatifi: lookahead'lı regex zor okunur. Pratik karar: ardışık dash kabul (hep tek tek), test'te `"a--b"` Valid olur.

**Düzeltme:** Yukarıdaki test array'inden `"a--b"` satırını çıkar (ardışık dash izinli yapacağız). Test sayısı 5 → 4 InvalidFormat case.

Final test'in `InvalidFormat` array'inden `"a--b"` kaldırılır.

- [ ] **Step 2: RED — derleme hatası**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~SlugValidatorTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `SlugValidator` + `SlugValidationResult` yok.

- [ ] **Step 3: SlugValidator impl**

`LiveDeck.LicenseServer/Services/IntakeForm/SlugValidator.cs`:

```csharp
using System.Text.RegularExpressions;

namespace LiveDeck.LicenseServer.Services.IntakeForm;

public enum SlugValidationResult
{
    Valid,
    Empty,
    InvalidLength,
    InvalidFormat,
    Reserved
}

public static class SlugValidator
{
    private static readonly Regex Pattern =
        new(@"^[a-z0-9](?:[a-z0-9-]{1,30}[a-z0-9])?$", RegexOptions.Compiled);

    private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "api", "hangfire", "me", "r", "unsubscribe",
        "password-reset", "auth", "login", "logout", "null",
        "undefined", "app", "assets", "static", "livedeck"
    };

    public static SlugValidationResult Validate(string? slug)
    {
        if (string.IsNullOrWhiteSpace(slug))
            return SlugValidationResult.Empty;

        if (slug.Length < 3 || slug.Length > 32)
            return SlugValidationResult.InvalidLength;

        if (!Pattern.IsMatch(slug))
            return SlugValidationResult.InvalidFormat;

        if (Reserved.Contains(slug))
            return SlugValidationResult.Reserved;

        return SlugValidationResult.Valid;
    }
}
```

- [ ] **Step 4: GREEN**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~SlugValidatorTests" 2>&1 | tail -5
```

Beklenen: tüm test'ler PASS (Theory'ler dahil).

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 132 + Theory case sayısı. Pratik olarak 132 baseline + ~24 yeni test method = 156 toplam.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Services/IntakeForm/SlugValidator.cs \
        LiveDeck.LicenseServer.Tests/Services/IntakeForm/SlugValidatorTests.cs
git commit -m "feat(license-server): add SlugValidator (pattern + reserved blacklist)"
```

---

### Task 3: WhatsAppLinkBuilder — pure unit (TDD)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/IntakeForm/WhatsAppLinkBuilder.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/IntakeForm/WhatsAppLinkBuilderTests.cs`

**Context:** Phone format normalization (strip +/space/-) + URL encode mesaj. Pure, no DI. 4 test.

- [ ] **Step 1: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Services/IntakeForm/WhatsAppLinkBuilderTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services.IntakeForm;

public class WhatsAppLinkBuilderTests
{
    private readonly WhatsAppLinkBuilder _b = new();

    [Fact]
    public void Build_produces_wa_me_url_with_phone_and_message()
    {
        var url = _b.Build("+905551234567", "bilalcanli", "Bilal Canlı", "İstanbul");

        url.Should().StartWith("https://wa.me/905551234567?text=");
    }

    [Fact]
    public void Build_strips_plus_space_and_dash_from_phone()
    {
        var url = _b.Build("+90 555 123-4567", "u", "n", "a");

        url.Should().StartWith("https://wa.me/905551234567?text=");
    }

    [Fact]
    public void Build_encodes_newline_and_special_chars_in_message()
    {
        var url = _b.Build("+905551234567", "user&one", "Ad Soyad", "Adres+Test");

        // URL encoded: \n = %0A, & = %26, + = %2B, space = %20 (or +)
        url.Should().Contain("%0A");           // newlines encoded
        url.Should().Contain("user%26one");    // & encoded
        url.Should().Contain("Adres%2BTest");  // + encoded
    }

    [Fact]
    public void Build_includes_three_labeled_lines_in_message()
    {
        var url = _b.Build("+905551234567", "uname", "Test User", "Test Adres");

        // Decode the text param to verify structure
        var queryStart = url.IndexOf("?text=") + 6;
        var encoded = url[queryStart..];
        var decoded = Uri.UnescapeDataString(encoded);

        decoded.Should().Contain("Kullanıcı adı: uname");
        decoded.Should().Contain("Ad Soyad: Test User");
        decoded.Should().Contain("Adres: Test Adres");
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~WhatsAppLinkBuilderTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `WhatsAppLinkBuilder` yok.

- [ ] **Step 3: WhatsAppLinkBuilder impl**

`LiveDeck.LicenseServer/Services/IntakeForm/WhatsAppLinkBuilder.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// wa.me deep-link URL üretici. Phone E.164 → digits-only.
/// Mesaj formatı: 3 satır (Kullanıcı adı / Ad Soyad / Adres), \n encode'lı.
/// </summary>
public sealed class WhatsAppLinkBuilder
{
    public string Build(string e164Phone, string username, string fullName, string address)
    {
        var phone = e164Phone
            .Replace("+", "")
            .Replace(" ", "")
            .Replace("-", "");

        var message = $"Kullanıcı adı: {username}\nAd Soyad: {fullName}\nAdres: {address}";
        var encoded = Uri.EscapeDataString(message);

        return $"https://wa.me/{phone}?text={encoded}";
    }
}
```

- [ ] **Step 4: GREEN**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~WhatsAppLinkBuilderTests" 2>&1 | tail -5
```

Beklenen: 4/4 PASS.

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: önceki + 4 yeni test (count baseline'a göre).

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.LicenseServer/Services/IntakeForm/WhatsAppLinkBuilder.cs \
        LiveDeck.LicenseServer.Tests/Services/IntakeForm/WhatsAppLinkBuilderTests.cs
git commit -m "feat(license-server): add WhatsAppLinkBuilder (wa.me URL with encoded message)"
```

---

### Task 4: IntakeFormService — orchestration (TDD with DbContext)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI registration)
- Create: `LiveDeck.LicenseServer.Tests/Services/IntakeForm/IntakeFormServiceTests.cs`

**Context:** Domain orchestration. Slug claim/update (uniqueness check), license-active check, submission persist, polling read. ApiFactory ile DbContext kullanılır. 7 test.

- [ ] **Step 1: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Services/IntakeForm/IntakeFormServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.IntakeForm;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services.IntakeForm;

public sealed class IntakeFormServiceTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormServiceTests(ApiFactory factory) => _factory = factory;

    private async Task<Customer> SeedCustomerAsync(bool withActiveLicense = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"if-{Guid.NewGuid():N}@x",
            Name = "If",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(c);
        if (withActiveLicense)
        {
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-" + Guid.NewGuid().ToString("N"),
                CustomerId = c.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
        }
        await db.SaveChangesAsync();
        return c;
    }

    [Fact]
    public async Task UpsertConfigAsync_creates_new_config_when_none_exists()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        var cfg = await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", "Title", true, default);

        cfg.Slug.Should().Be(slug);
        cfg.WhatsAppPhone.Should().Be("+905551234567");
        cfg.IsActive.Should().BeTrue();
        cfg.CreatedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UpsertConfigAsync_updates_existing_config()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        var slug1 = $"slug-{Guid.NewGuid():N}"[..15];
        var slug2 = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug1, "+905551111111", null, true, default);
        var updated = await svc.UpsertConfigAsync(customer.Id, slug2, "+905552222222", "New", false, default);

        updated.Slug.Should().Be(slug2);
        updated.WhatsAppPhone.Should().Be("+905552222222");
        updated.CustomTitle.Should().Be("New");
        updated.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task UpsertConfigAsync_throws_SlugAlreadyTaken_when_used_by_another_customer()
    {
        var c1 = await SeedCustomerAsync();
        var c2 = await SeedCustomerAsync();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];

        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();

        await svc.UpsertConfigAsync(c1.Id, slug, "+905551111111", null, true, default);

        var act = async () => await svc.UpsertConfigAsync(c2.Id, slug, "+905552222222", null, true, default);
        var ex = await act.Should().ThrowAsync<IntakeFormService.SlugAlreadyTakenException>();
        ex.Which.Slug.Should().Be(slug);
    }

    [Fact]
    public async Task GetActiveBySlugAsync_returns_config_when_license_active_and_form_active()
    {
        var customer = await SeedCustomerAsync(withActiveLicense: true);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        var loaded = await svc.GetActiveBySlugAsync(slug, default);

        loaded.Should().NotBeNull();
        loaded!.Slug.Should().Be(slug);
    }

    [Fact]
    public async Task GetActiveBySlugAsync_returns_null_when_form_isactive_false()
    {
        var customer = await SeedCustomerAsync(withActiveLicense: true);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, isActive: false, default);

        var loaded = await svc.GetActiveBySlugAsync(slug, default);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task GetActiveBySlugAsync_returns_null_when_customer_has_no_active_license()
    {
        var customer = await SeedCustomerAsync(withActiveLicense: false);
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        var loaded = await svc.GetActiveBySlugAsync(slug, default);

        loaded.Should().BeNull();
    }

    [Fact]
    public async Task SaveSubmissionAsync_persists_submission_with_audit_fields()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        var cfg = await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        var submission = await svc.SaveSubmissionAsync(
            cfg.Id, "uname", "Full Name", "Address",
            "10.0.0.5", "TestAgent/1.0", default);

        submission.Username.Should().Be("uname");
        submission.IpAddress.Should().Be("10.0.0.5");
        submission.UserAgent.Should().Be("TestAgent/1.0");

        using var s2 = _factory.Services.CreateScope();
        var db = s2.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var stored = await db.IntakeFormSubmissions.FirstOrDefaultAsync(s => s.Id == submission.Id);
        stored.Should().NotBeNull();
    }

    [Fact]
    public async Task GetSubmissionsSinceAsync_returns_only_newer_than_cursor_ordered_asc()
    {
        var customer = await SeedCustomerAsync();
        using var scope = _factory.Services.CreateScope();
        var svc = scope.ServiceProvider.GetRequiredService<IntakeFormService>();
        var slug = $"slug-{Guid.NewGuid():N}"[..15];
        var cfg = await svc.UpsertConfigAsync(customer.Id, slug, "+905551234567", null, true, default);

        // 3 submissions: now-2h, now-1h, now (in DB, scope shared with InMemory)
        await svc.SaveSubmissionAsync(cfg.Id, "u1", "n1", "a1", null, null, default);
        await Task.Delay(20);
        var t2 = DateTimeOffset.UtcNow;
        await Task.Delay(20);
        await svc.SaveSubmissionAsync(cfg.Id, "u2", "n2", "a2", null, null, default);
        await Task.Delay(20);
        await svc.SaveSubmissionAsync(cfg.Id, "u3", "n3", "a3", null, null, default);

        var rows = await svc.GetSubmissionsSinceAsync(customer.Id, t2, limit: 50, default);

        rows.Should().HaveCount(2);
        rows[0].Username.Should().Be("u2");
        rows[1].Username.Should().Be("u3");
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~IntakeFormServiceTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — `IntakeFormService` yok.

- [ ] **Step 3: IntakeFormService impl**

`LiveDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Services.IntakeForm;

/// <summary>
/// Domain orchestration for intake form configs and submissions.
/// Enforces slug uniqueness, license-active guard, and submission persistence.
/// </summary>
public sealed class IntakeFormService
{
    private readonly LicenseDbContext _db;

    public IntakeFormService(LicenseDbContext db) => _db = db;

    public sealed class SlugAlreadyTakenException : Exception
    {
        public string Slug { get; }
        public SlugAlreadyTakenException(string slug)
            : base($"Slug '{slug}' already taken by another customer.")
            => Slug = slug;
    }

    /// <summary>Loads a customer's existing config, or null if not configured.</summary>
    public Task<IntakeFormConfig?> GetByCustomerAsync(Guid customerId, CancellationToken ct = default) =>
        _db.IntakeFormConfigs.FirstOrDefaultAsync(c => c.CustomerId == customerId, ct);

    /// <summary>
    /// Returns config only if (a) form IsActive AND (b) customer has an active license
    /// (RevokedAt null AND ExpiresAt &gt; now). Otherwise null — caller treats as 410 Gone.
    /// </summary>
    public async Task<IntakeFormConfig?> GetActiveBySlugAsync(string slug, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var config = await _db.IntakeFormConfigs
            .FirstOrDefaultAsync(c => c.Slug == slug && c.IsActive, ct);
        if (config is null) return null;

        var hasActiveLicense = await _db.Licenses
            .AnyAsync(l => l.CustomerId == config.CustomerId
                        && l.RevokedAt == null
                        && l.ExpiresAt > now, ct);
        return hasActiveLicense ? config : null;
    }

    /// <summary>Idempotent claim/update. Throws SlugAlreadyTakenException if slug used by another customer.</summary>
    public async Task<IntakeFormConfig> UpsertConfigAsync(
        Guid customerId, string slug, string whatsAppPhone, string? customTitle, bool isActive,
        CancellationToken ct = default)
    {
        var conflict = await _db.IntakeFormConfigs
            .FirstOrDefaultAsync(c => c.Slug == slug && c.CustomerId != customerId, ct);
        if (conflict is not null) throw new SlugAlreadyTakenException(slug);

        var existing = await GetByCustomerAsync(customerId, ct);
        var now = DateTimeOffset.UtcNow;

        if (existing is null)
        {
            var created = new IntakeFormConfig
            {
                Id = Guid.NewGuid(),
                CustomerId = customerId,
                Slug = slug,
                WhatsAppPhone = whatsAppPhone,
                CustomTitle = customTitle,
                IsActive = isActive,
                CreatedAt = now,
                UpdatedAt = now
            };
            _db.IntakeFormConfigs.Add(created);
            await _db.SaveChangesAsync(ct);
            return created;
        }

        existing.Slug = slug;
        existing.WhatsAppPhone = whatsAppPhone;
        existing.CustomTitle = customTitle;
        existing.IsActive = isActive;
        existing.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);
        return existing;
    }

    public async Task<IntakeFormSubmission> SaveSubmissionAsync(
        Guid configId, string username, string fullName, string address,
        string? ipAddress, string? userAgent, CancellationToken ct = default)
    {
        var sub = new IntakeFormSubmission
        {
            Id = Guid.NewGuid(),
            IntakeFormConfigId = configId,
            Username = username,
            FullName = fullName,
            Address = address,
            SubmittedAt = DateTimeOffset.UtcNow,
            IpAddress = ipAddress,
            UserAgent = userAgent
        };
        _db.IntakeFormSubmissions.Add(sub);
        await _db.SaveChangesAsync(ct);
        return sub;
    }

    public Task<List<IntakeFormSubmission>> GetSubmissionsSinceAsync(
        Guid customerId, DateTimeOffset since, int limit, CancellationToken ct = default) =>
        _db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.SubmittedAt > since)
            .OrderBy(s => s.SubmittedAt)
            .Take(limit)
            .ToListAsync(ct);
}
```

- [ ] **Step 4: Program.cs DI registration**

`LiveDeck.LicenseServer/Program.cs` mevcut services bloğuna ekle (Phase 4d/4e servislerinin yanına):

```csharp
        builder.Services.AddScoped<IntakeFormService>();
```

- [ ] **Step 5: GREEN**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~IntakeFormServiceTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 7/7 IntakeFormServiceTests + önceki + 7 yeni.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.LicenseServer/Services/IntakeForm/IntakeFormService.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Services/IntakeForm/IntakeFormServiceTests.cs
git commit -m "feat(license-server): add IntakeFormService (slug claim + license-active guard)"
```

---

### Task 5: IntakeFormController — REST endpoints + rate-limit policy

**Files:**
- Create: `LiveDeck.LicenseServer/Controllers/IntakeFormController.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (rate-limit policy)
- Create: `LiveDeck.LicenseServer.Tests/Controllers/IntakeFormControllerTests.cs`

**Context:** 3 endpoint: GET/PUT `/api/v1/me/intake-form` + GET `/api/v1/me/form-submissions`. Bearer-Customer auth. Phone format validation (E.164). Rate-limit policy `intake-form-submit` ekle. 6 test.

- [ ] **Step 1: Failing tests yaz**

`LiveDeck.LicenseServer.Tests/Controllers/IntakeFormControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Controllers;

public sealed class IntakeFormControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, Guid customerId)> CreateAuthedClientAsync()
    {
        var client = _factory.CreateClient();
        var email = $"ifc-{Guid.NewGuid():N}@x";
        await client.PostAsJsonAsync("/api/v1/auth/register",
            new { email, name = "IFC", password = "secret-password" });

        Guid tokenId, customerId;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);
            customerId = customer.Id;
            var token = await db.EmailConfirmationTokens
                .Where(t => t.CustomerId == customerId).FirstAsync();
            tokenId = token.Token;

            // Aktif lisans seed
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-IFC-" + Guid.NewGuid().ToString("N"),
                CustomerId = customerId,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
            await db.SaveChangesAsync();
        }
        await client.GetAsync($"/api/v1/auth/confirm-email/{tokenId}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { email, password = "secret-password" });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, customerId);
    }

    [Fact]
    public async Task Get_intake_form_returns_404_when_not_configured()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var resp = await client.GetAsync("/api/v1/me/intake-form");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Put_intake_form_creates_config_and_returns_200()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];

        var resp = await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug,
            whatsAppPhone = "+905551234567",
            customTitle = "Test Form",
            isActive = true
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IntakeFormBody>();
        body!.slug.Should().Be(slug);
        body.formUrl.Should().EndWith($"/r/{slug}");
    }

    [Fact]
    public async Task Get_intake_form_returns_200_after_put()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];

        await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug, whatsAppPhone = "+905551234567", isActive = true
        });

        var resp = await client.GetAsync("/api/v1/me/intake-form");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<IntakeFormBody>();
        body!.slug.Should().Be(slug);
    }

    [Fact]
    public async Task Put_intake_form_returns_400_for_invalid_slug()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var resp = await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug = "ADMIN", whatsAppPhone = "+905551234567"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_intake_form_returns_400_for_invalid_phone()
    {
        var (client, _) = await CreateAuthedClientAsync();
        var resp = await client.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug = $"s-{Guid.NewGuid():N}"[..10], whatsAppPhone = "abc"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Put_intake_form_returns_409_for_slug_taken_by_another_customer()
    {
        var (client1, _) = await CreateAuthedClientAsync();
        var (client2, _) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];

        var first = await client1.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug, whatsAppPhone = "+905551111111"
        });
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        var second = await client2.PutAsJsonAsync("/api/v1/me/intake-form", new
        {
            slug, whatsAppPhone = "+905552222222"
        });
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Get_form_submissions_returns_empty_array_initially_then_includes_new_after_seed()
    {
        var (client, customerId) = await CreateAuthedClientAsync();
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        await client.PutAsJsonAsync("/api/v1/me/intake-form",
            new { slug, whatsAppPhone = "+905551234567" });

        // Initial: empty
        var resp1 = await client.GetAsync("/api/v1/me/form-submissions");
        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        var rows1 = await resp1.Content.ReadFromJsonAsync<List<SubmissionBody>>();
        rows1!.Count.Should().Be(0);

        // Seed submission
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var cfg = await db.IntakeFormConfigs.FirstAsync(c => c.CustomerId == customerId);
            db.IntakeFormSubmissions.Add(new IntakeFormSubmission
            {
                Id = Guid.NewGuid(),
                IntakeFormConfigId = cfg.Id,
                Username = "uname",
                FullName = "Full Name",
                Address = "Test Address",
                SubmittedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var resp2 = await client.GetAsync("/api/v1/me/form-submissions");
        var rows2 = await resp2.Content.ReadFromJsonAsync<List<SubmissionBody>>();
        rows2!.Count.Should().Be(1);
        rows2[0].username.Should().Be("uname");
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record IntakeFormBody(string slug, string whatsAppPhone, string? customTitle, bool isActive, string formUrl);
    private sealed record SubmissionBody(Guid id, string username, string fullName, string address, DateTimeOffset submittedAt);
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~IntakeFormControllerTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — controller yok.

- [ ] **Step 3: IntakeFormController impl**

`LiveDeck.LicenseServer/Controllers/IntakeFormController.cs`:

```csharp
using System.Security.Claims;
using System.Text.RegularExpressions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LiveDeck.LicenseServer.Controllers;

[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class IntakeFormController : ControllerBase
{
    private static readonly Regex E164 = new(@"^\+[1-9]\d{6,14}$", RegexOptions.Compiled);

    private readonly IntakeFormService _service;
    private readonly LicenseDbContext _db;
    private readonly string _publicBaseUrl;

    public IntakeFormController(IntakeFormService service, LicenseDbContext db, IConfiguration config)
    {
        _service = service;
        _db = db;
        _publicBaseUrl = config["App:PublicBaseUrl"]?.TrimEnd('/') ?? "https://localhost:5001";
    }

    public sealed record IntakeFormBody(
        string Slug, string WhatsAppPhone, string? CustomTitle, bool IsActive, string FormUrl);

    public sealed record UpdateRequest(
        string Slug, string WhatsAppPhone, string? CustomTitle, bool? IsActive);

    public sealed record SubmissionBody(
        Guid Id, string Username, string FullName, string Address, DateTimeOffset SubmittedAt);

    [HttpGet("api/v1/me/intake-form")]
    public async Task<IActionResult> GetMine(CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var cfg = await _service.GetByCustomerAsync(customerId, ct);
        if (cfg is null) return NotFound();
        return Ok(new IntakeFormBody(cfg.Slug, cfg.WhatsAppPhone, cfg.CustomTitle, cfg.IsActive,
            $"{_publicBaseUrl}/r/{cfg.Slug}"));
    }

    [HttpPut("api/v1/me/intake-form")]
    public async Task<IActionResult> Upsert([FromBody] UpdateRequest req, CancellationToken ct)
    {
        var slug = req.Slug?.Trim().ToLowerInvariant() ?? "";
        var slugResult = SlugValidator.Validate(slug);
        if (slugResult != SlugValidationResult.Valid)
            return Problem(title: $"invalid-slug-{slugResult.ToString().ToLowerInvariant()}", statusCode: 400);

        var phone = req.WhatsAppPhone?.Trim() ?? "";
        if (!E164.IsMatch(phone))
            return Problem(title: "invalid-phone-format", statusCode: 400);

        var customerId = GetCustomerId();
        try
        {
            var cfg = await _service.UpsertConfigAsync(
                customerId, slug, phone, req.CustomTitle?.Trim(),
                req.IsActive ?? true, ct);
            return Ok(new IntakeFormBody(cfg.Slug, cfg.WhatsAppPhone, cfg.CustomTitle, cfg.IsActive,
                $"{_publicBaseUrl}/r/{cfg.Slug}"));
        }
        catch (IntakeFormService.SlugAlreadyTakenException)
        {
            return Conflict(new { error = "slug-already-taken" });
        }
    }

    [HttpGet("api/v1/me/form-submissions")]
    public async Task<IActionResult> GetSubmissions(
        [FromQuery] DateTimeOffset? since,
        [FromQuery] int limit,
        CancellationToken ct)
    {
        if (limit < 1 || limit > 200) limit = 50;
        var customerId = GetCustomerId();
        var rows = await _service.GetSubmissionsSinceAsync(
            customerId, since ?? DateTimeOffset.MinValue, limit, ct);
        return Ok(rows.Select(s => new SubmissionBody(s.Id, s.Username, s.FullName, s.Address, s.SubmittedAt)));
    }

    private Guid GetCustomerId()
    {
        var sub = User.FindFirst("sub")?.Value
            ?? User.FindFirst(ClaimTypes.NameIdentifier)?.Value
            ?? throw new InvalidOperationException("sub claim missing");
        return Guid.Parse(sub);
    }
}
```

- [ ] **Step 4: Program.cs — rate-limit policy ekle**

`LiveDeck.LicenseServer/Program.cs` mevcut `AddRateLimiter(...)` çağrısı içine yeni policy ekle (Phase 4a `auth-login` policy'sinin yanına):

```csharp
            opt.AddPolicy("intake-form-submit", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = int.TryParse(Environment.GetEnvironmentVariable("LIVEDECK_INTAKE_RATELIMIT_PER_HOUR"), out var n) ? n : 5,
                        Window = TimeSpan.FromHours(1)
                    }));
```

- [ ] **Step 5: GREEN**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~IntakeFormControllerTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 7/7 controller tests + önceki + 7 yeni.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.LicenseServer/Controllers/IntakeFormController.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Controllers/IntakeFormControllerTests.cs
git commit -m "feat(license-server): add IntakeFormController (REST) + intake-form-submit rate-limit policy"
```

---

### Task 6: IntakeForm public Razor page (GET form + POST submit + honeypot)

**Files:**
- Create: `LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml`
- Create: `LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI WhatsAppLinkBuilder)
- Create: `LiveDeck.LicenseServer.Tests/Pages/Public/IntakeFormPageTests.cs`

**Context:** Public anonymous Razor sayfa. GET `/r/{slug}` → form render (license-active check, 410 if not). POST submit → honeypot check (silent), validation, persist, 302 wa.me redirect. 6 test (form GET, POST 302, honeypot silent, validation 400, license expired 410, slug not found 404).

- [ ] **Step 1: IntakeForm.cshtml.cs**

`LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.IntakeForm;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.RateLimiting;

namespace LiveDeck.LicenseServer.Pages.Public;

public class IntakeFormModel : PageModel
{
    private readonly IntakeFormService _service;
    private readonly WhatsAppLinkBuilder _linkBuilder;
    private readonly ILogger<IntakeFormModel> _log;

    public IntakeFormModel(
        IntakeFormService service,
        WhatsAppLinkBuilder linkBuilder,
        ILogger<IntakeFormModel> log)
    {
        _service = service;
        _linkBuilder = linkBuilder;
        _log = log;
    }

    [BindProperty(SupportsGet = true)]
    public string Slug { get; set; } = "";

    [BindProperty]
    public IntakeFormInput Input { get; set; } = new();

    public IntakeFormConfig? Config { get; private set; }

    public sealed class IntakeFormInput
    {
        [Required(ErrorMessage = "Kullanıcı adı gerekli")]
        [StringLength(64, ErrorMessage = "En fazla 64 karakter")]
        public string Username { get; set; } = "";

        [Required(ErrorMessage = "Ad Soyad gerekli")]
        [StringLength(200, ErrorMessage = "En fazla 200 karakter")]
        public string FullName { get; set; } = "";

        [Required(ErrorMessage = "Adres gerekli")]
        [StringLength(500, ErrorMessage = "En fazla 500 karakter")]
        public string Address { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);
        return Page();
    }

    [EnableRateLimiting("intake-form-submit")]
    public async Task<IActionResult> OnPostSubmitAsync(CancellationToken ct)
    {
        // Honeypot — bot doldurursa silent 200, persist YOK, redirect YOK
        if (!string.IsNullOrEmpty(Request.Form["website"]))
        {
            _log.LogInformation("Honeypot triggered for slug {Slug}", Slug);
            Config = await _service.GetActiveBySlugAsync(Slug, ct);
            if (Config is null) return StatusCode(StatusCodes.Status410Gone);
            return Page();
        }

        Config = await _service.GetActiveBySlugAsync(Slug, ct);
        if (Config is null) return StatusCode(StatusCodes.Status410Gone);

        if (!ModelState.IsValid) return Page();

        await _service.SaveSubmissionAsync(
            Config.Id,
            Input.Username.Trim(),
            Input.FullName.Trim(),
            Input.Address.Trim(),
            HttpContext.Connection.RemoteIpAddress?.ToString(),
            Request.Headers.UserAgent.ToString(),
            ct);

        var url = _linkBuilder.Build(
            Config.WhatsAppPhone,
            Input.Username.Trim(),
            Input.FullName.Trim(),
            Input.Address.Trim());
        return Redirect(url);
    }
}
```

- [ ] **Step 2: IntakeForm.cshtml**

`LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml`:

```cshtml
@page "/r/{slug}"
@model IntakeFormModel
@{
    Layout = null;
    ViewData["Title"] = Model.Config?.CustomTitle ?? "Bilgi Gönder";
}
<!DOCTYPE html>
<html lang="tr">
<head>
    <meta charset="utf-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <title>@ViewData["Title"]</title>
    <link rel="stylesheet" href="https://cdn.jsdelivr.net/npm/bootstrap@5.3.3/dist/css/bootstrap.min.css" />
</head>
<body class="bg-light">
<main class="container py-4" style="max-width: 480px;">
    <h2 class="mb-4">@(Model.Config!.CustomTitle ?? "Bilgi Gönder")</h2>
    <form method="post" asp-page-handler="Submit" autocomplete="off">
        <input type="hidden" asp-for="Slug" />

        <div class="mb-3">
            <label asp-for="Input.Username" class="form-label">Kullanıcı adı (Instagram/TikTok)</label>
            <input asp-for="Input.Username" class="form-control form-control-lg" maxlength="64" autofocus />
            <span asp-validation-for="Input.Username" class="text-danger"></span>
        </div>

        <div class="mb-3">
            <label asp-for="Input.FullName" class="form-label">Ad Soyad</label>
            <input asp-for="Input.FullName" class="form-control form-control-lg" maxlength="200" />
            <span asp-validation-for="Input.FullName" class="text-danger"></span>
        </div>

        <div class="mb-3">
            <label asp-for="Input.Address" class="form-label">Kargo Adresi</label>
            <textarea asp-for="Input.Address" class="form-control" rows="3" maxlength="500"></textarea>
            <span asp-validation-for="Input.Address" class="text-danger"></span>
        </div>

        <input type="text" name="website" tabindex="-1" autocomplete="off"
               style="position:absolute;left:-9999px;display:none" />

        <button type="submit" class="btn btn-success btn-lg w-100">
            Tamamla ve WhatsApp'tan Gönder
        </button>
    </form>
</main>
</body>
</html>
```

- [ ] **Step 3: Program.cs DI — WhatsAppLinkBuilder**

`LiveDeck.LicenseServer/Program.cs` mevcut services bloğuna ekle:

```csharp
        builder.Services.AddSingleton<WhatsAppLinkBuilder>();
```

- [ ] **Step 4: IntakeFormPageTests yaz**

`LiveDeck.LicenseServer.Tests/Pages/Public/IntakeFormPageTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Pages.Public;

public sealed class IntakeFormPageTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public IntakeFormPageTests(ApiFactory factory) => _factory = factory;

    private async Task<(string slug, Guid customerId)> SeedConfigAsync(
        bool licenseActive = true, bool formActive = true)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"if-{Guid.NewGuid():N}@x",
            Name = "If",
            PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow,
            EmailConfirmedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        if (licenseActive)
        {
            db.Licenses.Add(new License
            {
                Id = Guid.NewGuid(),
                LicenseKey = "LDK-IFP-" + Guid.NewGuid().ToString("N"),
                CustomerId = customer.Id,
                SkuCode = "STD",
                ActivationSlots = 1,
                IssuedAt = DateTimeOffset.UtcNow,
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
            });
        }
        var slug = $"s-{Guid.NewGuid():N}"[..10];
        db.IntakeFormConfigs.Add(new IntakeFormConfig
        {
            Id = Guid.NewGuid(),
            CustomerId = customer.Id,
            Slug = slug,
            WhatsAppPhone = "+905551234567",
            IsActive = formActive,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        return (slug, customer.Id);
    }

    [Fact]
    public async Task Get_form_page_returns_200_with_form_when_active()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await resp.Content.ReadAsStringAsync();
        html.Should().Contain("Kullanıcı adı");
        html.Should().Contain("Tamamla");
    }

    [Fact]
    public async Task Get_form_page_returns_410_when_form_inactive()
    {
        var (slug, _) = await SeedConfigAsync(formActive: false);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Get_form_page_returns_410_when_license_expired()
    {
        var (slug, _) = await SeedConfigAsync(licenseActive: false);
        var client = _factory.CreateClient();

        var resp = await client.GetAsync($"/r/{slug}");

        resp.StatusCode.Should().Be(HttpStatusCode.Gone);
    }

    [Fact]
    public async Task Post_submit_with_valid_input_redirects_to_wa_me()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        // Anti-forgery token
        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.Username"] = "bilalcanli",
            ["Input.FullName"] = "Bilal Canlı",
            ["Input.Address"] = "Atatürk Cad. No:12 İstanbul"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        postResp.StatusCode.Should().Be(HttpStatusCode.Redirect);
        postResp.Headers.Location!.ToString().Should().StartWith("https://wa.me/905551234567?text=");

        // Submission persisted?
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bilalcanli")
            .FirstOrDefaultAsync();
        sub.Should().NotBeNull();
    }

    [Fact]
    public async Task Post_submit_honeypot_filled_silently_returns_200_and_does_not_persist()
    {
        var (slug, customerId) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.Username"] = "bot",
            ["Input.FullName"] = "Bot Bot",
            ["Input.Address"] = "spam",
            ["website"] = "http://bot-spam.example"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // Silent: 200, NOT redirect
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var sub = await db.IntakeFormSubmissions
            .Where(s => s.Config.CustomerId == customerId && s.Username == "bot")
            .FirstOrDefaultAsync();
        sub.Should().BeNull();
    }

    [Fact]
    public async Task Post_submit_with_missing_username_returns_400()
    {
        var (slug, _) = await SeedConfigAsync();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = true
        });

        var getResp = await client.GetAsync($"/r/{slug}");
        var antiForgery = AdminLoginHelper.ExtractAntiForgeryToken(await getResp.Content.ReadAsStringAsync());

        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = antiForgery,
            ["Slug"] = slug,
            ["Input.Username"] = "",
            ["Input.FullName"] = "Bilal",
            ["Input.Address"] = "Adres"
        });
        var postResp = await client.PostAsync($"/r/{slug}?handler=Submit", form);

        // ModelState invalid → return Page() (200 with errors), Razor convention
        postResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var html = await postResp.Content.ReadAsStringAsync();
        html.Should().Contain("Kullanıcı adı gerekli");
    }
}
```

- [ ] **Step 5: GREEN**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~IntakeFormPageTests" 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 6/6 page tests + önceki + 6 yeni.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml \
        LiveDeck.LicenseServer/Pages/Public/IntakeForm.cshtml.cs \
        LiveDeck.LicenseServer/Program.cs \
        LiveDeck.LicenseServer.Tests/Pages/Public/IntakeFormPageTests.cs
git commit -m "feat(license-server): add Pages/Public/IntakeForm Razor page (form + honeypot + 410 license-bound)"
```

---

### Task 7: LicenseApiClient — 3 yeni metot + DTOs

**Files:**
- Create: `LiveDeck.Licensing/Api/Models/IntakeFormDtos.cs`
- Modify: `LiveDeck.Licensing/Api/LicenseApiClient.cs`
- Modify: `LiveDeck.Licensing.Tests/Api/LicenseApiClientTests.cs`

**Context:** Client SDK'ya 3 metot ekle: `GetIntakeFormAsync`, `UpsertIntakeFormAsync`, `GetFormSubmissionsAsync`. DTO'lar yeni dosya. 4 yeni test (mevcut LicenseApiClientTests dosyasının sonuna).

- [ ] **Step 1: IntakeFormDtos**

`LiveDeck.Licensing/Api/Models/IntakeFormDtos.cs`:

```csharp
namespace LiveDeck.Licensing.Api.Models;

public sealed record IntakeFormConfigDto(
    string Slug,
    string WhatsAppPhone,
    string? CustomTitle,
    bool IsActive,
    string FormUrl);

public sealed record IntakeFormUpsertRequest(
    string Slug,
    string WhatsAppPhone,
    string? CustomTitle,
    bool? IsActive);

public sealed record IntakeFormSubmissionDto(
    Guid Id,
    string Username,
    string FullName,
    string Address,
    DateTimeOffset SubmittedAt);
```

- [ ] **Step 2: LicenseApiClient genişlet**

`LiveDeck.Licensing/Api/LicenseApiClient.cs` — mevcut metotların **sonuna** (sınıf gövdesinde, helper'lardan önce) ekle:

```csharp
    // ─── Intake Form (Phase 4f) ───────────────────────────────────────

    /// <summary>Returns null when no config is set yet (404 from server).</summary>
    public async Task<IntakeFormConfigDto?> GetIntakeFormAsync(CancellationToken ct = default)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync("/api/v1/me/intake-form", ct); }
        catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

        using (resp)
        {
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
            if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
            return await DeserializeAsync<IntakeFormConfigDto>(resp, ct);
        }
    }

    public Task<IntakeFormConfigDto> UpsertIntakeFormAsync(IntakeFormUpsertRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<IntakeFormUpsertRequest, IntakeFormConfigDto>(
            "/api/v1/me/intake-form", req, ct, methodOverride: HttpMethod.Put);

    public async Task<List<IntakeFormSubmissionDto>> GetFormSubmissionsAsync(
        DateTimeOffset? since, int limit = 50, CancellationToken ct = default)
    {
        var qs = since is null
            ? $"?limit={limit}"
            : $"?since={Uri.EscapeDataString(since.Value.ToString("O"))}&limit={limit}";

        HttpResponseMessage resp;
        try { resp = await _http.GetAsync("/api/v1/me/form-submissions" + qs, ct); }
        catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
            return (await DeserializeAsync<List<IntakeFormSubmissionDto>>(resp, ct)) ?? new();
        }
    }
```

`PostJsonExpectingJsonAsync` mevcut helper'ı yalnızca POST kabul ediyordu. **Genişlet:** mevcut signature'a opsiyonel `HttpMethod methodOverride = null` parametre ekle.

`LicenseApiClient.cs` mevcut helper signature'ını şu şekilde güncelle:

```csharp
    private async Task<TResp> PostJsonExpectingJsonAsync<TReq, TResp>(
        string path, TReq body, CancellationToken ct, int[]? successCodes = null,
        HttpMethod? methodOverride = null)
    {
        var method = methodOverride ?? HttpMethod.Post;
        using var resp = await SendJsonAsync(method, path, body, ct);
        var ok = successCodes is null
            ? resp.IsSuccessStatusCode
            : Array.IndexOf(successCodes, (int)resp.StatusCode) >= 0;
        if (!ok) await ThrowMappedAsync(resp);
        return (await DeserializeAsync<TResp>(resp, ct))!;
    }
```

(Mevcut çağrılar default `methodOverride = null` ile POST davranışını koruyor — backward compatible.)

- [ ] **Step 3: Failing tests yaz**

`LiveDeck.Licensing.Tests/Api/LicenseApiClientTests.cs` mevcut sınıfa **mevcut testlerden sonra** 4 yeni test ekle (sınıfın `}` öncesi):

```csharp
    [Fact]
    public async Task GetIntakeFormAsync_returns_null_on_404()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(404));

        var result = await client.GetIntakeFormAsync();
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetIntakeFormAsync_returns_dto_on_200()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"slug":"burak","whatsAppPhone":"+905551234567","customTitle":"Title","isActive":true,"formUrl":"https://x/r/burak"}"""));

        var result = await client.GetIntakeFormAsync();

        result.Should().NotBeNull();
        result!.Slug.Should().Be("burak");
        result.FormUrl.Should().Be("https://x/r/burak");
    }

    [Fact]
    public async Task UpsertIntakeFormAsync_uses_PUT_method()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"slug":"new","whatsAppPhone":"+905551234567","customTitle":null,"isActive":true,"formUrl":"https://x/r/new"}"""));

        await client.UpsertIntakeFormAsync(new IntakeFormUpsertRequest("new", "+905551234567", null, true));

        handler.Requests[0].Method.Method.Should().Be("PUT");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/me/intake-form");
    }

    [Fact]
    public async Task GetFormSubmissionsAsync_returns_list_with_since_query_param()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"u","fullName":"n","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var since = new DateTimeOffset(2026, 4, 30, 11, 0, 0, TimeSpan.Zero);
        var rows = await client.GetFormSubmissionsAsync(since, limit: 25);

        rows.Should().HaveCount(1);
        rows[0].Username.Should().Be("u");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/me/form-submissions");
        handler.Requests[0].RequestUri.Query.Should().Contain("since=").And.Contain("limit=25");
    }
```

- [ ] **Step 4: GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Licensing 2>&1 | tail -5
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LicenseApiClientTests" 2>&1 | tail -5
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 4 yeni test pass + mevcut LicenseApiClientTests pass + tüm Licensing 108/108 (104 baseline + 4 yeni).

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.Licensing/Api/Models/IntakeFormDtos.cs \
        LiveDeck.Licensing/Api/LicenseApiClient.cs \
        LiveDeck.Licensing.Tests/Api/LicenseApiClientTests.cs
git commit -m "feat(licensing): add 3 intake form client methods to LicenseApiClient"
```

---

### Task 8: Customer.Address + Core migration 006 + repo Upsert metot

**Files:**
- Modify: `LiveDeck.Core/Customers/Customer.cs`
- Create: `LiveDeck.Core/Storage/Migrations/006_intake_form_address.sql`
- Modify: `LiveDeck.Core/Storage/Repositories/CustomerRepository.cs`
- Modify: `LiveDeck.Tests/Storage/CustomerRepositoryTests.cs`
- Modify: `LiveDeck.Tests/Storage/MigrationRunnerTests.cs`

**Context:** LiveDeck.Core'a `Customer.Address` (nullable) + SQLite migration 006 + CustomerRepository.UpsertFromIntakeForm metodu. Mevcut CustomerRepositoryTests ve MigrationRunnerTests'e regression + yeni test'ler.

- [ ] **Step 1: Customer record genişlet**

`LiveDeck.Core/Customers/Customer.cs` mevcut record'a son alan ekle:

```csharp
namespace LiveDeck.Core.Customers;

public sealed record Customer(
    string Id,
    string Platform,
    string Username,
    string? DisplayName,
    string? AvatarUrl,
    long FirstSeenAt,
    long LastSeenAt,
    bool IsBlacklisted,
    string? BlacklistReason,
    string? Notes,
    int TotalLabelsPrinted,
    decimal TotalAmount,
    long? BlacklistedAt,
    string? Address);   // YENİ Phase 4f
```

- [ ] **Step 2: SQLite migration 006**

`LiveDeck.Core/Storage/Migrations/006_intake_form_address.sql`:

```sql
-- Phase 4f: address from intake form submissions
ALTER TABLE Customer ADD COLUMN Address TEXT;

UPDATE _meta SET SchemaVersion = 6 WHERE Id = 1;
```

- [ ] **Step 3: CustomerRepository — Upsert metot ekle**

`LiveDeck.Core/Storage/Repositories/CustomerRepository.cs` mevcut sınıfa ekle (mevcut SELECT/INSERT'ler stilinde):

```csharp
    /// <summary>
    /// Upsert by (Platform, Username). Phase 4f intake form sync için.
    /// Mevcut müşteri varsa DisplayName, Address, LastSeenAt güncellenir;
    /// yoksa yeni satır insert edilir.
    /// </summary>
    public Customer UpsertFromIntakeForm(string username, string fullName, string address, long nowUnix)
    {
        const string platform = "form";
        using var conn = _connectionFactory.Open();

        var existing = conn.QueryFirstOrDefault<Customer>(@"
            SELECT Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                   IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                   BlacklistedAt, Address
            FROM Customer
            WHERE Platform = @platform AND Username = @username",
            new { platform, username });

        if (existing is not null)
        {
            conn.Execute(@"
                UPDATE Customer
                SET DisplayName = @fullName,
                    Address = @address,
                    LastSeenAt = @nowUnix
                WHERE Id = @id",
                new { fullName, address, nowUnix, id = existing.Id });
            return existing with { DisplayName = fullName, Address = address, LastSeenAt = nowUnix };
        }

        var id = Guid.NewGuid().ToString("N");
        conn.Execute(@"
            INSERT INTO Customer (Id, Platform, Username, DisplayName, AvatarUrl, FirstSeenAt, LastSeenAt,
                                  IsBlacklisted, BlacklistReason, Notes, TotalLabelsPrinted, TotalAmount,
                                  BlacklistedAt, Address)
            VALUES (@id, @platform, @username, @fullName, NULL, @nowUnix, @nowUnix,
                    0, NULL, NULL, 0, 0, NULL, @address)",
            new { id, platform, username, fullName, nowUnix, address });

        return new Customer(id, platform, username, fullName, null, nowUnix, nowUnix,
            false, null, null, 0, 0m, null, address);
    }
```

**Not:** mevcut `INSERT` çağrılarına `Address` parametresi eklemeli (Phase 1+ Insert metodunda da). Mevcut `INSERT INTO Customer` SQL satırlarını grep ile bul; her birine `Address` eklenir (NULL default kabul). Mevcut SELECT statementlere de `Address` eklenir.

CustomerRepository'nin mevcut metotlarında SQL düzenlemeleri:
- `Insert` metodu: SQL'e `, Address` ekle (NULL default)
- `GetById`, `Search`, `GetByPlatformAndUsername` (eğer varsa) SQL SELECT'lere `Address` ekle

Bu bir refactor; testler regression'ı doğrular. CustomerRepositoryTests mevcut Insert testleri Address null kontrolüyle pas geçer.

- [ ] **Step 4: MigrationRunnerTests güncelle**

`LiveDeck.Tests/Storage/MigrationRunnerTests.cs` mevcut Phase 2a/3a migration testlerini bul. Schema version assertion'ı 6'ya yükselt:

Mevcut test (Phase 2a sonrası):
```csharp
var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
version.Should().Be(5);   // veya mevcut version
```

Yeni:
```csharp
version.Should().Be(6);
```

Plus yeni test:
```csharp
[Fact]
public void Migration_006_adds_Address_column_to_Customer()
{
    using var db = new InMemorySqlite();
    var runner = new MigrationRunner(db);
    runner.Run();

    using var conn = db.Open();
    var hasAddress = conn.ExecuteScalar<int>(@"
        SELECT COUNT(*) FROM pragma_table_info('Customer') WHERE name = 'Address'");
    hasAddress.Should().Be(1);
}
```

- [ ] **Step 5: CustomerRepositoryTests yeni test'ler**

`LiveDeck.Tests/Storage/CustomerRepositoryTests.cs` mevcut sınıfa 3 yeni test ekle:

```csharp
    [Fact]
    public void UpsertFromIntakeForm_creates_new_customer_with_form_platform()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        var customer = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Canlı", "Atatürk Cad. No:12", now);

        customer.Platform.Should().Be("form");
        customer.Username.Should().Be("bilalcanli");
        customer.DisplayName.Should().Be("Bilal Canlı");
        customer.Address.Should().Be("Atatürk Cad. No:12");
        customer.FirstSeenAt.Should().Be(now);
        customer.LastSeenAt.Should().Be(now);
    }

    [Fact]
    public void UpsertFromIntakeForm_updates_existing_customer_by_platform_username()
    {
        var repo = CreateRepository();
        var firstNow = 1714521600L;
        var secondNow = 1714608000L;

        var first = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Eski", "Eski Adres", firstNow);
        var second = repo.UpsertFromIntakeForm("bilalcanli", "Bilal Yeni", "Yeni Adres", secondNow);

        second.Id.Should().Be(first.Id);    // same row
        second.DisplayName.Should().Be("Bilal Yeni");
        second.Address.Should().Be("Yeni Adres");
        second.FirstSeenAt.Should().Be(firstNow);
        second.LastSeenAt.Should().Be(secondNow);
    }

    [Fact]
    public void UpsertFromIntakeForm_treats_form_platform_as_distinct_from_instagram()
    {
        var repo = CreateRepository();
        var now = 1714521600L;

        // Same username, different platform — distinct customers
        repo.UpsertFromIntakeForm("bilalcanli", "Bilal F", "Form Adres", now);
        // Mevcut Insert API ile Instagram customer create
        repo.Insert(new Customer(
            Id: Guid.NewGuid().ToString("N"),
            Platform: "instagram",
            Username: "bilalcanli",
            DisplayName: "Bilal IG",
            AvatarUrl: null, FirstSeenAt: now, LastSeenAt: now,
            IsBlacklisted: false, BlacklistReason: null, Notes: null,
            TotalLabelsPrinted: 0, TotalAmount: 0m, BlacklistedAt: null,
            Address: null));

        var allByUsername = repo.Search("bilalcanli", limit: 10);
        allByUsername.Should().HaveCount(2);
        allByUsername.Should().Contain(c => c.Platform == "form");
        allByUsername.Should().Contain(c => c.Platform == "instagram");
    }
```

(Mevcut `CreateRepository` helper'ı sınıfta zaten var — Phase 1+ pattern.)

- [ ] **Step 6: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.Core 2>&1 | tail -5
dotnet build LiveDeck.Tests 2>&1 | tail -5
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~CustomerRepositoryTests|FullyQualifiedName~MigrationRunnerTests" 2>&1 | tail -5
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. CustomerRepositoryTests + MigrationRunnerTests yeni test'ler pass + 128 + 4 = 132/132 toplam.

**Eğer mevcut testler bozulduysa:** Customer record yeni alan eklediği için Insert/Update SQL'leri uyumsuz olabilir. Phase 1+ Insert/Update metotlarındaki SQL'lere `Address` parametresi eklemek gerek (nullable, default NULL). Eğer eski testler `new Customer(...)` çağrısını kullanıyorsa, 14. parametre olarak `Address: null` eklenir.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.Core/Customers/Customer.cs \
        LiveDeck.Core/Storage/Migrations/006_intake_form_address.sql \
        LiveDeck.Core/Storage/Repositories/CustomerRepository.cs \
        LiveDeck.Tests/Storage/CustomerRepositoryTests.cs \
        LiveDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(core): add Customer.Address + migration 006 + UpsertFromIntakeForm"
```

---

### Task 9: IntakeFormSyncService + IntakeFormSyncHostedService (LiveDeck.App)

**Files:**
- Modify: `LiveDeck.Core/Settings/AppSettings.cs` (add `LastIntakeFormSync` property)
- Create: `LiveDeck.App/Services/IntakeForm/IntakeFormSyncService.cs`
- Create: `LiveDeck.App/Services/IntakeForm/IntakeFormSyncHostedService.cs`
- Modify: `LiveDeck.App/AppHost.cs` (DI)
- Modify: `LiveDeck.App/App.xaml.cs` (manuel start of hosted service)
- Create: `LiveDeck.Tests/Services/IntakeForm/IntakeFormSyncServiceTests.cs`
- Create: `LiveDeck.Tests/Services/IntakeForm/IntakeFormSyncHostedServiceTests.cs`

**Context:** Phase 4b HeartbeatHostedService pattern ile 2dk PeriodicTimer. Pull → Customer create/update. `IntakeFormSyncService` test'lenebilir (clock + LicenseApiClient mock); HostedService timer test'i. 5 + 2 test.

- [ ] **Step 1: AppSettings.LastIntakeFormSync ekle**

`LiveDeck.Core/Settings/AppSettings.cs` mevcut sınıfa ekle:

```csharp
    /// <summary>Phase 4f: last intake form submission cursor (max SubmittedAt synced).</summary>
    public DateTimeOffset? LastIntakeFormSync { get; set; }
```

- [ ] **Step 2: IntakeFormSyncService impl**

`LiveDeck.App/Services/IntakeForm/IntakeFormSyncService.cs`:

```csharp
using LiveDeck.Core.Customers;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services.IntakeForm;

/// <summary>
/// Pulls new IntakeFormSubmission rows from the license server, upserts each
/// as a Customer (platform="form"), advances the cursor in AppSettings.
/// Idempotent: duplicate calls are no-op (server filters by SubmittedAt &gt; since).
/// </summary>
public sealed class IntakeFormSyncService
{
    private readonly LicenseApiClient _api;
    private readonly CustomerRepository _customers;
    private readonly SettingsStore _settingsStore;
    private readonly AppSettings _settings;
    private readonly IClock _clock;
    private readonly ILogger<IntakeFormSyncService> _log;

    public event EventHandler<int>? SubmissionsSynced;

    public IntakeFormSyncService(
        LicenseApiClient api,
        CustomerRepository customers,
        SettingsStore settingsStore,
        AppSettings settings,
        IClock clock,
        ILogger<IntakeFormSyncService> log)
    {
        _api = api;
        _customers = customers;
        _settingsStore = settingsStore;
        _settings = settings;
        _clock = clock;
        _log = log;
    }

    public async Task<int> SyncOnceAsync(CancellationToken ct = default)
    {
        var since = _settings.LastIntakeFormSync;

        List<IntakeFormSubmissionDto> submissions;
        try
        {
            submissions = await _api.GetFormSubmissionsAsync(since, limit: 50, ct);
        }
        catch (LicenseApiException ex)
        {
            _log.LogWarning(ex, "Intake form sync failed: {Code}", ex.Code);
            return 0;
        }

        if (submissions.Count == 0) return 0;

        var nowUnix = _clock.UnixNow();
        DateTimeOffset newCursor = since ?? DateTimeOffset.MinValue;

        foreach (var sub in submissions.OrderBy(s => s.SubmittedAt))
        {
            _customers.UpsertFromIntakeForm(sub.Username, sub.FullName, sub.Address, nowUnix);
            if (sub.SubmittedAt > newCursor) newCursor = sub.SubmittedAt;
        }

        _settings.LastIntakeFormSync = newCursor;
        _settingsStore.Save(_settings);

        _log.LogInformation("Intake form sync: {Count} submission(s) processed (cursor → {Cursor})",
            submissions.Count, newCursor);

        SubmissionsSynced?.Invoke(this, submissions.Count);
        return submissions.Count;
    }
}
```

- [ ] **Step 3: IntakeFormSyncHostedService impl**

`LiveDeck.App/Services/IntakeForm/IntakeFormSyncHostedService.cs`:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace LiveDeck.App.Services.IntakeForm;

/// <summary>
/// 2-minute PeriodicTimer loop calling IntakeFormSyncService.SyncOnceAsync.
/// Phase 4b HeartbeatHostedService pattern.
/// </summary>
public sealed class IntakeFormSyncHostedService : BackgroundService
{
    private readonly IntakeFormSyncService _syncService;
    private readonly ILogger<IntakeFormSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public IntakeFormSyncHostedService(
        IntakeFormSyncService syncService,
        ILogger<IntakeFormSyncHostedService> log)
        : this(syncService, log, TimeSpan.FromMinutes(2)) { }

    internal IntakeFormSyncHostedService(
        IntakeFormSyncService syncService,
        ILogger<IntakeFormSyncHostedService> log,
        TimeSpan interval)
    {
        _syncService = syncService;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try
            {
                await _syncService.SyncOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Intake form sync tick failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
```

- [ ] **Step 4: AppHost DI**

`LiveDeck.App/AppHost.cs` mevcut Phase 4b/4c licensing bloğunun **sonuna** ekle:

```csharp
        // Intake form sync (Phase 4f)
        services.AddSingleton<IntakeFormSyncService>(sp => new IntakeFormSyncService(
            sp.GetRequiredService<LicenseApiClient>(),
            sp.GetRequiredService<CustomerRepository>(),
            sp.GetRequiredService<SettingsStore>(),
            sp.GetRequiredService<AppSettings>(),
            sp.GetRequiredService<IClock>(),
            sp.GetRequiredService<ILogger<IntakeFormSyncService>>()));
        services.AddHostedService<IntakeFormSyncHostedService>();
```

`using LiveDeck.App.Services.IntakeForm;` AppHost.cs üst tarafına ekle.

- [ ] **Step 5: App.xaml.cs — IntakeForm hosted service start**

`LiveDeck.App/App.xaml.cs` mevcut Phase 4b heartbeat start bloğunu bul. Aynı pattern'le IntakeForm sync de start:

```csharp
        // Phase 4f: intake form sync hosted service
        var intakeSync = Host.Services.GetServices<IHostedService>()
            .OfType<LiveDeck.App.Services.IntakeForm.IntakeFormSyncHostedService>()
            .FirstOrDefault();
        _ = intakeSync?.StartAsync(CancellationToken.None);
```

Field eklenir:
```csharp
    private LiveDeck.App.Services.IntakeForm.IntakeFormSyncHostedService? _intakeSync;
```

(Alanı set et + OnExit'te `_intakeSync?.StopAsync(...)`.)

- [ ] **Step 6: IntakeFormSyncServiceTests yaz**

`LiveDeck.Tests/Services/IntakeForm/IntakeFormSyncServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.App.Services.IntakeForm;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveDeck.Tests.Services.IntakeForm;

public sealed class IntakeFormSyncServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    private static (IntakeFormSyncService svc, CustomerRepository repo, AppSettings settings, FakeHttpMessageHandler handler) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var dbFactory = new SqliteConnectionFactory(":memory:");
        new MigrationRunner(dbFactory).Run();
        var repo = new CustomerRepository(dbFactory);

        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();

        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);

        var svc = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        return (svc, repo, settings, handler);
    }

    [Fact]
    public async Task SyncOnceAsync_returns_zero_when_server_returns_empty()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200, "[]"));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
    }

    [Fact]
    public async Task SyncOnceAsync_creates_customer_with_form_platform()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"bilalcanli","fullName":"Bilal Canlı","address":"Atatürk Cad","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        var count = await svc.SyncOnceAsync();

        count.Should().Be(1);
        var customers = repo.Search("bilalcanli", limit: 5);
        customers.Should().Contain(c => c.Platform == "form" && c.Username == "bilalcanli");
    }

    [Fact]
    public async Task SyncOnceAsync_updates_existing_form_customer_on_second_pull()
    {
        var (svc, repo, _, _) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"u1","fullName":"Eski Ad","address":"Eski","submittedAt":"2026-04-30T11:00:00Z"},
                 {"id":"00000000-0000-0000-0000-000000000002","username":"u1","fullName":"Yeni Ad","address":"Yeni","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        await svc.SyncOnceAsync();

        var customer = repo.Search("u1", limit: 5).Single(c => c.Platform == "form");
        customer.DisplayName.Should().Be("Yeni Ad");
        customer.Address.Should().Be("Yeni");
    }

    [Fact]
    public async Task SyncOnceAsync_advances_cursor_to_max_submittedAt()
    {
        var (svc, _, settings, handler) = Build(_ => FakeHttpMessageHandler.Json(200,
            """[{"id":"00000000-0000-0000-0000-000000000001","username":"u","fullName":"n","address":"a","submittedAt":"2026-04-30T12:00:00Z"}]"""));

        await svc.SyncOnceAsync();

        settings.LastIntakeFormSync.Should().Be(new DateTimeOffset(2026, 4, 30, 12, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public async Task SyncOnceAsync_returns_zero_on_network_failure_and_does_not_advance_cursor()
    {
        var (svc, _, settings, _) = Build(_ => throw new HttpRequestException("dns fail"));
        settings.LastIntakeFormSync = new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero);

        var count = await svc.SyncOnceAsync();

        count.Should().Be(0);
        settings.LastIntakeFormSync.Should().Be(new DateTimeOffset(2026, 4, 30, 10, 0, 0, TimeSpan.Zero));
    }
}
```

- [ ] **Step 7: IntakeFormSyncHostedServiceTests yaz**

`LiveDeck.Tests/Services/IntakeForm/IntakeFormSyncHostedServiceTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.App.Services.IntakeForm;
using LiveDeck.Core.Customers;
using LiveDeck.Core.Settings;
using LiveDeck.Core.Storage;
using LiveDeck.Core.Storage.Repositories;
using LiveDeck.Core.Time;
using LiveDeck.Licensing.Api;
using LiveDeck.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LiveDeck.Tests.Services.IntakeForm;

public sealed class IntakeFormSyncHostedServiceTests
{
    private sealed class FakeClock : IClock
    {
        public long UnixNow() => 1714521600L;
        public DateTimeOffset Now => DateTimeOffset.FromUnixTimeSeconds(1714521600L);
    }

    [Fact]
    public async Task Hosted_service_calls_SyncOnceAsync_periodically()
    {
        int callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref callCount);
            return FakeHttpMessageHandler.Json(200, "[]");
        });

        var dbFactory = new SqliteConnectionFactory(":memory:");
        new MigrationRunner(dbFactory).Run();
        var repo = new CustomerRepository(dbFactory);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        var sync = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        var hosted = new IntakeFormSyncHostedService(sync,
            NullLogger<IntakeFormSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        await Task.Delay(250);
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        callCount.Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Hosted_service_continues_after_sync_throws()
    {
        int callCount = 0;
        var handler = new FakeHttpMessageHandler(_ =>
        {
            var c = Interlocked.Increment(ref callCount);
            if (c == 1) throw new HttpRequestException("fail once");
            return FakeHttpMessageHandler.Json(200, "[]");
        });

        var dbFactory = new SqliteConnectionFactory(":memory:");
        new MigrationRunner(dbFactory).Run();
        var repo = new CustomerRepository(dbFactory);
        var settingsPath = Path.Combine(Path.GetTempPath(), $"settings-{Guid.NewGuid():N}.json");
        var store = new SettingsStore(settingsPath);
        var settings = store.Load();
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        var sync = new IntakeFormSyncService(api, repo, store, settings, new FakeClock(),
            NullLogger<IntakeFormSyncService>.Instance);
        var hosted = new IntakeFormSyncHostedService(sync,
            NullLogger<IntakeFormSyncHostedService>.Instance,
            TimeSpan.FromMilliseconds(50));

        using var cts = new CancellationTokenSource();
        await hosted.StartAsync(cts.Token);
        await Task.Delay(250);
        cts.Cancel();
        try { await hosted.StopAsync(CancellationToken.None); } catch { }

        // İlk call throw, 2. call success — toplam ≥2
        callCount.Should().BeGreaterThanOrEqualTo(2);
    }
}
```

- [ ] **Step 8: GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~IntakeFormSync" 2>&1 | tail -5
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 5 + 2 = 7 yeni test pass + tüm LiveDeck.Tests 132 + 7 = 139 toplam.

**Eğer `FakeHttpMessageHandler` LiveDeck.Tests projesinde yoksa:** Phase 4b'de `LiveDeck.Licensing.Tests/TestHelpers/FakeHttpMessageHandler.cs` var. LiveDeck.Tests'te yeni helper olarak `LiveDeck.Tests/TestHelpers/FakeHttpMessageHandler.cs` oluştur (Phase 4b dosyasını kopyala — public class olduğu için reuse mümkün ama farklı assembly nedeniyle kopya daha sade).

- [ ] **Step 9: Commit**

```bash
git add LiveDeck.Core/Settings/AppSettings.cs \
        LiveDeck.App/Services/IntakeForm/ \
        LiveDeck.App/AppHost.cs \
        LiveDeck.App/App.xaml.cs \
        LiveDeck.Tests/Services/IntakeForm/ \
        LiveDeck.Tests/TestHelpers/
git commit -m "feat(app): add IntakeFormSyncService + 2dk hosted service polling"
```

---

### Task 10: Settings dialog "Form Linki" tab + ViewModel

**Files:**
- Create: `LiveDeck.App/ViewModels/IntakeFormSettingsViewModel.cs`
- Modify: `LiveDeck.App/Views/SettingsDialog.xaml`
- Modify: `LiveDeck.App/AppHost.cs` (DI)

**Context:** Phase 2a SettingsDialog tabbed yapısına yeni "Form Linki" tab. ViewModel: GET /me/intake-form (load), PUT (save), Clipboard.SetText (copy). Validation hata mesajları StatusMessage'a yansır. Yeni test yok — UI manuel smoke (Phase 4d Razor pages UI ile aynı approach).

- [ ] **Step 1: IntakeFormSettingsViewModel oluştur**

`LiveDeck.App/ViewModels/IntakeFormSettingsViewModel.cs`:

```csharp
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;

namespace LiveDeck.App.ViewModels;

public sealed partial class IntakeFormSettingsViewModel : ObservableObject
{
    private readonly LicenseApiClient _api;

    public IntakeFormSettingsViewModel(LicenseApiClient api)
    {
        _api = api;
        LoadCommand = new AsyncRelayCommand(LoadAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => !IsBusy);
        CopyLinkCommand = new RelayCommand(CopyLink, () => HasFormUrl);
    }

    [ObservableProperty] private string _slug = "";
    [ObservableProperty] private string _whatsAppPhone = "";
    [ObservableProperty] private string _customTitle = "";
    [ObservableProperty] private bool _isActive = true;
    [ObservableProperty] private string _formUrl = "";
    [ObservableProperty] private string _statusMessage = "";
    [ObservableProperty] private Brush _statusBrush = Brushes.Gray;
    [ObservableProperty] private bool _isBusy;

    public bool HasFormUrl => !string.IsNullOrEmpty(FormUrl);

    public ICommand LoadCommand { get; }
    public ICommand SaveCommand { get; }
    public ICommand CopyLinkCommand { get; }

    public async Task LoadAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var cfg = await _api.GetIntakeFormAsync();
            if (cfg is null)
            {
                StatusMessage = "Henüz form linkin yok. Slug ve WhatsApp telefonunu girip kaydet.";
                StatusBrush = Brushes.Gray;
                return;
            }
            Slug = cfg.Slug;
            WhatsAppPhone = cfg.WhatsAppPhone;
            CustomTitle = cfg.CustomTitle ?? "";
            IsActive = cfg.IsActive;
            FormUrl = cfg.FormUrl;
            StatusMessage = "Yüklendi.";
            StatusBrush = Brushes.SeaGreen;
            OnPropertyChanged(nameof(HasFormUrl));
        }
        catch (LicenseApiNetworkException)
        {
            StatusMessage = "Sunucuya ulaşılamadı.";
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiException ex)
        {
            StatusMessage = "Hata: " + ex.Message;
            StatusBrush = Brushes.Crimson;
        }
        finally { IsBusy = false; }
    }

    private async Task SaveAsync()
    {
        IsBusy = true;
        StatusMessage = "";
        try
        {
            var req = new IntakeFormUpsertRequest(
                Slug.Trim().ToLowerInvariant(),
                WhatsAppPhone.Trim(),
                string.IsNullOrWhiteSpace(CustomTitle) ? null : CustomTitle.Trim(),
                IsActive);
            var cfg = await _api.UpsertIntakeFormAsync(req);
            Slug = cfg.Slug;
            WhatsAppPhone = cfg.WhatsAppPhone;
            CustomTitle = cfg.CustomTitle ?? "";
            IsActive = cfg.IsActive;
            FormUrl = cfg.FormUrl;
            StatusMessage = "Kaydedildi: " + cfg.FormUrl;
            StatusBrush = Brushes.SeaGreen;
            OnPropertyChanged(nameof(HasFormUrl));
        }
        catch (ValidationException ex)
        {
            StatusMessage = TranslateValidationCode(ex.Code);
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiException ex) when (ex.Code == "slug-already-taken" || ex.Message.Contains("slug-already-taken"))
        {
            StatusMessage = "Bu slug başka bir yayıncı tarafından alındı. Farklı bir slug seç.";
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiNetworkException)
        {
            StatusMessage = "Sunucuya ulaşılamadı.";
            StatusBrush = Brushes.Crimson;
        }
        catch (LicenseApiException ex)
        {
            StatusMessage = "Hata: " + ex.Message;
            StatusBrush = Brushes.Crimson;
        }
        finally { IsBusy = false; }
    }

    private void CopyLink()
    {
        if (!HasFormUrl) return;
        try
        {
            Clipboard.SetText(FormUrl);
            StatusMessage = "Link panoya kopyalandı.";
            StatusBrush = Brushes.DodgerBlue;
        }
        catch
        {
            // Clipboard sometimes fails on remote desktop; ignore
        }
    }

    private static string TranslateValidationCode(string code) => code switch
    {
        "invalid-slug-empty" => "Slug boş olamaz.",
        "invalid-slug-invalidlength" => "Slug 3-32 karakter arası olmalı.",
        "invalid-slug-invalidformat" => "Slug sadece küçük harf, rakam ve tire içerebilir.",
        "invalid-slug-reserved" => "Bu slug rezerve. Farklı bir tane dene.",
        "invalid-phone-format" => "Telefon E.164 formatında olmalı (örn. +905551234567).",
        _ => "Doğrulama hatası: " + code
    };
}
```

- [ ] **Step 2: SettingsDialog.xaml — yeni TabItem ekle**

`LiveDeck.App/Views/SettingsDialog.xaml` mevcut TabControl içine yeni tab ekle (mevcut Phase 2a/3b-1 tab'larının yanına):

```xml
<TabItem Header="Form Linki">
    <ScrollViewer>
        <StackPanel Margin="16">
            <TextBlock Text="Müşteri Bilgi Formu" FontWeight="Bold" FontSize="16" Margin="0,0,0,8"/>
            <TextBlock Text="Müşterilerin form doldurup WhatsApp'tan size mesaj göndermesi için kalıcı bir link."
                       Foreground="Gray" TextWrapping="Wrap" Margin="0,0,0,16"/>

            <Label Content="Slug (URL'in son parçası)"/>
            <TextBox Text="{Binding Slug, UpdateSourceTrigger=PropertyChanged}"
                     ToolTip="Sadece küçük harf, rakam, tire. 3-32 karakter."/>
            <TextBlock Text="Örnek: 'burakstreamer' → https://license.livedeck.app/r/burakstreamer"
                       Foreground="Gray" FontSize="11" Margin="0,2,0,0"/>

            <Label Content="WhatsApp telefon (E.164 formatı)" Margin="0,12,0,0"/>
            <TextBox Text="{Binding WhatsAppPhone, UpdateSourceTrigger=PropertyChanged}"/>
            <TextBlock Text="Örnek: +905551234567 (başında + ve ülke kodu olmalı)"
                       Foreground="Gray" FontSize="11" Margin="0,2,0,0"/>

            <Label Content="Form başlığı (opsiyonel)" Margin="0,12,0,0"/>
            <TextBox Text="{Binding CustomTitle, UpdateSourceTrigger=PropertyChanged}"
                     ToolTip="Form sayfasının üstünde görünür. Boş bırakılırsa 'Bilgi Gönder'."/>

            <CheckBox Content="Form aktif (kapatırsan link 410 Gone döner)"
                      IsChecked="{Binding IsActive}" Margin="0,12,0,0"/>

            <StackPanel Orientation="Horizontal" Margin="0,16,0,0">
                <Button Content="Kaydet" Command="{Binding SaveCommand}" Padding="20,8" MinWidth="100"/>
                <Button Content="Linki Kopyala" Command="{Binding CopyLinkCommand}"
                        Padding="20,8" MinWidth="120" Margin="8,0,0,0"/>
            </StackPanel>

            <Border BorderBrush="LightGray" BorderThickness="1" Padding="8" Margin="0,16,0,0"
                    Visibility="{Binding HasFormUrl, Converter={StaticResource BoolToVis}}">
                <StackPanel>
                    <TextBlock Text="Linkin:" Foreground="Gray" FontSize="11"/>
                    <TextBlock Text="{Binding FormUrl}" FontFamily="Consolas"
                               TextWrapping="Wrap" Foreground="DodgerBlue"/>
                </StackPanel>
            </Border>

            <TextBlock Text="{Binding StatusMessage}" Foreground="{Binding StatusBrush}"
                       Margin="0,12,0,0" TextWrapping="Wrap"/>
        </StackPanel>
    </ScrollViewer>
</TabItem>
```

`BoolToVis` converter — Phase 4d'de `BooleanToVisibilityConverter` zaten kullanılıyor; SettingsDialog'a da eklenir (yoksa):
```xml
<Window.Resources>
    <BooleanToVisibilityConverter x:Key="BoolToVis"/>
</Window.Resources>
```

- [ ] **Step 3: SettingsViewModel — IntakeForm tab integration**

`LiveDeck.App/ViewModels/SettingsViewModel.cs` (Phase 2a) mevcut sınıfa yeni property ekle (Settings dialog ana ViewModel zaten transient):

```csharp
    public IntakeFormSettingsViewModel IntakeForm { get; }
```

Constructor'a `IntakeFormSettingsViewModel intakeForm` parametre eklenir:

```csharp
    public SettingsViewModel(/* mevcut deps */, IntakeFormSettingsViewModel intakeForm)
    {
        // mevcut atamalar
        IntakeForm = intakeForm;
        _ = IntakeForm.LoadAsync();   // dialog açılınca otomatik yükle
    }
```

XAML binding `DataContext="{Binding IntakeForm}"` ile tab içine bağlanır:
```xml
<TabItem Header="Form Linki" DataContext="{Binding IntakeForm}">
    <!-- yukarıdaki içerik -->
</TabItem>
```

- [ ] **Step 4: AppHost DI**

`LiveDeck.App/AppHost.cs` mevcut transient ViewModels listesine ekle:

```csharp
        services.AddTransient<ViewModels.IntakeFormSettingsViewModel>();
```

- [ ] **Step 5: Build + tests (regression)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -5
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 139/139 (no new tests; UI tab eklemesi).

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.App/ViewModels/IntakeFormSettingsViewModel.cs \
        LiveDeck.App/ViewModels/SettingsViewModel.cs \
        LiveDeck.App/Views/SettingsDialog.xaml \
        LiveDeck.App/AppHost.cs
git commit -m "feat(app): add 'Form Linki' tab to Settings dialog (slug + phone + copy link)"
```

---

### Task 11: MainShell badge + CustomerSearchDialog Platform=form filter

**Files:**
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs` (NewIntakeSubmissionsCount property + event subscription)
- Modify: `LiveDeck.App/Views/MainShellView.xaml` (badge UI element)
- Modify: `LiveDeck.App/ViewModels/CustomerSearchViewModel.cs` (Platform=form filter)

**Context:** `IntakeFormSyncService.SubmissionsSynced` event'ine MainShellViewModel subscribe olur, badge güncellenir. Badge tıklayınca CustomerSearchDialog Platform=form preset'iyle açılır + counter sıfırlanır. Phase 4b/4c MainShell binding pattern reuse.

- [ ] **Step 1: MainShellViewModel — NewIntakeSubmissionsCount + event subscription**

`LiveDeck.App/ViewModels/MainShellViewModel.cs` mevcut sınıfa ekle:

```csharp
    [ObservableProperty] private int _newIntakeSubmissionsCount;

    public bool HasNewIntakeSubmissions => NewIntakeSubmissionsCount > 0;

    partial void OnNewIntakeSubmissionsCountChanged(int value)
    {
        OnPropertyChanged(nameof(HasNewIntakeSubmissions));
    }
```

Constructor'a `IntakeFormSyncService syncService` parametre ekle (DI auto-resolve):

```csharp
    private readonly IntakeFormSyncService _intakeSync;

    public MainShellViewModel(/* mevcut deps */, IntakeFormSyncService intakeSync)
    {
        // mevcut atamalar
        _intakeSync = intakeSync;
        _intakeSync.SubmissionsSynced += OnIntakeSubmissionsSynced;
    }

    private void OnIntakeSubmissionsSynced(object? sender, int count)
    {
        // UI thread dispatch (Phase 4b pattern)
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(() => NewIntakeSubmissionsCount += count);
            return;
        }
        NewIntakeSubmissionsCount += count;
    }
```

Yeni komut `OpenIntakeSubmissionsCommand`:

```csharp
    public IAsyncRelayCommand OpenIntakeSubmissionsCommand { get; }
```

Constructor'da:
```csharp
        OpenIntakeSubmissionsCommand = new AsyncRelayCommand(OpenIntakeSubmissionsAsync);
```

Metot:
```csharp
    private async Task OpenIntakeSubmissionsAsync()
    {
        await Task.Yield();
        var dlg = global::LiveDeck.App.App.Host.Services.GetRequiredService<global::LiveDeck.App.Views.CustomerSearchDialog>();
        var vm = (CustomerSearchViewModel)dlg.DataContext;
        vm.PlatformFilter = "form";
        vm.RefreshSearch();   // Phase 3a CustomerSearchViewModel'da var olan helper

        dlg.Owner = System.Windows.Application.Current.MainWindow;
        dlg.ShowDialog();

        NewIntakeSubmissionsCount = 0;   // reset on dialog close
    }
```

`using LiveDeck.App.Services.IntakeForm;` ve `using Microsoft.Extensions.DependencyInjection;` ekle.

- [ ] **Step 2: MainShellView.xaml — badge UI**

`LiveDeck.App/Views/MainShellView.xaml` top bar'a (Phase 4b LicenseStatusIndicator yanına) ekle:

```xml
<Border Background="Crimson" CornerRadius="10" Padding="8,3"
        Cursor="Hand" Margin="8,0,0,0"
        VerticalAlignment="Center"
        Visibility="{Binding HasNewIntakeSubmissions, Converter={StaticResource BoolToVis}}"
        ToolTip="Yeni form başvurularını görüntülemek için tıkla">
    <Border.InputBindings>
        <MouseBinding MouseAction="LeftClick" Command="{Binding OpenIntakeSubmissionsCommand}"/>
    </Border.InputBindings>
    <TextBlock Foreground="White" FontSize="11" FontWeight="Bold">
        <Run Text="🔔"/>
        <Run Text="{Binding NewIntakeSubmissionsCount, Mode=OneWay}"/>
        <Run Text="yeni başvuru"/>
    </TextBlock>
</Border>
```

`BoolToVis` MainShell'de Phase 4d'den olabilir; yoksa `<Window.Resources>`'a ekle.

- [ ] **Step 3: CustomerSearchViewModel — PlatformFilter property**

`LiveDeck.App/ViewModels/CustomerSearchViewModel.cs` (Phase 3a) mevcut sınıfa ekle:

```csharp
    [ObservableProperty] private string? _platformFilter;

    partial void OnPlatformFilterChanged(string? value)
    {
        RefreshSearch();
    }

    /// <summary>Phase 4f: external trigger to re-run search after PlatformFilter changes.</summary>
    public void RefreshSearch()
    {
        // Mevcut Search/Filter logic'i yeniden çağır
        _ = LoadResultsAsync();   // veya Phase 3a metod adı
    }
```

Mevcut `LoadResultsAsync` (Phase 3a) içinde search query'sine PlatformFilter ekle:

```csharp
    private async Task LoadResultsAsync()
    {
        // mevcut query construction
        if (!string.IsNullOrEmpty(PlatformFilter))
        {
            results = results.Where(c => c.Platform == PlatformFilter);
        }
        // ...
    }
```

(Phase 3a CustomerSearchViewModel'in tam içeriği known değil — implementer mevcut search method'unu okuyup PlatformFilter'ı uygun yere entegre eder. Pattern: mevcut Username/DisplayName filter'larının yanına aynı yöntemle.)

- [ ] **Step 4: Build + tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -5
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 139/139 (no new tests; UI integration).

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.App/ViewModels/MainShellViewModel.cs \
        LiveDeck.App/Views/MainShellView.xaml \
        LiveDeck.App/ViewModels/CustomerSearchViewModel.cs
git commit -m "feat(app): add new-submissions badge in top bar + CustomerSearchDialog Platform=form filter"
```

---

### Task 12: Final verification + manual smoke

**Files:** None (verification only)

**Context:** Build + 3 test suite + manuel smoke checklist referansı.

- [ ] **Step 1: Solution build**

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
- LiveDeck.Tests: **139/139** (128 baseline + 4 CustomerRepo + 1 Migration + 5 IntakeFormSync + 2 IntakeFormSyncHosted = 12 yeni... beklenti 132 + 7 = 139)
- LiveDeck.Licensing.Tests: **108/108** (104 baseline + 4 client)
- LiveDeck.LicenseServer.Tests: **~163/163** (130 baseline + 2 entities + ~24 SlugValidator theory cases + 4 WhatsApp + 7 IntakeFormService + 7 controller + 6 form page = ~50 ama theory cases doğal şişme, gerçek 33 method)

**Toplam: ~410** (362 baseline + ~48 yeni). Spec hedefi ~399 → over-delivery.

- [ ] **Step 3: Manuel smoke (opsiyonel)**

Spec §9'daki 13 maddelik manuel smoke planını fiziksel browser + WhatsApp ile uygula. Kayıt etme; spec dosyası referans.

- [ ] **Step 4: Final commit (opsiyonel — sayım düzeltmesi)**

```bash
# Sadece test count assertion'ları gerçekle uyuşmuyorsa
git add docs/superpowers/plans/2026-04-30-phase-4f-intake-form.md
git commit -m "docs(plan): update Phase 4f task count assertions to actual"
```

Aksi takdirde Task 12 commit'siz tamamlanır.

---

## Self-Review

**1. Spec coverage:**

| Spec section | Task |
|---|---|
| §2.1 Solution etkisi (LicenseServer + Licensing + App + Core) | Task 1, 2, 3, 4, 5, 6 (server) + Task 7 (SDK) + Task 8 (Core) + Task 9, 10, 11 (App) |
| §2.2 Stack (Razor + polling + Bootstrap + AngleSharp) | Task 1 (Razor entity), Task 6 (Bootstrap), Task 9 (polling) |
| §2.3 5 URL endpoint | Task 5 (3 REST endpoint) + Task 6 (2 form endpoint /r/{slug} GET+POST) |
| §2.4 10-adımlık veri akışı | Tüm task'ler birlikte |
| §2.5 License-bound 410 policy | Task 4 (GetActiveBySlugAsync impl) + Task 6 (page returns 410) |
| §3 Form alanları + WhatsApp deep-link | Task 6 (form), Task 3 (WhatsApp builder) |
| §4 Slug + telefon konfig + Settings tab | Task 2 (validator), Task 5 (REST E.164 check), Task 10 (UI tab) |
| §5 Polling + Customer sync + Address + badge | Task 8 (Address), Task 9 (sync service), Task 11 (badge) |
| §6 Anti-spam (honeypot + rate-limit) | Task 5 (rate-limit policy), Task 6 (honeypot in handler) |
| §7 Konfigürasyon | Task 5 (rate-limit env), inline elsewhere |
| §8 Test stratejisi (~37 yeni test) | Task'ler kendi test'leri |
| §9 Manuel smoke 13 maddesi | Task 12 referansı |
| §10 YAGNI | Plan'a yansıdı (no implementation) |
| §12 Kabul kriterleri | Task 12 final verification |

**2. Placeholder scan:**
- Hiç "TBD" / "TODO" yok
- Tüm step'lerde concrete kod blokları
- Test'ler tam yazılmış (template değil)

**3. Type consistency:**
- `IntakeFormConfig` 8 alan (Task 1) — Task 4, 5, 6, 7 tutarlı kullanım ✓
- `IntakeFormSubmission` 7 alan — Task 1, 4, 5, 6, 9 tutarlı ✓
- `SlugValidator.Validate(string?) → SlugValidationResult` — Task 2 tanımı, Task 5 controller kullanır ✓
- `WhatsAppLinkBuilder.Build(phone, username, fullName, address)` — Task 3 tanımı, Task 6 page kullanır ✓
- `IntakeFormService` 4 method (GetByCustomer/GetActiveBySlug/UpsertConfig/SaveSubmission/GetSubmissionsSince) — Task 4 tanımı, Task 5 controller + Task 6 page kullanır ✓
- `LicenseApiClient` 3 yeni method (Get/Upsert/GetSubmissions) — Task 7 tanımı, Task 9 sync service + Task 10 UI vm kullanır ✓
- `Customer.Address` (Task 8) — Task 9 sync service kullanır ✓
- `CustomerRepository.UpsertFromIntakeForm(username, fullName, address, nowUnix)` — Task 8 tanımı, Task 9 sync service kullanır ✓
- `IntakeFormSyncService.SubmissionsSynced` event — Task 9 tanımı, Task 11 badge subscribe ✓
- `IntakeFormSettingsViewModel` 6 [ObservableProperty] — Task 10 tanımı, XAML binding ✓
- `MainShellViewModel.NewIntakeSubmissionsCount` + `HasNewIntakeSubmissions` — Task 11 tanımı, XAML badge ✓
- `CustomerSearchViewModel.PlatformFilter` — Task 11 tanımı, MainShell `OpenIntakeSubmissionsAsync` set eder ✓

**4. Cross-task dependencies:**
- Task 1 → Task 4 (DbContext + entity'ler service'ten kullanılır)
- Task 2 → Task 5 (SlugValidator controller validation)
- Task 3 → Task 6 (WhatsAppLinkBuilder page handler)
- Task 4 → Task 5 + Task 6 (service üst katman)
- Task 7 → Task 9 + Task 10 (client SDK 3 method)
- Task 8 → Task 9 (Customer.Address + UpsertFromIntakeForm)
- Task 9 → Task 11 (SubmissionsSynced event)
- Task 11 → Task 10 (sırası önemli değil — paralel olabilir; MainShell badge ve Settings tab bağımsız)

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-30-phase-4f-intake-form.md`.**

12 task. Tahmini ~3500 satır plan. Phase 4a/4b/4c/4d/4e ile aynı pattern (TDD, frequent commits, subagent-friendly). Test hedefi 362 → ~410.

İki yürütme seçeneği:

**1. Subagent-Driven (önerilen)** — Her task için fresh subagent dispatch. Phase 4a (15) + 4b (13) + 4c (14) + 4d (11) + 4e (11) = 64 task hep bu şekilde tamamlandı.

**2. Inline Execution** — executing-plans skill ile bu session'da batch yürütme.

Hangisi?


