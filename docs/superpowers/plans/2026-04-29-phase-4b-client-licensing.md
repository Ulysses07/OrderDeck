# Faz 4b — Client Licensing Modülü Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `LiveDeck.Licensing` adlı yeni client modülü ekle — DPAPI ile şifrelenmiş local auth/license storage, hardware fingerprint üretimi, login/register dialog, Phase 4a license server'ı ile entegrasyon, lisans expired/missing → soft-gate read-only mode.

**Architecture:** Yeni 2 proje (`LiveDeck.Licensing` Windows-only class library + `LiveDeck.Licensing.Tests`). LiveDeck.App'e DI integration + 2 yeni dialog (Login, Account) + MainShell soft-gate binding. Phase 4a server'a 1 endpoint patch (`GET /me/licenses`). LiveDeck.Core dokunulmaz; LiveDeck.Licensing izole.

**Tech Stack:** .NET 10 / WPF / `System.Security.Cryptography.ProtectedData` (DPAPI) / `System.Management` (WMI) / `Microsoft.Win32` (Registry) / `HttpClient` + `IHttpClientFactory` / `System.Text.Json` / `BackgroundService` / xUnit + FluentAssertions.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-4b state:** Phase 4a `f14f0ae` (master). 117 LiveDeck.Tests + 61 LiveDeck.LicenseServer.Tests = 178 baseline. Solution build 0 error / 0 warning.

**Spec reference:** `docs/superpowers/specs/2026-04-29-phase-4b-client-licensing-design.md`

---

## Task Index

**Server uzantısı (1):** `GET /me/licenses` endpoint — Phase 4a patch
**Foundation (2):** Yeni proje scaffolding + LicensingOptions + LicenseStatus enum
**Building blocks TDD (3-9):** HardwareIdProvider, EncryptedStore, AuthStore + LicenseStateStore, API DTOs + exceptions, LicenseApiClient, LoginService, LicenseService state machine, HeartbeatHostedService
**Integration (10-13):** AppHost DI, LoginDialog, AccountDialog + MainShell soft-gate, App.xaml.cs startup wiring

**Konfigürasyon yaklaşımı:** Spec'te `appsettings.json` bahsediyordu, ama mevcut codebase'de yok. Plan basit yaklaşım kullanır: AppHost.cs'de hardcoded default `LicensingOptions`, environment variable override (`LIVEDECK_LICENSE_BASE_URL`). appsettings.json/IConfiguration paradigması ileride gerekirse eklenir.

**Test framework deviation:** Spec'te `WireMock.Net` önerildi. Plan daha hafif `FakeHttpMessageHandler` (custom DelegatingHandler) kullanır — yeni dep yok, 30 satır kod, eşdeğer functionality.

---

### Task 1: Server-side patch — GET /me/licenses endpoint

**Files:**
- Modify: `LiveDeck.LicenseServer/Controllers/Auth/MeController.cs`
- Modify: `LiveDeck.LicenseServer.Tests/Auth/MeTests.cs`

**Context:** Phase 4a'da customer kendi lisanslarını listeleme endpoint'i yok. Phase 4b'nin Login akışı için gerekli (auto-activate kararı bu listeden geliyor). MeController'a yeni metot eklenir; LicenseDbContext + Bearer-Customer auth zaten var. TDD: önce başarısız test, sonra impl.

- [ ] **Step 1: Failing test ekle**

`LiveDeck.LicenseServer.Tests/Auth/MeTests.cs` dosyasını aç. Sınıfın **sonuna**, mevcut son test'in altına yeni test ekle. Önce gerekli using'leri kontrol et, eksikse ekle:

```csharp
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;
```

Sonra mevcut son `[Fact]` metodunun altına ve `private sealed record MeBody(...)` satırından önce şu test'i ekle:

```csharp
    [Fact]
    public async Task Get_my_licenses_returns_only_active_licenses()
    {
        var (client, email) = await CreateLoggedInClientAsync();

        // Seed: aynı customer'a 1 aktif + 1 revoke + 1 expired lisans
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var customer = await db.Customers.FirstAsync(c => c.Email == email);

            db.Licenses.AddRange(
                new License
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "LDK-ACTIVE-" + Guid.NewGuid().ToString("N"),
                    CustomerId = customer.Id,
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow,
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
                },
                new License
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "LDK-REVOKED-" + Guid.NewGuid().ToString("N"),
                    CustomerId = customer.Id,
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow.AddDays(-100),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
                    RevokedAt = DateTimeOffset.UtcNow.AddDays(-1),
                    RevokeReason = "test"
                },
                new License
                {
                    Id = Guid.NewGuid(),
                    LicenseKey = "LDK-EXPIRED-" + Guid.NewGuid().ToString("N"),
                    CustomerId = customer.Id,
                    SkuCode = "STD",
                    ActivationSlots = 1,
                    IssuedAt = DateTimeOffset.UtcNow.AddDays(-400),
                    ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
                });
            await db.SaveChangesAsync();
        }

        var resp = await client.GetAsync("/api/v1/me/licenses");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<List<LicenseSummaryBody>>();
        body.Should().NotBeNull();
        body!.Should().HaveCount(1);
        body[0].licenseKey.Should().StartWith("LDK-ACTIVE-");
        body[0].skuCode.Should().Be("STD");
    }

    private sealed record LicenseSummaryBody(string licenseKey, string skuCode, DateTimeOffset expiresAt, DateTimeOffset? revokedAt);
```

- [ ] **Step 2: RED — testi çalıştır, başarısız olduğunu doğrula**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~Get_my_licenses_returns_only_active_licenses" 2>&1 | tail -10
```

Beklenen: 404 NotFound döner (endpoint yok henüz). Test FAIL.

- [ ] **Step 3: MeController'a endpoint ekle**

`LiveDeck.LicenseServer/Controllers/Auth/MeController.cs`'i aç. Constructor'da `LicenseDbContext` injection zaten yok — eklenecek. Üst tarafa using ekle:

```csharp
using LiveDeck.LicenseServer.Data;
using Microsoft.EntityFrameworkCore;
```

Constructor'ı güncelle (varolan signature'ı bul, sadece `LicenseDbContext` parametresini ekle ve atayı yap):

```csharp
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public MeController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }
```

Mevcut `Get` metodunda `_db.Customers.FirstOrDefaultAsync(...)` zaten kullanılıyor olmalı; yoksa onu `_db` üzerinden değiştir. ChangePassword da aynı şekilde `_db` kullanır.

Sonra sınıfın sonundaki `private Guid GetCustomerId()` metodundan önce yeni endpoint ekle:

```csharp
    [HttpGet("licenses")]
    public async Task<IActionResult> GetMyLicenses(CancellationToken ct)
    {
        var id = GetCustomerId();
        var now = DateTimeOffset.UtcNow;
        var rows = await _db.Licenses
            .Where(l => l.CustomerId == id && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderByDescending(l => l.IssuedAt)
            .Select(l => new
            {
                licenseKey = l.LicenseKey,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = (DateTimeOffset?)null
            })
            .ToListAsync(ct);
        return Ok(rows);
    }
```

- [ ] **Step 4: GREEN — testi çalıştır, geçtiğini doğrula**

```bash
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~Get_my_licenses_returns_only_active_licenses" 2>&1 | tail -5
```

Beklenen: 1/1 PASS.

- [ ] **Step 5: Tüm test paketini çalıştır (regression)**

```bash
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 62/62 (61 baseline + 1 yeni).

- [ ] **Step 6: Build temiz mi?**

```bash
dotnet build LiveDeck.LicenseServer 2>&1 | tail -5
```

Beklenen: 0 errors, 0 warnings.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.LicenseServer/Controllers/Auth/MeController.cs LiveDeck.LicenseServer.Tests/Auth/MeTests.cs
git commit -m "feat(license-server): add GET /api/v1/me/licenses (active only)"
```

---

### Task 2: Solution scaffolding — LiveDeck.Licensing + LiveDeck.Licensing.Tests projects

**Files:**
- Create: `LiveDeck.Licensing/LiveDeck.Licensing.csproj`
- Create: `LiveDeck.Licensing/LicensingOptions.cs`
- Create: `LiveDeck.Licensing/LicenseStatus.cs`
- Create: `LiveDeck.Licensing.Tests/LiveDeck.Licensing.Tests.csproj`
- Create: `LiveDeck.Licensing.Tests/SmokeTests.cs`
- Modify: `LiveDeck.sln`

**Context:** İki yeni proje. Class library, Windows-only (DPAPI + WMI). LicensingOptions ve LicenseStatus shell sınıfları — sonraki task'lerde kullanılacak. SmokeTests yalnızca proje referansının çalıştığını doğrulayan minimal placeholder; sonraki task'lerde gerçek testlerle dolar.

- [ ] **Step 1: Class library projesini oluştur**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet new classlib -n LiveDeck.Licensing -f net10.0-windows
```

Default olarak `Class1.cs` dosyası oluşur — sileceğiz:

```bash
rm -f LiveDeck.Licensing/Class1.cs
```

- [ ] **Step 2: csproj içeriğini değiştir**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/LiveDeck.Licensing.csproj` dosyasını **tamamen** şununla değiştir:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Microsoft.Extensions.Hosting.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Logging.Abstractions" Version="9.0.0" />
    <PackageReference Include="Microsoft.Extensions.Options" Version="9.0.0" />
    <PackageReference Include="System.Management" Version="9.0.0" />
  </ItemGroup>
</Project>
```

`System.Management` WMI çağrıları için (Win32_Processor). `Microsoft.Win32.Registry` net10.0-windows'ta zaten dahil. `System.Security.Cryptography.ProtectedData` BCL'de mevcut.

- [ ] **Step 3: LicensingOptions oluştur**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/LicensingOptions.cs` oluştur:

```csharp
namespace LiveDeck.Licensing;

public sealed class LicensingOptions
{
    public string ServerBaseUrl { get; set; } = "https://license.livedeck.app";
    public int RequestTimeoutSeconds { get; set; } = 10;
    public int OfflineGraceDays { get; set; } = 14;
    public int HeartbeatIntervalHours { get; set; } = 24;
}
```

- [ ] **Step 4: LicenseStatus enum oluştur**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/LicenseStatus.cs` oluştur:

```csharp
namespace LiveDeck.Licensing;

/// <summary>
/// Client-side license state. Active and OfflineGrace allow writing; everything else is soft-gated.
/// </summary>
public enum LicenseStatus
{
    /// <summary>App is starting up; status not yet determined.</summary>
    Initializing,
    /// <summary>License is valid and verified online.</summary>
    Active,
    /// <summary>Server unreachable, but within offline grace window.</summary>
    OfflineGrace,
    /// <summary>Server unreachable and grace window exceeded — soft gate.</summary>
    OfflineExpired,
    /// <summary>Server reports license expired.</summary>
    ExpiredOnline,
    /// <summary>Server reports license revoked.</summary>
    Revoked,
    /// <summary>No license / no auth token / not activated on this device.</summary>
    NoLicense
}

public static class LicenseStatusExtensions
{
    /// <summary>True only when the app is allowed to perform write actions (print, create, etc.).</summary>
    public static bool IsWritable(this LicenseStatus status) =>
        status is LicenseStatus.Active or LicenseStatus.OfflineGrace;
}
```

- [ ] **Step 5: Test projesini oluştur**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet new xunit -n LiveDeck.Licensing.Tests -f net10.0-windows
```

Default `UnitTest1.cs` sil:

```bash
rm -f LiveDeck.Licensing.Tests/UnitTest1.cs
```

- [ ] **Step 6: Test csproj içeriğini değiştir**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/LiveDeck.Licensing.Tests.csproj` dosyasını **tamamen** şununla değiştir:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentAssertions" Version="7.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\LiveDeck.Licensing\LiveDeck.Licensing.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 7: SmokeTests yaz**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/SmokeTests.cs` oluştur:

```csharp
using FluentAssertions;
using Xunit;

namespace LiveDeck.Licensing.Tests;

public class SmokeTests
{
    [Fact]
    public void LicensingOptions_default_values_are_sane()
    {
        var opt = new LicensingOptions();
        opt.ServerBaseUrl.Should().StartWith("https://");
        opt.RequestTimeoutSeconds.Should().BeGreaterThan(0);
        opt.OfflineGraceDays.Should().Be(14);
        opt.HeartbeatIntervalHours.Should().Be(24);
    }

    [Fact]
    public void LicenseStatus_active_and_offline_grace_are_writable()
    {
        LicenseStatus.Active.IsWritable().Should().BeTrue();
        LicenseStatus.OfflineGrace.IsWritable().Should().BeTrue();
    }

    [Fact]
    public void LicenseStatus_other_states_are_not_writable()
    {
        LicenseStatus.Initializing.IsWritable().Should().BeFalse();
        LicenseStatus.OfflineExpired.IsWritable().Should().BeFalse();
        LicenseStatus.ExpiredOnline.IsWritable().Should().BeFalse();
        LicenseStatus.Revoked.IsWritable().Should().BeFalse();
        LicenseStatus.NoLicense.IsWritable().Should().BeFalse();
    }
}
```

- [ ] **Step 8: Solution'a ekle**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet sln add LiveDeck.Licensing/LiveDeck.Licensing.csproj
dotnet sln add LiveDeck.Licensing.Tests/LiveDeck.Licensing.Tests.csproj
```

- [ ] **Step 9: Build + test**

```bash
dotnet build LiveDeck.sln 2>&1 | tail -5
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 0 errors, 0 warnings. 3/3 SmokeTests pass.

Regression check:

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 117/117 + 62/62.

- [ ] **Step 10: Commit**

```bash
git add LiveDeck.sln LiveDeck.Licensing/ LiveDeck.Licensing.Tests/
git commit -m "feat(licensing): scaffold LiveDeck.Licensing project + LicensingOptions + LicenseStatus"
```

---

### Task 3: HardwareIdProvider (TDD)

**Files:**
- Create: `LiveDeck.Licensing/IHardwareIdProvider.cs`
- Create: `LiveDeck.Licensing/HardwareIdProvider.cs`
- Create: `LiveDeck.Licensing.Tests/HardwareIdProviderTests.cs`

**Context:** SHA-256(MachineGuid + CPU.ProcessorId + Username). Registry/WMI bağımlılıkları test'te zor — interface ile soyutlayıp prod impl'i WMI/Registry kullanır, test'te hash logic'ini ayrı doğrularız (`ComputeHash` helper static method olarak expose edilir).

- [ ] **Step 1: Interface yaz**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/IHardwareIdProvider.cs` oluştur:

```csharp
namespace LiveDeck.Licensing;

public interface IHardwareIdProvider
{
    /// <summary>SHA-256 hex (lowercase, 64 chars) deterministically derived from machine + user identity.</summary>
    string GetHardwareId();
}
```

- [ ] **Step 2: Failing test (hash logic)**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/HardwareIdProviderTests.cs` oluştur:

```csharp
using FluentAssertions;
using Xunit;

namespace LiveDeck.Licensing.Tests;

public class HardwareIdProviderTests
{
    [Fact]
    public void ComputeHash_is_deterministic_for_same_inputs()
    {
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "alice");
        var b = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "alice");
        a.Should().Be(b);
    }

    [Fact]
    public void ComputeHash_differs_for_different_inputs()
    {
        var a = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "alice");
        var b = HardwareIdProvider.ComputeHash("guid-2", "cpu-A", "alice");
        var c = HardwareIdProvider.ComputeHash("guid-1", "cpu-B", "alice");
        var d = HardwareIdProvider.ComputeHash("guid-1", "cpu-A", "bob");

        a.Should().NotBe(b);
        a.Should().NotBe(c);
        a.Should().NotBe(d);
    }

    [Fact]
    public void ComputeHash_returns_64_char_lowercase_hex()
    {
        var hash = HardwareIdProvider.ComputeHash("g", "c", "u");
        hash.Should().HaveLength(64);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    [Fact]
    public void ComputeHash_username_is_case_insensitive()
    {
        var lower = HardwareIdProvider.ComputeHash("g", "c", "alice");
        var upper = HardwareIdProvider.ComputeHash("g", "c", "ALICE");
        var mixed = HardwareIdProvider.ComputeHash("g", "c", "Alice");
        lower.Should().Be(upper);
        lower.Should().Be(mixed);
    }

    [Fact]
    public void GetHardwareId_returns_non_empty_string_on_real_machine()
    {
        // Integration test — runs WMI/Registry; only validates that it doesn't throw.
        var provider = new HardwareIdProvider();
        var id = provider.GetHardwareId();
        id.Should().NotBeNullOrWhiteSpace();
        id.Should().HaveLength(64);
    }
}
```

- [ ] **Step 3: RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~HardwareIdProvider" 2>&1 | tail -5
```

Beklenen: derleme hatası — `HardwareIdProvider` tipi yok.

- [ ] **Step 4: HardwareIdProvider impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/HardwareIdProvider.cs` oluştur:

```csharp
using System.Management;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LiveDeck.Licensing;

public sealed class HardwareIdProvider : IHardwareIdProvider
{
    public string GetHardwareId()
    {
        var machineGuid = ReadMachineGuid();
        var cpuId = ReadCpuId();
        var username = Environment.UserName;
        return ComputeHash(machineGuid, cpuId, username);
    }

    /// <summary>Pure hash function — exposed for testing.</summary>
    public static string ComputeHash(string machineGuid, string cpuId, string username)
    {
        var raw = $"{machineGuid}|{cpuId}|{username.ToLowerInvariant()}";
        var bytes = Encoding.UTF8.GetBytes(raw);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string ReadMachineGuid()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
        var value = key?.GetValue("MachineGuid") as string;
        if (string.IsNullOrEmpty(value))
            throw new InvalidOperationException("Cannot read HKLM\\SOFTWARE\\Microsoft\\Cryptography\\MachineGuid.");
        return value;
    }

    private static string ReadCpuId()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessorId FROM Win32_Processor");
            foreach (var obj in searcher.Get())
            {
                var id = obj["ProcessorId"]?.ToString();
                if (!string.IsNullOrWhiteSpace(id)) return id;
            }
        }
        catch
        {
            // WMI failures fall through to fallback.
        }
        return "unknown-cpu";
    }
}
```

- [ ] **Step 5: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~HardwareIdProvider" 2>&1 | tail -5
```

Beklenen: 5/5 PASS.

- [ ] **Step 6: Build + tüm Licensing tests**

```bash
dotnet build LiveDeck.Licensing 2>&1 | tail -3
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 0 errors / 0 warnings. 8/8 (3 smoke + 5 hardware).

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.Licensing/IHardwareIdProvider.cs LiveDeck.Licensing/HardwareIdProvider.cs LiveDeck.Licensing.Tests/HardwareIdProviderTests.cs
git commit -m "feat(licensing): add HardwareIdProvider (SHA-256 of MachineGuid+CPU+Username)"
```

---

### Task 4: EncryptedStore (DPAPI wrapper)

**Files:**
- Create: `LiveDeck.Licensing/Storage/EncryptedStore.cs`
- Create: `LiveDeck.Licensing.Tests/Storage/EncryptedStoreTests.cs`

**Context:** DPAPI ile JSON serialize/deserialize wrapper. `Save<T>(path, value)` ve `TryLoad<T>(path)` API. Decrypt fail (tampered, başka kullanıcıdan kopya) → null döner ve dosya silinir (fresh-start). Generic, hangi DTO olursa olsun çalışır.

- [ ] **Step 1: Failing tests yaz**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Storage/EncryptedStoreTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Storage;
using Xunit;

namespace LiveDeck.Licensing.Tests.Storage;

public sealed class EncryptedStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly EncryptedStore _store;

    public EncryptedStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new EncryptedStore();
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private sealed record Sample(string Name, int Value);

    [Fact]
    public void Save_then_TryLoad_roundtrips_object()
    {
        var path = Path.Combine(_dir, "sample.dat");
        var original = new Sample("hello", 42);

        _store.Save(path, original);
        var loaded = _store.TryLoad<Sample>(path);

        loaded.Should().NotBeNull();
        loaded!.Name.Should().Be("hello");
        loaded.Value.Should().Be(42);
    }

    [Fact]
    public void TryLoad_returns_null_when_file_missing()
    {
        var path = Path.Combine(_dir, "missing.dat");
        var loaded = _store.TryLoad<Sample>(path);
        loaded.Should().BeNull();
    }

    [Fact]
    public void TryLoad_deletes_corrupted_file_and_returns_null()
    {
        var path = Path.Combine(_dir, "corrupt.dat");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x01, 0x02, 0x03, 0x04 });

        var loaded = _store.TryLoad<Sample>(path);

        loaded.Should().BeNull();
        File.Exists(path).Should().BeFalse();
    }

    [Fact]
    public void Save_creates_parent_directory_if_missing()
    {
        var nestedDir = Path.Combine(_dir, "nested", "deep");
        var path = Path.Combine(nestedDir, "sample.dat");

        _store.Save(path, new Sample("x", 1));

        File.Exists(path).Should().BeTrue();
    }

    [Fact]
    public void Saved_payload_is_not_plain_json()
    {
        var path = Path.Combine(_dir, "encrypted.dat");
        _store.Save(path, new Sample("secret-value", 999));

        var raw = File.ReadAllBytes(path);
        var asUtf8 = System.Text.Encoding.UTF8.GetString(raw);
        asUtf8.Should().NotContain("secret-value");
        asUtf8.Should().NotContain("999");
    }

    [Fact]
    public void Delete_removes_file_when_present_and_is_idempotent()
    {
        var path = Path.Combine(_dir, "to-delete.dat");
        _store.Save(path, new Sample("x", 1));
        File.Exists(path).Should().BeTrue();

        _store.Delete(path);
        File.Exists(path).Should().BeFalse();

        // Idempotent: second delete throws nothing
        _store.Delete(path);
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~EncryptedStore" 2>&1 | tail -5
```

Beklenen: derleme hatası — `EncryptedStore` yok.

- [ ] **Step 3: EncryptedStore impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Storage/EncryptedStore.cs` oluştur:

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LiveDeck.Licensing.Storage;

/// <summary>
/// JSON + DPAPI (current-user scope). Tampered or cross-user files are deleted
/// on load and surface as <c>null</c> — caller treats this as fresh state.
/// </summary>
public sealed class EncryptedStore
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public void Save<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var json = JsonSerializer.Serialize(value, JsonOpts);
        var plaintext = Encoding.UTF8.GetBytes(json);
        var cipher = ProtectedData.Protect(plaintext, optionalEntropy: null, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, cipher);
    }

    public T? TryLoad<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;

        try
        {
            var cipher = File.ReadAllBytes(path);
            var plaintext = ProtectedData.Unprotect(cipher, optionalEntropy: null, DataProtectionScope.CurrentUser);
            var json = Encoding.UTF8.GetString(plaintext);
            return JsonSerializer.Deserialize<T>(json, JsonOpts);
        }
        catch (CryptographicException)
        {
            // Tampered or written by a different DPAPI principal — start fresh.
            TryDelete(path);
            return null;
        }
        catch (JsonException)
        {
            // Decrypts but doesn't deserialize — schema drift, treat as fresh.
            TryDelete(path);
            return null;
        }
    }

    public void Delete(string path)
    {
        TryDelete(path);
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch { /* best effort */ }
    }
}
```

- [ ] **Step 4: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~EncryptedStore" 2>&1 | tail -5
```

Beklenen: 6/6 PASS.

- [ ] **Step 5: Tüm Licensing testleri çalıştır**

```bash
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 14/14 (3 smoke + 5 hardware + 6 encrypted store).

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.Licensing/Storage/EncryptedStore.cs LiveDeck.Licensing.Tests/Storage/EncryptedStoreTests.cs
git commit -m "feat(licensing): add EncryptedStore (DPAPI + JSON) with tamper detection"
```

---

### Task 5: AuthStore + LicenseStateStore (typed wrappers)

**Files:**
- Create: `LiveDeck.Licensing/Storage/AuthRecord.cs`
- Create: `LiveDeck.Licensing/Storage/AuthStore.cs`
- Create: `LiveDeck.Licensing/Storage/LicenseRecord.cs`
- Create: `LiveDeck.Licensing/Storage/LicenseStateStore.cs`
- Create: `LiveDeck.Licensing.Tests/Storage/AuthStoreTests.cs`
- Create: `LiveDeck.Licensing.Tests/Storage/LicenseStateStoreTests.cs`

**Context:** EncryptedStore üzerine typed wrapper'lar. `AuthStore` `auth.dat`'ı, `LicenseStateStore` `license.dat`'ı yönetir. Her ikisinde de `Load() / Save() / Clear() / IsPresent` API. Path constructor'a `IAuthStorePath`/`ILicenseStorePath` ile inject edilir mi yoksa direkt string mi? — string yeter, AppHost path'i hesaplar.

- [ ] **Step 1: AuthRecord ve LicenseRecord oluştur**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Storage/AuthRecord.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Storage;

public sealed record AuthRecord(
    Guid CustomerId,
    string Email,
    string Name,
    string Token,
    DateTimeOffset TokenExpiresAt);
```

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Storage/LicenseRecord.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Storage;

public sealed record LicenseRecord(
    string LicenseKey,
    string SkuCode,
    DateTimeOffset ExpiresAt,
    int RemainingDaysAtLastCheck,
    DateTimeOffset LastValidatedAt,
    DateTimeOffset LastSuccessfulOnlineAt,
    string LastKnownStatus);
```

- [ ] **Step 2: Failing tests yaz (Auth)**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Storage/AuthStoreTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Storage;
using Xunit;

namespace LiveDeck.Licensing.Tests.Storage;

public sealed class AuthStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly string _path;
    private readonly AuthStore _store;

    public AuthStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _path = Path.Combine(_dir, "auth.dat");
        _store = new AuthStore(new EncryptedStore(), _path);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void IsPresent_is_false_when_no_file()
    {
        _store.IsPresent.Should().BeFalse();
    }

    [Fact]
    public void Load_returns_null_when_no_file()
    {
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_roundtrips_record()
    {
        var record = new AuthRecord(
            CustomerId: Guid.NewGuid(),
            Email: "user@example.com",
            Name: "Test User",
            Token: "header.payload.signature",
            TokenExpiresAt: DateTimeOffset.UtcNow.AddDays(7));

        _store.Save(record);
        _store.IsPresent.Should().BeTrue();

        var loaded = _store.Load();
        loaded.Should().NotBeNull();
        loaded!.CustomerId.Should().Be(record.CustomerId);
        loaded.Email.Should().Be(record.Email);
        loaded.Name.Should().Be(record.Name);
        loaded.Token.Should().Be(record.Token);
        loaded.TokenExpiresAt.Should().BeCloseTo(record.TokenExpiresAt, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void Clear_removes_file()
    {
        _store.Save(new AuthRecord(Guid.NewGuid(), "a", "b", "t", DateTimeOffset.UtcNow));
        _store.IsPresent.Should().BeTrue();

        _store.Clear();

        _store.IsPresent.Should().BeFalse();
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Clear_is_idempotent_when_no_file()
    {
        _store.Clear();
        _store.IsPresent.Should().BeFalse();
    }
}
```

- [ ] **Step 3: Failing tests yaz (LicenseState)**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Storage/LicenseStateStoreTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Storage;
using Xunit;

namespace LiveDeck.Licensing.Tests.Storage;

public sealed class LicenseStateStoreTests : IDisposable
{
    private readonly string _dir;
    private readonly LicenseStateStore _store;

    public LicenseStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new LicenseStateStore(new EncryptedStore(), Path.Combine(_dir, "license.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Load_returns_null_when_no_file()
    {
        _store.Load().Should().BeNull();
    }

    [Fact]
    public void Save_then_Load_roundtrips_record()
    {
        var record = new LicenseRecord(
            LicenseKey: "LDK-XYZ",
            SkuCode: "STD",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(365),
            RemainingDaysAtLastCheck: 365,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: DateTimeOffset.UtcNow,
            LastKnownStatus: "Active");

        _store.Save(record);
        var loaded = _store.Load();

        loaded.Should().NotBeNull();
        loaded!.LicenseKey.Should().Be("LDK-XYZ");
        loaded.SkuCode.Should().Be("STD");
        loaded.RemainingDaysAtLastCheck.Should().Be(365);
        loaded.LastKnownStatus.Should().Be("Active");
    }

    [Fact]
    public void Clear_removes_file()
    {
        _store.Save(new LicenseRecord("LDK", "STD", DateTimeOffset.UtcNow, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "x"));
        _store.IsPresent.Should().BeTrue();

        _store.Clear();

        _store.IsPresent.Should().BeFalse();
    }
}
```

- [ ] **Step 4: RED**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~AuthStoreTests|FullyQualifiedName~LicenseStateStoreTests" 2>&1 | tail -5
```

Beklenen: derleme hatası — AuthStore / LicenseStateStore yok.

- [ ] **Step 5: AuthStore impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Storage/AuthStore.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Storage;

public sealed class AuthStore
{
    private readonly EncryptedStore _store;
    private readonly string _path;

    public AuthStore(EncryptedStore store, string path)
    {
        _store = store;
        _path = path;
    }

    public bool IsPresent => File.Exists(_path);

    public AuthRecord? Load() => _store.TryLoad<AuthRecord>(_path);

    public void Save(AuthRecord record) => _store.Save(_path, record);

    public void Clear() => _store.Delete(_path);
}
```

- [ ] **Step 6: LicenseStateStore impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Storage/LicenseStateStore.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Storage;

public sealed class LicenseStateStore
{
    private readonly EncryptedStore _store;
    private readonly string _path;

    public LicenseStateStore(EncryptedStore store, string path)
    {
        _store = store;
        _path = path;
    }

    public bool IsPresent => File.Exists(_path);

    public LicenseRecord? Load() => _store.TryLoad<LicenseRecord>(_path);

    public void Save(LicenseRecord record) => _store.Save(_path, record);

    public void Clear() => _store.Delete(_path);
}
```

- [ ] **Step 7: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 22/22 (3 smoke + 5 hardware + 6 encrypted + 5 auth + 3 license-state).

- [ ] **Step 8: Commit**

```bash
git add LiveDeck.Licensing/Storage/AuthRecord.cs LiveDeck.Licensing/Storage/AuthStore.cs LiveDeck.Licensing/Storage/LicenseRecord.cs LiveDeck.Licensing/Storage/LicenseStateStore.cs LiveDeck.Licensing.Tests/Storage/AuthStoreTests.cs LiveDeck.Licensing.Tests/Storage/LicenseStateStoreTests.cs
git commit -m "feat(licensing): add AuthStore + LicenseStateStore (typed wrappers over EncryptedStore)"
```

---

### Task 6: API DTOs + LicenseApiException + LicenseApiClient

**Files:**
- Create: `LiveDeck.Licensing/Api/Models/AuthDtos.cs`
- Create: `LiveDeck.Licensing/Api/Models/LicenseDtos.cs`
- Create: `LiveDeck.Licensing/Api/LicenseApiException.cs`
- Create: `LiveDeck.Licensing/Api/LicenseApiClient.cs`
- Create: `LiveDeck.Licensing.Tests/TestHelpers/FakeHttpMessageHandler.cs`
- Create: `LiveDeck.Licensing.Tests/Api/LicenseApiClientTests.cs`

**Context:** HTTP client + error mapping katmanı. Server'ın döndürdüğü RFC 7807 ProblemDetails (title alanı = error code) parse edilir, exception subtipine map'lenir. `FakeHttpMessageHandler` test infra — `Func<HttpRequestMessage, HttpResponseMessage>` alır, direkt çağrı.

- [ ] **Step 1: Auth DTOs**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Api/Models/AuthDtos.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Api.Models;

public sealed record RegisterRequest(string Email, string Name, string Password);
public sealed record ResendRequest(string Email);
public sealed record LoginRequest(string Email, string Password);
public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);
public sealed record MeResponse(Guid Id, string Email, string Name, DateTimeOffset? EmailConfirmedAt, DateTimeOffset CreatedAt);
public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);
public sealed record LicenseSummary(string LicenseKey, string SkuCode, DateTimeOffset ExpiresAt, DateTimeOffset? RevokedAt);
```

- [ ] **Step 2: License DTOs**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Api/Models/LicenseDtos.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Api.Models;

public sealed record SlotInfoDto(int Used, int Total, bool ThisDeviceActive);

public sealed record ValidateRequest(string LicenseKey, string HardwareFingerprint);
public sealed record ValidateResponse(string Status, DateTimeOffset? ExpiresAt, int? RemainingDays, string? Sku, SlotInfoDto? SlotInfo);

public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName);
public sealed record ActivateResponse(Guid ActivationId, DateTimeOffset? ExpiresAt);

public sealed record DeactivateRequest(string LicenseKey, string HardwareFingerprint);
public sealed record HeartbeatRequest(string LicenseKey, string HardwareFingerprint);
public sealed record HeartbeatResponse(string? Status, DateTimeOffset? ExpiresAt);
```

- [ ] **Step 3: LicenseApiException + concrete subtipler**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Api/LicenseApiException.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Api;

public abstract class LicenseApiException : Exception
{
    public string Code { get; }

    protected LicenseApiException(string code, string message) : base(message)
    {
        Code = code;
    }
}

public sealed class InvalidCredentialsException : LicenseApiException
{
    public InvalidCredentialsException(string message = "E-posta veya şifre yanlış")
        : base("invalid-credentials", message) { }
}

public sealed class EmailNotConfirmedException : LicenseApiException
{
    public EmailNotConfirmedException(string message = "E-posta adresinizi doğrulayın")
        : base("email-not-confirmed", message) { }
}

public sealed class LicenseRevokedException : LicenseApiException
{
    public LicenseRevokedException(string message = "Lisans iptal edilmiş")
        : base("license-revoked", message) { }
}

public sealed class LicenseExpiredException : LicenseApiException
{
    public LicenseExpiredException(string message = "Lisans süresi dolmuş")
        : base("license-expired", message) { }
}

public sealed class SlotFullException : LicenseApiException
{
    public SlotFullException(string message = "Tüm cihaz slotları dolu")
        : base("slot-full", message) { }
}

public sealed class ValidationException : LicenseApiException
{
    public ValidationException(string code, string message) : base(code, message) { }
}

public sealed class LicenseApiNetworkException : LicenseApiException
{
    public LicenseApiNetworkException(string message, Exception? inner = null)
        : base("network", message)
    {
        if (inner is not null) Data["inner"] = inner;
    }
}

public sealed class LicenseApiUnknownException : LicenseApiException
{
    public int StatusCode { get; }
    public LicenseApiUnknownException(int statusCode, string message)
        : base("unknown", message)
    {
        StatusCode = statusCode;
    }
}
```

- [ ] **Step 4: FakeHttpMessageHandler test helper**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/TestHelpers/FakeHttpMessageHandler.cs` oluştur:

```csharp
namespace LiveDeck.Licensing.Tests.TestHelpers;

/// <summary>
/// Minimal DelegatingHandler that lets tests script HTTP responses by request.
/// Each Send call invokes the responder; tests assert on captured requests.
/// </summary>
public sealed class FakeHttpMessageHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;
    public List<HttpRequestMessage> Requests { get; } = new();

    public FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : this(req => Task.FromResult(responder(req))) { }

    public FakeHttpMessageHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
    {
        _responder = responder;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request);
        return await _responder(request);
    }

    public static HttpResponseMessage Json(int statusCode, string json) =>
        new((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };

    public static HttpResponseMessage Empty(int statusCode) =>
        new((System.Net.HttpStatusCode)statusCode);

    public static HttpResponseMessage Problem(int statusCode, string title, string? detail = null)
    {
        var problem = $$"""{"title":"{{title}}","detail":"{{detail ?? ""}}","status":{{statusCode}}}""";
        return new HttpResponseMessage((System.Net.HttpStatusCode)statusCode)
        {
            Content = new StringContent(problem, System.Text.Encoding.UTF8, "application/problem+json")
        };
    }
}
```

- [ ] **Step 5: LicenseApiClientTests yaz**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Api/LicenseApiClientTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Licensing.Tests.Api;

public class LicenseApiClientTests
{
    private static (LicenseApiClient client, FakeHttpMessageHandler handler) BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder, string? token = null)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        if (token is not null)
            http.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return (new LicenseApiClient(http), handler);
    }

    [Fact]
    public async Task LoginAsync_returns_token_on_200()
    {
        var (client, handler) = BuildClient(_ =>
            FakeHttpMessageHandler.Json(200, """{"token":"abc","expiresAt":"2026-05-06T12:00:00Z"}"""));

        var resp = await client.LoginAsync(new LoginRequest("user@example.com", "pw"));

        resp.Token.Should().Be("abc");
        handler.Requests[0].Method.Method.Should().Be("POST");
        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/auth/login");
    }

    [Fact]
    public async Task LoginAsync_throws_InvalidCredentials_on_401()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(401, "invalid-credentials"));

        var act = async () => await client.LoginAsync(new LoginRequest("u", "p"));
        await act.Should().ThrowAsync<InvalidCredentialsException>();
    }

    [Fact]
    public async Task LoginAsync_throws_EmailNotConfirmed_on_403()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(403, "email-not-confirmed"));

        var act = async () => await client.LoginAsync(new LoginRequest("u", "p"));
        await act.Should().ThrowAsync<EmailNotConfirmedException>();
    }

    [Fact]
    public async Task RegisterAsync_treats_201_and_202_as_success()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(201));
        await client.RegisterAsync(new RegisterRequest("u@x", "n", "p")); // no throw

        var (client2, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(202));
        await client2.RegisterAsync(new RegisterRequest("u@x", "n", "p")); // no throw
    }

    [Fact]
    public async Task RegisterAsync_throws_Validation_on_400()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(400, "password-too-short", "En az 8 karakter olmalı"));

        var act = async () => await client.RegisterAsync(new RegisterRequest("u@x", "n", "p"));
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Code.Should().Be("password-too-short");
    }

    [Fact]
    public async Task ValidateAsync_returns_status()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}"""));

        var resp = await client.ValidateAsync(new ValidateRequest("LDK-X", "fp"));

        resp.Should().NotBeNull();
        resp!.Status.Should().Be("active");
        resp.RemainingDays.Should().Be(365);
        resp.SlotInfo!.ThisDeviceActive.Should().BeTrue();
    }

    [Fact]
    public async Task ValidateAsync_returns_null_on_404()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(404));

        var resp = await client.ValidateAsync(new ValidateRequest("LDK-X", "fp"));
        resp.Should().BeNull();
    }

    [Fact]
    public async Task ActivateAsync_throws_SlotFull_on_409_with_slot_full_title()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(409, "slot-full", "Slot dolu"));

        var act = async () => await client.ActivateAsync(new ActivateRequest("LDK-X", "fp", null));
        await act.Should().ThrowAsync<SlotFullException>();
    }

    [Fact]
    public async Task ActivateAsync_throws_LicenseRevoked_on_409_with_revoked_title()
    {
        var (client, _) = BuildClient(_ =>
            FakeHttpMessageHandler.Problem(409, "license-revoked"));

        var act = async () => await client.ActivateAsync(new ActivateRequest("LDK-X", "fp", null));
        await act.Should().ThrowAsync<LicenseRevokedException>();
    }

    [Fact]
    public async Task NetworkFailure_wraps_in_LicenseApiNetworkException()
    {
        var (client, _) = BuildClient(_ => throw new HttpRequestException("dns fail"));

        var act = async () => await client.LoginAsync(new LoginRequest("u", "p"));
        await act.Should().ThrowAsync<LicenseApiNetworkException>();
    }

    [Fact]
    public async Task GetMyLicensesAsync_returns_list()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """[{"licenseKey":"LDK-A","skuCode":"STD","expiresAt":"2027-01-01T00:00:00Z","revokedAt":null}]"""));

        var resp = await client.GetMyLicensesAsync();
        resp.Should().HaveCount(1);
        resp[0].LicenseKey.Should().Be("LDK-A");
    }

    [Fact]
    public async Task DeactivateAsync_treats_204_as_success()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Empty(204));
        await client.DeactivateAsync(new DeactivateRequest("LDK-X", "fp")); // no throw
    }

    [Fact]
    public async Task HeartbeatAsync_returns_response_on_200()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-01-01T00:00:00Z"}"""));

        var resp = await client.HeartbeatAsync(new HeartbeatRequest("LDK-X", "fp"));
        resp.Status.Should().Be("active");
    }

    [Fact]
    public async Task HeartbeatAsync_throws_when_404_not_activated()
    {
        var (client, _) = BuildClient(_ => FakeHttpMessageHandler.Problem(404, "not-activated"));

        var act = async () => await client.HeartbeatAsync(new HeartbeatRequest("LDK-X", "fp"));
        var ex = await act.Should().ThrowAsync<ValidationException>();
        ex.Which.Code.Should().Be("not-activated");
    }

    [Fact]
    public async Task SetAuthToken_attaches_bearer_to_subsequent_requests()
    {
        var (client, handler) = BuildClient(_ => FakeHttpMessageHandler.Json(200,
            """{"id":"00000000-0000-0000-0000-000000000000","email":"u","name":"n","emailConfirmedAt":null,"createdAt":"2026-01-01T00:00:00Z"}"""));

        client.SetAuthToken("test-token");
        await client.GetMeAsync();

        handler.Requests[0].Headers.Authorization!.Scheme.Should().Be("Bearer");
        handler.Requests[0].Headers.Authorization.Parameter.Should().Be("test-token");
    }
}
```

- [ ] **Step 6: RED — derleme hatası**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LicenseApiClientTests" 2>&1 | tail -3
```

Beklenen: `LicenseApiClient` yok hatası.

- [ ] **Step 7: LicenseApiClient impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Api/LicenseApiClient.cs` oluştur:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using LiveDeck.Licensing.Api.Models;

namespace LiveDeck.Licensing.Api;

public sealed class LicenseApiClient
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient _http;

    public LicenseApiClient(HttpClient http) => _http = http;

    public void SetAuthToken(string? token)
    {
        _http.DefaultRequestHeaders.Authorization = string.IsNullOrEmpty(token)
            ? null
            : new AuthenticationHeaderValue("Bearer", token);
    }

    // ─── Auth (anonymous) ─────────────────────────────────────────────

    public Task<LoginResponse> LoginAsync(LoginRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<LoginRequest, LoginResponse>("/api/v1/auth/login", req, ct);

    public async Task RegisterAsync(RegisterRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/auth/register", req, ct);
        if ((int)resp.StatusCode is 201 or 202) return;
        await ThrowMappedAsync(resp);
    }

    public async Task ResendConfirmationAsync(ResendRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/auth/resend-confirmation", req, ct);
        if ((int)resp.StatusCode is 202 or 200) return;
        await ThrowMappedAsync(resp);
    }

    // ─── Me (Bearer-Customer) ─────────────────────────────────────────

    public Task<MeResponse> GetMeAsync(CancellationToken ct = default)
        => GetExpectingJsonAsync<MeResponse>("/api/v1/me", ct);

    public async Task ChangePasswordAsync(ChangePasswordRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/me/password", req, ct);
        if ((int)resp.StatusCode == 204) return;
        await ThrowMappedAsync(resp);
    }

    public Task<List<LicenseSummary>> GetMyLicensesAsync(CancellationToken ct = default)
        => GetExpectingJsonAsync<List<LicenseSummary>>("/api/v1/me/licenses", ct);

    // ─── Licenses (Bearer-Customer) ───────────────────────────────────

    /// <summary>Returns null when license/customer not found (404). All other errors throw.</summary>
    public async Task<ValidateResponse?> ValidateAsync(ValidateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/licenses/validate", req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return null;
        if (resp.IsSuccessStatusCode)
            return await DeserializeAsync<ValidateResponse>(resp, ct);
        await ThrowMappedAsync(resp);
        return null; // unreachable
    }

    public Task<ActivateResponse> ActivateAsync(ActivateRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<ActivateRequest, ActivateResponse>("/api/v1/licenses/activate", req, ct, successCodes: new[] { 201, 200 });

    public async Task DeactivateAsync(DeactivateRequest req, CancellationToken ct = default)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, "/api/v1/licenses/deactivate", req, ct);
        if ((int)resp.StatusCode is 204 or 200 or 404) return;
        await ThrowMappedAsync(resp);
    }

    public Task<HeartbeatResponse> HeartbeatAsync(HeartbeatRequest req, CancellationToken ct = default)
        => PostJsonExpectingJsonAsync<HeartbeatRequest, HeartbeatResponse>("/api/v1/licenses/heartbeat", req, ct);

    // ─── HTTP helpers ────────────────────────────────────────────────

    private async Task<TResp> PostJsonExpectingJsonAsync<TReq, TResp>(
        string path, TReq body, CancellationToken ct, int[]? successCodes = null)
    {
        using var resp = await SendJsonAsync(HttpMethod.Post, path, body, ct);
        var ok = successCodes is null
            ? resp.IsSuccessStatusCode
            : Array.IndexOf(successCodes, (int)resp.StatusCode) >= 0;
        if (!ok) await ThrowMappedAsync(resp);
        return (await DeserializeAsync<TResp>(resp, ct))!;
    }

    private async Task<TResp> GetExpectingJsonAsync<TResp>(string path, CancellationToken ct)
    {
        HttpResponseMessage resp;
        try { resp = await _http.GetAsync(path, ct); }
        catch (HttpRequestException ex) { throw new LicenseApiNetworkException(ex.Message, ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new LicenseApiNetworkException("timeout", ex); }

        using (resp)
        {
            if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
            return (await DeserializeAsync<TResp>(resp, ct))!;
        }
    }

    private async Task<HttpResponseMessage> SendJsonAsync<TReq>(
        HttpMethod method, string path, TReq body, CancellationToken ct)
    {
        var req = new HttpRequestMessage(method, path)
        {
            Content = JsonContent.Create(body, options: JsonOpts)
        };
        try
        {
            return await _http.SendAsync(req, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new LicenseApiNetworkException(ex.Message, ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new LicenseApiNetworkException("timeout", ex);
        }
    }

    private static async Task<T?> DeserializeAsync<T>(HttpResponseMessage resp, CancellationToken ct)
    {
        var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, JsonOpts, ct);
    }

    private static async Task ThrowMappedAsync(HttpResponseMessage resp)
    {
        var status = (int)resp.StatusCode;
        string? title = null;
        string? detail = null;

        try
        {
            var problem = await resp.Content.ReadFromJsonAsync<ProblemPayload>(JsonOpts);
            title = problem?.Title;
            detail = problem?.Detail;
        }
        catch
        {
            // Body wasn't problem+json — fall through with title=null
        }

        // Map by (status, title)
        if (status == 401) throw new InvalidCredentialsException(detail ?? "E-posta veya şifre yanlış");
        if (status == 403 && title == "email-not-confirmed") throw new EmailNotConfirmedException(detail ?? "E-posta doğrulanmamış");
        if (status == 409 && title == "slot-full") throw new SlotFullException(detail ?? "Slot dolu");
        if (status == 409 && title == "license-revoked") throw new LicenseRevokedException(detail ?? "Lisans iptal");
        if (status == 409 && title == "license-expired") throw new LicenseExpiredException(detail ?? "Lisans süresi dolmuş");
        if (status >= 400 && status < 500)
            throw new ValidationException(title ?? $"http-{status}", detail ?? $"HTTP {status}");

        throw new LicenseApiUnknownException(status, detail ?? $"HTTP {status}");
    }

    private sealed record ProblemPayload(string? Title, string? Detail, int? Status);
}
```

- [ ] **Step 8: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LicenseApiClientTests" 2>&1 | tail -5
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 14/14 API client testleri PASS, toplam 36/36 (22 önceki + 14 yeni).

- [ ] **Step 9: Commit**

```bash
git add LiveDeck.Licensing/Api/ LiveDeck.Licensing.Tests/Api/ LiveDeck.Licensing.Tests/TestHelpers/FakeHttpMessageHandler.cs
git commit -m "feat(licensing): add LicenseApiClient + DTOs + 8 typed exceptions"
```

---

### Task 7: LoginService

**Files:**
- Create: `LiveDeck.Licensing/Services/LoginService.cs`
- Create: `LiveDeck.Licensing.Tests/Services/LoginServiceTests.cs`

**Context:** Login + register + resend + license selection orkestrasyonu. UI'dan çağrılır. AuthStore'a kaydeder, LicenseApiClient'i kullanır. Tek public method başına bir sorumluluk: `LoginAsync`, `RegisterAsync`, `ResendConfirmationAsync`, `GetMyLicensesAsync` (cached token ile). License otomatik aktivasyon `LicenseService` katmanında — burada sadece auth.

- [ ] **Step 1: Failing test yaz**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Services/LoginServiceTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using LiveDeck.Licensing.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.Licensing.Tests.Services;

public sealed class LoginServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;

    public LoginServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _authStore = new AuthStore(new EncryptedStore(), Path.Combine(_dir, "auth.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (LoginService svc, FakeHttpMessageHandler handler, LicenseApiClient api) Build(
        Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        return (new LoginService(api, _authStore), handler, api);
    }

    [Fact]
    public async Task LoginAsync_persists_AuthRecord_with_token_and_me_data()
    {
        var customerId = Guid.NewGuid();
        var (svc, handler, _) = Build(req =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/auth/login")
                return FakeHttpMessageHandler.Json(200, """{"token":"jwt-abc","expiresAt":"2026-05-06T12:00:00Z"}""");
            if (req.RequestUri.AbsolutePath == "/api/v1/me")
                return FakeHttpMessageHandler.Json(200, $$"""{"id":"{{customerId}}","email":"user@example.com","name":"Test User","emailConfirmedAt":"2026-04-29T00:00:00Z","createdAt":"2026-04-01T00:00:00Z"}""");
            throw new InvalidOperationException("unexpected: " + req.RequestUri);
        });

        await svc.LoginAsync("user@example.com", "pw");

        _authStore.IsPresent.Should().BeTrue();
        var saved = _authStore.Load();
        saved.Should().NotBeNull();
        saved!.CustomerId.Should().Be(customerId);
        saved.Email.Should().Be("user@example.com");
        saved.Name.Should().Be("Test User");
        saved.Token.Should().Be("jwt-abc");
    }

    [Fact]
    public async Task LoginAsync_does_not_persist_when_credentials_invalid()
    {
        var (svc, _, _) = Build(_ => FakeHttpMessageHandler.Problem(401, "invalid-credentials"));

        var act = async () => await svc.LoginAsync("u", "wrong");
        await act.Should().ThrowAsync<InvalidCredentialsException>();

        _authStore.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task LoginAsync_does_not_persist_when_email_unconfirmed()
    {
        var (svc, _, _) = Build(_ => FakeHttpMessageHandler.Problem(403, "email-not-confirmed"));

        var act = async () => await svc.LoginAsync("u", "p");
        await act.Should().ThrowAsync<EmailNotConfirmedException>();

        _authStore.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task RegisterAsync_calls_register_endpoint_and_does_not_persist_auth()
    {
        var (svc, handler, _) = Build(_ => FakeHttpMessageHandler.Empty(201));

        await svc.RegisterAsync("u@x.com", "User", "password123");

        handler.Requests[0].RequestUri!.AbsolutePath.Should().Be("/api/v1/auth/register");
        _authStore.IsPresent.Should().BeFalse();
    }

    [Fact]
    public async Task Logout_clears_auth_store_and_token()
    {
        var (svc, _, api) = Build(_ => throw new InvalidOperationException("should not be called"));
        _authStore.Save(new AuthRecord(Guid.NewGuid(), "e", "n", "tok", DateTimeOffset.UtcNow.AddDays(7)));
        api.SetAuthToken("tok");

        svc.Logout();

        _authStore.IsPresent.Should().BeFalse();
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LoginServiceTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — LoginService yok.

- [ ] **Step 3: LoginService impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Services/LoginService.cs` oluştur:

```csharp
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Storage;

namespace LiveDeck.Licensing.Services;

/// <summary>
/// Orchestrates the auth flow: register/resend/login + persisting AuthRecord.
/// License activation lives in <see cref="LicenseService"/>.
/// </summary>
public sealed class LoginService
{
    private readonly LicenseApiClient _api;
    private readonly AuthStore _authStore;

    public LoginService(LicenseApiClient api, AuthStore authStore)
    {
        _api = api;
        _authStore = authStore;
    }

    public async Task LoginAsync(string email, string password, CancellationToken ct = default)
    {
        var loginResp = await _api.LoginAsync(new LoginRequest(email, password), ct);
        _api.SetAuthToken(loginResp.Token);

        var me = await _api.GetMeAsync(ct);

        _authStore.Save(new AuthRecord(
            CustomerId: me.Id,
            Email: me.Email,
            Name: me.Name,
            Token: loginResp.Token,
            TokenExpiresAt: loginResp.ExpiresAt));
    }

    public Task RegisterAsync(string email, string name, string password, CancellationToken ct = default)
        => _api.RegisterAsync(new RegisterRequest(email, name, password), ct);

    public Task ResendConfirmationAsync(string email, CancellationToken ct = default)
        => _api.ResendConfirmationAsync(new ResendRequest(email), ct);

    /// <summary>Returns the customer's active licenses (uses the token from <see cref="LicenseApiClient.SetAuthToken"/>).</summary>
    public Task<List<LicenseSummary>> GetMyLicensesAsync(CancellationToken ct = default)
        => _api.GetMyLicensesAsync(ct);

    public void Logout()
    {
        _authStore.Clear();
        _api.SetAuthToken(null);
    }
}
```

- [ ] **Step 4: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LoginServiceTests" 2>&1 | tail -3
```

Beklenen: 5/5 PASS. Tüm Licensing tests:

```bash
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 41/41.

- [ ] **Step 5: Commit**

```bash
git add LiveDeck.Licensing/Services/LoginService.cs LiveDeck.Licensing.Tests/Services/LoginServiceTests.cs
git commit -m "feat(licensing): add LoginService (login/register/resend/logout orchestration)"
```

---

### Task 8: LicenseService — state machine + InitializeAsync + RefreshAsync

**Files:**
- Create: `LiveDeck.Licensing/Services/LicenseService.cs`
- Create: `LiveDeck.Licensing.Tests/Services/LicenseServiceTests.cs`

**Context:** Çekirdek state machine. `InitializeAsync` (app start), `RefreshAsync` (heartbeat), `ActivateAsync` (UI'dan key seçimi sonrası). `CurrentStatus` property (event ile UI'a bildirir). Token expired ise auth temizler. Network fail durumunda offline grace mantığını uygular. Lisans cevaplarını `LicenseRecord` olarak persist eder.

- [ ] **Step 1: Failing tests**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Services/LicenseServiceTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using LiveDeck.Licensing.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.Licensing.Tests.Services;

public sealed class LicenseServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private readonly FakeHardwareIdProvider _hwId = new();
    private readonly IOptions<LicensingOptions> _opts =
        Options.Create(new LicensingOptions { OfflineGraceDays = 14 });

    public LicenseServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var enc = new EncryptedStore();
        _authStore = new AuthStore(enc, Path.Combine(_dir, "auth.dat"));
        _licenseStore = new LicenseStateStore(enc, Path.Combine(_dir, "license.dat"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private LicenseService Build(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        var handler = new FakeHttpMessageHandler(responder);
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        return new LicenseService(api, _authStore, _licenseStore, _hwId, _opts, NullLogger<LicenseService>.Instance);
    }

    private void SeedAuth(DateTimeOffset? tokenExp = null) =>
        _authStore.Save(new AuthRecord(
            CustomerId: Guid.NewGuid(),
            Email: "u@x",
            Name: "u",
            Token: "tok",
            TokenExpiresAt: tokenExp ?? DateTimeOffset.UtcNow.AddDays(7)));

    private void SeedLicense(DateTimeOffset? lastSuccessful = null, string status = "Active") =>
        _licenseStore.Save(new LicenseRecord(
            LicenseKey: "LDK-X",
            SkuCode: "STD",
            ExpiresAt: DateTimeOffset.UtcNow.AddDays(365),
            RemainingDaysAtLastCheck: 365,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: lastSuccessful ?? DateTimeOffset.UtcNow,
            LastKnownStatus: status));

    // ─── Initialize: no auth ──────────────────────────────────────────

    [Fact]
    public async Task Initialize_with_no_auth_sets_NoLicense()
    {
        var svc = Build(_ => throw new InvalidOperationException("should not call api"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
    }

    [Fact]
    public async Task Initialize_with_expired_token_clears_auth_and_sets_NoLicense()
    {
        SeedAuth(tokenExp: DateTimeOffset.UtcNow.AddDays(-1));
        var svc = Build(_ => throw new InvalidOperationException("should not call api"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
        _authStore.IsPresent.Should().BeFalse();
    }

    // ─── Initialize: auth + no license cache ──────────────────────────

    [Fact]
    public async Task Initialize_with_auth_but_no_license_cache_sets_NoLicense()
    {
        SeedAuth();
        var svc = Build(_ => throw new InvalidOperationException("should not call api when no license"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
    }

    // ─── Initialize: validate paths ────────────────────────────────────

    [Fact]
    public async Task Initialize_active_response_sets_Active_and_persists_license()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.Active);
        var saved = _licenseStore.Load();
        saved!.LastKnownStatus.Should().Be("Active");
    }

    [Fact]
    public async Task Initialize_revoked_response_sets_Revoked()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"revoked","expiresAt":"2027-01-01T00:00:00Z","remainingDays":0,"sku":"STD","slotInfo":null}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.Revoked);
    }

    [Fact]
    public async Task Initialize_expired_response_sets_ExpiredOnline()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"expired","expiresAt":"2024-01-01T00:00:00Z","remainingDays":0,"sku":"STD","slotInfo":null}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.ExpiredOnline);
    }

    [Fact]
    public async Task Initialize_notactivated_response_clears_license_and_sets_NoLicense()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Json(200,
            """{"status":"notactivated","expiresAt":"2027-01-01T00:00:00Z","remainingDays":300,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":false}}"""));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
        _licenseStore.IsPresent.Should().BeFalse();
    }

    // ─── Initialize: offline grace ────────────────────────────────────

    [Fact]
    public async Task Initialize_network_fail_inside_grace_window_sets_OfflineGrace()
    {
        SeedAuth();
        SeedLicense(lastSuccessful: DateTimeOffset.UtcNow.AddDays(-7));
        var svc = Build(_ => throw new HttpRequestException("dns fail"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.OfflineGrace);
    }

    [Fact]
    public async Task Initialize_network_fail_outside_grace_window_sets_OfflineExpired()
    {
        SeedAuth();
        SeedLicense(lastSuccessful: DateTimeOffset.UtcNow.AddDays(-15));
        var svc = Build(_ => throw new HttpRequestException("dns fail"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.OfflineExpired);
    }

    // ─── Initialize: 401 token expired by server ───────────────────────

    [Fact]
    public async Task Initialize_server_401_clears_auth_and_sets_NoLicense()
    {
        SeedAuth();
        SeedLicense();
        var svc = Build(_ => FakeHttpMessageHandler.Problem(401, "token-expired"));

        await svc.InitializeAsync();

        svc.CurrentStatus.Should().Be(LicenseStatus.NoLicense);
        _authStore.IsPresent.Should().BeFalse();
    }

    // ─── ActivateAsync ─────────────────────────────────────────────────

    [Fact]
    public async Task ActivateAsync_persists_license_record_and_sets_Active()
    {
        SeedAuth();
        var responder = (HttpRequestMessage req) =>
        {
            if (req.RequestUri!.AbsolutePath == "/api/v1/licenses/activate")
                return FakeHttpMessageHandler.Json(201,
                    $$"""{"activationId":"{{Guid.NewGuid()}}","expiresAt":"2027-04-29T00:00:00Z"}""");
            if (req.RequestUri.AbsolutePath == "/api/v1/licenses/validate")
                return FakeHttpMessageHandler.Json(200,
                    """{"status":"active","expiresAt":"2027-04-29T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}""");
            throw new InvalidOperationException(req.RequestUri.ToString());
        };
        var svc = Build(responder);

        await svc.ActivateAsync("LDK-NEW", machineName: "PC-1");

        svc.CurrentStatus.Should().Be(LicenseStatus.Active);
        var saved = _licenseStore.Load();
        saved.Should().NotBeNull();
        saved!.LicenseKey.Should().Be("LDK-NEW");
    }

    [Fact]
    public async Task ActivateAsync_throws_SlotFull_when_server_returns_409()
    {
        SeedAuth();
        var svc = Build(_ => FakeHttpMessageHandler.Problem(409, "slot-full"));

        var act = async () => await svc.ActivateAsync("LDK-NEW", machineName: null);
        await act.Should().ThrowAsync<SlotFullException>();
        svc.CurrentStatus.Should().NotBe(LicenseStatus.Active);
    }
}
```

`FakeHardwareIdProvider` test helper'ı henüz yok — Step 2'de eklenecek.

- [ ] **Step 2: FakeHardwareIdProvider helper ekle**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/TestHelpers/FakeHardwareIdProvider.cs` oluştur:

```csharp
using LiveDeck.Licensing;

namespace LiveDeck.Licensing.Tests.TestHelpers;

public sealed class FakeHardwareIdProvider : IHardwareIdProvider
{
    public string Id { get; set; } = "test-hw-fingerprint-deadbeef";
    public string GetHardwareId() => Id;
}
```

- [ ] **Step 3: RED**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LicenseServiceTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — LicenseService yok.

- [ ] **Step 4: LicenseService impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Services/LicenseService.cs` oluştur:

```csharp
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveDeck.Licensing.Services;

/// <summary>
/// State machine controller. Loads cached auth/license, calls /licenses/validate,
/// and emits a <see cref="LicenseStatus"/> for the UI to bind to.
/// </summary>
public sealed class LicenseService
{
    private readonly LicenseApiClient _api;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private readonly IHardwareIdProvider _hwId;
    private readonly LicensingOptions _opt;
    private readonly ILogger<LicenseService> _log;

    public LicenseService(
        LicenseApiClient api,
        AuthStore authStore,
        LicenseStateStore licenseStore,
        IHardwareIdProvider hwId,
        IOptions<LicensingOptions> opt,
        ILogger<LicenseService> log)
    {
        _api = api;
        _authStore = authStore;
        _licenseStore = licenseStore;
        _hwId = hwId;
        _opt = opt.Value;
        _log = log;
    }

    public LicenseStatus CurrentStatus { get; private set; } = LicenseStatus.Initializing;

    public AuthRecord? CurrentAuth { get; private set; }

    public LicenseRecord? CurrentLicense { get; private set; }

    public event EventHandler<LicenseStatus>? StatusChanged;

    /// <summary>
    /// Called once at app startup. Loads cached auth, attempts online validate, falls back to offline grace.
    /// </summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var auth = _authStore.Load();
        if (auth is null)
        {
            CurrentAuth = null;
            CurrentLicense = null;
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        if (auth.TokenExpiresAt <= DateTimeOffset.UtcNow)
        {
            _log.LogInformation("Saved auth token expired locally; clearing.");
            _authStore.Clear();
            CurrentAuth = null;
            CurrentLicense = null;
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        CurrentAuth = auth;
        _api.SetAuthToken(auth.Token);

        var license = _licenseStore.Load();
        CurrentLicense = license;
        if (license is null)
        {
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        await RefreshAsync(ct);
    }

    /// <summary>
    /// Re-validates the current license against the server. Called on startup and from heartbeat.
    /// </summary>
    public async Task RefreshAsync(CancellationToken ct = default)
    {
        var license = CurrentLicense ?? _licenseStore.Load();
        if (license is null || CurrentAuth is null)
        {
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        try
        {
            var response = await _api.ValidateAsync(
                new ValidateRequest(license.LicenseKey, _hwId.GetHardwareId()), ct);

            if (response is null)
            {
                // 404 — license not found for this customer
                _licenseStore.Clear();
                CurrentLicense = null;
                SetStatus(LicenseStatus.NoLicense);
                return;
            }

            HandleValidateResponse(license, response);
        }
        catch (InvalidCredentialsException)
        {
            // Token rejected by server — clear and force re-login
            _authStore.Clear();
            CurrentAuth = null;
            _api.SetAuthToken(null);
            SetStatus(LicenseStatus.NoLicense);
        }
        catch (LicenseApiNetworkException)
        {
            // Offline — grace decision
            var elapsed = DateTimeOffset.UtcNow - license.LastSuccessfulOnlineAt;
            var graceWindow = TimeSpan.FromDays(_opt.OfflineGraceDays);
            SetStatus(elapsed <= graceWindow ? LicenseStatus.OfflineGrace : LicenseStatus.OfflineExpired);
        }
    }

    /// <summary>
    /// Called from the UI after the user picks (or auto-binds) a license. Calls /licenses/activate
    /// then /licenses/validate to get fresh state.
    /// </summary>
    public async Task ActivateAsync(string licenseKey, string? machineName, CancellationToken ct = default)
    {
        if (CurrentAuth is null)
            throw new InvalidOperationException("ActivateAsync requires a logged-in customer.");

        var hwId = _hwId.GetHardwareId();
        await _api.ActivateAsync(new ActivateRequest(licenseKey, hwId, machineName), ct);

        var validate = await _api.ValidateAsync(new ValidateRequest(licenseKey, hwId), ct);
        if (validate is null)
        {
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        var seed = new LicenseRecord(
            LicenseKey: licenseKey,
            SkuCode: validate.Sku ?? "STD",
            ExpiresAt: validate.ExpiresAt ?? DateTimeOffset.UtcNow.AddDays(1),
            RemainingDaysAtLastCheck: validate.RemainingDays ?? 0,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: DateTimeOffset.UtcNow,
            LastKnownStatus: validate.Status);
        HandleValidateResponse(seed, validate);
    }

    /// <summary>Logout: clear caches and force NoLicense state.</summary>
    public void Logout()
    {
        _authStore.Clear();
        _licenseStore.Clear();
        _api.SetAuthToken(null);
        CurrentAuth = null;
        CurrentLicense = null;
        SetStatus(LicenseStatus.NoLicense);
    }

    private void HandleValidateResponse(LicenseRecord prior, ValidateResponse response)
    {
        var status = MapServerStatus(response.Status);

        if (status == LicenseStatus.NoLicense)
        {
            _licenseStore.Clear();
            CurrentLicense = null;
            SetStatus(LicenseStatus.NoLicense);
            return;
        }

        var isOnlineSuccess = status == LicenseStatus.Active;
        var lastOnline = isOnlineSuccess ? DateTimeOffset.UtcNow : prior.LastSuccessfulOnlineAt;

        var updated = new LicenseRecord(
            LicenseKey: prior.LicenseKey,
            SkuCode: response.Sku ?? prior.SkuCode,
            ExpiresAt: response.ExpiresAt ?? prior.ExpiresAt,
            RemainingDaysAtLastCheck: response.RemainingDays ?? prior.RemainingDaysAtLastCheck,
            LastValidatedAt: DateTimeOffset.UtcNow,
            LastSuccessfulOnlineAt: lastOnline,
            LastKnownStatus: response.Status);
        _licenseStore.Save(updated);
        CurrentLicense = updated;
        SetStatus(status);
    }

    private static LicenseStatus MapServerStatus(string serverStatus) =>
        serverStatus.ToLowerInvariant() switch
        {
            "active" => LicenseStatus.Active,
            "revoked" => LicenseStatus.Revoked,
            "expired" => LicenseStatus.ExpiredOnline,
            "notactivated" => LicenseStatus.NoLicense,
            _ => LicenseStatus.NoLicense
        };

    private void SetStatus(LicenseStatus status)
    {
        if (CurrentStatus == status) return;
        CurrentStatus = status;
        StatusChanged?.Invoke(this, status);
    }
}
```

- [ ] **Step 5: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~LicenseServiceTests" 2>&1 | tail -5
```

Beklenen: 11/11 PASS.

- [ ] **Step 6: Tüm Licensing tests**

```bash
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 52/52 (41 önceki + 11 yeni).

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.Licensing/Services/LicenseService.cs LiveDeck.Licensing.Tests/Services/LicenseServiceTests.cs LiveDeck.Licensing.Tests/TestHelpers/FakeHardwareIdProvider.cs
git commit -m "feat(licensing): add LicenseService (state machine + Initialize/Refresh/Activate)"
```

---

### Task 9: HeartbeatHostedService

**Files:**
- Create: `LiveDeck.Licensing/Services/HeartbeatHostedService.cs`
- Create: `LiveDeck.Licensing.Tests/Services/HeartbeatHostedServiceTests.cs`

**Context:** `BackgroundService` — `LicenseService.RefreshAsync` ı `HeartbeatIntervalHours` aralıklarla çağırır. Test edilebilir olması için `Task.Delay` yerine `PeriodicTimer` kullanırız (cancellation friendly). Test: hosted service'i kısa interval ile başlat, en az 2 refresh çağrısı yapıldığını doğrula.

- [ ] **Step 1: Failing tests yaz**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing.Tests/Services/HeartbeatHostedServiceTests.cs` oluştur:

```csharp
using FluentAssertions;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using LiveDeck.Licensing.Tests.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.Licensing.Tests.Services;

public sealed class HeartbeatHostedServiceTests : IDisposable
{
    private readonly string _dir;
    private readonly AuthStore _authStore;
    private readonly LicenseStateStore _licenseStore;
    private int _validateCallCount;

    public HeartbeatHostedServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "LiveDeck.Licensing.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var enc = new EncryptedStore();
        _authStore = new AuthStore(enc, Path.Combine(_dir, "auth.dat"));
        _licenseStore = new LicenseStateStore(enc, Path.Combine(_dir, "license.dat"));

        _authStore.Save(new AuthRecord(Guid.NewGuid(), "u@x", "u", "tok", DateTimeOffset.UtcNow.AddDays(7)));
        _licenseStore.Save(new LicenseRecord("LDK", "STD",
            DateTimeOffset.UtcNow.AddDays(365), 365,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, "Active"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private (HeartbeatHostedService svc, LicenseService licSvc) Build(TimeSpan interval)
    {
        var handler = new FakeHttpMessageHandler(_ =>
        {
            Interlocked.Increment(ref _validateCallCount);
            return FakeHttpMessageHandler.Json(200,
                """{"status":"active","expiresAt":"2027-01-01T00:00:00Z","remainingDays":365,"sku":"STD","slotInfo":{"used":1,"total":1,"thisDeviceActive":true}}""");
        });
        var http = new HttpClient(handler) { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http);
        var opts = Options.Create(new LicensingOptions { OfflineGraceDays = 14, HeartbeatIntervalHours = 24 });
        var licSvc = new LicenseService(api, _authStore, _licenseStore, new FakeHardwareIdProvider(), opts, NullLogger<LicenseService>.Instance);
        var hb = new HeartbeatHostedService(licSvc, NullLogger<HeartbeatHostedService>.Instance, interval);
        return (hb, licSvc);
    }

    [Fact]
    public async Task Heartbeat_calls_RefreshAsync_periodically()
    {
        var (hb, licSvc) = Build(interval: TimeSpan.FromMilliseconds(50));
        await licSvc.InitializeAsync();
        var initialCount = _validateCallCount;

        using var cts = new CancellationTokenSource();
        var task = hb.StartAsync(cts.Token);
        await Task.Delay(250);
        cts.Cancel();
        try { await hb.StopAsync(CancellationToken.None); } catch { }

        // After ~250ms with 50ms interval, expect at least 2 additional calls
        (_validateCallCount - initialCount).Should().BeGreaterThanOrEqualTo(2);
    }

    [Fact]
    public async Task Heartbeat_stops_cleanly_on_cancellation()
    {
        var (hb, _) = Build(interval: TimeSpan.FromSeconds(60));
        using var cts = new CancellationTokenSource();
        await hb.StartAsync(cts.Token);
        cts.Cancel();

        var stopAct = async () => await hb.StopAsync(CancellationToken.None);
        await stopAct.Should().NotThrowAsync();
    }
}
```

- [ ] **Step 2: RED**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~HeartbeatHostedServiceTests" 2>&1 | tail -3
```

Beklenen: derleme hatası — HeartbeatHostedService yok.

- [ ] **Step 3: HeartbeatHostedService impl**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.Licensing/Services/HeartbeatHostedService.cs` oluştur:

```csharp
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveDeck.Licensing.Services;

/// <summary>
/// Periodically calls <see cref="LicenseService.RefreshAsync"/> while the app is running.
/// Default interval comes from <see cref="LicensingOptions.HeartbeatIntervalHours"/>.
/// </summary>
public sealed class HeartbeatHostedService : BackgroundService
{
    private readonly LicenseService _licenseService;
    private readonly ILogger<HeartbeatHostedService> _log;
    private readonly TimeSpan _interval;

    public HeartbeatHostedService(
        LicenseService licenseService,
        ILogger<HeartbeatHostedService> log,
        IOptions<LicensingOptions> opt)
        : this(licenseService, log, TimeSpan.FromHours(opt.Value.HeartbeatIntervalHours)) { }

    // Test-only ctor with explicit interval.
    internal HeartbeatHostedService(
        LicenseService licenseService,
        ILogger<HeartbeatHostedService> log,
        TimeSpan interval)
    {
        _licenseService = licenseService;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(_interval);
        while (await WaitForNextTickSafely(timer, stoppingToken))
        {
            try
            {
                await _licenseService.RefreshAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Heartbeat refresh failed; will retry next interval");
            }
        }
    }

    private static async Task<bool> WaitForNextTickSafely(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
```

- [ ] **Step 4: GREEN**

```bash
dotnet test LiveDeck.Licensing.Tests --filter "FullyQualifiedName~HeartbeatHostedServiceTests" 2>&1 | tail -3
```

Beklenen: 2/2 PASS.

- [ ] **Step 5: Tüm Licensing tests**

```bash
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
```

Beklenen: 54/54.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.Licensing/Services/HeartbeatHostedService.cs LiveDeck.Licensing.Tests/Services/HeartbeatHostedServiceTests.cs
git commit -m "feat(licensing): add HeartbeatHostedService (PeriodicTimer-based RefreshAsync loop)"
```

---

### Task 10: AppHost DI integration + LiveDeck.App project reference

**Files:**
- Modify: `LiveDeck.App/LiveDeck.App.csproj` (project reference)
- Modify: `LiveDeck.App/AppHost.cs` (DI registrations + paths)
- Create: `LiveDeck.App/AppPaths.cs` patch (LicenseDataFolder, AuthFile, LicenseFile)
- Modify: `LiveDeck.Tests/.../AppHostTests.cs` (yeni DI registration testleri)

**Context:** LiveDeck.App'e `LiveDeck.Licensing` referansı + AppHost'a 8 yeni DI kaydı + AppPaths'e auth/license yol sabitleri. Environment variable override (`LIVEDECK_LICENSE_BASE_URL`) prod/dev ayrımı için. LiveDeck.Tests'e DI smoke test (resolve edilebiliyor mu).

- [ ] **Step 1: csproj reference ekle**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/LiveDeck.App.csproj`'i aç. `<ItemGroup>` (project references) bloğuna **mevcut referansların altına** yeni satır ekle:

```xml
    <ProjectReference Include="..\LiveDeck.Licensing\LiveDeck.Licensing.csproj" />
```

Final block şöyle olmalı:

```xml
  <ItemGroup>
    <ProjectReference Include="..\LiveDeck.Core\LiveDeck.Core.csproj" />
    <ProjectReference Include="..\LiveDeck.Chat\LiveDeck.Chat.csproj" />
    <ProjectReference Include="..\LiveDeck.Overlay\LiveDeck.Overlay.csproj" />
    <ProjectReference Include="..\LiveDeck.Labeling\LiveDeck.Labeling.csproj" />
    <ProjectReference Include="..\LiveDeck.Licensing\LiveDeck.Licensing.csproj" />
  </ItemGroup>
```

- [ ] **Step 2: AppPaths'e licensing yolları ekle**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppPaths.cs` dosyasını aç. Mevcut path property'lerinin yanına yeni 2 path ekle (örnekleri mevcut konvansiyona uygun):

```csharp
    public static string AuthFile => Path.Combine(LocalAppDataRoot, "auth.dat");
    public static string LicenseFile => Path.Combine(LocalAppDataRoot, "license.dat");
```

Note: `LocalAppDataRoot` mevcut bir property değilse, mevcut `SettingsFile`'ın hangi base'i kullandığına bak. Eğer mevcut paths `Environment.GetFolderPath(SpecialFolder.LocalApplicationData)` + "LiveDeck" pattern'i kullanıyorsa, aynı base'i kullan. Mevcut `LocalAppDataRoot` (veya equivalent) yoksa, AppPaths'in mevcut yapısını referans alarak aynı kalıbı uygula.

`AppPaths.EnsureDirectoriesExist()` zaten ana klasörü oluşturuyor — auth.dat / license.dat aynı klasörde, ek aksiyon gerek yok.

- [ ] **Step 3: AppHost'a DI kayıtlarını ekle**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/AppHost.cs`'i aç. Üst tarafa using'leri ekle:

```csharp
using System.Net.Http;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
```

Constructor içinde `// Shortcuts (Phase 3b-1)` bloğunun **altına** yeni "// Licensing (Phase 4b)" bloğu ekle:

```csharp
        // Licensing (Phase 4b)
        var licensingOptions = BuildLicensingOptions();
        services.AddSingleton(Options.Create(licensingOptions));
        services.AddSingleton<IHardwareIdProvider, HardwareIdProvider>();
        services.AddSingleton<EncryptedStore>();
        services.AddSingleton(sp => new AuthStore(
            sp.GetRequiredService<EncryptedStore>(), AppPaths.AuthFile));
        services.AddSingleton(sp => new LicenseStateStore(
            sp.GetRequiredService<EncryptedStore>(), AppPaths.LicenseFile));
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

Constructor'ın sonuna (mevcut `var orphans = ...` bloğunun **altına**) hosted service başlatma kodu ekle (HostedService'lerin manuel start'a ihtiyaç duyduğu pattern bu codebase'de yok — `Microsoft.Extensions.Hosting`'in `Host` builder'ını kullanmıyor olabiliriz; `BackgroundService.StartAsync` `App.xaml.cs`'den manuel çağrılır, Task 13'te yapacağız).

Sınıfın **sonunda yeni private metot ekle**:

```csharp
    private static LicensingOptions BuildLicensingOptions()
    {
        var opt = new LicensingOptions();
        var envBase = Environment.GetEnvironmentVariable("LIVEDECK_LICENSE_BASE_URL");
        if (!string.IsNullOrWhiteSpace(envBase)) opt.ServerBaseUrl = envBase.Trim();
        return opt;
    }
```

- [ ] **Step 4: Mevcut LiveDeck.Tests'e DI smoke testi ekle**

`LiveDeck.Tests/` altında AppHost'la ilgili mevcut test dosyası varsa onu genişlet. Yoksa yeni dosya oluştur: `C:/Users/burak/source/repos/LiveDeck/LiveDeck.Tests/App/LicensingDiTests.cs`

```csharp
using FluentAssertions;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.Tests.App;

public class LicensingDiTests
{
    [Fact]
    public void AppHost_resolves_LicenseService_singleton()
    {
        using var host = new global::LiveDeck.App.AppHost();

        var first = host.Services.GetRequiredService<LicenseService>();
        var second = host.Services.GetRequiredService<LicenseService>();

        first.Should().NotBeNull();
        first.Should().BeSameAs(second);
    }

    [Fact]
    public void AppHost_resolves_HardwareIdProvider_as_real_implementation()
    {
        using var host = new global::LiveDeck.App.AppHost();

        var hwId = host.Services.GetRequiredService<IHardwareIdProvider>();
        hwId.Should().BeOfType<HardwareIdProvider>();
    }

    [Fact]
    public void AppHost_resolves_AuthStore_and_LicenseStateStore()
    {
        using var host = new global::LiveDeck.App.AppHost();

        host.Services.GetRequiredService<AuthStore>().Should().NotBeNull();
        host.Services.GetRequiredService<LicenseStateStore>().Should().NotBeNull();
    }

    [Fact]
    public void AppHost_resolves_LicenseApiClient_with_BaseAddress()
    {
        using var host = new global::LiveDeck.App.AppHost();

        var client = host.Services.GetRequiredService<LicenseApiClient>();
        client.Should().NotBeNull();
    }
}
```

`LiveDeck.Tests` projesinin `LiveDeck.App`'e referansı olmayabilir. Olmadığını doğrula:

```bash
grep -i "LiveDeck.App" LiveDeck.Tests/LiveDeck.Tests.csproj
```

Sonuç boşsa, csproj'a referans ekle: `LiveDeck.Tests/LiveDeck.Tests.csproj` dosyasına ProjectReference bloğunu aç:

```xml
    <ProjectReference Include="..\LiveDeck.App\LiveDeck.App.csproj" />
```

`LiveDeck.Tests.csproj`'in target framework'ü (Phase 1b'de `net10.0-windows`'a yükseltilmişti) zaten LiveDeck.App'i import edebilir. Aksi takdirde Tests projesinde `<UseWPF>true</UseWPF>` gerekebilir; ama AppHost class'ı pure DI, WPF'siz çalışmalı. Run sonrası WPF init hatası gelirse Step 5'te ele al.

- [ ] **Step 5: Build + run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Beklenen: 0 errors, 0 warnings.

```bash
dotnet test LiveDeck.Tests --filter "FullyQualifiedName~LicensingDiTests" 2>&1 | tail -5
```

Beklenen: 4/4 PASS.

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 121/121 (117 baseline + 4 yeni).

```bash
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen: 54/54 + 62/62.

**Eğer LicensingDiTests AppHost ctor'da WPF/static state nedeniyle fail olursa:** AppHost'u test'ten kullanmak yerine, sadece `IServiceCollection` üzerinden registration'ları doğrulayan bir `ServicesExtensions.AddLivelicensing(...)` static helper extract et ve onu test et. Bu önerilen pattern; ama önce Step 5'in fail edip etmediğini gör. Fail ederse: AppHost.cs'de DI kayıtları yapan bloğu `AppHost`'tan ayrı bir `internal static class LicensingServices.Register(IServiceCollection services)` metoduna taşı, AppHost o metodu çağırsın. LicensingDiTests bu metodun resolved servislerini test etsin.

- [ ] **Step 6: Commit**

```bash
git add LiveDeck.App/LiveDeck.App.csproj LiveDeck.App/AppHost.cs LiveDeck.App/AppPaths.cs LiveDeck.Tests/
git commit -m "feat(app): wire LiveDeck.Licensing into AppHost DI + paths"
```

---

### Task 11: LoginDialog (XAML + ViewModel)

**Files:**
- Create: `LiveDeck.App/ViewModels/LoginDialogViewModel.cs`
- Create: `LiveDeck.App/Views/LoginDialog.xaml`
- Create: `LiveDeck.App/Views/LoginDialog.xaml.cs`
- Modify: `LiveDeck.App/AppHost.cs` (LoginDialog + ViewModel kaydı)

**Context:** Modal dialog, 4 mod state: Login, Register, ConfirmPending, LicenseSelection. Tek dialog, mode'a göre farklı paneller görünür/gizli. ViewModel CommunityToolkit.Mvvm `ObservableObject` + `RelayCommand`. Dialog kapanırken sonuç property'si (DialogResult bool? + LicenseSelection seçilen key).

UI testlerini WPF dialog için yazmıyoruz (manuel smoke). ViewModel'in command logic'i unit test edilebilir ama bu plan'da skip; manuel akış yeterli (Phase 4a UI'larda da WPF dialog testi yoktu).

- [ ] **Step 1: LoginDialogViewModel oluştur**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/LoginDialogViewModel.cs` oluştur:

```csharp
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Api.Models;
using LiveDeck.Licensing.Services;
using LiveDeck.Licensing.Storage;

namespace LiveDeck.App.ViewModels;

public enum LoginDialogMode
{
    Login,
    Register,
    ConfirmPending,
    LicenseSelection
}

public sealed partial class LoginDialogViewModel : ObservableObject
{
    private readonly LoginService _login;
    private readonly LicenseService _licenseService;
    private readonly AuthStore _authStore;

    public LoginDialogViewModel(LoginService login, LicenseService licenseService, AuthStore authStore)
    {
        _login = login;
        _licenseService = licenseService;
        _authStore = authStore;

        SubmitLoginCommand = new AsyncRelayCommand(SubmitLoginAsync, () => !IsBusy);
        SubmitRegisterCommand = new AsyncRelayCommand(SubmitRegisterAsync, () => !IsBusy);
        ResendCommand = new AsyncRelayCommand(ResendAsync, () => !IsBusy);
        ActivateSelectedCommand = new AsyncRelayCommand(ActivateSelectedAsync, () => !IsBusy && Selected is not null);
        SwitchToRegisterCommand = new RelayCommand(() => Mode = LoginDialogMode.Register);
        SwitchToLoginCommand = new RelayCommand(() => Mode = LoginDialogMode.Login);
    }

    [ObservableProperty] private LoginDialogMode _mode = LoginDialogMode.Login;
    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private string _passwordConfirm = "";
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private System.Collections.ObjectModel.ObservableCollection<LicenseSummary> _licenses = new();
    [ObservableProperty] private LicenseSummary? _selected;

    public ICommand SubmitLoginCommand { get; }
    public ICommand SubmitRegisterCommand { get; }
    public ICommand ResendCommand { get; }
    public ICommand ActivateSelectedCommand { get; }
    public ICommand SwitchToRegisterCommand { get; }
    public ICommand SwitchToLoginCommand { get; }

    /// <summary>Set when the dialog should close successfully — caller reads CurrentStatus.</summary>
    public event EventHandler? RequestClose;

    private async Task SubmitLoginAsync()
    {
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "E-posta ve şifre boş olamaz";
            return;
        }

        IsBusy = true; ErrorMessage = null;
        try
        {
            await _login.LoginAsync(Email, Password);
            // Now check user's licenses
            var licenses = await _login.GetMyLicensesAsync();
            if (licenses.Count == 0)
            {
                ErrorMessage = "Bu hesaba bağlı aktif lisans yok. Yöneticinize başvurun.";
                _login.Logout();
                return;
            }
            if (licenses.Count == 1)
            {
                await _licenseService.ActivateAsync(licenses[0].LicenseKey, machineName: Environment.MachineName);
                RequestClose?.Invoke(this, EventArgs.Empty);
                return;
            }

            Licenses.Clear();
            foreach (var l in licenses) Licenses.Add(l);
            Selected = Licenses[0];
            Mode = LoginDialogMode.LicenseSelection;
        }
        catch (InvalidCredentialsException) { ErrorMessage = "E-posta veya şifre yanlış"; }
        catch (EmailNotConfirmedException)
        {
            ErrorMessage = "E-postanı doğrula. Onay linki için e-posta kutunu kontrol et.";
        }
        catch (LicenseApiNetworkException) { ErrorMessage = "Sunucuya ulaşılamıyor. İnternet bağlantını kontrol et."; }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task SubmitRegisterAsync()
    {
        ErrorMessage = null;
        if (string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Name) || string.IsNullOrWhiteSpace(Password))
        {
            ErrorMessage = "Tüm alanları doldur";
            return;
        }
        if (Password.Length < 8) { ErrorMessage = "Şifre en az 8 karakter olmalı"; return; }
        if (Password != PasswordConfirm) { ErrorMessage = "Şifreler eşleşmiyor"; return; }

        IsBusy = true;
        try
        {
            await _login.RegisterAsync(Email, Name, Password);
            Mode = LoginDialogMode.ConfirmPending;
        }
        catch (ValidationException ex) { ErrorMessage = ex.Message; }
        catch (LicenseApiNetworkException) { ErrorMessage = "Sunucuya ulaşılamıyor"; }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ResendAsync()
    {
        IsBusy = true; ErrorMessage = null;
        try
        {
            await _login.ResendConfirmationAsync(Email);
            ErrorMessage = "Yeni doğrulama linki gönderildi.";
        }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }

    private async Task ActivateSelectedAsync()
    {
        if (Selected is null) return;
        IsBusy = true; ErrorMessage = null;
        try
        {
            await _licenseService.ActivateAsync(Selected.LicenseKey, machineName: Environment.MachineName);
            RequestClose?.Invoke(this, EventArgs.Empty);
        }
        catch (SlotFullException) { ErrorMessage = "Tüm cihaz slotları dolu. Diğer cihazda çıkış yap."; }
        catch (LicenseApiException ex) { ErrorMessage = "Hata: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
```

- [ ] **Step 2: LoginDialog.xaml**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/LoginDialog.xaml` oluştur:

```xml
<Window x:Class="LiveDeck.App.Views.LoginDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        xmlns:vm="clr-namespace:LiveDeck.App.ViewModels"
        Title="LiveDeck — Giriş"
        Width="420" Height="480"
        WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize"
        ShowInTaskbar="True">
    <Window.Resources>
        <BooleanToVisibilityConverter x:Key="BoolToVis"/>
    </Window.Resources>

    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="LiveDeck" FontSize="22" FontWeight="Bold" Margin="0,0,0,16"/>

        <!-- Login mode -->
        <StackPanel Grid.Row="1" Visibility="{Binding IsLoginMode, Converter={StaticResource BoolToVis}}">
            <TextBlock Text="E-posta" Margin="0,0,0,4"/>
            <TextBox Text="{Binding Email, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,12"/>
            <TextBlock Text="Şifre" Margin="0,0,0,4"/>
            <PasswordBox x:Name="LoginPassword" Margin="0,0,0,12" PasswordChanged="OnLoginPasswordChanged"/>
            <Button Content="Giriş yap" Command="{Binding SubmitLoginCommand}" Padding="0,8" Margin="0,8,0,0"/>
            <TextBlock Margin="0,12,0,0">
                <Hyperlink Command="{Binding SwitchToRegisterCommand}">Hesap oluştur</Hyperlink>
            </TextBlock>
        </StackPanel>

        <!-- Register mode -->
        <StackPanel Grid.Row="1" Visibility="{Binding IsRegisterMode, Converter={StaticResource BoolToVis}}">
            <TextBlock Text="Ad Soyad" Margin="0,0,0,4"/>
            <TextBox Text="{Binding Name, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,12"/>
            <TextBlock Text="E-posta" Margin="0,0,0,4"/>
            <TextBox Text="{Binding Email, UpdateSourceTrigger=PropertyChanged}" Margin="0,0,0,12"/>
            <TextBlock Text="Şifre (en az 8 karakter)" Margin="0,0,0,4"/>
            <PasswordBox x:Name="RegisterPassword" Margin="0,0,0,12" PasswordChanged="OnRegisterPasswordChanged"/>
            <TextBlock Text="Şifre tekrar" Margin="0,0,0,4"/>
            <PasswordBox x:Name="RegisterPasswordConfirm" Margin="0,0,0,12" PasswordChanged="OnRegisterPasswordConfirmChanged"/>
            <Button Content="Kayıt ol" Command="{Binding SubmitRegisterCommand}" Padding="0,8" Margin="0,8,0,0"/>
            <TextBlock Margin="0,12,0,0">
                <Hyperlink Command="{Binding SwitchToLoginCommand}">Giriş ekranına dön</Hyperlink>
            </TextBlock>
        </StackPanel>

        <!-- ConfirmPending mode -->
        <StackPanel Grid.Row="1" Visibility="{Binding IsConfirmPendingMode, Converter={StaticResource BoolToVis}}">
            <TextBlock TextWrapping="Wrap" Margin="0,0,0,16">
                E-posta adresine doğrulama linki gönderdik. Linke tıklayıp hesabını aktifleştir, sonra giriş yap.
            </TextBlock>
            <Button Content="Linki tekrar gönder" Command="{Binding ResendCommand}" Padding="0,6" Margin="0,0,0,12"/>
            <TextBlock>
                <Hyperlink Command="{Binding SwitchToLoginCommand}">Giriş ekranına dön</Hyperlink>
            </TextBlock>
        </StackPanel>

        <!-- LicenseSelection mode -->
        <StackPanel Grid.Row="1" Visibility="{Binding IsLicenseSelectionMode, Converter={StaticResource BoolToVis}}">
            <TextBlock Text="Bu makineye aktive edilecek lisansı seç:" Margin="0,0,0,12"/>
            <ListBox ItemsSource="{Binding Licenses}" SelectedItem="{Binding Selected}" Height="200"
                     DisplayMemberPath="LicenseKey"/>
            <Button Content="Bu makineye aktive et" Command="{Binding ActivateSelectedCommand}" Padding="0,8" Margin="0,12,0,0"/>
        </StackPanel>

        <!-- Error message + busy -->
        <StackPanel Grid.Row="2" Margin="0,16,0,0">
            <TextBlock Text="{Binding ErrorMessage}" Foreground="Crimson" TextWrapping="Wrap"
                       Visibility="{Binding HasError, Converter={StaticResource BoolToVis}}"/>
            <ProgressBar IsIndeterminate="True" Height="2" Margin="0,8,0,0"
                         Visibility="{Binding IsBusy, Converter={StaticResource BoolToVis}}"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: LoginDialog.xaml.cs (code-behind)**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/LoginDialog.xaml.cs` oluştur:

```csharp
using System;
using System.ComponentModel;
using System.Windows;
using LiveDeck.App.ViewModels;

namespace LiveDeck.App.Views;

public partial class LoginDialog : Window
{
    private readonly LoginDialogViewModel _vm;

    public LoginDialog(LoginDialogViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        vm.RequestClose += OnRequestClose;
        vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnRequestClose(object? sender, EventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Visibility convenience properties on VM are derived from Mode; we just need to refresh.
    }

    private void OnLoginPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb) _vm.Password = pb.Password;
    }

    private void OnRegisterPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb) _vm.Password = pb.Password;
    }

    private void OnRegisterPasswordConfirmChanged(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.PasswordBox pb) _vm.PasswordConfirm = pb.Password;
    }
}
```

- [ ] **Step 4: ViewModel'e visibility helper property'leri ekle**

`LoginDialogViewModel.cs`'e (Step 1'de oluşturduğun) sınıfın sonuna ekle (mevcut metotların altına, sınıfın `}` öncesi):

```csharp
    public bool IsLoginMode => Mode == LoginDialogMode.Login;
    public bool IsRegisterMode => Mode == LoginDialogMode.Register;
    public bool IsConfirmPendingMode => Mode == LoginDialogMode.ConfirmPending;
    public bool IsLicenseSelectionMode => Mode == LoginDialogMode.LicenseSelection;
    public bool HasError => !string.IsNullOrWhiteSpace(ErrorMessage);

    partial void OnModeChanged(LoginDialogMode value)
    {
        OnPropertyChanged(nameof(IsLoginMode));
        OnPropertyChanged(nameof(IsRegisterMode));
        OnPropertyChanged(nameof(IsConfirmPendingMode));
        OnPropertyChanged(nameof(IsLicenseSelectionMode));
    }

    partial void OnErrorMessageChanged(string? value)
    {
        OnPropertyChanged(nameof(HasError));
    }
```

`partial void` metodları CommunityToolkit.Mvvm source generator tarafından üretilen partial class'la birleşir; `[ObservableProperty]` field'ı için otomatik change hook.

- [ ] **Step 5: AppHost'a LoginDialog + ViewModel kaydet**

`AppHost.cs`'de "// Licensing (Phase 4b)" bloğunun **altına** ekle:

```csharp
        // Licensing dialogs (Phase 4b)
        services.AddTransient<ViewModels.LoginDialogViewModel>();
        services.AddTransient<Views.LoginDialog>();
```

- [ ] **Step 6: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.App 2>&1 | tail -10
```

Beklenen: 0 errors, 0 warnings.

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 121/121 — yeni testler yok ama önceki regression korunmuş olmalı.

- [ ] **Step 7: Commit**

```bash
git add LiveDeck.App/ViewModels/LoginDialogViewModel.cs LiveDeck.App/Views/LoginDialog.xaml LiveDeck.App/Views/LoginDialog.xaml.cs LiveDeck.App/AppHost.cs
git commit -m "feat(app): add LoginDialog (4-mode: Login/Register/ConfirmPending/LicenseSelection)"
```

---

### Task 12: AccountDialog + LicenseStatusIndicator + MainShell soft-gate bindings

**Files:**
- Create: `LiveDeck.App/ViewModels/AccountDialogViewModel.cs`
- Create: `LiveDeck.App/Views/AccountDialog.xaml`
- Create: `LiveDeck.App/Views/AccountDialog.xaml.cs`
- Modify: `LiveDeck.App/ViewModels/MainShellViewModel.cs` (`IsLicenseWritable`, `LicenseStatusText`, `LicenseStatusBrush`, command CanExecute)
- Modify: `LiveDeck.App/Views/MainShellView.xaml` (status indicator + ⋮ "Hesap" item)
- Modify: `LiveDeck.App/AppHost.cs` (AccountDialog + ViewModel kaydı)

**Context:** Login sonrası kullanıcı kontrolü için Hesap dialog'u + üst bar'da renkli status indicator + soft-gate property'si (binding ile mevcut komutlar disable). MainShellViewModel `LicenseService.StatusChanged` event'ine subscribe olur, UI thread'e dispatch eder.

- [ ] **Step 1: AccountDialogViewModel**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/AccountDialogViewModel.cs` oluştur:

```csharp
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Api;
using LiveDeck.Licensing.Services;

namespace LiveDeck.App.ViewModels;

public sealed partial class AccountDialogViewModel : ObservableObject
{
    private readonly LicenseService _licenseService;
    private readonly LoginService _loginService;

    public AccountDialogViewModel(LicenseService licenseService, LoginService loginService)
    {
        _licenseService = licenseService;
        _loginService = loginService;

        Email = _licenseService.CurrentAuth?.Email ?? "";
        Name = _licenseService.CurrentAuth?.Name ?? "";
        LicenseKey = _licenseService.CurrentLicense?.LicenseKey ?? "—";
        SkuCode = _licenseService.CurrentLicense?.SkuCode ?? "—";
        ExpiresAt = _licenseService.CurrentLicense?.ExpiresAt;
        StatusText = _licenseService.CurrentStatus.ToString();

        LogoutCommand = new RelayCommand(Logout);
        ReconnectCommand = new AsyncRelayCommand(ReconnectAsync);
    }

    [ObservableProperty] private string _email = "";
    [ObservableProperty] private string _name = "";
    [ObservableProperty] private string _licenseKey = "—";
    [ObservableProperty] private string _skuCode = "—";
    [ObservableProperty] private DateTimeOffset? _expiresAt;
    [ObservableProperty] private string _statusText = "";

    public ICommand LogoutCommand { get; }
    public ICommand ReconnectCommand { get; }

    public event EventHandler? RequestClose;

    private void Logout()
    {
        _licenseService.Logout();
        RequestClose?.Invoke(this, EventArgs.Empty);
    }

    private async Task ReconnectAsync()
    {
        try
        {
            await _licenseService.RefreshAsync();
            StatusText = _licenseService.CurrentStatus.ToString();
        }
        catch (LicenseApiException ex)
        {
            StatusText = "Hata: " + ex.Message;
        }
    }
}
```

- [ ] **Step 2: AccountDialog.xaml**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/AccountDialog.xaml` oluştur:

```xml
<Window x:Class="LiveDeck.App.Views.AccountDialog"
        xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="LiveDeck — Hesap"
        Width="420" Height="360"
        WindowStartupLocation="CenterScreen"
        ResizeMode="NoResize">
    <Grid Margin="24">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
            <RowDefinition Height="Auto"/>
        </Grid.RowDefinitions>

        <TextBlock Grid.Row="0" Text="Hesap Bilgileri" FontSize="18" FontWeight="Bold" Margin="0,0,0,16"/>

        <StackPanel Grid.Row="1">
            <TextBlock Text="E-posta" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding Email}" Margin="0,0,0,12"/>

            <TextBlock Text="Ad Soyad" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding Name}" Margin="0,0,0,16"/>

            <Separator Margin="0,4,0,12"/>

            <TextBlock Text="Lisans Anahtarı" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding LicenseKey}" FontFamily="Consolas" Margin="0,0,0,8"/>

            <TextBlock Text="SKU" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding SkuCode}" Margin="0,0,0,8"/>

            <TextBlock Text="Bitiş Tarihi" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding ExpiresAt, StringFormat='{}{0:dd.MM.yyyy}'}" Margin="0,0,0,8"/>

            <TextBlock Text="Durum" FontWeight="SemiBold"/>
            <TextBlock Text="{Binding StatusText}" Margin="0,0,0,8"/>
        </StackPanel>

        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right">
            <Button Content="Tekrar bağlan" Command="{Binding ReconnectCommand}" Padding="12,6" Margin="0,0,8,0"/>
            <Button Content="Çıkış yap" Command="{Binding LogoutCommand}" Padding="12,6"/>
        </StackPanel>
    </Grid>
</Window>
```

- [ ] **Step 3: AccountDialog.xaml.cs**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/AccountDialog.xaml.cs` oluştur:

```csharp
using System;
using System.Windows;
using LiveDeck.App.ViewModels;

namespace LiveDeck.App.Views;

public partial class AccountDialog : Window
{
    public AccountDialog(AccountDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.RequestClose += (_, _) => { DialogResult = true; Close(); };
    }
}
```

- [ ] **Step 4: AppHost'a AccountDialog kayıtları**

`AppHost.cs`'de "// Licensing dialogs (Phase 4b)" bloğunda mevcut LoginDialog kayıtlarının yanına ekle:

```csharp
        services.AddTransient<ViewModels.AccountDialogViewModel>();
        services.AddTransient<Views.AccountDialog>();
```

- [ ] **Step 5: MainShellViewModel'e licensing entegre et**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/ViewModels/MainShellViewModel.cs` dosyasını aç. Üst tarafa using ekle:

```csharp
using LiveDeck.Licensing;
using LiveDeck.Licensing.Services;
using System.Windows.Media;
```

Constructor parametresine `LicenseService licenseService` ekle, alana ata:

```csharp
    private readonly LicenseService _licenseService;
```

Atama:

```csharp
        _licenseService = licenseService;
        _licenseService.StatusChanged += OnLicenseStatusChanged;
        UpdateLicenseUiFromService();
```

`UpdateLicenseUiFromService()` metodunu ve event handler'ı sınıfa ekle:

```csharp
    [ObservableProperty] private bool _isLicenseWritable = true;
    [ObservableProperty] private string _licenseStatusText = "";
    [ObservableProperty] private Brush _licenseStatusBrush = Brushes.Gray;

    private void OnLicenseStatusChanged(object? sender, LicenseStatus status)
    {
        // Marshal to UI thread
        if (System.Windows.Application.Current?.Dispatcher is { } d && !d.CheckAccess())
        {
            d.InvokeAsync(UpdateLicenseUiFromService);
            return;
        }
        UpdateLicenseUiFromService();
    }

    private void UpdateLicenseUiFromService()
    {
        var status = _licenseService.CurrentStatus;
        IsLicenseWritable = status.IsWritable();
        (LicenseStatusText, LicenseStatusBrush) = status switch
        {
            LicenseStatus.Active        => ($"Lisans aktif — {_licenseService.CurrentLicense?.RemainingDaysAtLastCheck ?? 0} gün",
                                             (Brush)Brushes.SeaGreen),
            LicenseStatus.OfflineGrace  => ("Çevrimdışı (grace)", Brushes.Goldenrod),
            LicenseStatus.OfflineExpired or LicenseStatus.ExpiredOnline or LicenseStatus.Revoked
                                        => ("Lisans gerekli", Brushes.Crimson),
            LicenseStatus.NoLicense     => ("Lisans yok", Brushes.Gray),
            _                           => ("Başlatılıyor", Brushes.Gray)
        };
    }
```

Mevcut RelayCommand'ları (PrintCommand, MultiPrintCommand, vs.) — bunların `CanExecute`'ünde `IsLicenseWritable` koşulu olmalı. Mevcut kod genelde lambda olarak yazılmıştır:

```csharp
PrintCommand = new RelayCommand(DoPrint, () => CanPrint && IsLicenseWritable);
```

İlgili tüm yazma komutlarına `&& IsLicenseWritable` ekle:
- `PrintCommand`, `MultiPrintCommand` (eğer varsa farklı isim, mevcut adı koru)
- "Müşteri ekle" / "Add to queue" gibi yazma komutları
- Chat ingest start (eğer command olarak expose edilmişse)
- Giveaway create / draw komutları

Eğer komut `CanExecute` lambda'sı yoksa, `IsLicenseWritable` değiştiğinde `CanExecuteChanged`'i tetiklemek için `OnIsLicenseWritableChanged` partial method'unu kullan:

```csharp
    partial void OnIsLicenseWritableChanged(bool value)
    {
        // Refresh CanExecute on commands that depend on license state
        (PrintCommand as IRelayCommand)?.NotifyCanExecuteChanged();
        // ... vs other write commands
    }
```

`IRelayCommand` `using CommunityToolkit.Mvvm.Input;` altında.

- [ ] **Step 6: AccountDialog'u açan komut**

MainShellViewModel'e `OpenAccountCommand` ekle:

```csharp
    public IAsyncRelayCommand OpenAccountCommand { get; }
```

Constructor'da:

```csharp
        OpenAccountCommand = new AsyncRelayCommand(OpenAccountAsync);
```

Ve metot:

```csharp
    private async Task OpenAccountAsync()
    {
        await Task.Yield(); // ensure UI thread
        var dlg = global::LiveDeck.App.App.Host.Services.GetRequiredService<global::LiveDeck.App.Views.AccountDialog>();
        dlg.Owner = System.Windows.Application.Current.MainWindow;
        dlg.ShowDialog();
    }
```

`using Microsoft.Extensions.DependencyInjection;` eklemen gerekebilir.

- [ ] **Step 7: MainShellView.xaml'a status indicator + Hesap menü item ekle**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/Views/MainShellView.xaml`'i aç. Top bar'da mevcut başlık/menü yanına yeni TextBlock + ⋮ menü item ekle.

Mevcut top bar bölümünü bul (genelde `<Grid>` veya `<DockPanel>`'in üst satırında). Status indicator için (örnek konum — mevcut top bar yapısına göre adapte):

```xml
        <TextBlock Text="{Binding LicenseStatusText}"
                   Foreground="{Binding LicenseStatusBrush}"
                   FontWeight="SemiBold"
                   VerticalAlignment="Center"
                   Margin="12,0"
                   Cursor="Hand"
                   ToolTip="Hesap detayları için tıkla">
            <TextBlock.InputBindings>
                <MouseBinding MouseAction="LeftClick" Command="{Binding OpenAccountCommand}"/>
            </TextBlock.InputBindings>
        </TextBlock>
```

⋮ menüsünde "Hesap" MenuItem ekle (mevcut menü item'larının yanına):

```xml
        <MenuItem Header="Hesap" Command="{Binding OpenAccountCommand}"/>
```

- [ ] **Step 8: Build + test**

```bash
dotnet build LiveDeck.App 2>&1 | tail -10
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Beklenen: 0 errors. 121/121 testler hâlâ pass.

**Eğer MainShellViewModel constructor parametresi eklenince DI çözümleyemezse:** AppHost.cs'de `services.AddSingleton<ViewModels.MainShellViewModel>()` kaydını gözden geçir; CTOR'da yeni dependency `LicenseService` zaten registered olduğu için DI otomatik çözer. Mevcut MainShellViewModel'i resolve eden test (LicenseDiTests) bunu doğrular.

- [ ] **Step 9: Commit**

```bash
git add LiveDeck.App/ViewModels/AccountDialogViewModel.cs LiveDeck.App/ViewModels/MainShellViewModel.cs LiveDeck.App/Views/AccountDialog.xaml LiveDeck.App/Views/AccountDialog.xaml.cs LiveDeck.App/Views/MainShellView.xaml LiveDeck.App/AppHost.cs
git commit -m "feat(app): add AccountDialog + license status indicator + soft-gate bindings"
```

---

### Task 13: App.xaml.cs startup integration

**Files:**
- Modify: `LiveDeck.App/App.xaml.cs`

**Context:** Son entegrasyon adımı. OnStartup'ta LicenseService.InitializeAsync() çağrısı, NoLicense ise LoginDialog modal göster (cancel = app exit), sonra normal MainShell akışı. HeartbeatHostedService `IHostedService` olarak DI'da var ama `Microsoft.Extensions.Hosting.IHost` builder kullanmıyoruz; manuel `StartAsync` çağırırız OnStartup içinde, OnExit'te `StopAsync`.

- [ ] **Step 1: App.xaml.cs'i güncelle**

`C:/Users/burak/source/repos/LiveDeck/LiveDeck.App/App.xaml.cs` mevcut içeriği, `OnStartup` içinde `Host = new AppHost()` satırından sonra ve `_overlay = ...` satırından önce yeni licensing init ekle:

Üst tarafa using ekle:

```csharp
using LiveDeck.App.Views;
using LiveDeck.App.ViewModels;
using LiveDeck.Licensing;
using LiveDeck.Licensing.Services;
using Microsoft.Extensions.Hosting;
```

`OnStartup` metodunun gövdesini (mevcut content'i koruyarak) şu şekilde değiştir:

```csharp
    private LiveDeck.Licensing.Services.HeartbeatHostedService? _heartbeat;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Lock culture to tr-TR (mevcut kod)
        var tr = TrFormats.TR;
        Thread.CurrentThread.CurrentCulture = tr;
        Thread.CurrentThread.CurrentUICulture = tr;
        CultureInfo.DefaultThreadCurrentCulture = tr;
        CultureInfo.DefaultThreadCurrentUICulture = tr;
        FrameworkElement.LanguageProperty.OverrideMetadata(
            typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(tr.IetfLanguageTag)));

        Host = new AppHost();

        var logger = Host.Services.GetRequiredService<ILogger<App>>();
        logger.LogInformation("LiveDeck starting up");

        // Phase 4b: license bootstrap before showing main window
        var licenseService = Host.Services.GetRequiredService<LicenseService>();
        try
        {
            licenseService.InitializeAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "License initialization failed");
        }

        if (licenseService.CurrentStatus == LicenseStatus.NoLicense)
        {
            var loginDlg = Host.Services.GetRequiredService<LoginDialog>();
            var ok = loginDlg.ShowDialog();
            if (ok != true)
            {
                Shutdown();
                return;
            }
        }

        _overlay  = Host.Services.GetRequiredService<OverlayHost>();
        _ingestor = Host.Services.GetRequiredService<ChatBridgeIngestor>();

        _ = _overlay.StartAsync();
        _ = _ingestor.StartAsync(CancellationToken.None);

        // Heartbeat manual lifecycle (no IHost builder)
        _heartbeat = Host.Services.GetServices<IHostedService>()
            .OfType<HeartbeatHostedService>()
            .FirstOrDefault();
        _ = _heartbeat?.StartAsync(CancellationToken.None);

        base.OnStartup(e);
    }
```

`using System.Linq;` ekle (eğer yoksa).

`OnExit` metodunda `_heartbeat?.StopAsync(...)` ekle:

```csharp
    protected override void OnExit(ExitEventArgs e)
    {
        try { _heartbeat?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { _ingestor?.StopAsync(CancellationToken.None).GetAwaiter().GetResult(); } catch { }
        try { _overlay?.StopAsync().GetAwaiter().GetResult(); } catch { }
        Host.Dispose();
        base.OnExit(e);
    }
```

- [ ] **Step 2: Final build + tüm test paketleri**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
```

Beklenen: 0 errors, 0 warnings.

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
dotnet test LiveDeck.Licensing.Tests 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Beklenen toplam:
- LiveDeck.Tests: 121/121 (117 baseline + 4 DI)
- LiveDeck.Licensing.Tests: 54/54
- LiveDeck.LicenseServer.Tests: 62/62 (61 baseline + 1 me/licenses)
- Toplam: 237/237

Spec'teki ~212 hedefi aşıldı (LicenseService state machine için planlanan testler 11 yerine 11 + Heartbeat 2 + DI 4 = 17 oldu — kabul kriterinde ≥ olduğu için sorun yok).

- [ ] **Step 3: Manuel smoke (ön-koşul: Phase 4a server local'de çalışıyor)**

Spec'teki 14 maddelik manuel smoke planını sırasıyla uygula. Notlar:

1. `%LOCALAPPDATA%\LiveDeck\auth.dat` ve `license.dat` varsa sil (fresh start).
2. Server'ı `dotnet run --project LiveDeck.LicenseServer` ile başlat (DiskEmailSender dev mode).
3. Env var ile dev URL: `LIVEDECK_LICENSE_BASE_URL=https://localhost:5001`. PowerShell'de:
   ```powershell
   $env:LIVEDECK_LICENSE_BASE_URL = "https://localhost:5001"
   ```
4. App'i Visual Studio veya `dotnet run --project LiveDeck.App` ile başlat.
5. LoginDialog açılır → Register → Confirm akışı.
6. Test detayları için: `docs/superpowers/specs/2026-04-29-phase-4b-client-licensing-design.md` → §10 Manuel Smoke Plan.

Smoke check pass/fail durumunu commit message'a yansıtma; sadece kabul kriterleri için.

- [ ] **Step 4: Final commit**

```bash
git add LiveDeck.App/App.xaml.cs
git commit -m "feat(app): wire license bootstrap + heartbeat lifecycle into App startup"
```

---

## Self-Review

**1. Spec coverage:**
- §2 Mimari (proje yapısı, bağımlılık akışı): Task 2 + 10
- §3 Hardware fingerprint (SHA-256 + WMI/Registry): Task 3
- §4 Encrypted storage (DPAPI, format, tamper, logout): Task 4 + 5
- §5 State machine (Active/OfflineGrace/etc.): Task 8 (LicenseService) + Task 2 (enum)
- §5.1 API Client + 8 exception class: Task 6
- §5.3 Network policy (timeout, retry): Task 6 (timeout via HttpClient.Timeout); retry/backoff DEFERRED — `LicenseApiClient` tek deneme yapıyor. Spec'te 3 retry / exponential backoff bahsediliyor ama bu plan'da YAGNI olarak skip edildi (heartbeat fail = grace decision yapılır, retry'a gerek yok). Spec'i güncellemeden plan'da skip etmek tutarsız. **Plan'a notu ekle:** Heartbeat retry, ek karmaşıklık ve mevcut test/UX akışına değer eklemediği için bu fazda yok; bir sonraki release'de gerekli görülürse `LicenseApiClient.HeartbeatAsync` içine `Polly` veya manuel exponential backoff eklenebilir. (Task 9'un context'inde belirtildi.)
- §6 UI (LoginDialog 4 mode + AccountDialog + indicator + soft-gate): Task 11 + 12
- §7 DI + LicensingOptions: Task 10
- §8 Server patch (GET /me/licenses): Task 1
- §9 Test stratejisi: Task 3-9 (Licensing.Tests) + Task 10 (DI tests) + Task 1 (server test)
- §10 Manuel smoke: Task 13 referansı
- §11 YAGNI ve §13 kabul kriterleri: implicit, plan'a yansıdı

**2. Placeholder scan:** "TBD"/"TODO"/vague handler yok. Hata mesajları concrete (Türkçe).

**3. Type consistency:**
- `LicenseStatus` enum (Task 2) — Task 8 tüm değerleri kullanıyor ✓
- `IsWritable()` extension — Task 12 binding'inde kullanılıyor ✓
- `LicenseRecord` 7 alan — Task 5 oluşturur, Task 8 kullanır, alan adları tutarlı ✓
- `AuthRecord` 5 alan — Task 5 oluşturur, Task 7 + 8 kullanır ✓
- `LicenseApiClient.SetAuthToken(string?)` (Task 6) — Task 7 + 8'de kullanılıyor ✓
- `LicenseService.RefreshAsync` — Task 9 hosted service'inde çağrılır ✓
- `IHardwareIdProvider` (Task 3) — Task 8 + 10 DI'da kullanılır ✓

**4. Server patch position:** Task 1 olarak ilk sıraya koyuldu çünkü Task 7 (LoginService.GetMyLicensesAsync) ve Task 11 (LoginDialog auto-activate) bu endpoint'e bağımlı.

**5. Deviations from spec:**
- WireMock.Net yerine custom FakeHttpMessageHandler (lighter, no new dep)
- appsettings.json yerine env var override + hardcoded defaults (mevcut codebase pattern'i ile uyumlu)
- Heartbeat retry/exponential backoff YAGNI

---

## Execution Handoff

**Plan complete and saved to `docs/superpowers/plans/2026-04-29-phase-4b-client-licensing.md`.**

13 task, ~3500 satır. Phase 4a ile aynı pattern (TDD, frequent commits). Test hedefi 178 → ~237.

İki yürütme seçeneği:

**1. Subagent-Driven (önerilen)** — Her task için fresh subagent dispatch ediyorum, her task sonrası kısa rapor + sonraki task. Phase 4a'da 15 task'ı bu şekilde tamamladık.

**2. Inline Execution** — executing-plans skill ile bu session'da batch yürütme.

Hangisi?

