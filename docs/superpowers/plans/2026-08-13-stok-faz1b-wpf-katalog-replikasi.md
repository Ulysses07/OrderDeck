# Faz 1b — WPF Katalog Replikası Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** WPF'teki yerel `Product`/`ProductSize` tablolarını düşürüp yerlerine sunucu kataloğunun salt-okunur bir replikasını koymak; ürün kartı artık panelde tanımlanan ürünü (kapak fotoğrafı, ad, varyantlar) gösteriyor.

**Architecture:** Sunucu tarafında iki küçük ekleme — katalog çekme yükü kapak fotoğrafını taşıyor ve kategori listesi için ayrı bir uç açılıyor. WPF tarafında SQLite'a üç replika tablosu (`CatalogProduct`, `CatalogVariant`, `CatalogCategory`) geliyor; bir arka plan servisi katalogun **tam anlık görüntüsünü** sayfa sayfa çekip tek transaction'da baştan yazıyor. Fotoğraf baytları R2'den presigned URL ile çekilip nesne anahtarına göre diskte önbelleğe alınıyor. Ürün kartı **salt-okunur** hâle geliyor: tanımlama artık panelde.

**Tech Stack:** ASP.NET Core 10 + EF Core (sunucu) · WPF + CommunityToolkit.Mvvm · Dapper + SQLite (yerel) · Cloudflare R2 presigned URL · xUnit + FluentAssertions + Moq

---

## Bağlam — bu planın neden böyle olduğu

**Katalog çekme TAM ANLIK GÖRÜNTÜ, artımlı değil.** `after` bir *değişim* imleci
değil, birincil anahtar `Id` üstünde keyset sayfalama imleci. Panelden silinen
ya da arşivlenen ürün yanıtta hiç görünmez, mezar taşı da yoktur. Bu yüzden
replika **birleştirme değil, baştan yazma** yapmak zorunda: yarım kalmış bir
çekme replikaya asla yazılmaz, tam çekme bittiğinde tek transaction'da
`DELETE` + `INSERT`.

**Ürün kodu kimliktir, serbest metin değil.** Sunucu `Product.Code`'u yazma
anında `SearchNormalizer.Normalize` ile kanonikleştiriyor (PR #261): büyük
harf + Türkçe harfler ASCII'ye katlanmış + boşluklar sadeleşmiş. Operatör
hero'daki kod kutusuna `güzel elbise` yazdığında replika `GUZEL ELBISE`
satırını bulmalı → replikada ayrı bir `CodeNormalized` kolonu tutulur ve
aranan iğne de aynı fonksiyondan geçirilir. `OrderDeck.Core` zaten
`OrderDeck.Shared`'a referans veriyor (`OrderDeck.Core.csproj:20`), yani
`SearchNormalizer` doğrudan kullanılabilir.

**`CodeNormalized` indeksi UNIQUE DEĞİL.** Sunucudaki benzersizlik
`(LicenseId, Code)` üstünde ve `Code` zaten kanonik olduğu için çakışma
teorik olarak imkânsız. Ama replika, kendisine verilen veriyi **reddetmemeli**:
unique indeks olsaydı beklenmedik bir çakışma bütün senkron transaction'ını
düşürür ve replika sessizce eskimeye başlardı. Çakışmada aramanın ilk satırı
alması, senkronun tamamen durmasından iyidir.

**Fotoğraf: kapak, satır içinde, presigned.** Sunucuya ayrı bir fotoğraf ucu
eklenmiyor; `CatalogProductDto`'ya `CoverPhotoKey` + `CoverPhotoUrl` alanları
giriyor. Gerekçe: WPF kartı **tek** fotoğraf gösteriyor (galeri panelde),
presigned imzalama yerel HMAC (ağ çağrısı değil), ve panel tarafında zaten
birebir aynı desen var (`PanelProductPhotoController.ToDtoAsync`). URL 5 dakika
geçerli — indirme çekme döngüsünün hemen ardından yapıldığı için bu bol bol
yetiyor. Anahtar (`CoverPhotoKey`) önbellek anahtarı olarak kalıcı; URL kalıcı
değil, saklanmıyor.

**Fotoğraf indirmesi AYRI bir HttpClient ister.** `LicenseApiClient`'ın
HttpClient'ı her isteğe `Authorization: Bearer` ekliyor; presigned bir R2
URL'sine fazladan `Authorization` başlığı gitmesi isteği bozar. Bu yüzden
kimliksiz, adlandırılmış bir `catalog-photos` client'ı kurulacak.

**Yerel tablolar taşınmadan düşürülüyor.** Kullanıcı kararı (2026-08-13):
`Product`/`ProductSize` sahada boş, veri taşıma yapılmayacak, sayım/uyarı da
eklenmeyecek. Kart artık salt-okunur olduğu için bu tabloların tek yazarı
(`ProductCardViewModel.SaveCommand`) da aynı planda kalkıyor.

**Kapsam dışı (sonraki iki plan):** yorumdan varyant eşleştirme
(`AxisValueMatcher`, varyant seçici çekmece, `Label`'ın `ProductId`/
`ProductVariantId` taşıması, `CatalogAware = true` gönderimi) **plan 2**;
stok bakiyelerinin WPF'te gösterimi (`LicensesWpfStockPullController`)
**plan 3**. Bu planda kartta **adet gösterilmiyor** — eski `Quantity` düz bir
sayıydı ve hareket defteriyle ilgisi yoktu; gerçek bakiye plan 3'te geliyor.

---

## Dosya yapısı

**Sunucu (önce merge olmalı — WPF onsuz boş katalog görür):**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs` | DTO'ya kapak fotoğrafı alanları; yeni `categories` ucu |
| `OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs` | İkisinin de testleri |

**Yerel şema ve replika deposu:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Core/Storage/Migrations/025_catalog_replica.sql` | *(yeni)* Eski tabloları düşür, üç replika tablosunu kur |
| `OrderDeck.Core/Catalog/CatalogReplica.cs` | *(yeni)* `CatalogProduct`, `CatalogVariant`, `CatalogCategory` kayıtları |
| `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs` | *(yeni)* Tek transaction'da baştan yazma + kodla arama |
| `OrderDeck.Core/Catalog/Product.cs` | *(silinir)* Eski yerel kayıtlar |
| `OrderDeck.Core/Storage/Repositories/ProductRepository.cs` | *(silinir)* |

**Sunucudan çekme:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs` | *(yeni)* Tel modelleri |
| `OrderDeck.Licensing/Api/LicenseApiClient.cs` | İki yeni çekme metodu |
| `OrderDeck.App/Services/CatalogPhotoCache.cs` | *(yeni)* Nesne anahtarına göre disk önbelleği |
| `OrderDeck.App/Services/Sync/CatalogSyncService.cs` | *(yeni)* Tam çekme + baştan yazma + fotoğraf indirme |
| `OrderDeck.App/Services/Sync/CatalogSyncHostedService.cs` | *(yeni)* 5 dk periyot, açılışta ilk koşu |
| `OrderDeck.App/Services/ProductPhotoStore.cs` | *(silinir)* |

**Sunum:**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.App/ViewModels/ProductCardViewModel.cs` | Salt-okunur hâle gelir |
| `OrderDeck.App/ViewModels/ProductSizeViewModel.cs` | *(silinir)* → `CatalogVariantViewModel` |
| `OrderDeck.App/ViewModels/CatalogVariantViewModel.cs` | *(yeni)* Tek varyant satırı |
| `OrderDeck.App/Views/Shell/ProductCard.xaml(.cs)` | Düzenleme formu ve fotoğraf seçici kalkar |
| `OrderDeck.App/AppHost.cs` | DI kayıtları |

---

### Task 1: Sunucu — katalog yükü kapak fotoğrafını taşısın

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs`

- [ ] **Step 1: Testi yaz (başarısız olacak)**

`LicensesWpfCatalogPullControllerTests.cs` içindeki `SeedAsync` yardımcısına
fotoğraf tohumlama seçeneği ekle. Mevcut `SeedAsync` imzasını şuna çevir:

```csharp
    private async Task<(HttpClient Client, Guid LicenseId)> SeedAsync(
        int productCount, bool archiveFirst = false, bool withPhotos = false)
```

ve `db.SaveChangesAsync()` çağrısından **önce**, ürün döngüsünün içine — yani
`db.ProductVariants.Add(...)` bloğunun hemen ardına — şunu ekle:

```csharp
            if (withPhotos)
            {
                // Kapak = en KÜÇÜK SortOrder. İki fotoğrafı bilerek ters sırada
                // ekliyoruz ki "ilk eklenen" ile "kapak" karışırsa test yakalasın.
                db.ProductPhotos.Add(new ProductPhoto
                {
                    Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                    ObjectKey = $"{license.Id:N}/products/{product.Id:N}/ikinci.img",
                    ContentType = "image/jpeg", SizeBytes = 2048, SortOrder = 1,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                db.ProductPhotos.Add(new ProductPhoto
                {
                    Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                    ObjectKey = $"{license.Id:N}/products/{product.Id:N}/kapak.img",
                    ContentType = "image/jpeg", SizeBytes = 1024, SortOrder = 0,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            }
```

Sonra dosyanın sonuna iki test ekle:

```csharp
    [Fact]
    public async Task Cover_photo_is_the_smallest_sort_order()
    {
        var (client, licenseId) = await SeedAsync(1, withPhotos: true);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        var key = rows![0].GetProperty("coverPhotoKey").GetString();
        key.Should().EndWith("/kapak.img");

        // URL imzalı ve kısa ömürlü; sözleşme "anahtarı içeren bir adres dönüyor".
        rows[0].GetProperty("coverPhotoUrl").GetString().Should().Contain(key!);
    }

    [Fact]
    public async Task Product_without_photos_reports_null_cover()
    {
        var (client, licenseId) = await SeedAsync(1);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows![0].GetProperty("coverPhotoKey").ValueKind.Should().Be(JsonValueKind.Null);
        rows[0].GetProperty("coverPhotoUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }
```

- [ ] **Step 2: Testi koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~LicensesWpfCatalogPullControllerTests"
```
Beklenen: FAIL — `coverPhotoKey` diye bir özellik yok
(`KeyNotFoundException` / `InvalidOperationException`).

- [ ] **Step 3: Controller'ı değiştir**

`LicensesWpfCatalogPullController.cs`'te dört değişiklik:

1. `using OrderDeck.LicenseServer.Services.BroadcastPosts;` ekle.

2. Ctor'a depolamayı enjekte et:

```csharp
    private readonly LicenseDbContext _db;
    private readonly IBroadcastMediaStorage _storage;

    public LicensesWpfCatalogPullController(LicenseDbContext db, IBroadcastMediaStorage storage)
    {
        _db = db;
        _storage = storage;
    }
```

3. `CatalogProductDto`'nun sonuna iki alan ekle (`Variants`'tan **önce** —
   pozisyonel record'da sıra sözleşmenin parçası, ama JSON adla eşleştiği
   için istemci etkilenmez; okunabilirlik için fotoğraf alanlarını
   `UpdatedAt`'in yanına koyuyoruz):

`<param>` blokları record **bildiriminin üstüne** yazılır (kardeş
`LicensesWpfStockPullController.cs:77-80` kalıbı); parametre listesinin içine
gömülürse derleyici belgeye bağlamaz.

```csharp
    /// <param name="CoverPhotoKey">
    /// Kapak fotoğrafının R2 nesne anahtarı (en küçük <c>SortOrder</c>);
    /// fotoğraf yoksa null. <b>Önbellek anahtarı budur</b> — URL değil.
    /// </param>
    /// <param name="CoverPhotoUrl">
    /// Aynı nesne için <b>5 dakika</b> geçerli imzalı indirme adresi.
    /// Saklanmamalı; çekme döngüsünün hemen ardından indirilip
    /// <c>CoverPhotoKey</c> altına önbelleklenmeli.
    /// </param>
    public sealed record CatalogProductDto(
        Guid Id,
        Guid? CategoryId,
        string Code,
        string Name,
        string NameSearch,
        decimal DefaultPrice,
        string? ShelfLocation,
        string? Axis1Name, int? Axis1Role,
        string? Axis2Name, int? Axis2Role,
        DateTimeOffset UpdatedAt,
        string? CoverPhotoKey,
        string? CoverPhotoUrl,
        List<CatalogVariantDto> Variants);
```

4. Sorguyu iki aşamaya böl. İmzalama `async` ve depolama çağrısı olduğu için
   LINQ projeksiyonunun içinde yapılamaz — önce anahtar çekilir, sonra
   materyalize liste üstünde imzalanır. `rows` üretimindeki
   `.Select(p => new CatalogProductDto(...))` bloğunu şununla değiştir:

```csharp
        var rows = await q
            .OrderBy(p => p.Id)
            .Take(take)
            .Select(p => new CatalogProductDto(
                p.Id, p.CategoryId, p.Code, p.Name, p.NameSearch,
                p.DefaultPrice, p.ShelfLocation,
                p.Axis1Name, p.Axis1Role == null ? null : (int?)p.Axis1Role,
                p.Axis2Name, p.Axis2Role == null ? null : (int?)p.Axis2Role,
                p.UpdatedAt,
                // Kapak = en küçük SortOrder (ayrı IsCover bayrağı bilerek yok).
                p.Photos.OrderBy(x => x.SortOrder)
                        .Select(x => x.ObjectKey).FirstOrDefault(),
                null,
                p.Variants
                    .OrderBy(v => v.VariantCode)
                    .Select(v => new CatalogVariantDto(
                        v.Id, v.Axis1Value, v.Axis1Code,
                        v.Axis2Value, v.Axis2Code,
                        v.VariantCode, v.Barcode, v.IsActive))
                    .ToList()))
            .ToListAsync(ct);

        // İmzalama YEREL bir HMAC, ağ çağrısı değil — sayfa başına en çok 500
        // imza mikrosaniyeler sürer. Yine de fotoğrafsız ürünler atlanıyor.
        for (var i = 0; i < rows.Count; i++)
        {
            if (rows[i].CoverPhotoKey is not { Length: > 0 } key) continue;

            // İmzalama hatası sayfayı düşürmesin: CoverPhotoUrl null kalır, döngü
            // devam eder, uç yine 200 döner. Ürün verisi ve CoverPhotoKey akar;
            // istemci elindeki önbelleği korur. Alternatifi R2 kimlik bilgisi
            // eksik bir sunucuda bütün kataloğun HİÇ kurulamamasıdır (keyset
            // imleci ilk sayfada takılır).
            try
            {
                rows[i] = rows[i] with
                {
                    CoverPhotoUrl = await _storage.CreateDownloadUrlAsync(key, ct)
                };
            }
            // Yalnız GERÇEK iptal fırlatılır; koşulsuz bir dal, depolama ileride
            // ağ çağrısı yaparsa (zaman aşımı → ct iptal edilmemişken
            // TaskCanceledException) sayfayı yine düşürürdü.
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex,
                    "Kapak fotoğrafı imzalanamadı, CoverPhotoUrl null bırakıldı. Key={Key}", key);
            }
        }

        return Ok(rows);
```

Logger'ı da ctor'a enjekte et (`ILogger<LicensesWpfCatalogPullController>`,
kardeş controller'lardaki kalıp).

- [ ] **Step 4: Testleri koştur, geçtiklerini gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~LicensesWpfCatalogPullControllerTests"
```
Beklenen: PASS (mevcut testler dahil hepsi).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs
git commit -m "feat(katalog): WPF çekme yükü kapak fotoğrafını taşısın"
```

---

### Task 2: Sunucu — WPF için kategori çekme ucu

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs`

- [ ] **Step 1: Testi yaz (başarısız olacak)**

```csharp
    [Fact]
    public async Task Categories_are_returned_ordered_by_path()
    {
        var (client, licenseId) = await SeedAsync(0);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var kok = new Category
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Name = "Erkek",
            SortOrder = 0, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        kok.Path = $"/{kok.Id:N}/";
        var alt = new Category
        {
            Id = Guid.NewGuid(), LicenseId = licenseId, Name = "Tişört",
            ParentCategoryId = kok.Id, SortOrder = 0, IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        alt.Path = $"/{kok.Id:N}/{alt.Id:N}/";
        db.Categories.AddRange(kok, alt);
        await db.SaveChangesAsync();

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/categories");

        rows!.Should().HaveCount(2);
        // Path sıralaması ağacı ata-önce diziyor; WPF ayrıca sıralama yapmasın.
        rows[0].GetProperty("name").GetString().Should().Be("Erkek");
        rows[1].GetProperty("name").GetString().Should().Be("Tişört");
        rows[1].GetProperty("parentCategoryId").GetString()
               .Should().Be(kok.Id.ToString());
    }

    [Fact]
    public async Task Categories_of_a_foreign_license_are_not_returned()
    {
        var (client, _) = await SeedAsync(0);

        var res = await client.GetAsync(
            $"/api/v1/licenses/{Guid.NewGuid()}/catalog/categories");

        res.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
```

`SeedAsync(0)` çağrısı ürün üretmeden lisans kuruyor — mevcut döngü zaten
`productCount` kadar dönüyor, ek değişiklik gerekmiyor.

- [ ] **Step 2: Testi koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~LicensesWpfCatalogPullControllerTests.Categories"
```
Beklenen: FAIL — 404 (rota yok).

- [ ] **Step 3: Ucu yaz**

`CatalogProductDto`'nun altına DTO'yu, `Products` metodunun altına ucu ekle:

```csharp
    public sealed record CatalogCategoryDto(
        Guid Id,
        Guid? ParentCategoryId,
        string Name,
        string Path,
        int SortOrder,
        bool IsActive);

    /// <summary>
    /// Kategori ağacının tamamı. <b>Sayfalama yok</b>: derinlik
    /// <c>CatalogLimits.CategoryMaxDepth</c> ile sınırlı ve ağaç lisans başına
    /// onlar mertebesinde — sayfalamak, çözdüğünden çok karmaşıklık getirirdi.
    ///
    /// Pasif kategoriler de dönüyor: ürün pasif bir kategoriye bağlı kalmış
    /// olabilir ve WPF'te adının kaybolması "kategori yok" gibi görünürdü.
    /// Sıralama <c>Path</c> üstünde — ata her zaman çocuğundan önce gelir.
    /// </summary>
    [HttpGet("categories")]
    public async Task<IActionResult> Categories(Guid licenseId, CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        var rows = await _db.Categories
            .Where(c => c.LicenseId == licenseId)
            .OrderBy(c => c.Path)
            .Select(c => new CatalogCategoryDto(
                c.Id, c.ParentCategoryId, c.Name, c.Path, c.SortOrder, c.IsActive))
            .ToListAsync(ct);

        return Ok(rows);
    }
```

- [ ] **Step 4: Testleri koştur**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj \
  --filter "FullyQualifiedName~LicensesWpfCatalogPullControllerTests"
```
Beklenen: PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs
git commit -m "feat(katalog): WPF için kategori çekme ucu"
```

> **Görev sırası hakkında:** eski `Product`/`ProductSize` tablolarını düşüren
> göç **Task 8'de** (`026`). Sebep: düşürme, `ProductRepository` ve
> `ProductCardViewModel` hâlâ ayaktayken testleri kırar. Yıkıcı adımı en sona
> alarak her görev yeşil bitiyor.

---

### Task 3: Yerel şema — replika tabloları (025)

**Files:**
- Create: `OrderDeck.Core/Storage/Migrations/025_catalog_replica.sql`
- Modify: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs:32,56`

`.csproj`'de `<EmbeddedResource Include="Storage\Migrations\*.sql" />` glob'u
var — dosyayı eklemek yeterli, csproj'a dokunma.

- [ ] **Step 1: Testi güncelle (başarısız olacak)**

`OrderDeck.Tests/Storage/MigrationRunnerTests.cs` satır 32 ve 56:

```csharp
        version.Should().Be(25);
```

- [ ] **Step 2: Koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~MigrationRunnerTests"
```
Beklenen: FAIL — `Expected version to be 25, but found 24`.

- [ ] **Step 3: Göç dosyasını yaz**

`OrderDeck.Core/Storage/Migrations/025_catalog_replica.sql`:

```sql
-- Stok Faz 1b: katalog artık sunucuda. Bu üç tablo, sunucudaki kataloğun
-- SALT-OKUNUR replikası — WPF hiçbir zaman buraya kullanıcı verisi yazmaz,
-- CatalogSyncService her turda hepsini baştan yazar.
--
-- Neden tam baştan yazma: sunucudaki çekme ucu TAM ANLIK GÖRÜNTÜ döndürüyor.
-- `after` bir değişim imleci değil, birincil anahtar üstünde keyset sayfalama
-- imleci; silinen/arşivlenen ürün yanıtta hiç görünmez ve mezar taşı da yok.
-- Birleştirme yapsaydık silinen ürün WPF'te hayalet satır olarak kalır ve
-- yayında yanlış ürüne eşleşirdi.
--
-- Kimlikler sunucudaki GUID'ler, TEXT "N" biçiminde (32 hane, tiresiz) —
-- CustomerRepository'deki kural. Zaman damgaları unix SANİYE (IClock ile aynı
-- birim). decimal -> REAL (Label.Price ile aynı, bkz. 002).

CREATE TABLE CatalogProduct (
    Id             TEXT PRIMARY KEY,
    CategoryId     TEXT,
    -- Kanonik kod: sunucu bunu SearchNormalizer.Normalize ile yazıyor.
    Code           TEXT NOT NULL,
    -- Aranan iğnenin karşılaştırılacağı biçim. Sunucudaki Code zaten kanonik
    -- olduğu için ikisi bugün aynı; kolon yine de ayrı duruyor ki eşleştirme
    -- kuralı sunucudaki yazma kuralından bağımsız olarak burada okunabilsin.
    CodeNormalized TEXT NOT NULL,
    Name           TEXT NOT NULL,
    DefaultPrice   REAL NOT NULL,
    ShelfLocation  TEXT,
    Axis1Name      TEXT,
    Axis1Role      INTEGER,
    Axis2Name      TEXT,
    Axis2Role      INTEGER,
    -- R2 nesne anahtarı; fotoğraf önbelleğinin anahtarı da bu. URL SAKLANMAZ
    -- (imzalı ve 5 dakika ömürlü).
    CoverPhotoKey  TEXT,
    UpdatedAt      INTEGER NOT NULL
);

-- UNIQUE DEĞİL, bilerek: replika kendisine verilen veriyi reddetmemeli.
-- Benzersizliği sunucu (LicenseId, Code) üstünde zaten uyguluyor; burada
-- unique olsaydı beklenmedik bir çakışma bütün senkron transaction'ını
-- düşürür ve replika sessizce eskirdi.
--
-- Bu indeks yalnız EŞİTLİK aramasını hızlandırır: WHERE CodeNormalized = ?
-- → SEARCH ... USING INDEX. LIKE aramaları (WHERE CodeNormalized LIKE 'A%'
-- veya WHERE '…yorum…' LIKE '%'||CodeNormalized||'%') indeksi KULLANMAZ —
-- SQLite LIKE optimizasyonu BINARY collation'da çalışmaz (NOCASE ister).
-- Bu nedenle yorum eşleştirmesi metni token'lara bölüp token başına eşitlik
-- araması yapmalı. Katalog lisans başına yüzler mertebesinde olduğu için
-- tam tarama felaket değil, ama tercih bilinçli olsun; LIKE '%...%' yazan
-- biri sessizce tam tarama yapar.
-- NOT: "güzel elbise" gibi çok kelimeli kodlar tek token eşitliğiyle
-- bulunamaz; çok kelimeli adayları da kapsayan eşleştirme stratejisi
-- sonraki planda kararlaştırılacak.
CREATE INDEX IX_CatalogProduct_CodeNormalized ON CatalogProduct(CodeNormalized);

-- FK YOK, bilerek: SqliteConnectionFactory ve InMemorySqlite ikisi de
-- ForeignKeys=true kuruyor (024'teki ProductSize cascade'i çalışıyor).
-- Buradaki FK yokluğu aynı UNIQUE yokluğuyla aynı mantık — tek bozuk sayfa
-- bütün senkron transaction'ını düşürmesin. Bedeli: cascade yok, bu yüzden
-- Replace varyantları AÇIKÇA silmek zorunda; aksi hâlde kısmi tazeleme
-- öksüz satır üretir.
CREATE TABLE CatalogVariant (
    Id          TEXT PRIMARY KEY,
    ProductId   TEXT NOT NULL,  -- CatalogProduct.Id; FK bilerek yok, yukarıya bak.
    Axis1Value  TEXT,
    Axis1Code   TEXT,
    Axis2Value  TEXT,
    Axis2Code   TEXT,
    VariantCode TEXT NOT NULL,
    Barcode     TEXT,
    IsActive    INTEGER NOT NULL,
    -- Sunucunun JSON dizisindeki konum (0-tabanlı dizi indeksi). Sunucu
    -- varyantları OrderBy(v => v.VariantCode) ile sıralayıp dizi olarak
    -- gönderiyor; CatalogReplicaRepository bunu Select((v, i) => ... i) ile
    -- doldurur. Neden yerelde yeniden sıralamıyoruz: sunucu sırası SQL Server
    -- collation'ında üretiliyor, SQLite'ın ordinal sıralaması farklı düşebilir
    -- — o yüzden sıra taşınıyor, yeniden hesaplanmıyor.
    SortOrder   INTEGER NOT NULL
);

CREATE INDEX IX_CatalogVariant_ProductId ON CatalogVariant(ProductId);

CREATE TABLE CatalogCategory (
    Id               TEXT PRIMARY KEY,
    ParentCategoryId TEXT,
    Name             TEXT NOT NULL,
    -- Id tabanlı yol (/{id:N}/...); sıralaması ağacı ata-önce diziyor.
    Path             TEXT NOT NULL,
    SortOrder        INTEGER NOT NULL,
    IsActive         INTEGER NOT NULL
);

UPDATE _meta SET SchemaVersion = 25 WHERE Id = 1;
```

- [ ] **Step 4: Koştur, geçtiğini gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~MigrationRunnerTests"
```
Beklenen: PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Storage/Migrations/025_catalog_replica.sql \
        OrderDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(katalog): yerel katalog replikası şeması (025)"
```

---

### Task 4: Replika kayıtları ve deposu

**Files:**
- Create: `OrderDeck.Core/Catalog/CatalogReplica.cs`
- Create: `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs`
- Test: `OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs`

- [ ] **Step 1: Kayıtları yaz**

`OrderDeck.Core/Catalog/CatalogReplica.cs`:

```csharp
namespace OrderDeck.Core.Catalog;

/// <summary>
/// Sunucudaki katalog ürününün yerel salt-okunur kopyası.
/// </summary>
/// <param name="Id">Sunucudaki GUID, "N" biçiminde (32 hane, tiresiz).</param>
/// <param name="Code">Kanonik ürün kodu (sunucu böyle yazıyor).</param>
/// <param name="CodeNormalized">
/// <c>SearchNormalizer.Normalize(Code)</c>. Aranan iğne de aynı fonksiyondan
/// geçtiği için operatör "güzel elbise" yazdığında "GUZEL ELBISE" bulunur.
/// </param>
/// <param name="CoverPhotoKey">R2 nesne anahtarı; fotoğraf önbelleğinin anahtarı.</param>
/// <param name="UpdatedAt">Unix saniye.</param>
public sealed record CatalogProduct(
    string Id,
    string? CategoryId,
    string Code,
    string CodeNormalized,
    string Name,
    decimal DefaultPrice,
    string? ShelfLocation,
    string? Axis1Name,
    int? Axis1Role,
    string? Axis2Name,
    int? Axis2Role,
    string? CoverPhotoKey,
    long UpdatedAt);

/// <summary>Bir ürünün tek varyantı. Eksensiz üründe de tam bir varyant vardır.</summary>
public sealed record CatalogVariant(
    string Id,
    string ProductId,
    string? Axis1Value,
    string? Axis1Code,
    string? Axis2Value,
    string? Axis2Code,
    string VariantCode,
    string? Barcode,
    bool IsActive,
    int SortOrder);

/// <param name="Path">Id tabanlı yol; sıralaması ağacı ata-önce dizer.</param>
public sealed record CatalogCategory(
    string Id,
    string? ParentCategoryId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive);
```

- [ ] **Step 2: Testleri yaz (başarısız olacak)**

`OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class CatalogReplicaRepositoryTests
{
    private static CatalogReplicaRepository Make(out IDbConnectionFactory db)
    {
        db = InMemorySqlite.Create();
        new MigrationRunner(db).Run();
        return new CatalogReplicaRepository(db);
    }

    private static CatalogProduct Product(string id, string code, string name = "Elbise")
        => new(id, null, code, OrderDeck.Shared.Text.SearchNormalizer.Normalize(code),
               name, 199.90m, null, null, null, null, null, null, 1_700_000_000);

    [Fact]
    public void FindByCode_matches_case_and_turkish_letters_insensitively()
    {
        var repo = Make(out _);
        repo.Replace([Product("p1", "GUZEL ELBISE")], [], []);

        repo.FindByCode("güzel elbise")!.Id.Should().Be("p1");
        repo.FindByCode("  Güzel   Elbise ")!.Id.Should().Be("p1");
        repo.FindByCode("ISIK 1").Should().BeNull();
    }

    [Fact]
    public void Replace_wipes_rows_that_the_server_no_longer_reports()
    {
        var repo = Make(out _);
        repo.Replace([Product("p1", "A1"), Product("p2", "A2")], [], []);

        // Sunucu artık yalnız A2'yi bildiriyor: A1 panelden silinmiş demektir.
        repo.Replace([Product("p2", "A2")], [], []);

        repo.FindByCode("A1").Should().BeNull();
        repo.FindByCode("A2").Should().NotBeNull();
    }

    [Fact]
    public void Replace_wipes_variants_and_categories_that_the_server_no_longer_reports()
    {
        var repo = Make(out _);

        // İlk tur: ürün + varyant + kategori hepsi dolu.
        repo.Replace(
            [Product("p1", "A1")],
            [new CatalogVariant("v1", "p1", null, null, null, null, "A1", null, true, 0)],
            [new CatalogCategory("c1", null, "Erkek", "/c1/", 0, true)]);

        // İkinci tur: sunucu ürünü bildiriyor ama varyant ve kategori listesi boş.
        repo.Replace([Product("p1", "A1")], [], []);

        repo.GetVariants("p1").Should().BeEmpty();
        repo.GetCategories().Should().BeEmpty();
    }

    [Fact]
    public void GetVariants_returns_only_that_products_variants_in_sort_order()
    {
        var repo = Make(out _);
        repo.Replace(
            [Product("p1", "A1"), Product("p2", "A2")],
            [
                // "z-first" alfabetik olarak "a-second"'dan sonra gelir ama
                // SortOrder 0 ile önde olmalı — ORDER BY SortOrder'ı test eder.
                new CatalogVariant("z-first",  "p1", "Kırmızı", "KIRM", "S", "S", "A1-KIRM-S", null, true,  0),
                new CatalogVariant("a-second", "p1", "Kırmızı", "KIRM", "M", "M", "A1-KIRM-M", null, false, 1),
                new CatalogVariant("v9",       "p2", null, null, null, null, "A2", null, true, 0),
            ],
            []);

        var variants = repo.GetVariants("p1");

        // Sıra SortOrder'a göre: z-first önce, a-second sonra.
        variants.Select(v => v.Id).Should().Equal("z-first", "a-second");
        // IsActive doğru yuvarlak-turlanmalı.
        variants[0].IsActive.Should().BeTrue();
        variants[1].IsActive.Should().BeFalse();
    }

    [Fact]
    public void Replace_round_trips_categories()
    {
        var repo = Make(out _);
        // INSERT sırası PATH sırasının TERSİ — ORDER BY Path yoksa c2 önce gelir.
        repo.Replace(
            [],
            [],
            [
                new CatalogCategory("c2",   null, "Kadın",        "/c2/",     2, false),
                new CatalogCategory("c1a",  "c1", "Gömlek",      "/c1/c1a/", 1, true),
                new CatalogCategory("c1",   null, "Erkek",       "/c1/",     0, true),
            ]);

        var cats = repo.GetCategories();

        // ORDER BY Path: /c1/ < /c1/c1a/ < /c2/
        cats.Select(c => c.Id).Should().Equal("c1", "c1a", "c2");

        // Tüm alanlar yuvarlak-turlanmalı.
        var erkek = cats.Single(c => c.Id == "c1");
        erkek.Name.Should().Be("Erkek");
        erkek.Path.Should().Be("/c1/");
        erkek.SortOrder.Should().Be(0);
        erkek.ParentCategoryId.Should().BeNull();
        erkek.IsActive.Should().BeTrue();

        var gomlek = cats.Single(c => c.Id == "c1a");
        gomlek.ParentCategoryId.Should().Be("c1");
        gomlek.IsActive.Should().BeTrue();

        // IsActive false olan kategori doğru okunmalı.
        var kadin = cats.Single(c => c.Id == "c2");
        kadin.IsActive.Should().BeFalse();
        kadin.SortOrder.Should().Be(2);
    }

    [Fact]
    public void CoverPhotoKeys_lists_every_live_key_once()
    {
        var repo = Make(out _);
        repo.Replace(
            [
                // Aynı anahtar iki ayrı üründe — DISTINCT olmadan iki kez dönerdi.
                Product("p1", "A1") with { CoverPhotoKey = "lic/products/shared/k.img" },
                Product("p2", "A2") with { CoverPhotoKey = "lic/products/shared/k.img" },
                // Fotoğrafsız ürün listede olmamalı.
                Product("p3", "A3"),
            ],
            [], []);

        var keys = repo.CoverPhotoKeys();

        // Aynı anahtar tam olarak bir kez dönmeli.
        keys.Should().Equal("lic/products/shared/k.img");
    }

    [Fact]
    public void FindByCode_picks_the_same_row_every_time_when_the_code_repeats()
    {
        var repo = Make(out _);
        // Replika indeksi bilerek UNIQUE değil (bkz. göç 025): beklenmedik bir
        // çakışma bütün senkron transaction'ını düşürmesin. Bedeli, aynı kodu
        // taşıyan iki satırın mümkün olması — arama yine de KARARLI dönmeli,
        // yoksa aynı yorum yayının iki anında iki farklı ürüne eşleşir.
        // "z-dup" alfabetik olarak sonra gelir ama Code eşit olduğu için sırayı
        // Id belirler: ORDER BY Code, Id → "a-dup".
        repo.Replace(
            [Product("z-dup", "A1"), Product("a-dup", "A1")],
            [], []);

        repo.FindByCode("A1")!.Id.Should().Be("a-dup");
    }

    [Fact]
    public void FindByCode_round_trips_default_price()
    {
        var repo = Make(out _);
        var product = Product("p1", "A1") with { DefaultPrice = 199.95m };
        repo.Replace([product], [], []);

        var found = repo.FindByCode("A1");

        found!.DefaultPrice.Should().Be(199.95m);
    }
}
```

- [ ] **Step 3: Koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~CatalogReplicaRepositoryTests"
```
Beklenen: FAIL — `CatalogReplicaRepository` tipi yok (derleme hatası).

- [ ] **Step 4: Depoyu yaz**

`OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs`:

```csharp
using Dapper;
using OrderDeck.Core.Catalog;
using OrderDeck.Shared.Text;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucu kataloğunun yerel salt-okunur replikası. Tek yazarı
/// <c>CatalogSyncService</c>; kullanıcı arayüzü buraya asla yazmaz.
/// </summary>
public sealed class CatalogReplicaRepository
{
    private readonly IDbConnectionFactory _factory;

    public CatalogReplicaRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Replikayı baştan yazar. <b>Kısmi çağrılmamalı</b>: çağıran, sunucudan
    /// gelen TAM anlık görüntüyü elinde topladıktan sonra bir kez çağırır.
    /// Ağ yarıda koparsa hiç çağrılmaz ve replika eski hâliyle kullanılabilir
    /// kalır — yarım liste yazmak, silinmemiş ürünleri silmek olurdu.
    /// </summary>
    public void Replace(
        IReadOnlyList<CatalogProduct> products,
        IReadOnlyList<CatalogVariant> variants,
        IReadOnlyList<CatalogCategory> categories)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        // Silme sırası önemsiz (FK yok — replikada bütünlüğü sunucu garanti
        // ediyor, yerel cascade kurmak yanlış güven verirdi), ama hepsi AYNI
        // transaction'da: yarı silinmiş bir replika hiç yoktan kötüdür.
        conn.Execute("DELETE FROM CatalogVariant", transaction: tx);
        conn.Execute("DELETE FROM CatalogProduct", transaction: tx);
        conn.Execute("DELETE FROM CatalogCategory", transaction: tx);

        // DefaultPrice decimal olarak doğrudan bağlanıyor (ShipmentRepository'deki
        // gibi ara (double) cast'i YOK). Microsoft.Data.Sqlite decimal'i invariant
        // kültürde TEXT olarak bağlar; kolonun REAL affinity'si bunu sayıya çevirir.
        // Çift dönüşüm (decimal→double→REAL) yuvarlama hatası üretebileceğinden bu
        // yaklaşım bilinçli olarak ShipmentRepository'den farklı tutuluyor.
        if (products.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogProduct
                    (Id, CategoryId, Code, CodeNormalized, Name, DefaultPrice,
                     ShelfLocation, Axis1Name, Axis1Role, Axis2Name, Axis2Role,
                     CoverPhotoKey, UpdatedAt)
                VALUES
                    (@Id, @CategoryId, @Code, @CodeNormalized, @Name, @DefaultPrice,
                     @ShelfLocation, @Axis1Name, @Axis1Role, @Axis2Name, @Axis2Role,
                     @CoverPhotoKey, @UpdatedAt)
                """,
                products, tx);

        if (variants.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogVariant
                    (Id, ProductId, Axis1Value, Axis1Code, Axis2Value, Axis2Code,
                     VariantCode, Barcode, IsActive, SortOrder)
                VALUES
                    (@Id, @ProductId, @Axis1Value, @Axis1Code, @Axis2Value, @Axis2Code,
                     @VariantCode, @Barcode, @IsActive, @SortOrder)
                """,
                variants.Select(v => new
                {
                    v.Id, v.ProductId, v.Axis1Value, v.Axis1Code,
                    v.Axis2Value, v.Axis2Code, v.VariantCode, v.Barcode,
                    IsActive = v.IsActive ? 1 : 0,
                    v.SortOrder
                }).ToList(), tx);

        if (categories.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogCategory
                    (Id, ParentCategoryId, Name, Path, SortOrder, IsActive)
                VALUES
                    (@Id, @ParentCategoryId, @Name, @Path, @SortOrder, @IsActive)
                """,
                categories.Select(c => new
                {
                    c.Id, c.ParentCategoryId, c.Name, c.Path, c.SortOrder,
                    IsActive = c.IsActive ? 1 : 0
                }).ToList(), tx);

        tx.Commit();
    }

    /// <summary>
    /// Operatörün yazdığı kodu bulur. İğne saklanan kolonla <b>aynı</b>
    /// fonksiyondan geçiyor: büyük/küçük harf ve Türkçe harf farkı önemsiz,
    /// ardışık boşluklar sadeleşiyor.
    ///
    /// <c>LIMIT 1</c> savunma amaçlı: indeks unique değil (bkz. göç 025), yani
    /// beklenmedik bir çakışmada arama patlamak yerine ilk satırı verir.
    /// İkincil <c>Id</c> anahtarı, aynı Code'u taşıyan iki satırın sırasını
    /// deterministik yapar.
    /// </summary>
    public CatalogProduct? FindByCode(string? code)
    {
        var needle = SearchNormalizer.Normalize(code);
        if (needle.Length == 0) return null;

        using var conn = _factory.Open();
        return conn.Query<ProductRow>(
            $"SELECT {ProductColumns} FROM CatalogProduct WHERE CodeNormalized = @needle "
          + "ORDER BY Code, Id LIMIT 1",
            new { needle })
            .Select(Map).FirstOrDefault();
    }

    public IReadOnlyList<CatalogVariant> GetVariants(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<VariantRow>(
            """
            SELECT Id, ProductId, Axis1Value, Axis1Code, Axis2Value, Axis2Code,
                   VariantCode, Barcode, IsActive, SortOrder
            FROM CatalogVariant
            WHERE ProductId = @productId
            ORDER BY SortOrder
            """,
            new { productId })
            .Select(r => new CatalogVariant(
                r.Id, r.ProductId, r.Axis1Value, r.Axis1Code, r.Axis2Value,
                r.Axis2Code, r.VariantCode, r.Barcode, r.IsActive == 1, r.SortOrder))
            .ToList();
    }

    public IReadOnlyList<CatalogCategory> GetCategories()
    {
        using var conn = _factory.Open();
        return conn.Query<CategoryRow>(
            """
            SELECT Id, ParentCategoryId, Name, Path, SortOrder, IsActive
            FROM CatalogCategory ORDER BY Path
            """)
            .Select(r => new CatalogCategory(
                r.Id, r.ParentCategoryId, r.Name, r.Path, r.SortOrder, r.IsActive == 1))
            .ToList();
    }

    /// <summary>Önbellekte tutulması gereken canlı fotoğraf anahtarları.</summary>
    public IReadOnlyList<string> CoverPhotoKeys()
    {
        using var conn = _factory.Open();
        return conn.Query<string>(
            "SELECT DISTINCT CoverPhotoKey FROM CatalogProduct "
          + "WHERE CoverPhotoKey IS NOT NULL AND CoverPhotoKey <> '' ORDER BY CoverPhotoKey")
            .ToList();
    }

    private const string ProductColumns =
        "Id, CategoryId, Code, CodeNormalized, Name, DefaultPrice, ShelfLocation, "
      + "Axis1Name, Axis1Role, Axis2Name, Axis2Role, CoverPhotoKey, UpdatedAt";

    private static CatalogProduct Map(ProductRow r) => new(
        r.Id, r.CategoryId, r.Code, r.CodeNormalized, r.Name, r.DefaultPrice,
        r.ShelfLocation, r.Axis1Name, r.Axis1Role, r.Axis2Name, r.Axis2Role,
        r.CoverPhotoKey, r.UpdatedAt);

    // SQLite INTEGER -> Int64 döner; Dapper bunu record kurucusunun int
    // parametresine bağlayamaz. Daraltma bu ara sınıflarda yapılıyor
    // (bkz. ShipmentRepository.Row — repodaki yerleşik kural).
    private sealed class ProductRow
    {
        public string Id { get; init; } = "";
        public string? CategoryId { get; init; }
        public string Code { get; init; } = "";
        public string CodeNormalized { get; init; } = "";
        public string Name { get; init; } = "";
        public decimal DefaultPrice { get; init; }
        public string? ShelfLocation { get; init; }
        public string? Axis1Name { get; init; }
        public int? Axis1Role { get; init; }
        public string? Axis2Name { get; init; }
        public int? Axis2Role { get; init; }
        public string? CoverPhotoKey { get; init; }
        public long UpdatedAt { get; init; }
    }

    private sealed class VariantRow
    {
        public string Id { get; init; } = "";
        public string ProductId { get; init; } = "";
        public string? Axis1Value { get; init; }
        public string? Axis1Code { get; init; }
        public string? Axis2Value { get; init; }
        public string? Axis2Code { get; init; }
        public string VariantCode { get; init; } = "";
        public string? Barcode { get; init; }
        public int IsActive { get; init; }
        public int SortOrder { get; init; }
    }

    private sealed class CategoryRow
    {
        public string Id { get; init; } = "";
        public string? ParentCategoryId { get; init; }
        public string Name { get; init; } = "";
        public string Path { get; init; } = "";
        public int SortOrder { get; init; }
        public int IsActive { get; init; }
    }
}
```

- [ ] **Step 5: Koştur, geçtiğini gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~CatalogReplicaRepositoryTests"
```
Beklenen: PASS (5/5).

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.Core/Catalog/CatalogReplica.cs \
        OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs \
        OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs
git commit -m "feat(katalog): yerel replika deposu"
```

---

### Task 5: Tel modelleri ve API istemcisi

**Files:**
- Create: `OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs`
- Modify: `OrderDeck.Licensing/Api/LicenseApiClient.cs` (`GetWpfCustomersSinceAsync`'in hemen altına)
- Test: `OrderDeck.Tests/Licensing/LicenseApiClientTests.cs`

- [ ] **Step 1: DTO'ları yaz**

`OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs`:

```csharp
namespace OrderDeck.Licensing.Api.Models;

/// <summary>Sunucudan çekilen tek bir katalog ürünü (tel modeli).</summary>
/// <param name="CoverPhotoKey">
/// R2 nesne anahtarı; <b>kalıcı</b> önbellek anahtarı. Fotoğraf yoksa null.
/// </param>
/// <param name="CoverPhotoUrl">
/// Aynı nesnenin 5 dakika geçerli imzalı adresi. <b>Saklanmaz</b> — yalnız
/// bu çekme turunda indirmek için kullanılır.
/// <para><c>CoverPhotoKey</c> dolu iken bunun null gelmesi <b>meşru bir
/// durumdur</b>: sunucu imzalama başarısız olursa sayfayı düşürmez, URL'i null
/// bırakıp anahtarı yine gönderir. Önbellek bu durumda mevcut yerel dosyayı
/// KORUMALI, "fotoğraf silinmiş" sayıp atmamalıdır.</para>
/// </param>
/// <param name="Variants">
/// Ürünün varyantları. <b>Dizideki konum sıralamanın kendisidir</b> — sunucu
/// SQL Server collation'ında <c>VariantCode</c>'a göre sıralayıp gönderiyor;
/// SQLite'ın ordinal sıralaması farklı düşebileceği için yerelde yeniden
/// SIRALANMAZ, dizi indeksi <c>SortOrder</c> olarak taşınır (bkz.
/// <c>025_catalog_replica.sql</c>).
/// </param>
public sealed record CatalogProductPullItem(
    Guid Id,
    Guid? CategoryId,
    string Code,
    string Name,
    string NameSearch,
    decimal DefaultPrice,
    string? ShelfLocation,
    string? Axis1Name,
    int? Axis1Role,
    string? Axis2Name,
    int? Axis2Role,
    DateTimeOffset UpdatedAt,
    string? CoverPhotoKey,
    string? CoverPhotoUrl,
    List<CatalogVariantPullItem> Variants);

/// <summary>Bir ürünün tek varyantı; sırası taşıyıcı dizinin konumundan gelir.</summary>
public sealed record CatalogVariantPullItem(
    Guid Id,
    string? Axis1Value,
    string? Axis1Code,
    string? Axis2Value,
    string? Axis2Code,
    string VariantCode,
    string? Barcode,
    bool IsActive);

/// <summary>Kategori ağacının tek düğümü; <c>Path</c> sırası ata-önce dizer.</summary>
public sealed record CatalogCategoryPullItem(
    Guid Id,
    Guid? ParentCategoryId,
    string Name,
    string Path,
    int SortOrder,
    bool IsActive);
```

- [ ] **Step 2: Testleri yaz (başarısız olacak)**

`OrderDeck.Tests/Licensing/LicenseApiClientTests.cs` dosyasının sonuna:

```csharp
    [Fact]
    public async Task GetCatalogProductsAsync_passes_the_keyset_cursor()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req =>
        {
            seen = req;
            return FakeHttpMessageHandler.Json(200, """
                [{ "id":"11111111-1111-1111-1111-111111111111",
                   "code":"A1", "name":"Elbise", "nameSearch":"ELBISE",
                   "defaultPrice":199.90, "updatedAt":"2026-08-13T10:00:00Z",
                   "coverPhotoKey":"lic/products/p/k.img",
                   "coverPhotoUrl":"https://r2.local/k.img?sig=1",
                   "variants":[] }]
                """);
        });

        var licenseId = Guid.NewGuid();
        var after = Guid.Parse("22222222-2222-2222-2222-222222222222");

        var rows = await client.GetCatalogProductsAsync(licenseId, after, take: 200);

        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/products?after={after}&take=200");
        rows.Should().ContainSingle();
        rows[0].CoverPhotoKey.Should().Be("lic/products/p/k.img");
    }

    [Fact]
    public async Task GetCatalogProductsAsync_omits_the_cursor_on_the_first_page()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req => { seen = req; return FakeHttpMessageHandler.Json(200, "[]"); });

        var licenseId = Guid.NewGuid();
        // take, varsayılandan (200) FARKLI seçiliyor: aksi hâlde sorgu dizesine
        // 200 sabitlense de test yeşil kalır, parametre gerçekten sınanmaz.
        await client.GetCatalogProductsAsync(licenseId, after: null, take: 50);

        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/products?take=50");
    }

    [Fact]
    public async Task GetCatalogProductsAsync_sinir_icindeki_take_i_tele_aynen_yazar()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req => { seen = req; return FakeHttpMessageHandler.Json(200, "[]"); });

        var licenseId = Guid.NewGuid();
        await client.GetCatalogProductsAsync(licenseId, after: null, take: 500);

        // Üst sınırın kendisi GEÇERLİ: istemci onu kırpmadan, değiştirmeden geçirmeli.
        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/products?take=500");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    [InlineData(501)]
    [InlineData(1000)]
    public async Task GetCatalogProductsAsync_sinir_disi_take_degerini_reddeder(int take)
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "[]"));

        // Sessizce kırpsaydık çağıran kendi take'iyle "eksik sayfa = son sayfa"
        // sanıp kataloğun kalanını sildirirdi. Yüksek sesle reddet.
        var act = () => client.GetCatalogProductsAsync(Guid.NewGuid(), after: null, take: take);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task GetCatalogProductsAsync_null_govdeyi_bos_katalog_saymaz()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"));

        // Bozuk gövde "katalog boş" demek DEĞİL. Sessizce boş liste dönseydi
        // senkron döngüsü bunu son sayfa sanıp replikayı komple silerdi.
        var act = () => client.GetCatalogProductsAsync(Guid.NewGuid(), after: null);

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetCatalogProductsAsync_bozuk_govdeyi_LicenseApi_hatasina_cevirir()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "{\"oops\":1}"));

        // Çağıran (senkron döngüsü) bu dosyadan LicenseApiException bekliyor.
        // Ham JsonException sızsaydı hosted service sessizce ölür, senkron durur.
        var act = () => client.GetCatalogProductsAsync(Guid.NewGuid(), after: null);

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetCatalogCategoriesAsync_parses_the_tree()
    {
        HttpRequestMessage? seen = null;
        var client = BuildClient(req => { seen = req; return FakeHttpMessageHandler.Json(200, """
            [{ "id":"33333333-3333-3333-3333-333333333333",
               "parentCategoryId":null, "name":"Erkek",
               "path":"/33/", "sortOrder":0, "isActive":true }]
            """); });

        var licenseId = Guid.NewGuid();
        var rows = await client.GetCatalogCategoriesAsync(licenseId);

        // İstek kaydedilmezse test HERHANGİ bir URL'e karşı yeşil kalırdı.
        seen!.RequestUri!.PathAndQuery.Should().Be(
            $"/api/v1/licenses/{licenseId}/catalog/categories");
        rows.Should().ContainSingle();
        rows[0].Name.Should().Be("Erkek");
        rows[0].ParentCategoryId.Should().BeNull();
    }

    [Fact]
    public async Task GetCatalogCategoriesAsync_null_govdeyi_bos_agac_saymaz()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"));

        // Ürün ucuyla aynı sınıf hata: bozuk gövde "kategori yok" demek değil.
        var act = () => client.GetCatalogCategoriesAsync(Guid.NewGuid());

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }

    [Fact]
    public async Task GetCatalogProductsAsync_urun_alanlarini_tel_uzerinden_baglar()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, """
            [{ "id":"11111111-1111-1111-1111-111111111111",
               "categoryId":"55555555-5555-5555-5555-555555555555",
               "code":"A1", "name":"Elbise", "nameSearch":"ELBISE",
               "defaultPrice":199.90, "shelfLocation":"R3-K2",
               "axis1Name":"Beden", "axis1Role":1,
               "axis2Name":"Renk",  "axis2Role":2,
               "updatedAt":"2026-08-13T10:00:00Z",
               "coverPhotoKey":"lic/products/p/k.img",
               "coverPhotoUrl":"https://r2.local/k.img?sig=1",
               "variants":[{ "id":"44444444-4444-4444-4444-444444444444",
                             "axis1Value":"M","axis1Code":"M",
                             "axis2Value":"Kirmizi","axis2Code":"KRM",
                             "variantCode":"A1-M-KRM","barcode":"8690000000001",
                             "isActive":true }] }]
            """));

        var p = (await client.GetCatalogProductsAsync(Guid.NewGuid(), after: null))[0];

        // Kod, yayında yorumla eşleşen TEK alan — bağlanmazsa yanlış ürün satılır.
        p.Code.Should().Be("A1");
        p.Id.Should().Be(Guid.Parse("11111111-1111-1111-1111-111111111111"));
        p.CategoryId.Should().Be(Guid.Parse("55555555-5555-5555-5555-555555555555"));
        p.Name.Should().Be("Elbise");
        p.NameSearch.Should().Be("ELBISE");
        // Para: 199.90 tam gelmeli, araya double girmemeli (bkz. CatalogReplicaRepository).
        p.DefaultPrice.Should().Be(199.90m);
        // decimal'e özgü: double olsaydı bu çarpım 599.6999999999999 düşerdi.
        (p.DefaultPrice * 3).Should().Be(599.70m);
        p.ShelfLocation.Should().Be("R3-K2");
        p.Axis1Name.Should().Be("Beden");
        p.Axis1Role.Should().Be(1);
        p.Axis2Name.Should().Be("Renk");
        p.Axis2Role.Should().Be(2);
        // Yerel replika unix SANİYE saklıyor; damganın UTC çözüldüğünü sabitle.
        p.UpdatedAt.Should().Be(DateTimeOffset.Parse("2026-08-13T10:00:00Z"));
        p.UpdatedAt.ToUnixTimeSeconds().Should().Be(1786615200);
        p.CoverPhotoKey.Should().Be("lic/products/p/k.img");
        // İmzalı adres de bağlanmalı: null gelmesi MEŞRU sayıldığı için (bkz.
        // CatalogPullDtos) bozuk bir bağlama sessizce "fotoğraf yok"a düşer.
        p.CoverPhotoUrl.Should().Be("https://r2.local/k.img?sig=1");

        // Varyant sırası = dizi sırası; 025'teki SortOrder sözleşmesi buna dayanıyor.
        p.Variants.Should().ContainSingle();
        p.Variants[0].VariantCode.Should().Be("A1-M-KRM");
        p.Variants[0].Axis1Value.Should().Be("M");
        p.Variants[0].Axis2Code.Should().Be("KRM");
        p.Variants[0].IsActive.Should().BeTrue();
    }
```

- [ ] **Step 3: Koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~LicenseApiClientTests"
```
Beklenen: FAIL — `GetCatalogProductsAsync` tanımlı değil (derleme hatası).

- [ ] **Step 4: İstemci metotlarını yaz**

`LicenseApiClient.cs`'te `GetWpfCustomersSinceAsync`'in hemen altına:

```csharp
    // ─── WPF katalog replikası (Stok Faz 1b) ──────────────────────────────

    // Bu iki metot, dosyadaki diğer liste uçlarından (örn.
    // GetWpfCustomersSinceAsync) BİLEREK ayrılıyor: onlarda `?? new()` ile boş
    // liste dönmek zararsız, burada boş liste DÖNGÜ SONLANDIRICISI. Bozuk bir
    // gövde (200 + literal `null`) sessizce boş listeye çevrilirse çekme döngüsü
    // "katalog boş" sanır ve işlemsel DELETE+INSERT replikayı komple siler —
    // ardından yayında hiçbir ürün kodu eşleşmez. Bu yüzden coerce etmiyoruz,
    // fırlatıyoruz. Gerçek hatalar zaten gürültülü (ThrowMappedAsync); geriye
    // yalnız bu sessiz dönüşüm kalıyordu.

    /// <summary>
    /// Kataloğun bir sayfasını çeker. <b>Tam anlık görüntü</b> —
    /// <paramref name="after"/> bir DEĞİŞİM imleci değil, birincil anahtar
    /// üstünde keyset sayfalama imleci.
    /// <para>Çağıran, boş sayfa gelene kadar son ürünün <c>Id</c>'siyle döngüye
    /// devam ETMELİ ve ancak tamamı geldiğinde replikayı baştan yazmalıdır.</para>
    /// <para><paramref name="take"/> 1..500 dışındaysa metot <b>fırlatır</b>, sessizce
    /// kırpmaz: sunucunun onurlandırmayacağı bir take'i elinde tutan çağıran, dönen
    /// sayfayı "eksik" sanıp döngüyü erken bitirirdi.</para>
    /// <para><b>Dönen satır sayısı &lt; take'i bitiş göstergesi olarak KULLANMA.</b>
    /// Tek güvenilir bitiş işareti <c>rows.Count == 0</c>'dır: son dolu sayfa da tam
    /// olarak take satır içerebilir — o zaman "eksik sayfa" hiç görünmez — ve
    /// sunucunun sayfa davranışı bir gün değişse bile boş sayfa kuralı bozulmaz.</para>
    /// <para>İmleci <c>rows[^1].Id</c>'den al, sayfayı yeniden SIRALAMA: sunucu
    /// sırası SQL Server'ın <c>uniqueidentifier</c> karşılaştırmasında üretiliyor,
    /// .NET'in <c>Guid.CompareTo</c> sırası farklı düşer ve satır atlatır.</para>
    /// <para>Hata durumunda boş liste DÖNMEZ, <see cref="LicenseApiException"/>
    /// fırlatır — 404/401/5xx/ağ <b>ve bozuk gövde</b> (JSON değil, kesik ya da
    /// şemaya uymayan yanıt) dahil hepsi bu tek aileden gelir. Çağıran döngünün
    /// tamamını tek bir <c>catch (LicenseApiException)</c> ile sarmalayıp herhangi
    /// bir hatada replikayı yazmadan çıkabilir.</para>
    /// </summary>
    public async Task<List<CatalogProductPullItem>> GetCatalogProductsAsync(
        Guid licenseId, Guid? after, int take = 200, CancellationToken ct = default)
    {
        // Sunucu take'i 1..500'e kırpıyor (LicensesWpfCatalogPullController).
        // Sessizce kırpmıyoruz: take by-value, çağıran kendi elindeki 1000'i
        // görmeye devam eder ve "500 < 1000, demek ki son sayfa" diye döngüyü
        // erken bitirir — ardından gelen tam-yenileme kataloğun kalanını siler.
        // Sınır dışı take bir ÇAĞIRAN HATASI; sessizce düzeltmek yerine fırlat.
        if (take is < 1 or > 500)
            throw new ArgumentOutOfRangeException(nameof(take), take,
                "take 1..500 olmalı (sunucu sınırı, LicensesWpfCatalogPullController).");

        var qs = after is null ? $"?take={take}" : $"?after={after}&take={take}";
        return await GetExpectingJsonAsync<List<CatalogProductPullItem>>(
            $"/api/v1/licenses/{licenseId}/catalog/products{qs}", ct)
            ?? throw new LicenseApiUnknownException(200,
                "Katalog ürün sayfası bozuk geldi (gövde null). Bu 'katalog boş' demek değildir.");
    }

    /// <summary>Kategori ağacının tamamı; sayfalama yok (derinlik sınırlı).
    /// Sunucu <b>pasif</b> kategorileri de döndürür — bir ürün pasif kategoriye
    /// bağlı kalmış olabilir; WPF <c>IsActive == false</c> satırları beklemeli,
    /// bozuk veri saymamalıdır. Bozuk gövdede boş liste dönmez, fırlatır.</summary>
    public async Task<List<CatalogCategoryPullItem>> GetCatalogCategoriesAsync(
        Guid licenseId, CancellationToken ct = default)
        => await GetExpectingJsonAsync<List<CatalogCategoryPullItem>>(
            $"/api/v1/licenses/{licenseId}/catalog/categories", ct)
            ?? throw new LicenseApiUnknownException(200,
                "Katalog kategori ağacı bozuk geldi (gövde null). Bu 'kategori yok' demek değildir.");
```

Yukarıdaki XML doc "bozuk gövde de fırlatır" diyor; bu yalnız literal `null`
için doğruydu. Şemaya uymayan/kesik gövde ham `JsonException` olarak sızıyordu
ve çağıranın `catch (LicenseApiException)`'ını ıskalayıp hosted service'i
sessizce öldürürdü. Sözleşmeyi doğru kılmak için paylaşılan yardımcıya
(`GetExpectingJsonAsync`) tek satırlık bir çeviri ekleniyor — çözümü zaten
`JsonException` yakalayan hiçbir çağıran yok (tüm solution grep'lendi:
`EncryptedStore`, `ExtensionBridgeServer`, FB/YT token store'ları ve Instagram
parser'ları; hiçbiri bu istemciden geçmiyor), yani bu yalnızca daha önce
yakalanmayan bir çökmeyi dosyanın belgelenmiş hata tipine dönüştürüyor:

```csharp
                if (!resp.IsSuccessStatusCode) await ThrowMappedAsync(resp);
                try { return (await DeserializeAsync<TResp>(resp, ct))!; }
                catch (JsonException ex)
                {
                    // Gövde JSON değil ya da şemaya uymuyor (kesik yanıt, JSON
                    // content-type'lı HTML hata sayfası...). Çağıran bu dosyadan
                    // LicenseApiException bekliyor; ham JsonException sızmasın.
                    throw new LicenseApiUnknownException((int)resp.StatusCode,
                        $"Gövde çözümlenemedi: {ex.Message}");
                }
```

- [ ] **Step 5: Koştur, geçtiğini gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~LicenseApiClientTests"
```
Beklenen: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs \
        OrderDeck.Licensing/Api/LicenseApiClient.cs \
        OrderDeck.Tests/Licensing/LicenseApiClientTests.cs
git commit -m "feat(katalog): katalog çekme istemci metotları"
```

---

### Task 6: Fotoğraf önbelleği

**Files:**
- Create: `OrderDeck.App/Services/CatalogPhotoCache.cs`
- Test: `OrderDeck.Tests/App/CatalogPhotoCacheTests.cs`

- [ ] **Step 1: Testleri yaz (başarısız olacak)**

`OrderDeck.Tests/App/CatalogPhotoCacheTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.App.Services;
using Xunit;

namespace OrderDeck.Tests.App;

public class CatalogPhotoCacheTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "od-photo-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void Save_then_resolve_round_trips_a_key_with_slashes()
    {
        var cache = new CatalogPhotoCache(_root);
        const string key = "abc/products/def/kapak.img";

        cache.Has(key).Should().BeFalse();
        cache.Save(key, [1, 2, 3]);

        cache.Has(key).Should().BeTrue();
        var path = cache.ResolveAbsolute(key);
        File.ReadAllBytes(path!).Should().Equal(1, 2, 3);
    }

    [Fact]
    public void ResolveAbsolute_returns_null_for_unknown_or_empty_keys()
    {
        var cache = new CatalogPhotoCache(_root);

        cache.ResolveAbsolute(null).Should().BeNull();
        cache.ResolveAbsolute("").Should().BeNull();
        cache.ResolveAbsolute("hic/olmayan.img").Should().BeNull();
    }

    [Fact]
    public void Prune_deletes_files_whose_key_is_no_longer_live()
    {
        var cache = new CatalogPhotoCache(_root);
        cache.Save("a/kalan.img", [1]);
        cache.Save("a/giden.img", [2]);

        cache.Prune(["a/kalan.img"]);

        cache.Has("a/kalan.img").Should().BeTrue();
        cache.Has("a/giden.img").Should().BeFalse();
    }
}
```

- [ ] **Step 2: Koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~CatalogPhotoCacheTests"
```
Beklenen: FAIL — `CatalogPhotoCache` tipi yok.

- [ ] **Step 3: Önbelleği yaz**

`OrderDeck.App/Services/CatalogPhotoCache.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace OrderDeck.App.Services;

/// <summary>
/// Katalog kapak fotoğraflarının disk önbelleği.
///
/// Anahtar R2 nesne anahtarı (<c>{licenseId:N}/products/{productId:N}/x.img</c>)
/// — yani <b>eğik çizgi içeriyor</b> ve doğrudan dosya adı olamaz. Dosya adı
/// anahtarın SHA-256 özeti: hem düzleştirir hem uzunluk sınırını kaldırır hem
/// de <c>..</c> gibi bir şeyin köke kaçmasını yapısal olarak imkânsız kılar.
///
/// Sunucudan gelen indirme adresi 5 dakikada geçersizleşiyor; kalıcı olan tek
/// şey anahtar, o yüzden önbellek anahtarla adresleniyor. Fotoğraf değişince
/// nesne anahtarı da değişir (panel yeni bir GUID üretiyor), dolayısıyla
/// bayat içerik dönme ihtimali yok — eskisi <see cref="Prune"/> ile düşer.
/// </summary>
public sealed class CatalogPhotoCache
{
    private readonly string _root;

    /// <param name="root">
    /// Yalnız test için. Üretimde null → %LOCALAPPDATA%\OrderDeck\catalog-photos.
    /// </param>
    public CatalogPhotoCache(string? root = null)
    {
        _root = root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OrderDeck", "catalog-photos");
    }

    public bool Has(string? objectKey) => ResolveAbsolute(objectKey) is not null;

    public void Save(string objectKey, byte[] bytes)
    {
        Directory.CreateDirectory(_root);
        File.WriteAllBytes(Path.Combine(_root, FileNameFor(objectKey)), bytes);
    }

    /// <summary>Önbellekteki dosyanın tam yolu; yoksa null (view placeholder gösterir).</summary>
    public string? ResolveAbsolute(string? objectKey)
    {
        if (string.IsNullOrWhiteSpace(objectKey)) return null;
        var full = Path.Combine(_root, FileNameFor(objectKey));
        return File.Exists(full) ? full : null;
    }

    /// <summary>
    /// Canlı anahtar listesinde olmayan dosyaları siler. Katalogdan düşen ürünün
    /// fotoğrafı sonsuza kadar diskte kalmasın diye her senkron turunda çağrılır.
    /// </summary>
    public void Prune(IEnumerable<string> liveKeys)
    {
        if (!Directory.Exists(_root)) return;

        var keep = liveKeys.Select(FileNameFor).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(_root, "*.img").ToList())
        {
            if (keep.Contains(Path.GetFileName(file))) continue;
            // Dosya kilitliyse (Image hâlâ bağlı) atla: temizlik bir sonraki
            // turda yeniden denenir, önbellek tutarlılığı bundan etkilenmez.
            try { File.Delete(file); } catch (IOException) { }
        }
    }

    private static string FileNameFor(string objectKey)
        => Convert.ToHexString(
               SHA256.HashData(Encoding.UTF8.GetBytes(objectKey))).ToLowerInvariant()
           + ".img";
}
```

- [ ] **Step 4: Koştur, geçtiğini gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~CatalogPhotoCacheTests"
```
Beklenen: PASS (3/3).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/Services/CatalogPhotoCache.cs \
        OrderDeck.Tests/App/CatalogPhotoCacheTests.cs
git commit -m "feat(katalog): kapak fotoğrafı disk önbelleği"
```

---

### Task 7: Senkron servisi ve arka plan işi

**Files:**
- Create: `OrderDeck.App/Services/Sync/CatalogSyncService.cs`
- Create: `OrderDeck.App/Services/Sync/CatalogSyncHostedService.cs`
- Modify: `OrderDeck.App/AppHost.cs` (satır ~511 civarı, shopper ingest kayıtlarının yanına)
- Test: `OrderDeck.Tests/Services/Sync/CatalogSyncServiceTests.cs`

> **Test koşum takımı:** lisans kimliği çözümlemesi `GetMyLicensesAsync`
> üstünden gittiği için sahte HTTP yanıtları hem lisans listesini hem çekme
> ucunu karşılamalı. Bu tam olarak
> `OrderDeck.Tests/Services/Sync/ShopperRegistrationIngestServiceTests.cs`
> içinde çözülmüş durumda — oradaki kurulum yardımcısını (sahte handler'ın
> yönlendirme mantığı, `ICurrentLicenseProvider` sahtesi, lisans listesi JSON'u)
> **birebir** al ve yalnız yönlendirmeye iki dal ekle: yolu `/catalog/products`
> içeren istekler ürün sayfasını, `/catalog/categories` içerenler kategori
> listesini döndürsün. Yeni bir desen icat etme.

- [ ] **Step 1: Testleri yaz (başarısız olacak)**

`OrderDeck.Tests/Services/Sync/CatalogSyncServiceTests.cs` — dört senaryo:

```csharp
    [Fact]
    public async Task Pulls_every_page_until_a_short_page_arrives()
    {
        // 200'lük iki tam sayfa + 1 kayıtlık üçüncü sayfa = 401 ürün.
        // İkinci sayfanın isteği, birinci sayfanın SON ürününün id'sini
        // `after` olarak taşımalı.
        var harness = Harness.WithProductPages(pageSizes: [200, 200, 1]);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(401);
        harness.RequestedAfterCursors.Should().Equal(
            null, harness.LastIdOfPage(0), harness.LastIdOfPage(1));
        harness.Repo.FindByCode("A401").Should().NotBeNull();
    }

    [Fact]
    public async Task A_failure_midway_leaves_the_previous_replica_untouched()
    {
        var harness = Harness.WithProductPages(pageSizes: [200, 200, 1]);
        await harness.Service.SyncOnceAsync(CancellationToken.None);

        // İkinci sayfada 500: yarım liste ASLA yazılmamalı, yoksa silinmemiş
        // 201 ürün silinmiş sayılırdı.
        harness.FailProductPage(index: 1);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(0);
        harness.Repo.FindByCode("A401").Should().NotBeNull();
    }

    [Fact]
    public async Task Downloads_only_the_photos_that_are_not_cached_yet()
    {
        var harness = Harness.WithProductPages(pageSizes: [2], withPhotos: true);
        await harness.Service.SyncOnceAsync(CancellationToken.None);
        harness.PhotoDownloads.Should().Be(2);

        // İkinci turda anahtarlar değişmedi → tek bayt bile indirilmemeli.
        await harness.Service.SyncOnceAsync(CancellationToken.None);
        harness.PhotoDownloads.Should().Be(2);
    }

    [Fact]
    public async Task Returns_zero_without_calling_the_api_when_no_license_key_is_set()
    {
        var harness = Harness.WithProductPages(pageSizes: [1]);
        harness.SetLicenseKey(null);

        var written = await harness.Service.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(0);
        harness.RequestCount.Should().Be(0);
    }
```

- [ ] **Step 2: Koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~CatalogSyncServiceTests"
```
Beklenen: FAIL — `CatalogSyncService` tipi yok.

- [ ] **Step 3: Servisi yaz**

`OrderDeck.App/Services/Sync/CatalogSyncService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Api.Models;
using OrderDeck.Shared.Text;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Sunucu kataloğunu yerel replikaya çeker.
///
/// <b>Ya hep ya hiç:</b> sayfalar tamamlanmadan replikaya hiçbir şey yazılmaz.
/// Sunucu tam anlık görüntü döndürdüğü için yazma bir <c>DELETE + INSERT</c>;
/// yarım listeyle yazsaydık, ağ ikinci sayfada koptuğunda silinmemiş yüzlerce
/// ürün "panelden silinmiş" muamelesi görürdü.
/// </summary>
public sealed class CatalogSyncService
{
    private const int PageSize = 200;

    /// <summary>
    /// 40.000 ürünlük tavan. Sunucu imleci ilerletmezse (beklenmedik bir hata)
    /// döngü sonsuza kadar dönmesin; katalogun gerçek büyüklüğü yüzler mertebesi.
    /// </summary>
    private const int MaxPages = 200;

    /// <summary>Fotoğraf indirmede kullanılacak KİMLİKSİZ istemci adı.</summary>
    public const string PhotoClientName = "catalog-photos";

    private readonly LicenseApiClient _api;
    private readonly CatalogReplicaRepository _repo;
    private readonly CatalogPhotoCache _photos;
    private readonly IHttpClientFactory _httpFactory;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<CatalogSyncService> _log;

    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public CatalogSyncService(
        LicenseApiClient api,
        CatalogReplicaRepository repo,
        CatalogPhotoCache photos,
        IHttpClientFactory httpFactory,
        ICurrentLicenseProvider licenseProvider,
        ILogger<CatalogSyncService> log)
    {
        _api = api;
        _repo = repo;
        _photos = photos;
        _httpFactory = httpFactory;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    /// <summary>Yazılan ürün sayısı; senkron yapılamadıysa 0.</summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrEmpty(licenseKey)) return 0;

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct);
        if (licenseId is null) return 0;

        try
        {
            var pulled = new List<CatalogProductPullItem>();
            Guid? after = null;

            for (var page = 0; page < MaxPages; page++)
            {
                var batch = await _api.GetCatalogProductsAsync(licenseId.Value, after, PageSize, ct);
                pulled.AddRange(batch);
                if (batch.Count < PageSize) break;
                after = batch[^1].Id;
            }

            var categories = await _api.GetCatalogCategoriesAsync(licenseId.Value, ct);

            // Buraya geldiysek tam anlık görüntü elimizde: tek transaction'da yaz.
            _repo.Replace(
                pulled.Select(ToProduct).ToList(),
                pulled.SelectMany(ToVariants).ToList(),
                categories.Select(ToCategory).ToList());

            await DownloadMissingPhotosAsync(pulled, ct);
            _photos.Prune(_repo.CoverPhotoKeys());

            _log.LogInformation(
                "Katalog senkronu: {Products} ürün, {Categories} kategori",
                pulled.Count, categories.Count);
            return pulled.Count;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Katalog senkronu başarısız; replika olduğu gibi bırakıldı");
            return 0;
        }
    }

    private async Task DownloadMissingPhotosAsync(
        IReadOnlyList<CatalogProductPullItem> pulled, CancellationToken ct)
    {
        // İmzalı adres 5 dakika geçerli — indirme çekmenin hemen ardında.
        // Kimliksiz istemci ŞART: LicenseApiClient'ın istemcisi her isteğe
        // Authorization ekliyor ve presigned bir R2 adresine fazladan başlık
        // göndermek isteği bozar.
        var http = _httpFactory.CreateClient(PhotoClientName);

        foreach (var p in pulled)
        {
            if (p.CoverPhotoKey is not { Length: > 0 } key) continue;
            if (p.CoverPhotoUrl is not { Length: > 0 } url) continue;
            if (_photos.Has(key)) continue;

            try
            {
                _photos.Save(key, await http.GetByteArrayAsync(url, ct));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Tek fotoğrafın düşmesi katalogu düşürmez: kart placeholder
                // gösterir, bir sonraki turda taze bir imzayla yeniden denenir.
                _log.LogDebug(ex, "Kapak fotoğrafı indirilemedi: {Key}", key);
            }
        }
    }

    private static CatalogProduct ToProduct(CatalogProductPullItem p) => new(
        p.Id.ToString("N"),
        p.CategoryId?.ToString("N"),
        p.Code,
        SearchNormalizer.Normalize(p.Code),
        p.Name,
        p.DefaultPrice,
        p.ShelfLocation,
        p.Axis1Name, p.Axis1Role,
        p.Axis2Name, p.Axis2Role,
        p.CoverPhotoKey,
        p.UpdatedAt.ToUnixTimeSeconds());

    private static IEnumerable<CatalogVariant> ToVariants(CatalogProductPullItem p)
        // Sıra sunucunun kararı (VariantCode'a göre); replika onu indeksle korur.
        => p.Variants.Select((v, i) => new CatalogVariant(
            v.Id.ToString("N"), p.Id.ToString("N"),
            v.Axis1Value, v.Axis1Code, v.Axis2Value, v.Axis2Code,
            v.VariantCode, v.Barcode, v.IsActive, i));

    private static CatalogCategory ToCategory(CatalogCategoryPullItem c) => new(
        c.Id.ToString("N"), c.ParentCategoryId?.ToString("N"),
        c.Name, c.Path, c.SortOrder, c.IsActive);

    private async Task<Guid?> ResolveLicenseIdAsync(string licenseKey, CancellationToken ct)
    {
        if (_cachedLicenseId is not null && _cachedLicenseKey == licenseKey)
            return _cachedLicenseId;

        try
        {
            var licenses = await _api.GetMyLicensesAsync(ct);
            var match = licenses.FirstOrDefault(l => l.LicenseKey == licenseKey);
            if (match?.Id is null) return null;

            _cachedLicenseId = match.Id;
            _cachedLicenseKey = licenseKey;
            return _cachedLicenseId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogDebug(ex, "Katalog senkronu için lisans çözümlenemedi");
            return null;
        }
    }
}
```

> `ResolveLicenseIdAsync` ve dış `try/catch` kalıbı
> `ShopperRegistrationIngestService` ile birebir aynı — bilerek. İki servis
> ayrışırsa lisans çözümleme davranışı iki farklı yerde yaşamaya başlar.

- [ ] **Step 4: Arka plan işini yaz**

`OrderDeck.App/Services/Sync/CatalogSyncHostedService.cs` —
`ShopperRegistrationIngestHostedService` ile **aynı iskelet** (açılışta ilk
koşu, `PeriodicTimer`, `WaitSafe`), yalnız periyot farklı:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Katalog replikasını tazeler. Periyot 5 dakika: katalog yayın sırasında
/// nadiren değişiyor (panelde ürün girişi çoğunlukla yayın öncesi), ve her tur
/// TAM anlık görüntü çektiği için sık koşmak sunucuya bedavaya yük bindirir.
/// Açılıştaki ilk koşu, operatör yayına başlamadan replikanın dolmasını sağlıyor.
/// </summary>
public sealed class CatalogSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromMinutes(5);

    private readonly CatalogSyncService _service;
    private readonly ILogger<CatalogSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public CatalogSyncHostedService(
        CatalogSyncService service, ILogger<CatalogSyncHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Testler için kısa periyot enjekte eder.
    internal CatalogSyncHostedService(
        CatalogSyncService service, ILogger<CatalogSyncHostedService> log, TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation(
            "CatalogSyncHostedService starting (cadence={Cadence})", _interval);

        try { await _service.SyncOnceAsync(stoppingToken); }
        catch (OperationCanceledException) { return; }
        catch (Exception ex) { _log.LogWarning(ex, "İlk katalog senkronu başarısız"); }

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            try { await _service.SyncOnceAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Katalog senkron turu başarısız; sonraki turda yeniden denenecek");
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

- [ ] **Step 5: DI kayıtlarını ekle**

`AppHost.cs`, shopper ingest kayıtlarının (satır ~511-512) hemen altına:

```csharp
        // Katalog replikası (Stok Faz 1b): sunucudaki katalogun tam anlık
        // görüntüsü 5 dakikada bir yerel SQLite'a yazılır. Fotoğraf baytları
        // presigned R2 adresinden KİMLİKSİZ bir istemciyle çekiliyor —
        // LicenseApiClient'ın istemcisi Authorization ekler ve presigned
        // isteği bozardı.
        services.AddHttpClient(Services.Sync.CatalogSyncService.PhotoClientName);
        services.AddSingleton<Services.Sync.CatalogSyncService>();
        services.AddHostedService<Services.Sync.CatalogSyncHostedService>();
```

`CatalogReplicaRepository` ve `CatalogPhotoCache` kayıtlarını, eski
`ProductRepository`/`ProductPhotoStore` satırlarının (84 ve 86) yanına ekle:

```csharp
        services.AddSingleton<CatalogReplicaRepository>();
        services.AddSingleton<CatalogPhotoCache>();
```

> Hosted service'i `App.xaml.cs`'e elle eklemeye gerek **yok**:
> `WpfStartupEnvironment.StartBackgroundServicesAsync()` içindeki genel döngü
> kayıtlı bütün `IHostedService`'leri başlatıyor.

- [ ] **Step 6: Koştur, geçtiğini gör**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~CatalogSyncServiceTests"
```
Beklenen: derleme 0 hata, testler PASS (4/4).

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.App/Services/Sync/CatalogSyncService.cs \
        OrderDeck.App/Services/Sync/CatalogSyncHostedService.cs \
        OrderDeck.App/AppHost.cs \
        OrderDeck.Tests/Services/Sync/CatalogSyncServiceTests.cs
git commit -m "feat(katalog): WPF katalog senkron servisi"
```

---

### Task 8: Ürün kartı replikadan okusun; yerel ürün tanımı kaldırılsın

Bu görev **yıkıcı** olan tek görev. Sıraya en sona konmasının sebebi Task 3'te
yazıldı: `Product`/`ProductSize` tabloları düşerse `ProductRepository` ve eski
`ProductCardViewModel` ayaktayken testler kırılır. Burada önce sunum katmanı
replikaya bağlanıyor, sonra artık kimsenin kullanmadığı eski katman siliniyor,
en son tablolar düşüyor — tek commit içinde ama sırayla.

**Kartın yeni davranışı:** yalnız okur. Operatör ürün tanımlayamaz, ad/fotoğraf
değiştiremez, beden ekleyemez — katalogun tek sahibi panel. Adet **gösterilmez**:
stok bakiyeleri `LicensesWpfStockPullController`'dan gelir ve o **plan 3'ün**
konusu. Bu planın sonunda kart; kapak fotoğrafını, kodu, adı ve varyant
listesini gösterir.

**Files:**
- Create: `OrderDeck.App/ViewModels/CatalogVariantViewModel.cs`
- Create: `OrderDeck.Core/Storage/Migrations/026_drop_local_products.sql`
- Modify: `OrderDeck.App/ViewModels/ProductCardViewModel.cs` (baştan yazılır)
- Modify: `OrderDeck.App/Views/Shell/ProductCard.xaml` (C ve D bölümleri gider)
- Modify: `OrderDeck.App/Views/Shell/ProductCard.xaml.cs` (baştan yazılır)
- Modify: `OrderDeck.App/AppHost.cs:83-86`
- Modify: `OrderDeck.Tests/App/MainShellTestHarness.cs:105-109`
- Modify: `OrderDeck.Tests/App/MainShellPrintTests.cs:166-170`
- Modify: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs:32,56`
- Delete: `OrderDeck.Core/Catalog/Product.cs`
- Delete: `OrderDeck.Core/Storage/Repositories/ProductRepository.cs`
- Delete: `OrderDeck.App/Services/ProductPhotoStore.cs`
- Delete: `OrderDeck.App/ViewModels/ProductSizeViewModel.cs`
- Delete: `OrderDeck.Tests/Storage/ProductRepositoryTests.cs`
- Delete: `OrderDeck.Tests/App/ProductPhotoStoreTests.cs`
- Test: `OrderDeck.Tests/App/ProductCardViewModelTests.cs` (baştan yazılır)

> `MainShellViewModel` **değişmiyor**: ctor'u `ProductCardViewModel` tipini
> alıyor, tip adı aynı kalıyor. `MainShellViewCompositionTests.cs:53` de
> değişmiyor — orada `new ProductCard()` parametresiz kuruluyor.

- [ ] **Step 1: Testleri baştan yaz (başarısız olacak)**

`OrderDeck.Tests/App/ProductCardViewModelTests.cs` — **dosyanın tamamını** bu
içerikle değiştir (eski testler tanımlama/kaydetme akışını sınıyordu, o akış
artık yok):

```csharp
using System.IO;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Ürün kartı artık SALT OKUR: kaynağı sunucu kataloğunun yerel replikası.
/// Tanımlama/düzenleme/fotoğraf seçme akışları kaldırıldı (katalogun sahibi
/// panel), o yüzden buradaki testler yalnız Load'un dört durumunu sınıyor.
/// </summary>
public class ProductCardViewModelTests
{
    private static (ProductCardViewModel Vm, CatalogReplicaRepository Repo, string Root) Make()
    {
        var db = InMemorySqlite.Create();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);
        var root = Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"));
        return (new ProductCardViewModel(repo, new CatalogPhotoCache(root)), repo, root);
    }

    private static CatalogProduct Product(
        string id, string code, string name = "Elbise", string? coverKey = null)
        => new(id, null, code, SearchNormalizer.Normalize(code), name,
               199.90m, null, "Renk", 1, "Beden", 2, coverKey, 1_700_000_000);

    [Fact]
    public void Empty_code_shows_neither_product_nor_unknown()
    {
        var (vm, _, _) = Make();

        vm.Load("   ");

        vm.Code.Should().BeEmpty();
        vm.HasProduct.Should().BeFalse();
        vm.IsUnknown.Should().BeFalse();
        vm.Variants.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_code_is_reported_without_clearing_the_typed_code()
    {
        var (vm, _, _) = Make();

        vm.Load("A7");

        // Kod ekranda kalmalı: operatör neyi yazdığını görsün.
        vm.Code.Should().Be("A7");
        vm.HasProduct.Should().BeFalse();
        vm.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Known_code_loads_name_and_active_variants()
    {
        var (vm, repo, _) = Make();
        repo.Replace(
            [Product("p1", "GUZEL ELBISE", "Güzel Elbise")],
            [
                new CatalogVariant("v1", "p1", "Kırmızı", "KIRM", "M", "M",
                                   "GUZEL ELBISE-KIRM-M", null, true, 0),
                new CatalogVariant("v2", "p1", "Kırmızı", "KIRM", "L", "L",
                                   "GUZEL ELBISE-KIRM-L", null, false, 1),
            ],
            []);

        vm.Load("güzel elbise");

        vm.HasProduct.Should().BeTrue();
        vm.IsUnknown.Should().BeFalse();
        vm.Name.Should().Be("Güzel Elbise");
        // Pasif varyant gösterilmez: operatör satamayacağı bir kırılımı görmesin.
        vm.Variants.Should().ContainSingle().Which.Display.Should().Be("Kırmızı · M");
    }

    [Fact]
    public void Photo_path_is_null_until_the_cover_file_is_cached()
    {
        var (vm, repo, root) = Make();
        var photos = new CatalogPhotoCache(root);
        repo.Replace([Product("p1", "A1", coverKey: "lic/p1/kapak.img")], [], []);

        vm.Load("A1");
        vm.PhotoAbsolutePath.Should().BeNull();

        // Senkron fotoğrafı indirdikten sonra aynı kod yeniden okunduğunda yol dolar.
        photos.Save("lic/p1/kapak.img", [1, 2, 3]);
        vm.Load("A1");
        vm.PhotoAbsolutePath.Should().NotBeNull();
    }

    [Fact]
    public void Variant_without_axis_values_falls_back_to_its_code()
    {
        var vm = new CatalogVariantViewModel(
            new CatalogVariant("v1", "p1", null, null, null, null, "A1", null, true, 0));

        vm.Display.Should().Be("A1");
    }
}
```

- [ ] **Step 2: Koştur, başarısız olduğunu gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~ProductCardViewModelTests"
```
Beklenen: derleme hatası — `CatalogVariantViewModel` yok, `ProductCardViewModel`
ctor'u `CatalogReplicaRepository` almıyor.

- [ ] **Step 3: `CatalogVariantViewModel`'i yaz**

`OrderDeck.App/ViewModels/CatalogVariantViewModel.cs`:

```csharp
using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Ürün kartındaki tek varyant satırı. <b>Salt okunur</b> ve
/// <c>ObservableObject</c> değil: replikadan gelen satır kartın ömrü boyunca
/// değişmiyor, her <c>Load</c> koleksiyonu baştan kuruyor. Adet alanı YOK —
/// bakiyeler stok defterinden gelecek (plan 3).
/// </summary>
public sealed class CatalogVariantViewModel
{
    public CatalogVariantViewModel(CatalogVariant variant)
    {
        VariantCode = variant.VariantCode;

        var parts = new[] { variant.Axis1Value, variant.Axis2Value }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());

        var label = string.Join(" · ", parts);

        // Eksensiz üründe de tam bir varyant var; gösterilecek değer yoksa
        // boş kutu yerine varyant kodunu yazıyoruz.
        Display = label.Length > 0 ? label : VariantCode;
    }

    public string VariantCode { get; }

    public string Display { get; }
}
```

- [ ] **Step 4: `ProductCardViewModel`'i baştan yaz**

`OrderDeck.App/ViewModels/ProductCardViewModel.cs` — **dosyanın tamamı**:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.App.Services;
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Sağ paneldeki ürün kartı. Kaynağı sunucu kataloğunun yerel replikası
/// (<see cref="CatalogReplicaRepository"/>), yazma yolu YOK.
///
/// Neden salt okunur: katalogun tek sahibi panel. Operatör burada ürün
/// tanımlayabilseydi aynı ürünün iki ayrı gerçeği olurdu (yerelde tanımlı,
/// sunucuda yok) ve stok hareketi hangi ürüne yazılacağı belirsizleşirdi.
///
/// Üç durum: kod yok (boş kart) · kod var ama katalogda yok
/// (<see cref="IsUnknown"/>) · kod katalogda var (<see cref="HasProduct"/>).
/// Bilinmeyen kod bir <b>hata değil</b>: operatör kodu yazarken her ara tuş
/// vuruşu tanınmayan bir koddur, akış kesilmez.
/// </summary>
public sealed partial class ProductCardViewModel : ObservableObject
{
    private readonly CatalogReplicaRepository _repo;
    private readonly CatalogPhotoCache _photos;

    public ProductCardViewModel(CatalogReplicaRepository repo, CatalogPhotoCache photos)
    {
        _repo = repo;
        _photos = photos;
    }

    public ObservableCollection<CatalogVariantViewModel> Variants { get; } = new();

    [ObservableProperty]
    private string _code = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private bool _hasProduct;

    [ObservableProperty]
    private bool _isUnknown;

    /// <summary>R2 nesne anahtarı; dosya yolu değil.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PhotoAbsolutePath))]
    private string? _coverPhotoKey;

    /// <summary>
    /// Önbellekte dosya yoksa <c>null</c> — Image bağı boş kalır, kart
    /// bozulmaz. Senkron fotoğrafı indirince sonraki <see cref="Load"/>
    /// yolu doldurur.
    /// </summary>
    public string? PhotoAbsolutePath => _photos.ResolveAbsolute(CoverPhotoKey);

    /// <summary>
    /// Kartı verilen ürün koduna göre tazeler. Kod büyük/küçük harf ve Türkçe
    /// harf farkından bağımsız aranır (<c>SearchNormalizer</c> hem replikaya
    /// yazarken hem burada uygulanıyor).
    /// </summary>
    public void Load(string? code)
    {
        var trimmed = (code ?? string.Empty).Trim();
        if (trimmed.Length == 0)
        {
            Reset(string.Empty, unknown: false);
            return;
        }

        var product = _repo.FindByCode(trimmed);
        if (product is null)
        {
            Reset(trimmed, unknown: true);
            return;
        }

        Code = product.Code;
        Name = product.Name;
        CoverPhotoKey = product.CoverPhotoKey;
        HasProduct = true;
        IsUnknown = false;

        Variants.Clear();
        foreach (var v in _repo.GetVariants(product.Id))
        {
            // Pasif varyant gösterilmez: satılamayacak bir kırılım karta
            // girerse operatör onu okutmayı dener.
            if (v.IsActive) Variants.Add(new CatalogVariantViewModel(v));
        }
    }

    private void Reset(string code, bool unknown)
    {
        Code = code;
        Name = string.Empty;
        CoverPhotoKey = null;
        HasProduct = false;
        IsUnknown = unknown;
        Variants.Clear();
    }
}
```

- [ ] **Step 5: Koştur, geçtiğini gör**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj \
  --filter "FullyQualifiedName~ProductCardViewModelTests"
```
Beklenen: PASS (5/5).

- [ ] **Step 6: XAML'i sadeleştir**

`OrderDeck.App/Views/Shell/ProductCard.xaml` — **dosyanın tamamı**:

```xml
<UserControl x:Class="OrderDeck.App.Views.Shell.ProductCard"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <UserControl.Resources>
        <!-- Varyant rozeti. Eski SizeTile'ın yerini alıyor: adet kutusu YOK,
             çünkü bakiye stok defterinden gelecek (plan 3) ve elle
             düzenlenebilir bir sayı olmayacak. -->
        <DataTemplate x:Key="VariantChip">
            <Border Background="{StaticResource OD.Brush.Surface2}"
                    BorderBrush="{StaticResource OD.Brush.Border}"
                    BorderThickness="1"
                    CornerRadius="{StaticResource OD.Radius.Sm}"
                    Padding="{StaticResource OD.Pad.2}"
                    Margin="{StaticResource OD.Pad.1}">
                <TextBlock Text="{Binding Display}"
                           HorizontalAlignment="Center"
                           TextTrimming="CharacterEllipsis"
                           FontSize="{StaticResource OD.Font.F1}"
                           Foreground="{StaticResource OD.Brush.TextDim}"/>
            </Border>
        </DataTemplate>
    </UserControl.Resources>

    <Border Style="{StaticResource OD.Panel}" Padding="{StaticResource OD.Pad.4}"
            DataContext="{Binding ProductCard}">
        <Grid>
            <!-- ── A: kod girilmemiş ────────────────────────────────────── -->
            <TextBlock Text="Ürün kodu gir"
                       HorizontalAlignment="Center" VerticalAlignment="Center">
                <TextBlock.Style>
                    <Style TargetType="TextBlock" BasedOn="{StaticResource OD.Text.Micro}">
                        <Setter Property="Visibility" Value="Collapsed"/>
                        <Style.Triggers>
                            <DataTrigger Binding="{Binding Code}" Value="">
                                <Setter Property="Visibility" Value="Visible"/>
                            </DataTrigger>
                        </Style.Triggers>
                    </Style>
                </TextBlock.Style>
            </TextBlock>

            <!-- ── D: kod katalogda yok ─────────────────────────────────── -->
            <!-- "Ürün Tanımla" butonu KALDIRILDI: katalogun sahibi panel.
                 Operatöre ne yapacağı söyleniyor, akış kesilmiyor. -->
            <StackPanel VerticalAlignment="Center"
                        Visibility="{Binding IsUnknown,
                                     Converter={StaticResource BoolToVisibleConverter}}">
                <TextBlock Style="{StaticResource OD.Text.Micro}"
                           HorizontalAlignment="Center">
                    <Run Text="{Binding Code, Mode=OneWay}"/>
                    <Run Text="katalogda yok"/>
                </TextBlock>
                <TextBlock Text="Ürünü panelden ekleyin"
                           Style="{StaticResource OD.Text.Micro}"
                           HorizontalAlignment="Center"
                           Margin="{StaticResource OD.Pad.Top5}"/>
            </StackPanel>

            <!-- ── B: görüntüleme ───────────────────────────────────────── -->
            <!-- IsEditing gitti, tek koşul kaldı: artık üst üste binebilecek
                 ikinci bir dolu durum yok. -->
            <StackPanel Visibility="{Binding HasProduct,
                                     Converter={StaticResource BoolToVisibleConverter}}">
                <Border CornerRadius="{StaticResource OD.Radius.Md}"
                        Background="{StaticResource OD.Brush.Surface2}"
                        ClipToBounds="True">
                    <Border.Style>
                        <Style TargetType="Border">
                            <Setter Property="Height"
                                    Value="{StaticResource OD.Layout.ProductImageHeight}"/>
                            <Style.Triggers>
                                <!-- 850px altı: fotoğraf kısalır, liste ekranda kalır.
                                     Ata UserControl = bu ProductCard; DataContext'i
                                     MainShellViewModel (ProductCard'a inen bağ alttaki
                                     Border'da, kökte değil). Pencereye çıkma —
                                     MainWindow'un DataContext'i yok. -->
                                <DataTrigger Binding="{Binding DataContext.IsShort,
                                             RelativeSource={RelativeSource AncestorType=UserControl}}"
                                             Value="True">
                                    <Setter Property="Height"
                                            Value="{StaticResource OD.Layout.ProductImageHeightShort}"/>
                                </DataTrigger>
                            </Style.Triggers>
                        </Style>
                    </Border.Style>
                    <Image Source="{Binding PhotoAbsolutePath}" Stretch="UniformToFill"
                           RenderOptions.BitmapScalingMode="HighQuality"/>
                </Border>

                <TextBlock Text="{Binding Code}"
                           Style="{StaticResource OD.Text.Mono}"
                           Margin="{StaticResource OD.Pad.Top5}"/>

                <TextBlock Text="{Binding Name}" TextWrapping="Wrap"
                           FontFamily="{StaticResource OD.Font.Display}"
                           FontSize="{StaticResource OD.Font.F2}"
                           Foreground="{StaticResource OD.Brush.Text}"/>

                <!-- Varyantsız (tek varyantlı, eksensiz) üründe liste tek
                     rozet gösterir; başlık yine de anlamlı kalıyor. -->
                <TextBlock Text="VARYANTLAR" Style="{StaticResource OD.Text.Micro}"
                           Margin="{StaticResource OD.Pad.Top5}"/>
                <ItemsControl ItemsSource="{Binding Variants}"
                              ItemTemplate="{StaticResource VariantChip}">
                    <ItemsControl.ItemsPanel>
                        <ItemsPanelTemplate>
                            <UniformGrid Columns="3"/>
                        </ItemsPanelTemplate>
                    </ItemsControl.ItemsPanel>
                </ItemsControl>
            </StackPanel>
        </Grid>
    </Border>
</UserControl>
```

`OrderDeck.App/Views/Shell/ProductCard.xaml.cs` — **dosyanın tamamı**:

```csharp
using System.Windows.Controls;

namespace OrderDeck.App.Views.Shell;

/// <summary>
/// Kart salt okunur olduğu için kod-arkasında olay işleyicisi kalmadı
/// (beden uygulama ve fotoğraf seçme akışları kaldırıldı).
/// </summary>
public partial class ProductCard : UserControl
{
    public ProductCard() => InitializeComponent();
}
```

- [ ] **Step 7: Eski katmanı sil, kayıtları düzelt**

```bash
git rm OrderDeck.Core/Catalog/Product.cs \
       OrderDeck.Core/Storage/Repositories/ProductRepository.cs \
       OrderDeck.App/Services/ProductPhotoStore.cs \
       OrderDeck.App/ViewModels/ProductSizeViewModel.cs \
       OrderDeck.Tests/Storage/ProductRepositoryTests.cs \
       OrderDeck.Tests/App/ProductPhotoStoreTests.cs
```

`AppHost.cs` satır 83-86'daki dört satırı (iki yorum + iki kayıt) **sil**:

```csharp
        // Arayüz Faz 1: ürün kartı (ad/fotoğraf/beden adetleri) yalnız yerel SQLite.
        services.AddSingleton<ProductRepository>();
        // Ürün fotoğrafı deposu — kapsamı %LOCALAPPDATA%\OrderDeck\products.
        services.AddSingleton<ProductPhotoStore>();
```

Yerlerine Task 7'de eklenen replika kayıtları geçiyor (zaten eklendi):

```csharp
        // Ürün kartının kaynağı: sunucu kataloğunun yerel replikası (Stok Faz 1b).
        services.AddSingleton<CatalogReplicaRepository>();
        services.AddSingleton<CatalogPhotoCache>();
```

`OrderDeck.Tests/App/MainShellTestHarness.cs:105-109` →

```csharp
        var catalogRepo = new CatalogReplicaRepository(db);
        var productCard = new ProductCardViewModel(
            catalogRepo,
            new CatalogPhotoCache(Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))));
```

`OrderDeck.Tests/App/MainShellPrintTests.cs:166-170` → **aynı üç satır** (dosya
`clock.Object`'i başka yerlerde de kullandığı için `clock` değişkeni kalır;
yalnız bu çağrıdan düşer).

Her iki dosyada `using OrderDeck.App.Services;` (CatalogPhotoCache) ve
`using OrderDeck.Core.Storage.Repositories;` (CatalogReplicaRepository)
mevcut değilse ekle; artık kullanılmayan `ProductPhotoStore` using'i varsa
kaldır.

- [ ] **Step 8: Göçü yaz ve sürümü yükselt**

`OrderDeck.Core/Storage/Migrations/026_drop_local_products.sql`:

```sql
-- Stok Faz 1b: ürün tanımının sahibi artık SUNUCU KATALOĞU (025'teki
-- CatalogProduct/CatalogVariant replikası). Yerel Product/ProductSize
-- tabloları 024'te "sunucuda karşılığı yok, senkron yok" gerekçesiyle
-- açılmıştı; o gerekçe ortadan kalktı.
--
-- Veri taşınmıyor. Sebep: 2026-08-13'te sahadaki kurulumlarda bu tablolar
-- BOŞ olduğu doğrulandı — ürün kartı özelliği kullanılmamış. Taşıma kodu
-- yazmak, hiç var olmayan bir veri için kalıcı bakım yükü olurdu.
--
-- Sıra önemli: ProductSize'ın Product'a FK'si var, önce çocuk düşüyor.
DROP TABLE IF EXISTS ProductSize;
DROP TABLE IF EXISTS Product;

UPDATE _meta SET SchemaVersion = 26 WHERE Id = 1;
```

`OrderDeck.Tests/Storage/MigrationRunnerTests.cs` satır 32 ve 56: `Be(25)` →
`Be(26)`.

- [ ] **Step 9: Tam derleme + üç test projesi**

```bash
dotnet build OrderDeck.sln --configuration Debug --nologo
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```
Beklenen: 0 hata; silinen dört test dosyasının testleri düşmüş, kalan hepsi PASS.
`grep -rn "ProductRepository\|ProductPhotoStore\|ProductSizeViewModel" --include=*.cs .`
→ yalnız sunucu tarafındaki alakasız isimler (`PanelProductPhotoController` gibi)
kalmalı, WPF tarafında hiç eşleşme olmamalı.

- [ ] **Step 10: Commit**

```bash
git add OrderDeck.App/ViewModels/ProductCardViewModel.cs \
        OrderDeck.App/ViewModels/CatalogVariantViewModel.cs \
        OrderDeck.App/Views/Shell/ProductCard.xaml \
        OrderDeck.App/Views/Shell/ProductCard.xaml.cs \
        OrderDeck.App/AppHost.cs \
        OrderDeck.Core/Storage/Migrations/026_drop_local_products.sql \
        OrderDeck.Tests/App/ProductCardViewModelTests.cs \
        OrderDeck.Tests/App/MainShellTestHarness.cs \
        OrderDeck.Tests/App/MainShellPrintTests.cs \
        OrderDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(katalog): ürün kartı replikadan okusun, yerel ürün tanımı kalksın"
```

---

## Doğrulama

Görev adımlarındaki testlere ek olarak, dal bitince uçtan uca:

1. **Tam derleme:** `dotnet build OrderDeck.sln --configuration Release --nologo`
   → 0 hata, 0 **yeni** uyarı.
2. **Üç test projesi:**
   ```bash
   dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
   dotnet test OrderDeck.Licensing.Tests/OrderDeck.Licensing.Tests.csproj
   dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
   ```
3. **Kalıntı taraması:**
   ```bash
   grep -rn "ProductRepository\|ProductPhotoStore\|ProductSizeViewModel" --include=*.cs .
   grep -rn "ProductSize\|CREATE TABLE Product" --include=*.sql OrderDeck.Core
   ```
   İlkinde WPF tarafında eşleşme olmamalı; ikincisinde yalnız `024` (tarihsel)
   ve `026` (düşürme) çıkmalı.
4. **Göç zinciri temiz kurulumda:** yeni bir profil klasöründe uygulamayı aç →
   log'da göç hatası yok, `_meta.SchemaVersion = 26`.
5. **Göç zinciri MEVCUT kurulumda:** var olan `~/Documents/OrderDeck` veritabanıyla
   aç → 025 ve 026 sırayla koşuyor, uygulama açılıyor, ürün kartı "Ürün kodu gir"
   gösteriyor.
6. **Gerçek sunucuya karşı (kullanıcı):** panelden bir ürün + iki varyant + bir
   fotoğraf ekle → WPF'i aç, en fazla 5 dakika bekle (ya da yeniden başlat) →
   log'da `CatalogSyncHostedService starting` ve senkron satırı görünüyor;
   hero'daki kod kutusuna ürün kodunu **küçük harfle ve Türkçe karakterle** yaz
   (`güzel elbise`) → kart fotoğraf + ad + varyant rozetleriyle doluyor.
7. **Bilinmeyen kod:** katalogda olmayan bir kod yaz → kart "katalogda yok" +
   "Ürünü panelden ekleyin" gösteriyor, uygulama akışı kesilmiyor, log'da hata yok.
8. **Sunucu erişilemezken:** VPS'e giden ağı kes (ya da lisans anahtarını boşalt)
   → senkron turu sessizce başarısız oluyor, **replika olduğu gibi duruyor**,
   kart hâlâ eski veriyle çalışıyor.
9. **Fotoğraf çöp toplama:** panelden ürünü sil → bir sonraki turdan sonra
   `%LOCALAPPDATA%\OrderDeck\catalog-photos` altındaki dosya kaybolmuş olmalı.

## Kapsam dışı

- **Yorum eşleştirme ve varyant seçimi** — `AxisValueMatcher`, varyant seçici
  çekmece, `Label`'ın `ProductId`/`ProductVariantId` taşıması, `CatalogAware = true`
  gönderimi. **Plan 2.**
- **Stok bakiyelerinin WPF'te gösterimi** — `LicensesWpfStockPullController`
  tüketimi, kartta adet, düşük stok uyarısı. **Plan 3.**
- Kategori ağacının WPF'te **gösterimi**: bu planda kategoriler replikaya
  yazılıyor ama hiçbir ekran onları okumuyor. Sebep: ürün kartında kategori
  gösterilmiyor; veri, plan 2'nin eşleştirme kuralları için hazır bekliyor.
- Katalogda **arama/gezinme** ekranı (panelde var, WPF'te yok).
- Barkod okutma — Faz 1c.
- Fotoğrafın birden fazlası: replika yalnız **kapak** fotoğrafını tutuyor.

## Yayın

Tek dal, tek PR: `feat/stok-faz1b-wpf-katalog-replikasi`.

Sunucu değişikliği (Task 1-2) aynı PR'da çünkü WPF ayağı prod'a karşı ancak o
uçlar canlıyken sınanabiliyor; master'a merge otomatik deploy tetikliyor.

**Commit'siz duran dosyalar bu PR'a KARIŞTIRILMAYACAK** — `git add -A` / `git add .`
kullanma, yukarıdaki adımlarda yazıldığı gibi yol yol ekle. Dokunulmayacaklar:
`.claude/launch.json`, `.gitignore`, `.codex/`, `AGENTS.md`,
`docs/proje-analiz-raporu-2026-07-16.md`,
`docs/superpowers/plans/2026-07-28-whatsapp-odeme-hatirlatma-cloud-api.md`,
`docs/superpowers/specs/2026-07-28-whatsapp-otomasyon-design.md`.

Merge sonrası **deploy'un gerçekten olduğunu doğrula**: `license-server-deploy`
koşusunun yeşil olması yetmez (sıra-dışı koruması saniyeler içinde no-op olarak
başarıyla çıkabiliyor). Koşu süresine bak (gerçek deploy dakikalar sürer), sonra
VPS'te `LICENSE_SERVER_TAG`'in master HEAD'iyle eşleştiğini ve
`/api/v1/licenses/{id}/catalog/categories`'in 401 döndüğünü (rota kayıtlı)
kontrol et.
