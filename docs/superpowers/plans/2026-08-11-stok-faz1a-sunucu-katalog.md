# Faz 1a — Katalog (Sunucu) Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Lisans server'a kategori ağacı, ürün kartı, eksen/varyant modeli, ürün
fotoğrafı (R2) ve "stok elemanı" rolünü ekle — yayıncı paneli bu uçların üstüne
oturabilsin, WPF'e hiç dokunulmasın.

**Architecture:** Mevcut `Controllers/Panel/` deseni birebir kopyalanır:
`[Authorize(AuthenticationSchemes = "Bearer-Customer")]` + `User.GetTenantCustomerId()`
→ `ResolveActiveLicenseAsync` → `LicenseId` ile filtrelenmiş EF sorgusu. Üç yeni
entity (`Category`, `Product`, `ProductVariant`) `LicenseDbContext`'e girer, tek
migration ile SQL Server'a çıkar. Fotoğraf mevcut `IBroadcastMediaStorage`
presigned-URL akışını yeniden kullanır (yeni depolama soyutlaması yazılmaz). Stok
elemanı rolü, panelin **varsayılan-kapalı** ilk yetki kapısıdır: global bir MVC
filtresi `role=stock` taşıyan token'ı `[AllowStockStaff]` işaretlenmemiş her uçta
403'ler.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (prod SQL Server / test InMemory),
xUnit + FluentAssertions, `WebApplicationFactory<Program>` (`ApiFactory`),
Cloudflare R2 (AWS SDK S3, SigV4).

**Spec:** `docs/superpowers/specs/2026-08-07-stok-sistemi-design.md`

---

## Kapsam notu — bu plan neden yalnız sunucu

Spec'teki Faz 1a "panelde CRUD ekranı"nı da içeriyor. Panel **bu repoda değil**:
`C:\Users\burak\source\repos\OrderDeck-Mobile` monorepo'sunda `apps/panel`
(React + Vite + TypeScript, `packages/shared-api` üzerinden konuşuyor). İki repo =
iki ayrı branch, iki ayrı test koşusu, iki ayrı PR.

Bu yüzden Faz 1a ikiye bölündü:

| Parça | Repo | Durum |
|---|---|---|
| **1a-sunucu** — entity, migration, panel uçları, R2, rol kapısı | `LiveDeck` | **bu plan** |
| **1a-panel** — kategori ağacı UI, ürün kartı formu, varyant tablosu, fotoğraf yükleme | `OrderDeck-Mobile` | ayrı plan, bu bittikten sonra |

Panel planı bu plandan önce yazılamaz: uçların gerçek şekli (DTO alan adları,
hata `title` slug'ları, kod türetme davranışı) burada belirleniyor.

## Kapsam dışı (Faz 1a'da YAPILMAYACAK)

- **Stok hareketi tablosu / bakiye** — Faz 1b. Bu planda `StockMovement` yok.
- **Barkod üretimi, etiket PDF'i, okutma** — Faz 1c.
- **Arşivleme işi (Hangfire) ve arşiv uçları** — Faz 1c. `Product.IsArchived`
  alanı modele girer ve liste onu filtreler, ama arşive alan/çıkaran uç yazılmaz.
- **WPF senkronu** — Faz 1b.
- **WhatsApp** — Faz 2.
- **Sunucu tarafında görsel yeniden boyutlandırma.** Yükleme presigned URL ile
  doğrudan R2'ye gittiği için sunucu baytları hiç görmez. Küçültme **panelin**
  işi (canvas → JPEG); sunucu yalnız MIME + boyut sınırını `HeadAsync` ile
  **doğrular**. Bu bilinçli: sunucuya görsel işleme kütüphanesi sokmak (ImageSharp
  gelir eşiğine bağlı ticari lisans ister) bu fazın işi değil.

## Kararlar (spec'i uygularken netleştirilenler)

1. **`Category.Path` GUID tabanlı, küçük harf `N` formatı:** `/a3f1…/8b02…/`.
   Spec `/3/8/21/` diyor ama bu repoda tüm PK'lar `Guid`. Yol **her zaman**
   `id.ToString("N")` (küçük harf) ile üretilir, hiçbir yerde harf duyarsız
   karşılaştırma yapılmaz → PostgreSQL göçünde davranış değişmez.
2. **`Product.CategoryId` nullable.** Kart açılır açılmaz kaydedilebilmeli
   ("kod dolu gelir, hiçbir şey düşünmeden kaydedilir"); kategori zorunlu olsaydı
   bu akış kırılırdı.
3. **`ProductVariant` de `LicenseId` taşır** (spec: "kategori, ürün, varyant ve
   stok hareketi de `LicenseId` alır"). Denormalize ama kiracı filtresini tek
   kural yapıyor.
4. **Eksensiz üründe de tek varyant satırı** oluşur (`Axis1Value` ve
   `Axis2Value` `null`, `VariantCode = Product.Code`). Stok her zaman aynı
   yapıdan okunur.
5. **Ürün başına tek fotoğraf.** Spec "sayı sınırlı olacak" diyor; 1a'da sınır 1.
6. **Kod türetme ilk boşluksuz parçadan yapılır** — spec tablosundaki
   `103 Nude → 103` satırı ancak böyle tutar.

---

## Dosya yapısı

**Oluşturulacak:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Domain/Category.cs` | Kategori entity'si |
| `OrderDeck.LicenseServer/Domain/Product.cs` | Ürün entity'si + `AxisRole` enum |
| `OrderDeck.LicenseServer/Domain/ProductVariant.cs` | Varyant entity'si |
| `OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs` | Görünen değer → ASCII kod parçası (saf fonksiyon) |
| `OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs` | `A1 → A2 → … → A999 → B1` (saf fonksiyon) |
| `OrderDeck.LicenseServer/Services/Catalog/CategoryPathService.cs` | Yol üretimi, döngü koruması, alt ağaç taşıma (saf fonksiyonlar) |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelCategoriesController.cs` | Kategori CRUD + taşıma |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs` | Ürün kartı CRUD + ortak DTO'lar |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs` | Varyant ekle/güncelle/sil |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelProductPhotoController.cs` | Fotoğraf uçları (R2 presigned) |
| `OrderDeck.LicenseServer/Services/Auth/OperatorRoles.cs` | Rol sabitleri + `[AllowStockStaff]` |
| `OrderDeck.LicenseServer/Services/Auth/StockStaffScopeFilter.cs` | Varsayılan-kapalı yetki kapısı |
| `OrderDeck.LicenseServer/Data/Migrations/*_AddCatalog.cs` | `dotnet ef` üretir |
| `OrderDeck.LicenseServer.Tests/Services/Catalog/AxisCodeDeriverTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Services/Catalog/CatalogCodeSequenceTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Services/Catalog/CategoryPathServiceTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCategoriesControllerTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductVariantsControllerTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductPhotoControllerTests.cs` | |
| `OrderDeck.LicenseServer.Tests/Auth/StockStaffScopeTests.cs` | |

**Değiştirilecek:**

| Dosya | Değişiklik |
|---|---|
| `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` | 3 `DbSet` + `OnModelCreating` yapılandırması |
| `OrderDeck.LicenseServer/Services/Auth/JwtTokenService.cs` | Operator token'a `role` claim'i |
| `OrderDeck.LicenseServer/Services/Auth/TenantClaims.cs` | `GetOperatorRole()` |
| `OrderDeck.LicenseServer/Controllers/Auth/AuthController.cs:163` | `IssueOperatorToken`'a rol geçir |
| `OrderDeck.LicenseServer/Controllers/Panel/PanelOperatorsController.cs` | `InviteRequest`'e `Role`, doğrulama |
| `OrderDeck.LicenseServer/Program.cs:492` | `AddControllers(opt => opt.Filters.Add<StockStaffScopeFilter>())` |

---

## Görev sırası

1. `AxisCodeDeriver` — saf fonksiyon, bağımlılık yok
2. Entity'ler + `LicenseDbContext` + migration
3. `CatalogCodeSequence` — saf fonksiyon
4. `CategoryPathService` — saf fonksiyonlar
5. `PanelCategoriesController`
6. `PanelProductsController` — ürün kartı CRUD
7. `PanelProductVariantsController` — varyant uçları
8. `PanelProductPhotoController` — fotoğraf uçları
9. Stok elemanı rolü + varsayılan-kapalı kapı

Her görev kendi başına derlenir, testleri geçer ve commit edilir.

---

### Task 1: `AxisCodeDeriver` — görünen değer → ASCII kod parçası

Code128 yalnız ASCII kodlar; `ç ğ ı İ ö ş ü` barkoda giremez. Görünen ad serbest
kalır, kod parçası ondan türetilir.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Catalog/AxisCodeDeriverTests.cs`

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Catalog/AxisCodeDeriverTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class AxisCodeDeriverTests
{
    // Spec tablosu — "Kod parçası ASCII'ye türetilir (Code128 tuzağı)".
    [Theory]
    [InlineData("Siyah", "SIYA")]
    [InlineData("Yeşil", "YESI")]
    [InlineData("Bej", "BEJ")]
    [InlineData("M", "M")]
    [InlineData("38", "38")]
    [InlineData("103 Nude", "103")]
    public void Derives_spec_table_values(string display, string expected)
        => AxisCodeDeriver.Derive(display).Should().Be(expected);

    [Theory]
    [InlineData("çğıİöşü", "CGII")]
    [InlineData("ÇĞİÖŞÜ", "CGIO")]
    public void Strips_all_turkish_letters_to_ascii(string display, string expected)
        => AxisCodeDeriver.Derive(display).Should().Be(expected);

    [Fact]
    public void Result_is_always_ascii_uppercase_alphanumeric()
    {
        var code = AxisCodeDeriver.Derive("açık mavi-2");
        code.Should().MatchRegex("^[A-Z0-9]+$");
    }

    [Fact]
    public void Falls_through_to_next_token_when_first_token_has_no_ascii()
        => AxisCodeDeriver.Derive("— Mavi").Should().Be("MAVI");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("—")]
    public void Returns_empty_when_nothing_derivable(string display)
        => AxisCodeDeriver.Derive(display).Should().BeEmpty();

    [Fact]
    public void Truncates_to_four_characters()
        => AxisCodeDeriver.Derive("Antrasit").Should().Be("ANTR");
}
```

- [ ] **Step 2: Kırmızıyı doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~AxisCodeDeriverTests"
```
Beklenen: derleme hatası — `AxisCodeDeriver` yok.

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs`:

```csharp
using System.Text;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Eksen değerinin görünen adından barkoda girebilecek kod parçasını türetir.
///
/// Neden gerekli: barkod sembolü Code128 ve Code128 <b>ASCII</b> kodlar.
/// "Yeşil" doğrudan barkoda giremez. Görünen ad serbest kalır, kod parçası
/// buradan türetilir ve kullanıcı isterse elle düzeltir.
///
/// Kural: ilk boşluksuz parçadan ASCII harf/rakam süz, büyük harfe çevir,
/// 4 karaktere kısalt. İlk parçadan hiçbir şey çıkmazsa sonrakine geçilir
/// ("— Mavi" → MAVI).
///
/// Çıktı her zaman büyük harf olduğu için sorguda <c>ToUpper()</c> kullanmaya
/// gerek kalmaz — PostgreSQL göçünde eşleştirme davranışı değişmez.
/// </summary>
public static class AxisCodeDeriver
{
    public const int MaxLength = 4;

    public static string Derive(string? displayValue)
    {
        if (string.IsNullOrWhiteSpace(displayValue)) return string.Empty;

        foreach (var token in displayValue.Split(
                     (char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var code = Squeeze(token);
            if (code.Length > 0) return code;
        }

        return string.Empty;
    }

    private static string Squeeze(string token)
    {
        var sb = new StringBuilder(MaxLength);

        foreach (var ch in token.ToUpperInvariant())
        {
            var mapped = Map(ch);
            if (mapped is null) continue;

            sb.Append(mapped.Value);
            if (sb.Length == MaxLength) break;
        }

        return sb.ToString();
    }

    private static char? Map(char c) => c switch
    {
        'Ç' => 'C',
        'Ğ' => 'G',
        'İ' => 'I',      // U+0130: ToUpperInvariant bunu korur
        '\u0131' => 'I', // ı: ToUpperInvariant U+0131'i küçük bırakır
        'Ö' => 'O',
        'Ş' => 'S',
        'Ü' => 'U',
        >= 'A' and <= 'Z' => c,
        >= '0' and <= '9' => c,
        _ => null,
    };
}
```

> Not (uygulamada ölçüldü, 2026-08-12): `char.ToUpperInvariant('ı')` .NET 10'da
> `'I'` **vermiyor**, U+0131 olarak kalıyor — bu yüzden noktasız ı'nın kendi
> `Map` girişi var. `'i'` ise sorunsuz `'I'` oluyor. `'İ'` (U+0130) invariant'ta
> kendisi kalır, o da listede.

- [ ] **Step 4: Yeşili doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~AxisCodeDeriverTests"
```
Beklenen: 14 test geçer.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs \
        OrderDeck.LicenseServer.Tests/Services/Catalog/AxisCodeDeriverTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): eksen değerinden ASCII kod parçası türet

Code128 yalnız ASCII kodladığı için Türkçe karakterli eksen değerleri
barkoda doğrudan giremiyor. Görünen ad serbest kalıyor, kod parçası
türetiliyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Task 2: Entity'ler + `LicenseDbContext` + migration

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/Category.cs`
- Create: `OrderDeck.LicenseServer/Domain/Product.cs`
- Create: `OrderDeck.LicenseServer/Domain/ProductVariant.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer/Data/Migrations/*_AddCatalog.cs` (`dotnet ef` üretir)
- Test: `OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`

Bu görevde HTTP ucu yok; test "model kurulur ve alt ağaç filtresi çalışır" dumanı.
Gerçek davranış testleri Task 5'ten itibaren geliyor.

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

public class CatalogModelTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public CatalogModelTests(ApiFactory f) => _factory = f;

    private static License NewLicense() => new()
    {
        Id = Guid.NewGuid(),
        CustomerId = Guid.NewGuid(),
        LicenseKey = "LDK-CAT-" + Guid.NewGuid().ToString("N"),
        SkuCode = "STD",
        ActivationSlots = 1,
        IssuedAt = DateTimeOffset.UtcNow,
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
    };

    private static Category NewCategory(Guid licenseId, string name, string parentPath)
    {
        var id = Guid.NewGuid();
        return new Category
        {
            Id = id,
            LicenseId = licenseId,
            Name = name,
            Path = parentPath + id.ToString("N") + "/",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
    }

    [Fact]
    public async Task Category_product_and_variant_roundtrip()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = NewLicense();
        db.Licenses.Add(license);

        var category = NewCategory(license.Id, "Tişört", "/");
        db.Categories.Add(category);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            CategoryId = category.Id,
            Code = "A1",
            Name = "Basic Tişört",
            DefaultPrice = 499.90m,
            Cost = 210m,
            Axis1Name = "Renk",
            Axis1Role = AxisRole.Seller,
            Axis2Name = "Beden",
            Axis2Role = AxisRole.Viewer,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);

        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "Siyah", Axis1Code = "SIYA",
            Axis2Value = "M", Axis2Code = "M",
            VariantCode = "A1-SIYA-M",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });

        await db.SaveChangesAsync();

        var loaded = await db.Products
            .Include(p => p.Variants)
            .Include(p => p.Category)
            .FirstAsync(p => p.Id == product.Id);

        loaded.Category!.Name.Should().Be("Tişört");
        loaded.Axis1Role.Should().Be(AxisRole.Seller);
        loaded.Variants.Should().ContainSingle()
            .Which.VariantCode.Should().Be("A1-SIYA-M");
    }

    [Fact]
    public async Task Subtree_filter_is_a_single_StartsWith_on_path()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = NewLicense();
        db.Licenses.Add(license);

        var erkek = NewCategory(license.Id, "Erkek", "/");
        var ustGiyim = NewCategory(license.Id, "Üst Giyim", erkek.Path);
        var kadin = NewCategory(license.Id, "Kadın", "/");
        db.Categories.AddRange(erkek, ustGiyim, kadin);
        await db.SaveChangesAsync();

        var subtree = await db.Categories
            .Where(c => c.LicenseId == license.Id && c.Path.StartsWith(erkek.Path))
            .Select(c => c.Name)
            .ToListAsync();

        subtree.Should().BeEquivalentTo(new[] { "Erkek", "Üst Giyim" });
    }
}
```

- [ ] **Step 2: Kırmızıyı doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CatalogModelTests"`

Beklenen: derleme hatası — `Category`, `Product`, `ProductVariant` yok.

- [ ] **Step 3: Entity'leri yaz**

`OrderDeck.LicenseServer/Domain/Category.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Sınırsız derinlikte kategori ağacı (örn. Erkek &gt; Üst Giyim &gt; Tişört).
/// Ürün ağacın herhangi bir seviyesine bağlanabilir; yaprak olma zorunluluğu yok.
/// </summary>
public sealed class Category
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid? ParentCategoryId { get; set; }
    public Category? ParentCategory { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>
    /// Id tabanlı yol: kökte <c>/{id:N}/</c>, altta <c>/{parentId:N}/{id:N}/</c>.
    ///
    /// Neden id tabanlı ve neden <c>"N"</c> (küçük harf, tiresiz): alt ağaç
    /// filtresi tek <c>StartsWith</c> oluyor, recursive CTE gerekmiyor; ve yol
    /// her zaman aynı biçimde ÜRETİLDİĞİ için hiçbir yerde harf duyarsız
    /// karşılaştırma gerekmiyor → PostgreSQL göçünde davranış değişmez.
    /// İsim tabanlı yol olsaydı göçte arama sessizce değişirdi.
    /// </summary>
    public string Path { get; set; } = "/";

    public int SortOrder { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

`OrderDeck.LicenseServer/Domain/Product.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Eksenin rolü: barkod okutunca SABİTLENEN eksen mi, yoksa yorumdan gelmesi
/// beklenen AÇIK eksen mi.
///
/// Sabit "Renk + Beden" adlandırması çantada/kozmetikte kırılıyordu; asıl ayrım
/// eksenin adı değil rolü. Rujda tek eksen var ve rolü <see cref="Viewer"/>.
/// </summary>
public enum AxisRole
{
    /// <summary>Satıcı ekseni — okutulunca sabitlenir (renk, koku).</summary>
    Seller = 1,

    /// <summary>İzleyici ekseni — açık kalır, yorumdan gelir (beden, numara, hacim, ton).</summary>
    Viewer = 2,
}

/// <summary>
/// Katalog kartı (model). Fotoğraf ürün seviyesinde tutulur, varyantta değil —
/// aksi halde görsel sayısı varyant sayısı kadar katlanır.
/// </summary>
public sealed class Product
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    /// <summary>Nullable: kart açılır açılmaz, kategori seçmeden kaydedilebilmeli.</summary>
    public Guid? CategoryId { get; set; }
    public Category? Category { get; set; }

    /// <summary>Lisans başına benzersiz. Otomatik üretilir (A1, A2…), elle değiştirilebilir.</summary>
    public string Code { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>Yayında değiştirilebilen VARSAYILAN fiyat; siparişe o anki fiyat damgalanır.</summary>
    public decimal DefaultPrice { get; set; }

    /// <summary>Maliyet — ürün bazlı kâr için. Kullanıcı 1a'da kartta istedi.</summary>
    public decimal? Cost { get; set; }

    public string? Axis1Name { get; set; }
    public AxisRole? Axis1Role { get; set; }
    public string? Axis2Name { get; set; }
    public AxisRole? Axis2Role { get; set; }

    // Fotoğraf — BroadcastPost deseniyle birebir (R2, presigned URL).
    public string? PhotoObjectKey { get; set; }
    public string? PhotoContentType { get; set; }
    public long? PhotoSizeBytes { get; set; }
    public int? PhotoWidth { get; set; }
    public int? PhotoHeight { get; set; }

    /// <summary>Faz 1c'de Hangfire işi dolduracak; 1a'da yalnız liste filtresi okur.</summary>
    public bool IsArchived { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ProductVariant> Variants { get; set; } = new();
}
```

`OrderDeck.LicenseServer/Domain/ProductVariant.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Ürünün tek bir eksen kombinasyonu. Eksensiz üründe de TEK bir satır oluşur
/// (iki değer de null, <see cref="VariantCode"/> = ürün kodu) — böylece stok
/// her zaman aynı yapıdan okunur, özel durum kodu yazılmaz.
/// </summary>
public sealed class ProductVariant
{
    public Guid Id { get; set; }

    /// <summary>Product üzerinden türetilebilir; kiracı filtresini tek kural
    /// tutmak için denormalize edildi (spec: varyant da LicenseId alır).</summary>
    public Guid LicenseId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>Görünen değer — serbest, Türkçe karakter içerebilir ("Yeşil").</summary>
    public string? Axis1Value { get; set; }

    /// <summary>Barkoda giren ASCII parça ("YESI"). Elle düzeltilebilir.</summary>
    public string? Axis1Code { get; set; }

    public string? Axis2Value { get; set; }
    public string? Axis2Code { get; set; }

    /// <summary>Ürün kodu + eksen kod parçaları, "-" ile birleşik (A12-SIYA-M).</summary>
    public string VariantCode { get; set; } = string.Empty;

    /// <summary>Faz 1c'de doldurulur (Code128). 1a'da her zaman null.</summary>
    public string? Barcode { get; set; }

    public bool IsActive { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
```

- [ ] **Step 4: `LicenseDbContext`'e kaydet**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — diğer `DbSet`'lerin yanına:

```csharp
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<Product> Products => Set<Product>();
    public DbSet<ProductVariant> ProductVariants => Set<ProductVariant>();
```

`OnModelCreating` içine, mevcut `mb.Entity<...>` bloklarının yanına:

```csharp
        mb.Entity<Category>(b =>
        {
            b.HasKey(c => c.Id);
            b.Property(c => c.Name).HasMaxLength(120).IsRequired();
            b.Property(c => c.Path).HasMaxLength(512).IsRequired();
            b.HasOne(c => c.License).WithMany()
                .HasForeignKey(c => c.LicenseId).OnDelete(DeleteBehavior.Cascade);
            // Restrict: alt kategorisi olan kategori silinemesin, controller 409
            // dönsün. Cascade olsaydı tek DELETE koca ağacı sessizce uçururdu.
            b.HasOne(c => c.ParentCategory).WithMany()
                .HasForeignKey(c => c.ParentCategoryId).OnDelete(DeleteBehavior.Restrict);
            b.HasIndex(c => new { c.LicenseId, c.Path });
            b.HasIndex(c => new { c.LicenseId, c.ParentCategoryId, c.SortOrder });
        });

        mb.Entity<Product>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Code).HasMaxLength(32).IsRequired();
            b.Property(p => p.Name).HasMaxLength(200).IsRequired();
            b.Property(p => p.DefaultPrice).HasPrecision(18, 2);
            b.Property(p => p.Cost).HasPrecision(18, 2);
            b.Property(p => p.Axis1Name).HasMaxLength(40);
            b.Property(p => p.Axis2Name).HasMaxLength(40);
            b.Property(p => p.Axis1Role).HasConversion<int>();
            b.Property(p => p.Axis2Role).HasConversion<int>();
            b.Property(p => p.PhotoObjectKey).HasMaxLength(512);
            b.Property(p => p.PhotoContentType).HasMaxLength(100);
            b.HasOne(p => p.License).WithMany()
                .HasForeignKey(p => p.LicenseId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne(p => p.Category).WithMany()
                .HasForeignKey(p => p.CategoryId).OnDelete(DeleteBehavior.Restrict);
            // Ürün kodu LİSANS BAŞINA benzersiz — her yayıncının kendi A1'i olur.
            b.HasIndex(p => new { p.LicenseId, p.Code }).IsUnique();
            b.HasIndex(p => new { p.LicenseId, p.IsArchived, p.UpdatedAt });
        });

        mb.Entity<ProductVariant>(b =>
        {
            b.HasKey(v => v.Id);
            b.Property(v => v.Axis1Value).HasMaxLength(60);
            b.Property(v => v.Axis2Value).HasMaxLength(60);
            b.Property(v => v.Axis1Code).HasMaxLength(8);
            b.Property(v => v.Axis2Code).HasMaxLength(8);
            b.Property(v => v.VariantCode).HasMaxLength(64).IsRequired();
            b.Property(v => v.Barcode).HasMaxLength(64);
            b.HasOne(v => v.Product).WithMany(p => p.Variants)
                .HasForeignKey(v => v.ProductId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(v => new { v.ProductId, v.VariantCode }).IsUnique();
            b.HasIndex(v => new { v.LicenseId, v.VariantCode });
        });
```

- [ ] **Step 5: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CatalogModelTests"`

Beklenen: 2 test geçer.

- [ ] **Step 6: Migration üret**

Run:
```
dotnet ef migrations add AddCatalog --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer --output-dir Data/Migrations
```

Üretilen `Data/Migrations/*_AddCatalog.cs` dosyasını **oku ve doğrula**:
- `Categories`, `Products`, `ProductVariants` tabloları oluşuyor
- `IX_Products_LicenseId_Code` **unique**
- `IX_ProductVariants_ProductId_VariantCode` **unique**
- `migrationBuilder.Sql(...)` **hiç yok** — repo kuralı: ham SQL yazılmaz
  (göçü ucuz tutan iki kuraldan biri)

- [ ] **Step 7: Tüm sunucu paketini koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`

Beklenen: mevcut testlerin hepsi + 2 yeni test yeşil, 0 hata.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Category.cs OrderDeck.LicenseServer/Domain/Product.cs OrderDeck.LicenseServer/Domain/ProductVariant.cs OrderDeck.LicenseServer/Data/LicenseDbContext.cs OrderDeck.LicenseServer/Data/Migrations OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs
git commit -m "feat(katalog): kategori, urun ve varyant entity'leri"
```

---

### Task 3: `CatalogCodeSequence` — otomatik ürün kodu

Yayıncı kod uydurmakta zorlanıyor: kart açılınca kod alanı dolu gelmeli (`A1`,
`A2`…), üzerine yazılabilmeli, sayaç asla geri gitmemeli.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Catalog/CatalogCodeSequenceTests.cs`

Saf fonksiyon olarak yazılıyor çünkü **InMemory sağlayıcı benzersiz indeksleri
zorlamıyor** — yarış davranışını entegrasyon testiyle kanıtlamak mümkün değil.
Mantık burada test edilir, controller (Task 6) ayrıca açık `AnyAsync` çakışma
kontrolü yapar, prod'da benzersiz indeks son savunma olur.

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Catalog/CatalogCodeSequenceTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class CatalogCodeSequenceTests
{
    [Fact]
    public void First_code_is_A1()
        => CatalogCodeSequence.Next(Array.Empty<string>()).Should().Be("A1");

    [Fact]
    public void Increments_the_number()
        => CatalogCodeSequence.Next(new[] { "A1", "A2", "A3" }).Should().Be("A4");

    [Fact]
    public void Rolls_over_to_next_prefix_after_999()
        => CatalogCodeSequence.Next(new[] { "A999" }).Should().Be("B1");

    [Fact]
    public void Rolls_over_from_Z999_to_AA1()
        => CatalogCodeSequence.Next(new[] { "Z999" }).Should().Be("AA1");

    [Fact]
    public void Rolls_over_from_AZ999_to_BA1()
        => CatalogCodeSequence.Next(new[] { "AZ999" }).Should().Be("BA1");

    // "Sayaç geri gitmez": en yüksek kod neyse ondan devam eder, aradaki
    // boşluklar doldurulmaz. Arşivlenen ürünün kodu tekrar kullanılırsa eski
    // siparişler yanlış ürünü gösterir.
    [Fact]
    public void Never_reuses_a_gap()
        => CatalogCodeSequence.Next(new[] { "A1", "A7" }).Should().Be("A8");

    // Önek "ayarlanabilir": yayıncı elle B1 yazdığında sayaç oradan devam eder.
    [Fact]
    public void Longer_prefix_wins_over_lexicographic_order()
        => CatalogCodeSequence.Next(new[] { "Z5", "AA2" }).Should().Be("AA3");

    [Fact]
    public void Ignores_hand_written_codes_that_do_not_match_the_pattern()
        => CatalogCodeSequence.Next(new[] { "KIRMIZI-ELBISE", "A4" }).Should().Be("A5");

    [Fact]
    public void Falls_back_to_A1_when_no_code_matches_the_pattern()
        => CatalogCodeSequence.Next(new[] { "özel-kod" }).Should().Be("A1");

    [Fact]
    public void Is_case_insensitive_on_input_but_uppercase_on_output()
        => CatalogCodeSequence.Next(new[] { "a12" }).Should().Be("A13");
}
```

- [ ] **Step 2: Kırmızıyı doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CatalogCodeSequenceTests"`

Beklenen: derleme hatası — `CatalogCodeSequence` yok.

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs`:

```csharp
using System.Text;
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Otomatik ürün kodu üretir: A1 → A2 → … → A999 → B1 → … → Z999 → AA1.
///
/// Mevcut kodların EN YÜKSEĞİNDEN devam eder; boşlukları doldurmaz. Gerekçe:
/// arşivlenen ürünün kodu tekrar kullanılırsa eski siparişler yanlış ürünü
/// gösterir. Ürün kartı silinemediği için "en yüksek" taraması güvenli.
///
/// Önek ayrıca saklanmıyor: yayıncı elle bir kod yazdığında (örn. "B1") sayaç
/// kendiliğinden oradan devam eder. "Önek ayarlanabilir" gereksinimi böyle
/// karşılanıyor — yeni tablo gerekmiyor.
/// </summary>
public static class CatalogCodeSequence
{
    public const int MaxNumber = 999;

    private static readonly Regex Pattern =
        new("^([A-Z]+)([0-9]{1,3})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static string Next(IEnumerable<string> existingCodes)
    {
        string? bestPrefix = null;
        var bestNumber = 0;

        foreach (var raw in existingCodes)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var match = Pattern.Match(raw.Trim().ToUpperInvariant());
            if (!match.Success) continue;

            var prefix = match.Groups[1].Value;
            var number = int.Parse(match.Groups[2].Value);

            if (bestPrefix is null || Compare(prefix, number, bestPrefix, bestNumber) > 0)
            {
                bestPrefix = prefix;
                bestNumber = number;
            }
        }

        if (bestPrefix is null) return "A1";

        return bestNumber < MaxNumber
            ? bestPrefix + (bestNumber + 1)
            : NextPrefix(bestPrefix) + "1";
    }

    /// <summary>Önce önek uzunluğu, sonra önek, sonra sayı. "AA2" &gt; "Z5".</summary>
    private static int Compare(string prefixA, int numberA, string prefixB, int numberB)
    {
        if (prefixA.Length != prefixB.Length) return prefixA.Length - prefixB.Length;

        var byPrefix = string.CompareOrdinal(prefixA, prefixB);
        return byPrefix != 0 ? byPrefix : numberA - numberB;
    }

    /// <summary>A→B, Z→AA, AZ→BA — Excel sütun mantığı.</summary>
    private static string NextPrefix(string prefix)
    {
        var chars = prefix.ToCharArray();

        for (var i = chars.Length - 1; i >= 0; i--)
        {
            if (chars[i] != 'Z')
            {
                chars[i]++;
                return new string(chars);
            }

            chars[i] = 'A';
        }

        return "A" + new string(chars);
    }
}
```

- [ ] **Step 4: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CatalogCodeSequenceTests"`

Beklenen: 10 test geçer.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs OrderDeck.LicenseServer.Tests/Services/Catalog/CatalogCodeSequenceTests.cs
git commit -m "feat(katalog): otomatik urun kodu dizisi (A1 -> A999 -> B1)"
```

---

### Task 4: `CategoryPathService` — yol üretimi, döngü koruması, alt ağaç taşıma

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Catalog/CategoryPathService.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Catalog/CategoryPathServiceTests.cs`

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Services/Catalog/CategoryPathServiceTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class CategoryPathServiceTests
{
    private static readonly Guid Erkek = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid UstGiyim = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid Tisort = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid Kadin = Guid.Parse("44444444-4444-4444-4444-444444444444");

    private static string P(params Guid[] ids)
        => "/" + string.Concat(ids.Select(i => i.ToString("N") + "/"));

    [Fact]
    public void Root_path_is_slash_id_slash()
        => CategoryPathService.BuildPath(null, Erkek).Should().Be(P(Erkek));

    [Fact]
    public void Child_path_appends_to_parent_path()
        => CategoryPathService.BuildPath(P(Erkek), UstGiyim).Should().Be(P(Erkek, UstGiyim));

    [Fact]
    public void Path_is_lowercase_hex_without_dashes()
        => CategoryPathService.BuildPath(null, Erkek)
            .Should().Be("/11111111111111111111111111111111/");

    // Kendi alt ağacına taşıma = döngü. "Erkek"i "Tişört"ün altına taşıyamazsın.
    [Fact]
    public void Moving_into_own_subtree_is_a_cycle()
        => CategoryPathService.WouldCreateCycle(
                movedPath: P(Erkek),
                newParentPath: P(Erkek, UstGiyim, Tisort))
            .Should().BeTrue();

    [Fact]
    public void Moving_onto_itself_is_a_cycle()
        => CategoryPathService.WouldCreateCycle(P(Erkek), P(Erkek)).Should().BeTrue();

    [Fact]
    public void Moving_to_a_sibling_branch_is_not_a_cycle()
        => CategoryPathService.WouldCreateCycle(P(Erkek), P(Kadin)).Should().BeFalse();

    [Fact]
    public void Moving_to_root_is_not_a_cycle()
        => CategoryPathService.WouldCreateCycle(P(Erkek), null).Should().BeFalse();

    // Alt ağaç taşınınca torunların yolu da yeniden yazılmalı.
    [Fact]
    public void Rebase_rewrites_the_prefix_and_keeps_the_tail()
    {
        var oldPath = P(Erkek, UstGiyim);
        var newPath = P(Kadin, UstGiyim);
        var grandChild = P(Erkek, UstGiyim, Tisort);

        CategoryPathService.Rebase(grandChild, oldPath, newPath)
            .Should().Be(P(Kadin, UstGiyim, Tisort));
    }

    [Fact]
    public void Rebase_of_the_moved_node_itself_returns_the_new_path()
    {
        var oldPath = P(Erkek, UstGiyim);
        var newPath = P(Kadin, UstGiyim);

        CategoryPathService.Rebase(oldPath, oldPath, newPath).Should().Be(newPath);
    }
}
```

- [ ] **Step 2: Kırmızıyı doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CategoryPathServiceTests"`

Beklenen: derleme hatası — `CategoryPathService` yok.

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Services/Catalog/CategoryPathService.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Kategori ağacının id tabanlı yol hesapları. Hepsi saf fonksiyon — DB'ye
/// dokunmaz, controller sonucu uygular.
///
/// Yol biçimi: <c>/{id:N}/{childId:N}/</c>. Küçük harf, tiresiz ve HER ZAMAN
/// buradan üretiliyor → hiçbir karşılaştırma harf duyarsız olmak zorunda değil,
/// PostgreSQL göçünde davranış değişmez.
/// </summary>
public static class CategoryPathService
{
    public const string RootPath = "/";

    public static string BuildPath(string? parentPath, Guid id)
        => (parentPath ?? RootPath) + id.ToString("N") + "/";

    /// <summary>
    /// Bir kategori kendi alt ağacına taşınamaz. Yeni ebeveynin yolu, taşınan
    /// kategorinin yoluyla başlıyorsa bu bir döngüdür (kategorinin kendisi de
    /// dahil — kendi altına taşımak da yasak).
    /// </summary>
    public static bool WouldCreateCycle(string movedPath, string? newParentPath)
        => newParentPath is not null
           && newParentPath.StartsWith(movedPath, StringComparison.Ordinal);

    /// <summary>
    /// Alt ağaçtaki bir düğümün yolunu yeni ebeveyne göre yeniden yazar.
    /// <paramref name="path"/> mutlaka <paramref name="oldPrefix"/> ile
    /// başlamalıdır (çağıran zaten öyle filtreliyor).
    /// </summary>
    public static string Rebase(string path, string oldPrefix, string newPrefix)
        => newPrefix + path[oldPrefix.Length..];
}
```

- [ ] **Step 4: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CategoryPathServiceTests"`

Beklenen: 9 test geçer.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Catalog/CategoryPathService.cs OrderDeck.LicenseServer.Tests/Services/Catalog/CategoryPathServiceTests.cs
git commit -m "feat(katalog): kategori yolu, dongu korumasi ve alt agac tasima"
```

---

### Task 5: `PanelCategoriesController`

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelCategoriesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCategoriesControllerTests.cs`

Uçlar:

| Metod | Yol | İş |
|---|---|---|
| `GET` | `/api/panel/categories` | Lisansın tüm ağacı, `Path`'e göre sıralı |
| `POST` | `/api/panel/categories` | Yeni kategori (kök ya da alt) |
| `PUT` | `/api/panel/categories/{id}` | Ad / sıra / aktiflik |
| `PUT` | `/api/panel/categories/{id}/parent` | Taşı (döngü koruması + alt ağaç yeniden yazımı) |
| `DELETE` | `/api/panel/categories/{id}` | Alt kategorisi ya da ürünü varsa 409 |

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCategoriesControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelCategoriesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelCategoriesControllerTests(ApiFactory f) => _factory = f;

    private sealed record CategoryDto(
        Guid Id, Guid? ParentCategoryId, string Name, string Path,
        int Depth, int SortOrder, bool IsActive);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-CATC-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<CategoryDto> CreateAsync(
        HttpClient client, string name, Guid? parentId = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name, parentCategoryId = parentId, sortOrder = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    [Fact]
    public async Task Creates_a_three_level_tree_with_correct_paths_and_depths()
    {
        var (client, _) = await SeedAsync();

        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);
        var tisort = await CreateAsync(client, "Tişört", ust.Id);

        erkek.Path.Should().Be($"/{erkek.Id:N}/");
        erkek.Depth.Should().Be(0);
        ust.Path.Should().Be($"/{erkek.Id:N}/{ust.Id:N}/");
        ust.Depth.Should().Be(1);
        tisort.Path.Should().Be($"/{erkek.Id:N}/{ust.Id:N}/{tisort.Id:N}/");
        tisort.Depth.Should().Be(2);
    }

    [Fact]
    public async Task List_returns_the_tree_ordered_by_path()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        await CreateAsync(client, "Üst Giyim", erkek.Id);

        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");

        rows!.Select(r => r.Name).Should().ContainInOrder("Erkek", "Üst Giyim");
    }

    [Fact]
    public async Task List_is_scoped_to_the_callers_license()
    {
        var (clientA, _) = await SeedAsync();
        await CreateAsync(clientA, "A tarafı");

        var (clientB, _) = await SeedAsync();
        var rows = await clientB.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");

        rows!.Should().NotContain(r => r.Name == "A tarafı");
    }

    [Fact]
    public async Task Create_400_on_unknown_parent()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name = "Yetim", parentCategoryId = Guid.NewGuid(), sortOrder = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("parent-not-found");
    }

    [Fact]
    public async Task Create_400_on_empty_name()
    {
        var (client, _) = await SeedAsync();

        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name = "   ", parentCategoryId = (Guid?)null, sortOrder = 0 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-name");
    }

    [Fact]
    public async Task Rename_updates_the_name_but_not_the_path()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{erkek.Id}",
            new { name = "Bay", sortOrder = 3, isActive = false });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
        dto.Name.Should().Be("Bay");
        dto.SortOrder.Should().Be(3);
        dto.IsActive.Should().BeFalse();
        dto.Path.Should().Be(erkek.Path);
    }

    [Fact]
    public async Task Move_rewrites_the_whole_subtree()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);
        var tisort = await CreateAsync(client, "Tişört", ust.Id);
        var kadin = await CreateAsync(client, "Kadın");

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{ust.Id}/parent",
            new { parentCategoryId = kadin.Id });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");
        var movedUst = rows!.Single(r => r.Id == ust.Id);
        var movedTisort = rows!.Single(r => r.Id == tisort.Id);

        movedUst.Path.Should().Be($"/{kadin.Id:N}/{ust.Id:N}/");
        movedUst.ParentCategoryId.Should().Be(kadin.Id);
        movedTisort.Path.Should().Be($"/{kadin.Id:N}/{ust.Id:N}/{tisort.Id:N}/");
        movedTisort.Depth.Should().Be(2);
    }

    [Fact]
    public async Task Move_to_root_is_allowed()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{ust.Id}/parent",
            new { parentCategoryId = (Guid?)null });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
        dto.ParentCategoryId.Should().BeNull();
        dto.Path.Should().Be($"/{ust.Id:N}/");
        dto.Depth.Should().Be(0);
    }

    [Fact]
    public async Task Move_into_own_subtree_is_rejected()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        var ust = await CreateAsync(client, "Üst Giyim", erkek.Id);

        var resp = await client.PutAsJsonAsync($"/api/panel/categories/{erkek.Id}/parent",
            new { parentCategoryId = ust.Id });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("category-cycle");
    }

    [Fact]
    public async Task Delete_409_when_it_has_children()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateAsync(client, "Erkek");
        await CreateAsync(client, "Üst Giyim", erkek.Id);

        var resp = await client.DeleteAsync($"/api/panel/categories/{erkek.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("category-has-children");
    }

    [Fact]
    public async Task Delete_409_when_it_has_products()
    {
        var (client, licenseId) = await SeedAsync();
        var tisort = await CreateAsync(client, "Tişört");

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            db.Products.Add(new Product
            {
                Id = Guid.NewGuid(), LicenseId = licenseId, CategoryId = tisort.Id,
                Code = "A1", Name = "Basic", DefaultPrice = 100m,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.DeleteAsync($"/api/panel/categories/{tisort.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("category-has-products");
    }

    [Fact]
    public async Task Delete_removes_an_empty_leaf()
    {
        var (client, _) = await SeedAsync();
        var bos = await CreateAsync(client, "Boş");

        var resp = await client.DeleteAsync($"/api/panel/categories/{bos.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var rows = await client.GetFromJsonAsync<List<CategoryDto>>("/api/panel/categories");
        rows!.Should().NotContain(r => r.Id == bos.Id);
    }

    [Fact]
    public async Task Another_tenants_category_is_invisible_not_forbidden()
    {
        var (clientA, _) = await SeedAsync();
        var erkek = await CreateAsync(clientA, "Erkek");

        var (clientB, _) = await SeedAsync();
        var resp = await clientB.DeleteAsync($"/api/panel/categories/{erkek.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Kırmızıyı doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelCategoriesControllerTests"`

Beklenen: 13 test de 404 alarak düşer (controller yok).

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Controllers/Panel/PanelCategoriesController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Kategori ağacı (Faz 1a). Sınırsız derinlik; ürün ağacın herhangi bir
/// seviyesine bağlanabilir.
///
/// Yol id tabanlı tutulduğu için alt ağaç sorgusu tek <c>StartsWith</c>;
/// recursive CTE yok.
/// </summary>
[ApiController]
[Route("api/panel/categories")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelCategoriesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelCategoriesController(LicenseDbContext db) => _db = db;

    public sealed record CreateRequest(string Name, Guid? ParentCategoryId, int SortOrder);
    public sealed record UpdateRequest(string Name, int SortOrder, bool IsActive);
    public sealed record MoveRequest(Guid? ParentCategoryId);

    public sealed record CategoryDto(
        Guid Id,
        Guid? ParentCategoryId,
        string Name,
        string Path,
        int Depth,
        int SortOrder,
        bool IsActive);

    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var rows = await _db.Categories
            .Where(c => c.LicenseId == licenseId.Value)
            .OrderBy(c => c.Path)
            .ToListAsync(ct);

        return Ok(rows.Select(ToDto));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-name",
                detail: "Kategori adı boş olamaz.", statusCode: 400);

        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        string? parentPath = null;
        if (req.ParentCategoryId is not null)
        {
            parentPath = await _db.Categories
                .Where(c => c.Id == req.ParentCategoryId.Value && c.LicenseId == licenseId.Value)
                .Select(c => c.Path)
                .FirstOrDefaultAsync(ct);

            if (parentPath is null)
                return Problem(title: "parent-not-found",
                    detail: "Üst kategori bulunamadı.", statusCode: 400);
        }

        var now = DateTimeOffset.UtcNow;
        var category = new Category
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ParentCategoryId = req.ParentCategoryId,
            Name = req.Name.Trim(),
            SortOrder = req.SortOrder,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
        category.Path = CategoryPathService.BuildPath(parentPath, category.Id);

        _db.Categories.Add(category);
        await _db.SaveChangesAsync(ct);

        return Created($"/api/panel/categories/{category.Id}", ToDto(category));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-name",
                detail: "Kategori adı boş olamaz.", statusCode: 400);

        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var category = await FindAsync(id, licenseId.Value, ct);
        if (category is null) return NotFound();

        category.Name = req.Name.Trim();
        category.SortOrder = req.SortOrder;
        category.IsActive = req.IsActive;
        category.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(category));
    }

    /// <summary>
    /// Kategoriyi başka bir ebeveynin altına (ya da köke) taşır ve ALT AĞACIN
    /// TAMAMININ yolunu yeniden yazar. Kendi alt ağacına taşıma reddedilir.
    /// </summary>
    [HttpPut("{id:guid}/parent")]
    public async Task<IActionResult> Move(Guid id, [FromBody] MoveRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var category = await FindAsync(id, licenseId.Value, ct);
        if (category is null) return NotFound();

        string? newParentPath = null;
        if (req.ParentCategoryId is not null)
        {
            newParentPath = await _db.Categories
                .Where(c => c.Id == req.ParentCategoryId.Value && c.LicenseId == licenseId.Value)
                .Select(c => c.Path)
                .FirstOrDefaultAsync(ct);

            if (newParentPath is null)
                return Problem(title: "parent-not-found",
                    detail: "Üst kategori bulunamadı.", statusCode: 400);
        }

        if (CategoryPathService.WouldCreateCycle(category.Path, newParentPath))
            return Problem(title: "category-cycle",
                detail: "Bir kategori kendi alt ağacına taşınamaz.", statusCode: 400);

        var oldPrefix = category.Path;
        var newPrefix = CategoryPathService.BuildPath(newParentPath, category.Id);

        // Alt ağacın TAMAMI (kendisi dahil) — tek StartsWith ile bulunur.
        var subtree = await _db.Categories
            .Where(c => c.LicenseId == licenseId.Value && c.Path.StartsWith(oldPrefix))
            .ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        foreach (var node in subtree)
        {
            node.Path = CategoryPathService.Rebase(node.Path, oldPrefix, newPrefix);
            node.UpdatedAt = now;
        }

        category.ParentCategoryId = req.ParentCategoryId;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(category));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var category = await FindAsync(id, licenseId.Value, ct);
        if (category is null) return NotFound();

        var hasChildren = await _db.Categories
            .AnyAsync(c => c.ParentCategoryId == id, ct);
        if (hasChildren)
            return Problem(title: "category-has-children",
                detail: "Önce alt kategorileri taşı ya da sil.", statusCode: 409);

        var hasProducts = await _db.Products.AnyAsync(p => p.CategoryId == id, ct);
        if (hasProducts)
            return Problem(title: "category-has-products",
                detail: "Bu kategoride ürün var.", statusCode: 409);

        _db.Categories.Remove(category);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private Task<Category?> FindAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Categories.FirstOrDefaultAsync(
            c => c.Id == id && c.LicenseId == licenseId, ct);

    private static CategoryDto ToDto(Category c) => new(
        c.Id, c.ParentCategoryId, c.Name, c.Path,
        Depth: c.Path.Count(ch => ch == '/') - 2,
        c.SortOrder, c.IsActive);

    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        return _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
```

> `Depth` hesabı: kök yol `/{id}/` iki `/` taşır → derinlik 0. Her seviye bir
> `/` ekler. Bu yüzden `sayı('/') - 2`.

- [ ] **Step 4: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelCategoriesControllerTests"`

Beklenen: 13 test geçer.

- [ ] **Step 5: Convention testini koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelControllerConventionTests"`

Beklenen: geçer. (Bu test yeni Panel controller'ında `[ApiController]` ve
`[Authorize(AuthenticationSchemes = "Bearer-Customer")]` yoksa CI'ı kırar.)

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelCategoriesController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelCategoriesControllerTests.cs
git commit -m "feat(katalog): kategori agaci panel uclari"
```

---

### Task 6: Ürün kartı panel uçları (`PanelProductsController`)

Ürün kartının kendisi: kod, ad, fiyat, maliyet, kategori bağı, iki eksenin adı ve
rolü. Varyant satırları Task 7'de; burada yalnız **eksensiz ürünün tek otomatik
varyantı** üretilir (Karar 4).

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs`

**Uçlar:**

| Metot | Yol | İş |
|---|---|---|
| `GET` | `/api/panel/products?categoryId=&q=&includeArchived=&page=&pageSize=` | listeleme (alt ağaç filtresi) |
| `GET` | `/api/panel/products/next-code` | bir sonraki boş kod |
| `GET` | `/api/panel/products/{id}` | ürün + varyantları |
| `POST` | `/api/panel/products` | oluştur |
| `PUT` | `/api/panel/products/{id}` | güncelle |
| `DELETE` | `/api/panel/products/{id}` | sil (varyantlar cascade) |

> **1b notu:** `DELETE` şu an koşulsuz siliyor çünkü stok hareketi diye bir şey
> henüz yok. Faz 1b hareket tablosunu eklerken buraya "hareketi olan ürün
> silinemez" kapısı gelecek. Planda bu bilinçli bir sıralama; şimdi guard yazmak
> test edilemeyen ölü kod olurdu.

**Doğrulama kuralları (hata `title` sözlüğü):**

| Slug | Durum | Kod |
|---|---|---|
| `no-active-license` | müşterinin aktif lisansı yok | 400 |
| `missing-name` | ad boş | 400 |
| `invalid-price` | `DefaultPrice < 0` ya da `Cost < 0` | 400 |
| `category-not-found` | `CategoryId` bu lisansta yok | 400 |
| `axis-order` | `Axis2Name` var ama `Axis1Name` yok | 400 |
| `missing-axis-role` | eksen adı var, rolü yok | 400 |
| `duplicate-axis-role` | iki eksene de aynı rol verilmiş | 400 |
| `duplicate-code` | kod bu lisansta zaten var | 409 |
| `axis-in-use` | eksen açılıp/kapatılıyor ama değerli varyant var | 409 |

- [ ] **Step 1: Kırmızı testleri yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductsControllerTests(ApiFactory f) => _factory = f;

    private sealed record VariantDto(
        Guid Id, string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode, bool IsActive);

    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        bool IsArchived, List<VariantDto> Variants);

    private sealed record ProductRow(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, bool IsArchived, int VariantCount);

    private sealed record ProductPage(List<ProductRow> Items, int Total);

    private sealed record CategoryDto(
        Guid Id, Guid? ParentCategoryId, string Name, string Path,
        int Depth, int SortOrder, bool IsActive);

    private sealed record NextCodeDto(string Code);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<(HttpClient client, Guid licenseId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PROD-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    private static async Task<CategoryDto> CreateCategoryAsync(
        HttpClient client, string name, Guid? parentId = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/categories",
            new { name, parentCategoryId = parentId, sortOrder = 0 });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<CategoryDto>())!;
    }

    private static Task<HttpResponseMessage> PostProductAsync(
        HttpClient client, string name, string? code = null, Guid? categoryId = null,
        decimal price = 100m, decimal? cost = null,
        string? axis1Name = null, int? axis1Role = null,
        string? axis2Name = null, int? axis2Role = null)
        => client.PostAsJsonAsync("/api/panel/products", new
        {
            name, code, categoryId, defaultPrice = price, cost,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });

    private static async Task<ProductDto> CreateProductAsync(
        HttpClient client, string name, string? code = null, Guid? categoryId = null,
        decimal price = 100m, string? axis1Name = null, int? axis1Role = null,
        string? axis2Name = null, int? axis2Role = null)
    {
        var resp = await PostProductAsync(client, name, code, categoryId, price,
            null, axis1Name, axis1Role, axis2Name, axis2Role);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    [Fact]
    public async Task Requires_authentication()
    {
        var client = _factory.CreateClient();

        var resp = await client.GetAsync("/api/panel/products");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Create_assigns_A1_to_the_first_product()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Basic tişört");

        product.Code.Should().Be("A1");
    }

    [Fact]
    public async Task Create_assigns_A2_to_the_second_product()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Birinci");

        var second = await CreateProductAsync(client, "İkinci");

        second.Code.Should().Be("A2");
    }

    [Fact]
    public async Task Create_normalizes_the_manual_code_and_rejects_the_duplicate()
    {
        var (client, _) = await SeedAsync();

        var first = await CreateProductAsync(client, "Elle kodlu", code: "  a5 ");
        first.Code.Should().Be("A5");

        var resp = await PostProductAsync(client, "Aynı kod", code: "A5");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-code");
    }

    [Fact]
    public async Task Create_400_on_empty_name()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "   ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-name");
    }

    [Fact]
    public async Task Create_400_on_negative_price()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Eksi fiyat", price: -1m);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-price");
    }

    [Fact]
    public async Task Create_400_on_unknown_category()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Kayıp kategori", categoryId: Guid.NewGuid());

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("category-not-found");
    }

    [Fact]
    public async Task Create_400_when_axis2_is_set_without_axis1()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Ters eksen",
            axis2Name: "Beden", axis2Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("axis-order");
    }

    [Fact]
    public async Task Create_400_when_an_axis_has_no_role()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Rolsüz eksen", axis1Name: "Renk");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-role");
    }

    [Fact]
    public async Task Create_400_when_both_axes_share_a_role()
    {
        var (client, _) = await SeedAsync();

        var resp = await PostProductAsync(client, "Çift satıcı",
            axis1Name: "Renk", axis1Role: 1, axis2Name: "Beden", axis2Role: 1);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("duplicate-axis-role");
    }

    [Fact]
    public async Task Axisless_product_gets_exactly_one_auto_variant()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Tek kalem");

        product.Variants.Should().HaveCount(1);
        product.Variants[0].VariantCode.Should().Be(product.Code);
        product.Variants[0].Axis1Value.Should().BeNull();
    }

    [Fact]
    public async Task Product_with_an_axis_starts_with_no_variants()
    {
        var (client, _) = await SeedAsync();

        var product = await CreateProductAsync(client, "Renkli",
            axis1Name: "Renk", axis1Role: 2);

        product.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task List_filters_by_the_category_subtree()
    {
        var (client, _) = await SeedAsync();
        var erkek = await CreateCategoryAsync(client, "Erkek");
        var tisort = await CreateCategoryAsync(client, "Tişört", erkek.Id);
        var kadin = await CreateCategoryAsync(client, "Kadın");
        await CreateProductAsync(client, "Erkek tişört", categoryId: tisort.Id);
        await CreateProductAsync(client, "Kadın elbise", categoryId: kadin.Id);

        var page = await client.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?categoryId={erkek.Id}");

        page!.Items.Should().ContainSingle(p => p.Name == "Erkek tişört");
        page.Total.Should().Be(1);
    }

    [Fact]
    public async Task List_filters_by_name_or_code()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Keten gömlek");
        var pantolon = await CreateProductAsync(client, "Kot pantolon");

        var byName = await client.GetFromJsonAsync<ProductPage>("/api/panel/products?q=pantolon");
        byName!.Items.Should().ContainSingle(p => p.Name == "Kot pantolon");

        var byCode = await client.GetFromJsonAsync<ProductPage>(
            $"/api/panel/products?q={pantolon.Code.ToLowerInvariant()}");
        byCode!.Items.Should().ContainSingle(p => p.Id == pantolon.Id);
    }

    [Fact]
    public async Task List_reports_the_variant_count()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Tek kalem");

        var page = await client.GetFromJsonAsync<ProductPage>("/api/panel/products");

        page!.Items.Single().VariantCount.Should().Be(1);
    }

    [Fact]
    public async Task Get_of_another_tenants_product_returns_404()
    {
        var (clientA, _) = await SeedAsync();
        var product = await CreateProductAsync(clientA, "A ürünü");
        var (clientB, _) = await SeedAsync();

        var resp = await clientB.GetAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_renames_and_moves_the_product()
    {
        var (client, _) = await SeedAsync();
        var category = await CreateCategoryAsync(client, "Ayakkabı");
        var product = await CreateProductAsync(client, "Eski ad");

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = "Yeni ad", code = product.Code, categoryId = category.Id,
            defaultPrice = 250m, cost = 120m,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Name.Should().Be("Yeni ad");
        dto.CategoryId.Should().Be(category.Id);
        dto.DefaultPrice.Should().Be(250m);
        dto.Cost.Should().Be(120m);
    }

    [Fact]
    public async Task Update_rewrites_the_auto_variant_code_when_the_product_code_changes()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tek kalem");

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = "B7", categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Code.Should().Be("B7");
        dto.Variants.Single().VariantCode.Should().Be("B7");
    }

    [Fact]
    public async Task Update_drops_the_auto_variant_when_an_axis_is_switched_on()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Tek kalem");
        product.Variants.Should().HaveCount(1);

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = product.Code, categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = "Renk", axis1Role = 2,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
        dto.Axis1Name.Should().Be("Renk");
        dto.Variants.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_409_when_an_axis_is_switched_off_while_valued_variants_exist()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Renkli",
            axis1Name: "Renk", axis1Role: 2);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var entity = db.Products.Single(p => p.Id == product.Id);
            db.ProductVariants.Add(new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = entity.LicenseId, ProductId = entity.Id,
                Axis1Value = "Siyah", Axis1Code = "SIYA",
                VariantCode = entity.Code + "-SIYA", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
        }

        var resp = await client.PutAsJsonAsync($"/api/panel/products/{product.Id}", new
        {
            name = product.Name, code = product.Code, categoryId = (Guid?)null,
            defaultPrice = product.DefaultPrice, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("axis-in-use");
    }

    [Fact]
    public async Task Delete_removes_the_product_and_its_variants()
    {
        var (client, _) = await SeedAsync();
        var product = await CreateProductAsync(client, "Silinecek");

        var resp = await client.DeleteAsync($"/api/panel/products/{product.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Products.Any(p => p.Id == product.Id).Should().BeFalse();
        db.ProductVariants.Any(v => v.ProductId == product.Id).Should().BeFalse();
    }

    [Fact]
    public async Task Next_code_endpoint_returns_the_next_free_code()
    {
        var (client, _) = await SeedAsync();
        await CreateProductAsync(client, "Birinci");

        var dto = await client.GetFromJsonAsync<NextCodeDto>("/api/panel/products/next-code");

        dto!.Code.Should().Be("A2");
    }
}
```

- [ ] **Step 2: Kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelProductsControllerTests"`

Beklenen: `Requires_authentication` dışındaki testler 404 alarak düşer
(controller yok).

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün kartı (Faz 1a). Kart iki eksen taşır; her eksenin <b>adı</b> ve
/// <b>rolü</b> ürüne özeldir (satıcı ekseni barkotla sabitlenir, izleyici ekseni
/// yorumdan gelir). İkisi de kapatılabilir.
///
/// Eksensiz ürün de tek bir varyant satırı taşır (<c>VariantCode = Code</c>) —
/// böylece Faz 1b'de stok hareketi her zaman bir varyanta bağlanabilir.
/// </summary>
[ApiController]
[Route("api/panel/products")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelProductsController : ControllerBase
{
    private const int DefaultPageSize = 50;
    private const int MaxPageSize = 200;

    private readonly LicenseDbContext _db;

    public PanelProductsController(LicenseDbContext db) => _db = db;

    public sealed record UpsertRequest(
        string Name,
        string? Code,
        Guid? CategoryId,
        decimal DefaultPrice,
        decimal? Cost,
        string? Axis1Name,
        AxisRole? Axis1Role,
        string? Axis2Name,
        AxisRole? Axis2Role);

    public sealed record VariantDto(
        Guid Id,
        string? Axis1Value,
        string? Axis1Code,
        string? Axis2Value,
        string? Axis2Code,
        string VariantCode,
        string? Barcode,
        bool IsActive);

    public sealed record ProductDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        decimal DefaultPrice,
        decimal? Cost,
        string? Axis1Name,
        AxisRole? Axis1Role,
        string? Axis2Name,
        AxisRole? Axis2Role,
        string? PhotoObjectKey,
        bool IsArchived,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt,
        IReadOnlyList<VariantDto> Variants);

    public sealed record ProductRowDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        decimal DefaultPrice,
        bool IsArchived,
        string? PhotoObjectKey,
        int VariantCount,
        DateTimeOffset UpdatedAt);

    public sealed record ProductPageDto(IReadOnlyList<ProductRowDto> Items, int Total);

    public sealed record NextCodeDto(string Code);

    [HttpGet]
    public async Task<IActionResult> List(
        [FromQuery] Guid? categoryId,
        [FromQuery] string? q,
        [FromQuery] bool includeArchived,
        [FromQuery] int page,
        [FromQuery] int pageSize,
        CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var query = _db.Products.Where(p => p.LicenseId == licenseId.Value);

        if (!includeArchived)
            query = query.Where(p => !p.IsArchived);

        if (categoryId is not null)
        {
            var path = await _db.Categories
                .Where(c => c.Id == categoryId.Value && c.LicenseId == licenseId.Value)
                .Select(c => c.Path)
                .FirstOrDefaultAsync(ct);

            if (path is null)
                return Problem(title: "category-not-found",
                    detail: "Kategori bulunamadı.", statusCode: 400);

            var subtree = await _db.Categories
                .Where(c => c.LicenseId == licenseId.Value && c.Path.StartsWith(path))
                .Select(c => c.Id)
                .ToListAsync(ct);

            query = query.Where(p => p.CategoryId != null && subtree.Contains(p.CategoryId.Value));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var needle = q.Trim();
            var codeNeedle = needle.ToUpperInvariant();
            query = query.Where(p => p.Name.Contains(needle) || p.Code.Contains(codeNeedle));
        }

        var total = await query.CountAsync(ct);

        var size = pageSize <= 0 ? DefaultPageSize : Math.Min(pageSize, MaxPageSize);
        var skip = Math.Max(page - 1, 0) * size;

        var rows = await query
            .OrderBy(p => p.Code)
            .Skip(skip)
            .Take(size)
            .Select(p => new ProductRowDto(
                p.Id, p.CategoryId, p.Code, p.Name, p.DefaultPrice, p.IsArchived,
                p.PhotoObjectKey, p.Variants.Count, p.UpdatedAt))
            .ToListAsync(ct);

        return Ok(new ProductPageDto(rows, total));
    }

    [HttpGet("next-code")]
    public async Task<IActionResult> NextCode(CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var codes = await _db.Products
            .Where(p => p.LicenseId == licenseId.Value)
            .Select(p => p.Code)
            .ToListAsync(ct);

        return Ok(new NextCodeDto(CatalogCodeSequence.Next(codes)));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        return Ok(ToDto(product));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] UpsertRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var invalid = Validate(req);
        if (invalid is not null) return invalid;

        var categoryError = await ValidateCategoryAsync(req.CategoryId, licenseId.Value, ct);
        if (categoryError is not null) return categoryError;

        var code = NormalizeCode(req.Code);
        if (code.Length == 0)
        {
            var codes = await _db.Products
                .Where(p => p.LicenseId == licenseId.Value)
                .Select(p => p.Code)
                .ToListAsync(ct);
            code = CatalogCodeSequence.Next(codes);
        }
        else if (await _db.Products.AnyAsync(
                     p => p.LicenseId == licenseId.Value && p.Code == code, ct))
        {
            return Problem(title: "duplicate-code",
                detail: $"'{code}' kodu zaten kullanılıyor.", statusCode: 409);
        }

        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            CategoryId = req.CategoryId,
            Code = code,
            Name = req.Name.Trim(),
            DefaultPrice = req.DefaultPrice,
            Cost = req.Cost,
            Axis1Name = Trim(req.Axis1Name),
            Axis1Role = Trim(req.Axis1Name) is null ? null : req.Axis1Role,
            Axis2Name = Trim(req.Axis2Name),
            Axis2Role = Trim(req.Axis2Name) is null ? null : req.Axis2Role,
            IsArchived = false,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.Products.Add(product);

        if (product.Axis1Name is null)
            _db.ProductVariants.Add(BuildAutoVariant(product, now));

        await _db.SaveChangesAsync(ct);

        var saved = await LoadAsync(product.Id, licenseId.Value, ct);
        return CreatedAtAction(nameof(Get), new { id = product.Id }, ToDto(saved!));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid id, [FromBody] UpsertRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        var invalid = Validate(req);
        if (invalid is not null) return invalid;

        var categoryError = await ValidateCategoryAsync(req.CategoryId, licenseId.Value, ct);
        if (categoryError is not null) return categoryError;

        var code = NormalizeCode(req.Code);
        if (code.Length == 0) code = product.Code;
        if (code != product.Code && await _db.Products.AnyAsync(
                p => p.LicenseId == licenseId.Value && p.Code == code, ct))
        {
            return Problem(title: "duplicate-code",
                detail: $"'{code}' kodu zaten kullanılıyor.", statusCode: 409);
        }

        var newAxis1 = Trim(req.Axis1Name);
        var newAxis2 = Trim(req.Axis2Name);
        var toggled = (product.Axis1Name is null) != (newAxis1 is null)
                   || (product.Axis2Name is null) != (newAxis2 is null);

        if (toggled)
        {
            var hasValued = product.Variants.Any(
                v => v.Axis1Value is not null || v.Axis2Value is not null);
            if (hasValued)
                return Problem(title: "axis-in-use",
                    detail: "Eksen açılıp kapatılmadan önce varyantları silmelisin.",
                    statusCode: 409);

            _db.ProductVariants.RemoveRange(product.Variants.ToList());
            product.Variants.Clear();
        }

        var now = DateTimeOffset.UtcNow;
        product.CategoryId = req.CategoryId;
        product.Code = code;
        product.Name = req.Name.Trim();
        product.DefaultPrice = req.DefaultPrice;
        product.Cost = req.Cost;
        product.Axis1Name = newAxis1;
        product.Axis1Role = newAxis1 is null ? null : req.Axis1Role;
        product.Axis2Name = newAxis2;
        product.Axis2Role = newAxis2 is null ? null : req.Axis2Role;
        product.UpdatedAt = now;

        if (product.Axis1Name is null)
        {
            var auto = product.Variants.FirstOrDefault();
            if (auto is null)
            {
                var created = BuildAutoVariant(product, now);
                _db.ProductVariants.Add(created);
                product.Variants.Add(created);
            }
            else
            {
                auto.VariantCode = product.Code;
                auto.UpdatedAt = now;
            }
        }

        await _db.SaveChangesAsync(ct);
        return Ok(ToDto(product));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadAsync(id, licenseId.Value, ct);
        if (product is null) return NotFound();

        _db.ProductVariants.RemoveRange(product.Variants);
        _db.Products.Remove(product);
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private IActionResult? Validate(UpsertRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return Problem(title: "missing-name",
                detail: "Ürün adı boş olamaz.", statusCode: 400);

        if (req.DefaultPrice < 0 || req.Cost < 0)
            return Problem(title: "invalid-price",
                detail: "Fiyat ve maliyet negatif olamaz.", statusCode: 400);

        var axis1 = Trim(req.Axis1Name);
        var axis2 = Trim(req.Axis2Name);

        if (axis1 is null && axis2 is not null)
            return Problem(title: "axis-order",
                detail: "İkinci eksen için önce birinci ekseni tanımlamalısın.", statusCode: 400);

        if ((axis1 is not null && req.Axis1Role is null)
            || (axis2 is not null && req.Axis2Role is null))
            return Problem(title: "missing-axis-role",
                detail: "Her eksenin rolü seçilmeli (satıcı ya da izleyici).", statusCode: 400);

        if (axis1 is not null && axis2 is not null && req.Axis1Role == req.Axis2Role)
            return Problem(title: "duplicate-axis-role",
                detail: "İki eksene aynı rol verilemez.", statusCode: 400);

        return null;
    }

    private async Task<IActionResult?> ValidateCategoryAsync(
        Guid? categoryId, Guid licenseId, CancellationToken ct)
    {
        if (categoryId is null) return null;

        var exists = await _db.Categories.AnyAsync(
            c => c.Id == categoryId.Value && c.LicenseId == licenseId, ct);

        return exists
            ? null
            : Problem(title: "category-not-found", detail: "Kategori bulunamadı.", statusCode: 400);
    }

    private static ProductVariant BuildAutoVariant(Product product, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = product.LicenseId,
        ProductId = product.Id,
        VariantCode = product.Code,
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };

    private Task<Product?> LoadAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private static string NormalizeCode(string? code)
        => (code ?? string.Empty).Trim().ToUpperInvariant();

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static ProductDto ToDto(Product p) => new(
        p.Id, p.CategoryId, p.Code, p.Name, p.DefaultPrice, p.Cost,
        p.Axis1Name, p.Axis1Role, p.Axis2Name, p.Axis2Role,
        p.PhotoObjectKey, p.IsArchived, p.CreatedAt, p.UpdatedAt,
        p.Variants
            .OrderBy(v => v.VariantCode, StringComparer.Ordinal)
            .Select(v => new VariantDto(
                v.Id, v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
                v.VariantCode, v.Barcode, v.IsActive))
            .ToList());

    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        return _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
```

> **Kodun büyük harfe yazma anında normalize edilmesi bilinçli.** Sorguda
> `ToUpper()` yok; `p.Code == code` karşılaştırması Postgres'te de aynı sonucu
> verir. Ad araması (`p.Name.Contains`) SQL Server'ın harf duyarsız
> collation'ında duyarsız, Postgres'te duyarlı olur — göç anında `ILIKE`'a
> çevrilecek tek yer burası, ve testler bunu kod üzerinden (normalize edilmiş)
> doğruluyor.

- [ ] **Step 4: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelProductsControllerTests"`

Beklenen: 22 test geçer.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs
git commit -m "feat(katalog): urun karti panel uclari"
```

---

### Task 7: Varyant uçları (`PanelProductVariantsController`)

Varyant satırı = eksen değerlerinin bir kombinasyonu. Değerin **görünen adı**
serbest (`Yeşil`), **kod parçası** ASCII'ye türetilir (`YESI`) ve elle
düzeltilebilir — Code128 Türkçe harf kabul etmediği için (Faz 1c'de barkot bu
koddan basılacak).

Ayrı controller çünkü sorumluluk ayrı ve `PanelProductsController` zaten dolu;
`VariantDto` aynı namespace'te (`PanelProductsController.VariantDto`) durduğu
için tekrar tanımlanmıyor.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductVariantsControllerTests.cs`

**Uçlar:**

| Metot | Yol | İş |
|---|---|---|
| `POST` | `/api/panel/products/{productId}/variants` | varyant ekle |
| `PUT` | `/api/panel/products/{productId}/variants/{id}` | varyant güncelle |
| `DELETE` | `/api/panel/products/{productId}/variants/{id}` | varyant sil |

**Hata `title` sözlüğü:**

| Slug | Durum | Kod |
|---|---|---|
| `no-active-license` | aktif lisans yok | 400 |
| `product-has-no-axis` | ürün eksensiz, varyant elle eklenemez | 400 |
| `missing-axis-value` | zorunlu eksen değeri boş | 400 |
| `unexpected-axis-value` | ürün tek eksenli ama 2. eksen değeri gönderilmiş | 400 |
| `invalid-axis-code` | değerden ASCII kod türetilemedi ve elle de verilmedi | 400 |
| `duplicate-variant` | aynı üründe aynı varyant kodu | 409 |

- [ ] **Step 1: Kırmızı testleri yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductVariantsControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductVariantsControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductVariantsControllerTests(ApiFactory f) => _factory = f;

    private sealed record VariantDto(
        Guid Id, string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode, bool IsActive);

    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        bool IsArchived, List<VariantDto> Variants);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<HttpClient> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-VARI-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ProductDto> CreateProductAsync(
        HttpClient client,
        string? axis1Name = "Renk", int? axis1Role = 2,
        string? axis2Name = null, int? axis2Role = null)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Deneme ürünü", code = (string?)null, categoryId = (Guid?)null,
            defaultPrice = 100m, cost = (decimal?)null,
            axis1Name, axis1Role, axis2Name, axis2Role,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> PostVariantAsync(
        HttpClient client, Guid productId,
        string? axis1Value = null, string? axis1Code = null,
        string? axis2Value = null, string? axis2Code = null)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value, axis1Code, axis2Value, axis2Code, isActive = true });

    [Fact]
    public async Task Create_derives_the_code_from_the_display_value()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Yeşil");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Value.Should().Be("Yeşil");
        dto.Axis1Code.Should().Be("YESI");
        dto.VariantCode.Should().Be($"{product.Code}-YESI");
    }

    [Fact]
    public async Task Create_prefers_the_manually_supplied_code()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Yeşil", axis1Code: "yes");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Code.Should().Be("YES");
        dto.VariantCode.Should().Be($"{product.Code}-YES");
    }

    [Fact]
    public async Task Create_builds_a_two_segment_code_for_a_two_axis_product()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis2Code.Should().Be("38");
        dto.VariantCode.Should().Be($"{product.Code}-SIYA-38");
    }

    [Fact]
    public async Task Create_404_for_another_tenants_product()
    {
        var clientA = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var clientB = await SeedAsync();

        var resp = await PostVariantAsync(clientB, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Create_400_when_the_product_has_no_axis()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client, axis1Name: null, axis1Role: null);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("product-has-no-axis");
    }

    [Fact]
    public async Task Create_400_when_the_first_axis_value_is_missing()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "  ");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_the_second_axis_value_is_missing()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client,
            axis1Name: "Renk", axis1Role: 2, axis2Name: "Beden", axis2Role: 1);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyah");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("missing-axis-value");
    }

    [Fact]
    public async Task Create_400_when_a_second_value_is_sent_to_a_single_axis_product()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id,
            axis1Value: "Siyah", axis2Value: "38");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unexpected-axis-value");
    }

    [Fact]
    public async Task Create_400_when_no_ascii_code_can_be_derived()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "•••");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-axis-code");
    }

    [Fact]
    public async Task Create_409_on_a_duplicate_variant_code()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await PostVariantAsync(client, product.Id, axis1Value: "Siyahımsı");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    [Fact]
    public async Task Update_recomputes_the_variant_code()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}",
            new
            {
                axis1Value = "Beyaz", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = false,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<VariantDto>())!;
        dto.Axis1Code.Should().Be("BEYA");
        dto.VariantCode.Should().Be($"{product.Code}-BEYA");
        dto.IsActive.Should().BeFalse();
    }

    [Fact]
    public async Task Update_409_when_it_collides_with_a_sibling()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        await PostVariantAsync(client, product.Id, axis1Value: "Siyah");
        var beyaz = (await (await PostVariantAsync(client, product.Id, axis1Value: "Beyaz"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.PutAsJsonAsync(
            $"/api/panel/products/{product.Id}/variants/{beyaz.Id}",
            new
            {
                axis1Value = "Siyah", axis1Code = (string?)null,
                axis2Value = (string?)null, axis2Code = (string?)null,
                isActive = true,
            });

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("duplicate-variant");
    }

    [Fact]
    public async Task Delete_removes_the_variant()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var created = (await (await PostVariantAsync(client, product.Id, axis1Value: "Siyah"))
            .Content.ReadFromJsonAsync<VariantDto>())!;

        var resp = await client.DeleteAsync(
            $"/api/panel/products/{product.Id}/variants/{created.Id}");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.Variants.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelProductVariantsControllerTests"`

Beklenen: 13 test 404 alarak düşer (controller yok).

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün varyantları (Faz 1a). Varyant kodu <c>ÜRÜNKODU-EKSEN1[-EKSEN2]</c>
/// biçiminde ve yalnız ASCII harf/rakam taşır — Faz 1c'de Code128 barkot bu
/// koddan basılacak, Code128 Türkçe harf kabul etmiyor.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/variants")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelProductVariantsController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelProductVariantsController(LicenseDbContext db) => _db = db;

    public sealed record VariantRequest(
        string? Axis1Value,
        string? Axis1Code,
        string? Axis2Value,
        string? Axis2Code,
        bool IsActive);

    [HttpPost]
    public async Task<IActionResult> Create(
        Guid productId, [FromBody] VariantRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var built = BuildSegments(product, req, out var error);
        if (error is not null) return error;

        if (product.Variants.Any(v => v.VariantCode == built.VariantCode))
            return Duplicate(built.VariantCode);

        var now = DateTimeOffset.UtcNow;
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = built.Axis1Value,
            Axis1Code = built.Axis1Code,
            Axis2Value = built.Axis2Value,
            Axis2Code = built.Axis2Code,
            VariantCode = built.VariantCode,
            IsActive = req.IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        };
        _db.ProductVariants.Add(variant);
        product.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Created(
            $"/api/panel/products/{product.Id}/variants/{variant.Id}", ToDto(variant));
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(
        Guid productId, Guid id, [FromBody] VariantRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var variant = product.Variants.FirstOrDefault(v => v.Id == id);
        if (variant is null) return NotFound();

        var built = BuildSegments(product, req, out var error);
        if (error is not null) return error;

        if (product.Variants.Any(v => v.Id != id && v.VariantCode == built.VariantCode))
            return Duplicate(built.VariantCode);

        var now = DateTimeOffset.UtcNow;
        variant.Axis1Value = built.Axis1Value;
        variant.Axis1Code = built.Axis1Code;
        variant.Axis2Value = built.Axis2Value;
        variant.Axis2Code = built.Axis2Code;
        variant.VariantCode = built.VariantCode;
        variant.IsActive = req.IsActive;
        variant.UpdatedAt = now;
        product.UpdatedAt = now;
        await _db.SaveChangesAsync(ct);

        return Ok(ToDto(variant));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(
        Guid productId, Guid id, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await LoadProductAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var variant = product.Variants.FirstOrDefault(v => v.Id == id);
        if (variant is null) return NotFound();

        _db.ProductVariants.Remove(variant);
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);
        return NoContent();
    }

    private readonly record struct Segments(
        string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode);

    /// <summary>
    /// Eksen değerlerini doğrular, kod parçalarını türetir ve varyant kodunu kurar.
    /// Hata varsa <paramref name="error"/> dolar; dönen değer o durumda anlamsızdır.
    /// </summary>
    private Segments BuildSegments(Product product, VariantRequest req, out IActionResult? error)
    {
        error = null;

        if (product.Axis1Name is null)
        {
            error = Problem(title: "product-has-no-axis",
                detail: "Eksensiz üründe varyant satırı elle eklenemez.", statusCode: 400);
            return default;
        }

        var axis1Value = Trim(req.Axis1Value);
        var axis2Value = Trim(req.Axis2Value);

        if (axis1Value is null || (product.Axis2Name is not null && axis2Value is null))
        {
            error = Problem(title: "missing-axis-value",
                detail: "Her eksen için bir değer girmelisin.", statusCode: 400);
            return default;
        }

        if (product.Axis2Name is null && axis2Value is not null)
        {
            error = Problem(title: "unexpected-axis-value",
                detail: "Bu ürünün ikinci ekseni yok.", statusCode: 400);
            return default;
        }

        var axis1Code = ResolveCode(req.Axis1Code, axis1Value);
        var axis2Code = axis2Value is null ? null : ResolveCode(req.Axis2Code, axis2Value);

        if (axis1Code.Length == 0 || axis2Code?.Length == 0)
        {
            error = Problem(title: "invalid-axis-code",
                detail: "Değerden ASCII kod türetilemedi; kodu elle gir.", statusCode: 400);
            return default;
        }

        var variantCode = axis2Code is null
            ? $"{product.Code}-{axis1Code}"
            : $"{product.Code}-{axis1Code}-{axis2Code}";

        return new Segments(axis1Value, axis1Code, axis2Value, axis2Code, variantCode);
    }

    private IActionResult Duplicate(string variantCode)
        => Problem(title: "duplicate-variant",
            detail: $"'{variantCode}' varyantı bu üründe zaten var.", statusCode: 409);

    private static string ResolveCode(string? supplied, string displayValue)
    {
        var manual = AxisCodeDeriver.Derive(supplied);
        return manual.Length > 0 ? manual : AxisCodeDeriver.Derive(displayValue);
    }

    private Task<Product?> LoadProductAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private static string? Trim(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static PanelProductsController.VariantDto ToDto(ProductVariant v) => new(
        v.Id, v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
        v.VariantCode, v.Barcode, v.IsActive);

    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        return _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
```

> `Siyah` ve `Siyahımsı` ikisi de `SIYA` türetir → ikinci ekleme 409 verir.
> Bu kasıtlı: kullanıcı kodu elle ayırmak zorunda (`SIYM`). InMemory sağlayıcı
> tekil indeksi zorlamadığı için 409'u üreten şey testte de üretimde de
> controller'ın kendi kontrolü; `(ProductId, VariantCode)` tekil indeksi
> yalnız son savunma hattı.

- [ ] **Step 4: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelProductVariantsControllerTests"`

Beklenen: 13 test geçer.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductVariantsControllerTests.cs
git commit -m "feat(katalog): varyant uclari ve ASCII kod turetme"
```

---

### Task 8: Ürün fotoğrafı uçları (`PanelProductPhotoController`)

Fotoğraf **BroadcastPost deseniyle birebir**: sunucu presigned bir yükleme
URL'si üretir, panel baytları doğrudan R2'ye `PUT`'lar, sonra sunucuya
`ObjectKey`'i bildirir; sunucu `HeadAsync` ile nesnenin gerçekten orada
olduğunu doğrular ve **istemcinin iddia ettiği tip/boyutu değil, R2'nin
söylediğini** kaydeder.

Mevcut `IBroadcastMediaStorage` yeniden kullanılıyor — aynı R2 kovası, aynı
SigV4 yapılandırması. Yeni bir depolama soyutlaması açmak kopyalanmış
yapılandırma ve testte ikinci bir sahte demek olurdu; `ApiFactory` zaten
`FakeBroadcastMediaStorage`'ı yerine koyuyor.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductPhotoController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductPhotoControllerTests.cs`

**Uçlar:**

| Metot | Yol | İş |
|---|---|---|
| `POST` | `/api/panel/products/{productId}/photo/upload-url` | presigned yükleme URL'si + `objectKey` |
| `PUT` | `/api/panel/products/{productId}/photo` | yüklenen nesneyi ürüne bağla |
| `GET` | `/api/panel/products/{productId}/photo/url` | presigned indirme URL'si |
| `DELETE` | `/api/panel/products/{productId}/photo` | fotoğrafı kaldır (R2'den de sil) |

**Kurallar:**

- İzinli tipler: `image/jpeg`, `image/png`, `image/webp`. Başka tip →
  `unsupported-media-type` 400.
- Boyut `0 < size ≤ 5 MB`. Aşarsa `file-too-large` 400.
- Anahtar biçimi: `{licenseId:N}/products/{productId:N}/{rastgele:N}.img`.
  Rastgele parça her yüklemede yenilenir → CDN/tarayıcı önbelleği eski kareyi
  göstermez.
- Bağlama sırasında anahtar bu önekle başlamıyorsa `invalid-object-key` 400 —
  başka kiracının anahtarını yapıştırma denemesi burada ölür.
- R2'de nesne yoksa `object-not-found` 400.
- Ürün başına tek fotoğraf: yeni fotoğraf bağlanınca eski anahtar R2'den silinir.
- **Küçültme sunucunun işi değil** (baytlar sunucudan geçmiyor). Panel canvas ile
  küçültüp yükler; sunucu yalnız tip ve boyut sınırını uygular. `PhotoWidth` /
  `PhotoHeight` panelin bildirdiği bilgidir, doğrulanmaz — yalnız panelin
  yerleşimi (en-boy oranı) için tutulur.

- [ ] **Step 1: Kırmızı testleri yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductPhotoControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelProductPhotoControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelProductPhotoControllerTests(ApiFactory f) => _factory = f;

    private sealed record UploadUrlDto(string ObjectKey, string UploadUrl);
    private sealed record PhotoDto(
        string ObjectKey, string ContentType, long SizeBytes, int? Width, int? Height);
    private sealed record PhotoUrlDto(string Url);
    private sealed record ProductDto(
        Guid Id, Guid? CategoryId, string Code, string Name,
        decimal DefaultPrice, decimal? Cost,
        string? Axis1Name, int? Axis1Role, string? Axis2Name, int? Axis2Role,
        string? PhotoObjectKey, bool IsArchived);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<HttpClient> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PHOT-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static async Task<ProductDto> CreateProductAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Fotoğraflı ürün", code = (string?)null, categoryId = (Guid?)null,
            defaultPrice = 100m, cost = (decimal?)null,
            axis1Name = (string?)null, axis1Role = (int?)null,
            axis2Name = (string?)null, axis2Role = (int?)null,
        });
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<ProductDto>())!;
    }

    private static Task<HttpResponseMessage> UploadUrlAsync(
        HttpClient client, Guid productId,
        string contentType = "image/jpeg", long sizeBytes = 120_000)
        => client.PostAsJsonAsync($"/api/panel/products/{productId}/photo/upload-url",
            new { contentType, sizeBytes });

    private static Task<HttpResponseMessage> AttachAsync(
        HttpClient client, Guid productId, string objectKey,
        int? width = 800, int? height = 800)
        => client.PutAsJsonAsync($"/api/panel/products/{productId}/photo",
            new { objectKey, width, height });

    /// <summary>Panelin R2'ye yaptığı PUT'un yerine geçer.</summary>
    private void SimulateUpload(string objectKey, long size = 120_000,
        string contentType = "image/jpeg")
        => _factory.BroadcastMedia.Seed(objectKey, size, contentType);

    [Fact]
    public async Task Upload_url_is_scoped_to_the_license_and_product()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await UploadUrlAsync(client, product.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<UploadUrlDto>())!;
        dto.ObjectKey.Should().Contain($"/products/{product.Id:N}/");
        dto.UploadUrl.Should().NotBeNullOrWhiteSpace();
        _factory.BroadcastMedia.UploadCalls
            .Should().Contain(c => c.Key == dto.ObjectKey && c.ContentType == "image/jpeg");
    }

    [Fact]
    public async Task Upload_url_400_on_an_unsupported_content_type()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await UploadUrlAsync(client, product.Id, contentType: "application/pdf");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unsupported-media-type");
    }

    [Fact]
    public async Task Upload_url_400_when_the_file_is_too_large()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await UploadUrlAsync(client, product.Id, sizeBytes: 6 * 1024 * 1024);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("file-too-large");
    }

    [Fact]
    public async Task Upload_url_404_for_another_tenants_product()
    {
        var clientA = await SeedAsync();
        var product = await CreateProductAsync(clientA);
        var clientB = await SeedAsync();

        var resp = await UploadUrlAsync(clientB, product.Id);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Attach_records_the_size_and_type_reported_by_storage()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key, size: 99_000, contentType: "image/png");

        var resp = await AttachAsync(client, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = (await resp.Content.ReadFromJsonAsync<PhotoDto>())!;
        dto.ObjectKey.Should().Be(key);
        dto.SizeBytes.Should().Be(99_000);
        dto.ContentType.Should().Be("image/png");
        dto.Width.Should().Be(800);

        var product2 = await client.GetFromJsonAsync<ProductDto>(
            $"/api/panel/products/{product.Id}");
        product2!.PhotoObjectKey.Should().Be(key);
    }

    [Fact]
    public async Task Attach_400_when_the_object_is_not_in_storage()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;

        var resp = await AttachAsync(client, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("object-not-found");
    }

    [Fact]
    public async Task Attach_400_on_a_key_outside_the_products_prefix()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        const string foreignKey = "00000000000000000000000000000000/products/x/evil.img";
        SimulateUpload(foreignKey);

        var resp = await AttachAsync(client, product.Id, foreignKey);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-object-key");
    }

    [Fact]
    public async Task Attach_400_when_storage_reports_an_unsupported_type()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key, contentType: "application/zip");

        var resp = await AttachAsync(client, product.Id, key);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("unsupported-media-type");
    }

    [Fact]
    public async Task Attaching_a_second_photo_replaces_the_first()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var first = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(first);
        (await AttachAsync(client, product.Id, first)).StatusCode.Should().Be(HttpStatusCode.OK);

        var second = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(second);
        var resp = await AttachAsync(client, product.Id, second);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        second.Should().NotBe(first);
        var dto = (await resp.Content.ReadFromJsonAsync<PhotoDto>())!;
        dto.ObjectKey.Should().Be(second);
    }

    [Fact]
    public async Task Photo_url_returns_404_when_there_is_no_photo()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);

        var resp = await client.GetAsync($"/api/panel/products/{product.Id}/photo/url");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Photo_url_returns_a_download_url_when_a_photo_exists()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key);
        await AttachAsync(client, product.Id, key);

        var dto = await client.GetFromJsonAsync<PhotoUrlDto>(
            $"/api/panel/products/{product.Id}/photo/url");

        dto!.Url.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Delete_clears_the_photo_fields()
    {
        var client = await SeedAsync();
        var product = await CreateProductAsync(client);
        var key = (await (await UploadUrlAsync(client, product.Id))
            .Content.ReadFromJsonAsync<UploadUrlDto>())!.ObjectKey;
        SimulateUpload(key);
        await AttachAsync(client, product.Id, key);

        var resp = await client.DeleteAsync($"/api/panel/products/{product.Id}/photo");

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var after = await client.GetFromJsonAsync<ProductDto>($"/api/panel/products/{product.Id}");
        after!.PhotoObjectKey.Should().BeNull();
    }
}
```

- [ ] **Step 2: Kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelProductPhotoControllerTests"`

Beklenen: 12 test 404 alarak düşer (controller yok).

- [ ] **Step 3: Uygula**

`OrderDeck.LicenseServer/Controllers/Panel/PanelProductPhotoController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.BroadcastPosts;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Ürün fotoğrafı (Faz 1a). İki adımlı presigned yükleme: baytlar panelden
/// doğrudan R2'ye gider, sunucu yalnız anahtarı doğrular ve kaydeder.
///
/// Sunucu baytları görmediği için <b>küçültme yapamaz</b>; panel yüklemeden
/// önce küçültür, sunucu sınırı uygular.
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/photo")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelProductPhotoController : ControllerBase
{
    private const long MaxSizeBytes = 5 * 1024 * 1024;

    private static readonly string[] AllowedContentTypes =
        ["image/jpeg", "image/png", "image/webp"];

    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;

    public PanelProductPhotoController(LicenseDbContext db, IBroadcastMediaStorage storage)
    {
        _db = db;
        _storage = storage;
    }

    public sealed record UploadUrlRequest(string ContentType, long SizeBytes);
    public sealed record UploadUrlDto(string ObjectKey, string UploadUrl);
    public sealed record AttachRequest(string ObjectKey, int? Width, int? Height);
    public sealed record PhotoDto(
        string ObjectKey, string ContentType, long SizeBytes, int? Width, int? Height);
    public sealed record PhotoUrlDto(string Url);

    [HttpPost("upload-url")]
    public async Task<IActionResult> CreateUploadUrl(
        Guid productId, [FromBody] UploadUrlRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        if (!IsAllowed(req.ContentType)) return UnsupportedType();
        if (req.SizeBytes <= 0 || req.SizeBytes > MaxSizeBytes)
            return Problem(title: "file-too-large",
                detail: "Fotoğraf en çok 5 MB olabilir.", statusCode: 400);

        var objectKey = Prefix(licenseId.Value, productId) + Guid.NewGuid().ToString("N") + ".img";
        var url = await _storage.CreateUploadUrlAsync(objectKey, req.ContentType, req.SizeBytes, ct);

        return Ok(new UploadUrlDto(objectKey, url));
    }

    [HttpPut]
    public async Task<IActionResult> Attach(
        Guid productId, [FromBody] AttachRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var key = (req.ObjectKey ?? string.Empty).Trim();
        if (!key.StartsWith(Prefix(licenseId.Value, productId), StringComparison.Ordinal))
            return Problem(title: "invalid-object-key",
                detail: "Anahtar bu ürüne ait değil.", statusCode: 400);

        var info = await _storage.HeadAsync(key, ct);
        if (info is null)
            return Problem(title: "object-not-found",
                detail: "Yüklenen dosya depoda bulunamadı.", statusCode: 400);

        if (!IsAllowed(info.ContentType)) return UnsupportedType();
        if (info.SizeBytes <= 0 || info.SizeBytes > MaxSizeBytes)
            return Problem(title: "file-too-large",
                detail: "Fotoğraf en çok 5 MB olabilir.", statusCode: 400);

        var previousKey = product.PhotoObjectKey;

        product.PhotoObjectKey = key;
        product.PhotoContentType = info.ContentType;
        product.PhotoSizeBytes = info.SizeBytes;
        product.PhotoWidth = req.Width;
        product.PhotoHeight = req.Height;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        if (previousKey is not null && previousKey != key)
            await _storage.DeleteAsync(previousKey, ct);

        return Ok(new PhotoDto(key, info.ContentType, info.SizeBytes, req.Width, req.Height));
    }

    [HttpGet("url")]
    public async Task<IActionResult> GetUrl(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product?.PhotoObjectKey is null) return NotFound();

        var url = await _storage.CreateDownloadUrlAsync(product.PhotoObjectKey, ct);
        return Ok(new PhotoUrlDto(url));
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await FindAsync(productId, licenseId.Value, ct);
        if (product is null) return NotFound();

        var key = product.PhotoObjectKey;
        if (key is null) return NoContent();

        product.PhotoObjectKey = null;
        product.PhotoContentType = null;
        product.PhotoSizeBytes = null;
        product.PhotoWidth = null;
        product.PhotoHeight = null;
        product.UpdatedAt = DateTimeOffset.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _storage.DeleteAsync(key, ct);
        return NoContent();
    }

    private static string Prefix(Guid licenseId, Guid productId)
        => $"{licenseId:N}/products/{productId:N}/";

    private static bool IsAllowed(string? contentType)
        => contentType is not null
           && AllowedContentTypes.Contains(contentType.Trim().ToLowerInvariant());

    private IActionResult UnsupportedType()
        => Problem(title: "unsupported-media-type",
            detail: "Yalnız JPEG, PNG ve WebP kabul ediliyor.", statusCode: 400);

    private Task<Product?> FindAsync(Guid id, Guid licenseId, CancellationToken ct)
        => _db.Products.FirstOrDefaultAsync(p => p.Id == id && p.LicenseId == licenseId, ct);

    private Task<Guid?> ResolveActiveLicenseAsync(CancellationToken ct)
    {
        var customerId = User.GetTenantCustomerId();
        var now = DateTimeOffset.UtcNow;
        return _db.Licenses
            .Where(l => l.CustomerId == customerId && l.RevokedAt == null && l.ExpiresAt > now)
            .OrderBy(l => l.IssuedAt)
            .Select(l => (Guid?)l.Id)
            .FirstOrDefaultAsync(ct);
    }
}
```

> Eski anahtarın silinmesi **veritabanı kaydedildikten sonra**: R2 silme
> başarısız olursa yetim bir nesne kalır (ucuz), ters sırada olsaydı kayıt
> R2'de olmayan bir anahtarı gösterirdi (kırık).

- [ ] **Step 4: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~PanelProductPhotoControllerTests"`

Beklenen: 12 test geçer.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductPhotoController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductPhotoControllerTests.cs
git commit -m "feat(katalog): urun fotografi uclari (R2 presigned)"
```

---

### Task 9: Stok elemanı rolü + varsayılan-kapalı yetki kapısı

Spec'in kuralı: *"stok elemanı için varsayılan olarak her uç kapalı"*. Bu yüzden
kapı **beyaz liste**: `stock` rolündeki operatörün token'ı, uç açıkça
`[AllowStockStaff]` ile işaretlenmemişse 403 alır. Yarın panele yeni bir uç
eklendiğinde stok elemanı onu **kendiliğinden görmez** — görmesi gerekiyorsa
birinin bilerek işaretlemesi gerekir.

Bugün operator token'ında `role` claim'i **yok** (`JwtTokenService:40-51`
doğrulandı); `OperatorUser.Role` sütunu var ama `"owner"`/`"staff"` değerleriyle
sadece görüntüleme amaçlı kullanılıyor. Bu görev claim'i ekliyor.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Auth/OperatorRoles.cs`
- Create: `OrderDeck.LicenseServer/Services/Auth/StockStaffScopeFilter.cs`
- Modify: `OrderDeck.LicenseServer/Services/Auth/TenantClaims.cs`
- Modify: `OrderDeck.LicenseServer/Services/Auth/JwtTokenService.cs:40-51`
- Modify: `OrderDeck.LicenseServer/Controllers/Auth/AuthController.cs:163`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelOperatorsController.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs:492`
- Modify: dört katalog controller'ı (`[AllowStockStaff]` işareti)
- Test: `OrderDeck.LicenseServer.Tests/Auth/StockStaffScopeTests.cs`

> **Claim adı neden `role` değil `oprole`:** `JwtSecurityTokenHandler`'ın
> gelen-claim eşlemesi `role`'ü `ClaimTypes.Role`'e çevirir ve
> `[Authorize(Roles = …)]` makinesine bağlar. Bizim istediğimiz o değil;
> ayrı bir ad hem eşlemeden kaçınır hem de ileride gerçek rol tabanlı
> yetkilendirme eklenirse çakışmaz.

- [ ] **Step 1: Kırmızı testleri yaz**

`OrderDeck.LicenseServer.Tests/Auth/StockStaffScopeTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Auth;

/// <summary>
/// Stok elemanı rolü (Faz 1a). Kural: <c>stock</c> rolü için her uç varsayılan
/// olarak kapalı; yalnız <c>[AllowStockStaff]</c> ile işaretli uçlar açık.
/// </summary>
public class StockStaffScopeTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public StockStaffScopeTests(ApiFactory f) => _factory = f;

    private sealed record OperatorDto(
        Guid Id, Guid LicenseId, string Email, string Name, string Role,
        DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt, DateTimeOffset? RevokedAt);

    private sealed record OperatorLoginResp(
        string Token, DateTimeOffset ExpiresAt, Guid OperatorId, Guid TenantCustomerId,
        string Email, string Name, string Role);

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        var json = await resp.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(json)) return null;
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    private async Task<HttpClient> SeedOwnerAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        db.Licenses.Add(new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-STOK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        });
        await db.SaveChangesAsync();
        return client;
    }

    private static Task<HttpResponseMessage> InviteAsync(
        HttpClient ownerClient, string? role)
        => ownerClient.PostAsJsonAsync("/api/panel/operators", new
        {
            email = $"op-{Guid.NewGuid():N}@example.com",
            name = "Depo",
            password = "pwd-" + Guid.NewGuid().ToString("N"),
            role,
        });

    /// <summary>Verilen rolde bir operatör davet edip onun adına giriş yapmış client döner.</summary>
    private async Task<HttpClient> OperatorClientAsync(HttpClient ownerClient, string role)
    {
        var email = $"op-{Guid.NewGuid():N}@example.com";
        var password = "pwd-" + Guid.NewGuid().ToString("N");

        var invite = await ownerClient.PostAsJsonAsync("/api/panel/operators",
            new { email, name = "Depo", password, role });
        invite.StatusCode.Should().Be(HttpStatusCode.Created);

        var anon = _factory.CreateClient();
        var login = await anon.PostAsJsonAsync("/api/v1/auth/operator-login", new { email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await login.Content.ReadFromJsonAsync<OperatorLoginResp>())!;
        body.Role.Should().Be(role);

        anon.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", body.Token);
        return anon;
    }

    [Fact]
    public async Task Invite_without_a_role_still_creates_a_staff_operator()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: null);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<OperatorDto>())!;
        dto.Role.Should().Be("staff");
    }

    [Fact]
    public async Task Invite_creates_a_stock_operator()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: "stock");

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = (await resp.Content.ReadFromJsonAsync<OperatorDto>())!;
        dto.Role.Should().Be("stock");
    }

    [Fact]
    public async Task Invite_400_on_an_unknown_role()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: "admin");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-role");
    }

    [Fact]
    public async Task Invite_400_when_someone_tries_to_mint_an_owner()
    {
        var owner = await SeedOwnerAsync();

        var resp = await InviteAsync(owner, role: "owner");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await TitleAsync(resp)).Should().Be("invalid-role");
    }

    [Fact]
    public async Task Stock_operator_can_reach_the_catalog()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var products = await stock.GetAsync("/api/panel/products");
        var categories = await stock.GetAsync("/api/panel/categories");

        products.StatusCode.Should().Be(HttpStatusCode.OK);
        categories.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Stock_operator_is_blocked_from_the_customer_list()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        var resp = await stock.GetAsync("/api/panel/customers");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await TitleAsync(resp)).Should().Be("stock-staff-forbidden");
    }

    [Fact]
    public async Task Stock_operator_is_blocked_from_orders()
    {
        var owner = await SeedOwnerAsync();
        var stock = await OperatorClientAsync(owner, "stock");

        // Sipariş listesi PanelOrdersController'da oturum altında duruyor
        // ("/api/panel/orders" diye bir uç yok). Kapı action'dan önce çalıştığı
        // için oturumun gerçekten var olması gerekmiyor.
        var resp = await stock.GetAsync($"/api/panel/sessions/{Guid.NewGuid()}/orders");

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Staff_operator_still_reaches_the_customer_list()
    {
        var owner = await SeedOwnerAsync();
        var staff = await OperatorClientAsync(owner, "staff");

        var resp = await staff.GetAsync("/api/panel/customers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Owner_token_is_unaffected_by_the_gate()
    {
        var owner = await SeedOwnerAsync();

        var resp = await owner.GetAsync("/api/panel/customers");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~StockStaffScopeTests"`

Beklenen: derleme geçer ama rol testleri düşer — `role` alanı yok sayıldığı için
`invalid-role` yerine 201, `stock` yerine `staff` döner ve 403 beklenen yerlerde
200 gelir.

- [ ] **Step 3: Rol sabitleri ve işaret niteliği**

`OrderDeck.LicenseServer/Services/Auth/OperatorRoles.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// <c>OperatorUser.Role</c> sütununun kabul ettiği değerler.
/// <c>owner</c> davetle verilemez — lisans sahibi zaten Customer token'ı taşır,
/// sütunda yalnız tarihsel kayıtlar için durur.
/// </summary>
public static class OperatorRoles
{
    public const string Owner = "owner";
    public const string Staff = "staff";
    public const string Stock = "stock";

    /// <summary>Davetle atanabilir roller.</summary>
    public static bool IsAssignable(string? role) => role is Staff or Stock;
}

/// <summary>
/// Bu uç <c>stock</c> rolündeki operatöre açık. İşaretlenmemiş her uç kapalıdır
/// (<see cref="StockStaffScopeFilter"/>). Controller ya da action seviyesinde
/// kullanılabilir.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false)]
public sealed class AllowStockStaffAttribute : Attribute;
```

`OrderDeck.LicenseServer/Services/Auth/StockStaffScopeFilter.cs`:

```csharp
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OrderDeck.LicenseServer.Services.Auth;

/// <summary>
/// Varsayılan-kapalı yetki kapısı: <c>stock</c> rolündeki operatör yalnız
/// <see cref="AllowStockStaffAttribute"/> ile işaretli uçlara girebilir.
/// Global filtre olarak kayıtlı (<c>Program.cs</c>), böylece yarın eklenen bir
/// uç kendiliğinden açık gelmez.
/// </summary>
public sealed class StockStaffScopeFilter : IAsyncActionFilter
{
    public Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        if (context.HttpContext.User.GetOperatorRole() != OperatorRoles.Stock)
            return next();

        var allowed = context.ActionDescriptor.EndpointMetadata
            .Any(m => m is AllowStockStaffAttribute);

        if (allowed) return next();

        context.Result = new ObjectResult(new ProblemDetails
        {
            Title = "stock-staff-forbidden",
            Detail = "Stok elemanı bu bölüme erişemez.",
            Status = StatusCodes.Status403Forbidden,
        })
        {
            StatusCode = StatusCodes.Status403Forbidden,
        };
        return Task.CompletedTask;
    }
}
```

- [ ] **Step 4: Claim'i token'a ekle**

`TenantClaims.cs` — `OperatorId` sabitinin altına:

```csharp
    /// <summary>oprole: operatörün rolü ("staff" | "stock"). Customer token'larında yok.
    /// Ad bilerek "role" değil — JWT handler "role"ü ClaimTypes.Role'e eşliyor.</summary>
    public const string OperatorRole = "oprole";
```

ve `IsOperator`'ın altına:

```csharp
    /// <summary>
    /// Operatörün rolü. Customer token'larında ve rolsüz eski operator
    /// token'larında null döner — kapı yalnız "stock" değerine tepki verdiği
    /// için eski token'lar davranış değiştirmez.
    /// </summary>
    public static string? GetOperatorRole(this ClaimsPrincipal principal)
        => principal.FindFirst(OperatorRole)?.Value;
```

`JwtTokenService.cs:40-51` — imzaya rol ekle ve claim'i yaz:

```csharp
    public (string Token, DateTimeOffset ExpiresAt) IssueOperatorToken(
        Guid operatorId, Guid tenantCustomerId, string email, string role)
    {
        var lifetimeMinutes = _options.AccessTokenLifetimeMinutes > 0
            ? _options.AccessTokenLifetimeMinutes
            : 15;
        var expiresAt = DateTimeOffset.UtcNow.AddMinutes(lifetimeMinutes);
        var token = Build(JwtOptions.CustomerAudience, expiresAt,
            new Claim(TenantClaims.Sub, operatorId.ToString()),
            new Claim(TenantClaims.TenantCustomerId, tenantCustomerId.ToString()),
            new Claim(TenantClaims.PrincipalType, "operator"),
            new Claim(TenantClaims.OperatorId, operatorId.ToString()),
            new Claim(TenantClaims.OperatorRole, role),
            new Claim("email", email));
        return (token, expiresAt);
    }
```

`AuthController.cs:163` — tek çağıran, rolü geçir:

```csharp
        var (token, expiresAt) = _jwt.IssueOperatorToken(
            op.Id, op.License.CustomerId, op.Email, op.Role);
```

- [ ] **Step 5: Daveti role duyarlı yap**

`PanelOperatorsController.cs` — `InviteRequest`'e `Role` ekle:

```csharp
    public sealed record InviteRequest(string Email, string Name, string Password, string? Role);
```

`weak-password` kontrolünün hemen altına doğrulama:

```csharp
        var role = string.IsNullOrWhiteSpace(req.Role)
            ? OperatorRoles.Staff
            : req.Role.Trim().ToLowerInvariant();

        if (!OperatorRoles.IsAssignable(role))
            return Problem(title: "invalid-role",
                detail: "Rol yalnız 'staff' ya da 'stock' olabilir.", statusCode: 400);
```

ve `new OperatorUser { … }` içindeki sabit satırı değiştir:

```csharp
            Role = role,
```

- [ ] **Step 6: Filtreyi kaydet ve katalog uçlarını işaretle**

`Program.cs:492`:

```csharp
        builder.Services.AddControllers(opt =>
            opt.Filters.Add<OrderDeck.LicenseServer.Services.Auth.StockStaffScopeFilter>());
```

Dört katalog controller'ının sınıf niteliklerine `[AllowStockStaff]` ekle
(`[Authorize(...)]` satırının hemen altına):

- `PanelCategoriesController`
- `PanelProductsController`
- `PanelProductVariantsController`
- `PanelProductPhotoController`

Örnek:

```csharp
[ApiController]
[Route("api/panel/categories")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
[AllowStockStaff]
public sealed class PanelCategoriesController : ControllerBase
```

- [ ] **Step 7: Yeşili doğrula**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~StockStaffScopeTests"`

Beklenen: 9 test geçer.

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~OperatorLoginTests"`

Beklenen: geçer — mevcut testler `role` alanı göndermiyor, varsayılan `staff`
davranışı korunuyor.

- [ ] **Step 8: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Auth/OperatorRoles.cs OrderDeck.LicenseServer/Services/Auth/StockStaffScopeFilter.cs OrderDeck.LicenseServer/Services/Auth/TenantClaims.cs OrderDeck.LicenseServer/Services/Auth/JwtTokenService.cs OrderDeck.LicenseServer/Controllers/Auth/AuthController.cs OrderDeck.LicenseServer/Controllers/Panel/PanelOperatorsController.cs OrderDeck.LicenseServer/Controllers/Panel/PanelCategoriesController.cs OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs OrderDeck.LicenseServer/Controllers/Panel/PanelProductPhotoController.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Auth/StockStaffScopeTests.cs
git commit -m "feat(katalog): stok elemani rolu ve varsayilan-kapali yetki kapisi"
```

---

## Doğrulama (planın tamamı bitince)

1. **Tam test takımı:**

   Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`

   Beklenen: mevcut ~747 test + bu planın ~110 yeni testi, hepsi yeşil.
   Özellikle `PanelControllerConventionTests` ve `OperatorLoginTests` kırmızıya
   dönmemeli.

2. **WPF tarafı hiç etkilenmedi:**

   Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`

   Beklenen: 944 test yeşil — bu plan `OrderDeck.App`/`OrderDeck.Chat`'e tek satır
   dokunmuyor.

3. **Ham SQL sayacı hâlâ sıfır:**

   Run: `grep -rn "migrationBuilder.Sql" OrderDeck.LicenseServer/Data/Migrations/ | wc -l`

   Beklenen: `0`. PostgreSQL göçünü ucuz tutan iki kuraldan biri; katalog
   migration'ı bunu bozmamalı.

4. **Sorguda `ToUpper()` yok:**

   Run: `grep -rn "ToUpper()" OrderDeck.LicenseServer/Controllers/Panel/`

   Beklenen: yalnız `ToUpperInvariant()` çağrıları (yazma anında normalize),
   `IQueryable` içinde `ToUpper()` yok.

5. **Migration üretimi tekrarlanabilir:**

   Run: `dotnet ef migrations has-pending-model-changes --project OrderDeck.LicenseServer --startup-project OrderDeck.LicenseServer`

   Beklenen: "No changes have been made to the model since the last migration."

## Yayın

Tek PR, dal `feat/stok-faz1a-sunucu-katalog`.

PR başlığı: `feat(stok): Faz 1a sunucu — katalog, varyant, fotoğraf, stok rolü`

PR gövdesinde bulunsun:
- Bu planın kapsam dışı listesi (1b/1c'ye ne kaldı)
- Yeni uçların tablosu (panel planını yazacak kişinin sözleşmesi bu)
- Hata `title` sözlüğü (panel bu slug'lara göre Türkçe mesaj gösterecek)
- **`OperatorUser.Role`'e `stock` değerinin eklendiği notu** — mevcut prod
  kayıtları `owner`/`staff`, göç gerekmiyor, sütun zaten `string(16)`.

Commit'siz duran `.gitignore`, `.claude/launch.json` ve ilgisiz `docs/`
dosyaları bu PR'a karıştırılmayacak.
