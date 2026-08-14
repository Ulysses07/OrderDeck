# Yayın Kodu Modeli — Sunucu + Panel Uygulama Planı (1/3)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Tek "ürün kodu" kavramını ikiye ayır — sistem üretimi **stok kodu** (`SK00001`, ürün seviyesi, değişmez) ve operatörün verdiği **yayın kodu** (`ATEŞ`, ürün + satıcı-ekseni-değeri seviyesi, lisans başına kalıcı benzersiz) — ve varyant seviyesindeki kod kavramını (`VariantCode`, `Axis1Code`, `Axis2Code`) tamamen kaldır.

**Architecture:** Yayın kodu ayrı bir tabloda (`ProductBroadcastCode`) yaşar, çünkü kardinalitesi varyantla aynı değil: `ATEŞ` = ürün + satıcı ekseni değeri = N varyant satırı (Siyah·S, Siyah·M, Siyah·L). Kod hiçbir zaman serbest bırakılmaz — satırlar **ekleme-esaslı** (append-only), güncelleme yeni satır yazar, eski satır kodu rezerve tutmaya devam eder. Benzersizlik ve eşleştirme, koddan türetilen `CodeNormalized` (`SearchNormalizer`) üstünde çalışır; ham metin collation'a bağımlı olurdu. `VariantCode`'un boşalttığı benzersizlik görevini, varyant eksen değerlerinden türetilen normalize kolonlar (`Axis1ValueNorm`, `Axis2ValueNorm`) devralır.

**Tech Stack:** ASP.NET Core 10 + EF Core 10 (SQL Server prod, InMemory test), xunit + FluentAssertions, React 18.3 + TypeScript + TanStack Query v5 + Vitest 4 (panel).

**Kapsam (spec'ten harfiyen):** `ProductBroadcastCode` tablosu ve ucu, `Product.Code` → `SK00001` (sistem üretir), `VariantCode`/`Axis*Code`/`VariantCodeBuilder` temizliği, varyant benzersizliği, katalog çekme ucuna yayın kodları, panel ekranları. **WPF'e dokunulmaz.**

---

## Spec düzeltmeleri (uygulamadan önce oku)

Onaylı spec (`docs/superpowers/specs/2026-08-14-yayin-kodu-ve-yorum-eslestirme-design.md`) dört noktada gerçeğe uymuyor. Bu plan düzeltilmiş hâlini uygular.

### 1. `ProductVariant.SortOrder` diye bir kolon yok

Spec varyant sıralamasından bahsederken `SortOrder`'a atıf yapıyor. Sunucuda böyle bir kolon **yok**; `SortOrder` yalnız WPF replikasında (`OrderDeck.Core/Storage/Migrations/025_catalog_replica.sql`) var ve **dizi indeksinden** üretiliyor. Sunucu tarafında sıralama sorgu ifadesidir. Bu plan sıralamayı `OrderBy(Axis1ValueNorm).ThenBy(Axis2ValueNorm)` yapar.

**Ucuz vs doğru:** Ucuzu, sıralamayı `OrderBy(v => v.Id)`'ye çevirmekti — bir satır. Doğrusu normalize eksen değerleri, çünkü `025_catalog_replica.sql`'de yazılı olan "sunucu SQL Server collation'ında sıralıyor, SQLite ordinal'i farklı düşer, o yüzden yerelde yeniden sıralama" uyarısı ancak sıralama anahtarı **provider'dan bağımsız ASCII** olduğunda emekli olur. Geri dönüş bedeli: bugün ucuzu seçersek, Postgres göçünde varyant sırası sessizce değişir ve WPF kartındaki kırılım listesi karışır.

### 2. Panel deposu `OrderDeck-Shopper` değil, `OrderDeck-Mobile`

Panel `C:\Users\burak\source\repos\OrderDeck-Mobile\apps\panel`. Ana dal **`main`** (`master` değil). Panel görevleri (12–16) bu depoda çalışır.

### 3. Benzersizlik ham değer üstünde değil, türetilmiş normalize kolonlar üstünde

Spec `UNIQUE (ProductId, Axis1Value, Axis2Value)` diyor. Bu plan bunun yerine iki türetilmiş, **NULL kabul etmeyen** kolon ekler (`Axis1ValueNorm`, `Axis2ValueNorm`; eksen yoksa boş dize) ve indeksi onların üstüne kurar.

**Ucuz vs doğru:** Ucuzu spec'in harfi — ham kolonlara indeks. İki yerde sessizce bozulur:
- **Collation.** SQL Server varsayılanı harf duyarsız, PostgreSQL duyarlı. Ham indeks bugün "Siyah"/"siyah"ı çakıştırır, göç günü çakıştırmaz. Bu tam olarak `Product.NameSearch`'ün var olma sebebi.
- **NULL.** Her iki motorda da UNIQUE indekste NULL'lar **birbirinden farklı** sayılır; tek eksenli üründe `Axis2Value` NULL olduğu için indeks hiç ısırmaz.

Boş dizeye normalleştirmek ikisini de kapatır. Yan fayda: türetilmiş kodların ürettiği yapay `variant-code-collision` 409'u ("Kırmızı" ve "Kırmızılı" ikisi de `KIRM`) ortadan kalkar; geriye tek gerçek çakışma kalır (`duplicate-variant`).

### 4. WPF sözleşmesi kırılmasın: `VariantCode` telde kalır (geçici)

`OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs` içinde `CatalogVariantPullItem.VariantCode` **nullable değil**, ve WPF replikasında `VariantCode TEXT NOT NULL`. Sunucu kolonu bu planda siliniyor ama **tel modeli aynı kalıyor**: `VariantCode` alanı ürünün `Code`'uyla (yani `SK00001`) doldurulur, `Axis1Code`/`Axis2Code` alanları düşer (iki tarafta da nullable, JSON'da yokluk sorunsuz).

Neden zararsız: WPF `VariantCode`'u yalnız iki eksen değeri de boşken gösteriyor (`CatalogVariantViewModel`: `Display = label.Length > 0 ? label : VariantCode`), ve o satırlar zaten `BuildAutoVariant` ile `VariantCode = product.Code` olarak yazılıyordu. Davranış birebir aynı. Alan **plan 2/3'te** WPF replikasıyla birlikte kaldırılacak.

---

## File Structure

### LiveDeck (dal: `feat/yayin-kodu-sunucu`, `origin/master`'dan)

**Yeni:**
- `OrderDeck.LicenseServer/Domain/ProductBroadcastCode.cs` — yayın kodu varlığı. Tek sorumluluk: ürün + satıcı ekseni değeri → kod eşlemesinin kalıcı kaydı.
- `OrderDeck.LicenseServer/Services/Catalog/StockCodeSequence.cs` — `SK00001` üreteci. `CatalogCodeSequence`'ın yerine geçer.
- `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastCodesController.cs` — yayın kodu okuma/yazma ucu. Ayrı controller, çünkü `PanelProductsController` zaten 757 satır ve bu apayrı bir kaynak.
- `OrderDeck.LicenseServer.Tests/Services/StockCodeSequenceTests.cs`
- `OrderDeck.LicenseServer.Tests/Panel/PanelBroadcastCodesTests.cs`
- İki EF göçü: `AddProductBroadcastCode` (eklemeli), `DropVariantCodes` (yıkıcı).

**Değişecek:**
- `OrderDeck.LicenseServer/Domain/CatalogLimits.cs` — `BroadcastCode = 32` gelir; `VariantCode`, `AxisCode` gider.
- `OrderDeck.LicenseServer/Domain/Product.cs` — `Code` XML doc'u; `BroadcastCodes` gezinme özelliği.
- `OrderDeck.LicenseServer/Domain/ProductVariant.cs` — `VariantCode`/`Axis1Code`/`Axis2Code` gider, `Axis1ValueNorm`/`Axis2ValueNorm` gelir.
- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — `DbSet<ProductBroadcastCode>`, eşleme bloğu, `SyncDerivedColumns` üç yeni türetme.
- `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs` — `Code` istemciden alınmaz, `next-code` ucu ve `SyncVariantCodes` silinir, sıralama değişir.
- `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs` — eksen kodu alanları ve `VariantCodeTakenAsync` gider, `VariantValuesTakenAsync` gelir; eksen adı değişince yayın kodu taşınır.
- `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs` — yayın kodları eklenir, uyum kalkanı kurulur.
- `OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`, `.../Panel/PanelProductsTests.cs`, `.../Panel/PanelProductVariantsTests.cs`, `.../Licenses/LicensesWpfCatalogPullTests.cs`

**Silinecek:**
- `OrderDeck.LicenseServer/Services/Catalog/VariantCodeBuilder.cs`
- `OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs`
- `OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs`
- `OrderDeck.LicenseServer.Tests/Services/VariantCodeBuilderTests.cs`
- `OrderDeck.LicenseServer.Tests/Services/AxisCodeDeriverTests.cs`
- `OrderDeck.LicenseServer.Tests/Services/CatalogCodeSequenceTests.cs`

**Dokunulmayacak:** `OrderDeck.App/**` (WPF), `OrderDeck.Core/Storage/Migrations/**`, `OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs`.

### OrderDeck-Mobile (dal: `feat/yayin-kodu-panel`, **çekilmiş** `origin/main`'den)

**Değişecek:**
- `apps/panel/src/api/catalog.ts` — `Variant` tipinden kod alanları çıkar, `BroadcastCode` tipi ve kancaları girer, `useNextProductCode` silinir.
- `apps/panel/src/screens/UrunScreen.tsx` — kod girdisi salt-okunur rozete döner.
- `apps/panel/src/components/catalog/VariantSection.tsx` — eksen kodu girdileri ve `variantCode` rozetleri gider.
- `apps/panel/src/lib/combinations.ts` — `deriveAxisCode`/`codeMissing`/`TR_MAP` gider.
- `apps/panel/src/screens/StokUrunScreen.tsx` — satıcı ekseni değeri başına yayın kodu kutuları gelir.
- Kardeş testler: `UrunScreen.test.tsx`, `VariantSection.test.tsx`, `combinations.test.ts`, `StokUrunScreen.test.tsx`.

---

## Görev 0: Dalı aç

**Files:** yok (yalnız git)

- [ ] **Adım 1: LiveDeck dalını `origin/master`'dan aç**

```bash
cd /c/Users/burak/source/repos/LiveDeck
git fetch origin
git checkout -b feat/yayin-kodu-sunucu origin/master
git status --short
```

Beklenen: yeni dal, `git status` yalnız zaten kirli olan dosyaları listeliyor
(`.claude/launch.json`, `.gitignore`, `.codex/`, `AGENTS.md`, `mutate.py`,
`OrderDeck.App/OrderDeck.App_hy24kodh_wpftmp.csproj`, iki `docs/` dosyası).
**Bu dosyalar hiçbir commit'e girmeyecek — her commit'te dosyaları tek tek adıyla stage'le, asla `git add -A` / `git add .` kullanma.**

---

## Görev 1: `ProductBroadcastCode` varlığı, eşlemesi ve türetilmiş kolonu

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/ProductBroadcastCode.cs`
- Modify: `OrderDeck.LicenseServer/Domain/CatalogLimits.cs`
- Modify: `OrderDeck.LicenseServer/Domain/Product.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Test: `OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

`CatalogModelTests.cs` içindeki `Column_lengths_come_from_CatalogLimits` testinin
`expected` dizisine, son satırdan (`ProductVariant.Barcode`) sonra üç satır ekle:

```csharp
            (typeof(ProductVariant), nameof(ProductVariant.Barcode), CatalogLimits.Barcode),
            (typeof(ProductBroadcastCode), nameof(ProductBroadcastCode.SellerAxisValue), CatalogLimits.AxisValue),
            (typeof(ProductBroadcastCode), nameof(ProductBroadcastCode.Code), CatalogLimits.BroadcastCode),
            (typeof(ProductBroadcastCode), nameof(ProductBroadcastCode.CodeNormalized), CatalogLimits.BroadcastCode),
        };
```

Aynı dosyanın sonuna (son `}`'den önce) yeni testi ekle:

```csharp
    /// <summary>
    /// Bekçi: <c>CodeNormalized</c> türetilmiş bir kolon ve türetme controller'da
    /// DEĞİL, <c>SaveChanges</c> zincirinde yapılıyor — <c>NameSearch</c> ile aynı
    /// gerekçe. Yayın kodunun benzersizliği ve canlı yorum eşleştirmesi bu kolona
    /// dayandığı için, kuralı atlayan bir yazma yolu eklenirse kod sessizce
    /// eşleşmez hâle gelirdi.
    /// </summary>
    [Fact]
    public async Task Broadcast_code_normalized_is_derived_on_insert_and_refreshed_on_update()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = NewLicense();
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "SK00001",
            Name = "Yayın Kodlu",
            DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);

        var broadcast = new ProductBroadcastCode
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            SellerAxisValue = "Siyah",
            Code = "  ateş  ",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.ProductBroadcastCodes.Add(broadcast);
        await db.SaveChangesAsync();

        broadcast.CodeNormalized.Should().Be("ATES");

        broadcast.Code = "Kırmızı Ateş";
        await db.SaveChangesAsync();

        broadcast.CodeNormalized.Should().Be("KIRMIZI ATES",
            "kod değişince türetilmiş kolon bayat kalırsa yayın kodu canlı "
            + "yorumla eşleşmez");
    }
```

- [ ] **Adım 2: Testi çalıştır, derlemenin kırıldığını gör**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~CatalogModelTests
```

Beklenen: FAIL — derleme hatası `CS0246: The type or namespace name 'ProductBroadcastCode' could not be found` ve `CS0117: 'CatalogLimits' does not contain a definition for 'BroadcastCode'`.

- [ ] **Adım 3: `CatalogLimits`'e sınırı ekle**

`OrderDeck.LicenseServer/Domain/CatalogLimits.cs` içinde `VariantCode` sabitinin hemen üstüne:

```csharp
    /// <summary>
    /// Yayın kodu (<c>ProductBroadcastCode.Code</c>) ve normalize hâli.
    /// 32 karakter: operatör bunu canlı yayında sesli söylüyor ve izleyici
    /// yoruma yazıyor — pratikte 3-8 karakter. Tavan cömert bırakıldı ki
    /// "ATEŞ KIRMIZI" gibi iki kelimelik kodlar da sığsın.
    ///
    /// Normalize hâlin sınırı ham hâlle AYNI olmalı: <c>SearchNormalizer</c>
    /// karakter atmıyor (yalnız büyütüp katlıyor, boşlukları sadeleştiriyor),
    /// yani normalize hâl asla ham hâlden uzun olamaz.
    /// </summary>
    public const int BroadcastCode = 32;
```

- [ ] **Adım 4: Varlığı yaz**

`OrderDeck.LicenseServer/Domain/ProductBroadcastCode.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir ürünün canlı yayında duyurulan kodu. Ürün kodundan (<c>SK00001</c>)
/// ayrı bir kavram: stok kodu sistemin, yayın kodu operatörün.
///
/// <para><b>Neden ayrı tablo:</b> kardinalitesi varyantla aynı değil. "ATEŞ"
/// = ürün + satıcı ekseni değeri; altında N varyant satırı durur (Siyah·S,
/// Siyah·M, Siyah·L). <c>ProductVariant</c> üstünde bir kolon olsaydı aynı kod
/// N satıra kopyalanır ve benzersizlik kurulamazdı.</para>
///
/// <para><b>Neden satırlar silinmiyor:</b> bir kod bir daha ASLA
/// kullanılamaz — ürün arşivlense, kod değiştirilse bile eski satır durur ve
/// kodu rezerve tutar. Sebebi canlı yayının kendisi: izleyici eski bir yayın
/// videosundaki kodu bugün yoruma yazabilir; kod başka bir ürüne devredilmiş
/// olsaydı sipariş yanlış ürüne düşerdi. Kod değişikliği bu yüzden
/// <b>güncelleme değil, yeni satır</b>; "güncel" olan en yeni
/// <see cref="CreatedAt"/>.</para>
/// </summary>
public sealed class ProductBroadcastCode
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>
    /// Kiracı ayracı. <b>Bilerek yalnız skaler</b> — <c>License</c> gezinme
    /// özelliği ve ilişkisi YOK. İlişki kurulsaydı SQL Server iki cascade yolu
    /// görürdü (License→Product→BroadcastCode ve License→BroadcastCode) ve
    /// göç "multiple cascade paths" hatasıyla düşerdi.
    /// <c>ProductVariant</c> ve <c>ProductPhoto</c> aynı kalıpta.
    /// </summary>
    public Guid LicenseId { get; set; }

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>
    /// Kodun bağlandığı satıcı ekseni değeri (örn. "Siyah"). Ürünün satıcı
    /// ekseni yoksa <c>null</c> — kod o zaman ürünün tamamını gösterir.
    /// Ham hâli saklanır; panelde bu metin gösterilecek.
    /// </summary>
    public string? SellerAxisValue { get; set; }

    /// <summary>Operatörün yazdığı kod, kırpılmış ham hâli (görüntü için).</summary>
    public string Code { get; set; } = string.Empty;

    /// <summary>
    /// <c>SearchNormalizer.Normalize(Code)</c>. Türetilmiş — elle YAZILMAZ,
    /// <c>LicenseDbContext.SyncDerivedColumns</c> dolduruyor. Benzersizlik
    /// indeksi ve canlı yorum eşleştirmesi bunun üstünde çalışır.
    /// </summary>
    public string CodeNormalized { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }
}
```

- [ ] **Adım 5: `Product`'a gezinme özelliğini ekle ve `Code` doc'unu düzelt**

`OrderDeck.LicenseServer/Domain/Product.cs` — `Code` özelliğinin XML doc'unu şununla değiştir:

```csharp
    /// <summary>
    /// Stok kodu. Lisans başına benzersiz, <b>sistem üretir</b>
    /// (<c>SK00001</c>, <c>SK00002</c>…) ve bir daha değişmez — elle
    /// düzenlenemez. Yayında söylenen kod bu DEĞİL; o
    /// <see cref="ProductBroadcastCode"/>.
    /// </summary>
```

`Variants` listesinin hemen altına:

```csharp
    /// <summary>
    /// Bu ürüne verilmiş yayın kodları — emeklileri dahil (satır silinmiyor).
    /// "Güncel" kod, satıcı ekseni değeri başına en yeni <c>CreatedAt</c>.
    /// </summary>
    public List<ProductBroadcastCode> BroadcastCodes { get; set; } = new();
```

- [ ] **Adım 6: DbSet, eşleme ve türetmeyi ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — `DbSet<ProductVariant> ProductVariants` satırının altına:

```csharp
    public DbSet<ProductBroadcastCode> ProductBroadcastCodes => Set<ProductBroadcastCode>();
```

`OnModelCreating` içinde `mb.Entity<ProductVariant>(…)` bloğunun hemen ardına:

```csharp
        mb.Entity<ProductBroadcastCode>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.SellerAxisValue).HasMaxLength(CatalogLimits.AxisValue);
            b.Property(x => x.Code).HasMaxLength(CatalogLimits.BroadcastCode).IsRequired();
            b.Property(x => x.CodeNormalized).HasMaxLength(CatalogLimits.BroadcastCode).IsRequired();

            b.HasOne(x => x.Product).WithMany(p => p.BroadcastCodes)
                .HasForeignKey(x => x.ProductId).OnDelete(DeleteBehavior.Cascade);

            // Lisans başına KALICI benzersizlik: kod emekliye ayrılsa da satır
            // durduğu için indeks kodu rezerve tutmaya devam eder.
            b.HasIndex(x => new { x.LicenseId, x.CodeNormalized }).IsUnique();

            // "Bu ürünün kodları, en yenisi önce" — Görev 8'in GET'i ve Görev
            // 10'un çekme sorgusu bu sırayı istiyor. Baştaki kolonun ProductId
            // olması ayrıca FK'nin kendi indeksi işini görür: ürün silinince
            // cascade DELETE seek yapar. Kardeşler de aynı kalıpta
            // (ProductVariant: (ProductId, …), ProductPhoto: (ProductId, SortOrder)).
            // LicenseId kiracı filtresi olarak sorguda kalıntı predicate kalır.
            b.HasIndex(x => new { x.ProductId, x.CreatedAt });
        });
```

`SyncDerivedColumns()` metodunun sonuna, `Product` döngüsünün ardına:

```csharp
        foreach (var entry in ChangeTracker.Entries<ProductBroadcastCode>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            entry.Entity.CodeNormalized = SearchNormalizer.Normalize(entry.Entity.Code);
        }
```

- [ ] **Adım 7: Testleri çalıştır**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~CatalogModelTests
```

Beklenen: PASS (bu sınıftaki tüm testler yeşil).

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/ProductBroadcastCode.cs \
        OrderDeck.LicenseServer/Domain/CatalogLimits.cs \
        OrderDeck.LicenseServer/Domain/Product.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): yayın kodu varlığı ve türetilmiş normalize kolonu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 2: Eklemeli göç — `AddProductBroadcastCode`

**Files:**
- Create: `OrderDeck.LicenseServer/Migrations/*_AddProductBroadcastCode.cs` (araç üretir)

- [ ] **Adım 1: Göçü üret**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet ef migrations add AddProductBroadcastCode \
  --project OrderDeck.LicenseServer \
  --startup-project OrderDeck.LicenseServer \
  --context LicenseDbContext
```

Beklenen: `Done. To undo this action, use 'ef migrations remove'`.

- [ ] **Adım 2: Üretilen göçü oku ve doğrula**

Üretilen `Up(...)` şunları içermeli, **başka hiçbir şeyi değil**:
- `CreateTable("ProductBroadcastCodes", …)` — kolonlar `Id`, `LicenseId`, `ProductId`, `SellerAxisValue` (`nvarchar(60)`, nullable), `Code` (`nvarchar(32)`, not null), `CodeNormalized` (`nvarchar(32)`, not null), `CreatedAt`
- `ProductId` üstünde `onDelete: ReferentialAction.Cascade` FK
- `CreateIndex("IX_ProductBroadcastCodes_LicenseId_CodeNormalized", unique: true)`
- `CreateIndex("IX_ProductBroadcastCodes_ProductId_CreatedAt")`

Eğer başka bir tabloya `AlterColumn`/`DropColumn` sızmışsa, dal `origin/master` ile senkron değil demektir: `dotnet ef migrations remove` ile geri al, `git log --oneline -1 OrderDeck.LicenseServer/Migrations` ile son göçü kontrol et.

- [ ] **Adım 3: Derle**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Beklenen: `Build succeeded`, 0 hata.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.LicenseServer/Migrations
git commit -m "$(cat <<'EOF'
feat(katalog): ProductBroadcastCodes tablosu göçü

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 3: `StockCodeSequence`

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Catalog/StockCodeSequence.cs`
- Create: `OrderDeck.LicenseServer.Tests/Services/StockCodeSequenceTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Services/StockCodeSequenceTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public class StockCodeSequenceTests
{
    [Fact]
    public void First_code_is_SK00001()
    {
        StockCodeSequence.Next(Array.Empty<string>()).Should().Be("SK00001");
    }

    [Fact]
    public void Next_takes_the_highest_number_plus_one()
    {
        StockCodeSequence.Next(new[] { "SK00001", "SK00002" }).Should().Be("SK00003");
    }

    /// <summary>
    /// Boşluk DOLDURULMAZ. Silinen ürünün kodunu yeniden vermek, o kodla
    /// basılmış etiketi ve geçmiş stok hareketini başka bir ürüne bağlardı.
    /// </summary>
    [Fact]
    public void Gaps_are_never_reused()
    {
        StockCodeSequence.Next(new[] { "SK00001", "SK00005" }).Should().Be("SK00006");
    }

    [Fact]
    public void Unparseable_codes_are_ignored()
    {
        StockCodeSequence.Next(new[] { "A1", "", "   ", "SKABCDE", "SK1" })
            .Should().Be("SK00001");
    }

    [Fact]
    public void Casing_and_padding_do_not_matter()
    {
        StockCodeSequence.Next(new[] { " sk00009 " }).Should().Be("SK00010");
    }

    /// <summary>
    /// Tavan aşılırsa kırılmadan büyür: 99999'dan sonra SK100000. Sayaç
    /// dolunca istisna atmak, lisansın ürün eklemesini tamamen durdururdu.
    /// </summary>
    [Fact]
    public void Overflows_into_six_digits_instead_of_throwing()
    {
        StockCodeSequence.Next(new[] { "SK99999" }).Should().Be("SK100000");
    }
}
```

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockCodeSequenceTests
```

Beklenen: FAIL — `CS0246: The type or namespace name 'StockCodeSequence' could not be found`.

- [ ] **Adım 3: Üreteci yaz**

`OrderDeck.LicenseServer/Services/Catalog/StockCodeSequence.cs`:

```csharp
using System.Globalization;
using System.Text.RegularExpressions;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Stok kodu üreteci: <c>SK00001</c>, <c>SK00002</c>…
///
/// <para>Kod <b>sistemin</b>; operatör göremez, değiştiremez. Yayında söylenen
/// kod ayrı (<c>ProductBroadcastCode</c>). Bu ayrım olmadan tek kod iki işi
/// birden yapmak zorundaydı: hem kalıcı stok kimliği hem operatörün yayında
/// beğendiği kısa ad — ve ikisi çeliştiğinde etiket ile yayın ayrışıyordu.</para>
///
/// <para>Sayaç <b>en büyük + 1</b>; boşluk doldurulmaz.</para>
/// </summary>
public static class StockCodeSequence
{
    public const string Prefix = "SK";
    public const int Digits = 5;

    // 5+ hane: SK99999'dan sonrası altı haneye taşar ve o kodlar da okunabilmeli.
    private static readonly Regex Pattern =
        new(@"^SK([0-9]{5,})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Verilen kod kümesindeki en büyük numaranın bir fazlası. Kalıba uymayan
    /// kayıtlar (eski elle yazılmış <c>A1</c> gibi) sessizce atlanır — bu
    /// bilinçli: göç öncesinden kalan bir kod üretimi kilitlememeli.
    /// </summary>
    public static string Next(IEnumerable<string?> existing)
    {
        long max = 0;

        foreach (var raw in existing)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            var match = Pattern.Match(raw.Trim().ToUpperInvariant());
            if (!match.Success) continue;

            if (!long.TryParse(match.Groups[1].Value, NumberStyles.None,
                    CultureInfo.InvariantCulture, out var number)) continue;

            if (number > max) max = number;
        }

        return Format(max + 1);
    }

    /// <summary>
    /// Numarayı koda çevirir. <c>PadLeft</c> kullanılıyor, sabit genişlikli
    /// biçim değil: 99999 aşıldığında kesmek yerine altı haneye taşsın.
    /// </summary>
    public static string Format(long number) =>
        Prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(Digits, '0');
}
```

- [ ] **Adım 4: Testi çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockCodeSequenceTests
```

Beklenen: PASS, 6 test.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Catalog/StockCodeSequence.cs \
        OrderDeck.LicenseServer.Tests/Services/StockCodeSequenceTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): SK00001 stok kodu üreteci

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 4: `Product.Code` artık istemciden gelmez

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`
- Delete: `OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs`
- Delete: `OrderDeck.LicenseServer.Tests/Services/CatalogCodeSequenceTests.cs`
- Test: `OrderDeck.LicenseServer.Tests/Panel/PanelProductsTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`PanelProductsTests.cs` içine yeni test ekle. Dosyadaki mevcut testlerin kimlik
doğrulama/istemci kurulum kalıbını (ör. `await _factory.PanelClientAsync(...)`
benzeri yardımcı ne ise) **aynen** kullan; aşağıdaki gövde yalnız iddiaları
gösteriyor, `client` değişkeni komşu testlerdeki gibi kurulmalı:

```csharp
    /// <summary>
    /// Kod artık istemciden gelmiyor: gövdede kod alanı YOK, sunucu üretiyor.
    /// İkinci ürün sıradaki numarayı almalı — aynı kodu alsalardı yayın
    /// eşleştirmesi iki ürün arasında salınırdı.
    /// </summary>
    [Fact]
    public async Task Created_products_get_sequential_system_codes()
    {
        var client = await NewPanelClientAsync();

        var first = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Birinci", defaultPrice = 100m,
        });
        first.StatusCode.Should().Be(HttpStatusCode.Created);
        var firstDto = await first.Content.ReadFromJsonAsync<JsonElement>();

        var second = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "İkinci", defaultPrice = 100m,
        });
        second.StatusCode.Should().Be(HttpStatusCode.Created);
        var secondDto = await second.Content.ReadFromJsonAsync<JsonElement>();

        var firstCode = firstDto.GetProperty("code").GetString()!;
        var secondCode = secondDto.GetProperty("code").GetString()!;

        firstCode.Should().MatchRegex("^SK[0-9]{5,}$");
        secondCode.Should().MatchRegex("^SK[0-9]{5,}$");
        secondCode.Should().NotBe(firstCode);
    }

    /// <summary>
    /// Kod ucu emekli: panel artık "sıradaki kod"u sormuyor, kaydederken
    /// öğreniyor.
    /// </summary>
    [Fact]
    public async Task Next_code_endpoint_is_gone()
    {
        var client = await NewPanelClientAsync();

        var res = await client.GetAsync("/api/panel/products/next-code");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

> **Not:** `NewPanelClientAsync` yer tutucu değil — dosyada halihazırda bulunan
> panel istemcisi kurulum yardımcısının adıdır. Dosyayı açtığında komşu testin
> ilk satırındaki gerçek adı kullan ve bu iki testte de aynısını çağır.

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelProductsTests
```

Beklenen: FAIL — `Created_products_get_sequential_system_codes` üretilen kodun
`A1` olduğunu (regex tutmuyor), `Next_code_endpoint_is_gone` ise 200 döndüğünü
gösterir.

- [ ] **Adım 3: `UpsertRequest`'ten `Code`'u çıkar**

`PanelProductsController.cs` içinde:

```csharp
    public sealed record UpsertRequest(
        [MaxLength(CatalogLimits.ProductName)] string Name,
        Guid? CategoryId,
        decimal DefaultPrice,
        decimal? Cost,
        [MaxLength(CatalogLimits.ShelfLocation)] string? ShelfLocation,
        [MaxLength(CatalogLimits.AxisName)] string? Axis1Name, AxisRole? Axis1Role,
        [MaxLength(CatalogLimits.AxisName)] string? Axis2Name, AxisRole? Axis2Role);
```

`Validate(req)` metodunda `req.Code`'a değen bir dal varsa sil.

- [ ] **Adım 4: `Create`'i sistem üretimine çevir**

`Create` içindeki 313–326. satır aralığındaki kod bloğunu şununla değiştir:

```csharp
        // Kod SİSTEMİN: istemci gövdesinde yok, buradan üretiliyor. Operatörün
        // yayında söylediği kod ayrı bir kavram (ProductBroadcastCode).
        var codes = await _db.Products
            .Where(p => p.LicenseId == licenseId.Value)
            .Select(p => p.Code)
            .ToListAsync(ct);
        var code = StockCodeSequence.Next(codes);
```

`catch (DbUpdateException)` bloğunun gövdesini şununla değiştir (yorumun
gerekçesi aynı kalıyor, yalnız artık kullanıcının yazdığı bir kod yok):

```csharp
        catch (DbUpdateException)
        {
            // Yarış: iki istek aynı anda sıradaki numarayı okudu ve aynı kodu
            // üretti (çift tıklama). Sebebi SQL hata numarasından (2601/2627)
            // ayıklamıyoruz: sağlayıcıya bağımlı olur ve PostgreSQL göçünde
            // sessizce çürür. Tekrar SORMAK hem sağlayıcıdan bağımsız hem kesin.
            //
            // Yeniden deneme (kodu tazeleyip tekrar kaydetme) BİLEREK yapılmadı:
            // istisna sonrası entity'leri detach edip yeniden kurmak gerekirdi ve
            // o yol EF InMemory'de hiç çalışmaz (benzersiz indeks zorlanmıyor) —
            // yani hiç test edilemeyen bir kurtarma kodu eklerdik. Operatör
            // kaydete bir daha basınca yeni numara üretilir.
            var raced = await _db.Products.AnyAsync(
                p => p.LicenseId == licenseId.Value && p.Code == code && p.Id != product.Id, ct);
            if (raced)
                return Problem(title: "code-race",
                    detail: "Ürün kodu üretilirken çakışma oldu. Lütfen tekrar kaydet.",
                    statusCode: 409);
            throw; // Benzersizlik değilse (örn. eşzamanlı silinen kategorinin FK'sı)
                   // yutma — bilinmeyen veri hatası 500 olarak görünmeli.
        }
```

- [ ] **Adım 5: `Update`'ten kod yolunu çıkar**

399–405. satırlardaki `var code = NormalizeCode(req.Code); …` bloğunu **tamamen sil**.
471. satırdaki `product.Code = code;` satırını **sil** (kod değişmez).
501–512. satırlardaki `try/catch (DbUpdateException)` sarmalayıcısını kaldırıp
düz çağrıya indir:

```csharp
        // Kod artık burada değişmiyor; benzersizlik yarışı yalnız Create'te olabilir.
        await _db.SaveChangesAsync(ct);
```

- [ ] **Adım 6: Ölü kodu sil**

`PanelProductsController.cs` içinden sil:
- `NextCode` action'ı ve `[HttpGet("next-code")]` niteliği
- `NextCodeDto` record'u
- `CodeTakenAsync` metodu
- `NormalizeCode` metodu

Dosyaları sil:

```bash
git rm OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs \
       OrderDeck.LicenseServer.Tests/Services/CatalogCodeSequenceTests.cs
```

- [ ] **Adım 7: Derle ve kalan kırıkları düzelt**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Beklenen: `Build succeeded`. Hata çıkarsa `CatalogCodeSequence`/`NormalizeCode`
kullanan başka bir yer kalmıştır — o çağrıları da kaldır.

- [ ] **Adım 8: Testleri çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelProductsTests
```

Beklenen: iki yeni test PASS. Eski testlerden `code = "..."` gönderen ya da
`next-code` çağıran olanlar derlenmez/kırmızı olur — **onları uyarlamak bu
adımın parçası**: gövdeden `code` alanını çıkar, üretilen koda dair iddiaları
`MatchRegex("^SK[0-9]{5,}$")`'e çevir, `next-code` testini sil.

- [ ] **Adım 9: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs \
        OrderDeck.LicenseServer/Services/Catalog/CatalogCodeSequence.cs \
        OrderDeck.LicenseServer.Tests/Services/CatalogCodeSequenceTests.cs \
        OrderDeck.LicenseServer.Tests/Panel/PanelProductsTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): ürün kodu sistem üretimi SK00001, elle girilmiyor

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 5: Varyant benzersizliği normalize eksen değerlerine geçer

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/ProductVariant.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Test: `OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`

Bu görev **eklemeli**: eski `VariantCode` kolonu ve indeksi hâlâ duruyor, yeni
kolonlar yanına geliyor. Silme Görev 6'da.

- [ ] **Adım 1: Başarısız testleri yaz**

`CatalogModelTests.cs` — `expected` dizisine iki satır daha ekle
(`ProductVariant.Barcode` satırından hemen sonra):

```csharp
            (typeof(ProductVariant), nameof(ProductVariant.Axis1ValueNorm), CatalogLimits.AxisValue),
            (typeof(ProductVariant), nameof(ProductVariant.Axis2ValueNorm), CatalogLimits.AxisValue),
```

Dosyanın sonuna yeni test:

```csharp
    /// <summary>
    /// Bekçi: varyant benzersizliği artık normalize eksen değerleri üstünde ve
    /// bu kolonlar da <c>SaveChanges</c> zincirinde türetiliyor.
    ///
    /// <para>Eksen YOKSA değer <c>null</c> değil <b>boş dize</b>. Sebebi
    /// benzersizlik indeksi: hem SQL Server hem PostgreSQL, UNIQUE indekste
    /// NULL'ları birbirinden FARKLI sayar — tek eksenli üründe
    /// <c>Axis2Value</c> null olduğu için indeks hiç ısırmazdı ve aynı kırılım
    /// sınırsız kez eklenebilirdi.</para>
    /// </summary>
    [Fact]
    public async Task Variant_axis_values_are_normalized_and_never_null()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = NewLicense();
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "SK00042",
            Name = "Normalize",
            DefaultPrice = 10m,
            Axis1Name = "Renk",
            Axis1Role = AxisRole.Seller,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = " kırmızı ",
            Axis2Value = null,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.ProductVariants.Add(variant);
        await db.SaveChangesAsync();

        variant.Axis1ValueNorm.Should().Be("KIRMIZI");
        variant.Axis2ValueNorm.Should().Be("",
            "eksensiz kolon NULL kalırsa UNIQUE indeks o satırları benzersiz "
            + "sayar ve aynı kırılım tekrar tekrar eklenebilir");

        variant.Axis1Value = "Mavi";
        await db.SaveChangesAsync();

        variant.Axis1ValueNorm.Should().Be("MAVI");
    }
```

`ProductVariant` kurulumunda `VariantCode` zorunlu olduğu için test bu adımda
zaten derlenmeyecek olabilir — Görev 6 o alanı kaldırana kadar geçici olarak
`VariantCode = "SK00042-KIRM",` satırını ekle; Görev 6'nın ilk adımı bu satırı
silecek.

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~CatalogModelTests
```

Beklenen: FAIL — `CS1061: 'ProductVariant' does not contain a definition for 'Axis1ValueNorm'`.

- [ ] **Adım 3: Kolonları varlığa ekle**

`OrderDeck.LicenseServer/Domain/ProductVariant.cs` — `Axis2Value`'nun altına:

```csharp
    /// <summary>
    /// <c>SearchNormalizer.Normalize(Axis1Value)</c>. Türetilmiş — elle YAZILMAZ,
    /// <c>LicenseDbContext.SyncDerivedColumns</c> dolduruyor.
    ///
    /// <para>Varyantın kimliği bu iki kolon. Ham değerlerin üstüne benzersizlik
    /// kurmak iki yerde sessizce bozulurdu: harf duyarlılığı veritabanının
    /// collation'ına kalırdı (SQL Server duyarsız, PostgreSQL duyarlı) ve
    /// NULL'lar indekste birbirinden farklı sayılırdı. Eksen yoksa değer
    /// <b>boş dize</b>, null değil.</para>
    /// </summary>
    public string Axis1ValueNorm { get; set; } = string.Empty;

    /// <summary><see cref="Axis1ValueNorm"/>'un ikinci eksen karşılığı.</summary>
    public string Axis2ValueNorm { get; set; } = string.Empty;
```

- [ ] **Adım 4: Eşleme ve benzersizlik indeksi**

`LicenseDbContext.OnModelCreating` — `mb.Entity<ProductVariant>(…)` bloğuna ekle
(mevcut `HasIndex` satırlarının hemen üstüne):

```csharp
            b.Property(v => v.Axis1ValueNorm).HasMaxLength(CatalogLimits.AxisValue).IsRequired();
            b.Property(v => v.Axis2ValueNorm).HasMaxLength(CatalogLimits.AxisValue).IsRequired();

            // Varyantın kimliği eksen değerleri; kod DEĞİL. Kod üstündeki eski
            // indeks ("Kırmızı" ve "Kırmızılı" ikisi de KIRM) yapay çakışma
            // üretiyordu — o 409 bu indeksle birlikte ortadan kalkıyor.
            b.HasIndex(v => new { v.ProductId, v.Axis1ValueNorm, v.Axis2ValueNorm })
                .IsUnique();
```

- [ ] **Adım 5: Türetmeyi ekle**

`SyncDerivedColumns()` içine, `ProductBroadcastCode` döngüsünün ardına:

```csharp
        foreach (var entry in ChangeTracker.Entries<ProductVariant>())
        {
            if (entry.State is not (EntityState.Added or EntityState.Modified)) continue;
            // Normalize(null) boş dize döndürüyor — eksensiz satır da NOT NULL kalıyor.
            entry.Entity.Axis1ValueNorm = SearchNormalizer.Normalize(entry.Entity.Axis1Value);
            entry.Entity.Axis2ValueNorm = SearchNormalizer.Normalize(entry.Entity.Axis2Value);
        }
```

- [ ] **Adım 6: Testleri çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~CatalogModelTests
```

Beklenen: PASS.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/ProductVariant.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): varyant benzersizliği normalize eksen değerlerine taşındı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 6: `VariantCode` ve eksen kodlarını sil

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/ProductVariant.cs`
- Modify: `OrderDeck.LicenseServer/Domain/CatalogLimits.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`
- Delete: `OrderDeck.LicenseServer/Services/Catalog/VariantCodeBuilder.cs`, `.../AxisCodeDeriver.cs`
- Delete: `OrderDeck.LicenseServer.Tests/Services/VariantCodeBuilderTests.cs`, `.../AxisCodeDeriverTests.cs`
- Test: `OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`, `.../Panel/PanelProductVariantsTests.cs`

- [ ] **Adım 1: Testleri yeni gerçeğe göre yaz**

`CatalogModelTests.cs`:
- `expected` dizisinden şu üç satırı **sil**: `ProductVariant.Axis1Code`, `ProductVariant.Axis2Code`, `ProductVariant.VariantCode`.
- Görev 5'te eklediğin geçici `VariantCode = "SK00042-KIRM",` satırını sil.
- `Category_product_and_variant_roundtrip` testindeki varyant kurulumunu ve son iddiayı şununla değiştir:

```csharp
        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "Siyah",
            Axis2Value = "M",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
```

```csharp
        loaded.Variants.Should().ContainSingle()
            .Which.Axis1ValueNorm.Should().Be("SIYAH");
```

Aynı testte `Code = "A1"` olan ürün kodunu `Code = "SK00001"` yap (kod artık bu biçimde).

`PanelProductVariantsTests.cs`:
- Gövdesinde `axis1Code` / `axis2Code` gönderen tüm testlerden o alanları çıkar.
- `variant-code-collision` bekleyen test **artık geçerli değil** — "Kırmızı" ve
  "Kırmızılı" ayrı varyantlar olduğu için istek başarılı olmalı. Testi şöyle çevir:

```csharp
    /// <summary>
    /// Eskiden bu iki değer aynı türetilmiş koda (KIRM) düşüp yapay bir 409
    /// üretiyordu. Benzersizlik normalize DEĞERE taşındığından ikisi de
    /// meşru ve ayrı varyant.
    /// </summary>
    [Fact]
    public async Task Similar_axis_values_are_separate_variants()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithAxisAsync(client, "Renk");

        var first = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Kırmızı", isActive = true });
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        var second = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Kırmızılı", isActive = true });

        second.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    /// <summary>
    /// Farklı yazım gerçek tekrardır: "kırmızı" ile "Kırmızı" aynı varyant.
    /// </summary>
    [Fact]
    public async Task Same_axis_value_in_different_casing_is_a_duplicate()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithAxisAsync(client, "Renk");

        await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Kırmızı", isActive = true });

        var again = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "  kirmizi  ", isActive = true });

        again.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await again.Content.ReadAsStringAsync();
        body.Should().Contain("duplicate-variant");
    }
```

> `NewPanelClientAsync` ve `NewProductWithAxisAsync` yer tutucu değil: dosyada
> hâlihazırda bulunan kurulum yardımcılarıdır. Komşu testlerin kullandığı gerçek
> adları kullan; ürün oluşturan yardımcının gövdesinden de `code` alanını çıkar.

- [ ] **Adım 2: Testleri çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter "FullyQualifiedName~CatalogModelTests|FullyQualifiedName~PanelProductVariantsTests"
```

Beklenen: FAIL (derleme: `Axis1Code`/`VariantCode` hâlâ zorunlu alanlar).

- [ ] **Adım 3: Varlıktan ve sınırlardan kolonları kaldır**

`ProductVariant.cs` — `Axis1Code`, `Axis2Code`, `VariantCode` özelliklerini sil.
`CatalogLimits.cs` — `VariantCode` ve `AxisCode` sabitlerini sil.

- [ ] **Adım 4: Eşlemeyi temizle**

`LicenseDbContext.OnModelCreating`, `mb.Entity<ProductVariant>(…)` bloğundan sil:

```csharp
            b.Property(v => v.Axis1Code).HasMaxLength(CatalogLimits.AxisCode);
            b.Property(v => v.Axis2Code).HasMaxLength(CatalogLimits.AxisCode);
            b.Property(v => v.VariantCode).HasMaxLength(CatalogLimits.VariantCode).IsRequired();
            b.HasIndex(v => new { v.ProductId, v.VariantCode }).IsUnique();
            b.HasIndex(v => new { v.LicenseId, v.VariantCode });
```

- [ ] **Adım 5: `PanelProductVariantsController`'ı kod kavramından arındır**

Sınıf XML doc'unu (13–22. satırlar) şununla değiştir:

```csharp
/// <summary>
/// Ürün varyantları (Faz 1a). Varyantın <b>kodu yoktur</b>: kimliği
/// <c>Id</c>, kullanıcıya görünen adı eksen değerleridir ("Siyah · M").
/// Yayında söylenen kod ürün + satıcı ekseni seviyesinde ve ayrı bir
/// kaynakta (<see cref="ProductBroadcastCode"/>) yaşıyor.
///
/// Benzersizlik normalize eksen değerlerinde
/// (<c>Axis1ValueNorm</c>, <c>Axis2ValueNorm</c>) — türetilmiş kısaltmalarda
/// değil. Faz 1c'de barkot yükü basım anında <c>ProductVariant.Barcode</c>'a
/// yazılıp dondurulacak; okutma oradan çözümlenir.
/// </summary>
```

`VariantRequest`'i şununla değiştir:

```csharp
    // DİKKAT — positional record'da doğrulama attribute'u PARAMETREYE yazılır,
    // [property:] hedefiyle DEĞİL. MVC record'un birincil kurucusunu okuyor;
    // metadata property'ye taşınırsa çalışma zamanında istisna atıyor.
    public sealed record VariantRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? Axis1Value,
        [MaxLength(CatalogLimits.AxisValue)] string? Axis2Value,
        bool IsActive);
```

`Segments`, `BuildSegments`, `VariantCodeTakenAsync`, `SameValues` ve
`ResolveCode`'u şu blokla değiştir (`Describe` aynen kalıyor):

```csharp
    private readonly record struct Segments(
        string? Axis1Value, string? Axis2Value,
        string Axis1ValueNorm, string Axis2ValueNorm);

    /// <summary>
    /// Eksen değerlerini doğrular ve karşılaştırma biçimlerini kurar.
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

        // Normalleştirici arama, benzersizlik ve canlı eşleştirme ile ORTAK
        // (SearchNormalizer): kopyası yazılsaydı tanımlar zamanla ayrışırdı.
        // Kolonun kendisi SaveChanges zincirinde de aynı fonksiyonla doluyor;
        // buradaki hesap yalnız ÖN kontrol sorgusu için.
        return new Segments(
            axis1Value, axis2Value,
            SearchNormalizer.Normalize(axis1Value),
            SearchNormalizer.Normalize(axis2Value));
    }

    /// <summary>
    /// Bu kırılım üründe zaten varsa 409 döndürür, yoksa null.
    ///
    /// <para>Tek bir çakışma türü kaldı: <c>duplicate-variant</c>. Eski
    /// <c>variant-code-collision</c> dalı, türetilmiş kısaltmaların yapay
    /// çakışmasıydı ("Kırmızı" ve "Kırmızılı" ikisi de KIRM) — benzersizlik
    /// değerin kendisine taşındığı için o durum artık çakışma değil.</para>
    ///
    /// <para>Hem <c>SaveChanges</c> ÖNCESİ ön kontrol hem SONRASI yarış
    /// sınıflandırması buradan geçiyor: iki ayrı kopya olsaydı biri değişip
    /// öbürü kalır, aynı çakışma isteğin zamanlamasına göre farklı cevap
    /// alırdı.</para>
    ///
    /// <para>Sorgu <c>AsNoTracking</c>: <see cref="DbUpdateException"/> sonrası
    /// context kirli, başarısız kayıt hâlâ <c>Added</c> durumunda takip
    /// ediliyor; izlenen sorgu kimlik çözümlemesiyle o kaydı geri getirip
    /// yanlış cevap verebilir.</para>
    /// </summary>
    private async Task<IActionResult?> VariantValuesTakenAsync(
        Guid productId, Segments built, Guid? excludeId, CancellationToken ct)
    {
        var axis1 = built.Axis1ValueNorm;
        var axis2 = built.Axis2ValueNorm;

        var exists = await _db.ProductVariants
            .AsNoTracking()
            .AnyAsync(v => v.ProductId == productId
                           && v.Axis1ValueNorm == axis1
                           && v.Axis2ValueNorm == axis2
                           && (excludeId == null || v.Id != excludeId), ct);

        if (!exists) return null;

        return Problem(title: "duplicate-variant",
            detail: $"'{Describe(built.Axis1Value, built.Axis2Value)}' varyantı "
                  + "bu üründe zaten var.",
            statusCode: 409);
    }
```

`Create`, `CreateBulk` ve `Update` içinde:
- `VariantCodeTakenAsync(` çağrılarının hepsini `VariantValuesTakenAsync(` yap.
- `ProductVariant` kurulumlarından `Axis1Code`, `Axis2Code`, `VariantCode` atamalarını sil.
- `Update`'te `variant.Axis1Code`, `variant.Axis2Code`, `variant.VariantCode` atamalarını sil.
- `CreateBulk`'un parti içi tekrar taramasını (155–162. satırlar) şununla değiştir:

```csharp
        for (var i = 0; i < built.Count; i++)
        for (var j = i + 1; j < built.Count; j++)
            if (string.Equals(built[i].Axis1ValueNorm, built[j].Axis1ValueNorm,
                    StringComparison.Ordinal)
                && string.Equals(built[i].Axis2ValueNorm, built[j].Axis2ValueNorm,
                    StringComparison.Ordinal))
                return Problem(title: "duplicate-in-batch",
                    detail: $"'{Describe(built[j].Axis1Value, built[j].Axis2Value)}' "
                          + "listede birden fazla kez var.",
                    statusCode: 409);
```

`ToDto`'yu şununla değiştir:

```csharp
    private static PanelProductsController.VariantDto ToDto(ProductVariant v) => new(
        v.Id, v.Axis1Value, v.Axis2Value, v.Barcode, v.IsActive);
```

Kullanılmayan `using OrderDeck.LicenseServer.Services.Catalog;` satırını sil.

- [ ] **Adım 6: `PanelProductsController`'ı temizle**

`VariantDto`'yu şununla değiştir:

```csharp
    public sealed record VariantDto(
        Guid Id, string? Axis1Value, string? Axis2Value, string? Barcode, bool IsActive);
```

`BuildAutoVariant`'tan `VariantCode` atamasını sil:

```csharp
    private static ProductVariant BuildAutoVariant(Product product, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = product.LicenseId,
        ProductId = product.Id,
        // Eksen değeri yok; Axis*ValueNorm boş dize kalır ve UNIQUE indeks
        // ürün başına tek otomatik satıra izin verir.
        IsActive = true,
        CreatedAt = now,
        UpdatedAt = now,
    };
```

`SyncVariantCodes` metodunu ve `Update` içindeki `SyncVariantCodes(product, now);`
çağrısını sil.

`ToDtoAsync` içindeki varyant sıralamasını şununla değiştir:

```csharp
            // Sıralama normalize değerlerde: ASCII ve sağlayıcıdan bağımsız.
            // Ham değerde sıralamak sırayı veritabanının collation'ına bağlardı
            // ve Postgres göçünde kırılım listesi sessizce karışırdı.
            p.Variants
                .OrderBy(v => v.Axis1ValueNorm, StringComparer.Ordinal)
                .ThenBy(v => v.Axis2ValueNorm, StringComparer.Ordinal)
```

`ToDtoAsync`'in `VariantDto` kurulumundan da kod alanlarını çıkar:

```csharp
                .Select(v => new VariantDto(
                    v.Id, v.Axis1Value, v.Axis2Value, v.Barcode, v.IsActive))
```

- [ ] **Adım 7: WPF çekme ucuna uyum kalkanını kur**

`LicensesWpfCatalogPullController.cs` — `CatalogVariantDto` **tel modeli olarak
aynen kalıyor** (WPF sözleşmesi), yalnız kaynağı değişiyor. `Select` içindeki
varyant bloğunu şununla değiştir:

```csharp
                // GEÇİCİ UYUM KALKANI — plan 2/3'te kaldırılacak.
                //
                // Sunucudaki VariantCode/Axis*Code kolonları kalktı ama WPF
                // replikasında VariantCode hâlâ NOT NULL ve tel modelinde
                // nullable değil. Alanı ürünün stok koduyla dolduruyoruz:
                // WPF bu değeri YALNIZ iki eksen değeri de boşken gösteriyor
                // (CatalogVariantViewModel: Display = label ?? VariantCode) ve
                // o satırlar zaten BuildAutoVariant ile product.Code taşıyordu
                // — davranış birebir aynı.
                //
                // Axis1Code/Axis2Code artık gönderilmiyor; iki tarafta da
                // nullable olduğu için JSON'da yoklukları sorunsuz.
                p.Variants
                    .OrderBy(v => v.Axis1ValueNorm)
                    .ThenBy(v => v.Axis2ValueNorm)
                    .Select(v => new CatalogVariantDto(
                        v.Id, v.Axis1Value, null,
                        v.Axis2Value, null,
                        p.Code, v.Barcode, v.IsActive))
                    .ToList()))
```

- [ ] **Adım 8: Ölü servisleri sil**

```bash
git rm OrderDeck.LicenseServer/Services/Catalog/VariantCodeBuilder.cs \
       OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs \
       OrderDeck.LicenseServer.Tests/Services/VariantCodeBuilderTests.cs \
       OrderDeck.LicenseServer.Tests/Services/AxisCodeDeriverTests.cs
```

- [ ] **Adım 9: Derle, kalan atıfları temizle**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Beklenen: `Build succeeded`. Hata verirse kalan `VariantCode`/`Axis1Code`/
`Axis2Code`/`AxisCodeDeriver`/`VariantCodeBuilder` atıflarını kaldır.
Kontrol:

```bash
grep -rn "VariantCode\|Axis1Code\|Axis2Code\|AxisCodeDeriver" OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests
```

Beklenen çıktı: yalnız `LicensesWpfCatalogPullController.cs` (tel modeli
`CatalogVariantDto` ve uyum kalkanı yorumu) ve onun testleri.

- [ ] **Adım 10: Tüm sunucu testlerini çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: tümü PASS. Kırmızı kalan testler kod alanı bekleyen eski testlerdir;
bu adımda yeni gerçeğe uyarla.

- [ ] **Adım 11: Commit**

```bash
git add OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests
git commit -m "$(cat <<'EOF'
refactor(katalog): varyant kodu kavramı kaldırıldı

VariantCode, Axis1Code, Axis2Code kolonları ve türeten servisler
(VariantCodeBuilder, AxisCodeDeriver) silindi. Varyantın kimliği artık
normalize eksen değerleri. WPF çekme ucu geçici uyum kalkanıyla eski
tel modelini koruyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

> **Not:** `git add OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests` burada
> güvenli — kirli dosyaların hiçbiri bu iki klasörde değil. Yine de `git status --short`
> ile stage'i doğrula.

---

## Görev 7: Yıkıcı göç — `DropVariantCodes`

**Files:**
- Create: `OrderDeck.LicenseServer/Migrations/*_DropVariantCodes.cs` (araç üretir, sonra elle düzenlenir)

- [ ] **Adım 1: Göçü üret**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet ef migrations add DropVariantCodes \
  --project OrderDeck.LicenseServer \
  --startup-project OrderDeck.LicenseServer \
  --context LicenseDbContext
```

- [ ] **Adım 2: Üretilen göçe geri doldurmayı elle ekle**

EF'in ürettiği `Up(...)` şu sırada olmalı: eski indeksleri düşür → yeni kolonları
ekle → **geri doldurma** → yeni benzersiz indeksi kur → eski kolonları düşür.
Aracın koyduğu `CreateIndex` çağrısını, aşağıdaki `Sql` bloğundan **sonraya** taşı
ve bloğu araya ekle:

```csharp
        // Geri doldurma: yeni kolonlar SearchNormalizer'ın yaptığını yapmalı —
        // Türkçe harfleri ASCII'ye katla, büyüt, kırp. Sıra önemli: benzersiz
        // indeks bu doldurmadan SONRA kurulmalı, yoksa boş dizelerle dolu
        // kolonlarda ürün başına tek satıra düşer ve göç patlar.
        //
        // Bu dağıtımda tablo boş (2026-08-13: SELECT COUNT(*) FROM Products = 0),
        // yani pratikte no-op. Yine de veri varmış gibi yazıldı: bir sonraki
        // ortamda (staging, yeniden kurulan prod) boş olmayabilir.
        //
        // SearchNormalizer'dan tek farkı: kelime ARASI çoklu boşluğu teke
        // indirmiyor. Kabul edildi — eksen değerinde ("Siyah", "M") çift boşluk
        // gerçekçi değil, ve eşleşmeyen tek satır yalnız o varyantı yeniden
        // kaydetmeyi gerektirir.
        migrationBuilder.Sql(@"
UPDATE [ProductVariants]
SET [Axis1ValueNorm] = UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                         LTRIM(RTRIM(ISNULL([Axis1Value], N'')))
                       , N'ç', N'c'), N'Ç', N'C')
                       , N'ğ', N'g'), N'Ğ', N'G')
                       , N'ı', N'i'), N'İ', N'I')
                       , N'ö', N'o'), N'Ö', N'O')
                       , N'ş', N's'), N'Ş', N'S')
                       , N'ü', N'u'), N'Ü', N'U')),
    [Axis2ValueNorm] = UPPER(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                       REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(
                         LTRIM(RTRIM(ISNULL([Axis2Value], N'')))
                       , N'ç', N'c'), N'Ç', N'C')
                       , N'ğ', N'g'), N'Ğ', N'G')
                       , N'ı', N'i'), N'İ', N'I')
                       , N'ö', N'o'), N'Ö', N'O')
                       , N'ş', N's'), N'Ş', N'S')
                       , N'ü', N'u'), N'Ü', N'U'));
");
```

- [ ] **Adım 3: `Down` yolunu kabul edilebilir hâle getir**

`Down(...)` eski kolonları geri ekler ama içleri boş kalır ve `VariantCode`
NOT NULL'dır — araç `defaultValue: ""` koyar, o da eski benzersiz indeksi
patlatır. `Down`'un başına açık bir yorum ekle:

```csharp
        // Geri alma tek yönlü DEĞİL ama veri geri gelmez: VariantCode/Axis*Code
        // türetilmiş kolonlardı ve kaynakları (VariantCodeBuilder,
        // AxisCodeDeriver) bu sürümde silindi. Bu Down yalnız şemayı geri
        // kurar; birden fazla varyantı olan bir üründe eski benzersiz indeks
        // boş kodlar yüzünden kurulamaz. Gerçek geri dönüş yolu: yedekten
        // geri yükleme.
```

- [ ] **Adım 4: Derle**

```bash
dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj
```

Beklenen: `Build succeeded`.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Migrations
git commit -m "$(cat <<'EOF'
feat(katalog): varyant kodu kolonlarını düşüren göç + norm geri doldurma

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 8: Yayın kodu ucu

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastCodesController.cs`
- Modify: `OrderDeck.LicenseServer/Domain/Product.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs` (Adım 6 — silme bekçisi)
- Test: `OrderDeck.LicenseServer.Tests/Panel/PanelBroadcastCodesTests.cs`

- [ ] **Adım 1: Test dosyasındaki kurulum kalıbını öğren**

`OrderDeck.LicenseServer.Tests/Panel/PanelProductsTests.cs` dosyasını aç ve şunları not et:
- sınıf başlığı ve fixture (`IClassFixture<ApiFactory>` mi, başka mı)
- kimlik doğrulanmış panel istemcisini kuran yardımcının **gerçek adı**
- ürün oluşturan yardımcı varsa adı

Aşağıdaki testlerde bu adları kullan (planda `NewPanelClientAsync` diye anılıyorlar).

- [ ] **Adım 2: Başarısız testleri yaz**

`OrderDeck.LicenseServer.Tests/Panel/PanelBroadcastCodesTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Panel;

public class PanelBroadcastCodesTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelBroadcastCodesTests(ApiFactory f) => _factory = f;

    [Fact]
    public async Task Code_is_saved_and_returned_for_the_seller_axis_value()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var put = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = " ateş " });

        put.StatusCode.Should().Be(HttpStatusCode.OK);

        var get = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        get.GetArrayLength().Should().Be(1);
        get[0].GetProperty("code").GetString().Should().Be("ateş");
        get[0].GetProperty("sellerAxisValue").GetString().Should().Be("Siyah");
    }

    /// <summary>
    /// Kod bir daha ASLA devredilmez: başka bir ürüne verilmiş kod reddedilir.
    /// Devredilseydi, eski yayın videosundaki kodu bugün yazan izleyicinin
    /// siparişi yanlış ürüne düşerdi.
    /// </summary>
    [Fact]
    public async Task Code_used_by_another_product_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var first = await NewProductWithSellerAxisAsync(client);
        var second = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{first}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var clash = await client.PutAsJsonAsync(
            $"/api/panel/products/{second}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ates" });

        clash.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await clash.Content.ReadAsStringAsync())
            .Should().Contain("Bu yayın kodu daha önce kullanılmış.");
    }

    /// <summary>
    /// Aynı hedefe aynı kodu yeniden yazmak çakışma değil; satır tazelenir.
    /// Panel kaydete iki kez basınca 409 görmemeli.
    /// </summary>
    [Fact]
    public async Task Rewriting_the_same_code_to_the_same_target_succeeds()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var again = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// Kod değişince eski satır SİLİNMEZ — kodu rezerve tutmaya devam eder.
    /// "Güncel" olan yalnız en yeni satır, GET onu döndürür.
    /// </summary>
    [Fact]
    public async Task Changing_the_code_keeps_the_old_one_reserved()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);
        var other = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ESKI" });
        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "YENI" });

        var current = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");
        current.GetArrayLength().Should().Be(1);
        current[0].GetProperty("code").GetString().Should().Be("YENI");

        var stealOld = await client.PutAsJsonAsync(
            $"/api/panel/products/{other}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ESKI" });

        stealOld.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Unknown_seller_axis_value_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Turuncu", code = "ATEŞ" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Empty_code_is_rejected()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "   " });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>
    /// Ürün "Renk" ekseni satıcı rolünde, altında Siyah varyantı olacak şekilde
    /// kurulur ve Id'si döner.
    /// </summary>
    private static async Task<Guid> NewProductWithSellerAxisAsync(HttpClient client)
    {
        var created = await client.PostAsJsonAsync("/api/panel/products", new
        {
            name = "Yayın Kodlu " + Guid.NewGuid().ToString("N")[..6],
            defaultPrice = 100m,
            axis1Name = "Renk",
            axis1Role = 1,
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);

        var dto = await created.Content.ReadFromJsonAsync<JsonElement>();
        var id = dto.GetProperty("id").GetGuid();

        var variant = await client.PostAsJsonAsync(
            $"/api/panel/products/{id}/variants",
            new { axis1Value = "Siyah", isActive = true });
        variant.StatusCode.Should().Be(HttpStatusCode.Created);

        return id;
    }
}
```

`NewPanelClientAsync`'i Adım 1'de öğrendiğin gerçek yardımcıyla değiştir (ya da
o yardımcıyı çağıran tek satırlık özel bir metot yaz).

- [ ] **Adım 3: Testi çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBroadcastCodesTests
```

Beklenen: FAIL — PUT/GET 404 döndürüyor (uç yok).

- [ ] **Adım 4: `Product`'a satıcı ekseni yardımcılarını ekle**

`OrderDeck.LicenseServer/Domain/Product.cs` — dosyanın başına
`using System.ComponentModel.DataAnnotations.Schema;` ekle, sınıfın sonuna:

```csharp
    /// <summary>
    /// Satıcı ekseninin sırası: 1, 2 ya da 0 (satıcı ekseni yok). Satıcı
    /// ekseni barkot okutmayla sabitlenen eksendir; yayın kodu ona bağlanır.
    /// Tek bir yerde hesaplanıyor — iki controller da bunu kullanıyor ve
    /// kopyalansaydı biri değişip öbürü kalırdı.
    /// </summary>
    [NotMapped]
    public int SellerAxis =>
        Axis1Name is not null && Axis1Role == AxisRole.Seller ? 1
        : Axis2Name is not null && Axis2Role == AxisRole.Seller ? 2
        : 0;

    /// <summary>Varyantın satıcı ekseni değeri; satıcı ekseni yoksa null.</summary>
    public string? SellerAxisValueOf(ProductVariant variant) =>
        SellerAxis switch
        {
            1 => variant.Axis1Value,
            2 => variant.Axis2Value,
            _ => null,
        };
```

- [ ] **Adım 5: Controller'ı yaz**

`OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastCodesController.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.Shared.Text;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Yayın kodları: operatörün canlı yayında söylediği, izleyicinin yoruma
/// yazdığı kod. Ürünün stok kodundan (<c>SK00001</c>) apayrı.
///
/// <para><b>Neden ayrı controller:</b> kaynak ayrı ve kuralları ayrı —
/// <c>PanelProductsController</c> zaten 750+ satır ve buranın tek kuralı
/// ("kod bir daha asla devredilmez") ürün kartının kurallarıyla hiç
/// kesişmiyor.</para>
///
/// <para><b>Silme ucu YOK.</b> Kod serbest bırakılamaz: eski yayın
/// videosundaki kodu bugün yazan izleyicinin siparişi, kod devredilmiş olsaydı
/// yanlış ürüne düşerdi. Kod değişikliği bu yüzden güncelleme değil, yeni satır
/// — eski satır kodu rezerve tutmaya devam eder.</para>
/// </summary>
[ApiController]
[Route("api/panel/products/{productId:guid}/broadcast-codes")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelBroadcastCodesController : ControllerBase
{
    private readonly LicenseDbContext _db;

    public PanelBroadcastCodesController(LicenseDbContext db) => _db = db;

    public sealed record BroadcastCodeDto(
        string? SellerAxisValue, string Code, DateTimeOffset CreatedAt);

    public sealed record BroadcastCodeRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? SellerAxisValue,
        [MaxLength(CatalogLimits.BroadcastCode)] string? Code);

    /// <summary>
    /// Ürünün <b>güncel</b> yayın kodları: satıcı ekseni değeri başına en yeni
    /// satır. Emekli kodlar burada dönmez — panelin işi güncel kodu
    /// düzenletmek. (WPF çekme ucu emeklileri de alıyor; eşleştirme onlara
    /// ihtiyaç duyuyor.)
    /// </summary>
    [AllowStockStaff]
    [HttpGet]
    public async Task<IActionResult> Get(Guid productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var owns = await _db.Products
            .AnyAsync(p => p.Id == productId && p.LicenseId == licenseId.Value, ct);
        if (!owns) return NotFound();

        var rows = await _db.ProductBroadcastCodes.AsNoTracking()
            .Where(x => x.LicenseId == licenseId.Value && x.ProductId == productId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .ToListAsync(ct);

        var current = rows
            .GroupBy(x => SearchNormalizer.Normalize(x.SellerAxisValue), StringComparer.Ordinal)
            .Select(g => g.First())
            .Select(x => new BroadcastCodeDto(x.SellerAxisValue, x.Code, x.CreatedAt))
            .ToList();

        return Ok(current);
    }

    /// <summary>
    /// Bir satıcı ekseni değerine kod atar. Gövde tek satır taşır (toplu değil):
    /// panel kutuları tek tek kaydediyor ve bir kutunun 409'u ötekileri
    /// geri almamalı.
    /// </summary>
    [AllowStockStaff]
    [HttpPut]
    public async Task<IActionResult> Put(
        Guid productId, [FromBody] BroadcastCodeRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var product = await _db.Products
            .Include(p => p.Variants)
            .FirstOrDefaultAsync(p => p.Id == productId && p.LicenseId == licenseId.Value, ct);
        if (product is null) return NotFound();

        var code = (req.Code ?? string.Empty).Trim();
        if (code.Length == 0)
            return Problem(title: "missing-code",
                detail: "Yayın kodu boş olamaz.", statusCode: 400);

        // Normalize hâl boşsa kod yalnız noktalama taşıyor demektir; böyle bir
        // kod yorumda hiçbir zaman eşleşmez, kaydetmek yanlış güven verirdi.
        var normalized = SearchNormalizer.Normalize(code);
        if (normalized.Length == 0)
            return Problem(title: "invalid-code",
                detail: "Yayın kodu en az bir harf ya da rakam içermeli.", statusCode: 400);

        var sellerValue = ResolveSellerAxisValue(product, req.SellerAxisValue, out var axisError);
        if (axisError is not null) return axisError;

        var now = DateTimeOffset.UtcNow;

        var existing = await _db.ProductBroadcastCodes
            .FirstOrDefaultAsync(
                x => x.LicenseId == licenseId.Value && x.CodeNormalized == normalized, ct);

        if (existing is not null)
        {
            if (!IsSameTarget(existing, product.Id, sellerValue)) return CodeTaken();

            // Aynı hedefe aynı kod: yeni satır AÇMA (benzersiz indeks zaten
            // reddederdi), var olanı güncel yap.
            existing.Code = code;
            existing.SellerAxisValue = sellerValue;
            existing.CreatedAt = now;
            await _db.SaveChangesAsync(ct);
            return Ok(new BroadcastCodeDto(existing.SellerAxisValue, existing.Code, existing.CreatedAt));
        }

        var row = new ProductBroadcastCode
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = product.Id,
            SellerAxisValue = sellerValue,
            Code = code,
            CreatedAt = now,
        };
        _db.ProductBroadcastCodes.Add(row);

        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Yarış: ön kontrolden sonra başka bir istek aynı kodu aldı. Sebebi
            // SQL hata numarasından ayıklamıyoruz — sağlayıcıya bağımlı olur ve
            // PostgreSQL göçünde sessizce çürür; tekrar SORMAK bağımsız ve kesin.
            //
            // DİKKAT — bu dal uçtan uca test EDİLEMEZ: EF InMemory benzersiz
            // indeksi zorlamıyor, istisna testte hiç atılmıyor. Kararın kendisi
            // bu yüzden burada değil, iki yolun da çağırdığı IsSameTarget +
            // CodeTaken ikilisinde duruyor.
            var raced = await _db.ProductBroadcastCodes.AsNoTracking()
                .FirstOrDefaultAsync(
                    x => x.LicenseId == licenseId.Value
                         && x.CodeNormalized == normalized
                         && x.Id != row.Id, ct);
            if (raced is not null) return CodeTaken();
            throw; // Benzersizlik değilse yutma — bilinmeyen veri hatası 500 olmalı.
        }

        return Ok(new BroadcastCodeDto(row.SellerAxisValue, row.Code, row.CreatedAt));
    }

    /// <summary>
    /// Kayıtlı kod ile gelen hedef aynı mı. Ürün <b>ve</b> satıcı ekseni değeri
    /// eşleşmeli — aynı üründe "Siyah"ın kodunu "Kırmızı"ya kaydırmak da
    /// devretmektir.
    /// </summary>
    private static bool IsSameTarget(
        ProductBroadcastCode existing, Guid productId, string? sellerAxisValue)
        => existing.ProductId == productId
           && string.Equals(
               SearchNormalizer.Normalize(existing.SellerAxisValue),
               SearchNormalizer.Normalize(sellerAxisValue),
               StringComparison.Ordinal);

    private IActionResult CodeTaken()
        => Problem(title: "broadcast-code-taken",
            detail: "Bu yayın kodu daha önce kullanılmış.", statusCode: 409);

    /// <summary>
    /// Gelen satıcı ekseni değerini doğrular ve ürün kartındaki <b>kanonik</b>
    /// yazımına çevirir (kullanıcı "siyah" yazsa da kayda "Siyah" girer) —
    /// böylece panelde kod, varyant listesindeki değerle aynı metin altında
    /// görünür.
    /// </summary>
    private string? ResolveSellerAxisValue(
        Product product, string? supplied, out IActionResult? error)
    {
        error = null;
        var trimmed = string.IsNullOrWhiteSpace(supplied) ? null : supplied.Trim();

        if (product.SellerAxis == 0)
        {
            if (trimmed is not null)
                error = Problem(title: "unexpected-seller-axis-value",
                    detail: "Bu ürünün satıcı ekseni yok; yayın kodu ürünün "
                          + "tamamına verilir.", statusCode: 400);
            return null;
        }

        if (trimmed is null)
        {
            error = Problem(title: "missing-seller-axis-value",
                detail: "Yayın kodu bir satıcı ekseni değerine bağlanmalı.",
                statusCode: 400);
            return null;
        }

        var norm = SearchNormalizer.Normalize(trimmed);
        var match = product.Variants
            .Select(product.SellerAxisValueOf)
            .Where(value => value is not null)
            .FirstOrDefault(value =>
                string.Equals(SearchNormalizer.Normalize(value), norm, StringComparison.Ordinal));

        if (match is null)
        {
            var axisName = product.SellerAxis == 1 ? product.Axis1Name : product.Axis2Name;
            error = Problem(title: "unknown-seller-axis-value",
                detail: $"'{trimmed}' bu üründe bir {axisName} değeri değil.",
                statusCode: 400);
            return null;
        }

        return match;
    }

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

- [ ] **Adım 6: Kodu olan ürünün silinmesini engelle**

Bu adım, Görev 1'in kod incelemesinde çıkan bir deliği kapatıyor.
`ProductBroadcastCode` → `Product` ilişkisi `Cascade`, ve
`PanelProductsController.Delete` stok hareketi yoksa ürünü **fiziksel**
siliyor (`_db.Products.Remove`). Yani bugünkü hâliyle: kodu olan ürünü sil →
cascade kod satırını da götürür → "ATEŞ" serbest kalır → başka ürüne verilir →
eski yayın videosundaki kodu yazan izleyicinin siparişi **yanlış ürüne**
düşer. Tam olarak `ProductBroadcastCode` XML doc'unun engellemek için var
olduğu senaryo.

`Cascade` kalmalı (lisans/müşteri silme yolu ona bağlı); kapı ucun kendisinde
kapanır. `PanelProductsController.Delete` içinde, mevcut stok hareketi
kontrolünün (`product-has-stock-movements`, `Problem(... 409)`) **hemen
ardına**, aynı kalıpta:

```csharp
        // Kodu olan ürün silinemez: satır cascade ile giderse kod serbest
        // kalır ve bir daha ASLA devredilmemesi gereken kod başka bir ürüne
        // verilebilir hâle gelir (bkz. ProductBroadcastCode XML doc).
        if (await _db.ProductBroadcastCodes.AnyAsync(x => x.ProductId == product.Id, ct))
            return Problem(title: "product-has-broadcast-codes",
                detail: "Bu ürünün yayın kodları var; silinemez. Arşivleyebilirsiniz.",
                statusCode: 409);
```

Ve `PanelBroadcastCodesTests.cs` sonuna testi ekle:

```csharp
    /// <summary>
    /// Kodun kalıcı rezervasyonu ancak satır durursa mümkün; ürün fiziksel
    /// silinirse cascade satırı götürür ve kod yeniden dağıtılabilir hâle
    /// gelirdi. O yüzden kodu olan ürün silinmez, arşivlenir.
    /// </summary>
    [Fact]
    public async Task Product_with_broadcast_code_cannot_be_deleted()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var del = await client.DeleteAsync($"/api/panel/products/{productId}");

        del.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }
```

- [ ] **Adım 7: Testleri çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBroadcastCodesTests
```

Beklenen: 7 test PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastCodesController.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs \
        OrderDeck.LicenseServer/Domain/Product.cs \
        OrderDeck.LicenseServer.Tests/Panel/PanelBroadcastCodesTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): yayın kodu ucu — kod bir daha devredilmez

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 9: Eksen değeri yeniden adlandırılınca yayın kodu taşınsın

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Panel/PanelBroadcastCodesTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

`PanelBroadcastCodesTests.cs` sonuna:

```csharp
    /// <summary>
    /// Satıcı ekseni değeri yeniden adlandırılınca kod da taşınır. Taşınmasaydı
    /// kod sahipsiz kalır ve canlı yorumda hiçbir kırılıma çözülemezdi —
    /// operatör de bunu ancak yayın ortasında fark ederdi.
    /// </summary>
    [Fact]
    public async Task Renaming_the_seller_axis_value_carries_the_code()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Siyah", code = "ATEŞ" });

        var variants = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}");
        var variantId = variants.GetProperty("variants")[0].GetProperty("id").GetGuid();

        var renamed = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/variants/{variantId}",
            new { axis1Value = "Antrasit", isActive = true });
        renamed.StatusCode.Should().Be(HttpStatusCode.OK);

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(1);
        codes[0].GetProperty("sellerAxisValue").GetString().Should().Be("Antrasit");
        codes[0].GetProperty("code").GetString().Should().Be("ATEŞ");
    }

    /// <summary>
    /// Eski değeri taşıyan BAŞKA varyant kalmışsa bu bir yeniden adlandırma
    /// değil, tek satırın başka bir değere geçirilmesi — kod eski değerde kalır.
    /// </summary>
    [Fact]
    public async Task Moving_one_row_does_not_carry_the_code()
    {
        var client = await NewPanelClientAsync();
        var productId = await NewProductWithSellerAxisAsync(client);

        // İkinci bir "Siyah" satırı yaratmak için ürüne ikinci eksen gerekir;
        // bunun yerine ikinci varyantı farklı değerle açıp ONU Siyah'a taşıyoruz,
        // sonra ilk satırı yeniden adlandırıyoruz: eski değer hâlâ kullanımda.
        var second = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Gri", isActive = true });
        second.StatusCode.Should().Be(HttpStatusCode.Created);

        await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { sellerAxisValue = "Gri", code = "DUMAN" });

        var product = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}");
        var siyahId = product.GetProperty("variants").EnumerateArray()
            .First(v => v.GetProperty("axis1Value").GetString() == "Siyah")
            .GetProperty("id").GetGuid();

        // "Siyah" → "Gri" olamaz (tekrar), o yüzden Siyah'ı yeni bir değere al:
        // "Gri" kodunun taşınmadığını görmek istiyoruz.
        var moved = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/variants/{siyahId}",
            new { axis1Value = "Lacivert", isActive = true });
        moved.StatusCode.Should().Be(HttpStatusCode.OK);

        var codes = await client.GetFromJsonAsync<JsonElement>(
            $"/api/panel/products/{productId}/broadcast-codes");

        codes.GetArrayLength().Should().Be(1);
        codes[0].GetProperty("sellerAxisValue").GetString().Should().Be("Gri",
            "kodun bağlı olduğu değer değişmedi");
    }
```

- [ ] **Adım 2: Testleri çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBroadcastCodesTests
```

Beklenen: `Renaming_the_seller_axis_value_carries_the_code` FAIL
(`sellerAxisValue` hâlâ "Siyah"). Diğeri zaten PASS.

- [ ] **Adım 3: Taşımayı `Update`'e ekle**

`PanelProductVariantsController.Update` içinde, `var now = DateTimeOffset.UtcNow;`
satırından **önce** eski değeri yakala:

```csharp
        // Eski satıcı değeri, atamalardan ÖNCE okunmalı: aşağıdaki satırlar
        // variant'ı yerinde değiştiriyor.
        var oldSellerNorm = SearchNormalizer.Normalize(product.SellerAxisValueOf(variant));
```

Atamalardan sonra, `await _db.SaveChangesAsync(ct);` çağrısından **önce**:

```csharp
        // Satıcı ekseni değeri yeniden adlandırıldıysa yayın kodunu da taşı.
        // Kod, ürün + satıcı ekseni DEĞERİNE bağlı; değer değişip kod yerinde
        // kalsaydı kod hiçbir kırılıma çözülemez hâle gelirdi.
        //
        // Şart: eski değeri taşıyan BAŞKA varyant kalmamış olmalı. Kalmışsa bu
        // yeniden adlandırma değil, tek satırın başka değere geçirilmesidir ve
        // eski kod hâlâ geçerli bir kırılımı gösteriyor.
        //
        // Aynı SaveChanges içinde: ayrı bir kaydetme, arada düşen bir istekte
        // kodu sahipsiz bırakırdı.
        var newSellerNorm = SearchNormalizer.Normalize(product.SellerAxisValueOf(variant));
        if (product.SellerAxis != 0
            && oldSellerNorm.Length > 0
            && !string.Equals(oldSellerNorm, newSellerNorm, StringComparison.Ordinal))
        {
            var stillUsed = product.Variants.Any(v =>
                v.Id != variant.Id
                && string.Equals(
                    SearchNormalizer.Normalize(product.SellerAxisValueOf(v)),
                    oldSellerNorm, StringComparison.Ordinal));

            if (!stillUsed)
            {
                var newSellerValue = product.SellerAxisValueOf(variant);
                var affected = await _db.ProductBroadcastCodes
                    .Where(x => x.ProductId == product.Id)
                    .ToListAsync(ct);

                foreach (var codeRow in affected)
                    if (string.Equals(
                            SearchNormalizer.Normalize(codeRow.SellerAxisValue),
                            oldSellerNorm, StringComparison.Ordinal))
                        codeRow.SellerAxisValue = newSellerValue;
            }
        }
```

- [ ] **Adım 4: Testleri çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBroadcastCodesTests
```

Beklenen: 8 test PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs \
        OrderDeck.LicenseServer.Tests/Panel/PanelBroadcastCodesTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): eksen değeri yeniden adlandırılınca yayın kodu taşınıyor

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 10: Katalog çekme ucuna yayın kodları

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Licenses/LicensesWpfCatalogPullTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`LicensesWpfCatalogPullTests.cs` — dosyadaki mevcut kurulum kalıbıyla
(katalog verisi kuran yardımcı hangisiyse) yeni test ekle:

```csharp
    /// <summary>
    /// Çekme ucu yayın kodlarını EMEKLİLERİYLE BİRLİKTE gönderir. WPF'in
    /// eşleştiricisi (plan 2/3) eski yayın videolarından gelen kodları da
    /// çözebilmeli — emekli kod hâlâ aynı ürünü gösteriyor.
    /// </summary>
    [Fact]
    public async Task Pull_carries_broadcast_codes_including_retired_ones()
    {
        var (client, licenseId, productId) = await NewLicenseWithProductAsync();

        await AddBroadcastCodeAsync(productId, "Siyah", "ESKI", DateTimeOffset.UtcNow.AddDays(-2));
        await AddBroadcastCodeAsync(productId, "Siyah", "YENI", DateTimeOffset.UtcNow);

        var rows = await client.GetFromJsonAsync<JsonElement>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        var product = rows.EnumerateArray()
            .First(p => p.GetProperty("id").GetGuid() == productId);
        var codes = product.GetProperty("broadcastCodes");

        codes.GetArrayLength().Should().Be(2);
        codes.EnumerateArray().Select(c => c.GetProperty("code").GetString())
            .Should().BeEquivalentTo(new[] { "YENI", "ESKI" });
        codes[0].GetProperty("code").GetString().Should().Be("YENI",
            "en yeni kod başta gelmeli — güncel olan o");
        codes[0].GetProperty("codeNormalized").GetString().Should().Be("YENI");
    }
```

> `NewLicenseWithProductAsync` ve `AddBroadcastCodeAsync` bu dosyada henüz yok:
> ilkini dosyadaki mevcut kurulum yardımcısından uyarla, ikincisini
> `_factory.Services.CreateScope()` üstünden `LicenseDbContext`'e doğrudan satır
> yazan küçük bir özel metot olarak ekle (`CreatedAt`'i parametreden alsın ki
> sıralama deterministik olsun).

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~LicensesWpfCatalogPull
```

Beklenen: FAIL — `broadcastCodes` özelliği yok.

- [ ] **Adım 3: DTO'ya alanı ekle**

`LicensesWpfCatalogPullController.cs` — `CatalogVariantDto`'nun altına:

```csharp
    /// <summary>
    /// Yayın kodunun tel modeli. <c>CodeNormalized</c> de gönderiliyor:
    /// eşleştirmeyi WPF yapıyor ve normalleştirmeyi orada bir kez daha
    /// uygulamak, iki tanımın zamanla ayrışması demekti — kural sunucuda
    /// tanımlı, telde taşınıyor.
    /// </summary>
    public sealed record CatalogBroadcastCodeDto(
        string? SellerAxisValue, string Code, string CodeNormalized, DateTimeOffset CreatedAt);
```

`CatalogProductDto`'nun son parametresi olan `List<CatalogVariantDto> Variants`'tan
sonra ekle:

```csharp
        List<CatalogVariantDto> Variants,
        /// <summary>
        /// Ürünün TÜM yayın kodları, emekliler dahil, en yeni başta. Emekliler
        /// bilerek gönderiliyor: izleyici eski yayın videosundaki kodu bugün
        /// yazabilir ve o kod hâlâ aynı ürünü gösteriyor.
        /// </summary>
        List<CatalogBroadcastCodeDto> BroadcastCodes);
```

- [ ] **Adım 4: Sorguya ekle**

`Products` içindeki `.Select(p => new CatalogProductDto(...))` ifadesinin sonuna,
varyant listesinden sonra:

```csharp
                    .ToList(),
                p.BroadcastCodes
                    .OrderByDescending(x => x.CreatedAt)
                    .ThenByDescending(x => x.Id)
                    .Select(x => new CatalogBroadcastCodeDto(
                        x.SellerAxisValue, x.Code, x.CodeNormalized, x.CreatedAt))
                    .ToList()))
```

- [ ] **Adım 5: Testi çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~LicensesWpfCatalogPull
```

Beklenen: PASS.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs \
        OrderDeck.LicenseServer.Tests/Licenses/LicensesWpfCatalogPullTests.cs
git commit -m "$(cat <<'EOF'
feat(katalog): çekme ucu yayın kodlarını emeklileriyle gönderiyor

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 11: Sunucu tarafı doğrulama ve PR

**Files:** yok (doğrulama)

- [ ] **Adım 1: Tüm sunucu testleri**

```bash
cd /c/Users/burak/source/repos/LiveDeck
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: tümü PASS (silinen 3 test dosyası düşer, yeni testler eklenir).

- [ ] **Adım 2: WPF tarafının hâlâ derlendiğini doğrula**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
```

Beklenen: 0 hata, testler PASS. **Bu adım uyum kalkanının sınavı** — WPF'e hiç
dokunulmadığı hâlde katalog çekme sözleşmesi bozulmadıysa yeşil kalır.

- [ ] **Adım 3: Ölü atıf taraması**

```bash
grep -rn "CatalogCodeSequence\|VariantCodeBuilder\|AxisCodeDeriver\|SyncVariantCodes\|next-code" \
  OrderDeck.LicenseServer OrderDeck.LicenseServer.Tests
```

Beklenen: hiç eşleşme.

- [ ] **Adım 4: Kirli dosyaların commit'lere sızmadığını doğrula**

```bash
git status --short
git diff origin/master --stat
```

`git diff --stat` çıktısında `.claude/launch.json`, `.gitignore`, `.codex/`,
`AGENTS.md`, `mutate.py`, `*_wpftmp.csproj` ya da alakasız `docs/` dosyaları
**görünmemeli**. Görünüyorsa `git restore --staged <dosya>` ile geri al ve
commit'i düzelt.

- [ ] **Adım 5: PR aç**

> **Kullanıcı onayı gerekir** — push ve PR paylaşılan duruma dokunur. Bu adımı
> çalıştırmadan önce kullanıcıya sor.

```bash
git push -u origin feat/yayin-kodu-sunucu
gh pr create --title "feat(katalog): yayın kodu modeli — sunucu" --body "$(cat <<'EOF'
## Özet
- `ProductBroadcastCode` tablosu + `/api/panel/products/{id}/broadcast-codes` ucu
- Ürün kodu artık sistem üretimi (`SK00001`), elle girilmiyor
- `VariantCode` / `Axis1Code` / `Axis2Code` ve türeten servisler silindi
- Varyant benzersizliği normalize eksen değerlerine taşındı
- Katalog çekme ucu yayın kodlarını (emekliler dahil) gönderiyor

## Test planı
- [ ] `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
- [ ] `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` (WPF sözleşmesi bozulmadı)
- [ ] Prod göçü: `ProductVariants` boş olduğu için geri doldurma no-op

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Adım 6: Merge + dağıtım**

> **Kullanıcı onayı gerekir.** Merge sonrası sunucu `master`'a otomatik
> dağıtılıyor (`license-server-deploy.yml`). **Panel görevlerine (12–16) bu
> dağıtım tamamlanmadan başlanmamalı** — panel yeni sözleşmeye yazıyor.

---

## Panel (ayrı repo: `C:\Users\burak\source\repos\OrderDeck-Mobile`)

Buradan sonraki bütün yollar **OrderDeck-Mobile** deposuna göredir. Komutlar
deponun kökünden çalıştırılır; panel çalışma alanının adı `@orderdeck/panel`.

### Görev 12: Panel dalı + sunucu sözleşmesi (`api/catalog.ts`)

**Dosyalar:**
- Değiştir: `apps/panel/src/api/catalog.ts`
- Test: `apps/panel/src/api/catalog.test.tsx`

- [ ] **Adım 1: Dalı aç**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile
git fetch origin
git checkout -b feat/yayin-kodu-panel origin/main
npm install
```

> `origin/main` — bu depoda ana dalın adı `master` değil `main`. Yerel `main`
> bayat kalabiliyor (PR #44'te öyle olmuştu); `git fetch` bu yüzden zorunlu.

- [ ] **Adım 2: Sözleşme testlerini yaz (kırmızı)**

`apps/panel/src/api/catalog.test.tsx` — üstteki import bloğunu değiştir:

```tsx
import {
  useBroadcastCodes,
  useCreateVariantsBulk,
  useProducts,
  useReorderProductPhotos,
  useSetBroadcastCode,
  useUploadProductPhoto,
} from "./catalog";
```

Var olan `useCreateVariantsBulk` testinde eksen kodlarını **çıkar** (85. ve 89.
satırlar):

```tsx
    const created = await result.current.mutateAsync({
      productId: "p1",
      items: [{ axis1Value: "Kırmızı", isActive: true }],
    });

    expect(http.post).toHaveBeenCalledWith("/api/panel/products/p1/variants/bulk", {
      items: [{ axis1Value: "Kırmızı", isActive: true }],
    });
```

Dosyanın sonuna iki yeni blok ekle:

```tsx
describe("useBroadcastCodes", () => {
  it("ürünün yayın kodlarını kendi ucundan çeker", async () => {
    http.get.mockResolvedValue({
      data: [{ sellerAxisValue: "Siyah", code: "ATEŞ", createdAt: "2026-08-14T10:00:00Z" }],
    });

    const { result } = renderHook(() => useBroadcastCodes("p1"), { wrapper });

    await waitFor(() => expect(result.current.data).toBeDefined());
    expect(http.get).toHaveBeenCalledWith("/api/panel/products/p1/broadcast-codes");
    expect(result.current.data).toEqual([
      { sellerAxisValue: "Siyah", code: "ATEŞ", createdAt: "2026-08-14T10:00:00Z" },
    ]);
  });
});

describe("useSetBroadcastCode", () => {
  it("kodu PUT eder; satıcı ekseni değeri gövdede gider, adreste değil", async () => {
    // Değer serbest metin (boşluk, eğik çizgi, Türkçe harf içerebilir);
    // adrese koymak kaçış hatasına ve 404'e açık olurdu.
    http.put.mockResolvedValue({ data: null });

    const { result } = renderHook(() => useSetBroadcastCode(), { wrapper });
    await result.current.mutateAsync({ productId: "p1", sellerAxisValue: "Siyah", code: "ateş" });

    expect(http.put).toHaveBeenCalledWith("/api/panel/products/p1/broadcast-codes", {
      sellerAxisValue: "Siyah",
      code: "ateş",
    });
  });

  it("eksensiz üründe satıcı ekseni değerini null gönderir", async () => {
    http.put.mockResolvedValue({ data: null });

    const { result } = renderHook(() => useSetBroadcastCode(), { wrapper });
    await result.current.mutateAsync({ productId: "p1", sellerAxisValue: null, code: "TEK" });

    expect(http.put).toHaveBeenCalledWith("/api/panel/products/p1/broadcast-codes", {
      sellerAxisValue: null,
      code: "TEK",
    });
  });
});
```

- [ ] **Adım 3: Testi koştur, kırmızı olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/api/catalog.test.tsx
```

Beklenen: `useBroadcastCodes`/`useSetBroadcastCode` dışa aktarılmadığı için
başarısız (`does not provide an export named`).

- [ ] **Adım 4: Sözleşmeyi güncelle**

`apps/panel/src/api/catalog.ts` — `Variant` tipinden üç alanı çıkar:

```ts
export type Variant = {
  id: string;
  axis1Value: string | null;
  axis2Value: string | null;
  barcode: string | null;
  isActive: boolean;
};
```

`ProductUpsert`'ten `code` alanını çıkar (sunucunun `UpsertRequest`'inde
karşılığı kalmadı; göndermek sessizce yok sayılırdı — daha kötüsü, panelde
"kodu ben belirliyorum" yanılsaması sürerdi):

```ts
export type ProductUpsert = {
  name: string;
  categoryId?: string | null;
  defaultPrice: number;
  cost?: number | null;
  shelfLocation?: string | null;
  axis1Name?: string | null;
  axis1Role?: AxisRole | null;
  axis2Name?: string | null;
  axis2Role?: AxisRole | null;
};
```

`VariantUpsert`'ten eksen kodlarını çıkar (`isActive` yorumu **aynen kalır**):

```ts
export type VariantUpsert = {
  axis1Value?: string | null;
  axis2Value?: string | null;
  /**
   * ZORUNLU — isteğe bağlı yapma. Sunucudaki VariantRequest.IsActive düz
   * `bool`; alan gövdede yoksa `false` deserialize olur ve varyant PASİF
   * doğar. Tipi zorunlu tutmak bunu derleme zamanında yakalıyor.
   */
  isActive: boolean;
};
```

`useNextProductCode` fonksiyonunu (234–247. satırlar) **tümüyle sil** — sunucuda
`/api/panel/products/next-code` ucu kalmadı.

`useDeleteProduct`'tan sonra, `// ─── Varyantlar ───` başlığından **önce** yeni
bölümü ekle:

```ts
// ─── Yayın kodları ────────────────────────────────────────────────────────
// Yayın kodu ürün + SATICI EKSENİ DEĞERİ seviyesinde ("Elbise + Siyah" → ATEŞ);
// varyant seviyesinde kod yok. Sunucu ekle-only tutuyor: kod değişince yeni
// satır yazılır, eski satır kodu rezerve tutmaya devam eder. Bu uç yalnız
// GÜNCEL olanları döndürüyor.

export type BroadcastCode = {
  /** Satıcı ekseni olmayan üründe null — kod ürünün tamamına ait. */
  sellerAxisValue: string | null;
  code: string;
  createdAt: string;
};

export function useBroadcastCodes(productId: string | null) {
  return useQuery({
    queryKey: ["catalog", "broadcast-codes", productId],
    enabled: productId !== null,
    queryFn: async () => {
      const resp = await apiClient.get<BroadcastCode[]>(
        `/api/panel/products/${productId}/broadcast-codes`,
      );
      return resp.data;
    },
  });
}

export function useSetBroadcastCode() {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (args: {
      productId: string;
      sellerAxisValue: string | null;
      code: string;
    }) => {
      await apiClient.put(`/api/panel/products/${args.productId}/broadcast-codes`, {
        sellerAxisValue: args.sellerAxisValue,
        code: args.code,
      });
    },
    onSuccess: (_d, args) => {
      void qc.invalidateQueries({ queryKey: ["catalog", "broadcast-codes", args.productId] });
    },
  });
}
```

- [ ] **Adım 5: Testi koştur, yeşil olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/api/catalog.test.tsx
```

Beklenen: PASS. `npm run typecheck --workspace=@orderdeck/panel` bu aşamada
**hâlâ kırmızı** — ekranlar silinen alanları kullanıyor, Görev 13–15 onları
temizliyor.

- [ ] **Adım 6: Commit**

```bash
git add apps/panel/src/api/catalog.ts apps/panel/src/api/catalog.test.tsx
git commit -m "$(cat <<'EOF'
feat(panel): yayın kodu uçları + kod alanları sözleşmeden düştü

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Görev 13: Ürün kartında stok kodu salt-okunur

**Dosyalar:**
- Değiştir: `apps/panel/src/screens/UrunScreen.tsx`
- Test: `apps/panel/src/screens/UrunScreen.test.tsx`

- [ ] **Adım 1: Testleri yeni davranışa çevir (kırmızı)**

`UrunScreen.test.tsx` — sahte modülden `useNextProductCode` satırını sil
(22. satır):

```tsx
vi.mock("../api/catalog", async () => {
  const actual = await vi.importActual<typeof import("../api/catalog")>("../api/catalog");
  return {
    AXIS_ROLE: actual.AXIS_ROLE,
    useCategories: () => ({ data: [] }),
    useProduct: () => ({ data: state.product, isLoading: false }),
    useCreateProduct: () => ({ mutateAsync: state.createImpl, isPending: false }),
    useUpdateProduct: () => ({ mutateAsync: state.updateImpl, isPending: false }),
    useDeleteProduct: () => ({ mutateAsync: vi.fn() }),
  };
});
```

"sunucudan gelen sıradaki kodu doldurur" testini (69–73. satırlar) şununla
**değiştir**:

```tsx
  it("yeni üründe kodu istemez, kayıttan sonra atanacağını söyler", () => {
    // Kod artık sistem üretimi (SK00001). Kutu bırakılsaydı operatör kendi
    // kodunu yazar, sunucu sessizce yok sayar ve rafa yanlış kod basılırdı.
    state.role = "owner";
    renderNew();
    expect(screen.getByText("Kaydedince atanır")).toBeInTheDocument();
    expect(screen.queryByRole("textbox", { name: "Stok kodu" })).toBeNull();
  });
```

"sunucudan gelen ürünü forma yazar" testinde kod satırını rozete çevir
(108. satır):

```tsx
    expect(screen.getByText("A3")).toBeInTheDocument();
```

"kaydederken sunucunun beklediği gövdeyi gönderir" testinde `code: "A3",`
satırını (130) **sil**; beklenen gövde:

```tsx
    expect(state.updateImpl).toHaveBeenCalledWith({
      id: "p1",
      body: {
        name: "Kırmızı Elbise",
        categoryId: null,
        defaultPrice: 250,
        cost: 120,
        shelfLocation: "A-3",
        axis1Name: "Renk",
        axis1Role: 1,
        axis2Name: null,
        axis2Role: null,
      },
    });
```

"kaydetme reddedilirse sunucunun gerekçesini yazar" testindeki sahte hatayı
gerçek bir 409'a çevir — artık `duplicate-code` diye bir yanıt yok:

```tsx
  it("kaydetme reddedilirse sunucunun gerekçesini yazar", async () => {
    state.updateImpl = vi.fn(async () => {
      throw {
        response: { data: { title: "axis-in-use", detail: "Eksen varyantlarda kullanılıyor." } },
      };
    });
    renderEdit();
    await userEvent.click(await screen.findByRole("button", { name: "Kaydet" }));

    expect(await screen.findByRole("alert")).toHaveTextContent(
      "Eksen varyantlarda kullanılıyor.",
    );
  });
```

- [ ] **Adım 2: Testi koştur, kırmızı olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/screens/UrunScreen.test.tsx
```

Beklenen: FAIL — ekran hâlâ kod kutusu çiziyor ve gövdede `code` gönderiyor.

- [ ] **Adım 3: Ekranı güncelle**

`UrunScreen.tsx` — import bloğundan `useNextProductCode` satırını çıkar:

```tsx
import {
  AXIS_ROLE,
  useCategories,
  useCreateProduct,
  useDeleteProduct,
  useProduct,
  useUpdateProduct,
  type AxisRole,
  type ProductUpsert,
} from "../api/catalog";
```

57. satırdaki `const { data: nextCode } = useNextProductCode(isNew);` satırını
sil. 87–91. satırdaki "kod önerisi geldiğinde doldur" efektini **tümüyle sil**:

```tsx
  // Yeni üründe kod önerisi geldiğinde, kullanıcı henüz elle bir şey
  // yazmadıysa doldur.
  useEffect(() => {
    if (isNew && nextCode) setForm((f) => (f.code ? f : { ...f, code: nextCode }));
  }, [isNew, nextCode]);
```

`toBody()`'den `code` satırını çıkar:

```tsx
  function toBody(): ProductUpsert {
    return {
      name: form.name.trim(),
      categoryId: form.categoryId || null,
      defaultPrice: Number(form.defaultPrice) || 0,
      // Stok elemanı maliyeti hiç göndermiyor; sunucu zaten yok sayardı ama
      // alanı boş bırakmak "yanlışlıkla sıfırlandı" ihtimalini de kapatıyor.
      cost: showCost ? (form.cost.trim() === "" ? null : Number(form.cost)) : undefined,
      shelfLocation: form.shelfLocation.trim() || null,
      axis1Name: form.axis1Name.trim() || null,
      axis1Role: form.axis1Name.trim() ? form.axis1Role : null,
      axis2Name: form.axis2Name.trim() || null,
      axis2Role: form.axis2Name.trim() ? form.axis2Role : null,
    };
  }
```

Kod kutusunu rozete çevir (174–187. satırlardaki ızgara):

```tsx
        <div className="grid grid-cols-2 gap-3">
          <Field label="Stok kodu">
            {/*
              Salt-okunur: kodu sunucu üretiyor (SK00001) ve bir daha
              değişmiyor. Rafa basılan etiketin ekrandan sapmaması bu kurala
              bağlı — düzenlenebilir bir kutu o güvenceyi sessizce kaldırırdı.
              <span> (<p> değil): Field bir <label>, içine akış içeriği konamaz.
            */}
            <span className="block w-full rounded-xl border border-bg-elevated bg-bg-elevated px-3 py-2 font-mono text-sm text-text-muted">
              {isNew ? "Kaydedince atanır" : form.code}
            </span>
          </Field>
          <Field label="Raf">
            <input
              value={form.shelfLocation}
              onChange={(e) => set("shelfLocation", e.target.value)}
              maxLength={40}
              placeholder="A-3 / Depo 2"
              className={inputClass}
            />
          </Field>
        </div>
```

> `FormState.code` **duruyor**: sunucudan gelen ürünün kodunu göstermek için
> hâlâ gerekiyor. Yalnız yazma yolu kalktı.

- [ ] **Adım 4: Testi koştur, yeşil olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/screens/UrunScreen.test.tsx
```

Beklenen: PASS (11 test).

- [ ] **Adım 5: Commit**

```bash
git add apps/panel/src/screens/UrunScreen.tsx apps/panel/src/screens/UrunScreen.test.tsx
git commit -m "$(cat <<'EOF'
feat(panel): stok kodu salt-okunur rozet oldu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Görev 14: Eksen kod parçalarını arayüzden kaldır

**Dosyalar:**
- Değiştir: `apps/panel/src/lib/combinations.ts`
- Değiştir: `apps/panel/src/components/catalog/VariantSection.tsx`
- Test: `apps/panel/src/lib/combinations.test.ts`
- Test: `apps/panel/src/components/catalog/VariantSection.test.tsx`

- [ ] **Adım 1: `combinations.test.ts`'i yeni sözleşmeye çevir (kırmızı)**

Dosyanın tamamını şununla değiştir:

```ts
import { describe, expect, it } from "vitest";
import { parseValues, buildCombinations } from "./combinations";

describe("parseValues", () => {
  it("virgül ve satır sonuyla ayırır, boşlukları kırpar", () => {
    expect(parseValues(" Kırmızı, Siyah \n Mavi ")).toEqual(["Kırmızı", "Siyah", "Mavi"]);
  });

  it("tekrarları normalleştirerek eler, ilk yazımı korur", () => {
    expect(parseValues("Kırmızı, kırmızı, KIRMIZI")).toEqual(["Kırmızı"]);
  });

  it("boş girdide boş dizi döndürür", () => {
    expect(parseValues("  , , ")).toEqual([]);
  });
});

describe("buildCombinations", () => {
  it("iki eksenin çarpımını üretir, birinci eksen dışta döner", () => {
    const rows = buildCombinations(["Kırmızı", "Siyah"], ["S", "M", "L"], []);
    expect(rows).toHaveLength(6);
    expect(rows.slice(0, 3).map((r) => r.axis2Value)).toEqual(["S", "M", "L"]);
    expect(rows[0]).toEqual({ axis1Value: "Kırmızı", axis2Value: "S" });
  });

  it("tek eksende yalnız birinci değerleri üretir", () => {
    const rows = buildCombinations(["Kırmızı", "Siyah"], [], []);
    expect(rows).toHaveLength(2);
    expect(rows[0].axis2Value).toBeNull();
  });

  it("kartta zaten var olan varyantı önermez", () => {
    const rows = buildCombinations(
      ["Kırmızı", "Siyah"],
      ["S", "M"],
      [{ axis1Value: "kırmızı", axis2Value: "s" }],
    );
    expect(rows).toHaveLength(3);
    expect(rows.some((r) => r.axis1Value === "Kırmızı" && r.axis2Value === "S")).toBe(false);
  });

  it("birinci eksen boşsa hiçbir satır üretmez", () => {
    expect(buildCombinations([], ["S"], [])).toEqual([]);
  });

  it("kod türetilemeyen değeri artık elemez — kod diye bir şey yok", () => {
    // Eskiden "!!!" codeMissing işaretlenip yazılamaz hale geliyordu. Eksen
    // değeri serbest metin; sunucu da yalnız normalleştirip saklıyor.
    expect(buildCombinations(["!!!"], [], [])).toEqual([
      { axis1Value: "!!!", axis2Value: null },
    ]);
  });
});
```

- [ ] **Adım 2: Testi koştur, kırmızı olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/lib/combinations.test.ts
```

Beklenen: FAIL — `deriveAxisCode` importu kalktığı için önce derleme geçer, ama
üretilen satırlar hâlâ `axis1Code`/`axis2Code`/`codeMissing` taşıdığı için
`toEqual` tutmaz.

- [ ] **Adım 3: `combinations.ts`'i sadeleştir**

Dosyanın tamamını şununla değiştir:

```ts
/**
 * Varyant kombinasyon üreteci.
 *
 * Kod ÜRETMİYOR: varyantın kimliği Guid, ekranda eksen değerleriyle görünüyor
 * (`ATEŞ · M`). Eskiden burada sunucudaki AxisCodeDeriver kuralı birebir
 * tekrarlanıyordu; o kural sunucudan kalktığı için tekrar da kalktı.
 *
 * Normalleştirme KALDI: "kırmızı" ile "Kırmızı" aynı varyant, o yüzden zaten
 * kartta olan bir satırı yeniden önermemek için karşılaştırma normalleştirilmiş
 * değerler üstünden yapılıyor — sunucudaki SearchNormalizer ile aynı niyet.
 */

const TR_MAP: Record<string, string> = {
  Ç: "C", Ğ: "G", İ: "I", Ö: "O", Ş: "S", Ü: "U",
  ç: "C", ğ: "G", ı: "I", i: "I", ö: "O", ş: "S", ü: "U",
};

function foldToAscii(s: string): string {
  return s.replace(/[ÇĞİÖŞÜçğıiöşü]/g, (ch) => TR_MAP[ch] ?? ch);
}

/**
 * Karşılaştırma anahtarı. Türkçe büyük harfe çevirme JavaScript'te doğru
 * çalışmaz ("i".toUpperCase() → "I", "İ" beklenirdi); bu yüzden ÖNCE Türkçe
 * harfler ASCII'ye katlanıyor, SONRA büyütülüyor.
 */
function normalizeValue(value: string): string {
  return foldToAscii(value.trim()).toUpperCase();
}

/** Serbest metni değer listesine çevirir; virgül ya da satır sonu ayırır. */
export function parseValues(raw: string): string[] {
  const seen = new Set<string>();
  const out: string[] = [];
  for (const piece of raw.split(/[,\n]/)) {
    const value = piece.trim();
    if (value.length === 0) continue;
    const key = normalizeValue(value);
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(value);
  }
  return out;
}

export type CombinationRow = {
  axis1Value: string;
  axis2Value: string | null;
};

export type ExistingVariant = {
  axis1Value: string | null;
  axis2Value: string | null;
};

function existingKey(a1: string | null, a2: string | null): string {
  return `${normalizeValue(a1 ?? "")}\u0000${normalizeValue(a2 ?? "")}`;
}

/**
 * İki eksenin çarpımını üretir; kartta zaten bulunan satırları atar.
 * İkinci eksen listesi boşsa tek eksenli ürün varsayılır.
 *
 * Sıra: birinci eksen DIŞTA döner (Kırmızı-S, Kırmızı-M, Siyah-S…) — kullanıcı
 * tabloyu renk renk okur, beden beden değil.
 */
export function buildCombinations(
  axis1Values: string[],
  axis2Values: string[],
  existing: ExistingVariant[],
): CombinationRow[] {
  if (axis1Values.length === 0) return [];

  const taken = new Set(existing.map((e) => existingKey(e.axis1Value, e.axis2Value)));
  const second: (string | null)[] = axis2Values.length > 0 ? axis2Values : [null];

  const rows: CombinationRow[] = [];
  for (const v1 of axis1Values) {
    for (const v2 of second) {
      if (taken.has(existingKey(v1, v2))) continue;
      rows.push({ axis1Value: v1, axis2Value: v2 });
    }
  }
  return rows;
}
```

- [ ] **Adım 4: Testi koştur, yeşil olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/lib/combinations.test.ts
```

Beklenen: PASS (9 test).

- [ ] **Adım 5: `VariantSection.test.tsx`'i güncelle (kırmızı)**

`variant()` yardımcısından kod alanlarını çıkar (19–30. satırlar):

```tsx
function variant(p: Partial<Variant> & { id: string }): Variant {
  return {
    axis1Value: null,
    axis2Value: null,
    barcode: null,
    isActive: true,
    ...p,
  };
}
```

"var olan varyantı önermez" testindeki `variantCode: "A1-KIRM-S"` argümanını sil
(49. satır):

```tsx
    const existing = [variant({ id: "v1", axis1Value: "Kırmızı", axis2Value: "S" })];
```

"seçilenleri tek istekte gönderir" testinin beklentisinden kodları çıkar
(70–81. satırlar):

```tsx
    expect(state.bulkImpl).toHaveBeenCalledWith({
      productId: "p1",
      items: [
        {
          axis1Value: "Kırmızı",
          axis2Value: null,
          isActive: true,
        },
      ],
    });
```

Dosyanın sonuna, mevcut varyant listesinin yeni etiketini çivileyen bir test
ekle (son `it(...)` bloğundan sonra, `});` kapanışından önce):

```tsx
  it("mevcut varyantı eksen değerleriyle etiketler, kodla değil", () => {
    // Varyant kodu yok; silme düğmesinin erişilebilir adı da eksen değerinden
    // gelmeli, yoksa ekran okuyucuda iki varyant ayırt edilemez.
    const existing = [variant({ id: "v1", axis1Value: "Kırmızı", axis2Value: "S" })];
    render(<VariantSection productId="p1" axis1Name="Renk" axis2Name="Beden" variants={existing} />);

    expect(screen.getByRole("button", { name: "Kırmızı / S sil" })).toBeInTheDocument();
    expect(screen.getByRole("button", { name: "Kırmızı / S pasifleştir" })).toBeInTheDocument();
  });
```

- [ ] **Adım 6: Testi koştur, kırmızı olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/components/catalog/VariantSection.test.tsx
```

Beklenen: FAIL — düğme adları hâlâ varyant kodundan geliyor (`v1 sil`).

- [ ] **Adım 7: `VariantSection.tsx`'i güncelle**

`selected` süzgecinden `codeMissing`'i kaldır ve gönderiden kodları çıkar
(48 ve 50–73. satırlar):

```tsx
  const selected = proposed.filter((r) => !skipped.has(rowKey(r)));

  async function saveSelected() {
    if (selected.length === 0) return;
    setError(null);
    try {
      await bulk.mutateAsync({
        productId,
        items: selected.map((r) => ({
          axis1Value: r.axis1Value,
          axis2Value: r.axis2Value,
          isActive: true,
        })),
      });
      setRaw1("");
      setRaw2("");
      setSkipped(new Set());
    } catch (e) {
      // Sunucu detail'i hangi satırın çakıştığını yazıyor (duplicate-variant /
      // duplicate-in-batch); burada yeniden yazmak bilgiyi KAYBETTİRİR.
      setError(problemMessage(e, "Varyantlar yazılamadı; hiçbiri kaydedilmedi."));
    }
  }
```

Öneri listesindeki kod sütununu kaldır (100–117. satırlar):

```tsx
            {proposed.map((r) => {
              const key = rowKey(r);
              return (
                <li key={key} className="flex items-center gap-2 px-3 py-1.5 text-sm">
                  <input
                    type="checkbox"
                    checked={!skipped.has(key)}
                    onChange={() => toggle(r)}
                    aria-label={describe(r)}
                  />
                  <span className="flex-1 text-text">{describe(r)}</span>
                </li>
              );
            })}
```

`ExistingVariants` içinde kod rozetini ve kodla kurulan `aria-label`'ları eksen
değerlerine çevir (196–231. satırlar):

```tsx
      {variants.map((v) => {
        const label = describeVariant(v);
        return (
          <li key={v.id} className="flex items-center gap-2 px-3 py-2 text-sm">
            <span className={`flex-1 ${v.isActive ? "text-text" : "text-text-muted line-through"}`}>
              {label}
            </span>
            <button
              type="button"
              aria-label={`${label} ${v.isActive ? "pasifleştir" : "aktifleştir"}`}
              onClick={() =>
                update.mutate({
                  productId,
                  id: v.id,
                  body: {
                    axis1Value: v.axis1Value,
                    axis2Value: v.axis2Value,
                    isActive: !v.isActive,
                  },
                })
              }
              className="rounded px-2 py-0.5 text-xs text-text-muted hover:text-accent"
            >
              {v.isActive ? "Pasifleştir" : "Aktifleştir"}
            </button>
            <button
              type="button"
              aria-label={`${label} sil`}
              onClick={() => remove.mutate({ productId, id: v.id })}
              className="p-1 text-text-muted hover:text-danger"
            >
              <Trash2 size={14} />
            </button>
          </li>
        );
      })}
```

Dosyanın sonundaki yardımcıları güncelle — `codeOf` **silinir**, yerine varyant
etiketleyici gelir (236–246. satırlar):

```tsx
function rowKey(r: CombinationRow): string {
  return `${r.axis1Value}\u0000${r.axis2Value ?? ""}`;
}

function describe(r: CombinationRow): string {
  return [r.axis1Value, r.axis2Value].filter(Boolean).join(" / ");
}

/** Eksensiz varyantta gösterilecek bir değer yok; tire çizilir. */
function describeVariant(v: Variant): string {
  return [v.axis1Value, v.axis2Value].filter(Boolean).join(" / ") || "—";
}
```

- [ ] **Adım 8: Testi koştur, yeşil olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/components/catalog/VariantSection.test.tsx
```

Beklenen: PASS (7 test).

- [ ] **Adım 9: Commit**

```bash
git add apps/panel/src/lib/combinations.ts apps/panel/src/lib/combinations.test.ts \
        apps/panel/src/components/catalog/VariantSection.tsx \
        apps/panel/src/components/catalog/VariantSection.test.tsx
git commit -m "$(cat <<'EOF'
refactor(panel): eksen kod parçaları arayüzden kalktı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Görev 15: Stok ekranında yayın kodu kutuları

Spec'in istediği yerleşim:

```
Elbise                         stok kodu: SK00001
  Siyah   yayın kodu: [ATEŞ ]     S[5]  M[8]  L[3]
  Mavi    yayın kodu: [DENİZ]     S[4]  M[6]  L[2]
```

**Dosyalar:**
- Oluştur: `apps/panel/src/components/stock/BroadcastCodeSection.tsx`
- Oluştur: `apps/panel/src/components/stock/BroadcastCodeSection.test.tsx`
- Değiştir: `apps/panel/src/screens/StokUrunScreen.tsx`
- Test: `apps/panel/src/screens/StokUrunScreen.test.tsx`

> Neden ayrı dosya: satıcı eksenini bulma, değerleri tekilleştirme, kod çekme ve
> yazma birlikte ~130 satır. `StokUrunScreen` zaten bakiye toplama + kip
> yönetimi + hareket listesi taşıyor; kutuları oraya koymak ekranı iki işli
> yapardı. Ayrıca `StokUrunScreen`'in erken `return`'lerinden ÖNCE yeni bir
> kanca (`useBroadcastCodes`) çağırmak gerekirdi — çocuk bileşen o kanca-sırası
> tuzağını da kapatıyor.

- [ ] **Adım 1: Bileşen testini yaz (kırmızı)**

`apps/panel/src/components/stock/BroadcastCodeSection.test.tsx`:

```tsx
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { Product } from "../../api/catalog";
import { BroadcastCodeSection } from "./BroadcastCodeSection";

const state = vi.hoisted(() => ({
  codes: [] as { sellerAxisValue: string | null; code: string; createdAt: string }[],
  setImpl: vi.fn(async (_a: unknown) => {}),
}));

vi.mock("../../api/catalog", async () => {
  const actual = await vi.importActual<typeof import("../../api/catalog")>("../../api/catalog");
  return {
    AXIS_ROLE: actual.AXIS_ROLE,
    useBroadcastCodes: () => ({ data: state.codes }),
    useSetBroadcastCode: () => ({ mutateAsync: state.setImpl, isPending: false }),
  };
});

function product(over: Partial<Product> = {}): Product {
  return {
    id: "p1",
    categoryId: null,
    code: "SK00001",
    name: "Elbise",
    defaultPrice: 250,
    cost: null,
    shelfLocation: null,
    axis1Name: "Renk",
    axis1Role: 1,
    axis2Name: "Beden",
    axis2Role: 2,
    isArchived: false,
    createdAt: "2026-08-01T10:00:00Z",
    updatedAt: "2026-08-14T10:00:00Z",
    variants: [
      { id: "v1", axis1Value: "Siyah", axis2Value: "S", barcode: null, isActive: true },
      { id: "v2", axis1Value: "Siyah", axis2Value: "M", barcode: null, isActive: true },
      { id: "v3", axis1Value: "Mavi", axis2Value: "S", barcode: null, isActive: true },
    ],
    photos: [],
    ...over,
  };
}

beforeEach(() => {
  state.codes = [];
  state.setImpl = vi.fn(async () => {});
});

describe("BroadcastCodeSection", () => {
  it("satıcı ekseni değeri başına BİR kutu açar, varyant başına değil", () => {
    // Üç varyant var ama satıcı ekseni (Renk) iki değer taşıyor: Siyah, Mavi.
    render(<BroadcastCodeSection product={product()} />);

    expect(screen.getByLabelText("Siyah yayın kodu")).toBeInTheDocument();
    expect(screen.getByLabelText("Mavi yayın kodu")).toBeInTheDocument();
    expect(screen.getAllByRole("textbox")).toHaveLength(2);
  });

  it("aynı değerin farklı yazımını tek kutuda toplar", () => {
    const p = product({
      variants: [
        { id: "v1", axis1Value: "Siyah", axis2Value: "S", barcode: null, isActive: true },
        { id: "v2", axis1Value: "SİYAH", axis2Value: "M", barcode: null, isActive: true },
      ],
    });
    render(<BroadcastCodeSection product={p} />);

    expect(screen.getAllByRole("textbox")).toHaveLength(1);
  });

  it("sunucudaki güncel kodu kutuya yazar", () => {
    state.codes = [
      { sellerAxisValue: "Siyah", code: "ATEŞ", createdAt: "2026-08-14T10:00:00Z" },
    ];
    render(<BroadcastCodeSection product={product()} />);

    expect(screen.getByLabelText("Siyah yayın kodu")).toHaveValue("ATEŞ");
    expect(screen.getByLabelText("Mavi yayın kodu")).toHaveValue("");
  });

  it("kaydedince kodu satıcı ekseni değeriyle birlikte gönderir", async () => {
    render(<BroadcastCodeSection product={product()} />);

    await userEvent.type(screen.getByLabelText("Siyah yayın kodu"), "ateş");
    await userEvent.click(screen.getByRole("button", { name: "Siyah yayın kodunu kaydet" }));

    expect(state.setImpl).toHaveBeenCalledWith({
      productId: "p1",
      sellerAxisValue: "Siyah",
      code: "ateş",
    });
  });

  it("çakışmada sunucunun gerekçesini olduğu gibi gösterir", async () => {
    state.setImpl = vi.fn(async () => {
      throw {
        response: {
          data: { title: "code-taken", detail: "Bu yayın kodu daha önce kullanılmış." },
        },
      };
    });
    render(<BroadcastCodeSection product={product()} />);

    await userEvent.type(screen.getByLabelText("Siyah yayın kodu"), "ATEŞ");
    await userEvent.click(screen.getByRole("button", { name: "Siyah yayın kodunu kaydet" }));

    expect(await screen.findByText("Bu yayın kodu daha önce kullanılmış.")).toBeInTheDocument();
  });

  it("satıcı ekseni olmayan üründe tek kutu açar ve değeri null gönderir", async () => {
    const p = product({
      axis1Name: "Beden",
      axis1Role: 2,
      axis2Name: null,
      axis2Role: null,
      variants: [{ id: "v1", axis1Value: "M", axis2Value: null, barcode: null, isActive: true }],
    });
    render(<BroadcastCodeSection product={p} />);

    await userEvent.type(screen.getByLabelText("Yayın kodu"), "TEK");
    await userEvent.click(screen.getByRole("button", { name: "Yayın kodunu kaydet" }));

    expect(state.setImpl).toHaveBeenCalledWith({
      productId: "p1",
      sellerAxisValue: null,
      code: "TEK",
    });
  });

  it("boş kodu göndermez — kod zorunlu değil, ama boş kayıt da anlamsız", async () => {
    render(<BroadcastCodeSection product={product()} />);

    expect(screen.getByRole("button", { name: "Siyah yayın kodunu kaydet" })).toBeDisabled();
    expect(state.setImpl).not.toHaveBeenCalled();
  });

  it("satıcı ekseni var ama varyant yoksa kutu yerine yönlendirme yazar", () => {
    render(<BroadcastCodeSection product={product({ variants: [] })} />);

    expect(screen.queryAllByRole("textbox")).toHaveLength(0);
    expect(screen.getByText(/Varyant eklenince/)).toBeInTheDocument();
  });
});
```

- [ ] **Adım 2: Testi koştur, kırmızı olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/components/stock/BroadcastCodeSection.test.tsx
```

Beklenen: FAIL — `Failed to resolve import "./BroadcastCodeSection"`.

- [ ] **Adım 3: Bileşeni yaz**

`apps/panel/src/components/stock/BroadcastCodeSection.tsx`:

```tsx
import { useMemo, useState } from "react";
import {
  AXIS_ROLE,
  useBroadcastCodes,
  useSetBroadcastCode,
  type Product,
} from "../../api/catalog";
import { problemMessage } from "../../lib/apiError";

/**
 * Yayın kodu kutuları: **satıcı ekseni değeri başına bir kutu**.
 *
 * Neden varyant başına değil: yayın kodu ürün + satıcı ekseni değerini çözer
 * ("ATEŞ" → Elbise · Siyah); izleyici ekseni (beden) yorumdan gelir. Varyant
 * başına kod vermek, yayıncının kaçındığı `ATEŞ-SİYAH-M` birleşik koduna geri
 * dönüş olurdu.
 *
 * Kod ZORUNLU DEĞİL: depoya mal girip henüz yayına çıkarmamak meşru. Kodu
 * olmayan ürün yalnız canlıda çağrılamaz.
 */
type Props = { product: Product };

/** Karşılaştırma anahtarı; "SİYAH" ile "Siyah" aynı satıcı ekseni değeri. */
function normalize(value: string): string {
  return value.trim().toLocaleUpperCase("tr");
}

/** Rolü "satıcı" olan eksenin sırası; yoksa 0 (kod ürünün tamamına ait). */
function sellerAxisOf(p: Product): 0 | 1 | 2 {
  if (p.axis1Name && p.axis1Role === AXIS_ROLE.seller) return 1;
  if (p.axis2Name && p.axis2Role === AXIS_ROLE.seller) return 2;
  return 0;
}

export function BroadcastCodeSection({ product }: Props) {
  const { data: codes = [] } = useBroadcastCodes(product.id);

  // Değerler varyantlardan toplanıyor: satıcı ekseninin hangi değerlerinin
  // GERÇEKTEN var olduğunu yalnız varyant satırları biliyor. Eksen ADI
  // ("Renk") değerleri söylemez.
  const values = useMemo<(string | null)[]>(() => {
    const axis = sellerAxisOf(product);
    if (axis === 0) return [null];

    const seen = new Set<string>();
    const out: string[] = [];
    for (const v of product.variants) {
      const raw = (axis === 1 ? v.axis1Value : v.axis2Value) ?? "";
      if (raw.trim().length === 0) continue;
      const key = normalize(raw);
      if (seen.has(key)) continue;
      seen.add(key);
      out.push(raw);
    }
    return out;
  }, [product]);

  const current = useMemo(() => {
    const m = new Map<string, string>();
    for (const c of codes) m.set(normalize(c.sellerAxisValue ?? ""), c.code);
    return m;
  }, [codes]);

  if (values.length === 0) {
    return (
      <p className="px-1 py-2 text-xs text-text-muted">
        Varyant eklenince satıcı ekseni değeri başına yayın kodu kutusu açılır.
      </p>
    );
  }

  return (
    <div className="divide-y divide-bg-elevated">
      {values.map((value) => (
        <CodeBox
          key={value ?? ""}
          productId={product.id}
          sellerAxisValue={value}
          initial={current.get(normalize(value ?? "")) ?? ""}
        />
      ))}
    </div>
  );
}

function CodeBox({
  productId,
  sellerAxisValue,
  initial,
}: {
  productId: string;
  sellerAxisValue: string | null;
  initial: string;
}) {
  const [value, setValue] = useState(initial);
  const [error, setError] = useState<string | null>(null);
  const save = useSetBroadcastCode();

  // Satıcı ekseni yoksa önek de yok; etiket sade "Yayın kodu" olur.
  const label = sellerAxisValue ? `${sellerAxisValue} yayın kodu` : "Yayın kodu";

  async function submit() {
    const code = value.trim();
    if (code.length === 0) return;
    setError(null);
    try {
      await save.mutateAsync({ productId, sellerAxisValue, code });
    } catch (e) {
      // Sunucu çakışmada "Bu yayın kodu daha önce kullanılmış." diyor; burada
      // yeniden yazmak hangi kuralın çiğnendiği bilgisini kaybettirir.
      setError(problemMessage(e, "Yayın kodu kaydedilemedi."));
    }
  }

  return (
    <div className="py-2">
      <div className="flex items-center gap-2">
        {sellerAxisValue && (
          <span className="w-24 shrink-0 truncate text-sm text-text">{sellerAxisValue}</span>
        )}
        <input
          type="text"
          value={value}
          maxLength={32}
          onChange={(e) => setValue(e.target.value)}
          aria-label={label}
          placeholder="ATEŞ"
          className="min-w-0 flex-1 rounded-xl border border-bg-elevated bg-bg px-3 py-1.5 text-sm text-text placeholder:text-text-muted"
        />
        <button
          type="button"
          onClick={submit}
          disabled={value.trim().length === 0 || save.isPending}
          aria-label={`${label}nu kaydet`}
          className="shrink-0 rounded-lg bg-accent px-3 py-1.5 text-xs font-medium text-white disabled:opacity-50"
        >
          Kaydet
        </button>
      </div>
      {error && <p role="alert" className="mt-1 text-xs text-danger">{error}</p>}
    </div>
  );
}
```

> `maxLength={32}` sunucudaki `CatalogLimits.BroadcastCode` ile aynı sayı.
> Sunucu zaten reddediyor; kutuyu sınırlamak reddi kullanıcı yazmadan önce
> engelliyor.
>
> `aria-label={`${label}nu kaydet`}` — `label` zaten "Siyah yayın kodu" ya da
> "Yayın kodu"; sonuna `nu kaydet` eklenince testlerin aradığı iki ad da çıkıyor
> ("Siyah yayın kodunu kaydet" / "Yayın kodunu kaydet").

- [ ] **Adım 4: Testi koştur, yeşil olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/components/stock/BroadcastCodeSection.test.tsx
```

Beklenen: PASS (8 test).

- [ ] **Adım 5: `StokUrunScreen.test.tsx`'i güncelle (kırmızı)**

`variant()` yardımcısından kod alanlarını çıkar (43–54. satırlar):

```tsx
function variant(id: string, axis1: string) {
  return {
    id,
    axis1Value: axis1,
    axis2Value: null,
    barcode: null,
    isActive: true,
  };
}
```

Yeni bölümün ekrana bağlandığını kanıtlayan taklidi `StockMovementList`
sahtesinin hemen altına koy (39. satırdan sonra):

```tsx
vi.mock("../components/stock/BroadcastCodeSection", () => ({
  BroadcastCodeSection: ({ product }: { product: { id: string } }) => (
    <div data-testid="yayin-kodlari">{product.id}</div>
  ),
}));
```

`describe` bloğunun sonuna test ekle:

```tsx
  it("yayın kodu bölümünü ürünle birlikte çizer", () => {
    setup();
    expect(screen.getByTestId("yayin-kodlari")).toHaveTextContent("p1");
  });
```

- [ ] **Adım 6: Testi koştur, kırmızı olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/screens/StokUrunScreen.test.tsx
```

Beklenen: FAIL — `Unable to find an element by: [data-testid="yayin-kodlari"]`.

- [ ] **Adım 7: `StokUrunScreen.tsx`'i güncelle**

Import bloğuna yeni bölümü ekle ve `variantLabel`'ı kod bağımlılığından kurtar
(1–14. satırlar):

```tsx
import { useMemo, useState } from "react";
import { Link, useParams } from "react-router-dom";
import { ArrowLeft } from "lucide-react";
import { useProduct, type Variant } from "../api/catalog";
import { useStockBalances } from "../api/stock";
import { stockBalanceTone } from "../lib/stockTone";
import { BroadcastCodeSection } from "../components/stock/BroadcastCodeSection";
import { StockEntryForm } from "../components/stock/StockEntryForm";
import { StockMovementList } from "../components/stock/StockMovementList";

/**
 * "M · Kırmızı" — iki eksen de boşsa `fallback`'e düşer (çağıran, ürünün stok
 * kodunu veriyor). Eskiden varyant koduna düşüyordu; varyant kodu diye bir şey
 * kalmadı.
 */
function variantLabel(v: Variant, fallback: string): string {
  const parts = [v.axis1Value, v.axis2Value].filter(Boolean) as string[];
  return parts.length > 0 ? parts.join(" · ") : fallback;
}
```

`variantLabels` memo'sunu güncelle (50–54. satırlar):

```tsx
  const variantLabels = useMemo(() => {
    const m: Record<string, string> = {};
    for (const v of product?.variants ?? []) m[v.id] = variantLabel(v, product?.code ?? "—");
    return m;
  }, [product]);
```

Satır listesindeki çağrıyı güncelle (134. satır):

```tsx
              label={variantLabel(v, product.code)}
```

Kip seçicinin **üstüne**, `<div className="px-3">`'in ilk çocuğu olarak yayın
kodu bölümünü ekle (82. satırdan hemen sonra):

```tsx
        <section className="mb-3 rounded-xl border border-bg-elevated bg-bg-surface px-3 py-1">
          <h2 className="pt-1 text-[11px] font-semibold uppercase tracking-[0.08em] text-text-muted">
            Yayın kodu
          </h2>
          <BroadcastCodeSection product={product} />
        </section>
```

- [ ] **Adım 8: Testi koştur, yeşil olduğunu gör**

```bash
npm run test --workspace=@orderdeck/panel -- src/screens/StokUrunScreen.test.tsx
```

Beklenen: PASS (8 test).

- [ ] **Adım 9: Commit**

```bash
git add apps/panel/src/components/stock/BroadcastCodeSection.tsx \
        apps/panel/src/components/stock/BroadcastCodeSection.test.tsx \
        apps/panel/src/screens/StokUrunScreen.tsx \
        apps/panel/src/screens/StokUrunScreen.test.tsx
git commit -m "$(cat <<'EOF'
feat(panel): stok ekranında satıcı ekseni başına yayın kodu kutusu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

### Görev 16: Panel doğrulaması + PR

**Dosyalar:** yok (yalnız doğrulama).

- [ ] **Adım 1: Ölü atıf taraması**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile
grep -rn "axis1Code\|axis2Code\|variantCode\|codeMissing\|deriveAxisCode\|useNextProductCode\|next-code" apps/panel/src
```

Beklenen: **hiç eşleşme yok**. Çıkarsa o dosya Görev 12–15'te atlanmış demektir;
düzelt ve ilgili göreve dön.

- [ ] **Adım 2: Bütün panel testleri**

```bash
npm run test --workspace=@orderdeck/panel
```

Beklenen: tamamı PASS.

- [ ] **Adım 3: Tip denetimi ve derleme**

```bash
npm run typecheck --workspace=@orderdeck/panel
npm run build --workspace=@orderdeck/panel
```

Beklenen: 0 hata. `Property 'variantCode' does not exist on type 'Variant'`
çıkarsa Adım 1'in taraması kaçırmış demektir (örneğin şablon dizesi içinde) —
düzelt.

- [ ] **Adım 4: Lint**

```bash
npm run lint
```

Beklenen: 0 hata. `foldToAscii`/`TR_MAP` **kullanımda kalıyor**
(`normalizeValue` çağırıyor); `no-unused-vars` uyarısı çıkarsa `combinations.ts`
sadeleştirmesi eksik yapılmış demektir.

- [ ] **Adım 5: Diff hijyeni**

```bash
git status
git diff origin/main --stat
```

Beklenen dosya listesi tam olarak şu 12 dosya (10 değişen + 2 yeni):

```
apps/panel/src/api/catalog.ts
apps/panel/src/api/catalog.test.tsx
apps/panel/src/components/catalog/VariantSection.tsx
apps/panel/src/components/catalog/VariantSection.test.tsx
apps/panel/src/components/stock/BroadcastCodeSection.tsx
apps/panel/src/components/stock/BroadcastCodeSection.test.tsx
apps/panel/src/lib/combinations.ts
apps/panel/src/lib/combinations.test.ts
apps/panel/src/screens/StokUrunScreen.tsx
apps/panel/src/screens/StokUrunScreen.test.tsx
apps/panel/src/screens/UrunScreen.tsx
apps/panel/src/screens/UrunScreen.test.tsx
```

Başka bir şey varsa yanlışlıkla eklenmiştir; `git restore --staged <dosya>` ile
çıkar.

- [ ] **Adım 6: Push + PR**

> **Kullanıcı onayı gerekir** — push ve PR paylaşılan duruma dokunur.

```bash
git push -u origin feat/yayin-kodu-panel
gh pr create --title "feat(panel): yayın kodu kutuları + kod alanı temizliği" --body "$(cat <<'EOF'
## Özet
- Ürün kartında stok kodu salt-okunur rozet (`SK00001`), düzenleme alanı kalktı
- Stok ekranında **satıcı ekseni değeri başına** yayın kodu kutusu
- Eksen kod parçaları (`axis1Code`/`axis2Code`) ve `variantCode` arayüzden düştü
- `useNextProductCode` silindi (sunucudaki uç kalmadı)

> Sunucu tarafı ÖNCE merge edilip dağıtılmalı: LiveDeck `feat/yayin-kodu-sunucu`.

## Test planı
- [ ] `npm run test --workspace=@orderdeck/panel`
- [ ] `npm run typecheck --workspace=@orderdeck/panel`
- [ ] `npm run build --workspace=@orderdeck/panel`
- [ ] `npm run lint`
- [ ] Elle: iki eksenli üründe Siyah/Mavi için ayrı kod kaydı; aynı kodu ikinci
      ürüne vermeye çalışınca *"Bu yayın kodu daha önce kullanılmış."*

🤖 Generated with [Claude Code](https://claude.com/claude-code)
EOF
)"
```

- [ ] **Adım 7: Elle doğrulama (kullanıcı)**

1. Panelde bir ürün aç → **Stok kodu** kutusu yok, `SK…` rozeti var.
2. Yeni ürün → "Kaydedince atanır"; kaydet → kod `SK00001`'den sırayla artıyor.
3. Varyant ekle → kod sütunu yok, "Kırmızı / S" satırı var.
4. Stok ekranı → `Siyah` ve `Mavi` için ayrı kod kutusu; birine `ATEŞ` yaz,
   kaydet, sayfayı tazele → kutuda `ATEŞ` duruyor.
5. Başka bir ürünün kutusuna `ateş` yaz → *"Bu yayın kodu daha önce
   kullanılmış."*
6. `Siyah` değerini `Siyah 2` olarak yeniden adlandır (ürün kartından varyantı
   düzenle) → stok ekranındaki kutu `Siyah 2` başlığıyla **`ATEŞ`'i koruyor**
   (Görev 9'un taşıması).

---

## Bitince

- [ ] `MEMORY.md` 8. satırı ve `project_stok_sistemi.md` güncellensin: plan 1/3
      (sunucu + panel kod modeli) bitti, sırada plan 2/3 = WPF eşleştirme +
      varyant çekmecesi.
- [ ] Plan 2/3 yazılırken **`LicensesWpfCatalogPullController`'daki uyum
      yaması** (`VariantCode = p.Code`, eksen kodları `null`) kaldırılacak —
      Görev 6'da bilerek bırakıldı.
