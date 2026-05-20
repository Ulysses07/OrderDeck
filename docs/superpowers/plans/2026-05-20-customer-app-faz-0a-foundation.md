# Müşteri (Shopper) App — Faz 0a Foundation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Müşteri app'i için server-side foundation: PDF parser ortak lib, 5 yeni entity, License/Payment ek alanları, EF migration, Bearer-Shopper JWT scheme. Bu plan tek başına shippable — hiçbir endpoint yok ama mevcut yayıncı akışlarına dokunmuyor, sadece yeni masa ve infrastructure ekliyor.

**Architecture:** Önce mevcut `PdfDekontParser`'ı yeni paylaşılan lib `OrderDeck.PdfParsing`'e taşı (WPF + LicenseServer ortak kullanım için). Sonra `OrderDeck.LicenseServer/Domain/` altına 5 yeni entity ekle ve `LicenseDbContext`'e bağla. License/Payment'a ek alanlar ekle. Tek EF migration ile DB değişiklikleri uygulanır. Son adım `Program.cs`'te `Bearer-Shopper` JWT scheme'i kaydet — `Bearer-Customer`/`Bearer-Admin` pattern'i ile birebir aynı yapı.

**Tech Stack:** .NET 10, EF Core (SQL Server prod / InMemory test), xUnit + FluentAssertions, PdfPig, JWT bearer auth.

---

## File Structure

**New files:**
- `OrderDeck.PdfParsing/OrderDeck.PdfParsing.csproj` — yeni paylaşılan parser lib
- `OrderDeck.PdfParsing/PdfDekontParser.cs` — `OrderDeck.Core/Payments/PdfDekontParser.cs`'ten taşındı
- `OrderDeck.LicenseServer/Domain/Shopper.cs`
- `OrderDeck.LicenseServer/Domain/ShopperBroadcasterLink.cs`
- `OrderDeck.LicenseServer/Domain/WpfCustomerProjection.cs`
- `OrderDeck.LicenseServer/Domain/ShopperPushDevice.cs`
- `OrderDeck.LicenseServer/Domain/PaymentSubmissionAudit.cs`
- `OrderDeck.LicenseServer/Services/Auth/JwtOptions.cs` — `ShopperAudience` sabiti eklenir
- `OrderDeck.LicenseServer/Migrations/<timestamp>_AddShopperFoundation.cs` — auto-generated
- `OrderDeck.LicenseServer.Tests/Domain/ShopperEntityTests.cs`
- `OrderDeck.LicenseServer.Tests/Domain/LicenseShopperFieldsTests.cs`
- `OrderDeck.LicenseServer.Tests/Domain/PaymentShopperFieldsTests.cs`
- `OrderDeck.LicenseServer.Tests/Auth/BearerShopperSchemeTests.cs`

**Modified files:**
- `OrderDeck.Core/OrderDeck.Core.csproj` — `<ProjectReference Include="..\OrderDeck.PdfParsing\OrderDeck.PdfParsing.csproj" />` eklenir, `PdfPig` package referansı kalkar
- `OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` — aynı ProjectReference eklenir
- `OrderDeck.Tests/Payments/PdfDekontParserTests.cs` — namespace `using OrderDeck.PdfParsing;`
- `OrderDeck.LicenseServer/Domain/License.cs` — ek property'ler
- `OrderDeck.LicenseServer/Domain/Payment.cs` — ek property'ler
- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — yeni DbSet'ler + `OnModelCreating` config'leri
- `OrderDeck.LicenseServer/Program.cs` — `Bearer-Shopper` scheme tescil + options binding
- `OrderDeck.sln` — `OrderDeck.PdfParsing` projesi `dotnet sln add` ile eklenir

**Removed files:**
- `OrderDeck.Core/Payments/PdfDekontParser.cs` — taşındı (git mv)

---

## Task 1: Yeni `OrderDeck.PdfParsing` projesini oluştur

**Files:**
- Create: `OrderDeck.PdfParsing/OrderDeck.PdfParsing.csproj`
- Modify: `OrderDeck.sln`

- [ ] **Step 1: csproj dosyasını oluştur**

```bash
mkdir -p OrderDeck.PdfParsing
```

`OrderDeck.PdfParsing/OrderDeck.PdfParsing.csproj` içeriği:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <LangVersion>latest</LangVersion>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <WarningsNotAsErrors>CS1591</WarningsNotAsErrors>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="PdfPig" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Solution'a ekle**

```bash
dotnet sln OrderDeck.sln add OrderDeck.PdfParsing/OrderDeck.PdfParsing.csproj
```

- [ ] **Step 3: Boş projeyi build et — yeşil olmalı**

```bash
dotnet build OrderDeck.PdfParsing/OrderDeck.PdfParsing.csproj
```

Expected: `Build succeeded` + `0 Error(s)`.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.PdfParsing/ OrderDeck.sln
git commit -m "chore(pdf-parsing): yeni OrderDeck.PdfParsing lib iskeleti"
```

---

## Task 2: `PdfDekontParser`'ı yeni lib'e taşı

**Files:**
- Move: `OrderDeck.Core/Payments/PdfDekontParser.cs` → `OrderDeck.PdfParsing/PdfDekontParser.cs`
- Modify: `OrderDeck.Core/OrderDeck.Core.csproj`
- Modify: `OrderDeck.Tests/Payments/PdfDekontParserTests.cs`

- [ ] **Step 1: Dosyayı git mv ile taşı (history korunur)**

```bash
git mv OrderDeck.Core/Payments/PdfDekontParser.cs OrderDeck.PdfParsing/PdfDekontParser.cs
```

- [ ] **Step 2: Namespace güncelle**

`OrderDeck.PdfParsing/PdfDekontParser.cs:10` satırını:

```csharp
namespace OrderDeck.Core.Payments;
```

→ olarak değiştir:

```csharp
namespace OrderDeck.PdfParsing;
```

- [ ] **Step 3: `OrderDeck.Core.csproj`'tan PdfPig kaldır, OrderDeck.PdfParsing reference ekle**

`OrderDeck.Core/OrderDeck.Core.csproj` içindeki `<PackageReference Include="PdfPig" />` satırını sil. Aynı `<ItemGroup>`'a ekle:

```xml
<ProjectReference Include="..\OrderDeck.PdfParsing\OrderDeck.PdfParsing.csproj" />
```

- [ ] **Step 4: Test dosyasının using'ini güncelle**

`OrderDeck.Tests/Payments/PdfDekontParserTests.cs` en üstündeki `using OrderDeck.Core.Payments;` satırını şu olarak değiştir:

```csharp
using OrderDeck.PdfParsing;
```

- [ ] **Step 5: Build + tüm WPF test suite çalıştır — yeşil olmalı**

```bash
dotnet build OrderDeck.sln
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~PdfDekontParser"
```

Expected: Build succeeded; parser test'leri pass (taşıma fonksiyonel değişiklik içermiyor).

- [ ] **Step 6: Commit**

```bash
git add -A
git commit -m "refactor(pdf-parsing): PdfDekontParser'ı OrderDeck.PdfParsing lib'ine taşı"
```

---

## Task 3: `OrderDeck.LicenseServer`'a `OrderDeck.PdfParsing` reference ekle

**Files:**
- Modify: `OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`

- [ ] **Step 1: ProjectReference ekle**

`OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` içindeki `<ItemGroup>` blocklarından birine ekle (proje referansları olan):

```xml
<ProjectReference Include="..\OrderDeck.PdfParsing\OrderDeck.PdfParsing.csproj" />
```

- [ ] **Step 2: Build et — yeşil olmalı**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Expected: Build succeeded. Parser sınıfı henüz kullanılmıyor, ama referans hazır.

- [ ] **Step 3: Commit**

```bash
git add OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
git commit -m "chore(license-server): OrderDeck.PdfParsing referansı"
```

---

## Task 4: `Shopper` entity'sini oluştur

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/Shopper.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/ShopperEntityTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/ShopperEntityTests.cs` (yeni dosya):

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperEntityTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shopper-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_persists_all_required_fields()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Shoppers.Add(new Shopper
        {
            Id = id,
            FullName = "Ali Veli",
            Phone = "+905551112233",
            PasswordHash = "bcrypt-hash",
            Address = "Bağdat Cd. 1",
            Email = "ali@example.com",
            Tc = "12345678901",
            NotificationsEnabledBroadcast = true,
            NotificationsEnabledOrders = true,
            NotificationsEnabledPayments = false,
            CreatedAt = now,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Shoppers.SingleAsync(s => s.Id == id);
        loaded.FullName.Should().Be("Ali Veli");
        loaded.Phone.Should().Be("+905551112233");
        loaded.PasswordHash.Should().Be("bcrypt-hash");
        loaded.Email.Should().Be("ali@example.com");
        loaded.Tc.Should().Be("12345678901");
        loaded.NotificationsEnabledPayments.Should().BeFalse();
    }

    [Fact]
    public async Task Phone_is_unique()
    {
        await using var db = NewDb();
        var now = DateTimeOffset.UtcNow;
        db.Shoppers.Add(new Shopper
        {
            Id = Guid.NewGuid(), FullName = "A", Phone = "+905551112233",
            PasswordHash = "h", Address = "x", CreatedAt = now, UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        db.Shoppers.Add(new Shopper
        {
            Id = Guid.NewGuid(), FullName = "B", Phone = "+905551112233",
            PasswordHash = "h2", Address = "y", CreatedAt = now, UpdatedAt = now,
        });

        // InMemory provider doesn't enforce unique constraints by default; this
        // test will be enabled against a real provider in a later task. For now
        // verify the model metadata says the index is unique.
        var phoneIndex = db.Model.FindEntityType(typeof(Shopper))!
            .GetIndexes()
            .Single(i => i.Properties.Count == 1 && i.Properties[0].Name == nameof(Shopper.Phone));
        phoneIndex.IsUnique.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Test'i çalıştır — fail bekliyor (Shopper henüz yok)**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperEntityTests"
```

Expected: Build error — `Shopper` ve `db.Shoppers` çözülemiyor.

- [ ] **Step 3: `Shopper.cs` oluştur**

`OrderDeck.LicenseServer/Domain/Shopper.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Müşteri (shopper) app kullanıcısı. WPF'teki Customer entity'si (yayıncı)
/// ile karıştırılmamalı — bu, alışveriş yapan son kullanıcı. Telefon global
/// unique kimlik; bir shopper birden çok yayıncıya bağlı olabilir
/// (ShopperBroadcasterLink üzerinden).
/// </summary>
public sealed class Shopper
{
    public Guid Id { get; set; }
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";        // E.164, global unique
    public string PasswordHash { get; set; } = ""; // bcrypt
    public string Address { get; set; } = "";
    public string? Email { get; set; }
    public string? Tc { get; set; }                // KVKK: AES at-rest (Faz 0b'de)

    public bool NotificationsEnabledBroadcast { get; set; } = true;
    public bool NotificationsEnabledOrders { get; set; } = true;
    public bool NotificationsEnabledPayments { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
}
```

- [ ] **Step 4: `LicenseDbContext`'e DbSet + config ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` içindeki `DbSet` listesine (BroadcastPosts'tan sonra) ekle:

```csharp
public DbSet<Shopper> Shoppers => Set<Shopper>();
```

`OnModelCreating` içine (Customer config'inden sonra herhangi bir yere) ekle:

```csharp
mb.Entity<Shopper>(b =>
{
    b.HasKey(s => s.Id);
    b.Property(s => s.FullName).HasMaxLength(200).IsRequired();
    b.Property(s => s.Phone).HasMaxLength(20).IsRequired();
    b.HasIndex(s => s.Phone).IsUnique();
    b.Property(s => s.PasswordHash).HasMaxLength(256).IsRequired();
    b.Property(s => s.Address).HasMaxLength(500).IsRequired();
    b.Property(s => s.Email).HasMaxLength(256);
    b.Property(s => s.Tc).HasMaxLength(11);
});
```

- [ ] **Step 5: Test'i tekrar çalıştır — pass olmalı**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperEntityTests"
```

Expected: 2/2 pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Shopper.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/ShopperEntityTests.cs
git commit -m "feat(domain): Shopper entity (müşteri app kullanıcısı)"
```

---

## Task 5: `ShopperBroadcasterLink` entity'sini oluştur

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/ShopperBroadcasterLink.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/ShopperBroadcasterLinkTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/ShopperBroadcasterLinkTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperBroadcasterLinkTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"link-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_optional_wpf_customer_id_null()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperBroadcasterLinks.Add(new ShopperBroadcasterLink
        {
            Id = id,
            ShopperId = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),
            Platform = "instagram",
            Username = "@ali_veli",
            WpfCustomerId = null,
            JoinedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperBroadcasterLinks.SingleAsync(l => l.Id == id);
        loaded.Platform.Should().Be("instagram");
        loaded.Username.Should().Be("@ali_veli");
        loaded.WpfCustomerId.Should().BeNull();
        loaded.LeftAt.Should().BeNull();
    }

    [Fact]
    public void ShopperId_LicenseId_pair_is_unique()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(ShopperBroadcasterLink))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 2
                && i.Properties.Any(p => p.Name == nameof(ShopperBroadcasterLink.ShopperId))
                && i.Properties.Any(p => p.Name == nameof(ShopperBroadcasterLink.LicenseId)));
        index.Should().NotBeNull("aynı (Shopper, License) çifti iki kez eklenememeli");
        index!.IsUnique.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Test'i çalıştır — fail bekleniyor**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperBroadcasterLinkTests"
```

- [ ] **Step 3: `ShopperBroadcasterLink.cs` oluştur**

`OrderDeck.LicenseServer/Domain/ShopperBroadcasterLink.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper ↔ yayıncı (License) N:N pivot. Per-pair Platform + Username, ve
/// (LicenseId, Platform, Username) match sonucu doldurulan WpfCustomerId
/// (yayıncının WPF'teki lokal Customer kaydı GUID'i — null kalabilir,
/// eşleşme retroactive gelir).
/// </summary>
public sealed class ShopperBroadcasterLink
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public Guid? WpfCustomerId { get; set; }

    public DateTimeOffset JoinedAt { get; set; }
    public DateTimeOffset? LeftAt { get; set; }
}
```

- [ ] **Step 4: `LicenseDbContext`'e DbSet + config ekle**

DbSet listesine:

```csharp
public DbSet<ShopperBroadcasterLink> ShopperBroadcasterLinks => Set<ShopperBroadcasterLink>();
```

`OnModelCreating`'e:

```csharp
mb.Entity<ShopperBroadcasterLink>(b =>
{
    b.HasKey(l => l.Id);
    b.HasOne(l => l.Shopper).WithMany().HasForeignKey(l => l.ShopperId)
     .OnDelete(DeleteBehavior.Cascade);
    b.HasOne(l => l.License).WithMany().HasForeignKey(l => l.LicenseId)
     .OnDelete(DeleteBehavior.Cascade);
    b.HasIndex(l => new { l.ShopperId, l.LicenseId }).IsUnique();
    b.HasIndex(l => new { l.LicenseId, l.JoinedAt });
    b.Property(l => l.Platform).HasMaxLength(32).IsRequired();
    b.Property(l => l.Username).HasMaxLength(128).IsRequired();
});
```

- [ ] **Step 5: Test pass + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperBroadcasterLinkTests"
git add OrderDeck.LicenseServer/Domain/ShopperBroadcasterLink.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/ShopperBroadcasterLinkTests.cs
git commit -m "feat(domain): ShopperBroadcasterLink N:N pivot"
```

Expected: 2/2 pass; commit eklenir.

---

## Task 6: `WpfCustomerProjection` entity'sini oluştur

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/WpfCustomerProjection.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/WpfCustomerProjectionTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/WpfCustomerProjectionTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class WpfCustomerProjectionTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"wpfproj-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_nullable_identity_fields()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.WpfCustomerProjections.Add(new WpfCustomerProjection
        {
            Id = id,
            LicenseId = licenseId,
            Platform = "tiktok",
            Username = "@tt_user",
            FullName = null,        // initial WPF kaydında olmayabilir
            Phone = null,
            Address = null,
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.WpfCustomerProjections.SingleAsync(c => c.Id == id);
        loaded.Platform.Should().Be("tiktok");
        loaded.FullName.Should().BeNull();
    }

    [Fact]
    public void LicenseId_Platform_Username_combo_indexed_for_match()
    {
        using var db = NewDb();
        var entityType = db.Model.FindEntityType(typeof(WpfCustomerProjection))!;
        entityType.GetIndexes().Should().Contain(i =>
            i.Properties.Count == 3
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.LicenseId))
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.Platform))
            && i.Properties.Any(p => p.Name == nameof(WpfCustomerProjection.Username)),
            "sipariş eşleşmesi için (LicenseId, Platform, Username) sorgulanacak");
    }
}
```

- [ ] **Step 2: Test fail bekleniyor**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WpfCustomerProjectionTests"
```

- [ ] **Step 3: `WpfCustomerProjection.cs` oluştur**

`OrderDeck.LicenseServer/Domain/WpfCustomerProjection.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// WPF lokal Customer kayıtlarının server-side hafif kopyası. Order/Payment
/// satırlarındaki CustomerId string'i bu tablodaki Id'yi (GUID hex) işaret
/// eder. WPF tarafı periyodik sync ile günceller (POST
/// /api/licenses/{id}/wpf-customers/sync — Faz 0c'de eklenir).
/// </summary>
public sealed class WpfCustomerProjection
{
    public Guid Id { get; set; }              // WPF lokal Customer.Id
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;
    public string Platform { get; set; } = "";
    public string Username { get; set; } = "";
    public string? FullName { get; set; }
    public string? Phone { get; set; }
    public string? Address { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 4: DbContext'e ekle**

DbSet:

```csharp
public DbSet<WpfCustomerProjection> WpfCustomerProjections => Set<WpfCustomerProjection>();
```

OnModelCreating:

```csharp
mb.Entity<WpfCustomerProjection>(b =>
{
    b.HasKey(c => c.Id);
    b.HasOne(c => c.License).WithMany().HasForeignKey(c => c.LicenseId)
     .OnDelete(DeleteBehavior.Cascade);
    b.Property(c => c.Platform).HasMaxLength(32).IsRequired();
    b.Property(c => c.Username).HasMaxLength(128).IsRequired();
    b.Property(c => c.FullName).HasMaxLength(200);
    b.Property(c => c.Phone).HasMaxLength(20);
    b.Property(c => c.Address).HasMaxLength(500);
    b.HasIndex(c => new { c.LicenseId, c.Platform, c.Username });
});
```

- [ ] **Step 5: Test pass + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~WpfCustomerProjectionTests"
git add OrderDeck.LicenseServer/Domain/WpfCustomerProjection.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/WpfCustomerProjectionTests.cs
git commit -m "feat(domain): WpfCustomerProjection (WPF customer server kopyası)"
```

---

## Task 7: `ShopperPushDevice` entity'sini oluştur

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/ShopperPushDevice.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/ShopperPushDeviceTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/ShopperPushDeviceTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperPushDeviceTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoppush-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_unique_per_shopper_device()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var shopperId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperPushDevices.Add(new ShopperPushDevice
        {
            Id = id,
            ShopperId = shopperId,
            DeviceId = "ios-uuid-1",
            Platform = "ios",
            PushToken = "apns-token",
            UpdatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperPushDevices.SingleAsync(d => d.Id == id);
        loaded.DeviceId.Should().Be("ios-uuid-1");
        loaded.PushToken.Should().Be("apns-token");
    }

    [Fact]
    public void ShopperId_DeviceId_pair_is_unique_so_reregister_upserts()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(ShopperPushDevice))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 2
                && i.Properties.Any(p => p.Name == nameof(ShopperPushDevice.ShopperId))
                && i.Properties.Any(p => p.Name == nameof(ShopperPushDevice.DeviceId)));
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Test fail bekleniyor**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperPushDeviceTests"
```

- [ ] **Step 3: `ShopperPushDevice.cs` oluştur**

`OrderDeck.LicenseServer/Domain/ShopperPushDevice.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper app cihazlarının FCM/APNs token kaydı. Mevcut PushDevice'tan ayrı
/// tablo (FK Shopper'a, yayıncıya değil). (ShopperId, DeviceId) upsert key.
/// </summary>
public sealed class ShopperPushDevice
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public string DeviceId { get; set; } = "";       // app-local UUID
    public string Platform { get; set; } = "";       // ios / android
    public string PushToken { get; set; } = "";
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 4: DbContext'e ekle**

DbSet:

```csharp
public DbSet<ShopperPushDevice> ShopperPushDevices => Set<ShopperPushDevice>();
```

OnModelCreating:

```csharp
mb.Entity<ShopperPushDevice>(b =>
{
    b.HasKey(d => d.Id);
    b.HasOne(d => d.Shopper).WithMany().HasForeignKey(d => d.ShopperId)
     .OnDelete(DeleteBehavior.Cascade);
    b.Property(d => d.DeviceId).HasMaxLength(64).IsRequired();
    b.Property(d => d.Platform).HasMaxLength(16).IsRequired();
    b.Property(d => d.PushToken).HasMaxLength(512).IsRequired();
    b.HasIndex(d => new { d.ShopperId, d.DeviceId }).IsUnique();
});
```

- [ ] **Step 5: Test pass + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperPushDeviceTests"
git add OrderDeck.LicenseServer/Domain/ShopperPushDevice.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/ShopperPushDeviceTests.cs
git commit -m "feat(domain): ShopperPushDevice (FCM/APNs token kaydı)"
```

---

## Task 8: `PaymentSubmissionAudit` entity'sini oluştur

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/PaymentSubmissionAudit.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/PaymentSubmissionAuditTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/PaymentSubmissionAuditTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class PaymentSubmissionAuditTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"audit-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_with_raw_parser_text()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.PaymentSubmissionAudits.Add(new PaymentSubmissionAudit
        {
            Id = id,
            PaymentId = Guid.NewGuid(),
            ShopperId = Guid.NewGuid(),
            IpAddress = "1.2.3.4",
            UserAgent = "OrderDeck-Shopper/1.0 (iOS 17)",
            FraudFlags = "iban-mismatch,low-confidence",
            ParserConfidence = "Low",
            ParserRawText = "Ödeyen: ALİ VELİ\nTutar: 250 TL",
            CreatedAt = now,
        });
        await db.SaveChangesAsync();

        var loaded = await db.PaymentSubmissionAudits.SingleAsync(a => a.Id == id);
        loaded.FraudFlags.Should().Contain("iban-mismatch");
        loaded.ParserRawText.Should().StartWith("Ödeyen:");
    }
}
```

- [ ] **Step 2: Test fail bekleniyor**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PaymentSubmissionAuditTests"
```

- [ ] **Step 3: `PaymentSubmissionAudit.cs` oluştur**

`OrderDeck.LicenseServer/Domain/PaymentSubmissionAudit.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper-tarafından upload edilen dekontun fraud denetim izi. 90 gün
/// retention (yayıncı approval kararından sonra). FraudFlags + ParserConfidence
/// karar gerekçesini tarihselleştirir.
/// </summary>
public sealed class PaymentSubmissionAudit
{
    public Guid Id { get; set; }
    public Guid PaymentId { get; set; }
    public Guid ShopperId { get; set; }
    public string IpAddress { get; set; } = "";
    public string UserAgent { get; set; } = "";
    public string FraudFlags { get; set; } = "";     // comma-separated tokens
    public string ParserConfidence { get; set; } = ""; // High/Medium/Low/Unknown
    public string? ParserRawText { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Step 4: DbContext'e ekle**

DbSet:

```csharp
public DbSet<PaymentSubmissionAudit> PaymentSubmissionAudits => Set<PaymentSubmissionAudit>();
```

OnModelCreating:

```csharp
mb.Entity<PaymentSubmissionAudit>(b =>
{
    b.HasKey(a => a.Id);
    b.Property(a => a.IpAddress).HasMaxLength(45).IsRequired();   // IPv6 dahil
    b.Property(a => a.UserAgent).HasMaxLength(512).IsRequired();
    b.Property(a => a.FraudFlags).HasMaxLength(256).IsRequired();
    b.Property(a => a.ParserConfidence).HasMaxLength(16).IsRequired();
    b.HasIndex(a => a.PaymentId);
    b.HasIndex(a => a.CreatedAt);   // retention job cursor için
});
```

- [ ] **Step 5: Test pass + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PaymentSubmissionAuditTests"
git add OrderDeck.LicenseServer/Domain/PaymentSubmissionAudit.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/PaymentSubmissionAuditTests.cs
git commit -m "feat(domain): PaymentSubmissionAudit (fraud denetim izi)"
```

---

## Task 9: `License`'a Shopper ek alanları ekle

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/License.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/LicenseShopperFieldsTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/LicenseShopperFieldsTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class LicenseShopperFieldsTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"licshopper-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_persists_shopper_code_and_payment_account()
    {
        await using var db = NewDb();

        // License için zorunlu FK'lar
        var customerId = Guid.NewGuid();
        var skuId = Guid.NewGuid();
        db.Customers.Add(new Customer
        {
            Id = customerId, Email = $"u-{customerId:N}@x", Name = "Test",
            PasswordHash = "h", CreatedAt = DateTimeOffset.UtcNow,
        });
        db.Skus.Add(new Sku { Id = skuId, Code = "S1", Name = "Sku 1", PriceTry = 100 });
        await db.SaveChangesAsync();

        var licenseId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customerId,
            SkuId = skuId,
            IssuedAt = now,
            ExpiresAt = now.AddYears(1),
            LicenseKey = "key-1",
            Status = LicenseStatus.Active,
            ShopperCode = "royal",
            ShopperCodeUpdatedAt = now,
            PaymentIban = "TR330006100519786457841326",
            PaymentAccountHolder = "BURAK YILMAZ",
            ShopperAppEnabled = true,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Licenses.SingleAsync(l => l.Id == licenseId);
        loaded.ShopperCode.Should().Be("royal");
        loaded.PaymentIban.Should().Be("TR330006100519786457841326");
        loaded.PaymentAccountHolder.Should().Be("BURAK YILMAZ");
        loaded.ShopperAppEnabled.Should().BeTrue();
    }

    [Fact]
    public void ShopperCode_is_unique_globally()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(License))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 1
                && i.Properties[0].Name == nameof(License.ShopperCode));
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Test fail bekleniyor (alanlar yok)**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~LicenseShopperFieldsTests"
```

Expected: Build error — `ShopperCode` çözülemiyor.

- [ ] **Step 3: `License.cs`'e alanlar ekle**

`OrderDeck.LicenseServer/Domain/License.cs` içine, mevcut son property'den sonra ekle:

```csharp
// Müşteri (shopper) app entegrasyonu — Faz 0a, 2026-05-20.
// ShopperCode: yayıncının müşterilerine paylaştığı davet kodu (lowercase
// case-insensitive unique). Düzenleme 7 gün cooldown'a tabi (Faz 0b'de).
// PaymentIban / PaymentAccountHolder: WPF Settings sync sonucu — dekont
// fraud kontrolünde RecipientIban karşılaştırması için.
// ShopperAppEnabled: feature flag — public rollout başlamadan kapalı kalır.
public string? ShopperCode { get; set; }
public DateTimeOffset? ShopperCodeUpdatedAt { get; set; }
public string? PaymentIban { get; set; }
public string? PaymentAccountHolder { get; set; }
public bool ShopperAppEnabled { get; set; }
```

- [ ] **Step 4: `LicenseDbContext`'te License config'i güncelle**

`OnModelCreating` içindeki `mb.Entity<License>(...)` bloğunu bul (yoksa ekle), aşağıdaki property + index'leri ekle (var olanı bozmadan):

```csharp
b.Property(l => l.ShopperCode).HasMaxLength(20);
b.HasIndex(l => l.ShopperCode).IsUnique();
b.Property(l => l.PaymentIban).HasMaxLength(34);
b.Property(l => l.PaymentAccountHolder).HasMaxLength(200);
```

- [ ] **Step 5: Test pass + tüm Domain test'leri yeşil**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~Domain"
```

Expected: tüm Domain test'leri pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/License.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/LicenseShopperFieldsTests.cs
git commit -m "feat(domain): License — ShopperCode + IBAN + ShopperAppEnabled"
```

---

## Task 10: `Payment`'a Shopper ek alanları ekle

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/Payment.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/PaymentShopperFieldsTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/PaymentShopperFieldsTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class PaymentShopperFieldsTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"payshopper-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_persists_shopper_upload_metadata()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.Payments.Add(new Payment
        {
            Id = id,
            LicenseId = Guid.NewGuid(),
            PayerName = "ALİ VELİ",
            Amount = 250m,
            PaidAt = now,
            ReferansNo = "REF-1",
            Status = PaymentStatus.Pending,
            ShipmentDirective = ShipmentDirective.Normal,
            CreatedAt = now,
            UpdatedAt = now,
            // Yeni alanlar:
            ShopperId = Guid.NewGuid(),
            MediaObjectKey = "r2/payments/1.pdf",
            MediaContentType = "application/pdf",
            PdfHash = "sha256-abc",
            MetadataHash = "sha256-meta",
            RecipientIban = "TR33...",
            RecipientName = "BURAK YILMAZ",
            FraudFlags = "iban-mismatch",
            ParserConfidence = "Medium",
            PdfPurgedAt = null,
        });
        await db.SaveChangesAsync();

        var loaded = await db.Payments.SingleAsync(p => p.Id == id);
        loaded.ShopperId.Should().NotBeNull();
        loaded.MediaContentType.Should().Be("application/pdf");
        loaded.PdfHash.Should().Be("sha256-abc");
        loaded.FraudFlags.Should().Be("iban-mismatch");
        loaded.ParserConfidence.Should().Be("Medium");
        loaded.PdfPurgedAt.Should().BeNull();
    }

    [Fact]
    public void PdfHash_is_global_unique_to_prevent_cross_tenant_replay()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(Payment))!
            .GetIndexes()
            .SingleOrDefault(i =>
                i.Properties.Count == 1
                && i.Properties[0].Name == nameof(Payment.PdfHash));
        index.Should().NotBeNull();
        index!.IsUnique.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Test fail bekleniyor**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PaymentShopperFieldsTests"
```

- [ ] **Step 3: `Payment.cs`'e alanlar ekle**

`OrderDeck.LicenseServer/Domain/Payment.cs` içine, mevcut son property'den sonra ekle:

```csharp
// Müşteri (shopper) app upload alanları — Faz 0a, 2026-05-20.
// ShopperId null = legacy WhatsApp akışından gelen dekont.
public Guid? ShopperId { get; set; }
public string? MediaObjectKey { get; set; }
public string? MediaContentType { get; set; }
public string? PdfHash { get; set; }              // SHA256 of PDF bytes
public string? MetadataHash { get; set; }         // SHA256 of canonical fields
public string? RecipientIban { get; set; }
public string? RecipientName { get; set; }
public string FraudFlags { get; set; } = "";     // comma-separated
public string ParserConfidence { get; set; } = "Unknown";
public DateTimeOffset? PdfPurgedAt { get; set; }
```

- [ ] **Step 4: `LicenseDbContext`'te Payment config'i güncelle**

`mb.Entity<Payment>(...)` bloğuna ekle:

```csharp
b.Property(p => p.MediaObjectKey).HasMaxLength(256);
b.Property(p => p.MediaContentType).HasMaxLength(128);
b.Property(p => p.PdfHash).HasMaxLength(64);
b.HasIndex(p => p.PdfHash).IsUnique();
b.Property(p => p.MetadataHash).HasMaxLength(64);
b.HasIndex(p => p.MetadataHash);
b.Property(p => p.RecipientIban).HasMaxLength(34);
b.Property(p => p.RecipientName).HasMaxLength(200);
b.Property(p => p.FraudFlags).HasMaxLength(256).IsRequired();
b.Property(p => p.ParserConfidence).HasMaxLength(16).IsRequired();
b.HasIndex(p => p.ShopperId);
```

- [ ] **Step 5: Test pass + commit**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PaymentShopperFieldsTests"
git add OrderDeck.LicenseServer/Domain/Payment.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/PaymentShopperFieldsTests.cs
git commit -m "feat(domain): Payment — Shopper upload + PDF + fraud alanları"
```

---

## Task 11: EF migration üret

**Files:**
- Create (auto): `OrderDeck.LicenseServer/Migrations/<timestamp>_AddShopperFoundation.cs`
- Create (auto): `OrderDeck.LicenseServer/Migrations/<timestamp>_AddShopperFoundation.Designer.cs`
- Modify (auto): `OrderDeck.LicenseServer/Migrations/LicenseDbContextModelSnapshot.cs`

- [ ] **Step 1: Migration'ı oluştur**

Çalışma dizini repo kökü:

```bash
dotnet ef migrations add AddShopperFoundation \
  --project OrderDeck.LicenseServer \
  --output-dir Migrations
```

Expected: 3 dosya değişimi (yeni migration + designer + snapshot güncellenir).

- [ ] **Step 2: Migration dosyasını gözle kontrol et**

```bash
ls OrderDeck.LicenseServer/Migrations/ | tail -3
```

Beklenen yeni dosya: `<timestamp>_AddShopperFoundation.cs`. İçinde `Up()` metodu altında `CreateTable("Shoppers", ...)`, `CreateTable("ShopperBroadcasterLinks", ...)`, `CreateTable("WpfCustomerProjections", ...)`, `CreateTable("ShopperPushDevices", ...)`, `CreateTable("PaymentSubmissionAudits", ...)` ve `AddColumn` çağrıları (License + Payment için) bulunmalı. `Down()` simetrik silme operasyonları içermeli.

- [ ] **Step 3: Build et — migration kod-doğru olmalı**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Tam test suite'i çalıştır — InMemory provider yeni şemayı kabul ediyor mu**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Expected: Tüm testler pass (Domain testleri yeni şemayı, mevcut testler etkilenmemiş).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Migrations/
git commit -m "feat(migrations): AddShopperFoundation — 5 yeni tablo + License/Payment ek kolonlar"
```

---

## Task 12: `JwtOptions`'a `ShopperAudience` sabiti ekle

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Auth/JwtOptions.cs`

- [ ] **Step 1: Mevcut dosyayı oku**

```bash
cat OrderDeck.LicenseServer/Services/Auth/JwtOptions.cs
```

`CustomerAudience` ve `AdminAudience` sabitlerini gör. Yeni sabit aynı kalıpta eklenir.

- [ ] **Step 2: `ShopperAudience` ekle**

Mevcut sınıfın içine, `AdminAudience` const'ının hemen altına ekle:

```csharp
public const string ShopperAudience = "orderdeck-shopper";
```

- [ ] **Step 3: Build et**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Expected: Build succeeded.

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Auth/JwtOptions.cs
git commit -m "chore(auth): JwtOptions.ShopperAudience sabiti"
```

---

## Task 13: `Bearer-Shopper` JWT scheme'i `Program.cs`'te kaydet

**Files:**
- Modify: `OrderDeck.LicenseServer/Program.cs`
- Create: `OrderDeck.LicenseServer.Tests/Auth/BearerShopperSchemeTests.cs`

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Auth/BearerShopperSchemeTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

public class BearerShopperSchemeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public BearerShopperSchemeTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task BearerShopper_scheme_is_registered()
    {
        using var scope = _factory.Services.CreateScope();
        var provider = scope.ServiceProvider
            .GetRequiredService<IAuthenticationSchemeProvider>();

        var scheme = await provider.GetSchemeAsync("Bearer-Shopper");
        scheme.Should().NotBeNull("Bearer-Shopper scheme Program.cs'te tescil edilmiş olmalı");
    }

    [Fact]
    public void BearerShopper_token_validation_uses_ShopperAudience()
    {
        using var scope = _factory.Services.CreateScope();
        var opts = scope.ServiceProvider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get("Bearer-Shopper");

        opts.TokenValidationParameters.ValidAudience
            .Should().Be(JwtOptions.ShopperAudience);
        opts.TokenValidationParameters.ValidateAudience.Should().BeTrue();
        opts.TokenValidationParameters.ValidateIssuer.Should().BeTrue();
        opts.TokenValidationParameters.ValidateLifetime.Should().BeTrue();
    }
}
```

- [ ] **Step 2: Test fail bekleniyor**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BearerShopperSchemeTests"
```

Expected: 2 fail — scheme yok / options yok.

- [ ] **Step 3: `Program.cs`'te scheme ekle**

`OrderDeck.LicenseServer/Program.cs`:154 civarındaki `AddAuthentication()` chain'inde, `Bearer-Admin`'den sonra:

```csharp
.AddJwtBearer("Bearer-Customer", _ => { })
.AddJwtBearer("Bearer-Admin", _ => { })
.AddJwtBearer("Bearer-Shopper", _ => { })
```

`AddOptions<JwtBearerOptions>("Bearer-Admin")...` bloğunun hemen altına, aynı kalıpla:

```csharp
builder.Services.AddOptions<JwtBearerOptions>("Bearer-Shopper")
    .Configure<IOptions<JwtOptions>>((o, jwtOpts) =>
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOpts.Value.SecretKey));
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwtOpts.Value.Issuer,
            ValidateAudience = true, ValidAudience = JwtOptions.ShopperAudience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = key,
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });
```

- [ ] **Step 4: Test pass**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~BearerShopperSchemeTests"
```

Expected: 2/2 pass.

- [ ] **Step 5: Tüm test suite yeşil**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Expected: tüm testler pass (PR #74'teki convention test dahil — yeni scheme Panel controllers'ı etkilemiyor).

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Auth/BearerShopperSchemeTests.cs
git commit -m "feat(auth): Bearer-Shopper JWT scheme registration"
```

---

## Task 14: PR aç

- [ ] **Step 1: Branch push**

```bash
git push -u origin chore/customer-app-faz-0a
```

- [ ] **Step 2: PR aç**

```bash
gh pr create --title "feat(shopper-foundation): Faz 0a — parser lib + 5 entity + License/Payment alanları + Bearer-Shopper scheme" --body "$(cat <<'EOF'
## Summary

Müşteri (shopper) app'i için server foundation. Hiçbir endpoint yok — sadece veri modeli ve auth scheme infrastructure. Mevcut yayıncı akışlarını etkilemiyor.

### Yapılan

- \`OrderDeck.PdfParsing\` lib extract: \`PdfDekontParser\` taşındı, WPF (\`OrderDeck.Core\`) + LicenseServer ortak referans
- 5 yeni entity:
  - \`Shopper\` — müşteri app kullanıcısı (telefon global unique)
  - \`ShopperBroadcasterLink\` — N:N pivot, per-pair Platform + Username
  - \`WpfCustomerProjection\` — WPF lokal Customer kayıtlarının server kopyası (sipariş eşleşmesi için, Faz 0c'de sync'lenir)
  - \`ShopperPushDevice\` — FCM/APNs token kaydı (Shopper FK)
  - \`PaymentSubmissionAudit\` — fraud denetim izi
- \`License\` ek alanları: \`ShopperCode\` (case-insensitive unique), \`ShopperCodeUpdatedAt\`, \`PaymentIban\`, \`PaymentAccountHolder\`, \`ShopperAppEnabled\`
- \`Payment\` ek alanları: \`ShopperId\`, \`MediaObjectKey\`, \`MediaContentType\`, \`PdfHash\` (global unique), \`MetadataHash\`, \`RecipientIban\`, \`RecipientName\`, \`FraudFlags\`, \`ParserConfidence\`, \`PdfPurgedAt\`
- Tek EF migration \`AddShopperFoundation\` — 5 tablo + 15 yeni kolon
- \`Bearer-Shopper\` JWT scheme + \`JwtOptions.ShopperAudience\`

## Spec referansı

\`docs/superpowers/specs/2026-05-20-customer-app-design.md\` — Faz 0a tamamlandı, sıra Faz 0b (shopper endpoints) ve Faz 0c (WPF sync + UI).

## Test plan

- [x] \`dotnet test\` → tüm testler yeşil (yeni 11 test eklendi)
- [ ] Production DB'ye migration apply — staging önce, sonra prod
- [ ] Migration \`Down()\` rollback test (staging)

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

Expected: PR URL output.

---

## Self-Review (planı yazan tarafından)

**Spec coverage** — Faz 0a maddeleri:

| Spec maddesi | Task |
|--------------|------|
| `OrderDeck.PdfParsing` lib extract | 1, 2, 3 |
| Entity: Shopper | 4 |
| Entity: ShopperBroadcasterLink | 5 |
| Entity: WpfCustomerProjection | 6 |
| Entity: ShopperPushDevice | 7 |
| Entity: PaymentSubmissionAudit | 8 |
| License ek alanları | 9 |
| Payment ek alanları | 10 |
| EF migration | 11 |
| Bearer-Shopper JWT scheme | 12, 13 |

Faz 0a kapsamı dışı: WPF tarafı sync, endpoint'ler, UI — bunlar 0b ve 0c'de.

**Placeholder taraması**: TBD/TODO/FIXME yok. Tüm kod blokları gerçek içerikli. Komut çıktıları (expected) belirtilmiş.

**Type consistency**:
- `Shopper.Phone` — string, max 20 (E.164 buna sığar: `+90` + 10 hane = 13 char)
- `License.ShopperCode` — string?, max 20 (spec: 3-20 karakter)
- `Payment.PdfHash` — string?, max 64 (SHA256 hex = 64 char)
- `Payment.FraudFlags` — required string default "" (test'te bu kullanıldı)
- `JwtOptions.ShopperAudience` — const "orderdeck-shopper" (spec ile uyumlu)

Tutarsızlık bulunmadı.

---

## Sonraki Plan'lar (Faz 0a merge sonrası)

- `2026-05-20-customer-app-faz-0b-shopper-endpoints.md` — 0a'ya bağlı, tüm `/api/v1/shopper/*` endpoint'leri
- `2026-05-20-customer-app-faz-0c-broadcaster-side.md` — 0a'ya bağlı, paralel ilerleyebilir; WPF Customer sync + IBAN sync + WPF Ayarlar UI + mobile panel ShopperCode UI
