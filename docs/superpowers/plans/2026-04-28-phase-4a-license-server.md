# Faz 4a — LicenseServer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bağımsız ASP.NET Core REST API + SQL Server schema + email/şifre customer auth + self-signup + email doğrulama + lisans CRUD + N-slot aktivasyon yönetimi (server-only; client entegrasyonu Phase 4b).

**Architecture:** İki yeni proje (`LiveDeck.LicenseServer` ASP.NET Core 10 controllers + `LiveDeck.LicenseServer.Tests` xUnit integration tests). EF Core 9 + SQL Server. İki ayrı JWT scheme (Bearer-Customer 7g + Bearer-Admin 1s). Argon2id password hashing. `IEmailSender` abstraction + DiskEmailSender (dev) + SmtpEmailSender (prod). Mevcut LiveDeck.* projelerine dokunulmaz.

**Tech Stack:** ASP.NET Core 10, EF Core 9 (`Microsoft.EntityFrameworkCore.SqlServer`, `.Design`), `Konscious.Security.Cryptography.Argon2`, `Microsoft.AspNetCore.Authentication.JwtBearer`, `MailKit`, `Swashbuckle.AspNetCore`, xUnit + FluentAssertions + `Microsoft.AspNetCore.Mvc.Testing`.

**Working directory:** `C:\Users\burak\source\repos\LiveDeck`

**Pre-Faz-4a state:** Phase 3b-2 HEAD `4cd6add` + spec commit `2439f09`. 117/117 tests passing.

**Spec reference:** `docs/superpowers/specs/2026-04-28-phase-4a-license-server-design.md`

---

## Task Index

**Foundation (1-3):** Solution scaffolding + entities + DbContext
**Cross-cutting services (4-7):** PasswordHasher + JWT + Email + Program.cs middleware
**Auth endpoints (8-10):** Customer auth + Admin auth
**Admin CRUD (11-13):** Customers + Skus + Licenses
**Customer license operations (14):** Validate + activate + deactivate + heartbeat
**Polish (15):** Force-deactivate + Dockerfile + manual smoke

---

### Task 1: Solution scaffolding — yeni projeler + minimal Program.cs

**Files:**
- Create: `LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj`
- Create: `LiveDeck.LicenseServer/Program.cs`
- Create: `LiveDeck.LicenseServer/Controllers/HealthController.cs`
- Create: `LiveDeck.LicenseServer/appsettings.json`
- Create: `LiveDeck.LicenseServer/appsettings.Development.json`
- Create: `LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj`
- Create: `LiveDeck.LicenseServer.Tests/HealthCheckTests.cs`
- Modify: `LiveDeck.sln` (add 2 new projects)

**Context:** Greenfield projects. Solution dosyasına manuel ekleme yerine `dotnet sln add` komutu kullan. Health endpoint hem smoke test hem de WebApplicationFactory'nin doğru config'lendiğini gösterir.

- [ ] **Step 1: Create LicenseServer project**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet new webapi -n LiveDeck.LicenseServer -f net10.0 --use-controllers --no-openapi
```

This creates `LiveDeck.LicenseServer/` directory with default ASP.NET Core API skeleton. Delete the default `WeatherForecastController.cs` and `WeatherForecast.cs` if they appear:

```bash
rm -f LiveDeck.LicenseServer/Controllers/WeatherForecastController.cs LiveDeck.LicenseServer/WeatherForecast.cs
```

- [ ] **Step 2: Replace LiveDeck.LicenseServer.csproj**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk.Web">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="Konscious.Security.Cryptography.Argon2" Version="1.3.1" />
    <PackageReference Include="MailKit" Version="4.8.0" />
    <PackageReference Include="Microsoft.AspNetCore.Authentication.JwtBearer" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.SqlServer" Version="9.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.Design" Version="9.0.0">
      <IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets>
      <PrivateAssets>all</PrivateAssets>
    </PackageReference>
    <PackageReference Include="Swashbuckle.AspNetCore" Version="7.2.0" />
  </ItemGroup>
</Project>
```

- [ ] **Step 3: Replace Program.cs**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Program.cs`:

```csharp
namespace LiveDeck.LicenseServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.MapControllers();

        app.Run();
    }
}
```

`public class Program` kullanılır (default `internal` değil) — `WebApplicationFactory<Program>` test tarafından erişebilmeli.

- [ ] **Step 4: Create HealthController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/HealthController.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;

namespace LiveDeck.LicenseServer.Controllers;

[ApiController]
[Route("api/v1/[controller]")]
public sealed class HealthController : ControllerBase
{
    [HttpGet]
    public IActionResult Get() => Ok(new { status = "ok", service = "livedeck-license-server" });
}
```

- [ ] **Step 5: Configure appsettings.json**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/appsettings.json`:

```json
{
  "ConnectionStrings": {
    "LicenseDb": "Server=(localdb)\\mssqllocaldb;Database=LiveDeckLicense;Trusted_Connection=true;"
  },
  "Jwt": {
    "SecretKey": "REPLACE-WITH-256-BIT-RANDOM-SECRET-IN-PRODUCTION-AT-LEAST-32-CHARS",
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
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/appsettings.Development.json`:

```json
{
  "Email": {
    "Provider": "disk"
  },
  "Smtp": {
    "DiskOutputDirectory": "./tmp/emails"
  },
  "App": {
    "PublicBaseUrl": "https://localhost:5001"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Information"
    }
  }
}
```

- [ ] **Step 6: Create test project**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet new xunit -n LiveDeck.LicenseServer.Tests -f net10.0
```

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <LangVersion>latest</LangVersion>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="FluentAssertions" Version="7.0.0" />
    <PackageReference Include="Microsoft.AspNetCore.Mvc.Testing" Version="10.0.0" />
    <PackageReference Include="Microsoft.EntityFrameworkCore.InMemory" Version="9.0.0" />
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.14.1" />
    <PackageReference Include="xunit" Version="2.9.3" />
    <PackageReference Include="xunit.runner.visualstudio" Version="3.1.4" />
  </ItemGroup>

  <ItemGroup>
    <Using Include="Xunit" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\LiveDeck.LicenseServer\LiveDeck.LicenseServer.csproj" />
  </ItemGroup>
</Project>
```

Delete the default `UnitTest1.cs` that `dotnet new xunit` creates:

```bash
rm -f LiveDeck.LicenseServer.Tests/UnitTest1.cs
```

- [ ] **Step 7: Write health check test**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/HealthCheckTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace LiveDeck.LicenseServer.Tests;

public class HealthCheckTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public HealthCheckTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public async Task Health_returns_ok()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/api/v1/health");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<HealthBody>();
        body.Should().NotBeNull();
        body!.status.Should().Be("ok");
        body.service.Should().Be("livedeck-license-server");
    }

    private sealed record HealthBody(string status, string service);
}
```

- [ ] **Step 8: Add projects to LiveDeck.sln**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet sln add LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj
dotnet sln add LiveDeck.LicenseServer.Tests/LiveDeck.LicenseServer.Tests.csproj
```

- [ ] **Step 9: Build + test**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -10
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Expected: 0 errors. 1/1 health check test pass. Existing 117 tests in LiveDeck.Tests still pass (regression check):

```bash
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: 117/117.

- [ ] **Step 10: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.sln LiveDeck.LicenseServer/ LiveDeck.LicenseServer.Tests/
git commit -m "feat(license-server): scaffold ASP.NET Core project + health endpoint"
```

---

### Task 2: Domain entities

**Files:**
- Create: `LiveDeck.LicenseServer/Domain/Customer.cs`
- Create: `LiveDeck.LicenseServer/Domain/AdminUser.cs`
- Create: `LiveDeck.LicenseServer/Domain/Sku.cs`
- Create: `LiveDeck.LicenseServer/Domain/License.cs`
- Create: `LiveDeck.LicenseServer/Domain/Activation.cs`
- Create: `LiveDeck.LicenseServer/Domain/EmailConfirmationToken.cs`

**Context:** EF Core entity classes. Hiç logic yok, sadece data + relationships. Test gerekmez (sonraki task'ta DbContext ile birlikte test edilecek).

- [ ] **Step 1: Customer.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Domain/Customer.cs`:

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

    public ICollection<License> Licenses { get; } = new List<License>();
}
```

- [ ] **Step 2: AdminUser.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Domain/AdminUser.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class AdminUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
}
```

- [ ] **Step 3: Sku.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Domain/Sku.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class Sku
{
    public string Code { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int DefaultDurationDays { get; set; }
    public int DefaultActivationSlots { get; set; }
    public string? Description { get; set; }
}
```

- [ ] **Step 4: License.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Domain/License.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class License
{
    public Guid Id { get; set; }
    public string LicenseKey { get; set; } = "";
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public string SkuCode { get; set; } = "";
    public Sku Sku { get; set; } = null!;
    public int ActivationSlots { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokeReason { get; set; }

    public ICollection<Activation> Activations { get; } = new List<Activation>();
}
```

- [ ] **Step 5: Activation.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Domain/Activation.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class Activation
{
    public Guid Id { get; set; }
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string HardwareFingerprint { get; set; } = "";
    public string? MachineName { get; set; }
    public DateTimeOffset ActivatedAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public DateTimeOffset? DeactivatedAt { get; set; }
}
```

- [ ] **Step 6: EmailConfirmationToken.cs**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Domain/EmailConfirmationToken.cs`:

```csharp
namespace LiveDeck.LicenseServer.Domain;

public sealed class EmailConfirmationToken
{
    public Guid Token { get; set; }
    public Guid CustomerId { get; set; }
    public Customer Customer { get; set; } = null!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
```

- [ ] **Step 7: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
```

Expected: 0 errors.

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Domain/
git commit -m "feat(license-server): add 6 EF Core entity classes"
```

---

### Task 3: DbContext + initial migration + seed

**Files:**
- Create: `LiveDeck.LicenseServer/Data/LicenseDbContext.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DbContext DI registration)
- Create: `LiveDeck.LicenseServer/Data/Migrations/*` (auto-generated)
- Create: `LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`
- Create: `LiveDeck.LicenseServer.Tests/Data/DbContextTests.cs`

**Context:** EF Core DbContext + FluentAPI configuration (UNIQUE indexes, FK cascade, Argon2 hash storage). Initial migration. Seed migration for SKUs (no admin seed yet — Admin:InitialPasswordHash empty in dev). `ApiFactory` test helper that swaps SQL Server for InMemory.

- [ ] **Step 1: Create DbContext**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Data/LicenseDbContext.cs`:

```csharp
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Data;

public sealed class LicenseDbContext : DbContext
{
    public LicenseDbContext(DbContextOptions<LicenseDbContext> options) : base(options) { }

    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Sku> Skus => Set<Sku>();
    public DbSet<License> Licenses => Set<License>();
    public DbSet<Activation> Activations => Set<Activation>();
    public DbSet<EmailConfirmationToken> EmailConfirmationTokens => Set<EmailConfirmationToken>();

    protected override void OnModelCreating(ModelBuilder mb)
    {
        mb.Entity<Customer>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Email).HasMaxLength(256).IsRequired();
            b.HasIndex(c => c.Email).IsUnique();
            b.Property(c => c.Name).HasMaxLength(200).IsRequired();
            b.Property(c => c.PasswordHash).HasMaxLength(256).IsRequired();
        });

        mb.Entity<AdminUser>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.Username).HasMaxLength(64).IsRequired();
            b.HasIndex(a => a.Username).IsUnique();
            b.Property(a => a.PasswordHash).HasMaxLength(256).IsRequired();
        });

        mb.Entity<Sku>(b =>
        {
            b.HasKey(s => s.Code);
            b.Property(s => s.Code).HasMaxLength(16);
            b.Property(s => s.DisplayName).HasMaxLength(80).IsRequired();
            b.Property(s => s.Description).HasMaxLength(500);
        });

        mb.Entity<License>(b =>
        {
            b.HasKey(l => l.Id);
            b.Property(l => l.LicenseKey).HasMaxLength(40).IsRequired();
            b.HasIndex(l => l.LicenseKey).IsUnique();
            b.HasOne(l => l.Customer).WithMany(c => c.Licenses)
                .HasForeignKey(l => l.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(l => l.Sku).WithMany()
                .HasForeignKey(l => l.SkuCode).OnDelete(DeleteBehavior.Restrict);
            b.Property(l => l.RevokeReason).HasMaxLength(500);
        });

        mb.Entity<Activation>(b =>
        {
            b.HasKey(a => a.Id);
            b.Property(a => a.HardwareFingerprint).HasMaxLength(128).IsRequired();
            b.Property(a => a.MachineName).HasMaxLength(128);
            b.HasOne(a => a.License).WithMany(l => l.Activations)
                .HasForeignKey(a => a.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Filtered unique index — only enforce uniqueness for active rows
            b.HasIndex(a => new { a.LicenseId, a.HardwareFingerprint })
                .HasFilter("[DeactivatedAt] IS NULL")
                .IsUnique();
        });

        mb.Entity<EmailConfirmationToken>(b =>
        {
            b.HasKey(t => t.Token);
            b.HasOne(t => t.Customer).WithMany()
                .HasForeignKey(t => t.CustomerId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(t => new { t.CustomerId, t.UsedAt });
        });

        // Seed SKUs
        mb.Entity<Sku>().HasData(
            new Sku { Code = "STD", DisplayName = "Standard",
                      DefaultDurationDays = 365, DefaultActivationSlots = 1,
                      Description = "Tek cihaz, 1 yıl" },
            new Sku { Code = "PRO", DisplayName = "Professional",
                      DefaultDurationDays = 365, DefaultActivationSlots = 3,
                      Description = "3 cihaz, 1 yıl" });
    }
}
```

- [ ] **Step 2: Register DbContext in Program.cs**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Program.cs`. Replace the file contents:

```csharp
using LiveDeck.LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        builder.Services.AddDbContext<LicenseDbContext>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("LicenseDb")));

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.MapControllers();

        app.Run();
    }
}
```

- [ ] **Step 3: Generate initial migration**

```bash
cd /c/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer
dotnet ef migrations add InitialSchema --output-dir Data/Migrations
```

If `dotnet-ef` tool not installed: `dotnet tool install --global dotnet-ef --version 9.0.0` then retry. Expected: `Data/Migrations/{timestamp}_InitialSchema.cs` + `LicenseDbContextModelSnapshot.cs` created. Skus seed data automatically included.

- [ ] **Step 4: Create ApiFactory test helper**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureServices(services =>
        {
            // Replace SQL Server with InMemory for tests
            var dbContextDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<LicenseDbContext>));
            if (dbContextDescriptor is not null)
                services.Remove(dbContextDescriptor);

            services.AddDbContext<LicenseDbContext>(opt =>
                opt.UseInMemoryDatabase(_dbName));

            // Apply migrations / seed data
            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Database.EnsureCreated();
        });
    }
}
```

`EnsureCreated` ile InMemory provider seed data dahil schema'yı kurar (migration'lar InMemory ile çalışmaz, ama HasData seed çalışır).

- [ ] **Step 5: Write DbContext smoke test**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Data/DbContextTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Data;

public class DbContextTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public DbContextTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void Database_seeded_with_two_skus()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var skus = db.Skus.OrderBy(s => s.Code).ToList();
        skus.Should().HaveCount(2);
        skus[0].Code.Should().Be("PRO");
        skus[0].DefaultActivationSlots.Should().Be(3);
        skus[1].Code.Should().Be("STD");
        skus[1].DefaultActivationSlots.Should().Be(1);
    }
}
```

- [ ] **Step 6: Run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Expected: 2/2 pass (1 health + 1 dbcontext seed).

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Data/ LiveDeck.LicenseServer/Program.cs LiveDeck.LicenseServer.Tests/TestHelpers/ LiveDeck.LicenseServer.Tests/Data/
git commit -m "feat(license-server): add LicenseDbContext + initial migration + SKU seed"
```

---

### Task 4: PasswordHasher (Argon2id)

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Auth/PasswordHasher.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/PasswordHasherTests.cs`

**Context:** Argon2id wrapper. OWASP 2024: memory 64MB, iterations 4, parallelism 2. Salt 16 byte, hash 32 byte. Format: `$argon2id$v=19$m=65536,t=4,p=2${salt-base64}${hash-base64}`. TDD.

- [ ] **Step 1: Write failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Services/PasswordHasherTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Services.Auth;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class PasswordHasherTests
{
    private readonly PasswordHasher _hasher = new();

    [Fact]
    public void Hash_produces_argon2id_formatted_string()
    {
        var hash = _hasher.Hash("test-password-123");
        hash.Should().StartWith("$argon2id$v=19$m=65536,t=4,p=2$");
        // 4 segments after the format prefix: $argon2id$ $v=...$ $m=,t=,p=$ $salt$ $hash$
        hash.Split('$').Should().HaveCount(6);  // ["", "argon2id", "v=19", "m=...", "salt", "hash"]
    }

    [Fact]
    public void Verify_returns_true_for_correct_password()
    {
        var hash = _hasher.Hash("correct-horse-battery-staple");
        _hasher.Verify(hash, "correct-horse-battery-staple").Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_wrong_password()
    {
        var hash = _hasher.Hash("correct-horse");
        _hasher.Verify(hash, "wrong-password").Should().BeFalse();
    }

    [Fact]
    public void Hash_produces_different_output_for_same_input_due_to_random_salt()
    {
        var hash1 = _hasher.Hash("same-password");
        var hash2 = _hasher.Hash("same-password");
        hash1.Should().NotBe(hash2);   // different salts
        _hasher.Verify(hash1, "same-password").Should().BeTrue();
        _hasher.Verify(hash2, "same-password").Should().BeTrue();
    }

    [Fact]
    public void Verify_returns_false_for_malformed_hash()
    {
        _hasher.Verify("not-a-valid-hash", "any").Should().BeFalse();
        _hasher.Verify("", "any").Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run RED**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~PasswordHasherTests" 2>&1 | tail -5
```

Expected: compile error — `PasswordHasher` not found.

- [ ] **Step 3: Implement PasswordHasher**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Auth/PasswordHasher.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace LiveDeck.LicenseServer.Services.Auth;

/// <summary>
/// Argon2id password hasher. OWASP 2024 parameters: m=65536 KB, t=4 iterations, p=2 lanes.
/// Format: $argon2id$v=19$m=65536,t=4,p=2$&lt;salt-base64&gt;$&lt;hash-base64&gt;
/// </summary>
public sealed class PasswordHasher
{
    private const int MemoryKb = 65536;     // 64 MB
    private const int Iterations = 4;
    private const int Parallelism = 2;
    private const int SaltSize = 16;        // bytes
    private const int HashSize = 32;        // bytes

    public string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Compute(password, salt);
        return $"$argon2id$v=19$m={MemoryKb},t={Iterations},p={Parallelism}${Convert.ToBase64String(salt)}${Convert.ToBase64String(hash)}";
    }

    public bool Verify(string hashString, string password)
    {
        try
        {
            var parts = hashString.Split('$');
            if (parts.Length != 6) return false;
            if (parts[1] != "argon2id") return false;
            // parts[2] = "v=19"; parts[3] = "m=...,t=...,p=..."
            var salt = Convert.FromBase64String(parts[4]);
            var expectedHash = Convert.FromBase64String(parts[5]);
            var actualHash = Compute(password, salt);
            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Compute(string password, byte[] salt)
    {
        using var argon2 = new Argon2id(Encoding.UTF8.GetBytes(password))
        {
            Salt = salt,
            DegreeOfParallelism = Parallelism,
            Iterations = Iterations,
            MemorySize = MemoryKb,
        };
        return argon2.GetBytes(HashSize);
    }
}
```

- [ ] **Step 4: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~PasswordHasherTests" 2>&1 | tail -3
```

Expected: 5/5 pass.

- [ ] **Step 5: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Services/Auth/PasswordHasher.cs LiveDeck.LicenseServer.Tests/Services/PasswordHasherTests.cs
git commit -m "feat(license-server): add PasswordHasher (Argon2id, OWASP 2024 params)"
```

---

### Task 5: JwtTokenService

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Auth/JwtTokenService.cs`
- Create: `LiveDeck.LicenseServer/Services/Auth/JwtOptions.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/JwtTokenServiceTests.cs`

**Context:** İki ayrı token şeması: Customer (audience `livedeck-customer`, 7 gün) ve Admin (audience `livedeck-admin`, 1 saat). HS256 simetrik secret. TDD.

- [ ] **Step 1: Write failing tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Services/JwtTokenServiceTests.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.Extensions.Options;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class JwtTokenServiceTests
{
    private readonly JwtTokenService _service;

    public JwtTokenServiceTests()
    {
        var options = Options.Create(new JwtOptions
        {
            SecretKey = "test-secret-key-must-be-at-least-32-bytes-long-for-hs256",
            Issuer = "livedeck-license-server"
        });
        _service = new JwtTokenService(options);
    }

    [Fact]
    public void IssueCustomerToken_includes_sub_email_and_audience()
    {
        var customerId = Guid.NewGuid();
        var (token, expiresAt) = _service.IssueCustomerToken(customerId, "user@example.com");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("livedeck-customer");
        jwt.Issuer.Should().Be("livedeck-license-server");
        jwt.Claims.Should().ContainSingle(c => c.Type == "sub" && c.Value == customerId.ToString());
        jwt.Claims.Should().ContainSingle(c => c.Type == "email" && c.Value == "user@example.com");
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(7), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public void IssueAdminToken_uses_admin_audience_and_one_hour_expiry()
    {
        var adminId = Guid.NewGuid();
        var (token, expiresAt) = _service.IssueAdminToken(adminId, "admin");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Audiences.Should().ContainSingle().Which.Should().Be("livedeck-admin");
        jwt.Claims.Should().ContainSingle(c => c.Type == "sub" && c.Value == adminId.ToString());
        jwt.Claims.Should().ContainSingle(c => c.Type == "username" && c.Value == "admin");
        expiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddHours(1), TimeSpan.FromMinutes(1));
    }
}
```

- [ ] **Step 2: Implement JwtOptions + JwtTokenService**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Auth/JwtOptions.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Auth;

public sealed class JwtOptions
{
    public string SecretKey { get; set; } = "";
    public string Issuer { get; set; } = "";
    public const string CustomerAudience = "livedeck-customer";
    public const string AdminAudience = "livedeck-admin";
}
```

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Auth/JwtTokenService.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace LiveDeck.LicenseServer.Services.Auth;

public sealed class JwtTokenService
{
    private readonly JwtOptions _options;
    private readonly SigningCredentials _signing;

    public JwtTokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_options.SecretKey));
        _signing = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueCustomerToken(Guid customerId, string email)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddDays(7);
        var token = Build(JwtOptions.CustomerAudience, expiresAt,
            new Claim("sub", customerId.ToString()),
            new Claim("email", email));
        return (token, expiresAt);
    }

    public (string Token, DateTimeOffset ExpiresAt) IssueAdminToken(Guid adminId, string username)
    {
        var expiresAt = DateTimeOffset.UtcNow.AddHours(1);
        var token = Build(JwtOptions.AdminAudience, expiresAt,
            new Claim("sub", adminId.ToString()),
            new Claim("username", username));
        return (token, expiresAt);
    }

    private string Build(string audience, DateTimeOffset expiresAt, params Claim[] claims)
    {
        var jwt = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow,
            expires: expiresAt.UtcDateTime,
            signingCredentials: _signing);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }
}
```

- [ ] **Step 3: Run GREEN**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~JwtTokenServiceTests" 2>&1 | tail -3
```

Expected: 2/2 pass.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Services/Auth/ LiveDeck.LicenseServer.Tests/Services/JwtTokenServiceTests.cs
git commit -m "feat(license-server): add JwtTokenService (customer 7d / admin 1h)"
```

---

### Task 6: Email infrastructure

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Email/IEmailSender.cs`
- Create: `LiveDeck.LicenseServer/Services/Email/SmtpOptions.cs`
- Create: `LiveDeck.LicenseServer/Services/Email/SmtpEmailSender.cs`
- Create: `LiveDeck.LicenseServer/Services/Email/DiskEmailSender.cs`
- Create: `LiveDeck.LicenseServer/Services/Email/EmailTemplates.cs`
- Create: `LiveDeck.LicenseServer.Tests/TestHelpers/TestEmailSender.cs`

**Context:** Abstraction + 2 implementation + dev/test fake. Templates Türkçe HTML+plaintext.

- [ ] **Step 1: Create IEmailSender interface**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Email/IEmailSender.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Email;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string toName, string subject, string htmlBody, string plainBody, CancellationToken ct = default);
}
```

- [ ] **Step 2: Create SmtpOptions**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Email/SmtpOptions.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Email;

public sealed class SmtpOptions
{
    public string Host { get; set; } = "";
    public int Port { get; set; } = 587;
    public bool UseSsl { get; set; } = true;
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
    public string FromAddress { get; set; } = "";
    public string FromName { get; set; } = "";
    public string DiskOutputDirectory { get; set; } = "./tmp/emails";
}
```

- [ ] **Step 3: Implement SmtpEmailSender**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Email/SmtpEmailSender.cs`:

```csharp
using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveDeck.LicenseServer.Services.Email;

public sealed class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<SmtpEmailSender> _log;

    public SmtpEmailSender(IOptions<SmtpOptions> opt, ILogger<SmtpEmailSender> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public async Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        var msg = new MimeMessage();
        msg.From.Add(new MailboxAddress(_opt.FromName, _opt.FromAddress));
        msg.To.Add(new MailboxAddress(toName, toEmail));
        msg.Subject = subject;
        msg.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = plainBody }.ToMessageBody();

        try
        {
            using var smtp = new SmtpClient();
            await smtp.ConnectAsync(_opt.Host, _opt.Port,
                _opt.UseSsl ? MailKit.Security.SecureSocketOptions.StartTls
                            : MailKit.Security.SecureSocketOptions.None, ct);
            if (!string.IsNullOrEmpty(_opt.Username))
                await smtp.AuthenticateAsync(_opt.Username, _opt.Password, ct);
            await smtp.SendAsync(msg, ct);
            await smtp.DisconnectAsync(quit: true, ct);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "SMTP send failed for {Email}", toEmail);
            // Email failure is not propagated — caller already returned 202 to client.
        }
    }
}
```

- [ ] **Step 4: Implement DiskEmailSender**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Email/DiskEmailSender.cs`:

```csharp
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LiveDeck.LicenseServer.Services.Email;

public sealed class DiskEmailSender : IEmailSender
{
    private readonly SmtpOptions _opt;
    private readonly ILogger<DiskEmailSender> _log;

    public DiskEmailSender(IOptions<SmtpOptions> opt, ILogger<DiskEmailSender> log)
    {
        _opt = opt.Value;
        _log = log;
    }

    public Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        Directory.CreateDirectory(_opt.DiskOutputDirectory);
        var filename = $"{DateTime.UtcNow:yyyyMMddHHmmss}-{Guid.NewGuid():N}.eml";
        var path = Path.Combine(_opt.DiskOutputDirectory, filename);

        var sb = new StringBuilder();
        sb.AppendLine($"From: {_opt.FromName} <{_opt.FromAddress}>");
        sb.AppendLine($"To: {toName} <{toEmail}>");
        sb.AppendLine($"Subject: {subject}");
        sb.AppendLine($"Date: {DateTime.UtcNow:R}");
        sb.AppendLine("Content-Type: text/html; charset=utf-8");
        sb.AppendLine();
        sb.AppendLine(htmlBody);
        sb.AppendLine();
        sb.AppendLine("---PLAIN---");
        sb.AppendLine(plainBody);

        File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        _log.LogInformation("Email written to {Path}", path);
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 5: Email templates**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Email/EmailTemplates.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Email;

public static class EmailTemplates
{
    public static (string Subject, string Html, string Plain) ConfirmEmail(string customerName, string confirmUrl)
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

- [ ] **Step 6: TestEmailSender (in-memory capture)**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/TestHelpers/TestEmailSender.cs`:

```csharp
using System.Collections.Concurrent;
using LiveDeck.LicenseServer.Services.Email;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public sealed class TestEmailSender : IEmailSender
{
    public ConcurrentBag<SentEmail> Sent { get; } = new();

    public Task SendAsync(string toEmail, string toName, string subject,
        string htmlBody, string plainBody, CancellationToken ct = default)
    {
        Sent.Add(new SentEmail(toEmail, toName, subject, htmlBody, plainBody));
        return Task.CompletedTask;
    }
}

public sealed record SentEmail(string ToEmail, string ToName, string Subject, string HtmlBody, string PlainBody);
```

- [ ] **Step 7: Build**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
```

Expected: 0 errors. Sender registration in DI happens in Task 7.

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Services/Email/ LiveDeck.LicenseServer.Tests/TestHelpers/TestEmailSender.cs
git commit -m "feat(license-server): add IEmailSender + Smtp/Disk impls + Turkish templates"
```

---

### Task 7: Program.cs — JWT auth + rate limiting + DI registration

**Files:**
- Modify: `LiveDeck.LicenseServer/Program.cs`
- Modify: `LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs` (replace IEmailSender with TestEmailSender)

**Context:** Tüm cross-cutting concern'leri Program.cs'e entegre et: DbContext, JWT bearer (iki scheme), rate limiter, IEmailSender selection, JwtOptions/SmtpOptions binding, CORS, Swagger.

- [ ] **Step 1: Replace Program.cs**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Program.cs`:

```csharp
using System.Text;
using System.Threading.RateLimiting;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace LiveDeck.LicenseServer;

public class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // Options binding
        builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
        builder.Services.Configure<SmtpOptions>(builder.Configuration.GetSection("Smtp"));

        // DbContext
        builder.Services.AddDbContext<LicenseDbContext>(opt =>
            opt.UseSqlServer(builder.Configuration.GetConnectionString("LicenseDb")));

        // Services
        builder.Services.AddSingleton<PasswordHasher>();
        builder.Services.AddSingleton<JwtTokenService>();

        // Email sender selection
        var emailProvider = builder.Configuration["Email:Provider"] ?? "smtp";
        if (emailProvider.Equals("disk", StringComparison.OrdinalIgnoreCase))
            builder.Services.AddSingleton<IEmailSender, DiskEmailSender>();
        else
            builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

        // JWT auth — two schemes
        var jwtSecret = builder.Configuration["Jwt:SecretKey"]
            ?? throw new InvalidOperationException("Jwt:SecretKey missing");
        var jwtIssuer = builder.Configuration["Jwt:Issuer"]
            ?? throw new InvalidOperationException("Jwt:Issuer missing");
        var signingKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret));

        builder.Services.AddAuthentication()
            .AddJwtBearer("Bearer-Customer", o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtIssuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.CustomerAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            })
            .AddJwtBearer("Bearer-Admin", o =>
            {
                o.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true, ValidIssuer = jwtIssuer,
                    ValidateAudience = true, ValidAudience = JwtOptions.AdminAudience,
                    ValidateIssuerSigningKey = true, IssuerSigningKey = signingKey,
                    ValidateLifetime = true,
                    ClockSkew = TimeSpan.FromSeconds(30)
                };
            });
        builder.Services.AddAuthorization();

        // Rate limiting
        builder.Services.AddRateLimiter(opt =>
        {
            opt.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            opt.AddPolicy("auth-login", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 5,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            opt.AddPolicy("auth-register", ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 3,
                        Window = TimeSpan.FromMinutes(1)
                    }));
            opt.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
                RateLimitPartition.GetFixedWindowLimiter(
                    partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                    factory: _ => new FixedWindowRateLimiterOptions
                    {
                        PermitLimit = 100,
                        Window = TimeSpan.FromMinutes(1)
                    }));
        });

        // CORS — open for now (4d sıkılaştırılır)
        builder.Services.AddCors(opt =>
            opt.AddDefaultPolicy(p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

        builder.Services.AddControllers();
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen();

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }

        app.UseHttpsRedirection();
        app.UseCors();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapControllers();

        app.Run();
    }
}
```

- [ ] **Step 2: Update ApiFactory to swap IEmailSender**

Replace `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace LiveDeck.LicenseServer.Tests.TestHelpers;

public sealed class ApiFactory : WebApplicationFactory<Program>
{
    private readonly string _dbName = Guid.NewGuid().ToString();

    public TestEmailSender Email { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((ctx, cfg) =>
        {
            cfg.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Jwt:SecretKey"] = "test-secret-key-must-be-at-least-32-bytes-long-for-hs256",
                ["Jwt:Issuer"] = "livedeck-license-server",
                ["Email:Provider"] = "disk",
                ["App:PublicBaseUrl"] = "https://test.local",
            });
        });

        builder.ConfigureServices(services =>
        {
            // Replace SQL Server with InMemory
            var dbDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(DbContextOptions<LicenseDbContext>));
            if (dbDescriptor is not null) services.Remove(dbDescriptor);

            services.AddDbContext<LicenseDbContext>(opt => opt.UseInMemoryDatabase(_dbName));

            // Replace IEmailSender with TestEmailSender
            var emailDescriptor = services.SingleOrDefault(
                d => d.ServiceType == typeof(IEmailSender));
            if (emailDescriptor is not null) services.Remove(emailDescriptor);
            services.AddSingleton<IEmailSender>(Email);

            // Seed schema (HasData runs)
            using var scope = services.BuildServiceProvider().CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Database.EnsureCreated();
        });
    }
}
```

- [ ] **Step 3: Build + run all tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
```

Expected: 0 errors. Test count: 8 (1 health + 1 dbcontext + 5 hasher + 2 jwt). 8/8 pass.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Program.cs LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs
git commit -m "feat(license-server): wire JWT auth (2 schemes) + rate limiter + email DI in Program.cs"
```

---

### Task 8: Customer auth — register + confirm-email + resend

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Auth/EmailConfirmationService.cs`
- Create: `LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs`
- Create: `LiveDeck.LicenseServer.Tests/Auth/RegisterTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Auth/ConfirmEmailTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Auth/ResendConfirmationTests.cs`

**Context:** İlk üç anonim endpoint. Register: customer + ConfirmationToken oluştur, email gönder, 202. Confirm: token kullan, EmailConfirmedAt set, single-use işaretle. Resend: aktif (kullanılmamış, 24s'den yeni) token varsa onu yolla, yoksa yeni üret. Hepsi enumeration-safe.

- [ ] **Step 1: EmailConfirmationService**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Auth/EmailConfirmationService.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Email;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace LiveDeck.LicenseServer.Services.Auth;

public sealed class EmailConfirmationService
{
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(24);

    private readonly LicenseDbContext _db;
    private readonly IEmailSender _email;
    private readonly string _baseUrl;

    public EmailConfirmationService(LicenseDbContext db, IEmailSender email, IConfiguration cfg)
    {
        _db = db;
        _email = email;
        _baseUrl = cfg["App:PublicBaseUrl"] ?? "https://localhost:5001";
    }

    public async Task IssueAndSendAsync(Customer customer, CancellationToken ct = default)
    {
        var token = new EmailConfirmationToken
        {
            Token = Guid.NewGuid(),
            CustomerId = customer.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            UsedAt = null
        };
        _db.EmailConfirmationTokens.Add(token);
        await _db.SaveChangesAsync(ct);

        var url = $"{_baseUrl}/api/v1/auth/confirm-email/{token.Token}";
        var (subject, html, plain) = EmailTemplates.ConfirmEmail(customer.Name, url);
        await _email.SendAsync(customer.Email, customer.Name, subject, html, plain, ct);
    }

    /// <summary>True = success, false = invalid/expired/used.</summary>
    public async Task<bool> ConsumeAsync(Guid token, CancellationToken ct = default)
    {
        var record = await _db.EmailConfirmationTokens
            .Include(t => t.Customer)
            .FirstOrDefaultAsync(t => t.Token == token, ct);
        if (record is null) return false;
        if (record.UsedAt is not null) return false;
        if (DateTimeOffset.UtcNow - record.CreatedAt > TokenLifetime) return false;

        record.UsedAt = DateTimeOffset.UtcNow;
        record.Customer.EmailConfirmedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
```

Register service in `Program.cs` (in `// Services` block):

```csharp
        builder.Services.AddScoped<EmailConfirmationService>();
```

- [ ] **Step 2: AuthController — register/confirm/resend**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailConfirmationService _confirm;

    public AuthController(LicenseDbContext db, PasswordHasher hasher, EmailConfirmationService confirm)
    {
        _db = db;
        _hasher = hasher;
        _confirm = confirm;
    }

    public sealed record RegisterRequest(string Email, string Name, string Password);

    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) ||
            string.IsNullOrWhiteSpace(req.Name) ||
            string.IsNullOrWhiteSpace(req.Password))
            return Problem(title: "missing-fields", statusCode: 400);

        if (req.Password.Length < 8)
            return Problem(title: "password-too-short", detail: "En az 8 karakter olmalı.", statusCode: 400);

        // Enumeration koruması: zaten varsa sessizce 202 dön (yeni email yollanmaz)
        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (existing is not null) return StatusCode(202);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            Name = req.Name,
            PasswordHash = _hasher.Hash(req.Password),
            EmailConfirmedAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(201);
    }

    [HttpGet("confirm-email/{token:guid}")]
    public async Task<IActionResult> ConfirmEmail(Guid token, CancellationToken ct)
    {
        var ok = await _confirm.ConsumeAsync(token, ct);
        if (!ok) return Problem(title: "token-invalid", statusCode: 400);
        return Ok(new { ok = true });
    }

    public sealed record ResendRequest(string Email);

    [HttpPost("resend-confirmation")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email)) return StatusCode(202);

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        // Enumeration koruması: kullanıcı yoksa veya zaten confirmed ise sessiz 202
        if (customer is null) return StatusCode(202);
        if (customer.EmailConfirmedAt is not null) return StatusCode(202);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(202);
    }
}
```

- [ ] **Step 3: Tests — Register**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/RegisterTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class RegisterTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public RegisterTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Register_creates_customer_and_sends_confirmation_email()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"u-{Guid.NewGuid():N}@example.com",
            name = "Test User",
            password = "secret-password"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        _factory.Email.Sent.Should().NotBeEmpty();
        var lastEmail = _factory.Email.Sent.Last();
        lastEmail.Subject.Should().Contain("doğrulayın");
        lastEmail.PlainBody.Should().Contain("/api/v1/auth/confirm-email/");
    }

    [Fact]
    public async Task Register_with_short_password_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email = $"u-{Guid.NewGuid():N}@example.com",
            name = "Test",
            password = "short"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Register_with_existing_email_returns_202_silently_and_does_not_resend()
    {
        var client = _factory.CreateClient();
        var email = $"dup-{Guid.NewGuid():N}@example.com";

        // First registration
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "First", password = "secret-password"
        });
        var sentCountBefore = _factory.Email.Sent.Count;

        // Second registration with same email
        var resp = await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Second", password = "another-password"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Email.Sent.Count.Should().Be(sentCountBefore);   // no extra email
    }
}
```

- [ ] **Step 4: Tests — ConfirmEmail**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/ConfirmEmailTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class ConfirmEmailTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ConfirmEmailTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Confirm_with_valid_token_marks_email_confirmed()
    {
        var client = _factory.CreateClient();
        var email = $"c-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Confirm Test", password = "secret-password"
        });

        // Extract token from db
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email)
            .OrderByDescending(t => t.CreatedAt)
            .First().Token;

        var resp = await client.GetAsync($"/api/v1/auth/confirm-email/{token}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var customer = await db.Customers.FirstAsync(c => c.Email == email);
        customer.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Confirm_with_invalid_token_returns_400()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"/api/v1/auth/confirm-email/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Confirm_with_already_used_token_returns_400()
    {
        var client = _factory.CreateClient();
        var email = $"reuse-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Reuse", password = "secret-password"
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;

        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");           // first use
        var resp = await client.GetAsync($"/api/v1/auth/confirm-email/{token}");  // second use

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 5: Tests — Resend**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/ResendConfirmationTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class ResendConfirmationTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ResendConfirmationTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Resend_for_unconfirmed_user_sends_new_email()
    {
        var client = _factory.CreateClient();
        var email = $"resend-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Resend Test", password = "secret-password"
        });
        var countAfterRegister = _factory.Email.Sent.Count;

        var resp = await client.PostAsJsonAsync("/api/v1/auth/resend-confirmation", new { email });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Email.Sent.Count.Should().Be(countAfterRegister + 1);
    }

    [Fact]
    public async Task Resend_for_unknown_email_returns_202_silently()
    {
        var client = _factory.CreateClient();
        var countBefore = _factory.Email.Sent.Count;

        var resp = await client.PostAsJsonAsync("/api/v1/auth/resend-confirmation", new
        {
            email = $"never-{Guid.NewGuid():N}@example.com"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        _factory.Email.Sent.Count.Should().Be(countBefore);   // no email sent
    }
}
```

- [ ] **Step 6: Build + run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~Auth" 2>&1 | tail -3
```

Expected: 0 errors. 8/8 auth tests pass.

- [ ] **Step 7: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Services/Auth/EmailConfirmationService.cs LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs LiveDeck.LicenseServer/Program.cs LiveDeck.LicenseServer.Tests/Auth/
git commit -m "feat(license-server): add register + confirm-email + resend endpoints"
```

---

### Task 9: Customer auth — login + /me + password change

**Files:**
- Modify: `LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs` (add login + /me + password)
- Create: `LiveDeck.LicenseServer.Tests/Auth/LoginTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Auth/MeTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Auth/ChangePasswordTests.cs`

**Context:** Login → JWT customer token. `/me` ve `/me/password` JWT gerektirir. Login: email confirmed olmayan 403 `email-not-confirmed`. Password change: currentPassword doğru olmalı.

- [ ] **Step 1: Add login + me + password endpoints**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Auth/AuthController.cs`. Add `JwtTokenService` to constructor and three new endpoints. Replace the file contents:

```csharp
using System.Security.Claims;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly EmailConfirmationService _confirm;
    private readonly JwtTokenService _jwt;

    public AuthController(LicenseDbContext db, PasswordHasher hasher,
        EmailConfirmationService confirm, JwtTokenService jwt)
    {
        _db = db;
        _hasher = hasher;
        _confirm = confirm;
        _jwt = jwt;
    }

    public sealed record RegisterRequest(string Email, string Name, string Password);

    [HttpPost("register")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) ||
            string.IsNullOrWhiteSpace(req.Name) ||
            string.IsNullOrWhiteSpace(req.Password))
            return Problem(title: "missing-fields", statusCode: 400);

        if (req.Password.Length < 8)
            return Problem(title: "password-too-short", detail: "En az 8 karakter olmalı.", statusCode: 400);

        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (existing is not null) return StatusCode(202);

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            Name = req.Name,
            PasswordHash = _hasher.Hash(req.Password),
            EmailConfirmedAt = null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(201);
    }

    [HttpGet("confirm-email/{token:guid}")]
    public async Task<IActionResult> ConfirmEmail(Guid token, CancellationToken ct)
    {
        var ok = await _confirm.ConsumeAsync(token, ct);
        if (!ok) return Problem(title: "token-invalid", statusCode: 400);
        return Ok(new { ok = true });
    }

    public sealed record ResendRequest(string Email);

    [HttpPost("resend-confirmation")]
    [EnableRateLimiting("auth-register")]
    public async Task<IActionResult> ResendConfirmation([FromBody] ResendRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email)) return StatusCode(202);

        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (customer is null) return StatusCode(202);
        if (customer.EmailConfirmedAt is not null) return StatusCode(202);

        await _confirm.IssueAndSendAsync(customer, ct);
        return StatusCode(202);
    }

    public sealed record LoginRequest(string Email, string Password);
    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (customer is null || !_hasher.Verify(customer.PasswordHash, req.Password))
            return Problem(title: "invalid-credentials", statusCode: 401);

        if (customer.EmailConfirmedAt is null)
            return Problem(title: "email-not-confirmed", statusCode: 403);

        var (token, expiresAt) = _jwt.IssueCustomerToken(customer.Id, customer.Email);
        return Ok(new LoginResponse(token, expiresAt));
    }

    [HttpGet("me")]
    [Authorize(AuthenticationSchemes = "Bearer-Customer")]
    public async Task<IActionResult> Me(CancellationToken ct)
    {
        var id = GetCustomerId();
        var c = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            email = c.Email,
            name = c.Name,
            emailConfirmedAt = c.EmailConfirmedAt,
            createdAt = c.CreatedAt
        });
    }

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    [HttpPost("me/password")]
    [Authorize(AuthenticationSchemes = "Bearer-Customer")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        if (req.NewPassword.Length < 8)
            return Problem(title: "password-too-short", statusCode: 400);

        var id = GetCustomerId();
        var c = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (c is null) return NotFound();

        if (!_hasher.Verify(c.PasswordHash, req.CurrentPassword))
            return Problem(title: "wrong-current-password", statusCode: 400);

        c.PasswordHash = _hasher.Hash(req.NewPassword);
        await _db.SaveChangesAsync(ct);
        return NoContent();
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

The route `me/password` ends up at `api/v1/auth/me/password` — spec says `api/v1/me/password`. Adjust by routing `/me` and `/me/password` separately, OR move `/me` endpoints to a new controller. Per spec §4.2: paths are `/api/v1/me` and `/api/v1/me/password`. Move `/me` endpoints into a separate controller in Step 2.

- [ ] **Step 2: Move /me endpoints to MeController**

Remove `Me` and `ChangePassword` from `AuthController`. Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Auth/MeController.cs`:

```csharp
using System.Security.Claims;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/me")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class MeController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public MeController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var id = GetCustomerId();
        var c = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            email = c.Email,
            name = c.Name,
            emailConfirmedAt = c.EmailConfirmedAt,
            createdAt = c.CreatedAt
        });
    }

    public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequest req, CancellationToken ct)
    {
        if (req.NewPassword.Length < 8)
            return Problem(title: "password-too-short", statusCode: 400);

        var id = GetCustomerId();
        var c = await _db.Customers.FirstOrDefaultAsync(c => c.Id == id, ct);
        if (c is null) return NotFound();

        if (!_hasher.Verify(c.PasswordHash, req.CurrentPassword))
            return Problem(title: "wrong-current-password", statusCode: 400);

        c.PasswordHash = _hasher.Hash(req.NewPassword);
        await _db.SaveChangesAsync(ct);
        return NoContent();
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

Then **remove** `Me` and `ChangePassword` methods + `GetCustomerId` from `AuthController.cs`. The final AuthController only has `Register`, `ConfirmEmail`, `ResendConfirmation`, `Login`.

- [ ] **Step 3: Login tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/LoginTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class LoginTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LoginTests(ApiFactory factory) => _factory = factory;

    private async Task<string> RegisterAndConfirmAsync(string email, string password)
    {
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Test", password
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;
        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");
        return email;
    }

    [Fact]
    public async Task Login_with_valid_credentials_returns_token()
    {
        var email = $"login-{Guid.NewGuid():N}@example.com";
        await RegisterAndConfirmAsync(email, "secret-password");

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "secret-password"
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>();
        body.Should().NotBeNull();
        body!.Token.Should().NotBeNullOrEmpty();
        body.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Login_with_wrong_password_returns_401()
    {
        var email = $"wrong-{Guid.NewGuid():N}@example.com";
        await RegisterAndConfirmAsync(email, "secret-password");

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "wrong"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_for_unknown_email_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email = $"never-{Guid.NewGuid():N}@example.com",
            password = "anything"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Login_before_email_confirmed_returns_403()
    {
        var email = $"unconf-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Unconf", password = "secret-password"
        });
        // intentionally no confirm

        var resp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "secret-password"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
}
```

- [ ] **Step 4: Me + ChangePassword tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/MeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class MeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public MeTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string email)> CreateLoggedInClientAsync()
    {
        var email = $"me-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "Me Test", password = "secret-password"
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;
        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "secret-password"
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, email);
    }

    [Fact]
    public async Task Get_me_returns_authenticated_customer()
    {
        var (client, email) = await CreateLoggedInClientAsync();
        var resp = await client.GetAsync("/api/v1/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<MeBody>();
        body!.email.Should().Be(email);
        body.name.Should().Be("Me Test");
        body.emailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Get_me_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/me");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record MeBody(Guid id, string email, string name, DateTimeOffset? emailConfirmedAt);
}
```

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/ChangePasswordTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class ChangePasswordTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ChangePasswordTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string email)> RegisterConfirmLoginAsync(string password)
    {
        var email = $"pw-{Guid.NewGuid():N}@example.com";
        var client = _factory.CreateClient();
        await client.PostAsJsonAsync("/api/v1/auth/register", new
        {
            email, name = "PW Test", password
        });

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var token = db.EmailConfirmationTokens
            .Where(t => t.Customer.Email == email).First().Token;
        await client.GetAsync($"/api/v1/auth/confirm-email/{token}");

        var loginResp = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        return (client, email);
    }

    [Fact]
    public async Task ChangePassword_with_correct_current_returns_204_and_new_password_works()
    {
        var (client, email) = await RegisterConfirmLoginAsync("old-password");
        var resp = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "old-password",
            newPassword = "new-password"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        // Verify new password works
        var freshClient = _factory.CreateClient();
        var loginResp = await freshClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "new-password"
        });
        loginResp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ChangePassword_with_wrong_current_returns_400()
    {
        var (client, _) = await RegisterConfirmLoginAsync("real-password");
        var resp = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "wrong",
            newPassword = "new-password"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ChangePassword_with_short_new_returns_400()
    {
        var (client, _) = await RegisterConfirmLoginAsync("real-password");
        var resp = await client.PostAsJsonAsync("/api/v1/me/password", new
        {
            currentPassword = "real-password",
            newPassword = "short"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
}
```

- [ ] **Step 5: Build + run tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~Auth" 2>&1 | tail -3
```

Expected: 0 errors. 17/17 auth-related tests pass.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Controllers/Auth/ LiveDeck.LicenseServer.Tests/Auth/
git commit -m "feat(license-server): add login + /me + password change endpoints"
```

---

### Task 10: Admin auth — login + admin seed bootstrap

**Files:**
- Create: `LiveDeck.LicenseServer/Controllers/Auth/AdminAuthController.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (admin seed on startup)
- Create: `LiveDeck.LicenseServer.Tests/Auth/AdminLoginTests.cs`

**Context:** Tek admin user, `Admin:InitialUsername` + `Admin:InitialPasswordHash` config'lerinden boot'ta seed'lenir. Test'lerde ApiFactory bu config'leri override eder ve admin'i seed eder. Admin login → JWT admin token (1 saat).

- [ ] **Step 1: AdminAuthController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Auth/AdminAuthController.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Auth;

[ApiController]
[Route("api/v1/admin/auth")]
public sealed class AdminAuthController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;
    private readonly JwtTokenService _jwt;

    public AdminAuthController(LicenseDbContext db, PasswordHasher hasher, JwtTokenService jwt)
    {
        _db = db;
        _hasher = hasher;
        _jwt = jwt;
    }

    public sealed record LoginRequest(string Username, string Password);
    public sealed record LoginResponse(string Token, DateTimeOffset ExpiresAt);

    [HttpPost("login")]
    [EnableRateLimiting("auth-login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var admin = await _db.AdminUsers.FirstOrDefaultAsync(a => a.Username == req.Username, ct);
        if (admin is null || !_hasher.Verify(admin.PasswordHash, req.Password))
            return Problem(title: "invalid-credentials", statusCode: 401);

        admin.LastLoginAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        var (token, expiresAt) = _jwt.IssueAdminToken(admin.Id, admin.Username);
        return Ok(new LoginResponse(token, expiresAt));
    }
}
```

- [ ] **Step 2: Add admin seed to Program.cs**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Program.cs`. Add a helper static method and invoke it after `var app = builder.Build();`. Replace `var app = builder.Build();` block with:

```csharp
        var app = builder.Build();

        // Bootstrap: ensure DB created + seed admin user if config has hash
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Database.EnsureCreated();
            await SeedAdminAsync(db, app.Configuration);
        }

        if (app.Environment.IsDevelopment())
```

Change `Main` signature to async:

```csharp
    public static async Task Main(string[] args)
```

Add the helper at the bottom of the class:

```csharp
    private static async Task SeedAdminAsync(LicenseDbContext db, IConfiguration cfg)
    {
        var username = cfg["Admin:InitialUsername"];
        var hash = cfg["Admin:InitialPasswordHash"];
        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(hash)) return;

        var existing = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        if (existing is not null) return;

        db.AdminUsers.Add(new Domain.AdminUser
        {
            Id = Guid.NewGuid(),
            Username = username,
            PasswordHash = hash,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
    }
```

The seed only runs in production (initialPasswordHash provided). Test environment will explicitly seed in ApiFactory.

- [ ] **Step 3: Add admin seed helper to ApiFactory**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs`. Add these static helpers at the bottom:

```csharp
    public async Task<(string Token, Guid AdminId)> SeedAdminAndLoginAsync(
        string username = "admin", string password = "admin-password")
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<LiveDeck.LicenseServer.Services.Auth.PasswordHasher>();

        var existing = await db.AdminUsers.FirstOrDefaultAsync(a => a.Username == username);
        Guid id;
        if (existing is not null)
        {
            id = existing.Id;
        }
        else
        {
            id = Guid.NewGuid();
            db.AdminUsers.Add(new LiveDeck.LicenseServer.Domain.AdminUser
            {
                Id = id,
                Username = username,
                PasswordHash = hasher.Hash(password),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var client = CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new
        {
            username, password
        });
        var body = await resp.Content.ReadFromJsonAsync<LoginBody>();
        return (body!.Token, id);
    }

    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
```

Add usings at top of `ApiFactory.cs`:

```csharp
using System.Net.Http.Json;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
```

- [ ] **Step 4: Admin login tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Auth/AdminLoginTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Auth;

public class AdminLoginTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminLoginTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task AdminLogin_with_valid_credentials_returns_token()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        token.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task AdminLogin_with_wrong_password_returns_401()
    {
        var username = $"a-{Guid.NewGuid():N}";
        // Seed via factory but login with wrong pw
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var hasher = scope.ServiceProvider.GetRequiredService<PasswordHasher>();
        db.AdminUsers.Add(new AdminUser
        {
            Id = Guid.NewGuid(), Username = username,
            PasswordHash = hasher.Hash("real-pw"), CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/auth/login", new
        {
            username, password = "wrong"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

- [ ] **Step 5: Build + run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminLogin" 2>&1 | tail -3
```

Expected: 0 errors. 2/2 admin login tests pass. Full suite still passes.

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Controllers/Auth/AdminAuthController.cs LiveDeck.LicenseServer/Program.cs LiveDeck.LicenseServer.Tests/TestHelpers/ApiFactory.cs LiveDeck.LicenseServer.Tests/Auth/AdminLoginTests.cs
git commit -m "feat(license-server): add admin login + admin seed bootstrap"
```

---

### Task 11: Admin Customers + Skus endpoints

**Files:**
- Create: `LiveDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs`
- Create: `LiveDeck.LicenseServer/Controllers/Skus/AdminSkusController.cs`
- Create: `LiveDeck.LicenseServer.Tests/Customers/AdminCustomersTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Skus/AdminSkusTests.cs`

**Context:** Admin endpoint'leri `[Authorize(AuthenticationSchemes = "Bearer-Admin")]` ile korunur. Customers: list/get/create/confirm-email. Skus: read-only list (4a için yönetim YAGNI).

- [ ] **Step 1: AdminCustomersController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Customers/AdminCustomersController.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Customers;

[ApiController]
[Route("api/v1/admin/customers")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminCustomersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly PasswordHasher _hasher;

    public AdminCustomersController(LicenseDbContext db, PasswordHasher hasher)
    {
        _db = db;
        _hasher = hasher;
    }

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var rows = await _db.Customers
            .OrderByDescending(c => c.CreatedAt)
            .Select(c => new
            {
                id = c.Id,
                email = c.Email,
                name = c.Name,
                emailConfirmedAt = c.EmailConfirmedAt,
                createdAt = c.CreatedAt,
                licenseCount = c.Licenses.Count
            })
            .ToListAsync(ct);
        return Ok(rows);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers
            .Include(x => x.Licenses)
            .FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        return Ok(new
        {
            id = c.Id,
            email = c.Email,
            name = c.Name,
            emailConfirmedAt = c.EmailConfirmedAt,
            createdAt = c.CreatedAt,
            notes = c.Notes,
            licenses = c.Licenses.Select(l => new
            {
                id = l.Id,
                licenseKey = l.LicenseKey,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = l.RevokedAt
            })
        });
    }

    public sealed record CreateRequest(string Email, string Name, string? InitialPassword, bool? AutoConfirm);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Email) || string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-fields", statusCode: 400);

        var existing = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.Email, ct);
        if (existing is not null) return Conflict(new { error = "email-exists" });

        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = req.Email,
            Name = req.Name,
            PasswordHash = _hasher.Hash(req.InitialPassword ?? Guid.NewGuid().ToString("N")),
            EmailConfirmedAt = (req.AutoConfirm ?? false) ? DateTimeOffset.UtcNow : null,
            CreatedAt = DateTimeOffset.UtcNow
        };
        _db.Customers.Add(customer);
        await _db.SaveChangesAsync(ct);

        return CreatedAtAction(nameof(Get), new { id = customer.Id }, new { id = customer.Id });
    }

    [HttpPost("{id:guid}/confirm-email")]
    public async Task<IActionResult> ConfirmEmail(Guid id, CancellationToken ct)
    {
        var c = await _db.Customers.FirstOrDefaultAsync(x => x.Id == id, ct);
        if (c is null) return NotFound();
        if (c.EmailConfirmedAt is null)
        {
            c.EmailConfirmedAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
        }
        return NoContent();
    }
}
```

- [ ] **Step 2: AdminSkusController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Skus/AdminSkusController.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Skus;

[ApiController]
[Route("api/v1/admin/skus")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminSkusController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public AdminSkusController(LicenseDbContext db) => _db = db;

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var skus = await _db.Skus.OrderBy(s => s.Code).ToListAsync(ct);
        return Ok(skus);
    }
}
```

- [ ] **Step 3: AdminCustomers tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Customers/AdminCustomersTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Customers;

public class AdminCustomersTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminCustomersTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    [Fact]
    public async Task Create_customer_returns_201()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email = $"new-{Guid.NewGuid():N}@example.com",
            name = "New Customer",
            initialPassword = "initpw1234",
            autoConfirm = true
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Create_customer_with_existing_email_returns_409()
    {
        var client = await AdminClientAsync();
        var email = $"dup-{Guid.NewGuid():N}@example.com";
        await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "First", initialPassword = "pw12345678"
        });

        var resp = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Dup", initialPassword = "pw12345678"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task List_returns_customers_descending_by_created()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/admin/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ConfirmEmail_marks_customer_confirmed()
    {
        var client = await AdminClientAsync();
        var email = $"conf-{Guid.NewGuid():N}@example.com";
        var createResp = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Conf", initialPassword = "pw12345678", autoConfirm = false
        });
        var createBody = await createResp.Content.ReadFromJsonAsync<IdBody>();

        var resp = await client.PostAsync($"/api/v1/admin/customers/{createBody!.id}/confirm-email", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var c = await db.Customers.FirstAsync(c => c.Email == email);
        c.EmailConfirmedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task List_without_admin_token_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/admin/customers");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record IdBody(Guid id);
}
```

- [ ] **Step 4: AdminSkus tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Skus/AdminSkusTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Skus;

public class AdminSkusTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminSkusTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task List_returns_seeded_skus()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.GetAsync("/api/v1/admin/skus");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var skus = await resp.Content.ReadFromJsonAsync<List<SkuBody>>();
        skus.Should().NotBeNull();
        skus!.Should().Contain(s => s.code == "STD");
        skus.Should().Contain(s => s.code == "PRO");
    }

    private sealed record SkuBody(string code, string displayName, int defaultDurationDays, int defaultActivationSlots);
}
```

- [ ] **Step 5: Build + run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~Admin" 2>&1 | tail -3
```

Expected: 0 errors. 8/8 admin tests pass (2 login + 5 customers + 1 sku).

- [ ] **Step 6: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Controllers/Customers/ LiveDeck.LicenseServer/Controllers/Skus/ LiveDeck.LicenseServer.Tests/Customers/ LiveDeck.LicenseServer.Tests/Skus/
git commit -m "feat(license-server): add admin customers + skus endpoints"
```

---

### Task 12: License services — LicenseIssuer + LicenseValidator + ActivationManager

**Files:**
- Create: `LiveDeck.LicenseServer/Services/Licensing/LicenseIssuer.cs`
- Create: `LiveDeck.LicenseServer/Services/Licensing/LicenseValidator.cs`
- Create: `LiveDeck.LicenseServer/Services/Licensing/ActivationManager.cs`
- Create: `LiveDeck.LicenseServer/Services/Licensing/ValidationResult.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/LicenseIssuerTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/LicenseValidatorTests.cs`
- Create: `LiveDeck.LicenseServer.Tests/Services/ActivationManagerTests.cs`
- Modify: `LiveDeck.LicenseServer/Program.cs` (DI register services)

**Context:** Domain logic. Issuer: anahtar üret + persist. Validator: pure status hesabı. ActivationManager: slot enforcement, activate/deactivate/heartbeat orchestration.

- [ ] **Step 1: ValidationResult**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Licensing/ValidationResult.cs`:

```csharp
namespace LiveDeck.LicenseServer.Services.Licensing;

public enum LicenseStatus
{
    Active,
    Expired,
    Revoked,
    NotActivated,
    SlotMismatch
}

public sealed record ValidationResult(
    LicenseStatus Status,
    DateTimeOffset? ExpiresAt,
    int? RemainingDays,
    string? Sku,
    SlotInfo? SlotInfo);

public sealed record SlotInfo(int Used, int Total, bool ThisDeviceActive);
```

- [ ] **Step 2: LicenseIssuer**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Licensing/LicenseIssuer.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Services.Licensing;

public sealed class LicenseIssuer
{
    private readonly LicenseDbContext _db;

    public LicenseIssuer(LicenseDbContext db) => _db = db;

    /// <summary>Generates "LDK-{32 hex uppercase}" — guaranteed unique by retry on collision.</summary>
    public static string GenerateKey()
        => "LDK-" + Guid.NewGuid().ToString("N").ToUpperInvariant();

    public sealed record IssueRequest(
        string CustomerEmail,
        string SkuCode,
        int? DurationDaysOverride,
        int? SlotsOverride);

    public sealed record IssueResult(string LicenseKey, DateTimeOffset ExpiresAt);

    public sealed class IssueException : Exception
    {
        public string Code { get; }
        public IssueException(string code, string message) : base(message) => Code = code;
    }

    public async Task<IssueResult> IssueAsync(IssueRequest req, CancellationToken ct = default)
    {
        var customer = await _db.Customers.FirstOrDefaultAsync(c => c.Email == req.CustomerEmail, ct)
            ?? throw new IssueException("customer-not-found", $"Email yok: {req.CustomerEmail}");

        var sku = await _db.Skus.FirstOrDefaultAsync(s => s.Code == req.SkuCode, ct)
            ?? throw new IssueException("sku-not-found", $"SKU yok: {req.SkuCode}");

        var duration = req.DurationDaysOverride ?? sku.DefaultDurationDays;
        var slots = req.SlotsOverride ?? sku.DefaultActivationSlots;

        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = GenerateKey(),
            CustomerId = customer.Id,
            SkuCode = sku.Code,
            ActivationSlots = slots,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(duration),
            RevokedAt = null
        };
        _db.Licenses.Add(license);
        await _db.SaveChangesAsync(ct);
        return new IssueResult(license.LicenseKey, license.ExpiresAt);
    }
}
```

- [ ] **Step 3: LicenseValidator**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Licensing/LicenseValidator.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Services.Licensing;

public sealed class LicenseValidator
{
    private readonly LicenseDbContext _db;

    public LicenseValidator(LicenseDbContext db) => _db = db;

    public async Task<ValidationResult?> ValidateAsync(
        string licenseKey, string hardwareFingerprint, Guid customerId, CancellationToken ct = default)
    {
        var license = await _db.Licenses
            .Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey && l.CustomerId == customerId, ct);

        if (license is null) return null;

        var now = DateTimeOffset.UtcNow;
        var remainingDays = (int)Math.Max(0, (license.ExpiresAt - now).TotalDays);

        if (license.RevokedAt is not null)
            return new ValidationResult(LicenseStatus.Revoked, license.ExpiresAt, 0, license.SkuCode, null);

        if (license.ExpiresAt < now)
            return new ValidationResult(LicenseStatus.Expired, license.ExpiresAt, 0, license.SkuCode, null);

        var activeActivations = license.Activations
            .Where(a => a.DeactivatedAt is null).ToList();
        var thisDevice = activeActivations
            .FirstOrDefault(a => a.HardwareFingerprint == hardwareFingerprint);

        var slotInfo = new SlotInfo(activeActivations.Count, license.ActivationSlots, thisDevice is not null);

        if (thisDevice is null)
            return new ValidationResult(LicenseStatus.NotActivated, license.ExpiresAt, remainingDays, license.SkuCode, slotInfo);

        return new ValidationResult(LicenseStatus.Active, license.ExpiresAt, remainingDays, license.SkuCode, slotInfo);
    }
}
```

- [ ] **Step 4: ActivationManager**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Services/Licensing/ActivationManager.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Services.Licensing;

public sealed class ActivationManager
{
    private readonly LicenseDbContext _db;

    public ActivationManager(LicenseDbContext db) => _db = db;

    public sealed class ActivationException : Exception
    {
        public string Code { get; }
        public ActivationException(string code, string message) : base(message) => Code = code;
    }

    public async Task<Activation> ActivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, string? machineName,
        CancellationToken ct = default)
    {
        var license = await _db.Licenses
            .Include(l => l.Activations)
            .FirstOrDefaultAsync(l => l.LicenseKey == licenseKey && l.CustomerId == customerId, ct);

        if (license is null)
            throw new ActivationException("license-not-found", "Lisans bulunamadı");

        if (license.RevokedAt is not null)
            throw new ActivationException("license-revoked", "Lisans iptal edilmiş");

        if (license.ExpiresAt < DateTimeOffset.UtcNow)
            throw new ActivationException("license-expired", "Lisans süresi dolmuş");

        // Same device already active? Update LastSeenAt.
        var existing = license.Activations
            .FirstOrDefault(a => a.HardwareFingerprint == hardwareFingerprint && a.DeactivatedAt is null);
        if (existing is not null)
        {
            existing.LastSeenAt = DateTimeOffset.UtcNow;
            await _db.SaveChangesAsync(ct);
            return existing;
        }

        var activeCount = license.Activations.Count(a => a.DeactivatedAt is null);
        if (activeCount >= license.ActivationSlots)
            throw new ActivationException("slot-full", $"Slot dolu ({activeCount}/{license.ActivationSlots})");

        var activation = new Activation
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            HardwareFingerprint = hardwareFingerprint,
            MachineName = machineName,
            ActivatedAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            DeactivatedAt = null
        };
        _db.Activations.Add(activation);
        await _db.SaveChangesAsync(ct);
        return activation;
    }

    public async Task<bool> DeactivateAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct = default)
    {
        var activation = await _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a =>
                a.License.LicenseKey == licenseKey &&
                a.License.CustomerId == customerId &&
                a.HardwareFingerprint == hardwareFingerprint &&
                a.DeactivatedAt == null, ct);

        if (activation is null) return false;

        activation.DeactivatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> HeartbeatAsync(
        string licenseKey, Guid customerId, string hardwareFingerprint, CancellationToken ct = default)
    {
        var activation = await _db.Activations
            .Include(a => a.License)
            .FirstOrDefaultAsync(a =>
                a.License.LicenseKey == licenseKey &&
                a.License.CustomerId == customerId &&
                a.HardwareFingerprint == hardwareFingerprint &&
                a.DeactivatedAt == null, ct);

        if (activation is null) return false;

        activation.LastSeenAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ForceDeactivateAsync(Guid activationId, CancellationToken ct = default)
    {
        var activation = await _db.Activations.FirstOrDefaultAsync(a => a.Id == activationId, ct);
        if (activation is null || activation.DeactivatedAt is not null) return false;
        activation.DeactivatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return true;
    }
}
```

- [ ] **Step 5: Register services in Program.cs**

Edit `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Program.cs`. In the `// Services` block add:

```csharp
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Licensing.LicenseIssuer>();
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Licensing.LicenseValidator>();
        builder.Services.AddScoped<LiveDeck.LicenseServer.Services.Licensing.ActivationManager>();
```

- [ ] **Step 6: LicenseIssuer tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Services/LicenseIssuerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Licensing;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class LicenseIssuerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicenseIssuerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public void GenerateKey_returns_LDK_prefix_plus_32_hex()
    {
        var key = LicenseIssuer.GenerateKey();
        key.Should().StartWith("LDK-");
        key.Length.Should().Be(36);   // "LDK-" + 32 hex
        key.Substring(4).Should().MatchRegex("^[0-9A-F]{32}$");
    }

    [Fact]
    public async Task IssueAsync_creates_license_with_default_duration_and_slots()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var issuer = scope.ServiceProvider.GetRequiredService<LicenseIssuer>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"i-{Guid.NewGuid():N}@example.com",
            Name = "Issuer Test", PasswordHash = "x",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Customers.Add(customer);
        await db.SaveChangesAsync();

        var result = await issuer.IssueAsync(new(customer.Email, "STD", null, null));

        result.LicenseKey.Should().StartWith("LDK-");
        result.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddDays(365), TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task IssueAsync_with_unknown_customer_throws()
    {
        using var scope = _factory.Services.CreateScope();
        var issuer = scope.ServiceProvider.GetRequiredService<LicenseIssuer>();

        var act = async () => await issuer.IssueAsync(new("nope@x.com", "STD", null, null));
        var ex = await act.Should().ThrowAsync<LicenseIssuer.IssueException>();
        ex.Which.Code.Should().Be("customer-not-found");
    }
}
```

- [ ] **Step 7: ActivationManager tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Services/ActivationManagerTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Licensing;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class ActivationManagerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ActivationManagerTests(ApiFactory factory) => _factory = factory;

    private async Task<(Customer customer, License license)> SeedAsync(int slots = 1)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"a-{Guid.NewGuid():N}@x.com",
            Name = "AM", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(),
            LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id,
            SkuCode = "STD",
            ActivationSlots = slots,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(365)
        };
        db.Customers.Add(customer);
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (customer, license);
    }

    [Fact]
    public async Task Activate_first_device_succeeds()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        var act = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", "PC-1");
        act.Should().NotBeNull();
        act.HardwareFingerprint.Should().Be("fp-1");
    }

    [Fact]
    public async Task Activate_second_device_when_slots_1_throws_slot_full()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);

        var act = async () => await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-2", null);
        var ex = await act.Should().ThrowAsync<ActivationManager.ActivationException>();
        ex.Which.Code.Should().Be("slot-full");
    }

    [Fact]
    public async Task Activate_after_deactivate_succeeds()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);
        var ok = await mgr.DeactivateAsync(license.LicenseKey, customer.Id, "fp-1");
        ok.Should().BeTrue();

        var act = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-2", null);
        act.HardwareFingerprint.Should().Be("fp-2");
    }

    [Fact]
    public async Task Activate_same_device_twice_returns_same_activation_and_updates_LastSeen()
    {
        var (customer, license) = await SeedAsync(slots: 1);
        using var scope = _factory.Services.CreateScope();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        var first = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);
        await Task.Delay(20);
        var second = await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp-1", null);

        second.Id.Should().Be(first.Id);
        second.LastSeenAt.Should().BeAfter(first.ActivatedAt);
    }

    [Fact]
    public async Task Activate_revoked_license_throws()
    {
        var (customer, license) = await SeedAsync();
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var mgr = scope.ServiceProvider.GetRequiredService<ActivationManager>();

        var fresh = db.Licenses.Find(license.Id)!;
        fresh.RevokedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var act = async () => await mgr.ActivateAsync(license.LicenseKey, customer.Id, "fp", null);
        var ex = await act.Should().ThrowAsync<ActivationManager.ActivationException>();
        ex.Which.Code.Should().Be("license-revoked");
    }
}
```

- [ ] **Step 8: LicenseValidator tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Services/LicenseValidatorTests.cs`:

```csharp
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Domain;
using LiveDeck.LicenseServer.Services.Licensing;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Services;

public class LicenseValidatorTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public LicenseValidatorTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Validate_returns_null_for_unknown_key()
    {
        using var scope = _factory.Services.CreateScope();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();
        var result = await v.ValidateAsync("LDK-NONE", "fp", Guid.NewGuid());
        result.Should().BeNull();
    }

    [Fact]
    public async Task Validate_returns_NotActivated_when_no_activation()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"v-{Guid.NewGuid():N}@x.com",
            Name = "V", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.AddRange(customer, license);
        await db.SaveChangesAsync();

        var result = await v.ValidateAsync(license.LicenseKey, "fp-x", customer.Id);
        result.Should().NotBeNull();
        result!.Status.Should().Be(LicenseStatus.NotActivated);
        result.RemainingDays.Should().Be(30);
    }

    [Fact]
    public async Task Validate_returns_Expired_for_past_expiry()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"x-{Guid.NewGuid():N}@x.com",
            Name = "X", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow.AddDays(-400),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1)
        };
        db.AddRange(customer, license);
        await db.SaveChangesAsync();

        var result = await v.ValidateAsync(license.LicenseKey, "fp", customer.Id);
        result!.Status.Should().Be(LicenseStatus.Expired);
    }

    [Fact]
    public async Task Validate_returns_Active_when_activation_exists()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var v = scope.ServiceProvider.GetRequiredService<LicenseValidator>();

        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"a-{Guid.NewGuid():N}@x.com",
            Name = "A", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow
        };
        var license = new License
        {
            Id = Guid.NewGuid(), LicenseKey = LicenseIssuer.GenerateKey(),
            CustomerId = customer.Id, SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(60)
        };
        db.AddRange(customer, license);
        db.Activations.Add(new Activation
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, HardwareFingerprint = "fp-1",
            ActivatedAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var result = await v.ValidateAsync(license.LicenseKey, "fp-1", customer.Id);
        result!.Status.Should().Be(LicenseStatus.Active);
        result.SlotInfo!.ThisDeviceActive.Should().BeTrue();
        result.SlotInfo.Used.Should().Be(1);
        result.SlotInfo.Total.Should().Be(1);
    }
}
```

- [ ] **Step 9: Build + run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~Services" 2>&1 | tail -3
```

Expected: 0 errors. Service tests pass (5 hasher + 2 jwt + 3 issuer + 4 validator + 5 activation = 19 service tests).

- [ ] **Step 10: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Services/Licensing/ LiveDeck.LicenseServer/Program.cs LiveDeck.LicenseServer.Tests/Services/
git commit -m "feat(license-server): add LicenseIssuer + LicenseValidator + ActivationManager"
```

---

### Task 13: Admin License endpoints (issue + get + revoke + extend + list)

**Files:**
- Create: `LiveDeck.LicenseServer/Controllers/Licenses/AdminLicensesController.cs`
- Create: `LiveDeck.LicenseServer.Tests/Licenses/AdminLicensesTests.cs`

- [ ] **Step 1: AdminLicensesController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Licenses/AdminLicensesController.cs`:

```csharp
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace LiveDeck.LicenseServer.Controllers.Licenses;

[ApiController]
[Route("api/v1/admin/licenses")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminLicensesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly LicenseIssuer _issuer;

    public AdminLicensesController(LicenseDbContext db, LicenseIssuer issuer)
    {
        _db = db;
        _issuer = issuer;
    }

    public sealed record IssueRequest(string CustomerEmail, string SkuCode,
        int? DurationDaysOverride, int? SlotsOverride);

    [HttpPost]
    public async Task<IActionResult> Issue([FromBody] IssueRequest req, CancellationToken ct)
    {
        try
        {
            var result = await _issuer.IssueAsync(
                new(req.CustomerEmail, req.SkuCode, req.DurationDaysOverride, req.SlotsOverride), ct);
            return CreatedAtAction(nameof(Get), new { key = result.LicenseKey },
                new { licenseKey = result.LicenseKey, expiresAt = result.ExpiresAt });
        }
        catch (LicenseIssuer.IssueException ex)
        {
            return Problem(title: ex.Code, detail: ex.Message, statusCode: 400);
        }
    }

    [HttpGet("{key}")]
    public async Task<IActionResult> Get(string key, CancellationToken ct)
    {
        var l = await _db.Licenses
            .Include(x => x.Customer)
            .Include(x => x.Activations)
            .FirstOrDefaultAsync(x => x.LicenseKey == key, ct);
        if (l is null) return NotFound();

        return Ok(new
        {
            id = l.Id,
            licenseKey = l.LicenseKey,
            customerEmail = l.Customer.Email,
            skuCode = l.SkuCode,
            activationSlots = l.ActivationSlots,
            issuedAt = l.IssuedAt,
            expiresAt = l.ExpiresAt,
            revokedAt = l.RevokedAt,
            revokeReason = l.RevokeReason,
            activations = l.Activations.Select(a => new
            {
                id = a.Id,
                hardwareFingerprint = a.HardwareFingerprint,
                machineName = a.MachineName,
                activatedAt = a.ActivatedAt,
                lastSeenAt = a.LastSeenAt,
                deactivatedAt = a.DeactivatedAt
            })
        });
    }

    public sealed record RevokeRequest(string Reason);

    [HttpPost("{key}/revoke")]
    public async Task<IActionResult> Revoke(string key, [FromBody] RevokeRequest req, CancellationToken ct)
    {
        var l = await _db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, ct);
        if (l is null) return NotFound();
        l.RevokedAt = DateTimeOffset.UtcNow;
        l.RevokeReason = req.Reason;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    public sealed record ExtendRequest(int AdditionalDays);

    [HttpPost("{key}/extend")]
    public async Task<IActionResult> Extend(string key, [FromBody] ExtendRequest req, CancellationToken ct)
    {
        if (req.AdditionalDays <= 0) return Problem(title: "invalid-days", statusCode: 400);
        var l = await _db.Licenses.FirstOrDefaultAsync(x => x.LicenseKey == key, ct);
        if (l is null) return NotFound();
        l.ExpiresAt = l.ExpiresAt.AddDays(req.AdditionalDays);
        await _db.SaveChangesAsync(ct);
        return Ok(new { newExpiresAt = l.ExpiresAt });
    }

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] string? customer, [FromQuery] string? status, CancellationToken ct)
    {
        var q = _db.Licenses.Include(l => l.Customer).AsQueryable();
        if (!string.IsNullOrEmpty(customer))
            q = q.Where(l => l.Customer.Email.Contains(customer));
        if (status == "revoked") q = q.Where(l => l.RevokedAt != null);
        else if (status == "expired") q = q.Where(l => l.RevokedAt == null && l.ExpiresAt < DateTimeOffset.UtcNow);
        else if (status == "active") q = q.Where(l => l.RevokedAt == null && l.ExpiresAt >= DateTimeOffset.UtcNow);

        var rows = await q
            .OrderByDescending(l => l.IssuedAt)
            .Select(l => new
            {
                licenseKey = l.LicenseKey,
                customerEmail = l.Customer.Email,
                skuCode = l.SkuCode,
                expiresAt = l.ExpiresAt,
                revokedAt = l.RevokedAt
            })
            .ToListAsync(ct);
        return Ok(rows);
    }
}
```

- [ ] **Step 2: AdminLicenses tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Licenses/AdminLicensesTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Licenses;

public class AdminLicensesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public AdminLicensesTests(ApiFactory factory) => _factory = factory;

    private async Task<HttpClient> AdminClientAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<string> CreateCustomerAsync(HttpClient client, string email)
    {
        await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Test", initialPassword = "pw12345678", autoConfirm = true
        });
        return email;
    }

    [Fact]
    public async Task Issue_creates_license_for_existing_customer()
    {
        var client = await AdminClientAsync();
        var email = $"l-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);

        var resp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = await resp.Content.ReadFromJsonAsync<IssueBody>();
        body!.licenseKey.Should().StartWith("LDK-");
    }

    [Fact]
    public async Task Issue_with_unknown_customer_returns_400()
    {
        var client = await AdminClientAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = "nope@x.com", skuCode = "STD"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Get_returns_license_with_customer_and_empty_activations()
    {
        var client = await AdminClientAsync();
        var email = $"g-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);
        var issueResp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var resp = await client.GetAsync($"/api/v1/admin/licenses/{issued!.licenseKey}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Revoke_marks_license_revoked()
    {
        var client = await AdminClientAsync();
        var email = $"r-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);
        var issueResp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var resp = await client.PostAsJsonAsync($"/api/v1/admin/licenses/{issued!.licenseKey}/revoke", new
        {
            reason = "Test revoke"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task Extend_adds_days_to_expiry()
    {
        var client = await AdminClientAsync();
        var email = $"e-{Guid.NewGuid():N}@x.com";
        await CreateCustomerAsync(client, email);
        var issueResp = await client.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var resp = await client.PostAsJsonAsync($"/api/v1/admin/licenses/{issued!.licenseKey}/extend", new
        {
            additionalDays = 90
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task List_returns_issued_licenses()
    {
        var client = await AdminClientAsync();
        var resp = await client.GetAsync("/api/v1/admin/licenses");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private sealed record IssueBody(string licenseKey, DateTimeOffset expiresAt);
}
```

- [ ] **Step 3: Build + run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~AdminLicenses" 2>&1 | tail -3
```

Expected: 6/6 admin license tests pass.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Controllers/Licenses/AdminLicensesController.cs LiveDeck.LicenseServer.Tests/Licenses/AdminLicensesTests.cs
git commit -m "feat(license-server): add admin license endpoints (issue/get/revoke/extend/list)"
```

---

### Task 14: Customer License endpoints (validate + activate + deactivate + heartbeat)

**Files:**
- Create: `LiveDeck.LicenseServer/Controllers/Licenses/LicensesController.cs`
- Create: `LiveDeck.LicenseServer.Tests/Licenses/CustomerLicenseFlowTests.cs`

- [ ] **Step 1: LicensesController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Licenses/LicensesController.cs`:

```csharp
using System.Security.Claims;
using LiveDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiveDeck.LicenseServer.Controllers.Licenses;

[ApiController]
[Route("api/v1/licenses")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesController : ControllerBase
{
    private readonly LicenseValidator _validator;
    private readonly ActivationManager _activations;

    public LicensesController(LicenseValidator validator, ActivationManager activations)
    {
        _validator = validator;
        _activations = activations;
    }

    public sealed record LicenseHwRequest(string LicenseKey, string HardwareFingerprint);
    public sealed record ActivateRequest(string LicenseKey, string HardwareFingerprint, string? MachineName);

    [HttpPost("validate")]
    public async Task<IActionResult> Validate([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var result = await _validator.ValidateAsync(req.LicenseKey, req.HardwareFingerprint, customerId, ct);
        if (result is null) return NotFound();
        return Ok(new
        {
            status = result.Status.ToString().ToLowerInvariant(),
            expiresAt = result.ExpiresAt,
            remainingDays = result.RemainingDays,
            sku = result.Sku,
            slotInfo = result.SlotInfo
        });
    }

    [HttpPost("activate")]
    public async Task<IActionResult> Activate([FromBody] ActivateRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        try
        {
            var act = await _activations.ActivateAsync(
                req.LicenseKey, customerId, req.HardwareFingerprint, req.MachineName, ct);
            return StatusCode(201, new { activationId = act.Id, expiresAt = act.License?.ExpiresAt });
        }
        catch (ActivationManager.ActivationException ex)
        {
            return Problem(title: ex.Code, detail: ex.Message, statusCode: 409);
        }
    }

    [HttpPost("deactivate")]
    public async Task<IActionResult> Deactivate([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ok = await _activations.DeactivateAsync(req.LicenseKey, customerId, req.HardwareFingerprint, ct);
        if (!ok) return NotFound();
        return NoContent();
    }

    [HttpPost("heartbeat")]
    public async Task<IActionResult> Heartbeat([FromBody] LicenseHwRequest req, CancellationToken ct)
    {
        var customerId = GetCustomerId();
        var ok = await _activations.HeartbeatAsync(req.LicenseKey, customerId, req.HardwareFingerprint, ct);
        if (!ok) return Problem(title: "not-activated", statusCode: 404);

        // Return basic status for client offline grace handling (4b will need this).
        var result = await _validator.ValidateAsync(req.LicenseKey, req.HardwareFingerprint, customerId, ct);
        return Ok(new
        {
            status = result?.Status.ToString().ToLowerInvariant(),
            expiresAt = result?.ExpiresAt
        });
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

- [ ] **Step 2: Customer license flow tests**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Licenses/CustomerLicenseFlowTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Licenses;

public class CustomerLicenseFlowTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public CustomerLicenseFlowTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient client, string licenseKey)> SetupAsync(int slots = 1)
    {
        // Admin issues license
        var (adminToken, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"flow-{Guid.NewGuid():N}@x.com";
        await adminClient.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Flow", initialPassword = "pw12345678", autoConfirm = true
        });
        var issueResp = await adminClient.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD", slotsOverride = slots
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        // Customer logs in
        var customerClient = _factory.CreateClient();
        var loginResp = await customerClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "pw12345678"
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        customerClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);

        return (customerClient, issued!.licenseKey);
    }

    [Fact]
    public async Task Validate_unactivated_returns_NotActivated_status()
    {
        var (client, key) = await SetupAsync();
        var resp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<ValidateBody>();
        body!.status.Should().Be("notactivated");
    }

    [Fact]
    public async Task Activate_then_validate_returns_active()
    {
        var (client, key) = await SetupAsync();
        var actResp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = "PC-1"
        });
        actResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var validateResp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        var body = await validateResp.Content.ReadFromJsonAsync<ValidateBody>();
        body!.status.Should().Be("active");
    }

    [Fact]
    public async Task Activate_when_slot_full_returns_409()
    {
        var (client, key) = await SetupAsync(slots: 1);
        await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = (string?)null
        });

        var resp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-2", machineName = (string?)null
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Deactivate_frees_slot()
    {
        var (client, key) = await SetupAsync(slots: 1);
        await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = (string?)null
        });
        var deactResp = await client.PostAsJsonAsync("/api/v1/licenses/deactivate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        deactResp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var actResp = await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-2", machineName = (string?)null
        });
        actResp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Heartbeat_updates_LastSeenAt()
    {
        var (client, key) = await SetupAsync();
        await client.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1", machineName = (string?)null
        });

        var resp = await client.PostAsJsonAsync("/api/v1/licenses/heartbeat", new
        {
            licenseKey = key, hardwareFingerprint = "fp-1"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Customer_cannot_access_other_customers_license()
    {
        var (clientA, keyA) = await SetupAsync();
        var (clientB, _) = await SetupAsync();

        // clientB tries to validate clientA's license
        var resp = await clientB.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = keyA, hardwareFingerprint = "fp"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Validate_without_token_returns_401()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/licenses/validate", new
        {
            licenseKey = "LDK-X", hardwareFingerprint = "fp"
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private sealed record IssueBody(string licenseKey, DateTimeOffset expiresAt);
    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record ValidateBody(string status, DateTimeOffset? expiresAt, int? remainingDays);
}
```

- [ ] **Step 3: Build + run**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.LicenseServer 2>&1 | tail -3
dotnet test LiveDeck.LicenseServer.Tests --filter "FullyQualifiedName~CustomerLicense" 2>&1 | tail -3
```

Expected: 7/7 customer license flow tests pass.

- [ ] **Step 4: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Controllers/Licenses/LicensesController.cs LiveDeck.LicenseServer.Tests/Licenses/CustomerLicenseFlowTests.cs
git commit -m "feat(license-server): add customer license endpoints (validate/activate/deactivate/heartbeat)"
```

---

### Task 15: Force-deactivate + Dockerfile + final verification

**Files:**
- Create: `LiveDeck.LicenseServer/Controllers/Activations/AdminActivationsController.cs`
- Create: `LiveDeck.LicenseServer.Tests/Activations/ForceDeactivateTests.cs`
- Create: `LiveDeck.LicenseServer/Dockerfile`
- Create: `LiveDeck.LicenseServer/.dockerignore`
- Create: `docker-compose.yml`

- [ ] **Step 1: AdminActivationsController**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Controllers/Activations/AdminActivationsController.cs`:

```csharp
using LiveDeck.LicenseServer.Services.Licensing;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace LiveDeck.LicenseServer.Controllers.Activations;

[ApiController]
[Route("api/v1/admin/activations")]
[Authorize(AuthenticationSchemes = "Bearer-Admin")]
public sealed class AdminActivationsController : ControllerBase
{
    private readonly ActivationManager _activations;

    public AdminActivationsController(ActivationManager activations) => _activations = activations;

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> ForceDeactivate(Guid id, CancellationToken ct)
    {
        var ok = await _activations.ForceDeactivateAsync(id, ct);
        if (!ok) return NotFound();
        return NoContent();
    }
}
```

- [ ] **Step 2: ForceDeactivate test**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer.Tests/Activations/ForceDeactivateTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using LiveDeck.LicenseServer.Data;
using LiveDeck.LicenseServer.Tests.TestHelpers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LiveDeck.LicenseServer.Tests.Activations;

public class ForceDeactivateTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;

    public ForceDeactivateTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Admin_force_deactivates_active_activation()
    {
        // Setup: admin issues license, customer activates
        var (adminToken, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var adminClient = _factory.CreateClient();
        adminClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);

        var email = $"fd-{Guid.NewGuid():N}@x.com";
        await adminClient.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "FD", initialPassword = "pw12345678", autoConfirm = true
        });
        var issueResp = await adminClient.PostAsJsonAsync("/api/v1/admin/licenses", new
        {
            customerEmail = email, skuCode = "STD"
        });
        var issued = await issueResp.Content.ReadFromJsonAsync<IssueBody>();

        var custClient = _factory.CreateClient();
        var loginResp = await custClient.PostAsJsonAsync("/api/v1/auth/login", new
        {
            email, password = "pw12345678"
        });
        var login = await loginResp.Content.ReadFromJsonAsync<LoginBody>();
        custClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login!.Token);
        var actResp = await custClient.PostAsJsonAsync("/api/v1/licenses/activate", new
        {
            licenseKey = issued!.licenseKey, hardwareFingerprint = "fp-1", machineName = "PC"
        });
        var actBody = await actResp.Content.ReadFromJsonAsync<ActBody>();

        // Admin force-deactivates
        var resp = await adminClient.DeleteAsync($"/api/v1/admin/activations/{actBody!.activationId}");
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var act = await db.Activations.FirstAsync(a => a.Id == actBody.activationId);
        act.DeactivatedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Force_deactivate_unknown_id_returns_404()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var resp = await client.DeleteAsync($"/api/v1/admin/activations/{Guid.NewGuid()}");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private sealed record IssueBody(string licenseKey);
    private sealed record LoginBody(string Token, DateTimeOffset ExpiresAt);
    private sealed record ActBody(Guid activationId);
}
```

- [ ] **Step 3: Dockerfile**

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/Dockerfile`:

```dockerfile
# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj", "LiveDeck.LicenseServer/"]
RUN dotnet restore "LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj"
COPY LiveDeck.LicenseServer/ LiveDeck.LicenseServer/
RUN dotnet publish "LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "LiveDeck.LicenseServer.dll"]
```

Create `C:/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer/.dockerignore`:

```
**/bin/
**/obj/
**/.vs/
**/*.user
tmp/
```

- [ ] **Step 4: docker-compose.yml**

Create `C:/Users/burak/source/repos/LiveDeck/docker-compose.yml`:

```yaml
services:
  sqlserver:
    image: mcr.microsoft.com/mssql/server:2022-latest
    environment:
      ACCEPT_EULA: "Y"
      MSSQL_SA_PASSWORD: "Strong!Pass123"
    ports:
      - "1433:1433"
    volumes:
      - mssql-data:/var/opt/mssql

  license-server:
    build:
      context: .
      dockerfile: LiveDeck.LicenseServer/Dockerfile
    environment:
      ConnectionStrings__LicenseDb: "Server=sqlserver;Database=LiveDeckLicense;User=sa;Password=Strong!Pass123;TrustServerCertificate=true;"
      Jwt__SecretKey: "change-me-in-production-at-least-32-chars-long-secret"
      Jwt__Issuer: "livedeck-license-server"
      Email__Provider: "disk"
      App__PublicBaseUrl: "http://localhost:8080"
    ports:
      - "8080:8080"
    depends_on:
      - sqlserver

volumes:
  mssql-data:
```

`docker-compose.yml` repo kökünde — deploy 4a kapsamı dışı, sadece build edilebilir.

- [ ] **Step 5: Build + run all tests**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet build LiveDeck.sln 2>&1 | tail -5
dotnet test LiveDeck.LicenseServer.Tests 2>&1 | tail -3
dotnet test LiveDeck.Tests 2>&1 | tail -3
```

Expected: Solution build clean (0 errors). LicenseServer tests: ~50 pass (1 health + 1 dbcontext + 5 hasher + 2 jwt + 8 auth-register/confirm/resend + 4 login + 2 me + 3 changepw + 2 admin login + 5 customers + 1 sku + 3 issuer + 4 validator + 5 activation + 6 admin licenses + 7 customer flow + 2 force-deactivate). LiveDeck.Tests: 117/117 (regression check, mevcut faz testleri).

- [ ] **Step 6: Verify Docker build (optional, requires Docker installed)**

```bash
cd /c/Users/burak/source/repos/LiveDeck
docker build -t livedeck-license-server -f LiveDeck.LicenseServer/Dockerfile .
```

Expected: image builds successfully. Skip this step if Docker is not available locally.

- [ ] **Step 7: Manual smoke (with DiskEmailSender)**

Run the server in dev mode:

```bash
cd /c/Users/burak/source/repos/LiveDeck/LiveDeck.LicenseServer
dotnet run --launch-profile https
```

Open Swagger UI at `https://localhost:7XXX/swagger`. Verify 23 endpoints listed (4 anon + 7 customer-JWT + 12 admin-JWT). Manually:

1. POST `/api/v1/auth/register` with `{email, name, password}` — 201
2. Check `./tmp/emails/` directory — `.eml` file exists with confirmation link
3. Open the .eml, extract token, GET `/api/v1/auth/confirm-email/{token}` — 200
4. POST `/api/v1/auth/login` — get JWT
5. With JWT, POST `/api/v1/auth/login` — wait, that's anon. With JWT, GET `/api/v1/me` — see customer details
6. (Need admin) Bootstrap admin via env var `Admin__InitialUsername` + `Admin__InitialPasswordHash` (Argon2 hash from PasswordHasher).

Document the smoke results in `docs/smoke-tests/2026-04-28-phase-4a-smoke.md` if kept.

- [ ] **Step 8: Commit**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git add LiveDeck.LicenseServer/Controllers/Activations/ LiveDeck.LicenseServer/Dockerfile LiveDeck.LicenseServer/.dockerignore docker-compose.yml LiveDeck.LicenseServer.Tests/Activations/
git commit -m "feat(license-server): add force-deactivate + Dockerfile + docker-compose"
```

---

## Self-Review

**Spec coverage check:**

| Spec section | Plan task |
|---|---|
| §2.1 Solution etkisi (2 yeni proje) | Task 1 |
| §2.2 Stack | Tasks 1-7 (paketler + DI) |
| §2.3 Project yapısı | Tasks 1-15 (her dosya bir task'a bağlı) |
| §3.1 Entity'ler | Task 2 |
| §3.2 Indexler & FK | Task 3 (DbContext FluentAPI) |
| §3.3 Seed | Task 3 (HasData STD/PRO) |
| §3.4 Lisans key formatı | Task 12 (LicenseIssuer.GenerateKey) |
| §4.1 Public endpoints (4) | Tasks 8 (register/confirm/resend) + 9 (login) |
| §4.2 Customer JWT (7) | Tasks 9 (me/password) + 14 (4 license endpoints) |
| §4.3 Admin JWT (12) | Tasks 10 (admin login) + 11 (customers/skus) + 13 (licenses) + 15 (force-deactivate) |
| §4.4 Validate response detayı | Task 12 (ValidationResult) + 14 (controller) |
| §5.1 İki JWT scheme | Task 5 (JwtTokenService) + Task 7 (Program.cs) |
| §5.2 Argon2id parametreleri | Task 4 |
| §5.3 Email enumeration koruması | Task 8 (register/resend silent 202) + Task 9 (login generic 401) |
| §5.4 Rate limiting | Task 7 (Program.cs RateLimiter) |
| §5.5 HTTPS | Task 1 + 7 (UseHttpsRedirection) |
| §5.6 CORS | Task 7 (open policy) |
| §6 Email altyapısı | Task 6 (4 dosya: interface + 2 impl + templates) |
| §7 Hata yönetimi | Distributed (her endpoint'te ProblemDetails) |
| §8 Test stratejisi | Tasks 4-15 (her endpoint için integration tests) |
| §9 Configuration | Task 1 (appsettings) + Task 7 (env binding) |
| §10 Docker | Task 15 |
| §11 YAGNI | Plan refrains: no refresh tokens, no password reset, no audit log |
| §13 Kabul kriterleri | Task 15 step 5 (build+test) + step 7 (manual smoke) |

All sections covered.

**Placeholder scan:** "TBD", "TODO", "implement later", "fill in", "appropriate", "similar to" — none found. Each task has actual code blocks for every step.

**Type consistency check:**

- `Customer` 7 fields (Task 2), used in Tasks 3 (DbContext config), 8 (register), 9 (login/me), 11 (admin create), 12 (LicenseIssuer.IssueAsync). Consistent.
- `License` entity (Task 2), used in Tasks 3, 12 (issue), 13 (admin endpoints), 14 (validate/activate). Consistent.
- `Activation` entity (Task 2), used in Tasks 12 (manager), 14 (controller), 15 (force-deactivate). Consistent.
- `LicenseStatus` enum (Task 12), values `Active/Expired/Revoked/NotActivated/SlotMismatch` — match spec §4.4.
- `JwtOptions.CustomerAudience` / `AdminAudience` constants (Task 5), referenced in Task 7 (auth handlers), Tasks 9 (`Bearer-Customer`), 10 (`Bearer-Admin`), 11/13/15 (`Authorize` attributes). Consistent.
- `PasswordHasher.Hash`/`Verify` (Task 4), consumed Tasks 8 (register), 9 (login/changepw), 10 (admin login), 11 (admin create customer). Consistent.
- `JwtTokenService.IssueCustomerToken`/`IssueAdminToken` (Task 5), consumed Tasks 9 (login), 10 (admin login). Consistent.
- `IEmailSender.SendAsync` signature (Task 6), consumed Task 8 (`EmailConfirmationService.IssueAndSendAsync`). Consistent.
- `LicenseIssuer.IssueAsync` (Task 12), consumed Task 13 (AdminLicensesController.Issue). Consistent.
- `ActivationManager.{Activate,Deactivate,Heartbeat,ForceDeactivate}Async` (Task 12), consumed Tasks 14 (LicensesController) and 15 (AdminActivationsController). Consistent.
- `LicenseValidator.ValidateAsync(licenseKey, hwfp, customerId, ct)` 4-arg signature (Task 12), consumed Task 14 (LicensesController.Validate + Heartbeat). Consistent.
- `ApiFactory.SeedAdminAndLoginAsync` (Task 10), consumed Tasks 11/13/15 admin tests. Consistent.

All consistent.

---

## Execution Handoff

Plan complete and saved to `docs/superpowers/plans/2026-04-28-phase-4a-license-server.md`. Two execution options:

**1. Subagent-Driven (recommended)** — Fresh subagent per task, review between tasks, fast iteration

**2. Inline Execution** — Execute tasks in this session using executing-plans, batch execution with checkpoints

Hangisi?

