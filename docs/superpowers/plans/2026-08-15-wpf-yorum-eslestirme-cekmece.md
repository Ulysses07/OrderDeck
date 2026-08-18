# WPF Yayın Kodu Çözümlemesi + Yorum Eşleştirme + Varyant Çekmecesi (Plan 3/3)

> **Ajan işçiler için:** GEREKLİ ALT-BECERİ: Bu planı görev görev uygulamak için
> `superpowers:subagent-driven-development` (önerilen) veya
> `superpowers:executing-plans` kullan. Adımlar takip için checkbox (`- [ ]`)
> sözdizimiyle yazıldı.

**Hedef:** Operatörün kod kutusuna yazdığı **yayın kodu** ile ürün + satıcı-ekseni
değeri çözülsün; izleyici yorumundan kalan kelimeler **izleyici ekseni** değerlerine
eşleşsin; eşleşme tek değilse **varyant seçici çekmecesi** açılsın ve yazılan her
sipariş satırı `ProductId` + `ProductVariantId` taşısın.

**Mimari:** Eşleştirme mantığı WPF'ten bağımsız iki saf sınıfa çıkıyor
(`BroadcastCodeResolver` katalog replikasından çözümleme yapar, `AxisValueMatcher`
metin → eksen değeri eşlemesi yapar); ikisi de `OrderDeck.Core`'da ve doğrudan
birim testi yazılabilir. WPF tarafı yalnız akışı bağlar: kod kutusu → ürün kartı,
sohbet çift-tık → eşleştirme → (gerekirse) çekmece → `LabelService.Add`. Katalog
replikası bu planda yayın kodlarını da taşıyacak biçimde büyüyor; `Label` tablosu
iki yeni nullable kolon alıyor ve sunucuya `CatalogAware: true` ile gidiyor.

**Teknoloji yığını:** .NET 10, WPF (`net10.0-windows`), SQLite + Dapper, xUnit +
FluentAssertions, CommunityToolkit.Mvvm.

**Kaynak tasarım:** `docs/superpowers/specs/2026-08-14-yayin-kodu-ve-yorum-eslestirme-design.md`
(11 kabul kriteri; bu planın görevleri o kriterlere referans verir).

---

## Araştırmada çıkan iki çatışma — plan bunlara göre şekillendi

### 1. Uyum kalkanı bu sürümde KALDIRILAMAZ

Tasarım "plan 3/3'te geçici uyum kalkanı kaldırılacak" diyor. Kod okuması bunun
**sahada kurulu eski WPF sürümlerini sessizce bozacağını** gösterdi:

- `OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs` → `CatalogVariantPullItem.VariantCode`
  **non-nullable `string`**. Sunucu `variantCode` göndermeyi bırakırsa
  System.Text.Json alanı `null` bırakır (record kurucusuna `null!` geçer, exception
  atmaz).
- `OrderDeck.Core/Storage/Migrations/025_catalog_replica.sql` → `CatalogVariant.VariantCode TEXT NOT NULL`.
- Sonuç: `CatalogReplicaRepository.Replace(...)` INSERT'te patlar → tüm transaction
  geri alınır → `CatalogSyncService`'in
  `catch (Exception ex) when (!ct.IsCancellationRequested)` bloğu bunu bir Warning'e
  yutar. Kullanıcı hiçbir hata görmez, **kataloğu güncellenmeyi durdurur.**

Velopack ile güncelleme kademeli geldiği için sunucu deploy'u ile istemci
güncellemesi aynı anda olmuyor. Bu yüzden **kalkanın kaldırılması ayrı bir PR'a
alındı (Görev 12)** ve "yeni WPF sürümü sahaya yayıldıktan sonra" koşuluna
bağlandı. Bu planın PR'ı kalkana dokunmaz.

*Ucuz alternatif ve neden reddedildi:* `CatalogVariantPullItem.VariantCode`'u
`string?` yapıp kalkanı hemen kaldırmak. Bu, **yeni** istemciyi kurtarır ama zaten
sahada olan **eski** istemcinin DTO'sunu değiştirmez — sorun tam olarak eski
istemcide. Yani ucuz yol hiçbir şey çözmüyor.

### 2. Çekmece "akışı keser" ama görsel olarak modal DEĞİL

Tasarım: *"Çekmece akışı keser; çekmece kapanmadan başka mesaja geçilemez."*
`OrderDeck.App/Views/Shell/DrawerHost.xaml` ise açıkça şunu belgeliyor:
*"çekmece açıkken kabuğun geri kalanı tıklanabilir kalıyor, yani çekmece MODAL
DEĞİL. Bilinçli — operatör çekmece açıkken sohbete bakmaya devam edebilmeli."*

**Karar:** Kesme kuralı **ViewModel'de** uygulanacak (`MainShellViewModel`'de
tekrar-giriş kilidi: çekmece açıkken gelen ikinci çift-tık yok sayılır), görsel
scrim eklenmeyecek. Böylece hem tasarımın kuralı (ikinci sipariş oluşmaz) hem
kabuğun bilinçli kararı (sohbet okunabilir kalır) korunur.

---

## Dosya yapısı

**Yeni dosyalar**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Core/Storage/Migrations/027_catalog_broadcast_codes.sql` | Replikaya `CatalogBroadcastCode` tablosu; `CatalogVariant`'tan ölü kolonların atılması |
| `OrderDeck.Core/Storage/Migrations/028_label_catalog_ids.sql` | `Label.ProductId` / `Label.ProductVariantId` |
| `OrderDeck.Core/Catalog/BroadcastCodeResolver.cs` | Yayın kodu → ürün + satıcı ekseni değeri + o değere ait aktif varyantlar |
| `OrderDeck.Core/Catalog/AxisValueMatcher.cs` | Yorum metni → izleyici ekseni değer(ler)i (saf, bağımlılıksız) |
| `OrderDeck.App/ViewModels/VariantPickerViewModel.cs` | Çekmece içeriği: seçilebilir değer listesi + onay durumu |
| `OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml(.cs)` | Çekmecenin görünümü |
| `OrderDeck.Tests/Catalog/BroadcastCodeResolverTests.cs` | Görev 5 testleri |
| `OrderDeck.Tests/Catalog/AxisValueMatcherTests.cs` | Görev 7 testleri (kabul kriteri 5-9) |
| `OrderDeck.Tests/ViewModels/VariantPickerFlowTests.cs` | Görev 9 akış testleri (sahte `IDrawerService`) |

**Değişen dosyalar**

| Dosya | Değişiklik |
|---|---|
| `OrderDeck.Core/Catalog/CatalogReplica.cs` | `CatalogBroadcastCode` record'u eklenir; `CatalogVariant`'tan `Axis1Code`/`Axis2Code`/`VariantCode` çıkar |
| `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs` | `Replace` kodları da yazar; `FindBroadcastCode`, `GetProductById` eklenir; `FindByCode` kalır (stok kodu araması başka yerde kullanılıyor) |
| `OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs` | `CatalogBroadcastCodePullItem` + `BroadcastCodes`; varyanttan ölü alanlar çıkar |
| `OrderDeck.App/Services/Sync/CatalogSyncService.cs` | Yeni alanların eşlemesi |
| `OrderDeck.App/ViewModels/CatalogVariantViewModel.cs` | `VariantCode` yerine açık fallback parametresi |
| `OrderDeck.App/ViewModels/ProductCardViewModel.cs` | Yayın kodu ile çözümleme + satıcı ekseni son eki |
| `OrderDeck.App/Views/Shell/ProductCard.xaml` | `Elbise · Siyah` gösterimi |
| `OrderDeck.Core/Sales/Label.cs` | `ProductId` / `ProductVariantId` |
| `OrderDeck.Core/Sales/LabelService.cs` | `Add(...)` iki opsiyonel parametre alır |
| `OrderDeck.Core/Storage/Repositories/LabelRepository.cs` | INSERT + 5 SELECT + `Row` + `Map` |
| `OrderDeck.App/ViewModels/MainShellViewModel.cs` | `AddChatToQueueAsync` akışı, çekmece çağrısı, tekrar-giriş kilidi |
| `OrderDeck.App/Views/Shell/ChatPanel.xaml.cs` | İki olay işleyici (çift-tık + Enter) async'e döner |
| `OrderDeck.App/Services/Sync/SessionOrderSyncService.cs` | `ProductId`/`ProductVariantId` + `CatalogAware: true` |
| `OrderDeck.Tests/Storage/MigrationRunnerTests.cs` | Satır 32 ve 66'daki sürüm beklentisi |

---

### Görev 1: Göç 027 — replikaya yayın kodu tablosu, varyanttan ölü kolonların atılması

**Dosyalar:**
- Oluştur: `OrderDeck.Core/Storage/Migrations/027_catalog_broadcast_codes.sql`
- Değiştir: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs:32` ve `:66`

Göç dosyaları `.csproj`'de wildcard ile gömülü kaynak olarak toplanıyor
(`025`/`026` için ek bir kayıt satırı yok) — yeni dosya için proje dosyasına
dokunmaya gerek yok. Doğrulaması Adım 3'te zaten yapılıyor: sürüm 27 okunmazsa
dosya gömülmemiş demektir.

- [ ] **Adım 1: Göç dosyasını yaz**

`OrderDeck.Core/Storage/Migrations/027_catalog_broadcast_codes.sql`:

```sql
-- Yayın kodu replikaya iniyor. Operatör kod kutusuna STOK kodunu değil YAYIN
-- kodunu yazar; yayın kodu ürün + SATICI EKSENİ DEĞERİ düzeyindedir, yani tek
-- ürünün birden çok kodu olabilir ("ATEŞ" = Elbise/Siyah, "BUZ" = Elbise/Beyaz).
--
-- Sunucudaki çekme ucu kodları ürünün içinde gömülü dizi olarak gönderiyor ve
-- dizide Id YOK (bkz. CatalogBroadcastCodeDto). Bu yüzden burada da yapay
-- birincil anahtar tanımlamıyoruz; satırlar her senkron turunda tamamen
-- siliniyor ve baştan yazılıyor, kimliğe ihtiyaç duyan tek şey yok.
CREATE TABLE CatalogBroadcastCode (
    ProductId       TEXT NOT NULL,  -- CatalogProduct.Id; FK bilerek yok (bkz. 025).
    -- Satıcı ekseninin değeri ("Siyah"). Ürünün satıcı ekseni yoksa NULL —
    -- o zaman kod ürünün tamamını gösterir.
    SellerAxisValue TEXT,
    -- Operatörün yazdığı hâl; kartta/loglarda bunu gösteriyoruz.
    Code            TEXT NOT NULL,
    -- Aramanın karşılaştırdığı biçim; sunucu SearchNormalizer.Normalize ile
    -- üretip gönderiyor, yerelde YENİDEN HESAPLANMAZ. Sebep: normalize kuralı
    -- ileride değişirse sunucu ile yerel sessizce ayrışmasın — tek doğru kaynak
    -- sunucu.
    CodeNormalized  TEXT NOT NULL,
    CreatedAt       INTEGER NOT NULL,  -- unix saniye
    -- Sunucunun gönderdiği dizideki konum. Sunucu en yeni kodu başa koyuyor
    -- (OrderByDescending CreatedAt, ThenByDescending Id); sıra taşınıyor,
    -- yerelde yeniden hesaplanmıyor (025'teki SortOrder kuralının aynısı).
    SortOrder       INTEGER NOT NULL
);

-- Kod kutusundaki arama: WHERE CodeNormalized = ? → indeks kullanılır.
-- UNIQUE DEĞİL, 025'teki gerekçeyle: replika kendisine verilen veriyi
-- reddetmemeli; benzersizliği sunucu (LicenseId, CodeNormalized) üstünde
-- zaten uyguluyor.
CREATE INDEX IX_CatalogBroadcastCode_CodeNormalized ON CatalogBroadcastCode(CodeNormalized);
CREATE INDEX IX_CatalogBroadcastCode_ProductId ON CatalogBroadcastCode(ProductId);

-- CatalogVariant'tan üç ölü kolon çıkıyor: Axis1Code, Axis2Code, VariantCode.
-- Varyant kodu kavramı sunucudan tamamen kaldırıldı (bkz. 920c40c); replika
-- bugün VariantCode'a ürünün stok kodunu yazan geçici bir uyum kalkanıyla
-- besleniyor. Kolonlar gidince o kalkanın da bir işi kalmıyor.
--
-- Neden DROP COLUMN değil, yeniden kurma: SQLite'ın ALTER TABLE DROP COLUMN'u
-- 3.35+ gerektiriyor ve indeks/ifade bağımlılığı olan kolonlarda hata veriyor.
-- CREATE-yeni / INSERT-SELECT / DROP / RENAME kalıbı sürümden bağımsız çalışır
-- ve ÖNBELLEĞİ KORUR: kullanıcı bu göçten sonra bir sonraki senkrona kadar
-- kataloğu görmeye devam eder.
CREATE TABLE CatalogVariant_new (
    Id          TEXT PRIMARY KEY,
    ProductId   TEXT NOT NULL,
    Axis1Value  TEXT,
    Axis2Value  TEXT,
    Barcode     TEXT,
    IsActive    INTEGER NOT NULL,
    SortOrder   INTEGER NOT NULL
);

INSERT INTO CatalogVariant_new (Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder)
SELECT Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder FROM CatalogVariant;

DROP TABLE CatalogVariant;
ALTER TABLE CatalogVariant_new RENAME TO CatalogVariant;

-- İndeksi yeniden kurmak ZORUNLU: DROP TABLE indeksi de düşürdü.
CREATE INDEX IX_CatalogVariant_ProductId ON CatalogVariant(ProductId);

UPDATE _meta SET SchemaVersion = 27 WHERE Id = 1;
```

- [ ] **Adım 2: Test beklentisini güncelle**

`OrderDeck.Tests/Storage/MigrationRunnerTests.cs` içinde **iki** yerde
(satır 32 ve satır 66) aynı satır var:

```csharp
version.Should().Be(26);
```

İkisini de şuna çevir:

```csharp
version.Should().Be(27);
```

- [ ] **Adım 3: Testi çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests`
Beklenen: PASS. (Bu aşamada `CatalogReplicaRepository` hâlâ eski kolonlara yazdığı
için diğer katalog testleri KIRIK — normal, Görev 2'de düzeliyor. Bu yüzden bu
adımda `--filter` ile yalnız göç testleri çalıştırılıyor.)

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.Core/Storage/Migrations/027_catalog_broadcast_codes.sql OrderDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(katalog): replikaya yayın kodu tablosu, varyanttan ölü kolonlar çıktı"
```

---

### Görev 2: Replika modeli ve deposu

**Dosyalar:**
- Değiştir: `OrderDeck.Core/Catalog/CatalogReplica.cs`
- Değiştir: `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs`
- Test: `OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs` (var olan dosya; yoksa oluştur)

- [ ] **Adım 1: Önce testi yaz**

`OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs` dosyasının sonuna
(sınıfın içine) ekle:

```csharp
    [Fact]
    public void FindBroadcastCode_normalize_edilmis_iğneyle_bulur()
    {
        var repo = NewRepo();   // dosyadaki var olan yardımcı; yoksa:
                                // new CatalogReplicaRepository(new InMemorySqlite())
        var product = new CatalogProduct(
            "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
            "Renk", 1, "Beden", 2, null, 0);

        repo.Replace(
            new[] { product },
            Array.Empty<CatalogVariant>(),
            Array.Empty<CatalogCategory>(),
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        // Operatör küçük harf + Türkçe karakterle yazıyor; iğne aynı
        // normalizasyondan geçtiği için bulunmalı.
        var hit = repo.FindBroadcastCode("ateş");

        hit.Should().NotBeNull();
        hit!.ProductId.Should().Be("p1");
        hit.SellerAxisValue.Should().Be("Siyah");
        hit.Code.Should().Be("Ateş");
    }

    [Fact]
    public void Replace_eski_yayin_kodlarini_siler()
    {
        var repo = NewRepo();
        var product = new CatalogProduct(
            "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
            null, null, null, null, null, 0);

        repo.Replace(new[] { product }, Array.Empty<CatalogVariant>(), Array.Empty<CatalogCategory>(),
            new[] { new CatalogBroadcastCode("p1", null, "Ateş", "ATES", 0, 0) });
        repo.Replace(new[] { product }, Array.Empty<CatalogVariant>(), Array.Empty<CatalogCategory>(),
            new[] { new CatalogBroadcastCode("p1", null, "Buz", "BUZ", 0, 0) });

        // Sunucuda silinen kod yerelde hayalet kalırsa yayında YANLIŞ ÜRÜNE
        // eşleşir — bu testin tek amacı o hayaleti yakalamak.
        repo.FindBroadcastCode("ateş").Should().BeNull();
        repo.FindBroadcastCode("buz").Should().NotBeNull();
    }

    [Fact]
    public void GetProductById_bulamayinca_null_doner()
    {
        var repo = NewRepo();
        repo.GetProductById("yok").Should().BeNull();
    }
```

Dosyanın başında `using OrderDeck.Core.Catalog;` ve `using FluentAssertions;`
olduğundan emin ol.

- [ ] **Adım 2: Testi çalıştır, DERLENMEDİĞİNİ gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogReplicaRepositoryTests`
Beklenen: derleme hatası — `CatalogBroadcastCode` yok, `Replace` 4 argüman almıyor,
`FindBroadcastCode`/`GetProductById` yok.

- [ ] **Adım 3: Modeli güncelle**

`OrderDeck.Core/Catalog/CatalogReplica.cs` içinde `CatalogVariant` record'unu
şununla değiştir (üç alan çıkıyor):

```csharp
/// <summary>Bir ürünün tek varyantı. Eksensiz üründe de tam bir varyant vardır.</summary>
public sealed record CatalogVariant(
    string Id,
    string ProductId,
    string? Axis1Value,
    string? Axis2Value,
    string? Barcode,
    bool IsActive,
    int SortOrder);
```

Ve dosyanın sonuna ekle:

```csharp
/// <summary>
/// Operatörün yayında kullandığı kod. Stok kodundan (<c>SK00001</c>) FARKLI:
/// stok kodunu sunucu üretir ve değişmez; yayın kodunu operatör panelden verir,
/// ürün + <b>satıcı ekseni değeri</b> düzeyindedir ve kalıcı olarak rezervedir.
/// </summary>
/// <param name="SellerAxisValue">
/// Kodun işaret ettiği satıcı ekseni değeri ("Siyah"). Ürünün satıcı ekseni
/// yoksa null — kod o zaman ürünün tamamını gösterir.
/// </param>
/// <param name="CodeNormalized">
/// Sunucunun ürettiği normalize biçim. <b>Yerelde yeniden hesaplanmaz</b>;
/// arama iğnesi <c>SearchNormalizer.Normalize</c> ile üretilip bununla
/// karşılaştırılır.
/// </param>
/// <param name="SortOrder">Sunucunun gönderdiği dizideki konum (en yeni önce).</param>
public sealed record CatalogBroadcastCode(
    string ProductId,
    string? SellerAxisValue,
    string Code,
    string CodeNormalized,
    long CreatedAt,
    int SortOrder);
```

- [ ] **Adım 4: Depoyu güncelle**

`OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs`:

`Replace` imzasına dördüncü parametreyi ekle ve gövdesini güncelle:

```csharp
    public void Replace(
        IReadOnlyList<CatalogProduct> products,
        IReadOnlyList<CatalogVariant> variants,
        IReadOnlyList<CatalogCategory> categories,
        IReadOnlyList<CatalogBroadcastCode> broadcastCodes)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        conn.Execute("DELETE FROM CatalogBroadcastCode", transaction: tx);
        conn.Execute("DELETE FROM CatalogVariant", transaction: tx);
        conn.Execute("DELETE FROM CatalogProduct", transaction: tx);
        conn.Execute("DELETE FROM CatalogCategory", transaction: tx);
```

Ürün INSERT'i aynen kalır. Varyant INSERT'ini üç kolon eksiğiyle yeniden yaz:

```csharp
        if (variants.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogVariant
                    (Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder)
                VALUES
                    (@Id, @ProductId, @Axis1Value, @Axis2Value, @Barcode, @IsActive, @SortOrder)
                """,
                variants.Select(v => new
                {
                    v.Id, v.ProductId, v.Axis1Value, v.Axis2Value, v.Barcode,
                    IsActive = v.IsActive ? 1 : 0,
                    v.SortOrder
                }).ToList(), tx);
```

Kategori INSERT'inden **sonra**, `tx.Commit()`'ten **önce** ekle:

```csharp
        if (broadcastCodes.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogBroadcastCode
                    (ProductId, SellerAxisValue, Code, CodeNormalized, CreatedAt, SortOrder)
                VALUES
                    (@ProductId, @SellerAxisValue, @Code, @CodeNormalized, @CreatedAt, @SortOrder)
                """,
                broadcastCodes, tx);
```

`GetVariants` sorgusunu ve eşlemesini daralt:

```csharp
    public IReadOnlyList<CatalogVariant> GetVariants(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<VariantRow>(
            """
            SELECT Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder
            FROM CatalogVariant
            WHERE ProductId = @productId
            ORDER BY SortOrder
            """,
            new { productId })
            .Select(r => new CatalogVariant(
                r.Id, r.ProductId, r.Axis1Value, r.Axis2Value,
                r.Barcode, r.IsActive == 1, r.SortOrder))
            .ToList();
    }
```

`VariantRow` sınıfından `Axis1Code`, `Axis2Code`, `VariantCode` özelliklerini sil.

`FindByCode`'un hemen ardına iki yeni metot ekle:

```csharp
    /// <summary>
    /// Operatörün kod kutusuna yazdığı <b>yayın kodunu</b> bulur. İğne saklanan
    /// kolonla aynı normalizasyondan geçiyor: büyük/küçük harf ve Türkçe harf
    /// farkı önemsiz, ardışık boşluklar sadeleşir.
    ///
    /// <c>LIMIT 1</c> savunma amaçlı: indeks unique değil (bkz. göç 027).
    /// Beklenmedik bir çakışmada patlamak yerine sunucunun sırasındaki ilk
    /// (= en yeni) kodu verir; <c>ProductId</c> ikincil anahtarı sırayı
    /// deterministik yapar.
    /// </summary>
    public CatalogBroadcastCode? FindBroadcastCode(string? code)
    {
        var needle = SearchNormalizer.Normalize(code);
        if (needle.Length == 0) return null;

        using var conn = _factory.Open();
        return conn.Query<BroadcastCodeRow>(
            """
            SELECT ProductId, SellerAxisValue, Code, CodeNormalized, CreatedAt, SortOrder
            FROM CatalogBroadcastCode
            WHERE CodeNormalized = @needle
            ORDER BY SortOrder, ProductId LIMIT 1
            """,
            new { needle })
            .Select(r => new CatalogBroadcastCode(
                r.ProductId, r.SellerAxisValue, r.Code, r.CodeNormalized,
                r.CreatedAt, r.SortOrder))
            .FirstOrDefault();
    }

    /// <summary>Yayın kodundan gelen kimlikle ürünü çeker.</summary>
    public CatalogProduct? GetProductById(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<ProductRow>(
            $"SELECT {ProductColumns} FROM CatalogProduct WHERE Id = @productId LIMIT 1",
            new { productId })
            .Select(Map).FirstOrDefault();
    }
```

Ve `CategoryRow`'un yanına satır sınıfını ekle:

```csharp
    private sealed class BroadcastCodeRow
    {
        public string ProductId { get; init; } = "";
        public string? SellerAxisValue { get; init; }
        public string Code { get; init; } = "";
        public string CodeNormalized { get; init; } = "";
        public long CreatedAt { get; init; }
        public int SortOrder { get; init; }
    }
```

- [ ] **Adım 5: Testi çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogReplicaRepositoryTests`
Beklenen: PASS. Diğer testler hâlâ derlenmeyebilir (`CatalogSyncService`,
`CatalogVariantViewModel`) — Görev 3 ve 4'te düzeliyor.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.Core/Catalog/CatalogReplica.cs OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs
git commit -m "feat(katalog): replika deposu yayın kodlarını taşıyor"
```

---

### Görev 3: Tel modeli ve senkron eşlemesi

**Dosyalar:**
- Değiştir: `OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs`
- Değiştir: `OrderDeck.App/Services/Sync/CatalogSyncService.cs`

- [ ] **Adım 1: DTO'ları güncelle**

`OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs`:

`CatalogProductPullItem`'ın son parametresinden sonra bir tane daha ekle ve XML
doc'a not düş:

```csharp
/// <param name="BroadcastCodes">
/// Ürüne verilmiş yayın kodları; sunucu <b>en yeni önce</b> sıralayıp gönderiyor.
/// Dizideki konum sıralamanın kendisidir (bkz. <c>027_catalog_broadcast_codes.sql</c>).
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
    List<CatalogVariantPullItem> Variants,
    List<CatalogBroadcastCodePullItem> BroadcastCodes);
```

`CatalogVariantPullItem`'dan üç ölü alanı çıkar:

```csharp
/// <summary>Bir ürünün tek varyantı; sırası taşıyıcı dizinin konumundan gelir.</summary>
public sealed record CatalogVariantPullItem(
    Guid Id,
    string? Axis1Value,
    string? Axis2Value,
    string? Barcode,
    bool IsActive);
```

Ve yeni record'u ekle:

```csharp
/// <summary>
/// Ürüne verilmiş bir yayın kodu. <c>SellerAxisValue</c> null ise kod ürünün
/// tamamını gösterir (ürünün satıcı ekseni yok).
/// </summary>
public sealed record CatalogBroadcastCodePullItem(
    string? SellerAxisValue,
    string Code,
    string CodeNormalized,
    DateTimeOffset CreatedAt);
```

> **Not:** Sunucu tarafındaki `CatalogBroadcastCodeDto` bu alanları zaten aynı
> sırayla gönderiyor (bkz. `LicensesWpfCatalogPullController`), dolayısıyla
> sunucuda değişiklik gerekmez. `variantCode` alanı JSON'da fazladan gelmeye
> devam edecek ve yeni DTO'da karşılığı olmadığı için sessizce yok sayılacak —
> `JsonSerializerOptions`'ta `UnmappedMemberHandling.Disallow` yok, doğrulandı.

- [ ] **Adım 2: Senkron eşlemesini güncelle**

`OrderDeck.App/Services/Sync/CatalogSyncService.cs`:

`ToVariants` metodunu daralt:

```csharp
    private static IEnumerable<CatalogVariant> ToVariants(CatalogProductPullItem p) =>
        p.Variants.Select((v, i) => new CatalogVariant(
            v.Id.ToString("N"),
            p.Id.ToString("N"),
            v.Axis1Value,
            v.Axis2Value,
            v.Barcode,
            v.IsActive,
            i));
```

Yanına yeni eşleyiciyi ekle:

```csharp
    // Dizideki konum SortOrder oluyor — varyantlarla aynı kural (bkz. 027).
    private static IEnumerable<CatalogBroadcastCode> ToBroadcastCodes(CatalogProductPullItem p) =>
        p.BroadcastCodes.Select((c, i) => new CatalogBroadcastCode(
            p.Id.ToString("N"),
            c.SellerAxisValue,
            c.Code,
            c.CodeNormalized,
            c.CreatedAt.ToUnixTimeSeconds(),
            i));
```

Sayfaları toplayan döngüde varyant listesinin yanına kod listesini de biriktir
(döngüden önce `var codes = new List<CatalogBroadcastCode>();`, döngü içinde
varyant `AddRange` satırının hemen altına `codes.AddRange(ToBroadcastCodes(item));`),
ve `Replace` çağrısını dördüncü argümanla tamamla:

```csharp
        _repo.Replace(products, variants, categories, codes);
```

- [ ] **Adım 3: Derle**

Çalıştır: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Beklenen: `CatalogVariantViewModel`'in `VariantCode`'a eriştiği yerde hata —
Görev 4'te düzeliyor. Başka hata olmamalı.

- [ ] **Adım 4: Commit**

```bash
git add OrderDeck.Licensing/Api/Models/CatalogPullDtos.cs OrderDeck.App/Services/Sync/CatalogSyncService.cs
git commit -m "feat(katalog): yayın kodları tel modelinden replikaya akıyor"
```

---

### Görev 4: Varyant görünüm modelinin fallback'i açık parametre olsun

**Dosyalar:**
- Değiştir: `OrderDeck.App/ViewModels/CatalogVariantViewModel.cs`
- Test: `OrderDeck.Tests/ViewModels/CatalogVariantViewModelTests.cs` (yoksa oluştur)

`VariantCode` artık yok. Eksensiz üründe gösterilecek bir etiket gerektiği için
fallback metni **çağıran** verir (panelin `variantLabel(v, product.code)`
davranışının aynısı: ürünün stok kodu).

- [ ] **Adım 1: Testi yaz**

`OrderDeck.Tests/ViewModels/CatalogVariantViewModelTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class CatalogVariantViewModelTests
{
    private static CatalogVariant Variant(string? a1, string? a2) =>
        new("v1", "p1", a1, a2, null, true, 0);

    [Fact]
    public void Iki_eksen_nokta_ile_birlesir()
    {
        new CatalogVariantViewModel(Variant("Siyah", "M"), "SK00001")
            .Display.Should().Be("Siyah · M");
    }

    [Fact]
    public void Tek_eksen_yalniz_kendini_gosterir()
    {
        new CatalogVariantViewModel(Variant("Siyah", null), "SK00001")
            .Display.Should().Be("Siyah");
    }

    [Fact]
    public void Eksensiz_varyant_fallback_metnini_gosterir()
    {
        // Eksensiz üründe varyantın gösterecek değeri yok; boş çip yerine
        // ürünün stok kodu görünür.
        new CatalogVariantViewModel(Variant(null, null), "SK00001")
            .Display.Should().Be("SK00001");
    }
}
```

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogVariantViewModelTests`
Beklenen: derleme hatası (kurucu iki argüman almıyor).

- [ ] **Adım 3: Görünüm modelini güncelle**

`OrderDeck.App/ViewModels/CatalogVariantViewModel.cs` içinde kurucuyu şu hâle
getir (`VariantCode` özelliği tamamen silinir; sınıfın kalan üyeleri aynen kalır):

```csharp
    /// <param name="fallbackLabel">
    /// Varyantın gösterilecek hiçbir eksen değeri yoksa kullanılacak metin —
    /// çağıran ürünün stok kodunu verir. Fallback'i buraya gömmek yerine
    /// parametre yapmak bilinçli: görünüm modelinin ürüne erişimi yok.
    /// </param>
    public CatalogVariantViewModel(CatalogVariant variant, string fallbackLabel)
    {
        Id = variant.Id;
        IsActive = variant.IsActive;

        var parts = new[] { variant.Axis1Value, variant.Axis2Value }
            .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim());
        var label = string.Join(" · ", parts);
        Display = label.Length > 0 ? label : fallbackLabel;
    }
```

(Sınıfta `VariantCode` özelliğine yapılan başka atıf kalmadığından emin ol:
`grep -rn "VariantCode" OrderDeck.App OrderDeck.Core OrderDeck.Licensing` →
yalnız sunucu tarafı eşleşmeler kalmalı.)

- [ ] **Adım 4: Çağıranı düzelt**

`OrderDeck.App/ViewModels/ProductCardViewModel.cs` içinde
`new CatalogVariantViewModel(v)` çağrısına ürünün kodunu ikinci argüman olarak
geçir: `new CatalogVariantViewModel(v, product.Code)`.

- [ ] **Adım 5: Testi çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogVariantViewModelTests`
Beklenen: 3 test PASS.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.App/ViewModels/CatalogVariantViewModel.cs OrderDeck.App/ViewModels/ProductCardViewModel.cs OrderDeck.Tests/ViewModels/CatalogVariantViewModelTests.cs
git commit -m "refactor(katalog): varyant etiketi fallback'i çağırandan geliyor"
```

---

### Görev 5: `BroadcastCodeResolver` — yayın kodu → ürün + satıcı ekseni + aday varyantlar

**Dosyalar:**
- Oluştur: `OrderDeck.Core/Catalog/BroadcastCodeResolver.cs`
- Test: `OrderDeck.Tests/Catalog/BroadcastCodeResolverTests.cs`

Bu sınıf **tek doğru kaynak**: hem ürün kartı hem sipariş akışı aynı çözümlemeyi
kullanır, böylece kartta görünen varyantlarla çekmecede seçilebilenler asla
ayrışmaz.

- [ ] **Adım 1: Testi yaz**

`OrderDeck.Tests/Catalog/BroadcastCodeResolverTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.Storage;   // InMemorySqlite yardımcısının namespace'i
using Xunit;

namespace OrderDeck.Tests.Catalog;

public class BroadcastCodeResolverTests
{
    // Renk = satıcı ekseni (1), Beden = izleyici ekseni (2).
    private static CatalogProduct Elbise() => new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private static CatalogVariant V(string id, string renk, string beden, bool active = true) =>
        new(id, "p1", renk, beden, null, active, 0);

    private static (BroadcastCodeResolver Resolver, CatalogReplicaRepository Repo) Build(
        CatalogProduct product,
        IReadOnlyList<CatalogVariant> variants,
        IReadOnlyList<CatalogBroadcastCode> codes)
    {
        var repo = new CatalogReplicaRepository(new InMemorySqlite());
        repo.Replace(new[] { product }, variants, Array.Empty<CatalogCategory>(), codes);
        return (new BroadcastCodeResolver(repo), repo);
    }

    [Fact]
    public void Kod_urunu_ve_satici_ekseni_degerini_cozer()
    {
        var (resolver, _) = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "M"), V("v2", "Beyaz", "M") },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var r = resolver.Resolve("ateş");

        r.Should().NotBeNull();
        r!.Product.Name.Should().Be("Elbise");
        r.SellerAxisValue.Should().Be("Siyah");
        r.ViewerAxisName.Should().Be("Beden");
    }

    [Fact]
    public void Varyantlar_satici_ekseni_degerine_gore_suzulur()
    {
        var (resolver, _) = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "S"), V("v2", "Siyah", "M"), V("v3", "Beyaz", "S") },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var r = resolver.Resolve("ateş")!;

        // "ATEŞ" siyah demek; beyaz varyant bu kodun altında hiç görünmemeli,
        // yoksa operatör kartta olmayan bir bedeni seçebilir.
        r.Variants.Select(v => v.Id).Should().Equal("v1", "v2");
        r.ViewerAxisValues.Should().Equal("S", "M");
    }

    [Fact]
    public void Pasif_varyant_aday_degildir()
    {
        var (resolver, _) = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "S"), V("v2", "Siyah", "M", active: false) },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        resolver.Resolve("ateş")!.ViewerAxisValues.Should().Equal("S");
    }

    [Fact]
    public void Eksensiz_urunde_varyant_kimligi_null()
    {
        var kolye = new CatalogProduct(
            "p1", null, "SK00002", "SK00002", "Kolye", 50m, null,
            null, null, null, null, null, 0);

        var (resolver, _) = Build(
            kolye,
            new[] { new CatalogVariant("v1", "p1", null, null, null, true, 0) },
            new[] { new CatalogBroadcastCode("p1", null, "Buz", "BUZ", 0, 0) });

        var r = resolver.Resolve("buz")!;

        r.HasViewerAxis.Should().BeFalse();
        // Kabul kriteri 11: eksensiz üründe stok ÜRÜNDEN düşer, varyanttan değil.
        r.ResolveVariantId(null).Should().BeNull();
    }

    [Fact]
    public void Yalniz_satici_ekseni_varsa_varyant_kimligi_belirlenir()
    {
        var product = new CatalogProduct(
            "p1", null, "SK00003", "SK00003", "Çanta", 200m, null,
            "Renk", 1, null, null, null, 0);

        var (resolver, _) = Build(
            product,
            new[] { new CatalogVariant("v1", "p1", "Siyah", null, null, true, 0),
                    new CatalogVariant("v2", "p1", "Beyaz", null, null, true, 1) },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var r = resolver.Resolve("ateş")!;

        r.HasViewerAxis.Should().BeFalse();
        r.ResolveVariantId(null).Should().Be("v1");
    }

    [Fact]
    public void Izleyici_ekseni_degeri_varyanta_cevrilir()
    {
        var (resolver, _) = Build(
            Elbise(),
            new[] { V("v1", "Siyah", "S"), V("v2", "Siyah", "M") },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        var r = resolver.Resolve("ateş")!;

        r.ResolveVariantId("m").Should().Be("v2");
        r.ResolveVariantId("XXL").Should().BeNull();
    }

    [Fact]
    public void Bilinmeyen_kod_null_doner()
    {
        var (resolver, _) = Build(Elbise(), Array.Empty<CatalogVariant>(),
            Array.Empty<CatalogBroadcastCode>());

        resolver.Resolve("yok").Should().BeNull();
        resolver.Resolve(null).Should().BeNull();
    }
}
```

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BroadcastCodeResolverTests`
Beklenen: derleme hatası — `BroadcastCodeResolver` yok.

- [ ] **Adım 3: Sınıfı yaz**

`OrderDeck.Core/Catalog/BroadcastCodeResolver.cs`:

```csharp
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;

namespace OrderDeck.Core.Catalog;

/// <summary>
/// Yayın kodunu çözer. Operatörün kod kutusuna yazdığı kod ürünü <b>ve</b> satıcı
/// ekseninin değerini birlikte belirler; geriye kalan tek serbestlik izleyici
/// ekseni olur — yorumdan çıkarılacak şey tam olarak odur.
///
/// Hem ürün kartı hem sipariş akışı bu sınıfı kullanır: kartta görünen varyant
/// listesiyle çekmecede seçilebilen değerlerin ayrışması mümkün olmamalı.
/// </summary>
public sealed class BroadcastCodeResolver
{
    // Sunucudaki AxisRole enum'ının sayısal karşılıkları (Product.cs).
    // Replika bunları ham int olarak taşıyor; paylaşılan bir enum yok.
    private const int SellerRole = 1;
    private const int ViewerRole = 2;

    private readonly CatalogReplicaRepository _repo;

    public BroadcastCodeResolver(CatalogReplicaRepository repo) => _repo = repo;

    public BroadcastCodeResolution? Resolve(string? code)
    {
        var hit = _repo.FindBroadcastCode(code);
        if (hit is null) return null;

        var product = _repo.GetProductById(hit.ProductId);
        // Kod var ama ürün yok: replika tutarsız (olmaması gereken durum, ama
        // replika UNIQUE/FK kurmadığı için imkânsız değil). Sessizce
        // "bilinmeyen kod" davranışına düşüyoruz — çökmek yerine.
        if (product is null) return null;

        var sellerAxis = AxisIndexOf(product, SellerRole);
        var viewerAxis = AxisIndexOf(product, ViewerRole);

        var variants = _repo.GetVariants(product.Id)
            .Where(v => v.IsActive)
            .Where(v => sellerAxis == 0
                     || Same(AxisValue(v, sellerAxis), hit.SellerAxisValue))
            .ToList();

        var viewerValues = viewerAxis == 0
            ? Array.Empty<string>()
            : variants.Select(v => AxisValue(v, viewerAxis))
                      .Where(v => !string.IsNullOrWhiteSpace(v))
                      .Select(v => v!.Trim())
                      // Sıra varyant sırasından gelir (SortOrder), alfabetik
                      // değil: sunucudaki sıralama tek doğru kaynak.
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();

        return new BroadcastCodeResolution(
            product,
            hit.Code,
            hit.SellerAxisValue,
            viewerAxis == 0 ? null : AxisName(product, viewerAxis),
            viewerAxis,
            variants,
            viewerValues);
    }

    private static int AxisIndexOf(CatalogProduct p, int role) =>
        p.Axis1Role == role ? 1 : p.Axis2Role == role ? 2 : 0;

    internal static string? AxisValue(CatalogVariant v, int axis) =>
        axis == 1 ? v.Axis1Value : axis == 2 ? v.Axis2Value : null;

    private static string? AxisName(CatalogProduct p, int axis) =>
        axis == 1 ? p.Axis1Name : axis == 2 ? p.Axis2Name : null;

    internal static bool Same(string? a, string? b) =>
        SearchNormalizer.Normalize(a) == SearchNormalizer.Normalize(b);
}

/// <summary>Çözülmüş yayın kodu; kart ve sipariş akışı bunu paylaşır.</summary>
/// <param name="Code">Operatörün yazdığı kodun kanonik hâli ("Ateş").</param>
/// <param name="ViewerAxisIndex">1, 2 veya 0 (izleyici ekseni yok).</param>
/// <param name="Variants">Satıcı ekseni değerine göre süzülmüş aktif varyantlar.</param>
/// <param name="ViewerAxisValues">Varyant sırasında, tekilleştirilmiş izleyici değerleri.</param>
public sealed record BroadcastCodeResolution(
    CatalogProduct Product,
    string Code,
    string? SellerAxisValue,
    string? ViewerAxisName,
    int ViewerAxisIndex,
    IReadOnlyList<CatalogVariant> Variants,
    IReadOnlyList<string> ViewerAxisValues)
{
    public bool HasViewerAxis => ViewerAxisIndex != 0;

    private bool HasAnyAxis => Product.Axis1Name is not null || Product.Axis2Name is not null;

    /// <summary>
    /// Sipariş satırına yazılacak varyant kimliği.
    /// <list type="bullet">
    /// <item>Ürünün hiç ekseni yoksa <b>null</b> — stok ürün düzeyinden düşer
    /// (kabul kriteri 11). Replikada tek bir varyant satırı olsa bile bilerek
    /// null: panel de o ürünü "Ürün geneli" kovasında gösteriyor.</item>
    /// <item>Yalnız satıcı ekseni varsa süzme zaten tek varyant bırakır.</item>
    /// <item>İzleyici ekseni varsa değer varyanta çevrilir.</item>
    /// </list>
    /// </summary>
    public string? ResolveVariantId(string? viewerAxisValue)
    {
        if (!HasAnyAxis) return null;

        if (!HasViewerAxis)
            return Variants.Count == 1 ? Variants[0].Id : null;

        if (string.IsNullOrWhiteSpace(viewerAxisValue)) return null;

        return Variants.FirstOrDefault(v =>
            BroadcastCodeResolver.Same(
                BroadcastCodeResolver.AxisValue(v, ViewerAxisIndex), viewerAxisValue))?.Id;
    }
}
```

- [ ] **Adım 4: Testi çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BroadcastCodeResolverTests`
Beklenen: 7 test PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.Core/Catalog/BroadcastCodeResolver.cs OrderDeck.Tests/Catalog/BroadcastCodeResolverTests.cs
git commit -m "feat(katalog): yayın kodu çözümleyicisi"
```

---

### Görev 6: Ürün kartı yayın koduyla çözümlesin

**Dosyalar:**
- Değiştir: `OrderDeck.App/ViewModels/ProductCardViewModel.cs`
- Değiştir: `OrderDeck.App/Views/Shell/ProductCard.xaml`
- Test: `OrderDeck.Tests/ViewModels/ProductCardViewModelTests.cs` (var olan dosya)

Tasarımın kuralı: **stok kodu kod kutusunda aranmaz.** Bugün
`ProductCardViewModel.Load` `_repo.FindByCode(...)` çağırıyor, yani stok kodunu
arıyor. Bu görev onu `BroadcastCodeResolver`'a çeviriyor ve karta
`Elbise · Siyah` gösterimini ekliyor (kabul kriteri 4).

- [ ] **Adım 1: Testi yaz**

`OrderDeck.Tests/ViewModels/ProductCardViewModelTests.cs` içindeki var olan
kurulum yardımcılarını yayın kodu yazacak biçimde güncelle (artık `Replace`
dördüncü argüman istiyor) ve şu testleri ekle:

```csharp
    [Fact]
    public void Yayin_kodu_urunu_ve_satici_degerini_gosterir()
    {
        var vm = NewCard(
            product: Elbise(),
            variants: new[] { V("v1", "Siyah", "M"), V("v2", "Beyaz", "M") },
            codes: new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        vm.Load("ateş");

        vm.HasProduct.Should().BeTrue();
        vm.Name.Should().Be("Elbise");
        // Kabul kriteri 4: kart "Elbise · Siyah" der.
        vm.SellerAxisSuffix.Should().Be(" · Siyah");
        vm.Variants.Select(v => v.Display).Should().Equal("Siyah · M");
    }

    [Fact]
    public void Stok_kodu_kod_kutusunda_ARANMAZ()
    {
        var vm = NewCard(
            product: Elbise(),
            variants: new[] { V("v1", "Siyah", "M") },
            codes: new[] { new CatalogBroadcastCode("p1", "Siyah", "Ateş", "ATES", 0, 0) });

        // SK00001 ürünün stok kodu; kod kutusu YAYIN kodu kutusudur.
        vm.Load("SK00001");

        vm.IsUnknown.Should().BeTrue();
        vm.HasProduct.Should().BeFalse();
    }

    [Fact]
    public void Satici_ekseni_yoksa_son_ek_bostur()
    {
        var vm = NewCard(
            product: Kolye(),
            variants: new[] { new CatalogVariant("v1", "p1", null, null, null, true, 0) },
            codes: new[] { new CatalogBroadcastCode("p1", null, "Buz", "BUZ", 0, 0) });

        vm.Load("buz");

        vm.SellerAxisSuffix.Should().BeEmpty();
    }
```

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCardViewModelTests`
Beklenen: derleme hatası (`SellerAxisSuffix` yok) veya `Stok_kodu...` testi FAIL.

- [ ] **Adım 3: Görünüm modelini güncelle**

`OrderDeck.App/ViewModels/ProductCardViewModel.cs`:

Alan ve kurucuyu `BroadcastCodeResolver` alacak şekilde değiştir (depoya doğrudan
erişim kaldırılır; fotoğraf önbelleği aboneliği aynen kalır):

```csharp
    private readonly BroadcastCodeResolver _resolver;
```

`Load` gövdesini şu hâle getir:

```csharp
    /// <summary>
    /// Kod kutusundaki metni <b>yayın kodu</b> olarak çözer. Stok kodu burada
    /// bilerek aranmaz: stok kodu depo/raf dilidir, yayın kodu yayın dilidir;
    /// ikisini aynı kutuda kabul etmek "SK00001" yazan operatöre yanlış ürünü
    /// açabilirdi.
    /// </summary>
    public void Load(string? code)
    {
        var trimmed = code?.Trim() ?? "";
        Code = trimmed;

        var resolution = trimmed.Length == 0 ? null : _resolver.Resolve(trimmed);
        _resolution = resolution;

        if (resolution is null)
        {
            Name = "";
            SellerAxisSuffix = "";
            CoverPhotoKey = null;
            Variants.Clear();
            OnPropertyChanged(nameof(HasProduct));
            OnPropertyChanged(nameof(IsUnknown));
            UpdatePhotoPath();
            return;
        }

        Name = resolution.Product.Name;
        SellerAxisSuffix = string.IsNullOrWhiteSpace(resolution.SellerAxisValue)
            ? ""
            : $" · {resolution.SellerAxisValue!.Trim()}";
        CoverPhotoKey = resolution.Product.CoverPhotoKey;

        Variants.Clear();
        foreach (var v in resolution.Variants)
            Variants.Add(new CatalogVariantViewModel(v, resolution.Product.Code));

        OnPropertyChanged(nameof(HasProduct));
        OnPropertyChanged(nameof(IsUnknown));
        UpdatePhotoPath();
    }
```

Yeni özellikleri ekle (`ObservableProperty` kalıbı dosyada zaten kullanılıyor):

```csharp
    /// <summary>Kartta ürün adının ardına eklenen " · Siyah" son eki; yoksa boş.</summary>
    [ObservableProperty]
    private string _sellerAxisSuffix = "";

    /// <summary>Çözülmüş kod; sipariş akışı bunu kart üzerinden okur.</summary>
    public BroadcastCodeResolution? Resolution => _resolution;
    private BroadcastCodeResolution? _resolution;
```

`HasProduct` / `IsUnknown` hesaplarını `_resolution`'a bağla:

```csharp
    public bool HasProduct => _resolution is not null;
    public bool IsUnknown => Code.Length > 0 && _resolution is null;
```

- [ ] **Adım 4: Kaydı güncelle**

`BroadcastCodeResolver` DI'da yok. `OrderDeck.App/AppHost.cs` içinde
`CatalogReplicaRepository` kaydının hemen ardına ekle:

```csharp
        services.AddSingleton<OrderDeck.Core.Catalog.BroadcastCodeResolver>();
```

- [ ] **Adım 5: XAML'i güncelle**

`OrderDeck.App/Views/Shell/ProductCard.xaml` — `HasProduct` bölümündeki ürün adı
`TextBlock`'unu `Run`'lara böl. Son eki ayrı `Run` yapmak bilinçli: satıcı ekseni
değerini ikincil renkte göstermek istiyoruz ve `Text` bağlaması tek parça
olsaydı string birleştirme için dönüştürücü gerekirdi.

```xml
<TextBlock TextTrimming="CharacterEllipsis"
           Style="{StaticResource ProductCardTitle}">
    <Run Text="{Binding Name, Mode=OneWay}" />
    <Run Text="{Binding SellerAxisSuffix, Mode=OneWay}"
         Foreground="{DynamicResource TextMutedBrush}" />
</TextBlock>
```

(`ProductCardTitle` ve `TextMutedBrush` adlarını dosyadaki gerçek kaynak
adlarıyla değiştir — mevcut `TextBlock`'un `Style`/`Foreground` değerlerini aynen
koru.)

- [ ] **Adım 6: Testleri çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCard`
Beklenen: `ProductCardViewModelTests` ve `ProductCardTemplateTests` PASS
(özellikle `Only_one_of_the_three_sections_is_visible_at_a_time` — üç bölümün
karşılıklı dışlaması bozulmamalı).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/ViewModels/ProductCardViewModel.cs OrderDeck.App/Views/Shell/ProductCard.xaml OrderDeck.App/AppHost.cs OrderDeck.Tests/ViewModels/ProductCardViewModelTests.cs
git commit -m "feat(kart): kod kutusu yayın kodunu çözüyor, kart satıcı eksenini gösteriyor"
```

---

### Görev 7: `AxisValueMatcher` — yorum metni → izleyici ekseni değerleri

**Dosyalar:**
- Oluştur: `OrderDeck.Core/Catalog/AxisValueMatcher.cs`
- Test: `OrderDeck.Tests/Catalog/AxisValueMatcherTests.cs`

Saf sınıf: veritabanı yok, WPF yok, tek girdisi metin. Tasarımın **token eşitliği,
substring YOK, fuzzy YOK** kuralı burada uygulanıyor — `l`/`1` karışması yanlış
beden → iade → çift yönlü kargo demek.

- [ ] **Adım 1: Testi yaz**

`OrderDeck.Tests/Catalog/AxisValueMatcherTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Catalog;
using Xunit;

namespace OrderDeck.Tests.Catalog;

public class AxisValueMatcherTests
{
    private static readonly string[] Bedenler = { "S", "M", "L", "XL" };

    private static AxisMatchResult M(string comment, string code = "Ateş", string[]? values = null)
        => AxisValueMatcher.Match(comment, code, values ?? Bedenler);

    // --- Kabul kriteri 5 ---
    [Fact]
    public void Tek_tam_eslesme_cekmece_actirmaz()
    {
        var r = M("ateş m");
        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("M");
        r.NeedsPicker.Should().BeFalse();
    }

    // --- Kabul kriteri 6: substring eşleştirme YOK ---
    [Fact]
    public void Xl_tek_eslesmedir_x_ve_l_diye_bolunmez()
    {
        var r = M("ateş xl");
        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("XL");
        r.NeedsPicker.Should().BeFalse();
    }

    // --- Kabul kriteri 7 ---
    [Fact]
    public void Bitisik_yazim_kombinasyon_olarak_cozulur()
    {
        var r = M("ateş ml");
        r.Kind.Should().Be(AxisMatchKind.Combination);
        r.Values.Should().Equal("M", "L");
        // Kombinasyon TAHMİNDİR; operatör onaylamadan sipariş yazılmaz.
        r.NeedsPicker.Should().BeTrue();
    }

    // --- Kabul kriteri 8 ---
    [Fact]
    public void Hicbir_sey_eslesmezse_bos_sonuc_doner()
    {
        var r = M("bana da");
        r.Kind.Should().Be(AxisMatchKind.None);
        r.Values.Should().BeEmpty();
        r.NeedsPicker.Should().BeTrue();
    }

    // --- Kabul kriteri 9 ---
    [Fact]
    public void Iki_ayri_token_iki_deger_verir()
    {
        var r = M("ateş m l");
        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("M", "L");
        r.NeedsPicker.Should().BeTrue();   // birden çok → onay istenir
    }

    [Fact]
    public void Tek_degere_inen_kombinasyon_bile_onay_ister()
    {
        // "mm" → M + M → tekilleşip tek değere iniyor, ama yine de TAHMİN;
        // operatör "M" mi yoksa yazım hatası mı bilmiyoruz.
        var r = M("ateş mm");
        r.Kind.Should().Be(AxisMatchKind.Combination);
        r.Values.Should().Equal("M");
        r.NeedsPicker.Should().BeTrue();
    }

    [Fact]
    public void Belirsiz_bolme_hicbir_seyi_isaretlemez()
    {
        // "LSM" hem L+S+M hem LS+M diye bölünebiliyor → tahmin etmeyiz.
        var r = M("ateş lsm", values: new[] { "S", "M", "L", "LS" });
        r.Kind.Should().Be(AxisMatchKind.None);
        r.Values.Should().BeEmpty();
    }

    [Fact]
    public void Dolgu_kelimeleri_atilir()
    {
        M("ateş beden m").Values.Should().Equal("M");
        M("ateş bedeni m").Values.Should().Equal("M");
        M("ateş numara 38", values: new[] { "36", "38" }).Values.Should().Equal("38");
    }

    [Fact]
    public void Es_anlamlilar_tam_tarama_basarisiz_olunca_devreye_girer()
    {
        M("ateş medium").Values.Should().Equal("M");
        M("ateş küçük").Values.Should().Equal("S");
        M("ateş büyük").Values.Should().Equal("L");
    }

    [Fact]
    public void Eksende_ORTA_varsa_es_anlamli_devreye_girmez()
    {
        // Eş anlamlı geçişi tam taramadan SONRA çalışıyor; aksi hâlde ekseninde
        // gerçekten "Orta" yazan ürün eşleşmeyi kaybederdi.
        var r = M("ateş orta", values: new[] { "Orta", "Büyük" });
        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("Orta");
    }

    [Fact]
    public void Cok_kelimeli_yayin_kodu_metinden_silinir()
    {
        var r = AxisValueMatcher.Match("güzel elbise m", "Güzel Elbise", Bedenler);
        r.Values.Should().Equal("M");
    }

    [Fact]
    public void Cok_kelimeli_eksen_degeri_eslesir()
    {
        var r = M("ateş 50 ml", values: new[] { "50 ML", "100 ML" });
        r.Kind.Should().Be(AxisMatchKind.Exact);
        r.Values.Should().Equal("50 ML");
    }

    [Fact]
    public void Noktalama_ve_turkce_harf_onemsiz()
    {
        AxisValueMatcher.Match("ATEŞ, m!", "ateş", Bedenler).Values.Should().Equal("M");
    }

    [Fact]
    public void Tireli_deger_bozulmaz()
    {
        M("ateş 36-38", values: new[] { "36-38", "40-42" }).Values.Should().Equal("36-38");
    }

    [Fact]
    public void Eksen_degeri_yoksa_bos_doner()
    {
        AxisValueMatcher.Match("ateş m", "Ateş", Array.Empty<string>())
            .Kind.Should().Be(AxisMatchKind.None);
    }
}
```

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~AxisValueMatcherTests`
Beklenen: derleme hatası — `AxisValueMatcher` yok.

- [ ] **Adım 3: Sınıfı yaz**

`OrderDeck.Core/Catalog/AxisValueMatcher.cs`:

```csharp
using OrderDeck.Shared.Text;

namespace OrderDeck.Core.Catalog;

/// <summary>Eşleşmenin nasıl bulunduğu — çekmecenin açılıp açılmayacağını belirler.</summary>
public enum AxisMatchKind
{
    /// <summary>Hiçbir değer bulunamadı.</summary>
    None,
    /// <summary>Token eşitliğiyle (ya da eş anlamlıyla) doğrudan bulundu.</summary>
    Exact,
    /// <summary>Bitişik yazımın bölünmesiyle TAHMİN edildi; onay ister.</summary>
    Combination
}

/// <param name="Values">Eksen değerlerinin kanonik hâli, eksen sırasında.</param>
public sealed record AxisMatchResult(AxisMatchKind Kind, IReadOnlyList<string> Values)
{
    public static readonly AxisMatchResult Empty =
        new(AxisMatchKind.None, Array.Empty<string>());

    /// <summary>
    /// Çekmece açılmalı mı? Tasarımın kuralı: <b>yalnız</b> tam ve tek eşleşmede
    /// akış kesilmeden sipariş yazılır. Kombinasyon tek değere inse bile onay
    /// ister — o bir tahmin, operatörün gördüğü bir şey değil.
    /// </summary>
    public bool NeedsPicker => Kind != AxisMatchKind.Exact || Values.Count != 1;
}

/// <summary>
/// İzleyici yorumundan eksen değerlerini çıkarır. <b>Substring yok, fuzzy yok</b>:
/// yalnız token eşitliği. Sebep somut — "l" ile "1" birbirine benziyor; yanlış
/// beden yazmak iade ve çift yönlü kargo demek. Emin olmadığımızda hiçbir şey
/// işaretlemeyip operatöre soruyoruz.
/// </summary>
public static class AxisValueMatcher
{
    // Normalize edilmiş hâlleriyle (büyük harf, Türkçe katlanmış).
    private static readonly HashSet<string> Fillers = new(StringComparer.Ordinal)
        { "BEDEN", "BEDENI", "NUMARA", "NUMARASI", "NO" };

    private static readonly Dictionary<string, string> Synonyms = new(StringComparer.Ordinal)
    {
        ["SMALL"] = "S", ["KUCUK"] = "S",
        ["MEDIUM"] = "M", ["ORTA"] = "M",
        ["LARGE"] = "L", ["BUYUK"] = "L"
    };

    // Noktalama token'ın UCUNDAN kırpılır, İÇİNDEN değil: "m!" → "M" olurken
    // "36-38" bozulmadan kalmalı.
    private static readonly char[] EdgePunctuation = ".,;:!?()[]{}\"'".ToCharArray();

    // Bitişik yazım bölmesinin girdi sınırı. Uzun token'da olası bölme sayısı
    // üstel büyür; 12 hane hiçbir gerçek beden yazımını dışarıda bırakmıyor.
    private const int MaxCombinationTokenLength = 12;

    /// <param name="comment">İzleyicinin ham yorumu.</param>
    /// <param name="activeCode">
    /// Operatörün kutudaki aktif yayın kodu. Kod metinden <b>silinir</b>; aranmaz.
    /// Bu ayrım çok kelimeli kodları ("Güzel Elbise") sorunsuz kılar — kodun ne
    /// olduğunu zaten biliyoruz, keşfetmemiz gerekmiyor.
    /// </param>
    /// <param name="axisValues">İzleyici ekseninin kanonik değerleri, eksen sırasında.</param>
    public static AxisMatchResult Match(
        string? comment, string? activeCode, IReadOnlyList<string> axisValues)
    {
        if (axisValues.Count == 0) return AxisMatchResult.Empty;

        var tokens = Tokenize(comment);
        if (tokens.Count == 0) return AxisMatchResult.Empty;

        var codeTokens = Tokenize(activeCode);
        while (RemoveSequence(tokens, codeTokens)) { }

        tokens.RemoveAll(Fillers.Contains);
        if (tokens.Count == 0) return AxisMatchResult.Empty;

        // Uzun değer dizisi önce denenir: "50 ML" varken "ML" tek başına
        // tüketilmesin.
        var candidates = axisValues
            .Select((v, i) => new Candidate(i, Tokenize(v)))
            .Where(c => c.Tokens.Count > 0)
            .OrderByDescending(c => c.Tokens.Count)
            .ToList();

        // SortedSet: sonuç her zaman EKSEN sırasında ve tekil çıkar.
        var hits = new SortedSet<int>();

        // 1) Tam tarama — tüketerek. Aynı değer iki kez yazıldıysa bir kez sayılır.
        foreach (var c in candidates)
            while (RemoveSequence(tokens, c.Tokens))
                hits.Add(c.Index);

        if (hits.Count > 0)
            return new AxisMatchResult(AxisMatchKind.Exact, Project(axisValues, hits));

        // 2) Eş anlamlılar — YALNIZ tam tarama boş dönünce. Aksi hâlde ekseninde
        //    gerçekten "Orta" yazan ürün "orta"yı M'ye çevirip eşleşmeyi kaybederdi.
        var singles = candidates.Where(c => c.Tokens.Count == 1).ToList();
        foreach (var t in tokens)
        {
            if (!Synonyms.TryGetValue(t, out var target)) continue;
            var c = singles.FirstOrDefault(x => x.Tokens[0] == target);
            if (c is not null) hits.Add(c.Index);
        }

        if (hits.Count > 0)
            return new AxisMatchResult(AxisMatchKind.Exact, Project(axisValues, hits));

        // 3) Bitişik yazım bölmesi — son çare, sonucu her hâlükârda onaya gider.
        foreach (var token in tokens)
        {
            if (token.Length > MaxCombinationTokenLength) continue;

            var solutions = new List<List<int>>();
            Collect(token, 0, singles, new List<int>(), solutions, limit: 2);

            // Birden çok bölme mümkünse TAHMİN ETMEYİZ: yanlış tahmin, hiç
            // tahmin etmemekten daha pahalı (operatör onaylayıp geçebilir).
            if (solutions.Count != 1) return AxisMatchResult.Empty;

            foreach (var i in solutions[0]) hits.Add(i);
        }

        return hits.Count == 0
            ? AxisMatchResult.Empty
            : new AxisMatchResult(AxisMatchKind.Combination, Project(axisValues, hits));
    }

    private sealed record Candidate(int Index, List<string> Tokens);

    private static IReadOnlyList<string> Project(IReadOnlyList<string> axisValues, SortedSet<int> hits)
        => hits.Select(i => axisValues[i]).ToList();

    private static List<string> Tokenize(string? text)
    {
        var normalized = SearchNormalizer.Normalize(text);
        if (normalized.Length == 0) return new List<string>();

        return normalized
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim(EdgePunctuation))
            .Where(t => t.Length > 0)
            .ToList();
    }

    /// <summary>
    /// <paramref name="needle"/> dizisini <paramref name="tokens"/> içinde
    /// <b>bitişik alt dizi</b> olarak arar ve ilk bulduğunu siler. Dönüş: silindi mi.
    /// </summary>
    private static bool RemoveSequence(List<string> tokens, IReadOnlyList<string> needle)
    {
        if (needle.Count == 0 || needle.Count > tokens.Count) return false;

        for (var i = 0; i + needle.Count <= tokens.Count; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Count; j++)
            {
                if (string.Equals(tokens[i + j], needle[j], StringComparison.Ordinal)) continue;
                ok = false;
                break;
            }
            if (!ok) continue;

            tokens.RemoveRange(i, needle.Count);
            return true;
        }
        return false;
    }

    /// <summary>
    /// Tek token'ı eksen değerlerinin ardışık birleşimi olarak bölmeye çalışır.
    /// En az İKİ parça arar: tek parçalık bölme zaten tam eşleşmedir ve bu
    /// noktaya gelinmişse tam tarama başarısız olmuştur.
    /// </summary>
    private static void Collect(
        string token, int pos, List<Candidate> singles,
        List<int> path, List<List<int>> found, int limit)
    {
        if (found.Count >= limit) return;

        if (pos == token.Length)
        {
            if (path.Count >= 2) found.Add(new List<int>(path));
            return;
        }

        foreach (var c in singles)
        {
            var value = c.Tokens[0];
            if (pos + value.Length > token.Length) continue;
            if (string.CompareOrdinal(token, pos, value, 0, value.Length) != 0) continue;

            path.Add(c.Index);
            Collect(token, pos + value.Length, singles, path, found, limit);
            path.RemoveAt(path.Count - 1);

            if (found.Count >= limit) return;
        }
    }
}
```

- [ ] **Adım 4: Testi çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~AxisValueMatcherTests`
Beklenen: 15 test PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.Core/Catalog/AxisValueMatcher.cs OrderDeck.Tests/Catalog/AxisValueMatcherTests.cs
git commit -m "feat(eşleştirme): yorumdan izleyici ekseni değerlerini çıkaran eşleştirici"
```

---

### Görev 8: `Label` katalog kimliklerini taşısın (göç 028)

**Dosyalar:**
- Oluştur: `OrderDeck.Core/Storage/Migrations/028_label_catalog_ids.sql`
- Değiştir: `OrderDeck.Core/Sales/Label.cs`
- Değiştir: `OrderDeck.Core/Storage/Repositories/LabelRepository.cs`
- Değiştir: `OrderDeck.Core/Sales/LabelService.cs`
- Değiştir: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs:32` ve `:66`
- Test: `OrderDeck.Tests/Storage/LabelRepositoryTests.cs` (var olan dosya)

- [ ] **Adım 1: Testi yaz**

`OrderDeck.Tests/Storage/LabelRepositoryTests.cs` sınıfının içine ekle:

```csharp
    [Fact]
    public void Katalog_kimlikleri_gidip_geliyor()
    {
        var repo = NewRepo();
        var label = NewLabel() with
        {
            ProductId = "p1",
            ProductVariantId = "v2"
        };

        repo.Insert(label);

        var back = repo.GetById(label.Id);
        back!.ProductId.Should().Be("p1");
        back.ProductVariantId.Should().Be("v2");
    }

    [Fact]
    public void Katalog_kimlikleri_opsiyonel()
    {
        // Bilinmeyen kod / katalog dışı satış: satır yine yazılır, sadece
        // stok hareketi doğmaz (kabul kriteri 10).
        var repo = NewRepo();
        var label = NewLabel();

        repo.Insert(label);

        var back = repo.GetById(label.Id);
        back!.ProductId.Should().BeNull();
        back.ProductVariantId.Should().BeNull();
    }
```

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LabelRepositoryTests`
Beklenen: derleme hatası — `Label`'da `ProductId` yok.

- [ ] **Adım 3: Göçü yaz**

`OrderDeck.Core/Storage/Migrations/028_label_catalog_ids.sql`:

```sql
-- Sipariş satırı artık hangi katalog ürününe/varyantına ait olduğunu taşıyor.
-- Bu iki kolon olmadan sunucu stok düşemez: sunucu yalnız Code metnini görüyordu
-- ve o metin yayın kodu, ürün kimliği değil.
--
-- İkisi de NULL kalabilir ve bu MEŞRU bir durum:
--   * Bilinmeyen kod / kod yokken yazılan satır → ikisi de NULL, stok hareketi yok.
--   * Eksensiz ürün (kolye) → ProductId dolu, ProductVariantId NULL; stok ürün
--     düzeyinden düşer (kabul kriteri 11).
-- Kimlikler sunucudaki GUID'ler, TEXT "N" biçiminde (32 hane, tiresiz).
-- FK yok: bunlar SUNUCU kimlikleri, yerel katalog replikası ise her senkronda
-- baştan yazılıyor — FK kursak, sunucuda arşivlenen bir ürün eski sipariş
-- satırlarını kilitlerdi.
ALTER TABLE Label ADD COLUMN ProductId TEXT;
ALTER TABLE Label ADD COLUMN ProductVariantId TEXT;

UPDATE _meta SET SchemaVersion = 28 WHERE Id = 1;
```

`OrderDeck.Tests/Storage/MigrationRunnerTests.cs` içindeki iki `version.Should().Be(27);`
satırını `version.Should().Be(28);` yap.

- [ ] **Adım 4: Kaydı güncelle**

`OrderDeck.Core/Sales/Label.cs` — `SyncedAt`'ten sonra iki parametre ekle
(sonda olmaları şart: `Label` konumsal record ve mevcut çağrılar sıraya bağlı):

```csharp
    long? SyncedAt = null,
    /// <summary>Katalog ürününün sunucudaki kimliği ("N" biçimi). Null =
    /// satır bir katalog ürününe bağlanamadı (bilinmeyen kod, kodsuz satış);
    /// sunucu bu satır için stok hareketi üretmez.</summary>
    string? ProductId = null,
    /// <summary>Katalog varyantının sunucudaki kimliği. ProductId dolu iken
    /// bunun null olması meşrudur: ürünün hiç ekseni yoksa stok ürün
    /// düzeyinden düşer.</summary>
    string? ProductVariantId = null);
```

- [ ] **Adım 5: Depoyu güncelle**

`OrderDeck.Core/Storage/Repositories/LabelRepository.cs` — **beş** noktaya
dokunulacak. Eksik bırakılan bir SELECT sessizce null döndürür, bu yüzden hepsi
tek tek:

**(a) `Insert` (satır ~20):** kolon listesine ve `VALUES`'a ekle, anonim nesneye
alanları koy:

```csharp
            @"INSERT INTO Label
              (Id, SessionId, CustomerId, Platform, Username, DisplayName, MessageText, Code, Price, AddedAt, PrintedAt,
               IsBackupPromoted, ParentLabelId, IsTentativeBackup, IsShippingFee, ShipmentId, SyncedAt,
               ProductId, ProductVariantId)
              VALUES
              (@Id, @SessionId, @CustomerId, @Platform, @Username, @DisplayName, @MessageText, @Code, @Price, @AddedAt, @PrintedAt,
               @IsBackupPromoted, @ParentLabelId, @IsTentativeBackup, @IsShippingFee, @ShipmentId, @SyncedAt,
               @ProductId, @ProductVariantId)",
            new
            {
                l.Id, l.SessionId, l.CustomerId, l.Platform, l.Username, l.DisplayName, l.MessageText,
                l.Code, l.Price, l.AddedAt, l.PrintedAt,
                IsBackupPromoted = l.IsBackupPromoted ? 1 : 0,
                l.ParentLabelId,
                IsTentativeBackup = l.IsTentativeBackup ? 1 : 0,
                IsShippingFee = l.IsShippingFee ? 1 : 0,
                l.ShipmentId,
                l.SyncedAt,
                l.ProductId,
                l.ProductVariantId
            });
```

**(b) Tam `Row` SELECT'leri — satır 49, 65, 84, 164, 398.** Beşinin de sonu aynı:

```
                     ... IsShippingFee, ShipmentId, SyncedAt
```

Beşini de şununla değiştir:

```
                     ... IsShippingFee, ShipmentId, SyncedAt, ProductId, ProductVariantId
```

Doğrulama komutu (değişiklikten sonra 5 eşleşme vermeli):
`grep -c "ProductVariantId" OrderDeck.Core/Storage/Repositories/LabelRepository.cs`
→ Insert'teki 3 + SELECT'lerdeki 5 + `Row`daki 1 + `Map`teki 1 = **10**.

> Satır 290 ve 418'deki kısmi SELECT'ler raporlama içindir ve `Row`'a
> eşlenmiyor — onlara **dokunma**.

**(c) `Row` sınıfı (satır ~465):** `SyncedAt`'ten sonra ekle:

```csharp
        public string? ProductId { get; init; }
        public string? ProductVariantId { get; init; }
```

**(d) `Map` (satır ~434):** son argümanların ardına ekle:

```csharp
            SyncedAt: r.SyncedAt,
            ProductId: r.ProductId,
            ProductVariantId: r.ProductVariantId);
```

- [ ] **Adım 6: `LabelService.Add`'i genişlet**

`OrderDeck.Core/Sales/LabelService.cs`:

```csharp
    public Label Add(string sessionId, ChatMessage message, decimal price, string? code,
        bool isBackupPromoted = false, string? parentLabelId = null,
        bool isTentativeBackup = false,
        string? productId = null, string? productVariantId = null)
```

Metodun içinde `new Label(...)` kurulurken iki adlandırılmış argümanı ekle:

```csharp
            ProductId: productId,
            ProductVariantId: productVariantId);
```

(Var olan çağıranlar opsiyonel parametreleri geçmediği için değişmez.)

- [ ] **Adım 7: Testleri çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter "FullyQualifiedName~LabelRepositoryTests|FullyQualifiedName~MigrationRunnerTests"`
Beklenen: hepsi PASS.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.Core/Storage/Migrations/028_label_catalog_ids.sql OrderDeck.Core/Sales/Label.cs OrderDeck.Core/Sales/LabelService.cs OrderDeck.Core/Storage/Repositories/LabelRepository.cs OrderDeck.Tests/Storage/LabelRepositoryTests.cs OrderDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(sipariş): satırlar katalog ürün ve varyant kimliğini taşıyor"
```

---

### Görev 9: Varyant seçici çekmecesi ve sipariş akışı

**Dosyalar:**
- Oluştur: `OrderDeck.App/ViewModels/VariantPickerViewModel.cs`
- Oluştur: `OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml`
- Oluştur: `OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml.cs`
- Değiştir: `OrderDeck.App/ViewModels/MainShellViewModel.cs:884-903`
- Değiştir: `OrderDeck.App/Views/Shell/ChatPanel.xaml.cs`
- Test: `OrderDeck.Tests/ViewModels/VariantPickerFlowTests.cs`

- [ ] **Adım 1: Görünüm modelini yaz**

`OrderDeck.App/ViewModels/VariantPickerViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>Çekmecedeki tek satır: bir izleyici ekseni değeri.</summary>
public sealed partial class VariantPickerItemViewModel : ObservableObject
{
    public VariantPickerItemViewModel(string value, bool isChecked)
    {
        Value = value;
        _isChecked = isChecked;
    }

    public string Value { get; }

    [ObservableProperty]
    private bool _isChecked;
}

/// <summary>
/// Varyant seçici çekmecesinin içeriği. Çekmece <b>yalnız</b> kod katalogda
/// çözüldüğünde, üründe izleyici ekseni varken ve eşleşme sayısı 1 DEĞİLKEN
/// açılır — tek eşleşmede akış kesilmez, operatör hiçbir şey tıklamaz.
/// </summary>
public sealed partial class VariantPickerViewModel : ObservableObject
{
    public VariantPickerViewModel(BroadcastCodeResolution resolution, AxisMatchResult match)
    {
        ProductLine = string.IsNullOrWhiteSpace(resolution.SellerAxisValue)
            ? resolution.Product.Name
            : $"{resolution.Product.Name} · {resolution.SellerAxisValue!.Trim()}";
        AxisName = resolution.ViewerAxisName ?? "";

        Hint = match.Kind switch
        {
            // Kombinasyon bir TAHMİN; operatör onaylamadan sipariş yazılmaz.
            AxisMatchKind.Combination => "Bitişik yazımdan tahmin edildi — onayla.",
            AxisMatchKind.None => "Yorumda beden bulunamadı.",
            _ => "Birden çok beden yazılmış — her biri ayrı satır olur."
        };

        var preselected = new HashSet<string>(match.Values, StringComparer.OrdinalIgnoreCase);
        Items = new ObservableCollection<VariantPickerItemViewModel>(
            resolution.ViewerAxisValues.Select(v =>
                new VariantPickerItemViewModel(v, preselected.Contains(v))));

        foreach (var item in Items)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VariantPickerItemViewModel.IsChecked))
                    OnPropertyChanged(nameof(CanConfirm));
            };
    }

    public string ProductLine { get; }
    public string AxisName { get; }
    public string Hint { get; }
    public ObservableCollection<VariantPickerItemViewModel> Items { get; }

    /// <summary>Hiçbir şey işaretli değilken onay düğmesi kapalı: boş sipariş yazılamaz.</summary>
    public bool CanConfirm => Items.Any(i => i.IsChecked);

    /// <summary>Onaylanan değerler; <b>her biri ayrı sipariş satırı</b> olur.</summary>
    public IReadOnlyList<string> SelectedValues =>
        Items.Where(i => i.IsChecked).Select(i => i.Value).ToList();
}
```

- [ ] **Adım 2: Akış testini yaz**

`OrderDeck.Tests/ViewModels/VariantPickerFlowTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class VariantPickerFlowTests
{
    /// <summary>
    /// WPF denetimi KURMAYAN sahte çekmece servisi: builder hiç çağrılmıyor,
    /// içerik görünüm modeli kabuğun <c>ActiveVariantPicker</c> özelliğinden
    /// okunuyor. Böylece akış testleri STA thread gerektirmiyor.
    /// </summary>
    private sealed class FakeDrawers : IDrawerService
    {
        private readonly Func<VariantPickerViewModel, bool> _act;
        private readonly Func<VariantPickerViewModel?> _current;

        public int ShowCount { get; private set; }

        public FakeDrawers(Func<VariantPickerViewModel?> current,
                           Func<VariantPickerViewModel, bool> act)
        { _current = current; _act = act; }

        public Task<bool> ShowAsync(string title, Func<Drawer, object> buildContent)
        {
            ShowCount++;
            var vm = _current();
            vm.Should().NotBeNull();
            return Task.FromResult(_act(vm!));
        }

        public bool CloseTop() => false;
    }

    [Fact]
    public async Task Tek_tam_eslesmede_cekmece_ACILMAZ()
    {
        var (shell, drawers) = Harness.Build(
            comment: "ateş m", act: _ => true);

        await shell.AddChatToQueueAsync(Harness.Message("ateş m"));

        drawers.ShowCount.Should().Be(0);
        shell.PrintQueue.Should().HaveCount(1);
        Harness.LastLabel(shell).ProductVariantId.Should().Be("v2");   // (Siyah, M)
    }

    [Fact]
    public async Task Esc_hicbir_siparis_yazmaz()
    {
        // Kabul kriteri 8: boş çekmece + Esc → sipariş YOK.
        var (shell, drawers) = Harness.Build(comment: "bana da", act: _ => false);

        await shell.AddChatToQueueAsync(Harness.Message("bana da"));

        drawers.ShowCount.Should().Be(1);
        shell.PrintQueue.Should().BeEmpty();
    }

    [Fact]
    public async Task Iki_isaretli_deger_IKI_ayri_satir_yazar()
    {
        // Kabul kriteri 9.
        var (shell, drawers) = Harness.Build(comment: "ateş m l", act: vm =>
        {
            vm.SelectedValues.Should().Equal("M", "L");   // ikisi de önceden işaretli
            return true;
        });

        await shell.AddChatToQueueAsync(Harness.Message("ateş m l"));

        drawers.ShowCount.Should().Be(1);
        shell.PrintQueue.Should().HaveCount(2);
    }

    [Fact]
    public async Task Bilinmeyen_kodda_cekmece_acilmaz_satir_yazilir()
    {
        // Kabul kriteri 10: satır bugünkü gibi yazılır, katalog kimliği yok.
        var (shell, drawers) = Harness.Build(
            comment: "zzz m", activeCode: "zzz", act: _ => true);

        await shell.AddChatToQueueAsync(Harness.Message("zzz m"));

        drawers.ShowCount.Should().Be(0);
        shell.PrintQueue.Should().HaveCount(1);
        Harness.LastLabel(shell).ProductId.Should().BeNull();
        Harness.LastLabel(shell).ProductVariantId.Should().BeNull();
    }

    [Fact]
    public async Task Eksensiz_urunde_cekmece_acilmaz_varyant_null()
    {
        // Kabul kriteri 11: kolye → stok üründen düşer.
        var (shell, drawers) = Harness.BuildAxisless(comment: "buz", activeCode: "buz");

        await shell.AddChatToQueueAsync(Harness.Message("buz"));

        drawers.ShowCount.Should().Be(0);
        Harness.LastLabel(shell).ProductId.Should().NotBeNull();
        Harness.LastLabel(shell).ProductVariantId.Should().BeNull();
    }
}
```

> `Harness` yardımcısını `OrderDeck.Tests/ViewModels/` altında var olan
> `MainShellViewModel` kurulum yardımcısından türet (bkz. mevcut
> `MainShellViewModelTests` içindeki inşa kodu). Harness'ın yapması gerekenler:
> InMemory SQLite + göçler, replikaya `Elbise` (Renk=satıcı, Beden=izleyici) ve
> varyantlar `v1=(Siyah,S)`, `v2=(Siyah,M)`, `v3=(Siyah,L)` + kod `ATEŞ`,
> `Kolye` + kod `BUZ`; aktif yayın açık; `ActivePriceText = "100"`;
> `ActiveCode` parametreden (varsayılan `"ateş"`); `FakeDrawers` bağlı.
> `Harness.LastLabel(shell)` en son eklenen `Label`'ı depodan okur.

- [ ] **Adım 3: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~VariantPickerFlowTests`
Beklenen: derleme hatası — `AddChatToQueueAsync` ve `ActiveVariantPicker` yok.

- [ ] **Adım 4: Kabuğun akışını yaz**

`OrderDeck.App/ViewModels/MainShellViewModel.cs` — satır 884-903'teki
`AddChatToQueue` metodunu tamamen şununla değiştir:

```csharp
    /// <summary>
    /// Çekmece açıkken gelen ikinci çift-tık yok sayılsın diye. Tasarım
    /// "çekmece akışı keser" diyor, ama <c>DrawerHost</c> bilinçli olarak MODAL
    /// DEĞİL (operatör çekmece açıkken sohbeti okumaya devam edebilmeli).
    /// Kesme kuralını bu yüzden görsel bir perdeyle değil, burada uyguluyoruz.
    /// </summary>
    private bool _variantPickerOpen;

    /// <summary>Açık çekmecenin içeriği; testler akışı bunun üzerinden sürer.</summary>
    public VariantPickerViewModel? ActiveVariantPicker { get; private set; }

    public async Task AddChatToQueueAsync(ChatMessageViewModel messageVm)
    {
        if (_variantPickerOpen) return;

        var session = _sessions.GetActive();
        if (session is null)
        {
            _dialogs.Show("Önce yayın başlat.", "Aktif yayın yok");
            return;
        }

        if (!TryParsePrice(ActivePriceText, out var price))
        {
            _dialogs.Show("Geçerli bir fiyat gir (örn: 100 veya 99.50).",
                "Geçersiz fiyat", DialogSeverity.Warning);
            return;
        }

        var code = string.IsNullOrWhiteSpace(ActiveCode) ? null : ActiveCode.Trim();
        var resolution = ProductCard.Resolution;

        // Kod katalogda çözülmediyse veya üründe izleyici ekseni yoksa seçilecek
        // bir şey yok: satır bugünkü gibi tek seferde yazılır.
        if (resolution is null || !resolution.HasViewerAxis)
        {
            WriteOrder(session.Id, messageVm, price, code, resolution, null);
            return;
        }

        var match = AxisValueMatcher.Match(
            messageVm.Message.Text, code, resolution.ViewerAxisValues);

        if (!match.NeedsPicker)
        {
            WriteOrder(session.Id, messageVm, price, code, resolution, match.Values[0]);
            return;
        }

        if (_drawers is null) return;   // yalnız testte (sahte servis verilmediyse)

        var picker = new VariantPickerViewModel(resolution, match);
        ActiveVariantPicker = picker;
        _variantPickerOpen = true;
        try
        {
            var confirmed = await _drawers.ShowAsync(
                $"{picker.ProductLine} — {picker.AxisName}",
                d => Views.Drawers.VariantPickerDrawer.Create(d, picker));

            // Esc / Vazgeç → HİÇBİR sipariş yazılmaz (kabul kriteri 8).
            if (!confirmed) return;

            // İşaretlenen her değer AYRI satır (kabul kriteri 9).
            foreach (var value in picker.SelectedValues)
                WriteOrder(session.Id, messageVm, price, code, resolution, value);
        }
        finally
        {
            _variantPickerOpen = false;
            ActiveVariantPicker = null;
        }
    }

    private void WriteOrder(
        string sessionId, ChatMessageViewModel messageVm, decimal price,
        string? code, BroadcastCodeResolution? resolution, string? viewerAxisValue)
    {
        var label = _labels.Add(sessionId, messageVm.Message, price, code,
            productId: resolution?.Product.Id,
            productVariantId: resolution?.ResolveVariantId(viewerAxisValue));
        PrintQueue.Add(new LabelViewModel(label, messageVm.IsSenderBlacklisted));
    }
```

Dosyanın başına `using OrderDeck.Core.Catalog;` ekle.

Eski senkron `AddChatToQueue` çağıranlarını bul ve `await ...Async(...)` yap:
`grep -rn "AddChatToQueue" OrderDeck.App OrderDeck.Tests`

- [ ] **Adım 5: Çekmece görünümünü yaz**

`OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml`:

```xml
<UserControl x:Class="OrderDeck.App.Views.Drawers.VariantPickerDrawer"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <StackPanel Margin="0,4,0,0">
        <TextBlock Text="{Binding ProductLine}" FontWeight="SemiBold" />
        <TextBlock Text="{Binding Hint}" Opacity="0.7" TextWrapping="Wrap" Margin="0,4,0,12" />

        <ItemsControl ItemsSource="{Binding Items}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <CheckBox Content="{Binding Value}"
                              IsChecked="{Binding IsChecked, Mode=TwoWay}"
                              Margin="0,0,0,8" />
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>

        <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,16,0,0">
            <Button x:Name="CancelButton" Content="Vazgeç" Margin="0,0,8,0"
                    Click="Cancel_OnClick" />
            <Button x:Name="ConfirmButton" Content="Ekle"
                    IsDefault="True"
                    IsEnabled="{Binding CanConfirm}"
                    Click="Confirm_OnClick" />
        </StackPanel>
    </StackPanel>
</UserControl>
```

`OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Varyant seçici. Kurucusu private + statik <c>Create</c>:
/// <c>BackupTransferDrawer</c> ile aynı kalıp — denetim yalnız bir
/// <see cref="Drawer"/> bağlamında var olabilir.
/// </summary>
public partial class VariantPickerDrawer : UserControl
{
    private readonly Drawer _drawer;

    private VariantPickerDrawer(Drawer drawer, VariantPickerViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        DataContext = vm;
    }

    public static VariantPickerDrawer Create(Drawer drawer, VariantPickerViewModel vm)
        => new(drawer, vm);

    private void Confirm_OnClick(object sender, RoutedEventArgs e) => _drawer.Close(true);

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
```

> **Doğrula:** `Drawer.Close`'un varsayılanı ve `IDrawerService.CloseTop()`'un
> ESC yolunda ürettiği sonuç `false` olmalı. `OrderDeck.App/Services/Drawers/`
> altındaki uygulamayı oku; `CloseTop` `Close(true)` çağırıyorsa ESC sipariş
> yazar ve kabul kriteri 8 sessizce bozulur.

- [ ] **Adım 6: Sohbet paneli işleyicilerini güncelle**

`OrderDeck.App/Views/Shell/ChatPanel.xaml.cs` — **iki** işleyici var (çift-tık ve
Enter), ikisi de aynı gövdeyi taşıyor. İkisini de şu hâle getir:

```csharp
    // async void: WPF olay işleyicisinin başka seçeneği yok. Çekmece boyunca
    // atılan istisnaları yutmamak için gövde try/catch İÇERMİYOR — akışta
    // beklenen tek await zaten çekmecenin kapanması.
    private async void ChatList_OnDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is not MainShellViewModel vm) return;
        if (ChatList.SelectedItem is not ChatMessageViewModel msgVm) return;
        if (vm.TryAssignChatAsBackup(msgVm)) return;
        await vm.AddChatToQueueAsync(msgVm);
    }
```

(`ChatList_OnPreviewKeyDown` içindeki Enter dalı için de aynısı; `e.Handled`
atamaları varsa `await`'ten ÖNCE kalsın.)

- [ ] **Adım 7: Testleri çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~VariantPickerFlowTests`
Beklenen: 5 test PASS.

Çalıştır: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Beklenen: 0 hata.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.App/ViewModels/VariantPickerViewModel.cs OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml OrderDeck.App/Views/Drawers/VariantPickerDrawer.xaml.cs OrderDeck.App/ViewModels/MainShellViewModel.cs OrderDeck.App/Views/Shell/ChatPanel.xaml.cs OrderDeck.Tests/ViewModels/VariantPickerFlowTests.cs
git commit -m "feat(yayın): yorumdan varyant eşleştirme ve varyant seçici çekmece"
```

---

### Görev 10: Sipariş senkronu katalog kimliklerini göndersin

**Dosyalar:**
- Değiştir: `OrderDeck.App/Services/Sync/SessionOrderSyncService.cs:107-128`
- Test: `OrderDeck.Tests/Sync/SessionOrderSyncServiceTests.cs` (var olan dosya)

Tel modeli (`SyncOrderItem`) alanları **zaten taşıyor**; WPF onları doldurmuyordu.
`CatalogAware` bayrağı sunucuya "bu istemci katalog kimliği gönderebiliyor"
diyor — bayrak `false` iken sunucu satırları katalog dışı sayıyor.

- [ ] **Adım 1: Testi yaz**

`OrderDeck.Tests/Sync/SessionOrderSyncServiceTests.cs` içine ekle (dosyadaki
sahte `ILicenseApi` yakalayıcısını kullan):

```csharp
    [Fact]
    public async Task Katalog_kimlikleri_sunucuya_gider()
    {
        var (svc, api, labels) = Build();
        labels.Insert(NewLabel() with { ProductId = "p1", ProductVariantId = "v2" });

        await svc.RunOnceAsync(CancellationToken.None);

        var request = api.LastOrdersRequest!;
        // Bayrak false kalırsa sunucu satırı katalog dışı sayar ve stok düşmez.
        request.CatalogAware.Should().BeTrue();
        request.Orders.Single().ProductId.Should().Be(Guid.Parse("p1".PadLeft(32, '0')));
        request.Orders.Single().ProductVariantId.Should().NotBeNull();
    }
```

> Testte kullanılacak kimlikleri gerçek "N" biçiminde GUID yaz (örn.
> `Guid.NewGuid().ToString("N")`); yukarıdaki `PadLeft` yalnız niyeti gösteriyor,
> gerçek testte sabit bir GUID üret ve iki tarafta da onu kullan.

- [ ] **Adım 2: Testi çalıştır, kırmızıyı gör**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~SessionOrderSyncServiceTests`
Beklenen: FAIL — `CatalogAware` false, `ProductId` null.

- [ ] **Adım 3: Servisi güncelle**

`OrderDeck.App/Services/Sync/SessionOrderSyncService.cs`, `PushOrdersAsync`
içinde `IsTentativeBackup` satırından sonra iki alan ekle:

```csharp
            IsTentativeBackup: l.IsTentativeBackup,
            // Yerelde TEXT "N", telde Guid. Ayrıştırılamayan değer null'a
            // düşer: bozuk tek satır bütün partiyi düşürmemeli.
            ProductId: System.Guid.TryParse(l.ProductId, out var pid) ? pid : null,
            ProductVariantId: System.Guid.TryParse(l.ProductVariantId, out var vid) ? vid : null
        )).ToList();
```

Ve istek nesnesine bayrağı ekle:

```csharp
            // Bu istemci katalog kimliği gönderebiliyor; sunucu stok hareketini
            // yalnız bu bayrak açıkken üretiyor.
            _ = await _api.SyncOrdersAsync(licenseId, new SyncOrdersRequest(items, CatalogAware: true), ct);
```

- [ ] **Adım 4: Testi çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~SessionOrderSyncServiceTests`
Beklenen: PASS.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.App/Services/Sync/SessionOrderSyncService.cs OrderDeck.Tests/Sync/SessionOrderSyncServiceTests.cs
git commit -m "feat(senkron): sipariş satırları katalog kimlikleriyle sunucuya gidiyor"
```

---

### Görev 11: Tam doğrulama ve belgeler

**Dosyalar:**
- Değiştir: `docs/superpowers/specs/2026-08-14-yayin-kodu-ve-yorum-eslestirme-design.md`

- [ ] **Adım 1: Tüm test paketini çalıştır**

Çalıştır: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Beklenen: 0 hata. Kırılan varsa **düzelt, atlama** — özellikle
`ProductCardTemplateTests` ve `MainShellViewModelTests`.

- [ ] **Adım 2: WPF'i derle**

Çalıştır: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Beklenen: 0 hata, 0 yeni uyarı.

- [ ] **Adım 3: Ölü kavramın gerçekten gittiğini doğrula**

Çalıştır: `grep -rn "VariantCode" OrderDeck.App OrderDeck.Core OrderDeck.Licensing`
Beklenen: **hiç eşleşme yok.** (Sunucu tarafındaki geçici kalkan Görev 12'ye ait,
o hâlâ duruyor.)

- [ ] **Adım 4: Tasarım belgesine kapanış notu düş**

`docs/superpowers/specs/2026-08-14-yayin-kodu-ve-yorum-eslestirme-design.md`
dosyasının sonuna ekle:

```markdown
## Uygulama notu (2026-08-15)

Plan 3/3 uygulandı: `BroadcastCodeResolver` + `AxisValueMatcher` + varyant seçici
çekmece + `Label.ProductId/ProductVariantId` + `CatalogAware` senkron.

**Kapsamdan çıkarılan tek madde:** sunucudaki geçici uyum kalkanı
(`LicensesWpfCatalogPullController` içindeki `VariantCode` doldurması) bu sürümde
KALDIRILMADI. Sebep: `CatalogVariantPullItem.VariantCode` eski istemcilerde
non-nullable ve replikadaki kolon `NOT NULL`; kalkan kaldırılınca sahadaki eski
WPF kurulumları katalog senkronunu **sessizce** kaybederdi (hata
`CatalogSyncService`'te Warning'e yutuluyor). Kaldırma işi ayrı PR'a alındı ve
yeni WPF sürümünün Velopack ile yayılmasına bağlandı.
```

- [ ] **Adım 5: Commit**

```bash
git add docs/superpowers/specs/2026-08-14-yayin-kodu-ve-yorum-eslestirme-design.md
git commit -m "docs(katalog): plan 3/3 uygulama notu ve kalkan erteleme gerekçesi"
```

---

### Görev 12: (SONRAKİ SÜRÜM) Uyum kalkanının kaldırılması

> **Bu görev bu PR'a DAHİL DEĞİL.** Ayrı bir dalda, ayrı PR olarak ve **yalnız
> yeni WPF sürümü sahaya yayıldıktan sonra** yapılacak. Buraya yazılmasının
> sebebi kaybolmaması.

**Ön koşul (kontrol edilmeden başlama):**
- Yeni WPF sürümü Velopack feed'inde yayınlandı;
- Telemetri/log veya kullanıcı doğrulaması ile eski sürümlerin sahada kalmadığı
  makul ölçüde teyit edildi (ya da eski sürümlerin katalog senkronunu
  kaybetmesi bilinçli olarak kabul edildi).

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`

**Yapılacak:** `GEÇİCİ UYUM KALKANI` yorumunu ve `CatalogVariantDto` kurulumundaki
`null, null, p.Code` doldurmalarını kaldır; sunucu tarafı DTO'yu da istemcideki
yeni şekle (`Id, Axis1Value, Axis2Value, Barcode, IsActive`) daralt.

**Doğrulama:** `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
+ prod'a deploy sonrası yeni WPF ile bir katalog senkronu çalıştırıp replikanın
dolduğunu gör.

---

## Öz-inceleme sonucu

**Tasarım kapsamı:** 11 kabul kriterinin hepsi bir göreve bağlı —
1-3 (sunucu/panel) plan 1-2'de bitti; 4 → Görev 6; 5, 6 → Görev 7 + 9;
7, 8, 9 → Görev 7 + 9; 10 → Görev 9 (`Bilinmeyen_kodda...`); 11 → Görev 5 + 9.

**Tip tutarlılığı:** `BroadcastCodeResolution.ResolveVariantId(string?)`,
`AxisMatchResult.NeedsPicker`, `VariantPickerViewModel.SelectedValues`,
`LabelService.Add(..., productId, productVariantId)` adları görevler arasında
aynı yazıldı; `CatalogVariant` yedi alanlı hâliyle Görev 2'den sonra her yerde
tutarlı.

**Bilerek bırakılan boşluk:** Görev 9'daki `Harness` yardımcısı, var olan
`MainShellViewModelTests` inşa koduna dayandığı için burada satır satır
yazılmadı; yapması gerekenler madde madde listelendi. Görev 9'u uygulayan kişi
önce o dosyayı okumalı.

---

## Kapsam dışı

- `liveChatMessages`/moderasyon, fotoğraf önbelleği, panel tarafı — hiçbirine
  dokunulmuyor.
- Stok uyarıları, "stokta yok" rozetleri, çekmecede stok gösterimi.
- Uyum kalkanının kaldırılması (Görev 12 — ayrı PR, ayrı sürüm).

## Yayın

Tek PR: `feat/wpf-yorum-eslestirme`. Commit'siz duran `.gitignore` /
`.claude/launch.json` / `.codex/` / `AGENTS.md` / eski `docs/` dosyaları bu PR'a
**karıştırılmayacak** — her commit'te dosyalar tek tek `git add` edilecek.
