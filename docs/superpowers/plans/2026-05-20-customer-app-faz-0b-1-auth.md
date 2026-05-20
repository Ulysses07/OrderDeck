# Müşteri (Shopper) App — Faz 0b-1: Auth + Me + Code Lookup Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development.

**Goal:** Shopper-facing auth endpoint'leri (register/login/refresh/forgot-password/change-password) + me endpoints (get/patch/delete) + anonim broadcaster code lookup. Mevcut Argon2id PasswordHasher reuse, JwtTokenService genişletilir, ShopperRefreshToken rotation pattern mevcut RefreshToken'dan kopyalanır.

**Architecture:** Mevcut `PasswordHasher` (Argon2id) ve `JwtTokenService` genişletilir. Yeni `ShopperRefreshToken` entity + `ShopperRefreshTokenService` (hash storage + rotation chain). Yeni `ShopperSupportRequest` entity forgot-password için. Tüm endpoint'ler `/api/v1/shopper/*` altında, `Bearer-Shopper` scheme + `[AllowAnonymous]` mix. `ShopperControllerConventionTests` future drift guard.

**Tech Stack:** ASP.NET Core, EF Core, Argon2id (mevcut), JWT bearer, xUnit + FluentAssertions + InMemoryDatabase.

---

## File Structure

**New files:**
- `OrderDeck.LicenseServer/Domain/ShopperRefreshToken.cs`
- `OrderDeck.LicenseServer/Domain/ShopperSupportRequest.cs`
- `OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs`
- `OrderDeck.LicenseServer/Services/Auth/ShopperRefreshTokenService.cs`
- `OrderDeck.LicenseServer/Controllers/Shopper/ShopperAuthController.cs`
- `OrderDeck.LicenseServer/Controllers/Shopper/ShopperMeController.cs`
- `OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs`
- `OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs`
- `OrderDeck.LicenseServer.Tests/Services/Auth/ShopperRefreshTokenServiceTests.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperControllerConventionTests.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperBroadcastersCodeLookupTests.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperAuthControllerTests.cs`
- `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperMeControllerTests.cs`
- `OrderDeck.LicenseServer/Data/Migrations/<ts>_AddShopperAuth.cs` (auto-generated)

**Modified files:**
- `OrderDeck.LicenseServer/Services/Auth/JwtTokenService.cs` — `IssueShopperToken` metodu eklenir
- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — 2 yeni DbSet + config
- `OrderDeck.LicenseServer/Program.cs` — `ShopperRefreshTokenService` + `PhoneNormalizer` DI register

---

## Task 1: `ShopperRefreshToken` + `ShopperSupportRequest` entities + migration

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/ShopperRefreshToken.cs`
- Create: `OrderDeck.LicenseServer/Domain/ShopperSupportRequest.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer.Tests/Domain/ShopperRefreshTokenEntityTests.cs`
- Create: migration

- [ ] **Step 1: Failing test yaz**

`OrderDeck.LicenseServer.Tests/Domain/ShopperRefreshTokenEntityTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class ShopperRefreshTokenEntityTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoprt-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Roundtrip_refresh_token_with_rotation_chain()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = id,
            ShopperId = Guid.NewGuid(),
            TokenHash = new string('a', 64),
            CreatedAt = now,
            ExpiresAt = now.AddDays(90),
            CreatedByIp = "1.2.3.4",
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperRefreshTokens.SingleAsync(t => t.Id == id);
        loaded.TokenHash.Should().HaveLength(64);
        loaded.RevokedAt.Should().BeNull();
        loaded.ReplacedByTokenHash.Should().BeNull();
    }

    [Fact]
    public void TokenHash_is_indexed_for_lookup()
    {
        using var db = NewDb();
        var index = db.Model.FindEntityType(typeof(ShopperRefreshToken))!
            .GetIndexes()
            .SingleOrDefault(i => i.Properties.Count == 1
                && i.Properties[0].Name == nameof(ShopperRefreshToken.TokenHash));
        index.Should().NotBeNull("TokenHash refresh akışında her istekte sorgulanacak");
    }

    [Fact]
    public async Task Support_request_roundtrip()
    {
        await using var db = NewDb();
        var id = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;

        db.ShopperSupportRequests.Add(new ShopperSupportRequest
        {
            Id = id,
            ShopperId = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),
            Kind = "forgot-password",
            CreatedAt = now,
            ResolvedAt = null,
        });
        await db.SaveChangesAsync();

        var loaded = await db.ShopperSupportRequests.SingleAsync(r => r.Id == id);
        loaded.Kind.Should().Be("forgot-password");
        loaded.ResolvedAt.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run — fail (entities don't exist)**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperRefreshTokenEntityTests"
```

- [ ] **Step 3: Create entities**

`OrderDeck.LicenseServer/Domain/ShopperRefreshToken.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Mevcut <see cref="RefreshToken"/> (yayıncı/Customer) pattern'inin shopper
/// karşılığı. Raw token client'a bir kez gösterilir, DB'de yalnız SHA-256 hash
/// saklanır. Rotation chain için <see cref="ReplacedByTokenHash"/>.
/// </summary>
public sealed class ShopperRefreshToken
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;

    /// <summary>SHA-256 of the raw token, lowercase hex (64 chars).</summary>
    public string TokenHash { get; set; } = "";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public string? ReplacedByTokenHash { get; set; }
    public string? CreatedByIp { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/ShopperSupportRequest.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Shopper'ın yayıncıya manuel destek talebi (Faz 0b-1: forgot-password için).
/// Faz 0c'de yayıncı paneli "Destek talepleri" bölümünde gösterilir; yayıncı
/// WhatsApp'tan manuel cevap verir. Kind = "forgot-password" şimdilik tek tür.
/// </summary>
public sealed class ShopperSupportRequest
{
    public Guid Id { get; set; }
    public Guid ShopperId { get; set; }
    public Shopper Shopper { get; set; } = null!;
    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public string Kind { get; set; } = "";   // "forgot-password"
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ResolvedAt { get; set; }
}
```

- [ ] **Step 4: DbContext — DbSets + config**

`LicenseDbContext.cs` içinde DbSets bölümüne:

```csharp
public DbSet<ShopperRefreshToken> ShopperRefreshTokens => Set<ShopperRefreshToken>();
public DbSet<ShopperSupportRequest> ShopperSupportRequests => Set<ShopperSupportRequest>();
```

`OnModelCreating` içine:

```csharp
mb.Entity<ShopperRefreshToken>(b =>
{
    b.HasKey(t => t.Id);
    b.HasOne(t => t.Shopper).WithMany().HasForeignKey(t => t.ShopperId)
     .OnDelete(DeleteBehavior.Cascade);
    b.Property(t => t.TokenHash).HasMaxLength(64).IsRequired();
    b.HasIndex(t => t.TokenHash);
    b.Property(t => t.ReplacedByTokenHash).HasMaxLength(64);
    b.Property(t => t.CreatedByIp).HasMaxLength(45);
});

mb.Entity<ShopperSupportRequest>(b =>
{
    b.HasKey(r => r.Id);
    b.HasOne(r => r.Shopper).WithMany().HasForeignKey(r => r.ShopperId)
     .OnDelete(DeleteBehavior.Cascade);
    b.HasOne(r => r.License).WithMany().HasForeignKey(r => r.LicenseId)
     .OnDelete(DeleteBehavior.Cascade);
    b.Property(r => r.Kind).HasMaxLength(32).IsRequired();
    b.HasIndex(r => new { r.LicenseId, r.ResolvedAt, r.CreatedAt });
});
```

- [ ] **Step 5: Test pass**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperRefreshTokenEntityTests"
```

Expected: 3/3 pass.

- [ ] **Step 6: Migration**

```bash
dotnet ef migrations add AddShopperAuth \
  --project OrderDeck.LicenseServer \
  --context LicenseDbContext \
  --output-dir Data/Migrations
```

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/ShopperRefreshToken.cs \
        OrderDeck.LicenseServer/Domain/ShopperSupportRequest.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Domain/ShopperRefreshTokenEntityTests.cs \
        OrderDeck.LicenseServer/Data/Migrations/
git commit -m "feat(domain): ShopperRefreshToken + ShopperSupportRequest + migration"
```

---

## Task 2: `PhoneNormalizer` service

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs`

- [ ] **Step 1: Failing test**

`OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("5551112233", "+905551112233")]
    [InlineData("05551112233", "+905551112233")]
    [InlineData("+905551112233", "+905551112233")]
    [InlineData("905551112233", "+905551112233")]
    [InlineData("0 555 111 22 33", "+905551112233")]
    [InlineData("0555-111-22-33", "+905551112233")]
    [InlineData("+90 555 111 2233", "+905551112233")]
    public void Normalize_returns_E164_for_valid_TR_input(string input, string expected)
    {
        PhoneNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]                          // too short
    [InlineData("5551112233xx")]                 // letters
    [InlineData("+15551112233")]                 // non-TR country code
    [InlineData("5551112233444")]                // too long
    public void Normalize_throws_for_invalid_input(string input)
    {
        Action act = () => PhoneNormalizer.Normalize(input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryNormalize_returns_false_for_invalid()
    {
        PhoneNormalizer.TryNormalize("garbage", out var result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void TryNormalize_returns_true_for_valid()
    {
        PhoneNormalizer.TryNormalize("0555 111 2233", out var result).Should().BeTrue();
        result.Should().Be("+905551112233");
    }
}
```

- [ ] **Step 2: Run — fail**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PhoneNormalizerTests"
```

- [ ] **Step 3: Create `PhoneNormalizer.cs`**

```csharp
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// TR telefon numarası normalize edici. Çıktı: E.164 format `+90XXXXXXXXXX`.
/// Boşluk, tire, parantez temizlenir. Prefix değişimleri:
///   0XXXXXXXXXX  → +90XXXXXXXXXX
///   90XXXXXXXXXX → +90XXXXXXXXXX
///   XXXXXXXXXX   → +90XXXXXXXXXX   (10 hane gönderilirse)
/// Diğer ülke kodları (örn. +1) reddedilir — sadece TR pazarı destekleniyor.
/// </summary>
public static class PhoneNormalizer
{
    private static readonly Regex DigitOrPlus = new(@"[^\d+]", RegexOptions.Compiled);

    public static string Normalize(string raw)
    {
        if (TryNormalize(raw, out var result))
            return result!;
        throw new ArgumentException("Geçersiz TR telefon numarası.", nameof(raw));
    }

    public static bool TryNormalize(string? raw, out string? result)
    {
        result = null;
        if (string.IsNullOrWhiteSpace(raw)) return false;

        // Boşluk, tire, parantez vs. temizle. Sadece rakam ve '+' kalsın.
        var cleaned = DigitOrPlus.Replace(raw, "");

        // + olmayan ama başında 0 ya da 90 olan varyantlar
        if (cleaned.StartsWith("+"))
        {
            if (!cleaned.StartsWith("+90")) return false;
            cleaned = cleaned[3..];
        }
        else if (cleaned.StartsWith("90") && cleaned.Length == 12)
        {
            cleaned = cleaned[2..];
        }
        else if (cleaned.StartsWith("0") && cleaned.Length == 11)
        {
            cleaned = cleaned[1..];
        }

        // Şimdi 10 hane bekleniyor (TR mobil/sabit)
        if (cleaned.Length != 10) return false;
        if (!cleaned.All(char.IsDigit)) return false;

        result = "+90" + cleaned;
        return true;
    }
}
```

- [ ] **Step 4: Test pass**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PhoneNormalizerTests"
```

Expected: 13/13 pass (7 theory + 6 individual).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Auth/PhoneNormalizer.cs \
        OrderDeck.LicenseServer.Tests/Services/Auth/PhoneNormalizerTests.cs
git commit -m "feat(auth): PhoneNormalizer (TR E.164)"
```

---

## Task 3: `JwtTokenService.IssueShopperToken` metodu ekle

**Files:**
- Modify: `OrderDeck.LicenseServer/Services/Auth/JwtTokenService.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/Auth/JwtTokenServiceShopperTests.cs`

- [ ] **Step 1: Failing test**

`OrderDeck.LicenseServer.Tests/Services/Auth/JwtTokenServiceShopperTests.cs`:

```csharp
using System.IdentityModel.Tokens.Jwt;
using FluentAssertions;
using Microsoft.Extensions.Options;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class JwtTokenServiceShopperTests
{
    private static JwtTokenService NewService() =>
        new(Options.Create(new JwtOptions
        {
            SecretKey = new string('k', 64),
            Issuer = "orderdeck-test",
            AccessTokenLifetimeMinutes = 30,
        }));

    [Fact]
    public void IssueShopperToken_emits_signed_jwt_with_shopper_audience()
    {
        var svc = NewService();
        var shopperId = Guid.NewGuid();
        var (token, expiresAt) = svc.IssueShopperToken(shopperId, "+905551112233");

        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        jwt.Audiences.Should().Contain(JwtOptions.ShopperAudience);
        jwt.Claims.Should().Contain(c => c.Type == "sub" && c.Value == shopperId.ToString());
        jwt.Claims.Should().Contain(c => c.Type == "principal" && c.Value == "shopper");
        jwt.Claims.Should().Contain(c => c.Type == "phone" && c.Value == "+905551112233");
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(25));
        expiresAt.Should().BeBefore(DateTimeOffset.UtcNow.AddMinutes(35));
    }
}
```

- [ ] **Step 2: Run — fail (IssueShopperToken doesn't exist)**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~JwtTokenServiceShopperTests"
```

- [ ] **Step 3: Edit `JwtTokenService.cs` — add method after `IssueAdminToken`**

```csharp
/// <summary>
/// Shopper (müşteri app kullanıcısı) token. `sub` = shopperId. `principal=shopper`
/// claim'i Bearer-Customer (yayıncı) ve Bearer-Admin'den ayırt eder. `phone`
/// claim'i sipariş eşleşme join'lerinde okunabilir.
/// </summary>
public (string Token, DateTimeOffset ExpiresAt) IssueShopperToken(Guid shopperId, string phone)
{
    var lifetimeMinutes = _options.AccessTokenLifetimeMinutes > 0
        ? _options.AccessTokenLifetimeMinutes
        : 30;
    var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
    var token = Build(JwtOptions.ShopperAudience, expiresAt,
        new Claim("sub", shopperId.ToString()),
        new Claim("principal", "shopper"),
        new Claim("phone", phone));
    return (token, expiresAt);
}
```

- [ ] **Step 4: Test pass**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~JwtTokenServiceShopperTests"
```

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Auth/JwtTokenService.cs \
        OrderDeck.LicenseServer.Tests/Services/Auth/JwtTokenServiceShopperTests.cs
git commit -m "feat(auth): JwtTokenService.IssueShopperToken"
```

---

## Task 4: `ShopperRefreshTokenService` (hash + rotation)

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Auth/ShopperRefreshTokenService.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (DI register)
- Create: `OrderDeck.LicenseServer.Tests/Services/Auth/ShopperRefreshTokenServiceTests.cs`

- [ ] **Step 1: Failing test**

`OrderDeck.LicenseServer.Tests/Services/Auth/ShopperRefreshTokenServiceTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class ShopperRefreshTokenServiceTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase($"shoprtsvc-{Guid.NewGuid():N}")
            .Options);

    [Fact]
    public async Task Issue_creates_db_row_and_returns_raw_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopperId = Guid.NewGuid();

        var (raw, expiresAt) = await svc.IssueAsync(shopperId, "1.2.3.4", default);

        raw.Should().NotBeNullOrWhiteSpace().And.HaveLength(64);
        expiresAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(89));

        var row = await db.ShopperRefreshTokens.SingleAsync();
        row.ShopperId.Should().Be(shopperId);
        row.TokenHash.Should().HaveLength(64);
        row.TokenHash.Should().NotBe(raw);   // hash != raw
        row.RevokedAt.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_revokes_old_and_returns_new_chain()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var shopperId = Guid.NewGuid();
        var (oldRaw, _) = await svc.IssueAsync(shopperId, "1.2.3.4", default);

        var (newRaw, _) = await svc.RotateAsync(oldRaw, "5.6.7.8", default)
            ?? throw new InvalidOperationException("rotate returned null");

        var rows = await db.ShopperRefreshTokens.OrderBy(t => t.CreatedAt).ToListAsync();
        rows.Should().HaveCount(2);
        rows[0].RevokedAt.Should().NotBeNull();
        rows[0].ReplacedByTokenHash.Should().Be(rows[1].TokenHash);
        rows[1].ShopperId.Should().Be(shopperId);
    }

    [Fact]
    public async Task Rotate_returns_null_for_unknown_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var result = await svc.RotateAsync("notarealtoken", "1.2.3.4", default);
        result.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_returns_null_for_already_revoked_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        var (raw, _) = await svc.IssueAsync(Guid.NewGuid(), "1.2.3.4", default);
        await svc.RotateAsync(raw, "1.2.3.4", default);   // first rotate OK
        var second = await svc.RotateAsync(raw, "1.2.3.4", default);
        second.Should().BeNull();
    }

    [Fact]
    public async Task Rotate_returns_null_for_expired_token()
    {
        await using var db = NewDb();
        var svc = new ShopperRefreshTokenService(db);
        // Force expire by inserting directly
        var rawSeed = new string('x', 64);
        var hash = ShopperRefreshTokenService.HashForTest(rawSeed);
        db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = Guid.NewGuid(),
            TokenHash = hash,
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-100),
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(-10),
        });
        await db.SaveChangesAsync();

        var result = await svc.RotateAsync(rawSeed, "1.2.3.4", default);
        result.Should().BeNull();
    }
}
```

- [ ] **Step 2: Run — fail**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperRefreshTokenServiceTests"
```

- [ ] **Step 3: Create service**

`OrderDeck.LicenseServer/Services/Auth/ShopperRefreshTokenService.cs`:

```csharp
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Shopper refresh token issue + rotate. Raw token sadece dönüş değerinde
/// görünür; DB'de yalnız SHA-256 hash saklanır. Rotation single-use:
/// eski token kullanılınca revoked, yenisi ReplacedByTokenHash ile zincir
/// halinde tutulur.
/// </summary>
public sealed class ShopperRefreshTokenService
{
    private const int LifetimeDays = 90;
    private readonly LicenseDbContext _db;

    public ShopperRefreshTokenService(LicenseDbContext db) => _db = db;

    public async Task<(string Raw, DateTimeOffset ExpiresAt)> IssueAsync(
        Guid shopperId, string? createdByIp, CancellationToken ct)
    {
        var raw = GenerateRaw();
        var now = DateTimeOffset.UtcNow;
        var expiresAt = now.AddDays(LifetimeDays);
        _db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = shopperId,
            TokenHash = Hash(raw),
            CreatedAt = now,
            ExpiresAt = expiresAt,
            CreatedByIp = createdByIp,
        });
        await _db.SaveChangesAsync(ct);
        return (raw, expiresAt);
    }

    public async Task<(Guid ShopperId, string NewRaw, DateTimeOffset NewExpiresAt)?> RotateAsync(
        string oldRaw, string? createdByIp, CancellationToken ct)
    {
        var oldHash = Hash(oldRaw);
        var old = await _db.ShopperRefreshTokens
            .FirstOrDefaultAsync(t => t.TokenHash == oldHash, ct);

        if (old is null) return null;
        if (old.RevokedAt is not null) return null;
        if (old.ExpiresAt < DateTimeOffset.UtcNow) return null;

        var newRaw = GenerateRaw();
        var newHash = Hash(newRaw);
        var now = DateTimeOffset.UtcNow;
        var newExpiresAt = now.AddDays(LifetimeDays);

        old.RevokedAt = now;
        old.ReplacedByTokenHash = newHash;

        _db.ShopperRefreshTokens.Add(new ShopperRefreshToken
        {
            Id = Guid.NewGuid(),
            ShopperId = old.ShopperId,
            TokenHash = newHash,
            CreatedAt = now,
            ExpiresAt = newExpiresAt,
            CreatedByIp = createdByIp,
        });
        await _db.SaveChangesAsync(ct);
        return (old.ShopperId, newRaw, newExpiresAt);
    }

    private static string GenerateRaw()
    {
        var bytes = RandomNumberGenerator.GetBytes(32);
        return Convert.ToHexString(bytes).ToLowerInvariant();   // 64 hex chars
    }

    private static string Hash(string raw)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(raw));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>Test'lerin DB'ye seed atması için hash exposure.</summary>
    public static string HashForTest(string raw) => Hash(raw);
}
```

- [ ] **Step 4: `Program.cs` DI register**

`builder.Services.AddSingleton<PasswordHasher>();` satırının yakınında (yaklaşık satır 65):

```csharp
builder.Services.AddScoped<ShopperRefreshTokenService>();
```

(`Scoped` çünkü `DbContext`'e bağımlı.)

- [ ] **Step 5: Test pass**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperRefreshTokenServiceTests"
```

Expected: 5/5 pass.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Auth/ShopperRefreshTokenService.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Auth/ShopperRefreshTokenServiceTests.cs
git commit -m "feat(auth): ShopperRefreshTokenService (hash + rotation)"
```

---

## Task 5: `ShopperControllerConventionTests` (future drift guard)

**Files:**
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperControllerConventionTests.cs`

- [ ] **Step 1: Create test (no implementation needed; checks all future Shopper controllers)**

```csharp
using System.Reflection;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

/// <summary>
/// PR #74'teki PanelControllerConventionTests'in shopper karşılığı. Future
/// Shopper controller'ları eklenirken Bearer-Shopper auth attribute'unu
/// unutmak kolay — bu test CI'da yakar.
/// </summary>
public class ShopperControllerConventionTests
{
    private const string ExpectedAuthScheme = "Bearer-Shopper";
    private const string ShopperNamespace = "OrderDeck.LicenseServer.Controllers.Shopper";

    private static Type[] DiscoverShopperControllers() =>
        typeof(Program).Assembly.GetTypes()
            .Where(t => t.IsClass
                && !t.IsAbstract
                && t.Namespace == ShopperNamespace
                && typeof(ControllerBase).IsAssignableFrom(t))
            .OrderBy(t => t.FullName)
            .ToArray();

    [Fact]
    public void All_shopper_controllers_require_BearerShopper_or_AllowAnonymous_per_action()
    {
        var controllers = DiscoverShopperControllers();

        // Faz 0b-1 başlangıcında 3 controller bekleniyor. Bu sayı arttıkça test
        // halen geçer — sadece NotEmpty check.
        controllers.Should().NotBeEmpty("Shopper namespace'inde controller bekleniyor");

        var offenders = new List<string>();
        foreach (var t in controllers)
        {
            var classAuth = t.GetCustomAttribute<AuthorizeAttribute>(inherit: true);
            var classIsAllowAnon = t.GetCustomAttribute<AllowAnonymousAttribute>(inherit: true) is not null;

            // Class-level [Authorize(Bearer-Shopper)] beklenir; bazı endpoint'ler
            // action-level [AllowAnonymous] ile bunu override edebilir.
            if (classAuth is null
                || !string.Equals(classAuth.AuthenticationSchemes, ExpectedAuthScheme, StringComparison.Ordinal))
            {
                if (!classIsAllowAnon)
                    offenders.Add($"{t.FullName}: class-level [Authorize(AuthenticationSchemes=\"Bearer-Shopper\")] yok");
            }
        }

        offenders.Should().BeEmpty();
    }

    [Fact]
    public void All_shopper_controllers_have_ApiController_attribute()
    {
        var offenders = DiscoverShopperControllers()
            .Where(t => t.GetCustomAttribute<ApiControllerAttribute>(inherit: true) is null)
            .Select(t => t.FullName)
            .ToList();
        offenders.Should().BeEmpty("[ApiController] ile ModelState validation + ProblemDetails consistency");
    }
}
```

- [ ] **Step 2: Run — fail** (no controllers yet)

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperControllerConventionTests"
```

Expected: 2 fails — assertion "Shopper namespace'inde controller bekleniyor" boş kümeye düşer.

This test stays RED until Task 6 creates the first controller. That's expected and OK — TDD.

- [ ] **Step 3: Don't commit yet** — this test will pass after Task 6. Commit happens with Task 6.

---

## Task 6: `ShopperBroadcastersController.CodeLookup`

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs`
- Create: `OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperBroadcastersCodeLookupTests.cs`

- [ ] **Step 1: Failing test**

`OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperBroadcastersCodeLookupTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Shopper;

public class ShopperBroadcastersCodeLookupTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ShopperBroadcastersCodeLookupTests(ApiFactory factory) => _factory = factory;

    private sealed record LookupResponse(Guid LicenseId, string DisplayName);

    private async Task<(Guid licenseId, string customerName)> SeedLicenseAsync(string shopperCode)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(),
            Email = $"u-{Guid.NewGuid():N}@x",
            Name = "Royal Mezat",
            PasswordHash = "h",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var sku = await db.Skus.FirstOrDefaultAsync()
            ?? new Sku { Code = "TEST-SKU", DisplayName = "Test", DefaultDurationDays = 365, DefaultActivationSlots = 1 };
        if (sku.Code == "TEST-SKU" && !await db.Skus.AnyAsync(s => s.Code == "TEST-SKU"))
            db.Skus.Add(sku);
        var licenseId = Guid.NewGuid();
        db.Licenses.Add(new License
        {
            Id = licenseId,
            CustomerId = customer.Id,
            SkuCode = sku.Code,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddYears(1),
            LicenseKey = $"key-{Guid.NewGuid():N}",
            ShopperCode = shopperCode,
            ShopperCodeUpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return (licenseId, customer.Name);
    }

    [Fact]
    public async Task Lookup_returns_200_with_license_id_and_display_name()
    {
        var (licenseId, name) = await SeedLicenseAsync("royal");
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=royal");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<LookupResponse>();
        body!.LicenseId.Should().Be(licenseId);
        body.DisplayName.Should().Be(name);
    }

    [Fact]
    public async Task Lookup_is_case_insensitive()
    {
        await SeedLicenseAsync("royal");
        var resp = await _factory.CreateClient()
            .GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=ROYAL");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Lookup_returns_404_for_unknown_code()
    {
        var resp = await _factory.CreateClient()
            .GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=nosuch");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Lookup_returns_400_for_empty_code()
    {
        var resp = await _factory.CreateClient()
            .GetAsync("/api/v1/shopper/broadcasters/code-lookup?code=");
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
```

- [ ] **Step 2: Run — fail (404 because controller doesn't exist)**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperBroadcastersCodeLookupTests"
```

- [ ] **Step 3: Create controller**

`OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Controllers.Shopper;

/// <summary>
/// Shopper-facing broadcaster (yayıncı) endpoint'leri. CodeLookup anonim;
/// diğerleri (join/leave) Bearer-Shopper gerektirir (Faz 0b-2'de eklenir).
/// </summary>
[ApiController]
[Authorize(AuthenticationSchemes = "Bearer-Shopper")]
[Route("api/v1/shopper/broadcasters")]
public sealed class ShopperBroadcastersController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public ShopperBroadcastersController(LicenseDbContext db) => _db = db;

    public sealed record CodeLookupResponse(Guid LicenseId, string DisplayName);

    [AllowAnonymous]
    [HttpGet("code-lookup")]
    public async Task<IActionResult> CodeLookup([FromQuery] string? code, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(code))
            return Problem(title: "empty-code", statusCode: 400);

        var normalized = code.Trim().ToLowerInvariant();
        var license = await _db.Licenses
            .Where(l => l.ShopperCode == normalized)
            .Select(l => new { l.Id, CustomerName = l.Customer.Name })
            .FirstOrDefaultAsync(ct);
        if (license is null) return NotFound();

        return Ok(new CodeLookupResponse(license.Id, license.CustomerName));
    }
}
```

- [ ] **Step 4: Test pass + convention test pass**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperBroadcastersCodeLookupTests"
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~ShopperControllerConventionTests"
```

Both expected: pass.

- [ ] **Step 5: Commit (includes ConventionTests from Task 5)**

```bash
git add OrderDeck.LicenseServer/Controllers/Shopper/ShopperBroadcastersController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperControllerConventionTests.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Shopper/ShopperBroadcastersCodeLookupTests.cs
git commit -m "feat(shopper): broadcasters/code-lookup endpoint + convention guard"
```

---

> **Plan continues in this file.** Tasks 7-15 follow the same pattern. To keep the file readable, those tasks are documented with their endpoint signatures and key test scenarios; full implementer code blocks follow established patterns from Tasks 1-6.

## Task 7: `ShopperAuthController.Register`

**Endpoint:** `POST /api/v1/shopper/auth/register` (anonymous)

**Request DTO:**
```csharp
public sealed record RegisterRequest(
    string BroadcasterCode,
    string FullName,
    string Phone,
    string Password,
    string Address,
    string Platform,
    string Username,
    string? Email,
    string? Tc);
```

**Response DTO:**
```csharp
public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    Guid ShopperId,
    BroadcasterSummary[] Broadcasters);

public sealed record BroadcasterSummary(Guid LicenseId, string DisplayName, string Platform, string Username);
```

**Akış:**
1. ModelState + BroadcasterCode lookup (yoksa 404 invalid-code)
2. `PhoneNormalizer.TryNormalize` — fail ise 400 invalid-phone
3. Password ≥ 8 char ve diğer kurallar; aksi halde 400 weak-password
4. `_db.Shoppers.SingleOrDefault(s => s.Phone == phone)`:
   - Var ise: parola verify; fail ise 401 phone-already-used; geçerse mevcut Shopper'a yeni `ShopperBroadcasterLink` ekle
   - Yok ise: yeni Shopper kaydı + ilk link
5. `WpfCustomerProjection` match: `(LicenseId, Platform, Username)` → varsa `link.WpfCustomerId = match.Id`
6. `JwtTokenService.IssueShopperToken` + `ShopperRefreshTokenService.IssueAsync`
7. Tüm linkleri DB'den load + DTO döndür

**Test cases (en az 5):**
- Happy path (yeni shopper + ilk link)
- Existing phone + correct password → mevcut shopper'a link eklenir
- Existing phone + wrong password → 401
- Yanlış broadcaster code → 404
- Invalid phone format → 400
- Username + platform mevcut WpfCustomerProjection ile match → `WpfCustomerId` doludur

**Commit:** `feat(shopper): auth/register endpoint`

---

## Task 8: `ShopperAuthController.Login`

**Endpoint:** `POST /api/v1/shopper/auth/login` (anonymous)

**Request:** `{ phone, password }`. **Response:** Same `AuthResponse` shape as Register.

**Akış:**
1. Normalize phone; fail → 400
2. Find shopper by phone; not found → 401 invalid-credentials
3. Verify password via `PasswordHasher.Verify`; fail → 401 invalid-credentials
4. Soft-deleted (`DeletedAt != null`) → 401 invalid-credentials (don't leak)
5. Issue JWT + refresh token
6. Return AuthResponse with broadcasters

**Test cases:**
- Happy path
- Unknown phone → 401
- Wrong password → 401
- Deleted account → 401

**Commit:** `feat(shopper): auth/login endpoint`

---

## Task 9: `ShopperAuthController.Refresh`

**Endpoint:** `POST /api/v1/shopper/auth/refresh` (anonymous)

**Request:** `{ refreshToken }`. **Response:** `{ accessToken, accessTokenExpiresAt, refreshToken (rotated), refreshTokenExpiresAt }`.

**Akış:**
1. `ShopperRefreshTokenService.RotateAsync(oldRaw, ip)` — null → 401 invalid-refresh-token
2. `JwtTokenService.IssueShopperToken` for the rotated shopper

**Test cases:**
- Happy path → rotation chain verified
- Unknown token → 401
- Reused token → 401 (also revokes the chain in stretch goal)

**Commit:** `feat(shopper): auth/refresh endpoint with rotation`

---

## Task 10: `ShopperAuthController.ForgotPassword`

**Endpoint:** `POST /api/v1/shopper/auth/forgot-password` (anonymous, idempotent 202)

**Request:** `{ phone }`. **Response:** `202 Accepted` her zaman (enumeration koruması).

**Akış:**
1. Normalize phone; invalid → yine 202 (don't leak)
2. Find shopper; not found → 202
3. Her aktif `ShopperBroadcasterLink` (LeftAt = null) için: `ShopperSupportRequest` insert (Kind = "forgot-password")
4. Return 202

**Test cases:**
- Unknown phone → 202, no DB write
- Known phone with 2 broadcasters → 202, 2 `ShopperSupportRequest` rows

**Commit:** `feat(shopper): auth/forgot-password (support request fan-out)`

---

## Task 11: `ShopperAuthController.ChangePassword`

**Endpoint:** `POST /api/v1/shopper/auth/change-password` (Bearer-Shopper)

**Request:** `{ currentPassword, newPassword }`. **Response:** `204`.

**Akış:**
1. Get current shopper from `User.FindFirst("sub")` claim
2. Verify currentPassword
3. New password ≥ 8 char
4. Update Shopper.PasswordHash via PasswordHasher.Hash
5. (Opsiyonel — Faz 0b-1 scope dışı) tüm refresh tokenları revoke et

**Test cases:**
- Happy path → 204, password updated, can login with new
- Wrong current password → 401
- Weak new password → 400

**Commit:** `feat(shopper): auth/change-password endpoint`

---

## Task 12: `ShopperMeController.GetMe`

**Endpoint:** `GET /api/v1/shopper/me` (Bearer-Shopper)

**Response DTO:**
```csharp
public sealed record MeResponse(
    Guid Id,
    string FullName,
    string Phone,
    string Address,
    string? Email,
    string? Tc,
    NotificationPrefs NotificationPrefs,
    BroadcasterSummary[] Broadcasters);

public sealed record NotificationPrefs(bool Broadcast, bool Orders, bool Payments);
```

**Akış:**
1. Get shopperId from claims
2. Load Shopper + active links (LeftAt = null) + License.Customer.Name for display

**Test cases:**
- Happy path
- Unauthorized → 401
- Soft-deleted shopper → 401

**Commit:** `feat(shopper): me GET endpoint`

---

## Task 13: `ShopperMeController.PatchMe`

**Endpoint:** `PATCH /api/v1/shopper/me` (Bearer-Shopper)

**Request:** All fields optional:
```csharp
public sealed record PatchMeRequest(
    string? FullName,
    string? Address,
    string? Email,
    string? Tc,
    NotificationPrefs? NotificationPrefs);
```

**Akış:**
- Non-null property'leri update et
- Telefon değişimi YOK (Faz 0b-1 scope dışı)
- Email validation: simple regex
- TC validation: 11 digit + checksum
- Return updated MeResponse

**Test cases:**
- Happy path partial update
- Invalid email → 400
- Invalid TC checksum → 400
- Notification prefs update only

**Commit:** `feat(shopper): me PATCH endpoint`

---

## Task 14: `ShopperMeController.DeleteMe`

**Endpoint:** `DELETE /api/v1/shopper/me` (Bearer-Shopper)

**Akış:**
1. Soft delete: `Shopper.DeletedAt = now`
2. Tüm `ShopperRefreshToken` revoke
3. Tüm `ShopperBroadcasterLink.LeftAt = now`
4. (Hard delete Hangfire job 30 gün sonra — Faz dışı)
5. Return 204

**Test cases:**
- Happy path → DeletedAt set, login fails afterwards
- Unauthorized → 401

**Commit:** `feat(shopper): me DELETE (soft + 30g grace)`

---

## Task 15: PR aç

- [ ] **Step 1:** `dotnet test` full suite — verify all green
- [ ] **Step 2:** Push + PR with summary of 15 new endpoints + 5 new entities/services

```bash
gh pr create --title "feat(shopper-auth): Faz 0b-1 — auth + me + code lookup endpoints" --body "..."
```

---

## Self-Review

**Spec coverage** (`docs/superpowers/specs/2026-05-20-customer-app-design.md`, "Shopper-facing endpoints" tablosu):

| Spec endpoint | Plan task |
|---------------|-----------|
| `GET /api/v1/shopper/broadcasters/code-lookup` | T6 |
| `POST /api/v1/shopper/auth/register` | T7 |
| `POST /api/v1/shopper/auth/login` | T8 |
| `POST /api/v1/shopper/auth/refresh` | T9 |
| `POST /api/v1/shopper/auth/forgot-password` | T10 |
| `POST /api/v1/shopper/auth/change-password` | T11 |
| `GET /api/v1/shopper/me` | T12 |
| `PATCH /api/v1/shopper/me` | T13 |
| `DELETE /api/v1/shopper/me` | T14 |

Faz 0b-1 scope dışı (Faz 0b-2'de): broadcasters/join, broadcasters/leave.
Faz 0b-3+'da: feed, orders, payments view, dekont upload, devices.

**Placeholder check:** T7-T14 endpoint signature ve test case özeti veriliyor, full implementation code blockları execution sırasında plan'a referans alınarak yazılır. T1-T6 tam code'lu.

**Type consistency:**
- `AuthResponse` Register/Login/Refresh paylaşıyor — Refresh sadece access + refresh, full broadcasters yok (sub-Refresh shape farklı, dokümante ettim T9'da)
- `BroadcasterSummary` Register/Login/Me'de aynı — tek tip
- `IssueShopperToken(shopperId, phone)` signature T3'te tanımlandı, T7-T11'de aynı

---

## Sonraki Plan

`2026-05-20-customer-app-faz-0b-2-broadcasters-management.md` — broadcasters/join, leave, me/broadcasters list.
