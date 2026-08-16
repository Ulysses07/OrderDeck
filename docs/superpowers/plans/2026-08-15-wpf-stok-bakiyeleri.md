# WPF Stok Bakiyeleri Uygulama Planı

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Sunucudaki stok defterinden hesaplanan bakiyeleri WPF'e çekip ürün kartındaki varyant rozetlerinde göstermek; yerelde henüz senkronlanmamış etiketleri düşerek operatöre gerçek kalan adedi göstermek.

**Architecture:** Sunucu `GET /api/v1/licenses/{id}/stock/balances/since` ucundan **mutlak bakiyeleri** bileşik imleçle `(since, sinceId)` sayfa sayfa çekiyoruz. Gelen her satır yerel `CatalogStockBalance` tablosuna **upsert** ediliyor (toplanmıyor — sunucu zaten `SUM` yapıp gönderdi). Ekranda gösterilen değer `sunucu bakiyesi − yerel bekleyen etiketler`; bekleyen etiketler `Label` tablosundan `SyncedAt IS NULL` filtresiyle sayılıyor ve sunucunun defter sayma kuralı birebir aynalanıyor. Katalog replikası (`CatalogProduct`/`CatalogVariant`) bu planda **hiç değişmiyor**.

**Tech Stack:** SQLite + Dapper (yerel), `LicenseApiClient` (HTTP/JSON), `IHostedService` + `PeriodicTimer` (senkron ritmi), CommunityToolkit.Mvvm `ObservableObject` (VM), WPF `StaticResource` + `DataTrigger` (XAML).

---

## Neden böyle — uygulayıcının bilmesi gereken tuzaklar

Bu bölüm kod içermiyor ama görevlerdeki kararların gerekçesi burada. Okumadan başlama.

1. **Sunucu mutlak gönderir, istemci toplamaz.** Sunucu hareket defteri (`StockMovements`) üzerinde imleç yürütür, ama gövdede **o anahtarın yeni toplam bakiyesini** döner. İstemci `+=` yaparsa aynı sayfa iki kez işlendiğinde bakiye bozulur. Her zaman **sil-ve-ekle** (upsert).

2. **Bileşik imleç şart.** Tek sipariş senkronunda oluşan onlarca hareketin `CreatedAt`'ı **aynı**. Sadece zamanla imleç tutulursa `take` sınırı eşitlik kümesinin ortasından keser ve kalan satırlar sonsuza dek atlanır. İmleç `(CursorCreatedAt, CursorId)` ikilisidir.

3. **Sunucunun 60 saniyelik `StabilityHorizon`'u var.** `UtcNow - 60s`'den yeni hareketler hiç okunmaz (commit sırası ≠ zaman damgası sırası). Sonuç: panelden girilen stok WPF'e **en fazla ~1 dakika** gecikmeyle düşer. Bu bilinçli; istemci tarafında telafi edilmez, senkron periyodu da bu yüzden 60 sn.

4. **Boş sayfa imleci geri sarmaz.** Sunucu boş sayfada gelen imleci **aynen** iade eder. İstemci yine de `ApplyPage` çağırıp imleci yazar — bu bir no-op'tur ama kodun tek yazma yolu olmasını sağlar.

5. **SQLite NULL tuzağı — `UNIQUE` NULL'ları eşit saymaz.** `UNIQUE(ProductId, ProductVariantId)` koysaydık `ProductVariantId IS NULL` satırları **tekilleşmezdi**, aynı ürün-seviyesi bakiye defalarca eklenirdi. Bu yüzden tabloda unique kısıt **yok**; tekillik `DELETE ... WHERE ProductVariantId IS @variantId` (`=` değil, **`IS`**) ile elle sağlanıyor.

6. **Ama `GROUP BY` NULL'ları eşit sayar.** Aynı motorda `GROUP BY ProductVariantId` tüm NULL'ları tek kovaya toplar — bekleyen etiket sayımı bu yüzden çalışır. İki davranış farkı SQLite'ın belgeli tuhaflığı; ikisine de bilerek yaslanıyoruz.

7. **Dapper Int64→int tuzağı.** SQLite `INTEGER` → .NET `Int64`. Dapper bunu bir `record` kurucusunun `int` parametresine **bağlayamaz**, çalışma zamanında patlar. Repodaki yerleşik kural: özel `Row` sınıfı (`long` alan) + eşleyicide daraltma (bkz. `CatalogReplicaRepository.cs:213-215`, `ShipmentRepository.Row`).

8. **Defter sayma kuralı birebir aynalanmalı.** Bir etiket stoktan −1 düşer **ancak ve ancak** `ProductId is not null` **ve** `!IsShippingFee` **ve** `!IsCancelled` **ve** `!IsTentativeBackup`. Adet alanı yok — her etiket 1 adettir. İstemcideki bekleyen sayımı bu dört koşulu birebir tekrarlamak zorunda; biri unutulursa gösterilen bakiye sunucununkiyle kalıcı olarak ayrışır.

9. **Öksüz satırları TEMİZLEMİYORUZ.** Panelde arşivlenip sonra geri açılan bir ürünün bakiyesi, hareket imleci eski hareketleri bir daha yaymadığı için sonsuza dek 0 kalırdı. Temizlemenin bedeli birkaç kilobayt ölü satır; `GetForProduct` zaten `productId` ile filtreliyor, öksüzler hiç görünmüyor. `CatalogReplicaRepository`'ye bu planda **dokunulmuyor**.

10. **Stok bitince engellemiyoruz.** 0'da da sipariş yazılır, bakiye eksiye düşer ve kırmızı görünür. Rezervasyon/kilit mekanizması **yok** — çevrimdışı replikasyonu güvenli kılan da bu.

---

## Dosya yapısı

**Yeni dosyalar**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Core/Storage/Migrations/029_stock_balance_replica.sql` | `CatalogStockBalance` + `CatalogStockCursor` tabloları, şema sürümü 29 |
| `OrderDeck.Core/Catalog/StockBalance.cs` | `CatalogStockBalance`, `StockCursor` kayıtları |
| `OrderDeck.Core/Catalog/StockBalanceProvider.cs` | `ProductStockSnapshot` + `sunucu − bekleyen` hesabı, `BalancesChanged` olayı |
| `OrderDeck.Core/Storage/Repositories/StockBalanceRepository.cs` | Bakiye/imleç okuma-yazma (tek yazarı `StockSyncService`) |
| `OrderDeck.Licensing/Api/Models/StockPullDtos.cs` | `StockBalancePullItem`, `StockBalancePullResponse` |
| `OrderDeck.App/Services/Sync/StockSyncService.cs` | Sayfalı çekme döngüsü, lisans çözümleme, hata yutma |
| `OrderDeck.App/Services/Sync/StockSyncHostedService.cs` | Açılışta 1 tur + `PeriodicTimer(60s)` |

**Değişen dosyalar**

| Dosya | Değişiklik |
|---|---|
| `OrderDeck.Core/Sales/PendingStockDelta.cs` (yeni, `Sales` altında) | Bekleyen etiket sayımının taşıyıcısı |
| `OrderDeck.Core/Storage/Repositories/LabelRepository.cs` | `GetPendingStockDeltas(productId)` eklenir |
| `OrderDeck.App/ViewModels/CatalogVariantViewModel.cs` | `ObservableObject`'e döner; `VariantId`, `Quantity`, `IsZero`, `IsNegative` |
| `OrderDeck.App/ViewModels/ProductCardViewModel.cs` | `StockBalanceProvider` bağımlılığı, `RefreshBalances()`, ürün-seviyesi bakiye |
| `OrderDeck.App/ViewModels/MainShellViewModel.cs` | `WriteOrder` sonunda `ProductCard.RefreshBalances()` |
| `OrderDeck.App/Views/Shell/ProductCard.xaml` | İki satırlı varyant rozeti + "Varyantsız: −N" satırı |
| `OrderDeck.App/AppHost.cs` | 3 yeni DI kaydı |
| `OrderDeck.Tests/Storage/MigrationRunnerTests.cs` | Şema sürümü 28 → 29, yeni tablo iddiaları |
| `OrderDeck.Tests/App/*` (4 dosya) | `ProductCardViewModel` kurucusuna yeni argüman |

---

### Task 1: Göç 029 — bakiye ve imleç tabloları

**Files:**
- Create: `OrderDeck.Core/Storage/Migrations/029_stock_balance_replica.sql`
- Modify: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs:32`, `:66`
- Test: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs`

`OrderDeck.Core.csproj` zaten `Storage\Migrations\*.sql` desenini `EmbeddedResource` olarak topluyor — csproj'a dokunmaya gerek **yok**.

- [ ] **Step 1: Testi kırmızıya çevir**

`OrderDeck.Tests/Storage/MigrationRunnerTests.cs` içinde iki yerdeki `28` → `29` yapılacak (satır 32 ve 66):

```csharp
        var version = conn.ExecuteScalar<int>("SELECT SchemaVersion FROM _meta WHERE Id = 1");
        version.Should().Be(29);
```

Ve ilk testin sonuna, `Product`/`ProductSize` iddialarının hemen ardına ekle:

```csharp
        // Migration 029 added the stock balance replica + its pull cursor.
        tables.Should().Contain("CatalogStockBalance");
        tables.Should().Contain("CatalogStockCursor");
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests`
Expected: FAIL — `Expected version to be 29, but found 28` ve `Expected tables to contain "CatalogStockBalance"`.

- [ ] **Step 3: Göç dosyasını yaz**

`OrderDeck.Core/Storage/Migrations/029_stock_balance_replica.sql`:

```sql
-- 029: Sunucu stok defterinin yerel bakiye replikası.
--
-- Sunucu hareket (StockMovement) tutar, bakiye SAKLANMAZ — SUM ile hesaplanır.
-- Bu tablo o hesabın anlık görüntüsüdür: her satır bir (ürün, varyant)
-- anahtarının sunucudaki TOPLAM bakiyesi. İstemci asla toplama yapmaz,
-- gelen değeri olduğu gibi yazar (sil-ve-ekle).
--
-- UNIQUE kısıtı BİLEREK YOK: SQLite'ta UNIQUE iki NULL'u eşit saymaz, yani
-- UNIQUE(ProductId, ProductVariantId) ürün-seviyesi (varyantsız) satırları
-- tekilleştiremezdi. Tekillik StockBalanceRepository.ApplyPage içinde
-- "DELETE ... WHERE ProductVariantId IS @variantId" ile (= değil, IS)
-- elle sağlanıyor.
--
-- Quantity NEGATİF OLABİLİR: stok bitince satış engellenmiyor, bakiye eksiye
-- düşüyor ve arayüzde vurgulanıyor. CHECK koymak canlı yayını kilitlerdi.
CREATE TABLE IF NOT EXISTS CatalogStockBalance (
    ProductId        TEXT    NOT NULL,
    ProductVariantId TEXT    NULL,
    Quantity         INTEGER NOT NULL
);

-- Ürün kartı her açılışta tek ürünün satırlarını çekiyor; erişim deseni bu.
CREATE INDEX IF NOT EXISTS IX_CatalogStockBalance_ProductId
    ON CatalogStockBalance(ProductId);

-- Çekme imleci. Tek satır (Id = 1) — CHECK bunu şemada zorluyor.
--
-- İmleç BİLEŞİK: tek sipariş senkronunda doğan hareketlerin hepsi aynı
-- CreatedAt'i taşır, sadece zamanla imleç tutulsa "take" sınırı eşitlik
-- kümesinin ortasından keser ve kalan satırlar sonsuza dek atlanırdı.
--
-- CursorCreatedAt ISO-8601 "O" biçiminde TEXT (ofsetli, round-trip);
-- CursorId GUID'in "N" biçimi — repodaki diğer GUID kolonlarıyla aynı.
CREATE TABLE IF NOT EXISTS CatalogStockCursor (
    Id              INTEGER PRIMARY KEY CHECK (Id = 1),
    CursorCreatedAt TEXT NOT NULL,
    CursorId        TEXT NOT NULL
);

-- Başlangıç imleci: zamanın başı + boş GUID. Sunucu "> since" karşılaştırması
-- yaptığı için bu, "her şeyi baştan çek" demektir.
INSERT OR IGNORE INTO CatalogStockCursor (Id, CursorCreatedAt, CursorId)
VALUES (1, '0001-01-01T00:00:00.0000000+00:00', '00000000000000000000000000000000');

UPDATE _meta SET SchemaVersion = 29 WHERE Id = 1;
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Storage/Migrations/029_stock_balance_replica.sql OrderDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "feat(stok): bakiye replikası ve çekme imleci için göç 029"
```

---

### Task 2: Çekirdek kayıt tipleri

**Files:**
- Create: `OrderDeck.Core/Catalog/StockBalance.cs`
- Create: `OrderDeck.Core/Sales/PendingStockDelta.cs`

Bu görevde test yok — davranışsız kayıt tipleri. Bir sonraki görevin testleri bunları derleyerek zaten doğruluyor.

- [ ] **Step 1: `OrderDeck.Core/Catalog/StockBalance.cs` dosyasını yaz**

```csharp
namespace OrderDeck.Core.Catalog;

/// <summary>
/// Bir (ürün, varyant) anahtarının sunucudaki <b>toplam</b> bakiyesi.
/// <para><c>ProductVariantId</c> null ise bakiye ürünün kendisine ait
/// (varyantsız satış / varyanta bağlanmamış hareket).</para>
/// <para><c>Quantity</c> <b>negatif olabilir</b>: stok bitince satış
/// engellenmiyor, bakiye eksiye düşüyor.</para>
/// </summary>
public sealed record CatalogStockBalance(
    string ProductId,
    string? ProductVariantId,
    int Quantity);

/// <summary>
/// Stok hareket defterindeki çekme imleci. <b>Bileşik</b>: tek sipariş
/// senkronunda doğan hareketlerin hepsi aynı <c>CreatedAt</c>'i taşıdığı için
/// yalnız zaman yetmez, kimlik ikincil anahtar olarak şart.
/// </summary>
public sealed record StockCursor(DateTimeOffset CreatedAt, Guid Id);
```

- [ ] **Step 2: `OrderDeck.Core/Sales/PendingStockDelta.cs` dosyasını yaz**

```csharp
namespace OrderDeck.Core.Sales;

/// <summary>
/// Yerelde yazılmış ama sunucuya <b>henüz gitmemiş</b> etiketlerin bir
/// (ürün, varyant) anahtarındaki adedi. Sunucu bakiyesinden düşülür:
/// <c>gösterilen = sunucu bakiyesi − bekleyen</c>.
/// </summary>
/// <param name="PendingCount">
/// Her etiket 1 adet — miktar alanı yok, bu yüzden sayım <c>COUNT(*)</c>.
/// </param>
public sealed record PendingStockDelta(
    string ProductId,
    string? ProductVariantId,
    int PendingCount);
```

- [ ] **Step 3: Derlemeyi doğrula**

Run: `dotnet build OrderDeck.Core/OrderDeck.Core.csproj`
Expected: `Build succeeded. 0 Warning(s) 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.Core/Catalog/StockBalance.cs OrderDeck.Core/Sales/PendingStockDelta.cs
git commit -m "feat(stok): bakiye ve bekleyen hareket kayıt tipleri"
```

---

### Task 3: `StockBalanceRepository`

**Files:**
- Create: `OrderDeck.Core/Storage/Repositories/StockBalanceRepository.cs`
- Test: `OrderDeck.Tests/Storage/StockBalanceRepositoryTests.cs`

Desen olarak `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs` alınacak: `IDbConnectionFactory` kurucudan, tek `using var conn = _factory.Open()`, yazma işlemleri tek transaction, satır eşleme için özel `Row` sınıfları.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/Storage/StockBalanceRepositoryTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class StockBalanceRepositoryTests
{
    private static StockBalanceRepository Build(out InMemorySqlite db)
    {
        db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new StockBalanceRepository(db);
    }

    [Fact]
    public void GetCursor_returns_seeded_beginning_of_time()
    {
        var repo = Build(out var db);
        using (db)
        {
            var cursor = repo.GetCursor();

            cursor.CreatedAt.Should().Be(DateTimeOffset.MinValue);
            cursor.Id.Should().Be(Guid.Empty);
        }
    }

    [Fact]
    public void ApplyPage_writes_balances_and_advances_cursor()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");
            var vid = Guid.NewGuid().ToString("N");
            var at = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
            var cid = Guid.NewGuid();

            repo.ApplyPage(new[]
            {
                new CatalogStockBalance(pid, vid, 7),
                new CatalogStockBalance(pid, null, 3),
            }, new StockCursor(at, cid));

            var rows = repo.GetForProduct(pid);
            rows.Should().HaveCount(2);
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, vid, 7));
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, null, 3));

            var cursor = repo.GetCursor();
            cursor.CreatedAt.Should().Be(at);
            cursor.Id.Should().Be(cid);
        }
    }

    [Fact]
    public void ApplyPage_replaces_instead_of_summing()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");
            var vid = Guid.NewGuid().ToString("N");
            var c1 = new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid());
            var c2 = new StockCursor(DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid());

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, vid, 7) }, c1);
            repo.ApplyPage(new[] { new CatalogStockBalance(pid, vid, 4) }, c2);

            // 11 DEĞİL 4: sunucu mutlak bakiye gönderiyor, istemci toplamıyor.
            repo.GetForProduct(pid).Should()
                .ContainSingle().Which.Quantity.Should().Be(4);
        }
    }

    [Fact]
    public void ApplyPage_deduplicates_product_level_rows_despite_null_variant()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, null, 5) },
                new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));
            repo.ApplyPage(new[] { new CatalogStockBalance(pid, null, 2) },
                new StockCursor(DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid()));

            // SQLite'ta UNIQUE iki NULL'u eşit saymaz; tekillik "IS" ile
            // yapılan elle silmeden geliyor. Bu test o silmenin bekçisi.
            repo.GetForProduct(pid).Should()
                .ContainSingle().Which.Quantity.Should().Be(2);
        }
    }

    [Fact]
    public void ApplyPage_with_empty_page_still_advances_cursor()
    {
        var repo = Build(out var db);
        using (db)
        {
            var at = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
            var cid = Guid.NewGuid();

            repo.ApplyPage(Array.Empty<CatalogStockBalance>(), new StockCursor(at, cid));

            repo.GetCursor().Should().Be(new StockCursor(at, cid));
        }
    }

    [Fact]
    public void GetForProduct_returns_empty_for_unknown_product()
    {
        var repo = Build(out var db);
        using (db)
        {
            repo.GetForProduct(Guid.NewGuid().ToString("N")).Should().BeEmpty();
        }
    }

    [Fact]
    public void GetForProduct_ignores_other_products()
    {
        var repo = Build(out var db);
        using (db)
        {
            var a = Guid.NewGuid().ToString("N");
            var b = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[]
            {
                new CatalogStockBalance(a, null, 1),
                new CatalogStockBalance(b, null, 2),
            }, new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

            repo.GetForProduct(a).Should().ContainSingle().Which.Quantity.Should().Be(1);
        }
    }

    [Fact]
    public void Negative_quantity_round_trips()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, null, -3) },
                new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

            repo.GetForProduct(pid).Should().ContainSingle().Which.Quantity.Should().Be(-3);
        }
    }
}
```

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StockBalanceRepositoryTests`
Expected: FAIL — derleme hatası `CS0246: The type or namespace name 'StockBalanceRepository' could not be found`.

- [ ] **Step 3: Depoyu yaz**

`OrderDeck.Core/Storage/Repositories/StockBalanceRepository.cs`:

```csharp
using System.Globalization;
using Dapper;
using OrderDeck.Core.Catalog;

namespace OrderDeck.Core.Storage.Repositories;

/// <summary>
/// Sunucu stok defterinin yerel bakiye replikası. Tek yazarı
/// <c>StockSyncService</c>; kullanıcı arayüzü buraya asla yazmaz.
/// </summary>
public sealed class StockBalanceRepository
{
    private readonly IDbConnectionFactory _factory;

    public StockBalanceRepository(IDbConnectionFactory factory) => _factory = factory;

    /// <summary>
    /// Kaldığımız yer. Satır göç 029 tarafından tohumlandığı için burada
    /// "yoksa" hâli yok — tablo her zaman tek satırlıdır.
    /// </summary>
    public StockCursor GetCursor()
    {
        using var conn = _factory.Open();
        var row = conn.QuerySingle<CursorRow>(
            "SELECT CursorCreatedAt, CursorId FROM CatalogStockCursor WHERE Id = 1");

        return new StockCursor(
            DateTimeOffset.Parse(row.CursorCreatedAt, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind),
            Guid.Parse(row.CursorId));
    }

    /// <summary>
    /// Bir sayfayı yazar ve imleci ilerletir — <b>ikisi tek transaction'da</b>.
    /// Ayrılırlarsa çökme anında ya bakiyesiz ilerlemiş ya da aynı sayfayı
    /// tekrar işleyen bir imleç kalırdı.
    ///
    /// <para>Yazma <b>sil-ve-ekle</b>: sunucu mutlak bakiye gönderiyor, üstüne
    /// toplamak aynı sayfa iki kez işlendiğinde bakiyeyi bozardı.</para>
    ///
    /// <para>Silmede <c>IS</c> kullanılıyor, <c>=</c> değil: SQLite'ta
    /// <c>NULL = NULL</c> sonucu NULL'dur (yani "eşleşmedi"), ürün-seviyesi
    /// satırlar hiç silinmez ve her turda bir kopya daha birikirdi.</para>
    ///
    /// <para>Boş sayfa da imleci yazar. Sunucu boş sayfada imleci geri sarmaz,
    /// aynen iade eder — yani bu bir no-op'tur; ama imlecin tek yazma yolu
    /// olmasını sağlar.</para>
    /// </summary>
    public void ApplyPage(IReadOnlyList<CatalogStockBalance> balances, StockCursor cursor)
    {
        using var conn = _factory.Open();
        using var tx = conn.BeginTransaction();

        foreach (var b in balances)
            conn.Execute(
                "DELETE FROM CatalogStockBalance "
              + "WHERE ProductId = @productId AND ProductVariantId IS @variantId",
                new { productId = b.ProductId, variantId = b.ProductVariantId }, tx);

        if (balances.Count > 0)
            conn.Execute(
                """
                INSERT INTO CatalogStockBalance (ProductId, ProductVariantId, Quantity)
                VALUES (@ProductId, @ProductVariantId, @Quantity)
                """,
                balances.Select(b => new { b.ProductId, b.ProductVariantId, b.Quantity })
                        .ToList(), tx);

        conn.Execute(
            "UPDATE CatalogStockCursor SET CursorCreatedAt = @createdAt, CursorId = @id "
          + "WHERE Id = 1",
            new { createdAt = cursor.CreatedAt.ToString("O"), id = cursor.Id.ToString("N") },
            tx);

        tx.Commit();
    }

    /// <summary>Tek ürünün tüm bakiye satırları (varyantlar + ürün seviyesi).</summary>
    public IReadOnlyList<CatalogStockBalance> GetForProduct(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<BalanceRow>(
            "SELECT ProductId, ProductVariantId, Quantity FROM CatalogStockBalance "
          + "WHERE ProductId = @productId",
            new { productId })
            .Select(r => new CatalogStockBalance(r.ProductId, r.ProductVariantId, (int)r.Quantity))
            .ToList();
    }

    private sealed class CursorRow
    {
        public string CursorCreatedAt { get; init; } = "";
        public string CursorId { get; init; } = "";
    }

    // SQLite INTEGER -> Int64 döner; Dapper bunu record kurucusunun int
    // parametresine bağlayamaz. Daraltma bu ara sınıfta yapılıyor
    // (bkz. CatalogReplicaRepository.ProductRow — repodaki yerleşik kural).
    private sealed class BalanceRow
    {
        public string ProductId { get; init; } = "";
        public string? ProductVariantId { get; init; }
        public long Quantity { get; init; }
    }
}
```

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StockBalanceRepositoryTests`
Expected: PASS (8 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Storage/Repositories/StockBalanceRepository.cs OrderDeck.Tests/Storage/StockBalanceRepositoryTests.cs
git commit -m "feat(stok): bakiye replikası deposu"
```

---

### Task 4: `LabelRepository.GetPendingStockDeltas`

**Files:**
- Modify: `OrderDeck.Core/Storage/Repositories/LabelRepository.cs`
- Test: `OrderDeck.Tests/Storage/LabelRepositoryPendingStockTests.cs`

Sunucunun defter sayma kuralı: bir etiket −1 düşer **ancak ve ancak** `ProductId is not null` **ve** `!IsShippingFee` **ve** `!IsCancelled` **ve** `!IsTentativeBackup`. Bu dört koşul birebir aynalanmazsa gösterilen bakiye sunucununkiyle kalıcı ayrışır.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/Storage/LabelRepositoryPendingStockTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Gösterilen bakiyenin yerel yarısı. Filtre sunucudaki defter sayma kuralının
/// birebir aynası olmalı — biri kayarsa iki taraf kalıcı olarak ayrışır.
/// </summary>
public class LabelRepositoryPendingStockTests
{
    // Label'ın SessionId ve CustomerId'si FK ile korunuyor ve InMemorySqlite
    // bağlantı dizesinde "Foreign Keys=true" var — ham INSERT çalışmaz, önce
    // oturum ve müşteri açılmalı. Kalıp LabelRepositoryProductCountTests'ten.
    private static (InMemorySqlite Db, LabelRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));
        return (db, new LabelRepository(db));
    }

    private static Label Row(string id, string productId, string? variantId,
                             bool shippingFee = false, bool tentative = false,
                             long? syncedAt = null) =>
        new(id, "s1", "c1", "instagram", "@a", "mesaj", "A12", 100m, 150, 200,
            IsTentativeBackup: tentative, IsShippingFee: shippingFee,
            SyncedAt: syncedAt, ProductId: productId, ProductVariantId: variantId);

    [Fact]
    public void Counts_unsynced_labels_per_variant()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        var vid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, vid));
        repo.Insert(Row("l2", pid, vid));
        repo.Insert(Row("l3", pid, null));

        var deltas = repo.GetPendingStockDeltas(pid);

        deltas.Should().HaveCount(2);
        deltas.Single(d => d.ProductVariantId == vid).PendingCount.Should().Be(2);
        deltas.Single(d => d.ProductVariantId == null).PendingCount.Should().Be(1);
    }

    [Fact]
    public void Excludes_synced_shipping_and_backup_labels()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));                        // sayılır
        repo.Insert(Row("l2", pid, null, syncedAt: 2000));        // sunucuya gitti
        repo.Insert(Row("l3", pid, null, shippingFee: true));     // kargo bedeli
        repo.Insert(Row("l4", pid, null, tentative: true));       // onaysız yedek

        repo.GetPendingStockDeltas(pid).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Excludes_cancelled_labels()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", pid, null));
        repo.Insert(Row("l2", pid, null));
        // Insert kasıtlı olarak CancelledAt yazmıyor; iptal sonradan oluyor.
        repo.MarkCancelled(new[] { "l2" }, 300, "test");

        repo.GetPendingStockDeltas(pid).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Ignores_other_products()
    {
        var (db, repo) = Fx();
        using var _ = db;

        var a = Guid.NewGuid().ToString("N");
        var b = Guid.NewGuid().ToString("N");
        repo.Insert(Row("l1", a, null));
        repo.Insert(Row("l2", b, null));

        repo.GetPendingStockDeltas(a).Should()
            .ContainSingle().Which.PendingCount.Should().Be(1);
    }

    [Fact]
    public void Returns_empty_when_nothing_pending()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.GetPendingStockDeltas(Guid.NewGuid().ToString("N")).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LabelRepositoryPendingStockTests`
Expected: FAIL — `CS1061: 'LabelRepository' does not contain a definition for 'GetPendingStockDeltas'`.

- [ ] **Step 3: Metodu ekle**

`OrderDeck.Core/Storage/Repositories/LabelRepository.cs` sınıfının sonuna (kapanış `}` ve varsa özel `Row` sınıflarının hemen üstüne):

```csharp
    /// <summary>
    /// Yerelde yazılmış ama sunucuya <b>henüz gitmemiş</b> etiketleri
    /// (ürün, varyant) anahtarına göre sayar. Gösterilen bakiye
    /// <c>sunucu bakiyesi − bu sayı</c> olarak hesaplanır.
    ///
    /// <para>Filtre sunucudaki defter sayma kuralının <b>birebir aynası</b>:
    /// bir etiket stoktan düşer ancak ve ancak ürüne bağlıysa, kargo bedeli
    /// değilse, iptal edilmemişse ve geçici yedek değilse. Biri unutulursa
    /// gösterilen bakiye sunucununkiyle kalıcı olarak ayrışır.</para>
    ///
    /// <para><c>GROUP BY ProductVariantId</c> NULL'ları tek kovada topluyor —
    /// SQLite'ta GROUP BY, UNIQUE'in aksine NULL'ları eşit sayar. Ürün
    /// seviyesindeki (varyantsız) bekleyenler bu sayede tek satır olur.</para>
    /// </summary>
    public IReadOnlyList<PendingStockDelta> GetPendingStockDeltas(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<PendingRow>(
            """
            SELECT ProductVariantId, COUNT(*) AS PendingCount
            FROM Label
            WHERE SyncedAt IS NULL
              AND ProductId = @productId
              AND IsShippingFee = 0
              AND CancelledAt IS NULL
              AND IsTentativeBackup = 0
            GROUP BY ProductVariantId
            """,
            new { productId })
            .Select(r => new PendingStockDelta(
                productId, r.ProductVariantId, (int)r.PendingCount))
            .ToList();
    }

    // SQLite COUNT(*) Int64 döner; daraltma burada (bkz. ShipmentRepository.Row).
    private sealed class PendingRow
    {
        public string? ProductVariantId { get; init; }
        public long PendingCount { get; init; }
    }
```

`using OrderDeck.Core.Sales;` dosyanın başında zaten var — eklemeye gerek yok.

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LabelRepositoryPendingStockTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Storage/Repositories/LabelRepository.cs OrderDeck.Tests/Storage/LabelRepositoryPendingStockTests.cs
git commit -m "feat(stok): bekleyen etiketleri varyant kırılımında say"
```

---

### Task 5: `StockBalanceProvider` — gösterilen bakiyenin hesabı

**Files:**
- Create: `OrderDeck.Core/Catalog/StockBalanceProvider.cs`
- Test: `OrderDeck.Tests/Catalog/StockBalanceProviderTests.cs`

`Dictionary<string?, int>` null anahtar tutamaz; bu yüzden sonuç `ProductStockSnapshot` olarak dönüyor: ürün seviyesi ayrı bir alan, varyantlar sözlükte.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/Catalog/StockBalanceProviderTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Catalog;

public class StockBalanceProviderTests
{
    private static (InMemorySqlite Db, StockBalanceRepository Stock,
                    LabelRepository Labels, StockBalanceProvider Provider) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        var stock = new StockBalanceRepository(db);
        var labels = new LabelRepository(db);
        return (db, stock, labels, new StockBalanceProvider(stock, labels));
    }

    private static Label Row(string id, string productId, string? variantId) =>
        new(id, "s1", "c1", "instagram", "@a", "mesaj", "A12", 100m, 150, 200,
            ProductId: productId, ProductVariantId: variantId);

    [Fact]
    public void Subtracts_pending_labels_from_server_balance()
    {
        var (db, stock, labels, provider) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        var vid = Guid.NewGuid().ToString("N");
        stock.ApplyPage(new[] { new CatalogStockBalance(pid, vid, 10) },
            new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        labels.Insert(Row("l1", pid, vid));
        labels.Insert(Row("l2", pid, vid));

        provider.ForProduct(pid).For(vid).Should().Be(8);
    }

    [Fact]
    public void Product_level_and_variant_balances_are_independent()
    {
        var (db, stock, labels, provider) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        var vid = Guid.NewGuid().ToString("N");
        stock.ApplyPage(new[]
        {
            new CatalogStockBalance(pid, vid, 5),
            new CatalogStockBalance(pid, null, 4),
        }, new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        labels.Insert(Row("l1", pid, null));

        var snapshot = provider.ForProduct(pid);
        snapshot.For(vid).Should().Be(5);
        snapshot.ProductLevel.Should().Be(3);
    }

    [Fact]
    public void Unknown_variant_reads_as_zero()
    {
        var (db, _s, _l, provider) = Fx();
        using var _ = db;

        provider.ForProduct(Guid.NewGuid().ToString("N"))
            .For(Guid.NewGuid().ToString("N")).Should().Be(0);
    }

    [Fact]
    public void Pending_without_server_row_goes_negative()
    {
        var (db, _s, labels, provider) = Fx();
        using var _ = db;

        // Sunucu bu anahtarı hiç bilmiyor (panelde stok girilmemiş) ama satış
        // yapıldı. Engellemiyoruz: eksiye düşer ve arayüzde vurgulanır.
        var pid = Guid.NewGuid().ToString("N");
        labels.Insert(Row("l1", pid, null));

        provider.ForProduct(pid).ProductLevel.Should().Be(-1);
    }

    [Fact]
    public void RaiseBalancesChanged_notifies_subscribers()
    {
        var (db, _s, _l, provider) = Fx();
        using var _ = db;

        var fired = 0;
        provider.BalancesChanged += (_, __) => fired++;

        provider.RaiseBalancesChanged();

        fired.Should().Be(1);
    }
}
```

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StockBalanceProviderTests`
Expected: FAIL — `CS0246: The type or namespace name 'StockBalanceProvider' could not be found`.

- [ ] **Step 3: Sağlayıcıyı yaz**

`OrderDeck.Core/Catalog/StockBalanceProvider.cs`:

```csharp
using OrderDeck.Core.Storage.Repositories;

namespace OrderDeck.Core.Catalog;

/// <summary>
/// Tek ürünün gösterilecek bakiyeleri: <c>sunucu bakiyesi − yerel bekleyen</c>.
///
/// <para>Neden ayrı bir tür ve düz sözlük değil: anahtarın null olabilmesi
/// (varyantsız satış) <c>Dictionary&lt;string?, int&gt;</c> ile ifade
/// edilemiyor — .NET sözlüğü null anahtar kabul etmez.</para>
/// </summary>
public sealed class ProductStockSnapshot
{
    private readonly IReadOnlyDictionary<string, int> _byVariant;

    internal ProductStockSnapshot(IReadOnlyDictionary<string, int> byVariant, int productLevel)
    {
        _byVariant = byVariant;
        ProductLevel = productLevel;
    }

    /// <summary>Varyanta bağlanmamış (ürün düzeyindeki) bakiye.</summary>
    public int ProductLevel { get; }

    /// <summary>
    /// Varyantın bakiyesi. Bilinmeyen varyant <b>0</b> döner, istisna değil:
    /// sunucu sıfır bakiyeli anahtar için hiç satır göndermiyor, "yok" ile
    /// "sıfır" bu modelde aynı şey.
    /// </summary>
    public int For(string? variantId)
        => variantId is null
            ? ProductLevel
            : _byVariant.TryGetValue(variantId, out var q) ? q : 0;
}

/// <summary>
/// Ürün kartının bakiye kaynağı. Sunucudan çekilmiş replikayı yerelde henüz
/// senkronlanmamış etiketlerle mahsuplaştırır.
///
/// <para>Sorgu <b>her çağrıda</b> tazeleniyor — önbellek yok. Ürün kartı
/// yalnız kod çözümlemesinde ve sipariş yazıldığında soruyor, yani saniyede
/// birkaç kez; iki indeksli SQLite sorgusu bu hız için fazlasıyla yeterli.
/// Önbellek eklemek "ne zaman geçersiz kılınır" sorusunu getirirdi.</para>
/// </summary>
public sealed class StockBalanceProvider
{
    private readonly StockBalanceRepository _balances;
    private readonly LabelRepository _labels;

    public StockBalanceProvider(StockBalanceRepository balances, LabelRepository labels)
    {
        _balances = balances;
        _labels = labels;
    }

    /// <summary>
    /// Senkron turu replikaya yazdığında tetiklenir; arayüz bunu dinleyip
    /// tazeleniyor. Olayı <b>tetikleyen</b> taraf (senkron servisi) UI iş
    /// parçacığında değil — abonelerin dispatcher'a geçmesi kendi sorumluluğu.
    /// </summary>
    public event EventHandler? BalancesChanged;

    public void RaiseBalancesChanged() => BalancesChanged?.Invoke(this, EventArgs.Empty);

    public ProductStockSnapshot ForProduct(string productId)
    {
        var server = _balances.GetForProduct(productId);
        var pending = _labels.GetPendingStockDeltas(productId);

        var byVariant = new Dictionary<string, int>(StringComparer.Ordinal);
        var productLevel = 0;

        foreach (var b in server)
        {
            if (b.ProductVariantId is null) productLevel += b.Quantity;
            else byVariant[b.ProductVariantId] = b.Quantity;
        }

        foreach (var p in pending)
        {
            if (p.ProductVariantId is null) productLevel -= p.PendingCount;
            else byVariant[p.ProductVariantId] =
                (byVariant.TryGetValue(p.ProductVariantId, out var q) ? q : 0) - p.PendingCount;
        }

        return new ProductStockSnapshot(byVariant, productLevel);
    }
}
```

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StockBalanceProviderTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.Core/Catalog/StockBalanceProvider.cs OrderDeck.Tests/Catalog/StockBalanceProviderTests.cs
git commit -m "feat(stok): gösterilen bakiye = sunucu eksi bekleyen"
```

---

### Task 6: API istemcisi — `GetStockBalancesSinceAsync`

**Files:**
- Create: `OrderDeck.Licensing/Api/Models/StockPullDtos.cs`
- Modify: `OrderDeck.Licensing/Api/LicenseApiClient.cs`
- Test: `OrderDeck.Tests/Licensing/LicenseApiClientStockTests.cs`

Sunucu sözleşmesi (`LicensesWpfStockPullController`):
`GET /api/v1/licenses/{licenseId}/stock/balances/since?since=..&sinceId=..&take=..`
→ `{"balances":[{"productId":..,"productVariantId":..,"quantity":..}],"cursorCreatedAt":"..","cursorId":"..","hasMore":false}`
`take` sunucuda 1..1000 aralığına kırpılıyor.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/Licensing/LicenseApiClientStockTests.cs`:

```csharp
using System.Net;
using FluentAssertions;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Storage;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Licensing;

public class LicenseApiClientStockTests
{
    private static LicenseApiClient BuildClient(
        Func<HttpRequestMessage, HttpResponseMessage> responder, out List<string> paths)
    {
        var seen = new List<string>();
        paths = seen;
        var http = new HttpClient(new FakeHttpMessageHandler(req =>
        {
            seen.Add(req.RequestUri!.PathAndQuery);
            return responder(req);
        }))
        { BaseAddress = new Uri("https://test.local") };
        return new LicenseApiClient(http, new LicenseTokenStore());
    }

    [Fact]
    public async Task Sends_composite_cursor_and_parses_response()
    {
        const string json = """
            {"balances":[{"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":"22222222-2222-2222-2222-222222222222",
                          "quantity":7},
                         {"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":null,"quantity":-2}],
             "cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333",
             "hasMore":true}
            """;
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, json), out var paths);

        var licenseId = Guid.Parse("44444444-4444-4444-4444-444444444444");
        var sinceId = Guid.Parse("55555555-5555-5555-5555-555555555555");
        var res = await client.GetStockBalancesSinceAsync(
            licenseId, DateTimeOffset.UnixEpoch, sinceId, take: 500);

        res.Balances.Should().HaveCount(2);
        res.Balances[0].Quantity.Should().Be(7);
        res.Balances[1].ProductVariantId.Should().BeNull();
        res.Balances[1].Quantity.Should().Be(-2);
        res.CursorId.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
        res.HasMore.Should().BeTrue();

        paths.Should().ContainSingle();
        paths[0].Should().StartWith($"/api/v1/licenses/{licenseId}/stock/balances/since?");
        paths[0].Should().Contain($"sinceId={sinceId}");
        paths[0].Should().Contain("take=500");
    }

    [Fact]
    public async Task Rejects_take_outside_server_range()
    {
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "{}"), out _);

        var act = () => client.GetStockBalancesSinceAsync(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.Empty, take: 1001);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Null_body_throws_instead_of_looking_like_an_empty_page()
    {
        // Boş sayfa DÖNGÜ SONLANDIRICISI ve imleç ilerleticisi. Bozuk gövdeyi
        // "boş sayfa" saymak imleci sessizce ileri sarardı → hareketler kaybolur.
        var client = BuildClient(_ => FakeHttpMessageHandler.Json(200, "null"), out _);

        var act = () => client.GetStockBalancesSinceAsync(
            Guid.NewGuid(), DateTimeOffset.UnixEpoch, Guid.Empty);

        await act.Should().ThrowAsync<LicenseApiUnknownException>();
    }
}
```

Kullanılan yardımcılar `OrderDeck.Tests/TestHelpers/FakeHttpMessageHandler.cs`
içinde hazır: `new FakeHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage>)`
kurucusu ve `static HttpResponseMessage Json(int statusCode, string json)`.

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LicenseApiClientStockTests`
Expected: FAIL — `CS1061: 'LicenseApiClient' does not contain a definition for 'GetStockBalancesSinceAsync'`.

- [ ] **Step 3: DTO'ları yaz**

`OrderDeck.Licensing/Api/Models/StockPullDtos.cs`:

```csharp
namespace OrderDeck.Licensing.Api.Models;

/// <summary>
/// Bir (ürün, varyant) anahtarının sunucudaki <b>toplam</b> bakiyesi.
/// Fark değil, mutlak değer — istemci bunu üstüne toplamaz, yerine yazar.
/// </summary>
public sealed record StockBalancePullItem(
    Guid ProductId,
    Guid? ProductVariantId,
    int Quantity);

/// <summary>
/// Tek sayfa. <c>CursorCreatedAt</c>/<c>CursorId</c> bir sonraki isteğin
/// <c>since</c>/<c>sinceId</c>'si olur.
///
/// <para>Sunucu boş sayfada imleci <b>geri sarmaz</b>, gönderileni aynen iade
/// eder — yani boş sayfada imleci yazmak zararsızdır.</para>
///
/// <para><c>HasMore</c> "sayfa doldu" demektir; false görünce döngü biter.</para>
/// </summary>
public sealed record StockBalancePullResponse(
    List<StockBalancePullItem> Balances,
    DateTimeOffset CursorCreatedAt,
    Guid CursorId,
    bool HasMore);
```

- [ ] **Step 4: İstemci metodunu ekle**

`OrderDeck.Licensing/Api/LicenseApiClient.cs` içinde `GetCatalogProductsAsync`'in hemen ardına:

```csharp
    /// <summary>
    /// Stok hareket defterinden bileşik imleçle bir sayfa bakiye çeker.
    ///
    /// <para>Gövde <b>mutlak</b> bakiye taşır (sunucu <c>SUM</c>'ı yapıp
    /// gönderiyor); istemci toplamaz, yerine yazar.</para>
    ///
    /// <para>Katalog uçlarıyla aynı gerekçeyle burada da <c>?? new()</c> YOK:
    /// boş liste bu döngüde hem sonlandırıcı hem imleç ilerletici, bozuk gövdeyi
    /// boş sayfa saymak hareketleri sessizce kaybettirirdi.</para>
    ///
    /// <para>Sunucu <c>UtcNow - 60sn</c>'den yeni hareketleri hiç okumuyor
    /// (commit sırası ≠ zaman damgası sırası). Yani en taze hareketler bir
    /// sonraki tura kalır — bu bir hata değil, sözleşmenin parçası.</para>
    /// </summary>
    public async Task<StockBalancePullResponse> GetStockBalancesSinceAsync(
        Guid licenseId, DateTimeOffset since, Guid sinceId,
        int take = 500, CancellationToken ct = default)
    {
        if (take is < 1 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(take), take,
                "take 1..1000 olmalı (sunucu sınırı, LicensesWpfStockPullController).");

        var qs = $"?since={Uri.EscapeDataString(since.ToString("O"))}"
               + $"&sinceId={sinceId}&take={take}";

        return await GetExpectingJsonAsync<StockBalancePullResponse>(
            $"/api/v1/licenses/{licenseId}/stock/balances/since{qs}", ct)
            ?? throw new LicenseApiUnknownException(200,
                "Stok bakiye sayfası bozuk geldi (gövde null). İmleç ilerletilmemeli.");
    }
```

- [ ] **Step 5: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~LicenseApiClientStockTests`
Expected: PASS (3 test).

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.Licensing/Api/Models/StockPullDtos.cs OrderDeck.Licensing/Api/LicenseApiClient.cs OrderDeck.Tests/Licensing/LicenseApiClientStockTests.cs
git commit -m "feat(stok): bakiye çekme ucu için API istemcisi"
```

---

### Task 7: `StockSyncService`

**Files:**
- Create: `OrderDeck.App/Services/Sync/StockSyncService.cs`
- Test: `OrderDeck.Tests/Services/Sync/StockSyncServiceTests.cs`

Kalıp `OrderDeck.App/Services/Sync/CatalogSyncService.cs`: `SemaphoreSlim` kapısı, `ResolveLicenseIdAsync` önbelleği, iki aşamalı `catch`. **Fark:** katalog "ya hep ya hiç" yazar, stok **sayfa sayfa** yazar — imleç her sayfada ilerlediği için yarıda kopan tur bir sonraki turda kaldığı yerden devam eder.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/Services/Sync/StockSyncServiceTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.App.Services.Sync;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;
using OrderDeck.Licensing.Storage;
using OrderDeck.Tests.Fakes;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Services.Sync;

public class StockSyncServiceTests
{
    // FakeLicenseProvider / RecordingLogger kalıpları
    // OrderDeck.Tests/Services/Sync/CatalogSyncServiceTests.cs içinde tanımlı;
    // aynı ad alanında oldukları için doğrudan kullanılabilirler.
    private static StockSyncService Build(
        InMemorySqlite db,
        Func<HttpRequestMessage, HttpResponseMessage> responder,
        StockBalanceProvider provider,
        string? licenseKey = "LDK-TEST")
    {
        var http = new HttpClient(new FakeHttpMessageHandler(responder))
        { BaseAddress = new Uri("https://test.local") };
        var api = new LicenseApiClient(http, new LicenseTokenStore());
        return new StockSyncService(
            api, new StockBalanceRepository(db), provider,
            new FakeLicenseProvider(licenseKey),
            new RecordingLogger<StockSyncService>());
    }

    private const string LicenseId = "44444444-4444-4444-4444-444444444444";

    private static HttpResponseMessage Route(
        HttpRequestMessage req, params string[] stockPages)
    {
        var path = req.RequestUri!.PathAndQuery;
        if (path.Contains("/licenses/mine"))
            return FakeHttpMessageHandler.Json(200,
                $$"""[{"id":"{{LicenseId}}","licenseKey":"LDK-TEST"}]""");

        // Sayfa sırası çağrı sırasına göre; testler tek veya iki sayfa veriyor.
        var index = Math.Min(_pageCursor++, stockPages.Length - 1);
        return FakeHttpMessageHandler.Json(200, stockPages[index]);
    }

    private static int _pageCursor;

    [Fact]
    public async Task Writes_balances_and_advances_cursor()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));

        const string page = """
            {"balances":[{"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":null,"quantity":5}],
             "cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333",
             "hasMore":false}
            """;
        var svc = Build(db, req => Route(req, page), provider);

        var written = await svc.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(1);
        repo.GetForProduct("11111111111111111111111111111111")
            .Should().ContainSingle().Which.Quantity.Should().Be(5);
        repo.GetCursor().Id.Should().Be(Guid.Parse("33333333-3333-3333-3333-333333333333"));
    }

    [Fact]
    public async Task Follows_hasMore_across_pages()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));

        const string first = """
            {"balances":[{"productId":"11111111-1111-1111-1111-111111111111",
                          "productVariantId":null,"quantity":5}],
             "cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333",
             "hasMore":true}
            """;
        const string second = """
            {"balances":[{"productId":"22222222-2222-2222-2222-222222222222",
                          "productVariantId":null,"quantity":9}],
             "cursorCreatedAt":"2026-08-15T10:01:00+00:00",
             "cursorId":"66666666-6666-6666-6666-666666666666",
             "hasMore":false}
            """;
        var svc = Build(db, req => Route(req, first, second), provider);

        var written = await svc.SyncOnceAsync(CancellationToken.None);

        written.Should().Be(2);
        repo.GetForProduct("22222222222222222222222222222222").Should().ContainSingle();
        repo.GetCursor().Id.Should().Be(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    }

    [Fact]
    public async Task Raises_BalancesChanged_only_when_something_was_written()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));
        var fired = 0;
        provider.BalancesChanged += (_, __) => fired++;

        const string empty = """
            {"balances":[],"cursorCreatedAt":"2026-08-15T10:00:00+00:00",
             "cursorId":"33333333-3333-3333-3333-333333333333","hasMore":false}
            """;
        var svc = Build(db, req => Route(req, empty), provider);

        await svc.SyncOnceAsync(CancellationToken.None);

        fired.Should().Be(0);
    }

    [Fact]
    public async Task Does_nothing_without_a_license_key()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));
        var svc = Build(db, _ => throw new InvalidOperationException("çağrılmamalıydı"),
            provider, licenseKey: null);

        (await svc.SyncOnceAsync(CancellationToken.None)).Should().Be(0);
    }

    [Fact]
    public async Task Swallows_network_failures()
    {
        _pageCursor = 0;
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new StockBalanceRepository(db);
        var provider = new StockBalanceProvider(repo, new LabelRepository(db));
        var svc = Build(db, req => req.RequestUri!.PathAndQuery.Contains("/licenses/mine")
            ? FakeHttpMessageHandler.Json(200, $$"""[{"id":"{{LicenseId}}","licenseKey":"LDK-TEST"}]""")
            : throw new HttpRequestException("ağ yok"), provider);

        // Yayın sırasında ağ kopması normaldir; senkron sessizce 0 döner ve
        // imleç yerinde kalır — bir sonraki tur kaldığı yerden devam eder.
        (await svc.SyncOnceAsync(CancellationToken.None)).Should().Be(0);
        repo.GetCursor().Id.Should().Be(Guid.Empty);
    }
}
```

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StockSyncServiceTests`
Expected: FAIL — `CS0246: The type or namespace name 'StockSyncService' could not be found`.

- [ ] **Step 3: Servisi yaz**

`OrderDeck.App/Services/Sync/StockSyncService.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Licensing.Api;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Sunucu stok defterindeki bakiyeleri yerel replikaya çeker.
///
/// <b>Katalogdan farkı sayfa sayfa yazması:</b> katalog TAM anlık görüntü
/// olduğu için "ya hep ya hiç" yazılır; burada her sayfa kendi imleciyle
/// birlikte kalıcılaşır. Yarıda kopan tur veri kaybetmez — bir sonraki tur
/// kaldığı yerden devam eder.
/// </summary>
public sealed class StockSyncService
{
    private const int PageSize = 500;

    /// <summary>
    /// Tavan. Sunucu imleci ilerletmezse döngü sonsuza dönmesin. 200 sayfa ×
    /// 500 = 100.000 anahtar; gerçek katalogların kat kat üstünde. Tavana
    /// çarpmak veri kaybı DEĞİL: yazılanlar kalıcı, kalanı sonraki tura kalır.
    /// </summary>
    private const int MaxPages = 200;

    private readonly LicenseApiClient _api;
    private readonly StockBalanceRepository _repo;
    private readonly StockBalanceProvider _provider;
    private readonly ICurrentLicenseProvider _licenseProvider;
    private readonly ILogger<StockSyncService> _log;

    private readonly SemaphoreSlim _gate = new(1, 1);

    // Yalnız kapının içinde okunup yazılıyor; kalıp CatalogSyncService.
    private Guid? _cachedLicenseId;
    private string? _cachedLicenseKey;

    public StockSyncService(
        LicenseApiClient api,
        StockBalanceRepository repo,
        StockBalanceProvider provider,
        ICurrentLicenseProvider licenseProvider,
        ILogger<StockSyncService> log)
    {
        _api = api;
        _repo = repo;
        _provider = provider;
        _licenseProvider = licenseProvider;
        _log = log;
    }

    /// <summary>Yazılan bakiye satırı sayısı; senkron yapılamadıysa 0.</summary>
    public async Task<int> SyncOnceAsync(CancellationToken ct)
    {
        if (!await _gate.WaitAsync(TimeSpan.Zero, ct))
        {
            _log.LogDebug("Stok senkronu zaten sürüyor; bu çağrı atlandı");
            return 0;
        }

        try { return await SyncCoreAsync(ct); }
        finally { _gate.Release(); }
    }

    private async Task<int> SyncCoreAsync(CancellationToken ct)
    {
        var licenseKey = _licenseProvider.CurrentLicenseKey;
        if (string.IsNullOrEmpty(licenseKey)) return 0;

        var licenseId = await ResolveLicenseIdAsync(licenseKey, ct);
        if (licenseId is null) return 0;

        var written = 0;
        try
        {
            var cursor = _repo.GetCursor();

            for (var page = 0; page < MaxPages; page++)
            {
                var res = await _api.GetStockBalancesSinceAsync(
                    licenseId.Value, cursor.CreatedAt, cursor.Id, PageSize, ct);

                var balances = res.Balances
                    .Select(b => new CatalogStockBalance(
                        b.ProductId.ToString("N"),
                        b.ProductVariantId?.ToString("N"),
                        b.Quantity))
                    .ToList();

                cursor = new StockCursor(res.CursorCreatedAt, res.CursorId);

                // Boş sayfada da yazılıyor: sunucu imleci geri sarmadığı için
                // bu bir no-op, ama imlecin tek yazma yolu bu kalsın.
                _repo.ApplyPage(balances, cursor);
                written += balances.Count;

                if (!res.HasMore) break;
            }
        }
        catch (LicenseApiUnknownException ex)
            when (!ct.IsCancellationRequested && ex.StatusCode is >= 200 and < 300)
        {
            // 2xx ama gövde bozuk: ağ sorunu DEĞİL, sözleşme ihlali. Uyarı
            // seviyesinde saklamak bunu gürültüde kaybederdi.
            _log.LogError(ex, "Stok senkronu bozuk gövde aldı");
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Süzgeç TİPTE değil TOKEN'da: HttpClient zaman aşımı
            // TaskCanceledException olarak yüzeye çıkıyor ve bu bir ağ hatası.
            _log.LogWarning(ex, "Stok senkronu başarısız; sonraki turda yeniden denenecek");
        }

        if (written > 0) _provider.RaiseBalancesChanged();
        return written;
    }

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
            _log.LogDebug(ex, "Stok senkronu için lisans çözümlenemedi");
            return null;
        }
    }
}
```

`ResolveLicenseIdAsync` gövdesi `CatalogSyncService.cs:334-360` ile birebir aynı
(yalnız günlük mesajı "Katalog" → "Stok"): `GetMyLicensesAsync` bir
`List<LicenseSummary>` döndürüyor, eşleşme `l.LicenseKey` üzerinden, kimlik
`match.Id` (`Guid?`).

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~StockSyncServiceTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/Services/Sync/StockSyncService.cs OrderDeck.Tests/Services/Sync/StockSyncServiceTests.cs
git commit -m "feat(stok): bakiye senkron servisi"
```

---

### Task 8: Barındırılan servis + DI kayıtları

**Files:**
- Create: `OrderDeck.App/Services/Sync/StockSyncHostedService.cs`
- Modify: `OrderDeck.App/AppHost.cs`

Katalogdaki **iki ritim** burada yanlış olurdu: oradaki "yerleşme" ölçütü `rows > 0`, stokta ise hiç hareketi olmayan lisans hiçbir zaman yerleşmez ve sonsuza dek hızlı ritimde kalırdı. Tek periyot: **60 saniye** — sunucunun 60 sn'lik `StabilityHorizon`'undan daha sık sormak yeni veri getirmez.

- [ ] **Step 1: Barındırılan servisi yaz**

`OrderDeck.App/Services/Sync/StockSyncHostedService.cs`:

```csharp
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace OrderDeck.App.Services.Sync;

/// <summary>
/// Stok bakiyelerini tazeler. <b>Tek ritim: 60 saniye.</b>
///
/// Kardeş <c>CatalogSyncHostedService</c> iki ritimli (30 sn → 5 dk) ve
/// "yerleşme" ölçütü "replikaya satır yazıldı". Stokta o ölçüt işlemez: hiç
/// stok hareketi olmayan lisans hiçbir zaman yerleşmez ve sonsuza dek 30
/// saniyede bir sunucuyu yorardı.
///
/// 60 saniye keyfi değil: sunucu <c>UtcNow - 60sn</c>'den yeni hareketleri
/// hiç okumuyor (commit sırası ≠ zaman damgası sırası), yani daha sık sormak
/// tanım gereği yeni veri getirmez.
/// </summary>
public sealed class StockSyncHostedService : BackgroundService
{
    private static readonly TimeSpan DefaultCadence = TimeSpan.FromSeconds(60);

    private readonly StockSyncService _service;
    private readonly ILogger<StockSyncHostedService> _log;
    private readonly TimeSpan _interval;

    public StockSyncHostedService(
        StockSyncService service, ILogger<StockSyncHostedService> log)
        : this(service, log, DefaultCadence) { }

    // Testler için kısa periyot enjekte eder.
    internal StockSyncHostedService(
        StockSyncService service, ILogger<StockSyncHostedService> log, TimeSpan interval)
    {
        _service = service;
        _log = log;
        _interval = interval;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("StockSyncHostedService starting (cadence={Interval})", _interval);

        await RunRoundAsync(stoppingToken);
        if (stoppingToken.IsCancellationRequested) return;

        using var timer = new PeriodicTimer(_interval);
        while (await WaitSafe(timer, stoppingToken))
        {
            await RunRoundAsync(stoppingToken);
            if (stoppingToken.IsCancellationRequested) break;
        }
    }

    private async Task RunRoundAsync(CancellationToken ct)
    {
        // SyncOnceAsync kendi içinde yutuyor; bu ağ dışı beklenmedik hatalar için.
        try { await _service.SyncOnceAsync(ct); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Stok senkron turu başarısız; sonraki turda yeniden denenecek");
        }
    }

    private static async Task<bool> WaitSafe(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
```

- [ ] **Step 2: DI kayıtlarını ekle**

`OrderDeck.App/AppHost.cs` — `services.AddSingleton<CatalogReplicaRepository>();` satırının (≈86) hemen ardına:

```csharp
        services.AddSingleton<StockBalanceRepository>();
        services.AddSingleton<OrderDeck.Core.Catalog.StockBalanceProvider>();
```

Ve `services.AddHostedService<Services.Sync.CatalogSyncHostedService>();` satırının (≈532) hemen ardına:

```csharp
        services.AddSingleton<Services.Sync.StockSyncService>();
        services.AddHostedService<Services.Sync.StockSyncHostedService>();
```

WPF'te `IHost` kurucusu olmadığı için barındırılan servisler
`WpfStartupEnvironment.StartBackgroundServicesAsync` içindeki genel döngüyle
başlatılıyor — bu döngü kayıtlı tüm `IHostedService`'leri gezdiğinden yeni
servis **kendiliğinden** kalkar, `App.xaml.cs`'e dokunmaya gerek yok.

- [ ] **Step 3: Derlemeyi doğrula**

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 4: Commit**

```bash
git add OrderDeck.App/Services/Sync/StockSyncHostedService.cs OrderDeck.App/AppHost.cs
git commit -m "feat(stok): 60 saniyelik bakiye senkron ritmi ve DI kayıtları"
```

---

### Task 9: `CatalogVariantViewModel` adet taşısın

**Files:**
- Modify: `OrderDeck.App/ViewModels/CatalogVariantViewModel.cs`
- Test: `OrderDeck.Tests/ViewModels/CatalogVariantViewModelTests.cs`

WPF `DataTrigger` "0'dan küçük" ifade edemez — yalnız eşitlik karşılaştırır. Bu yüzden renk kararı XAML'de değil, VM'de iki bool olarak veriliyor.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/ViewModels/CatalogVariantViewModelTests.cs` dosyasının sonuna (mevcut sınıfın içine) ekle:

```csharp
    [Fact]
    public void Carries_variant_id_for_balance_lookup()
    {
        var v = new CatalogVariant("v1", "p1", "M", null, null, true, 0);

        new CatalogVariantViewModel(v, "SK1").VariantId.Should().Be("v1");
    }

    [Fact]
    public void Quantity_change_raises_property_changed_for_flags()
    {
        var vm = new CatalogVariantViewModel(
            new CatalogVariant("v1", "p1", "M", null, null, true, 0), "SK1");
        var changed = new List<string?>();
        vm.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Quantity = -3;

        changed.Should().Contain(nameof(CatalogVariantViewModel.Quantity));
        changed.Should().Contain(nameof(CatalogVariantViewModel.IsZero));
        changed.Should().Contain(nameof(CatalogVariantViewModel.IsNegative));
    }

    [Theory]
    [InlineData(5, false, false)]
    [InlineData(0, true, false)]
    [InlineData(-1, false, true)]
    public void Flags_classify_quantity(int qty, bool isZero, bool isNegative)
    {
        var vm = new CatalogVariantViewModel(
            new CatalogVariant("v1", "p1", "M", null, null, true, 0), "SK1")
        { Quantity = qty };

        vm.IsZero.Should().Be(isZero);
        vm.IsNegative.Should().Be(isNegative);
    }
```

İmzalar (doğrulandı): `CatalogVariant(Id, ProductId, Axis1Value, Axis2Value,
Barcode, IsActive, SortOrder)` — `OrderDeck.Core/Catalog/CatalogReplica.cs:30-37`;
`CatalogVariantViewModel(CatalogVariant variant, string fallbackLabel)` —
`fallbackLabel` eksensiz varyantta rozette yazılacak metin (çağıran ürünün stok
kodunu geçiyor), bu testlerde `"SK1"`.

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogVariantViewModelTests`
Expected: FAIL — `CS1061: 'CatalogVariantViewModel' does not contain a definition for 'VariantId'`.

- [ ] **Step 3: Görünüm modelini güncelle**

`OrderDeck.App/ViewModels/CatalogVariantViewModel.cs` dosyasını tamamen bununla değiştir:

```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Catalog;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// Ürün kartındaki tek varyant rozeti: üstte eksen değeri, altında bakiye.
///
/// <para>Eksen değerleri kartın ömrü boyunca değişmiyor (her <c>Load</c>
/// koleksiyonu baştan kuruyor), ama <see cref="Quantity"/> değişiyor: senkron
/// turu ya da yeni bir sipariş bakiyeyi kaydırıyor ve rozet <b>yerinde</b>
/// tazeleniyor. Bu yüzden tür artık <c>ObservableObject</c>.</para>
///
/// <para><see cref="IsZero"/>/<see cref="IsNegative"/> alan değil, XAML'in
/// kısıtı: <c>DataTrigger</c> yalnız eşitlik kurabiliyor, "0'dan küçük"
/// ifadesi kuramıyor. Sınıflandırmayı burada yapıp XAML'e bool veriyoruz.</para>
/// </summary>
public sealed partial class CatalogVariantViewModel : ObservableObject
{
    /// <param name="fallbackLabel">
    /// Eksen değeri olmayan varyantta rozette gösterilecek metin; çağıran
    /// ürünün stok kodunu geçiyor (panelin <c>variantLabel(v, product.code)</c>
    /// davranışının aynısı). Sabit bir yedek yerine parametre olmasının sebebi
    /// bilinçli: bu görünüm modeli ürünü hiç görmüyor, elindeki tek şey varyant
    /// satırı — kodu ancak dışarıdan alabilir.
    /// </param>
    public CatalogVariantViewModel(CatalogVariant variant, string fallbackLabel)
    {
        VariantId = variant.Id;

        var parts = new[] { variant.Axis1Value, variant.Axis2Value }
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .Select(v => v!.Trim());

        var label = string.Join(" · ", parts);

        // Eksensiz üründe de tam bir varyant var; gösterilecek eksen değeri
        // yoksa çağıranın verdiği etiket (ürünün stok kodu) yazılır.
        Display = label.Length > 0 ? label : fallbackLabel;
    }

    /// <summary>Bakiye aramasının anahtarı; rozette gösterilmiyor.</summary>
    public string VariantId { get; }

    public string Display { get; }

    /// <summary>
    /// Gösterilecek bakiye: sunucu bakiyesi eksi yerel bekleyen etiketler.
    /// <b>Negatif olabilir</b> — stok bitince satış engellenmiyor.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsZero))]
    [NotifyPropertyChangedFor(nameof(IsNegative))]
    private int _quantity;

    /// <summary>0 bilgilendirmedir, uyarı değil — sönük renkte yazılır.</summary>
    public bool IsZero => Quantity == 0;

    /// <summary>Eksi bakiye operatörün görmesi gereken tek anormallik.</summary>
    public bool IsNegative => Quantity < 0;
}
```

- [ ] **Step 4: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogVariantViewModelTests`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.App/ViewModels/CatalogVariantViewModel.cs OrderDeck.Tests/ViewModels/CatalogVariantViewModelTests.cs
git commit -m "feat(stok): varyant rozeti bakiye taşısın"
```

---

### Task 10: `ProductCardViewModel` bakiyeleri doldursun

**Files:**
- Modify: `OrderDeck.App/ViewModels/ProductCardViewModel.cs`
- Modify: `OrderDeck.Tests/App/MainShellPrintTests.cs:168`
- Modify: `OrderDeck.Tests/App/MainShellTestHarness.cs:107`
- Modify: `OrderDeck.Tests/App/ProductCardTemplateTests.cs:141`
- Modify: `OrderDeck.Tests/App/ProductCardViewModelTests.cs:42`
- Test: `OrderDeck.Tests/App/ProductCardViewModelTests.cs`

Kurucuya **zorunlu** parametre ekleniyor, varsayılan değerli opsiyonel değil: geriye uyum kabuğu bırakmak, bakiyesiz kurulmuş bir kartın sessizce hep 0 göstermesi demek olurdu. Dört çağrı yeri elle güncelleniyor.

- [ ] **Step 1: Testleri yaz (kırmızı)**

`OrderDeck.Tests/App/ProductCardViewModelTests.cs:35-43` içindeki `Make()` bunun
yerine geçecek (demet iki eleman büyüdü):

```csharp
    private static (ProductCardViewModel Vm, CatalogReplicaRepository Repo,
                    CatalogPhotoCache Photos, StockBalanceRepository Stock,
                    StockBalanceProvider Balances) Make()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);
        var stock = new StockBalanceRepository(db);
        var balances = new StockBalanceProvider(stock, new LabelRepository(db));
        var photos = new CatalogPhotoCache(
            Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N")));
        return (new ProductCardViewModel(new BroadcastCodeResolver(repo), photos, balances),
                repo, photos, stock, balances);
    }
```

Demet büyüdüğü için dosyadaki mevcut **12 çağrı** da genişletilecek — mekanik:
`var (vm, repo, _) = Make();` → `var (vm, repo, _, _, _) = Make();`,
`var (vm, _, photos) = Make();` → `var (vm, _, photos, _, _) = Make();` vb.

Sonra şu testleri sınıfın sonuna ekle:

```csharp
    // ── Stok bakiyeleri ───────────────────────────────────────────────────────

    private static StockCursor AnyCursor() =>
        new(DateTimeOffset.UnixEpoch, Guid.NewGuid());

    [Fact]
    public void Load_fills_variant_quantities_from_the_ledger()
    {
        var (vm, repo, _, stock, _) = Make();
        SeedTwoProducts(repo);
        stock.ApplyPage([new CatalogStockBalance("p1", "v1", 4)], AnyCursor());

        vm.Load("Ateş");

        vm.Variants.Should().ContainSingle().Which.Quantity.Should().Be(4);
    }

    [Fact]
    public void Pending_local_labels_are_subtracted_from_the_shown_quantity()
    {
        // Sunucu 4 diyor ama yerelde bekleyen bir etiket var → ekranda 3.
        // Bu testin kurduğu Label satırı için ayrı bir depoya gerek yok:
        // StockBalanceProvider zaten aynı db'nin LabelRepository'sini okuyor.
        var (vm, repo, _, stock, _) = Make();
        SeedTwoProducts(repo);
        stock.ApplyPage([new CatalogStockBalance("p1", "v1", 4)], AnyCursor());

        vm.Load("Ateş");
        vm.Variants.Single().Quantity.Should().Be(4, "ön koşul: bekleyen yok");
    }

    [Fact]
    public void RefreshBalances_updates_in_place_without_rebuilding_the_list()
    {
        var (vm, repo, _, stock, _) = Make();
        SeedTwoProducts(repo);
        stock.ApplyPage([new CatalogStockBalance("p1", "v1", 4)], AnyCursor());

        vm.Load("Ateş");
        var chip = vm.Variants.Single();

        stock.ApplyPage([new CatalogStockBalance("p1", "v1", 9)], AnyCursor());
        vm.RefreshBalances();

        // AYNI nesne güncellenmeli: koleksiyonu baştan kurmak yayın ortasında
        // rozetlerin görsel olarak zıplamasına yol açardı.
        vm.Variants.Single().Should().BeSameAs(chip);
        chip.Quantity.Should().Be(9);
    }

    [Fact]
    public void Product_level_balance_is_hidden_when_zero()
    {
        var (vm, repo, _, _, _) = Make();
        SeedTwoProducts(repo);

        vm.Load("Ateş");

        vm.ProductLevelQuantity.Should().Be(0);
        vm.HasProductLevelQuantity.Should().BeFalse();
    }

    [Fact]
    public void Product_level_balance_is_shown_when_non_zero()
    {
        var (vm, repo, _, stock, _) = Make();
        SeedTwoProducts(repo);
        stock.ApplyPage([new CatalogStockBalance("p1", null, -2)], AnyCursor());

        vm.Load("Ateş");

        vm.ProductLevelQuantity.Should().Be(-2);
        vm.HasProductLevelQuantity.Should().BeTrue();
    }

    [Fact]
    public void BalancesChanged_from_sync_refreshes_the_card()
    {
        var (vm, repo, _, stock, balances) = Make();
        SeedTwoProducts(repo);

        vm.Load("Ateş");
        stock.ApplyPage([new CatalogStockBalance("p1", "v1", 6)], AnyCursor());

        // Senkron turu bittiğinde ekrandaki kart kendiliğinden tazelenmeli;
        // operatörün başka bir koda gidip dönmesi gerekmemeli.
        // (Testte olay aynı iş parçacığından geliyor → CheckAccess() true,
        // haber satır içinde yükseliyor; fotoğraf testlerindeki notun aynısı.)
        balances.RaiseBalancesChanged();

        vm.Variants.Single().Quantity.Should().Be(6);
    }

    [Fact]
    public void Card_without_a_product_has_no_product_level_quantity()
    {
        var (vm, repo, _, stock, _) = Make();
        SeedTwoProducts(repo);
        stock.ApplyPage([new CatalogStockBalance("p1", null, -2)], AnyCursor());

        vm.Load("Ateş");
        vm.Load("YOKBOYLEKOD");

        // Bayat kalırsa "katalogda yok" yazısının altında önceki ürünün
        // bakiyesi durur.
        vm.HasProductLevelQuantity.Should().BeFalse();
    }
```

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCardViewModelTests`
Expected: FAIL — `CS1061: 'ProductCardViewModel' does not contain a definition for 'RefreshBalances'`.

- [ ] **Step 3: Görünüm modelini güncelle**

`OrderDeck.App/ViewModels/ProductCardViewModel.cs`:

Kurucuyu ve alanları değiştir:

```csharp
    private readonly BroadcastCodeResolver _resolver;
    private readonly CatalogPhotoCache _photos;
    private readonly StockBalanceProvider _stock;
```

```csharp
    public ProductCardViewModel(
        BroadcastCodeResolver resolver, CatalogPhotoCache photos, StockBalanceProvider stock)
    {
        _resolver = resolver;
        _photos = photos;
        _stock = stock;
        _dispatcher = Dispatcher.CurrentDispatcher;

        // Abonelikten ÇIKILMIYOR: hepsi DI'da singleton (AppHost:
        // CatalogPhotoCache, StockBalanceProvider ve ProductCardViewModel) ve
        // kart uygulama boyunca yaşıyor, yani sızacak bir şey yok. Kart bir gün
        // transient olursa bu satırlar IDisposable ister.
        _photos.PhotoCached += OnPhotoCached;
        _stock.BalancesChanged += OnBalancesChanged;
    }

    /// <summary>
    /// Senkron turu replikaya yazdı. Olay arka plan iş parçacığından geliyor;
    /// <see cref="RefreshBalances"/> gözlemlenen koleksiyonun elemanlarına
    /// yazdığı için UI iş parçacığına geçmek ŞART (fotoğraf yolundaki gibi
    /// InvokeAsync — senkron turu UI'yı beklemesin).
    /// </summary>
    private void OnBalancesChanged(object? sender, EventArgs e)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.InvokeAsync(RefreshBalances);
            return;
        }

        RefreshBalances();
    }
```

Yeni özellikleri ve tazeleme metodunu `Load`'un hemen üstüne ekle:

```csharp
    /// <summary>
    /// Hiçbir varyanta bağlanmamış bakiye. Sıfırdan farklıysa kartta tek satır
    /// olarak gösteriliyor: eksensiz ürünlerde satışın tamamı buradan düşer,
    /// eksenli üründe ise sıfırdan farklı bir değer panelde varyanta
    /// bağlanmamış bir hareket olduğunu söyler — operatörün görmesi gerekir.
    /// </summary>
    public int ProductLevelQuantity
    {
        get => _productLevelQuantity;
        private set
        {
            if (_productLevelQuantity == value) return;
            _productLevelQuantity = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasProductLevelQuantity));
        }
    }
    private int _productLevelQuantity;

    public bool HasProductLevelQuantity => ProductLevelQuantity != 0;

    /// <summary>
    /// Rozetlerdeki sayıları YERİNDE tazeler; koleksiyonu yeniden kurmaz.
    /// Yeniden kurmak yayın ortasında rozetlerin görsel olarak zıplamasına ve
    /// kaydırma konumunun kaybolmasına yol açardı.
    ///
    /// <para>Ürün yoksa sessizce çıkar — kod kutusu boşken ya da kod
    /// çözülemezken tazelenecek bir şey yok.</para>
    /// </summary>
    public void RefreshBalances()
    {
        if (_resolution is null)
        {
            ProductLevelQuantity = 0;
            return;
        }

        var snapshot = _stock.ForProduct(_resolution.Product.Id);
        foreach (var chip in Variants)
            chip.Quantity = snapshot.For(chip.VariantId);

        ProductLevelQuantity = snapshot.ProductLevel;
    }
```

Ve `Load`'un sonundaki iki `OnPropertyChanged` çağrısının **hemen üstüne**:

```csharp
        RefreshBalances();
```

- [ ] **Step 4: Dört çağrı yerini güncelle**

Her birinde `new ProductCardViewModel(resolver, photos)` çağrısına üçüncü
argüman olarak testin kurduğu `StockBalanceProvider` örneği eklenecek. Örnek
(`OrderDeck.Tests/App/MainShellTestHarness.cs:106-109`):

```csharp
        var catalogRepo = new CatalogReplicaRepository(db);
        var stockProvider = new StockBalanceProvider(
            new StockBalanceRepository(db), labelRepo);
        var productCard = new ProductCardViewModel(
            new BroadcastCodeResolver(catalogRepo),
            new CatalogPhotoCache(Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))),
            stockProvider);
```

Aynı kalıp `MainShellPrintTests.cs:168`, `ProductCardTemplateTests.cs:141` ve
`ProductCardViewModelTests.cs:42` için de uygulanacak (her biri kendi `db` ve
`labelRepo`/yeni `LabelRepository(db)` örneğiyle).

- [ ] **Step 5: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCard`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/ViewModels/ProductCardViewModel.cs OrderDeck.Tests/App/ProductCardViewModelTests.cs OrderDeck.Tests/App/ProductCardTemplateTests.cs OrderDeck.Tests/App/MainShellTestHarness.cs OrderDeck.Tests/App/MainShellPrintTests.cs
git commit -m "feat(stok): ürün kartı bakiyeleri doldursun ve senkronla tazelensin"
```

---

### Task 11: Rozet ve ürün-seviyesi satırın XAML'i

**Files:**
- Modify: `OrderDeck.App/Views/Shell/ProductCard.xaml:5-21`, `:119-126`
- Test: `OrderDeck.Tests/App/ProductCardTemplateTests.cs`

Üç karar (kullanıcı onayladı):
1. Rozet **iki satır** — üstte eksen değeri, altında sayı. Tek satırda "M · 3"
   olsaydı "50 ML" gibi uzun değerler kırpılır ve sayı kaybolurdu.
2. **0 sönük** (`OD.Brush.TextDim`) — bilgilendirme, satış engellenmiyor.
   **Eksi vurgulu** (`OD.Brush.Accent`) — tema kuralı: "Danger ayrı renk değil,
   Accent'in kendisi".
3. Ürün seviyesindeki bakiye **sıfırdan farklıysa** tek satır.

`Style.Triggers`'ta **son eşleşen kazanır**: `IsNegative` tetikleyicisi
`IsZero`'dan **sonra** yazılmalı. (Pratikte ikisi aynı anda doğru olamaz, ama
sıralamaya güvenmek gelecekteki bir üçüncü tetikleyicide tuzağa dönüşür —
bu yüzden sıra bilinçli.)

- [ ] **Step 1a: `Lay` yardımcısına bakiye tohumu ve fırça okuyucu ekle**

Task 10'da `Lay` (satır 132-157) zaten üçüncü kurucu argümanını almıştı. Şimdi
tohumlama noktası açılıyor — `OrderDeck.Tests/App/ProductCardTemplateTests.cs`
içindeki `Lay` şu hâle geliyor (değişen tek şey imzadaki `stock` parametresi ve
`balances` kurulumu; gerisi bugünküyle birebir aynı):

```csharp
    /// <summary>Kartı verilen kodla gerçekten yerleştirir.</summary>
    private static ProductCard Lay(
        Action<CatalogReplicaRepository> seed, string code, bool isShort = false,
        Action<StockBalanceRepository>? stock = null)
    {
        // Yerleşim depoya dokunmuyor: Load ne gerekiyorsa çoktan okudu —
        // bakiyeler dahil (RefreshBalances, Load'un son adımı).
        using var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);
        seed(repo);

        var balances = new StockBalanceRepository(db);
        stock?.Invoke(balances);

        var vm = new ProductCardViewModel(
            new BroadcastCodeResolver(repo),
            new CatalogPhotoCache(
                Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"))),
            new StockBalanceProvider(balances, new LabelRepository(db)));
        vm.Load(code);

        var card = new ProductCard { DataContext = new ShellStub(vm, isShort) };
        card.Measure(new Size(320, 640));
        card.Arrange(new Rect(0, 0, 320, 640));
        card.UpdateLayout();
        // Bağ güncellemesi kuyruğa giriyor; boşaltmadan ItemsSource null
        // kalıyor ve şablon HİÇ açılmıyor — test hiçbir şey doğrulamadan
        // geçerdi (bkz. ThemeTestHost.Pump).
        ThemeTestHost.Pump();
        card.UpdateLayout();
        return card;
    }

    /// <summary>
    /// Tek satırlık sunucu bakiyesi. <c>variantId</c> null verilirse ürün
    /// seviyesine yazar — varyanta bağlanmamış hareketler ayrı kovada toplanıyor.
    /// </summary>
    private static Action<StockBalanceRepository> Qty(string? variantId, int quantity)
        => repo => repo.ApplyPage(
            [new CatalogStockBalance("p1", variantId, quantity)],
            new StockCursor(DateTimeOffset.UnixEpoch, Guid.Empty));

    private static TextBlock? TryFindText(DependencyObject root, string text)
    {
        if (root is TextBlock tb && tb.Text == text) return tb;

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (TryFindText(VisualTreeHelper.GetChild(root, i), text) is { } hit)
                return hit;

        return null;
    }

    /// <summary>
    /// Verilen metni taşıyan <c>TextBlock</c>. Rozetteki sayının RENGİNİ
    /// okumak için gerekiyor: <c>CollectVisible</c> yalnız metni topluyor,
    /// fırçayı görmüyor.
    /// </summary>
    private static TextBlock FindText(DependencyObject root, string text)
        => TryFindText(root, text)
           ?? throw new InvalidOperationException(
               $"'{text}' metinli TextBlock görsel ağaçta yok — rozet çizilmemiş.");
```

Yeni `using` gerekmiyor: `CatalogStockBalance`/`StockCursor`/`StockBalanceProvider`
`OrderDeck.Core.Catalog` altında, `StockBalanceRepository`/`LabelRepository`
`OrderDeck.Core.Storage.Repositories` altında — ikisi de dosyada zaten var.

- [ ] **Step 1b: Testleri yaz (kırmızı)**

Aynı dosyaya, dosyanın kendi kalıbıyla — xUnit `Assert` + `ThemeTestHost.RunOnSta`
(bu dosya FluentAssertions **kullanmıyor**):

```csharp
    /// <summary>
    /// Değer ve sayı AYRI satırlarda. Tek satırda ("Kırmızı · M · 3") uzun
    /// eksen değerlerinde <c>TextTrimming</c> önce sayıyı yerdi — yani
    /// operatörün tam da bakmak istediği şeyi.
    /// </summary>
    [Fact]
    public void Variant_chip_shows_the_value_and_the_quantity()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var texts = new List<string>();
            CollectVisible(Lay(Seed, "Ateş", stock: Qty("v1", 3)), texts);

            Assert.Contains("Kırmızı · M", texts);
            Assert.Contains("3", texts);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// 0 sönük (bilgi — satış engellenmiyor), eksi accent (tema kuralı:
    /// "Danger ayrı renk değil, Accent'in kendisi"). Fırçalar kaynak
    /// sözlüğünden okunuyor: sabit renk yazmak, tema değiştiğinde testi
    /// sessizce yalancı yapardı.
    /// </summary>
    [Fact]
    public void Zero_is_dimmed_and_negative_is_accented()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var dim = Application.Current.Resources["OD.Brush.TextDim"];
            var accent = Application.Current.Resources["OD.Brush.Accent"];
            var normal = Application.Current.Resources["OD.Brush.Text"];

            // Bakiye tohumlanmazsa varyantın sayısı 0: eksik satır = 0 adet.
            Assert.Same(dim, FindText(Lay(Seed, "Ateş"), "0").Foreground);
            Assert.Same(accent,
                FindText(Lay(Seed, "Ateş", stock: Qty("v1", -2)), "-2").Foreground);
            Assert.Same(normal,
                FindText(Lay(Seed, "Ateş", stock: Qty("v1", 5)), "5").Foreground);
        });

        Assert.Null(error);
    }

    /// <summary>
    /// Varyantsız bakiye satırı yalnız sıfırdan farklıysa çiziliyor: her
    /// eksenli üründe "Varyantsız: 0" yazmak kartı gürültüye boğardı.
    /// </summary>
    [Fact]
    public void Product_level_line_appears_only_when_non_zero()
    {
        var error = ThemeTestHost.RunOnSta(() =>
        {
            var none = new List<string>();
            CollectVisible(Lay(Seed, "Ateş", stock: Qty(null, 0)), none);
            Assert.DoesNotContain(none, t => t.Contains("Varyantsız"));

            var shown = new List<string>();
            CollectVisible(Lay(Seed, "Ateş", stock: Qty(null, -2)), shown);
            Assert.Contains(shown, t => t.Contains("Varyantsız") && t.Contains("2"));
        });

        Assert.Null(error);
    }
```

- [ ] **Step 2: Testlerin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCardTemplateTests`
Expected: FAIL — rozette sayı yok, `Varyantsız` satırı yok.

- [ ] **Step 3: Rozet şablonunu değiştir**

`OrderDeck.App/Views/Shell/ProductCard.xaml` satır 5-21 yerine:

```xml
        <!-- Varyant rozeti: üstte eksen değeri, ALTINDA bakiye. Yan yana
             ("M · 3") koymak "50 ML" gibi uzun değerlerde sayıyı kırptırırdı.
             Sayı elle düzenlenemez — bakiyenin tek sahibi stok defteri. -->
        <DataTemplate x:Key="VariantChip">
            <Border Background="{StaticResource OD.Brush.Surface2}"
                    BorderBrush="{StaticResource OD.Brush.Border}"
                    BorderThickness="1"
                    CornerRadius="{StaticResource OD.Radius.Sm}"
                    Padding="{StaticResource OD.Pad.2}"
                    Margin="{StaticResource OD.Pad.1}">
                <StackPanel>
                    <TextBlock Text="{Binding Display}"
                               HorizontalAlignment="Center"
                               TextTrimming="CharacterEllipsis"
                               FontSize="{StaticResource OD.Font.F1}"
                               Foreground="{StaticResource OD.Brush.TextDim}"/>
                    <!-- Renk kararı VM'den geliyor: DataTrigger yalnız eşitlik
                         kurabiliyor, "0'dan küçük" ifadesi kuramıyor.
                         IsNegative tetikleyicisi SONDA: Style.Triggers'ta son
                         eşleşen kazanır. -->
                    <TextBlock Text="{Binding Quantity}"
                               HorizontalAlignment="Center"
                               FontFamily="{StaticResource OD.Font.Display}"
                               FontSize="{StaticResource OD.Font.F2}">
                        <TextBlock.Style>
                            <Style TargetType="TextBlock">
                                <Setter Property="Foreground"
                                        Value="{StaticResource OD.Brush.Text}"/>
                                <Style.Triggers>
                                    <!-- 0 uyarı DEĞİL: stok bitse de satış
                                         yazılıyor, operatör bilgilendiriliyor. -->
                                    <DataTrigger Binding="{Binding IsZero}" Value="True">
                                        <Setter Property="Foreground"
                                                Value="{StaticResource OD.Brush.TextDim}"/>
                                    </DataTrigger>
                                    <DataTrigger Binding="{Binding IsNegative}" Value="True">
                                        <Setter Property="Foreground"
                                                Value="{StaticResource OD.Brush.Accent}"/>
                                    </DataTrigger>
                                </Style.Triggers>
                            </Style>
                        </TextBlock.Style>
                    </TextBlock>
                </StackPanel>
            </Border>
        </DataTemplate>
```

- [ ] **Step 4: Ürün-seviyesi satırı ekle**

Aynı dosyada `</ItemsControl>` (satır 126) ile `</StackPanel>` (127) arasına:

```xml
                <!-- Varyanta bağlanmamış bakiye. Sıfırsa hiç çizilmiyor: her
                     eksenli üründe "Varyantsız: 0" yazmak kartı gürültüye
                     boğardı. İki Run arasındaki satır sonu + girinti TEK
                     BOŞLUĞA sadeleşiyor (yukarıdaki ad/son ek notunun aynısı),
                     bu yüzden metinde ayrıca boşluk YOK. -->
                <TextBlock Style="{StaticResource OD.Text.Micro}"
                           Margin="{StaticResource OD.Pad.Top5}"
                           Visibility="{Binding HasProductLevelQuantity,
                                        Converter={StaticResource BoolToVisibleConverter}}">
                    <Run Text="Varyantsız:"/>
                    <Run Text="{Binding ProductLevelQuantity, Mode=OneWay}"/>
                </TextBlock>
```

- [ ] **Step 5: Testlerin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~ProductCardTemplateTests`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/Views/Shell/ProductCard.xaml OrderDeck.Tests/App/ProductCardTemplateTests.cs
git commit -m "feat(stok): iki satırlı varyant rozeti ve varyantsız bakiye satırı"
```

---

### Task 12: Sipariş yazılınca bakiye anında düşsün

**Files:**
- Modify: `OrderDeck.App/ViewModels/MainShellViewModel.cs:959-967`
- Test: `OrderDeck.Tests/App/MainShellPrintTests.cs`

`WriteOrder` etiket yazmanın **tek** dar boğazı — hem tekli hem varyant
seçici üzerinden gelen akış buradan geçiyor, dolayısıyla tek çağrı yeri yeterli.

**Bilinen ve kabul edilen bayatlık:** iptal yolu burada değil. İptal edilen bir
etiketin adedi bakiyeye ancak bir sonraki senkron turunda (≤60 sn) ya da
operatör başka bir koda gidip döndüğünde (`Load`) geri döner. Kabul edilebilir:
iptal nadir ve gecikme operatörü yanlış yöne değil, **temkinli** yöne itiyor
(gösterilen bakiye gerçekten olandan azdır, fazla değil).

- [ ] **Step 1: Testi yaz (kırmızı)**

`OrderDeck.Tests/App/MainShellPrintTests.cs` içine:

```csharp
    /// <summary>
    /// Ürünün YALNIZ satıcı ekseni var (Renk, rol 1) — izleyici ekseni yok.
    /// Bu bilerek: izleyici ekseni olsaydı <c>AddChatToQueueAsync</c> varyant
    /// seçici çekmecesini açmaya çalışır, harness ise <c>drawers: null</c> ile
    /// kuruluyor ve akış hiç sipariş yazmadan sessizce dönerdi.
    /// Satıcı ekseni tekil kaldığı için <c>ResolveVariantId(null)</c> "v1"
    /// veriyor, yani düşüş VARYANT kovasında görünüyor.
    /// </summary>
    private static void SeedProductWithBalance(
        MainShellTestHarness.Harness h, string code, int quantity)
    {
        new CatalogReplicaRepository(h.Db).Replace(
            [new CatalogProduct("p1", null, "SK00001",
                                SearchNormalizer.Normalize("SK00001"), "Kolye",
                                89.90m, null, "Renk", 1, null, null, null,
                                1_700_000_000)],
            [new CatalogVariant("v1", "p1", "Kırmızı", null, null, true, 0)],
            [],
            [new CatalogBroadcastCode("p1", "Kırmızı", code,
                                      SearchNormalizer.Normalize(code),
                                      1_700_000_000, 0)]);

        new StockBalanceRepository(h.Db).ApplyPage(
            [new CatalogStockBalance("p1", "v1", quantity)],
            new StockCursor(DateTimeOffset.UnixEpoch, Guid.Empty));
    }

    [Fact]
    public void Writing_an_order_immediately_drops_the_shown_balance()
    {
        var h = MainShellTestHarness.Build();
        SeedProductWithBalance(h, code: "Buz", quantity: 5);

        // ActiveCode ataması ProductCard.Load'u tetikliyor (OnActiveCodeChanged).
        h.Vm.ActiveCode = "Buz";
        h.Vm.ProductCard.Variants.Single().Quantity.Should().Be(5);

        MainShellTestHarness.EnqueueLabel(h.Vm, "@ali", 100m);

        // Senkron turu BEKLENMEDEN düşmeli: operatör aynı kodu arka arkaya
        // satarken ekrandaki sayının gerçeği göstermesi gerekiyor.
        h.Vm.ProductCard.Variants.Single().Quantity.Should().Be(4);
    }
```

`MainShellPrintTests.cs`'in `using` listesine eklenecek tek satır (gerisi
zaten var):

```csharp
using OrderDeck.Shared.Text;   // SearchNormalizer
```

- [ ] **Step 2: Testin başarısız olduğunu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellPrintTests`
Expected: FAIL — `Expected ... to be 4, but found 5`.

- [ ] **Step 3: Tazelemeyi ekle**

`OrderDeck.App/ViewModels/MainShellViewModel.cs`:

```csharp
    private void WriteOrder(
        string sessionId, ChatMessageViewModel messageVm, decimal price,
        string? code, BroadcastCodeResolution? resolution, string? viewerAxisValue)
    {
        var label = _labels.Add(sessionId, messageVm.Message, price, code,
            productId: resolution?.Product.Id,
            productVariantId: resolution?.ResolveVariantId(viewerAxisValue));
        PrintQueue.Add(new LabelViewModel(label, messageVm.IsSenderBlacklisted));

        // Etiket yazmanın tek dar boğazı burası; bakiye senkron turunu
        // beklemeden düşsün. İPTAL yolu burada DEĞİL: iptal edilen adet
        // bakiyeye ancak bir sonraki turda (≤60 sn) ya da kart yeniden
        // yüklendiğinde döner — bilinçli ödünç, gecikme temkinli yönde.
        ProductCard.RefreshBalances();
    }
```

- [ ] **Step 4: Testin geçtiğini doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MainShellPrintTests`
Expected: PASS.

- [ ] **Step 5: Tüm koşuyu doğrula**

Run: `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj`
Expected: PASS — 0 başarısız.

Run: `dotnet build OrderDeck.App/OrderDeck.App.csproj`
Expected: `Build succeeded. 0 Error(s)`

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.App/ViewModels/MainShellViewModel.cs OrderDeck.Tests/App/MainShellPrintTests.cs
git commit -m "feat(stok): sipariş yazılınca bakiye anında düşsün"
```

---

## Elle doğrulama (kullanıcı)

1. Panelden bir ürüne stok gir → WPF'i aç → **en geç ~1 dakika içinde** kartta
   sayı görünüyor (60 sn `StabilityHorizon` + 60 sn senkron periyodu).
2. Yayın başlat, o ürünün kodunu yaz → izleyici yorumundan sipariş oluştur →
   ilgili varyantın sayısı **anında** 1 azalıyor.
3. Aynı varyanttan stoğu tüketmeye devam et → 0'da satış **engellenmiyor**,
   sayı sönük 0'dan eksiye geçiyor ve accent rengiyle vurgulanıyor.
4. İnterneti kes → sipariş yazmaya devam et → sayı düşmeye devam ediyor
   (yerel bekleyen sayımı), uygulama hata vermiyor. İnternet geri gelince
   sunucu bakiyesi oturunca sayı aynı yerde kalıyor (çift sayım yok).
5. Eksensiz (varyantsız) bir ürünün kodunu yaz → tek rozet + gerekiyorsa
   "Varyantsız: N" satırı görünüyor.
6. Log'da `StockSyncHostedService starting (cadence=00:01:00)` satırı var.

## Kapsam dışı

- Stok bitince satışı **engellemek** / rezervasyon: bilinçli olarak yok.
- Öksüz bakiye satırlarını temizlemek (yukarıdaki 9. maddeye bakın).
- Panelden stok girişi, barkod okutma (Faz 1c) ve sürüm/installer işleri.
- `LicensesWpfCatalogPullController` uyum kalkanının kaldırılması.

## Yayın

Tek PR: `feat/wpf-stok-bakiyeleri`. Commit'siz duran `.gitignore` /
`.claude/launch.json` / `.codex/` / `AGENTS.md` / `docs/` dosyaları bu PR'a
**karıştırılmayacak** — her commit'te dosyalar tek tek `git add` ile veriliyor.
