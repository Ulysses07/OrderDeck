# Faz 1b — Stok Defteri (Sunucu Ayağı) Uygulama Planı

> **Ajan çalışanlar için:** ZORUNLU ALT-BECERİ: Bu planı görev görev uygulamak için
> `superpowers:subagent-driven-development` (önerilen) veya
> `superpowers:executing-plans` kullanın. Adımlar takip için checkbox (`- [ ]`)
> söz dizimindedir.

**Goal:** Sunucuda işaretli hareketlerden oluşan bir stok defteri kurmak; WPF'ten
gelen sipariş senkronunu bu deftere idempotent biçimde bağlamak; panele stok
giriş/sayım/bakiye uçlarını, WPF'e de katalog ve hareket çekme uçlarını açmak.

**Architecture:** Bakiye hiçbir yerde mutlak sayı olarak saklanmaz —
`StockMovement` satırlarının işaretli toplamıdır. Sipariş senkronu deftere
*olay ekleyerek* değil, **mutabakat** yaparak yazar: saf bir
`StockLedgerReconciler`, siparişin *olması gereken* miktarı ile o siparişin
*hâlihazırdaki* hareket toplamını karşılaştırıp yalnız farkı üretir. Bu sayede
aynı sipariş kaç kez gelirse gelsin (WPF `SyncedAt=NULL` yaparak iptal, iptal
geri alma, fiyat düzeltme, varyant yeniden bağlama durumlarında siparişi tekrar
gönderiyor) defter tek doğru sonuca yakınsar ve hiçbir satır silinmez.

**Tech Stack:** ASP.NET Core 10, EF Core 10 (SQL Server prod / InMemory test),
xUnit + FluentAssertions, Dapper (yalnız WPF tarafında, bu planın dışında).

---

## Neden bu tasarım (uygulayıcının bilmesi gerekenler)

Bunları okumadan koda başlamayın; her biri planın bir yerinde "neden böyle
yazılmış" sorusunun cevabıdır.

1. **Defter append-only'dir.** İptal edilen satış silinmez; ters işaretli yeni
   bir hareket yazılır. Denetlenebilirlik bunun karşılığı.
2. **Mutabakat, olay eklemek değil.** `LabelRepository`'de `Uncancel` metodu var
   — kullanıcı iptali geri alabiliyor. Ayrıca `MarkPrinted`, `MarkCancelled`,
   `UpdatePrice` hepsi `SyncedAt = NULL` yapıyor, yani **aynı sipariş sunucuya
   defalarca gelir**. "Sipariş geldi → −1 yaz" tasarımı ikinci gelişte stoğu
   yanlış düşürürdü. Bu yüzden yazma yolu saf bir mutabakat fonksiyonundan geçer.
3. **Düşüm en dar bilinen seviyeden yapılır:** varyant biliniyorsa varyant,
   bilinmiyorsa yalnız ürün. Yayında hız, kırılım doğruluğuna feda edilmez.
4. **Negatif stok serbesttir** (uyarılır, engellenmez). Bu yüzden rezervasyon /
   kilit mekanizması yok — ve olmayacak.
5. **`Order.ProductId` / `Order.ProductVariantId` FK DEĞİLDİR.** `Order.CustomerId`
   nasıl FK'sız bir string ise bunlar da FK'sız `Guid?`. Gerekçe: sunucuda silinmiş
   bir ürüne referans veren tek bir sipariş, tüm senkron paketini 500'e düşürüp
   WPF'in outbox'ını kalıcı olarak kilitlerdi. `StockMovement` ise **gerçek FK
   alır** (Restrict) — defterin bütünlüğü senkron dayanıklılığından önce gelir.
   İkisini bağdaştıran şey, yazıcının bilinmeyen id'yi hareketi atlayarak
   geçiştirmesidir (aşağıda Task 5).
6. **Zaman iki ayrı kolondur.** `OccurredAt` iş zamanıdır ve **geçmişe dönük
   olabilir** (WPF çevrimdışıyken satılan sipariş, gerçek `AddedAt` damgasıyla
   saatler sonra gelir). `CreatedAt` sunucuya yazılma anıdır ve monotondur.
   **Çekme imleci (cursor) `CreatedAt` üstünden koşar.** `OccurredAt` üstünden
   koşsaydı geç gelen çevrimdışı satışlar imlecin gerisinde kalıp sessizce
   atlanırdı.
7. **Katalog çekmesi tam anlık görüntüdür, artımlı değil.** Gerekçe: panelden
   ürün/varyant **silinebiliyor** ve artımlı çekme silmeleri göremez → WPF'te
   hayalet satır kalır. Katalog küçük (lisans başına yüzler mertebesi), tam
   sayfalı anlık görüntü kendini onarır. Stok tarafı ise **artımlı** çekilir —
   defter append-only, orada silme diye bir şey yok.
8. **WPF'e hareket değil bakiye iner.** Stok ucu imleci hareketler üstünde koşar
   ama gövdede ham defter satırı değil, değişen her ürün/varyant için **o anda
   yeniden hesaplanmış mutlak bakiye** döner. Bütün defteri WPF'e indirmek yerel
   tabloyu sonsuza büyütür ve toplama mantığını ikinci kez yazdırırdı. İstemci
   bu yüzden **upsert** eder, toplamaz.

---

## Dosya haritası

**Yeni proje**
- `OrderDeck.Shared/OrderDeck.Shared.csproj` — hem sunucunun hem WPF'in
  kullanacağı saf metin yardımcıları.
- `OrderDeck.Shared/Text/TurkishAscii.cs` (taşınır)
- `OrderDeck.Shared/Text/SearchNormalizer.cs` (taşınır)

**Sunucu — yeni**
- `OrderDeck.LicenseServer/Domain/StockMovement.cs` — entity + `StockMovementReason`
- `OrderDeck.LicenseServer/Services/Stock/StockLedgerReconciler.cs` — saf mutabakat
- `OrderDeck.LicenseServer/Services/Stock/StockLedgerWriter.cs` — mutabakatı DB'ye bağlar
- `OrderDeck.LicenseServer/Services/Stock/StockBalanceService.cs` — bakiye toplama
- `OrderDeck.LicenseServer/Controllers/Panel/PanelStockController.cs` — giriş/sayım/bakiye/hareket
- `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`
- `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfStockPullController.cs` —
  bakiye anlık görüntüsü (hareket değil), artımlı bileşik imleçle

**Sunucu — değişecek**
- `OrderDeck.LicenseServer/Domain/Order.cs` — `ProductId` / `ProductVariantId`
- `OrderDeck.LicenseServer/Domain/CatalogLimits.cs` — `MovementNote`
- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — DbSet + fluent config
- `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs` — DTO + yazıcı bağlantısı
- `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs` — silme 409 koruması
- `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs` — silme 409 koruması
- `OrderDeck.LicenseServer/Program.cs` — `StockLedgerWriter` / `StockBalanceService` DI

**İstemci sözleşmesi (bu repoda, WPF tarafı)**
- `OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs` — iki opsiyonel alan

---

## Task 1: `OrderDeck.Shared` projesi — normalleştiriciyi ortak zemine taşı

**Neden bu görev bu planda:** WPF'in yerel katalog kopyası, sunucunun ürettiği
`NameSearch` değerini saklayacak. WPF aranan iğneyi *başka bir* normalleştiriciden
geçirirse yerel arama sessizce yanlış sonuç döner — ve hiçbir test bunu göstermez,
çünkü her iki taraf da kendi kopyasıyla tutarlıdır. Taşımayı burada yapıyoruz ki
WPF planı yalnız WPF dosyalarına dokunsun.

**Files:**
- Create: `OrderDeck.Shared/OrderDeck.Shared.csproj`
- Create: `OrderDeck.Shared/Text/TurkishAscii.cs`
- Create: `OrderDeck.Shared/Text/SearchNormalizer.cs`
- Delete: `OrderDeck.LicenseServer/Services/Catalog/TurkishAscii.cs`
- Delete: `OrderDeck.LicenseServer/Services/Catalog/SearchNormalizer.cs`
- Modify: `OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
- Modify: `OrderDeck.Core/OrderDeck.Core.csproj`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Modify: `OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Catalog/SearchNormalizerTests.cs` (yalnız `using` satırı)

- [ ] **Step 1: Projeyi oluştur**

`OrderDeck.Shared/OrderDeck.Shared.csproj`:

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
</Project>
```

- [ ] **Step 2: `TurkishAscii`'yi taşı**

`OrderDeck.Shared/Text/TurkishAscii.cs` — **dosyanın tam içeriği**:

```csharp
namespace OrderDeck.Shared.Text;

/// <summary>
/// Türkçe harflerin ASCII karşılıkları — <b>tek kaynak</b>.
///
/// Neden ayrı bir sınıf: aynı katlama tablosuna birden çok iş ihtiyaç duyuyor —
/// barkoda girecek kod parçası (<c>AxisCodeDeriver</c>, Code128 yalnız ASCII
/// kodlar), arama normalleştirmesi (<c>SearchNormalizer</c>, "tisort" yazan
/// kullanıcı "Tişört"ü bulmalı) ve WPF tarafındaki yorum eşleştirmesi. Tablo
/// kopyalanırsa sessizce ayrışır ve ayrışmayı hiçbir test göstermez: her taraf
/// kendi kopyasıyla tutarlı kalır, yalnız birbirleriyle tutarsız olur.
///
/// Neden <c>OrderDeck.Shared</c>'da: sunucu ile WPF arasında ortak assembly yok;
/// WPF'in yerel katalog kopyası sunucunun ürettiği <c>NameSearch</c> değerini
/// saklıyor, iğne başka bir tablodan geçerse yerel arama sessizce bozulur.
/// </summary>
public static class TurkishAscii
{
    /// <summary>
    /// Büyük harfe çevrilmiş bir karakteri ASCII karşılığına indirger;
    /// karşılığı olmayanı olduğu gibi döner.
    /// </summary>
    public static char Fold(char upper) => upper switch
    {
        'Ç' => 'C',
        'Ğ' => 'G',
        'İ' => 'I',      // U+0130 — ToUpperInvariant bunu korur
        '\u0131' => 'I', // ı — ToUpperInvariant U+0131'i küçük bırakır
        'Ö' => 'O',
        'Ş' => 'S',
        'Ü' => 'U',
        _ => upper,
    };
}
```

**DİKKAT:** Özgün dosyada `<see cref="AxisCodeDeriver"/>` ve
`<see cref="SearchNormalizer"/>` vardı. `AxisCodeDeriver` bu projeden görünmüyor;
cref çözülemezse CS1574 uyarısı çıkar ve `TreatWarningsAsErrors` yüzünden **derleme
kırılır**. Bu yüzden yukarıda `<c>` kullanıldı. Aynı kural `SearchNormalizer` için
de geçerli.

- [ ] **Step 3: `SearchNormalizer`'ı taşı**

`OrderDeck.Shared/Text/SearchNormalizer.cs` — **dosyanın tam içeriği**:

```csharp
using System.Text;

namespace OrderDeck.Shared.Text;

/// <summary>
/// Arama için karşılaştırılabilir biçim üretir: büyük harf + Türkçe harfler
/// ASCII'ye katlanmış + boşluklar sadeleşmiş.
///
/// Neden gerekli: <c>Name.Contains(q)</c> SQL'de <c>LIKE '%…%'</c>'ye çevriliyor
/// ve büyük/küçük harf duyarlılığını <b>veritabanının collation'ı</b> belirliyor.
/// SQL Server varsayılanı duyarsız, PostgreSQL ise duyarlı — yani göç günü arama
/// sessizce bozulurdu ("tişört" yazan "Tişört"ü bulamazdı). Hem saklanan değer
/// (<c>Product.NameSearch</c>) hem aranan iğne <b>aynı</b> fonksiyondan geçtiği
/// için eşleşme collation'dan bağımsız hâle gelir; göçte davranış değişmez.
///
/// Türkçe harf katlaması ayrıca gerçek bir kullanıcı şikâyetini kapatıyor:
/// "tisort" → "Tişört", "kirmizi" → "Kırmızı" artık eşleşiyor.
///
/// Harf/rakam dışı karakterler (tire, nokta, parantez) <b>korunur</b>: atmak
/// sürpriz eşleşmeler üretir ("A-1" ile "A1" aynı şey değil).
/// </summary>
public static class SearchNormalizer
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var sb = new StringBuilder(value.Length);
        var pendingSpace = false;

        foreach (var ch in value.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(ch))
            {
                // Baştaki boşluk hiç yazılmaz, ardışık boşluklar tek boşluğa iner;
                // sondaki de yazılmadan kalır (bayrak asla boşaltılmaz).
                pendingSpace = sb.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                sb.Append(' ');
                pendingSpace = false;
            }

            sb.Append(TurkishAscii.Fold(ch));
        }

        return sb.ToString();
    }
}
```

- [ ] **Step 4: Eski dosyaları sil**

```bash
git rm OrderDeck.LicenseServer/Services/Catalog/TurkishAscii.cs \
       OrderDeck.LicenseServer/Services/Catalog/SearchNormalizer.cs
```

- [ ] **Step 5: Proje referanslarını ekle**

`OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` — mevcut son
`<ItemGroup>` içine, `OrderDeck.PdfParsing` satırının yanına:

```xml
    <ProjectReference Include="..\OrderDeck.Shared\OrderDeck.Shared.csproj" />
```

`OrderDeck.Core/OrderDeck.Core.csproj` — `OrderDeck.PdfParsing` satırının yanına
**aynı satır**. Core'da bugün kullanan yok; referans şimdi ekleniyor ki WPF planı
yalnız WPF dosyalarına dokunsun ve TFM uyumu bugün doğrulanmış olsun.

- [ ] **Step 6: Projeyi çözüme ekle**

```bash
dotnet sln OrderDeck.sln add OrderDeck.Shared/OrderDeck.Shared.csproj
```

- [ ] **Step 7: `using` satırlarını ekle**

Aşağıdaki beş dosyanın `using` bloğuna `using OrderDeck.Shared.Text;` ekleyin
(alfabetik sırada, mevcut `using OrderDeck.LicenseServer…` satırlarından sonra):

- `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- `OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs`
- `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`
- `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`
- `OrderDeck.LicenseServer.Tests/Services/Catalog/SearchNormalizerTests.cs`

`AxisCodeDeriver.cs:54` ve `PanelProductVariantsController.cs:381` satırlarındaki
`<see cref="TurkishAscii"/>` / `<see cref="SearchNormalizer"/>` atıfları
`using` eklendikten sonra çözülmeye devam eder — değiştirmeyin.

- [ ] **Step 8: Derle ve testleri koştur**

Run: `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj`
Expected: 0 hata, 0 uyarı.

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~SearchNormalizer`
Expected: PASS (4 test).

Run: `dotnet build OrderDeck.Core/OrderDeck.Core.csproj`
Expected: 0 hata.

- [ ] **Step 9: Commit**

```bash
git add OrderDeck.Shared OrderDeck.sln \
        OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
        OrderDeck.Core/OrderDeck.Core.csproj \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Services/Catalog/AxisCodeDeriver.cs \
        OrderDeck.LicenseServer/Services/Catalog/TurkishAscii.cs \
        OrderDeck.LicenseServer/Services/Catalog/SearchNormalizer.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs \
        OrderDeck.LicenseServer.Tests/Services/Catalog/SearchNormalizerTests.cs
git commit -m "$(cat <<'EOF'
refactor(shared): normalleştiriciyi OrderDeck.Shared'a taşı

WPF yerel katalog kopyası sunucunun ürettiği NameSearch'ü saklayacak;
iğne farklı bir katlama tablosundan geçerse yerel arama sessizce bozulur.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 2: `StockMovement` entity + şema

**Files:**
- Create: `OrderDeck.LicenseServer/Domain/StockMovement.cs`
- Modify: `OrderDeck.LicenseServer/Domain/CatalogLimits.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Create: `OrderDeck.LicenseServer/Data/Migrations/*_AddStockMovement.cs` (üretilir)
- Test: `OrderDeck.LicenseServer.Tests/Data/StockModelTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Data/StockModelTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Data;

/// <summary>
/// Şema iddialarını EF model metadata'sından doğrular. Gerekçe: testler
/// InMemory üstünde koşuyor ve InMemory HasMaxLength'i de indeksleri de yok
/// sayıyor — davranışsal test bu ayrımı gösteremez, metadata gösterir.
/// </summary>
public class StockModelTests
{
    private static IModel Model()
    {
        var opts = new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        using var db = new LicenseDbContext(opts);
        return db.Model;
    }

    [Fact]
    public void Note_max_length_matches_CatalogLimits()
    {
        var prop = Model().FindEntityType(typeof(StockMovement))!
            .FindProperty(nameof(StockMovement.Note))!;
        prop.GetMaxLength().Should().Be(CatalogLimits.MovementNote);
    }

    [Fact]
    public void Cursor_index_on_license_and_created_at_exists()
    {
        var indexes = Model().FindEntityType(typeof(StockMovement))!.GetIndexes();
        indexes.Should().ContainSingle(i =>
            i.Properties.Count == 2 &&
            i.Properties[0].Name == nameof(StockMovement.LicenseId) &&
            i.Properties[1].Name == nameof(StockMovement.CreatedAt));
    }

    [Fact]
    public void Balance_index_on_license_product_variant_exists()
    {
        var indexes = Model().FindEntityType(typeof(StockMovement))!.GetIndexes();
        indexes.Should().ContainSingle(i =>
            i.Properties.Count == 3 &&
            i.Properties[0].Name == nameof(StockMovement.LicenseId) &&
            i.Properties[1].Name == nameof(StockMovement.ProductId) &&
            i.Properties[2].Name == nameof(StockMovement.ProductVariantId));
    }

    [Fact]
    public void Order_index_exists_for_reconciliation_lookup()
    {
        var indexes = Model().FindEntityType(typeof(StockMovement))!.GetIndexes();
        indexes.Should().Contain(i =>
            i.Properties.Count == 1 &&
            i.Properties[0].Name == nameof(StockMovement.OrderId));
    }

    [Fact]
    public void Product_and_variant_deletes_are_restricted()
    {
        var entity = Model().FindEntityType(typeof(StockMovement))!;
        foreach (var fkName in new[]
                 { nameof(StockMovement.ProductId), nameof(StockMovement.ProductVariantId) })
        {
            var fk = entity.GetForeignKeys()
                .Single(f => f.Properties.Count == 1 && f.Properties[0].Name == fkName);
            fk.DeleteBehavior.Should().Be(DeleteBehavior.Restrict);
        }
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockModelTests`
Expected: FAIL — `StockMovement` tipi yok, derleme hatası.

- [ ] **Step 3: Sınırı ekle**

`OrderDeck.LicenseServer/Domain/CatalogLimits.cs` — `Barcode` sabitinin altına:

```csharp
    /// <summary>
    /// Stok hareketi notu ("mal kabul irsaliye 4412", "sayım farkı"). Serbest
    /// metin; 200 karakter panelde tek satırda okunabilir kalıyor.
    /// </summary>
    public const int MovementNote = 200;
```

- [ ] **Step 4: Entity'yi yaz**

`OrderDeck.LicenseServer/Domain/StockMovement.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Bir stok hareketinin gerekçesi. Sayı değerleri kalıcıdır (kolonda saklanır),
/// asla değiştirilmez.
/// </summary>
public enum StockMovementReason
{
    /// <summary>Yayında satış — WPF sipariş senkronundan türer.</summary>
    Sale = 1,

    /// <summary>Satışın iptali veya iadesi — satışın ters işaretlisi.</summary>
    CancelReturn = 2,

    /// <summary>Mal kabul / stok girişi — panelden elle.</summary>
    Entry = 3,

    /// <summary>Sayım düzeltmesi — sayılan ile defterin farkı kadar.</summary>
    CountAdjustment = 4,
}

/// <summary>
/// Stok defterinin tek satırı. <b>Bakiye hiçbir yerde saklanmaz</b> — bu
/// satırların işaretli toplamıdır.
///
/// Neden mutlak bakiye kolonu yok: bakiye kolonu, aynı ürüne aynı anda yazan iki
/// yol (yayın senkronu + panel girişi) olduğu anda kilit ister; kilit de yayın
/// hızını vurur. Toplam ise çakışmasız ve geçmişe dönük düzeltilebilir. Bunun
/// bedeli her okumada bir <c>SUM</c>; katalog ölçeğinde (lisans başına yüzler
/// mertebesi ürün) bu bedel önemsiz.
///
/// Satırlar <b>asla silinmez veya güncellenmez</b>. İptal, ters işaretli yeni bir
/// satırdır — defter denetlenebilir kalsın diye.
/// </summary>
public sealed class StockMovement
{
    public Guid Id { get; set; }

    public Guid LicenseId { get; set; }
    public License License { get; set; } = null!;

    public Guid ProductId { get; set; }
    public Product Product { get; set; } = null!;

    /// <summary>
    /// Null ise düşüm/giriş <b>ürün seviyesindedir</b>: hangi varyant olduğu
    /// bilinmiyor. Spec bunu açıkça kabul ediyor — yayında hız, kırılım
    /// doğruluğuna feda edilmez. Sonucu: "A12'den 10 sattım" doğru, "kaçı M'di"
    /// bilinmez.
    /// </summary>
    public Guid? ProductVariantId { get; set; }
    public ProductVariant? ProductVariant { get; set; }

    /// <summary>
    /// <b>İşaretli</b> miktar: satış negatif, giriş/iade pozitif. Sıfır satır
    /// hiç yazılmaz (mutabakat sıfır farkı üretmez).
    /// </summary>
    public int Quantity { get; set; }

    public StockMovementReason Reason { get; set; }

    /// <summary>
    /// Kaynak sipariş — <see cref="StockMovementReason.Sale"/> ve
    /// <see cref="StockMovementReason.CancelReturn"/> için dolu, elle girişlerde
    /// null. FK <b>değil</b>: <c>Order</c> ile <c>StockMovement</c> aynı işlemde
    /// yazılıyor ve sipariş kimliği WPF'ten geliyor; sert bağ kurmak senkronu
    /// kırılgan yapardı. Mutabakat bu kolonu indeksten okur.
    /// </summary>
    public Guid? OrderId { get; set; }

    public string? Note { get; set; }

    /// <summary>
    /// <b>İş zamanı</b> — hareketin gerçekte olduğu an. Geçmişe dönük olabilir:
    /// WPF çevrimdışıyken satılan sipariş kendi <c>AddedAt</c> damgasıyla saatler
    /// sonra ulaşır.
    /// </summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>
    /// <b>Sunucuya yazılma anı</b> — monoton artar. Çekme imleci (WPF stok
    /// senkronu) bunun üstünden koşar. <see cref="OccurredAt"/> üstünden koşsaydı
    /// geç ulaşan çevrimdışı satışlar imlecin gerisinde kalıp sessizce atlanırdı.
    /// </summary>
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Elle girişlerde işlemi yapan operatör; senkrondan gelenlerde null.</summary>
    public Guid? CreatedByOperatorId { get; set; }
}
```

- [ ] **Step 5: DbContext'e ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — `ProductPhotos` DbSet'inin
altına:

```csharp
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
```

`OnModelCreating` içinde, `mb.Entity<ProductPhoto>(…)` bloğunun hemen ardına:

```csharp
        mb.Entity<StockMovement>(b =>
        {
            b.HasKey(m => m.Id);
            b.Property(m => m.Reason).HasConversion<int>();
            b.Property(m => m.Note).HasMaxLength(CatalogLimits.MovementNote);

            b.HasOne(m => m.License).WithMany()
                .HasForeignKey(m => m.LicenseId).OnDelete(DeleteBehavior.Cascade);

            // Restrict: hareketi olan ürün/varyant SİLİNEMEZ. Cascade olsaydı tek
            // bir yanlış tıklama defterin bir bölümünü sessizce yok ederdi.
            // Controller bunu 409 ile karşılıyor (bkz. Task 10) — kullanıcı
            // DbUpdateException/500 değil, Türkçe bir açıklama görüyor.
            b.HasOne(m => m.Product).WithMany()
                .HasForeignKey(m => m.ProductId).OnDelete(DeleteBehavior.Restrict);
            b.HasOne(m => m.ProductVariant).WithMany()
                .HasForeignKey(m => m.ProductVariantId).OnDelete(DeleteBehavior.Restrict);

            // Bakiye toplaması: WHERE LicenseId=… GROUP BY ProductId, ProductVariantId
            b.HasIndex(m => new { m.LicenseId, m.ProductId, m.ProductVariantId });
            // WPF çekme imleci: WHERE LicenseId=… AND CreatedAt > @since ORDER BY CreatedAt
            b.HasIndex(m => new { m.LicenseId, m.CreatedAt });
            // Mutabakat: bir siparişin mevcut hareketlerini bul.
            b.HasIndex(m => m.OrderId);
        });
```

- [ ] **Step 6: Testi koştur, yeşili gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockModelTests`
Expected: PASS (5 test).

- [ ] **Step 7: Migration üret**

```bash
dotnet ef migrations add AddStockMovement \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
  --output-dir Data/Migrations
```

Üretilen `Up()` içinde şunların olduğunu **gözle doğrulayın**: `StockMovements`
tablosu, `Quantity` int, `Reason` int, `Note` nvarchar(200), üç indeks, ve
Product/ProductVariant FK'larında `onDelete: ReferentialAction.Restrict`.

- [ ] **Step 8: Tüm sunucu testlerini koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: hepsi PASS (~752).

- [ ] **Step 9: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/StockMovement.cs \
        OrderDeck.LicenseServer/Domain/CatalogLimits.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations \
        OrderDeck.LicenseServer.Tests/Data/StockModelTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): StockMovement defteri — şema

Bakiye saklanmıyor; işaretli hareketlerin toplamı. OccurredAt iş zamanı,
CreatedAt sunucu yazma anı — imleç CreatedAt üstünden koşacak.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 3: `StockLedgerReconciler` — saf mutabakat

**Bu planın kalbi.** Veritabanına dokunmaz, saat okumaz, kimlik doğrulamaz —
girdi alır, fark listesi döner. Bu yüzden onlarca senaryo saniyeler içinde test
edilebiliyor.

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Stock/StockLedgerReconciler.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/Stock/StockLedgerReconcilerTests.cs`

- [ ] **Step 1: Başarısız testleri yaz**

`OrderDeck.LicenseServer.Tests/Services/Stock/StockLedgerReconcilerTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.LicenseServer.Services.Stock;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Stock;

public class StockLedgerReconcilerTests
{
    private static readonly Guid P = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid V1 = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid V2 = Guid.Parse("33333333-3333-3333-3333-333333333333");

    private static LedgerOrderState Order(
        Guid? productId = null,
        Guid? variantId = null,
        bool shippingFee = false,
        bool cancelled = false,
        bool tentative = false)
        => new(Guid.NewGuid(), productId ?? P, variantId, shippingFee, cancelled, tentative);

    private static Dictionary<StockKey, int> None() => new();

    [Fact]
    public void New_sale_emits_minus_one_at_variant_level()
    {
        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), None());

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
        });
    }

    [Fact]
    public void New_sale_without_variant_deducts_at_product_level()
    {
        var deltas = StockLedgerReconciler.Reconcile(Order(), None());

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, null), -1),
        });
    }

    [Fact]
    public void Repush_of_already_recorded_sale_emits_nothing()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), existing);

        deltas.Should().BeEmpty();
    }

    [Fact]
    public void Cancelling_a_recorded_sale_emits_plus_one()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(
            Order(variantId: V1, cancelled: true), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), 1),
        });
    }

    [Fact]
    public void Uncancelling_emits_minus_one_again()
    {
        // İptal sonrası defter: -1 (satış) + 1 (iptal) = 0
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = 0 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
        });
    }

    [Fact]
    public void Shipping_fee_row_never_touches_stock()
    {
        StockLedgerReconciler.Reconcile(Order(shippingFee: true), None())
            .Should().BeEmpty();
    }

    [Fact]
    public void Tentative_backup_writes_no_movement()
    {
        StockLedgerReconciler.Reconcile(Order(variantId: V1, tentative: true), None())
            .Should().BeEmpty();
    }

    [Fact]
    public void Promoting_a_tentative_backup_emits_the_sale()
    {
        // Yedek onaylandı: artık tentative değil, ilk kez düşülüyor.
        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), None());

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
        });
    }

    [Fact]
    public void Order_without_product_emits_nothing()
    {
        var order = new LedgerOrderState(Guid.NewGuid(), null, null, false, false, false);

        StockLedgerReconciler.Reconcile(order, None()).Should().BeEmpty();
    }

    [Fact]
    public void Rebinding_to_another_variant_returns_the_old_and_takes_the_new()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V2), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V2), -1),
            new LedgerDelta(new StockKey(P, V1), 1),
        });
    }

    [Fact]
    public void Binding_a_variant_to_a_product_level_sale_moves_the_deduction_down()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, null)] = -1 };

        var deltas = StockLedgerReconciler.Reconcile(Order(variantId: V1), existing);

        deltas.Should().BeEquivalentTo(new[]
        {
            new LedgerDelta(new StockKey(P, V1), -1),
            new LedgerDelta(new StockKey(P, null), 1),
        });
    }

    [Fact]
    public void Cancelled_order_with_already_balanced_ledger_emits_nothing()
    {
        var existing = new Dictionary<StockKey, int> { [new StockKey(P, V1)] = 0 };

        StockLedgerReconciler.Reconcile(Order(variantId: V1, cancelled: true), existing)
            .Should().BeEmpty();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    public void Reconcile_is_idempotent_when_applied_repeatedly(int rounds)
    {
        var ledger = new Dictionary<StockKey, int>();
        var order = Order(variantId: V1);

        for (var i = 0; i < rounds; i++)
        {
            foreach (var d in StockLedgerReconciler.Reconcile(order, ledger))
            {
                ledger[d.Key] = ledger.TryGetValue(d.Key, out var cur)
                    ? cur + d.QuantityDelta
                    : d.QuantityDelta;
            }
        }

        ledger.Should().BeEquivalentTo(
            new Dictionary<StockKey, int> { [new StockKey(P, V1)] = -1 });
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockLedgerReconcilerTests`
Expected: FAIL — `StockLedgerReconciler` tipi yok, derleme hatası.

- [ ] **Step 3: Mutabakatı yaz**

`OrderDeck.LicenseServer/Services/Stock/StockLedgerReconciler.cs`:

```csharp
namespace OrderDeck.LicenseServer.Services.Stock;

/// <summary>
/// Defterin toplandığı anahtar. Varyant null ise ürün seviyesi.
/// </summary>
public readonly record struct StockKey(Guid ProductId, Guid? ProductVariantId);

/// <summary>
/// Mutabakata giren siparişin stok açısından tek ilgilendiren yüzü. Fiyat,
/// müşteri, platform gibi alanlar bilerek yok — defter bunları umursamıyor.
/// </summary>
/// <param name="OrderId">Yalnız izlenebilirlik için; mutabakat kararını etkilemez.</param>
public sealed record LedgerOrderState(
    Guid OrderId,
    Guid? ProductId,
    Guid? ProductVariantId,
    bool IsShippingFee,
    bool IsCancelled,
    bool IsTentativeBackup);

/// <summary>Bir anahtara yazılacak fark. Sıfır fark asla üretilmez.</summary>
public sealed record LedgerDelta(StockKey Key, int QuantityDelta);

/// <summary>
/// Bir siparişin <b>olması gereken</b> stok etkisi ile <b>hâlihazırdaki</b> etkisi
/// arasındaki farkı üretir. Saf: veritabanı, saat, kimlik yok.
///
/// Neden "olay ekleme" değil de mutabakat: WPF aynı siparişi defalarca gönderir.
/// <c>LabelRepository</c>'de iptal (<c>MarkCancelled</c>), <b>iptali geri alma</b>
/// (<c>Uncancel</c>), basım (<c>MarkPrinted</c>) ve fiyat düzeltme
/// (<c>UpdatePrice</c>) hepsi <c>SyncedAt = NULL</c> yapıyor — yani satır yeniden
/// senkron kuyruğuna giriyor. "Sipariş geldi → −1 yaz" tasarımı ikinci gelişte
/// stoğu ikinci kez düşürürdü. Burada fark sıfırsa hiçbir şey yazılmaz.
///
/// Ters işlem de aynı fonksiyondan çıkar: iptal edilmiş siparişin olması gereken
/// etkisi 0'dır, defterde −1 duruyorsa fark +1 olur ve çağıran bunu
/// <c>CancelReturn</c> olarak yazar. Hiçbir satır silinmez.
/// </summary>
public static class StockLedgerReconciler
{
    /// <param name="order">Siparişin güncel hâli.</param>
    /// <param name="existing">
    /// Bu siparişin bugüne kadar yazılmış hareketlerinin anahtar bazlı
    /// <b>toplamı</b>. Boş sözlük "hiç yazılmamış" demektir.
    /// </param>
    public static IReadOnlyList<LedgerDelta> Reconcile(
        LedgerOrderState order,
        IReadOnlyDictionary<StockKey, int> existing)
    {
        var desired = Desired(order);
        var deltas = new List<LedgerDelta>();

        foreach (var (key, want) in desired)
        {
            existing.TryGetValue(key, out var have);
            if (want != have) deltas.Add(new LedgerDelta(key, want - have));
        }

        // Artık istenmeyen anahtarlar: iptal, varyant yeniden bağlama, ürün
        // değişikliği. Sıfırlanacak kadar ters hareket yazılır.
        foreach (var (key, have) in existing)
        {
            if (desired.ContainsKey(key)) continue;
            if (have != 0) deltas.Add(new LedgerDelta(key, -have));
        }

        return deltas;
    }

    /// <summary>
    /// Siparişin olması gereken stok etkisi. En fazla tek girdi döner; sipariş
    /// stoğu ilgilendirmiyorsa boş.
    /// </summary>
    private static Dictionary<StockKey, int> Desired(LedgerOrderState o)
    {
        var desired = new Dictionary<StockKey, int>();

        // Ürün bağlanmamış: kullanıcı kararı gereği satış YİNE OLUR, kart
        // "tanımlı değil" der; stok hareketi yazılmaz.
        if (o.ProductId is null) return desired;

        // Kargo ücreti satırı ürün değil.
        if (o.IsShippingFee) return desired;

        // İptal edilmiş siparişin olması gereken etkisi sıfırdır.
        if (o.IsCancelled) return desired;

        // Geçici yedek henüz satış değil: asıl satış iptal edilirse yedek
        // yükselir ve O ZAMAN düşülür. Şimdi düşmek çift sayım olurdu.
        if (o.IsTentativeBackup) return desired;

        // Miktar alanı yok: her etiket bir adettir.
        desired[new StockKey(o.ProductId.Value, o.ProductVariantId)] = -1;
        return desired;
    }
}
```

- [ ] **Step 4: Testi koştur, yeşili gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockLedgerReconcilerTests`
Expected: PASS (14 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Stock/StockLedgerReconciler.cs \
        OrderDeck.LicenseServer.Tests/Services/Stock/StockLedgerReconcilerTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): StockLedgerReconciler — idempotent mutabakat

WPF aynı siparişi iptal/geri alma/basım/fiyat değişiminde yeniden gönderiyor;
olay ekleme tasarımı çift sayardı. Fark sıfırsa hiçbir şey yazılmaz.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 4: Siparişi ürüne bağla — `Order.ProductId` / `ProductVariantId`

**Files:**
- Modify: `OrderDeck.LicenseServer/Domain/Order.cs`
- Modify: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs:145-172,201-253,285-294`
- Modify: `OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs`
- Create: `OrderDeck.LicenseServer/Data/Migrations/*_AddOrderProductLink.cs` (üretilir)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderProductLinkSyncTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderProductLinkSyncTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class OrderProductLinkSyncTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public OrderProductLinkSyncTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid LicenseId, Guid ProductId, Guid VariantId)> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-STOK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "A1",
            Name = "Tişört",
            DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "M",
            Axis1Code = "M",
            VariantCode = "A1-M",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProductVariants.Add(variant);

        await db.SaveChangesAsync();
        return (client, license.Id, product.Id, variant.Id);
    }

    private static object OrderPayload(Guid orderId, Guid? productId, Guid? variantId) => new
    {
        id = orderId,
        sessionId = (Guid?)null,
        customerId = Guid.NewGuid().ToString("N"),
        platform = "youtube",
        username = "izleyici",
        displayName = "İzleyici",
        messageText = "A1 M",
        code = "A1",
        price = 100m,
        addedAt = DateTimeOffset.UtcNow,
        printedAt = (DateTimeOffset?)null,
        cancelledAt = (DateTimeOffset?)null,
        cancelReason = (string?)null,
        isShippingFee = false,
        isBackupPromoted = false,
        isTentativeBackup = false,
        productId,
        productVariantId = variantId
    };

    [Fact]
    public async Task Sync_persists_product_link()
    {
        var (client, licenseId, productId, variantId) = await SeedAsync();
        var orderId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, productId, variantId) } });

        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var saved = await db.Orders.SingleAsync(o => o.Id == orderId);
        saved.ProductId.Should().Be(productId);
        saved.ProductVariantId.Should().Be(variantId);
    }

    [Fact]
    public async Task Sync_without_product_link_keeps_nulls()
    {
        var (client, licenseId, _, _) = await SeedAsync();
        var orderId = Guid.NewGuid();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, null, null) } });

        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var saved = await db.Orders.SingleAsync(o => o.Id == orderId);
        saved.ProductId.Should().BeNull();
        saved.ProductVariantId.Should().BeNull();
    }

    [Fact]
    public async Task Repushing_the_same_order_can_rebind_the_variant()
    {
        var (client, licenseId, productId, variantId) = await SeedAsync();
        var orderId = Guid.NewGuid();

        await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, productId, null) } });

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, productId, variantId) } });

        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var saved = await db.Orders.SingleAsync(o => o.Id == orderId);
        saved.ProductVariantId.Should().Be(variantId);
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~OrderProductLinkSyncTests`
Expected: FAIL — `Order.ProductId` yok, derleme hatası.

- [ ] **Step 3: Entity'ye alanları ekle**

`OrderDeck.LicenseServer/Domain/Order.cs` — `IsTentativeBackup` özelliğinin altına:

```csharp
    /// <summary>
    /// Bağlandığı katalog ürünü. <b>FK DEĞİL</b> — bilerek.
    ///
    /// Gerekçe: sert bir FK, sunucuda silinmiş bir ürüne referans veren tek bir
    /// siparişte tüm senkron paketini 500'e düşürür ve WPF'in outbox'ı sonsuza
    /// dek aynı paketi yeniden dener — yani tek bir kayıt bütün senkronu kilitler.
    /// <c>CustomerId</c> de aynı sebeple FK'sız. Defter tarafındaki
    /// <c>StockMovement</c> ise gerçek FK alıyor; bilinmeyen id'yi
    /// <c>StockLedgerWriter</c> hareketi atlayarak eliyor.
    /// </summary>
    public Guid? ProductId { get; set; }

    /// <summary>
    /// Bağlandığı varyant. Null ise düşüm ürün seviyesinde yapılır — hangi
    /// varyant olduğu bilinmiyor demektir, hata değil.
    /// </summary>
    public Guid? ProductVariantId { get; set; }
```

- [ ] **Step 4: İndeksi ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — `mb.Entity<Order>(…)` bloğunun
içine (mevcut indekslerin yanına):

```csharp
            // Ürün silinmeden önce "bu ürüne bağlı sipariş var mı" sorusu ve
            // panel ürün-bazlı sipariş listesi bu indeksi okur. FK yok, indeks var.
            b.HasIndex(o => new { o.LicenseId, o.ProductId });
```

- [ ] **Step 5: Sunucu DTO'larını genişlet**

`LicensesSessionsSyncController.cs:161` — `SyncOrderItem` kaydının son parametresi
`bool IsTentativeBackup` idi; sonuna **varsayılanlı** iki parametre eklenir:

```csharp
        bool IsTentativeBackup,
        // Varsayılanlı: eski WPF sürümleri bu alanları göndermiyor ve
        // göndermemeleri hata değil — ürün bağlanmamış sipariş geçerli bir
        // durumdur (kart "tanımlı değil" der, satış yine olur).
        Guid? ProductId = null,
        Guid? ProductVariantId = null);
```

`SyncedOrderDto` — `IsTentativeBackup` ile `UpdatedAt` arasına:

```csharp
        bool IsShippingFee, bool IsBackupPromoted, bool IsTentativeBackup,
        Guid? ProductId, Guid? ProductVariantId,
        DateTimeOffset UpdatedAt);
```

- [ ] **Step 6: Upsert ve echo'yu bağla**

`SyncOrders` içindeki güncelleme dalına (`current.IsTentativeBackup = …` satırının altına):

```csharp
                current.ProductId = item.ProductId;
                current.ProductVariantId = item.ProductVariantId;
```

Ekleme dalına (`IsTentativeBackup = item.IsTentativeBackup,` satırının altına):

```csharp
                    ProductId = item.ProductId,
                    ProductVariantId = item.ProductVariantId,
```

Echo projeksiyonunda (`o.IsShippingFee, o.IsBackupPromoted, o.IsTentativeBackup,`
satırının altına):

```csharp
                o.ProductId, o.ProductVariantId,
```

- [ ] **Step 7: İstemci sözleşmesini genişlet**

`OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs` — `SyncOrderItem`
kaydının sonuna:

```csharp
        bool IsTentativeBackup,
        System.Guid? ProductId = null,
        System.Guid? ProductVariantId = null);
```

Varsayılan değerler sayesinde mevcut çağrı yeri
(`OrderDeck.App/Services/Sync/SessionOrderSyncService.cs`) **değişmeden derlenir**;
WPF alanları Faz 1b'nin WPF planında doldurulacak. `SyncedOrderDto`'ya dokunulmaz —
JSON'da fazladan gelen alanlar sessizce yok sayılır.

- [ ] **Step 8: Migration üret**

```bash
dotnet ef migrations add AddOrderProductLink \
  --project OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj \
  --output-dir Data/Migrations
```

`Up()` içinde yalnız iki nullable `uniqueidentifier` kolonu ve bir indeks
olmalı — **hiçbir `AddForeignKey` çağrısı olmamalı**. Varsa fluent config yanlış.

- [ ] **Step 9: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~OrderProductLinkSyncTests`
Expected: PASS (3 test).

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: hepsi PASS.

- [ ] **Step 10: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/Order.cs \
        OrderDeck.LicenseServer/Data/LicenseDbContext.cs \
        OrderDeck.LicenseServer/Data/Migrations \
        OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs \
        OrderDeck.Licensing/Api/Models/SessionOrderSyncDtos.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/OrderProductLinkSyncTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): siparişe ürün/varyant bağı (FK'sız)

Sert FK, silinmiş ürüne referans veren tek siparişte tüm senkron paketini
500'e düşürüp WPF outbox'ını kilitlerdi.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 5: `StockLedgerWriter` — mutabakatı veritabanına bağla

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Stock/StockLedgerWriter.cs`
- Modify: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs:123` civarı (DI)
- Test: `OrderDeck.LicenseServer.Tests/Services/Stock/StockLedgerWriterTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Stock/StockLedgerWriterTests.cs`:

```csharp
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Stock;

/// <summary>
/// Yazıcıyı gerçek senkron ucundan sürer — mutabakatın saf birim testleri
/// Task 3'te; buradaki soru "veritabanına doğru satır düşüyor mu".
/// </summary>
public class StockLedgerWriterTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public StockLedgerWriterTests(ApiFactory factory) => _factory = factory;

    private sealed record Seed(HttpClient Client, Guid LicenseId, Guid ProductId, Guid VariantId);

    private async Task<Seed> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            LicenseKey = "LDK-LEDG-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Code = "A1", Name = "Tişört", DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            Axis1Value = "M", Axis1Code = "M", VariantCode = "A1-M",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProductVariants.Add(variant);

        await db.SaveChangesAsync();
        return new Seed(client, license.Id, product.Id, variant.Id);
    }

    private static object Payload(
        Guid orderId, Guid? productId, Guid? variantId,
        bool cancelled = false, bool shippingFee = false, bool tentative = false) => new
    {
        id = orderId,
        sessionId = (Guid?)null,
        customerId = Guid.NewGuid().ToString("N"),
        platform = "youtube",
        username = "izleyici",
        displayName = (string?)null,
        messageText = "A1 M",
        code = "A1",
        price = 100m,
        addedAt = DateTimeOffset.UtcNow,
        printedAt = (DateTimeOffset?)null,
        cancelledAt = cancelled ? DateTimeOffset.UtcNow : (DateTimeOffset?)null,
        cancelReason = cancelled ? "vazgeçti" : null,
        isShippingFee = shippingFee,
        isBackupPromoted = false,
        isTentativeBackup = tentative,
        productId,
        productVariantId = variantId
    };

    private Task<HttpResponseMessage> SyncAsync(Seed s, params object[] orders)
        => s.Client.PostAsJsonAsync($"/api/v1/licenses/{s.LicenseId}/orders/sync", new { orders });

    private async Task<List<StockMovement>> MovementsAsync(Guid licenseId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        return await db.StockMovements
            .Where(m => m.LicenseId == licenseId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync();
    }

    [Fact]
    public async Task Sale_writes_a_minus_one_movement()
    {
        var s = await SeedAsync();
        (await SyncAsync(s, Payload(Guid.NewGuid(), s.ProductId, s.VariantId)))
            .EnsureSuccessStatusCode();

        var movements = await MovementsAsync(s.LicenseId);
        movements.Should().ContainSingle();
        movements[0].Quantity.Should().Be(-1);
        movements[0].Reason.Should().Be(StockMovementReason.Sale);
        movements[0].ProductVariantId.Should().Be(s.VariantId);
    }

    [Fact]
    public async Task Repushing_the_same_order_writes_nothing_new()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();

        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId))).EnsureSuccessStatusCode();
        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId))).EnsureSuccessStatusCode();

        (await MovementsAsync(s.LicenseId)).Should().ContainSingle();
    }

    [Fact]
    public async Task Cancelling_writes_a_reversing_movement_and_keeps_the_original()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();

        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId))).EnsureSuccessStatusCode();
        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId, cancelled: true)))
            .EnsureSuccessStatusCode();

        var movements = await MovementsAsync(s.LicenseId);
        movements.Should().HaveCount(2);
        movements.Sum(m => m.Quantity).Should().Be(0);
        movements.Should().Contain(m => m.Reason == StockMovementReason.CancelReturn
                                        && m.Quantity == 1);
    }

    [Fact]
    public async Task Shipping_fee_row_writes_no_movement()
    {
        var s = await SeedAsync();
        (await SyncAsync(s, Payload(Guid.NewGuid(), s.ProductId, s.VariantId, shippingFee: true)))
            .EnsureSuccessStatusCode();

        (await MovementsAsync(s.LicenseId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Tentative_backup_writes_no_movement_until_promoted()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();

        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId, tentative: true)))
            .EnsureSuccessStatusCode();
        (await MovementsAsync(s.LicenseId)).Should().BeEmpty();

        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId))).EnsureSuccessStatusCode();
        (await MovementsAsync(s.LicenseId)).Should().ContainSingle(m => m.Quantity == -1);
    }

    [Fact]
    public async Task Unknown_product_is_skipped_without_failing_the_batch()
    {
        var s = await SeedAsync();
        var resp = await SyncAsync(s, Payload(Guid.NewGuid(), Guid.NewGuid(), null));

        resp.EnsureSuccessStatusCode();
        (await MovementsAsync(s.LicenseId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Unknown_variant_falls_back_to_product_level_deduction()
    {
        var s = await SeedAsync();
        (await SyncAsync(s, Payload(Guid.NewGuid(), s.ProductId, Guid.NewGuid())))
            .EnsureSuccessStatusCode();

        var movements = await MovementsAsync(s.LicenseId);
        movements.Should().ContainSingle();
        movements[0].ProductVariantId.Should().BeNull();
        movements[0].ProductId.Should().Be(s.ProductId);
    }

    [Fact]
    public async Task Movement_uses_order_added_at_as_occurred_at_and_now_as_created_at()
    {
        var s = await SeedAsync();
        var backdated = DateTimeOffset.UtcNow.AddHours(-6);
        var payload = new
        {
            id = Guid.NewGuid(),
            sessionId = (Guid?)null,
            customerId = Guid.NewGuid().ToString("N"),
            platform = "youtube",
            username = "izleyici",
            displayName = (string?)null,
            messageText = "A1 M",
            code = "A1",
            price = 100m,
            addedAt = backdated,
            printedAt = (DateTimeOffset?)null,
            cancelledAt = (DateTimeOffset?)null,
            cancelReason = (string?)null,
            isShippingFee = false,
            isBackupPromoted = false,
            isTentativeBackup = false,
            productId = s.ProductId,
            productVariantId = (Guid?)s.VariantId
        };

        (await SyncAsync(s, payload)).EnsureSuccessStatusCode();

        var movement = (await MovementsAsync(s.LicenseId)).Single();
        movement.OccurredAt.Should().BeCloseTo(backdated, TimeSpan.FromSeconds(2));
        movement.CreatedAt.Should().BeAfter(backdated.AddHours(1));
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockLedgerWriterTests`
Expected: FAIL — hiç hareket yazılmıyor.

- [ ] **Step 3: Yazıcıyı yaz**

`OrderDeck.LicenseServer/Services/Stock/StockLedgerWriter.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Stock;

/// <summary>Yazıcıya giren tek sipariş: mutabakat durumu + iş zamanı.</summary>
public sealed record LedgerOrderInput(LedgerOrderState State, DateTimeOffset OccurredAt);

/// <summary>
/// <see cref="StockLedgerReconciler"/>'ı veritabanına bağlar: mevcut hareketleri
/// okur, katalog kimliklerini doğrular, farkları hareket satırlarına çevirir.
///
/// <b>SaveChanges ÇAĞIRMAZ.</b> Çağıran (sipariş senkron ucu) siparişleri ve
/// hareketleri tek <c>SaveChanges</c>'te yazar — böylece "sipariş kaydedildi ama
/// hareket kaydedilmedi" diye bir ara durum oluşmaz.
/// </summary>
public sealed class StockLedgerWriter
{
    private readonly LicenseDbContext _db;
    private readonly ILogger<StockLedgerWriter> _log;

    public StockLedgerWriter(LicenseDbContext db, ILogger<StockLedgerWriter> log)
    {
        _db = db;
        _log = log;
    }

    public async Task ApplyAsync(
        Guid licenseId,
        IReadOnlyList<LedgerOrderInput> orders,
        DateTimeOffset now,
        CancellationToken ct)
    {
        if (orders.Count == 0) return;

        var orderIds = orders.Select(o => o.State.OrderId).Distinct().ToList();

        // Bu siparişlerin bugüne kadarki hareketleri, sipariş+anahtar bazında
        // TOPLANMIŞ hâlde. Toplam, mutabakatın tek girdisi.
        var existingRows = await _db.StockMovements
            .Where(m => m.LicenseId == licenseId
                        && m.OrderId != null
                        && orderIds.Contains(m.OrderId!.Value))
            .Select(m => new { m.OrderId, m.ProductId, m.ProductVariantId, m.Quantity })
            .ToListAsync(ct);

        var existingByOrder = existingRows
            .GroupBy(r => r.OrderId!.Value)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyDictionary<StockKey, int>)g
                    .GroupBy(r => new StockKey(r.ProductId, r.ProductVariantId))
                    .ToDictionary(x => x.Key, x => x.Sum(r => r.Quantity)));

        // Katalog kimliklerini doğrula. StockMovement GERÇEK FK taşıyor; var
        // olmayan bir id yazmaya kalkarsak tüm paket 500 olur.
        var productIds = orders
            .Select(o => o.State.ProductId).Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToList();
        var knownProducts = productIds.Count == 0
            ? new HashSet<Guid>()
            : (await _db.Products
                .Where(p => p.LicenseId == licenseId && productIds.Contains(p.Id))
                .Select(p => p.Id)
                .ToListAsync(ct)).ToHashSet();

        var variantIds = orders
            .Select(o => o.State.ProductVariantId).Where(id => id.HasValue).Select(id => id!.Value)
            .Distinct().ToList();
        var knownVariants = variantIds.Count == 0
            ? new Dictionary<Guid, Guid>()
            : await _db.ProductVariants
                .Where(v => v.LicenseId == licenseId && variantIds.Contains(v.Id))
                .ToDictionaryAsync(v => v.Id, v => v.ProductId, ct);

        foreach (var input in orders)
        {
            var state = Sanitize(input.State, licenseId, knownProducts, knownVariants);

            var existing = existingByOrder.TryGetValue(state.OrderId, out var e)
                ? e
                : new Dictionary<StockKey, int>();

            foreach (var delta in StockLedgerReconciler.Reconcile(state, existing))
            {
                _db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    LicenseId = licenseId,
                    ProductId = delta.Key.ProductId,
                    ProductVariantId = delta.Key.ProductVariantId,
                    Quantity = delta.QuantityDelta,
                    // İşaret gerekçeyi belirler: eksiye giden düşüm satıştır,
                    // artıya dönen her şey iptal/iadedir.
                    Reason = delta.QuantityDelta < 0
                        ? StockMovementReason.Sale
                        : StockMovementReason.CancelReturn,
                    OrderId = state.OrderId,
                    OccurredAt = input.OccurredAt,
                    CreatedAt = now,
                });
            }
        }
    }

    /// <summary>
    /// Katalogda bulunmayan kimlikleri eler. Bilinmeyen ürün → hiç hareket
    /// (satış yine geçerli, kart "tanımlı değil" der). Bilinmeyen ya da başka
    /// ürüne ait varyant → ürün seviyesine düşülür; spec zaten ürün seviyesi
    /// düşümü meşru sayıyor, burada varyant tahmin etmektense atfetmemeyi
    /// seçiyoruz.
    /// </summary>
    private LedgerOrderState Sanitize(
        LedgerOrderState state,
        Guid licenseId,
        HashSet<Guid> knownProducts,
        Dictionary<Guid, Guid> knownVariants)
    {
        if (state.ProductId is null) return state;

        if (!knownProducts.Contains(state.ProductId.Value))
        {
            _log.LogWarning(
                "Stok hareketi atlandı: bilinmeyen ürün {ProductId} (license={LicenseId}, order={OrderId})",
                state.ProductId, licenseId, state.OrderId);
            return state with { ProductId = null, ProductVariantId = null };
        }

        if (state.ProductVariantId is null) return state;

        if (!knownVariants.TryGetValue(state.ProductVariantId.Value, out var owner)
            || owner != state.ProductId.Value)
        {
            _log.LogWarning(
                "Varyant çözülemedi, ürün seviyesine düşülüyor: {VariantId} (license={LicenseId}, order={OrderId})",
                state.ProductVariantId, licenseId, state.OrderId);
            return state with { ProductVariantId = null };
        }

        return state;
    }
}
```

- [ ] **Step 4: DI'ya kaydet**

`OrderDeck.LicenseServer/Program.cs` — `builder.Services.AddScoped<IntakeFormService>();`
satırının altına:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Stock.StockLedgerWriter>();
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Stock.StockBalanceService>();
```

(`StockBalanceService` Task 6'da yazılıyor; bu satırı **Task 6 bittikten sonra**
ekleyin, yoksa derleme kırılır. Task 5'te yalnız `StockLedgerWriter` satırını ekleyin.)

- [ ] **Step 5: Senkron ucuna bağla**

`LicensesSessionsSyncController.cs` — ctor'a yeni bağımlılık:

```csharp
    private readonly LicenseDbContext _db;
    private readonly INotificationSender _push;
    private readonly Services.Stock.StockLedgerWriter _ledger;
    private readonly ILogger<LicensesSessionsSyncController> _log;

    public LicensesSessionsSyncController(
        LicenseDbContext db,
        INotificationSender push,
        Services.Stock.StockLedgerWriter ledger,
        ILogger<LicensesSessionsSyncController> log)
    {
        _db = db;
        _push = push;
        _ledger = ledger;
        _log = log;
    }
```

`SyncOrders` içinde, `foreach (var item in req.Orders)` döngüsünün **kapanışından
sonra** ve `await _db.SaveChangesAsync(ct);` satırının **hemen öncesine**:

```csharp
        // Defter, siparişlerle AYNI SaveChanges'te yazılır: "sipariş kaydedildi
        // ama stok düşmedi" ara durumu hiç oluşmasın.
        await _ledger.ApplyAsync(
            licenseId,
            req.Orders.Select(o => new Services.Stock.LedgerOrderInput(
                new Services.Stock.LedgerOrderState(
                    o.Id,
                    o.ProductId,
                    o.ProductVariantId,
                    o.IsShippingFee,
                    o.CancelledAt is not null,
                    o.IsTentativeBackup),
                o.AddedAt)).ToList(),
            now,
            ct);
```

- [ ] **Step 6: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockLedgerWriterTests`
Expected: PASS (8 test).

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: hepsi PASS.

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Stock/StockLedgerWriter.cs \
        OrderDeck.LicenseServer/Controllers/Licenses/LicensesSessionsSyncController.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Stock/StockLedgerWriterTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): sipariş senkronu deftere bağlandı

Hareketler siparişlerle aynı SaveChanges'te yazılıyor; bilinmeyen ürün
atlanıyor, bilinmeyen varyant ürün seviyesine düşüyor — paket 500 olmuyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 6: `StockBalanceService` — bakiye toplama

**Files:**
- Create: `OrderDeck.LicenseServer/Services/Stock/StockBalanceService.cs`
- Modify: `OrderDeck.LicenseServer/Program.cs` (Task 5 Step 4'teki ikinci satır)
- Test: `OrderDeck.LicenseServer.Tests/Services/Stock/StockBalanceServiceTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Services/Stock/StockBalanceServiceTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Stock;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Stock;

public class StockBalanceServiceTests
{
    private static readonly Guid License = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid OtherLicense = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static readonly Guid P1 = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid P2 = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly Guid V1 = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static LicenseDbContext NewDb() => new(
        new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StockMovement Mv(Guid licenseId, Guid productId, Guid? variantId, int qty) => new()
    {
        Id = Guid.NewGuid(),
        LicenseId = licenseId,
        ProductId = productId,
        ProductVariantId = variantId,
        Quantity = qty,
        Reason = qty < 0 ? StockMovementReason.Sale : StockMovementReason.Entry,
        OccurredAt = DateTimeOffset.UtcNow,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    [Fact]
    public async Task Sums_movements_per_key()
    {
        using var db = NewDb();
        db.StockMovements.AddRange(
            Mv(License, P1, V1, 10),
            Mv(License, P1, V1, -3),
            Mv(License, P1, null, -1));
        await db.SaveChangesAsync();

        var svc = new StockBalanceService(db);
        var balances = await svc.GetAsync(License, null, default);

        balances.Should().BeEquivalentTo(new[]
        {
            new StockBalance(P1, V1, 7),
            new StockBalance(P1, null, -1),
        });
    }

    [Fact]
    public async Task Negative_balance_is_reported_not_clamped()
    {
        using var db = NewDb();
        db.StockMovements.Add(Mv(License, P1, V1, -5));
        await db.SaveChangesAsync();

        var svc = new StockBalanceService(db);
        var balances = await svc.GetAsync(License, null, default);

        balances.Single().Quantity.Should().Be(-5);
    }

    [Fact]
    public async Task Other_licenses_are_never_included()
    {
        using var db = NewDb();
        db.StockMovements.AddRange(
            Mv(License, P1, null, 4),
            Mv(OtherLicense, P1, null, 99));
        await db.SaveChangesAsync();

        var svc = new StockBalanceService(db);
        var balances = await svc.GetAsync(License, null, default);

        balances.Single().Quantity.Should().Be(4);
    }

    [Fact]
    public async Task Product_filter_narrows_the_result()
    {
        using var db = NewDb();
        db.StockMovements.AddRange(
            Mv(License, P1, null, 4),
            Mv(License, P2, null, 9));
        await db.SaveChangesAsync();

        var svc = new StockBalanceService(db);
        var balances = await svc.GetAsync(License, new[] { P2 }, default);

        balances.Should().BeEquivalentTo(new[] { new StockBalance(P2, null, 9) });
    }

    [Fact]
    public async Task Empty_ledger_returns_empty_list_not_null()
    {
        using var db = NewDb();
        var svc = new StockBalanceService(db);

        (await svc.GetAsync(License, null, default)).Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockBalanceServiceTests`
Expected: FAIL — `StockBalanceService` tipi yok.

- [ ] **Step 3: Servisi yaz**

`OrderDeck.LicenseServer/Services/Stock/StockBalanceService.cs`:

```csharp
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;

namespace OrderDeck.LicenseServer.Services.Stock;

/// <summary>Bir anahtarın güncel bakiyesi. Negatif olabilir — bu bir hata değil.</summary>
public sealed record StockBalance(Guid ProductId, Guid? ProductVariantId, int Quantity);

/// <summary>
/// Defteri anahtar bazında toplar. Ayrı bir bakiye tablosu <b>yok</b>: iki yazan
/// yol (yayın senkronu + panel girişi) bir bakiye kolonunu kilitsiz güncelleyemez,
/// kilit de yayın hızını vurur. Toplam ise çakışmasız.
/// </summary>
public sealed class StockBalanceService
{
    private readonly LicenseDbContext _db;
    public StockBalanceService(LicenseDbContext db) => _db = db;

    /// <param name="productIds">Null veya boş ise lisansın tamamı.</param>
    public async Task<IReadOnlyList<StockBalance>> GetAsync(
        Guid licenseId,
        IReadOnlyCollection<Guid>? productIds,
        CancellationToken ct)
    {
        var q = _db.StockMovements.Where(m => m.LicenseId == licenseId);

        if (productIds is { Count: > 0 })
        {
            var ids = productIds.ToList();
            q = q.Where(m => ids.Contains(m.ProductId));
        }

        return await q
            .GroupBy(m => new { m.ProductId, m.ProductVariantId })
            .Select(g => new StockBalance(
                g.Key.ProductId, g.Key.ProductVariantId, g.Sum(m => m.Quantity)))
            .ToListAsync(ct);
    }
}
```

- [ ] **Step 4: DI satırını ekle**

`Program.cs` — Task 5 Step 4'te ertelenen satır:

```csharp
        builder.Services.AddScoped<OrderDeck.LicenseServer.Services.Stock.StockBalanceService>();
```

- [ ] **Step 5: Testi koştur, yeşili gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockBalanceServiceTests`
Expected: PASS (5 test).

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Stock/StockBalanceService.cs \
        OrderDeck.LicenseServer/Program.cs \
        OrderDeck.LicenseServer.Tests/Services/Stock/StockBalanceServiceTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): StockBalanceService — defterden bakiye toplama

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 7: `PanelStockController` — giriş, sayım, bakiye, hareket

Bu uç olmadan defter yalnız satışla besleniyor ve her bakiye negatif çıkıyor.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Panel/PanelStockController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStockControllerTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStockControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelStockControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelStockControllerTests(ApiFactory factory) => _factory = factory;

    private sealed record Seed(HttpClient Client, Guid LicenseId, Guid ProductId, Guid VariantId);

    private async Task<Seed> SeedAsync()
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-PSTK-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Code = "A1", Name = "Tişört", DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            Axis1Value = "M", Axis1Code = "M", VariantCode = "A1-M",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProductVariants.Add(variant);

        await db.SaveChangesAsync();
        return new Seed(client, license.Id, product.Id, variant.Id);
    }

    private static async Task<string?> DetailAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("detail", out var d) ? d.GetString() : null;
    }

    private static async Task<int> BalanceOfAsync(HttpClient client, Guid productId, Guid? variantId)
    {
        var resp = await client.GetAsync($"/api/panel/stock/balances?productId={productId}");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        foreach (var row in doc.RootElement.EnumerateArray())
        {
            var rowVariant = row.GetProperty("productVariantId");
            var matches = variantId is null
                ? rowVariant.ValueKind == JsonValueKind.Null
                : rowVariant.ValueKind != JsonValueKind.Null
                  && rowVariant.GetGuid() == variantId.Value;
            if (matches) return row.GetProperty("quantity").GetInt32();
        }
        return 0;
    }

    [Fact]
    public async Task Entry_increases_the_balance()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = s.VariantId, quantity = 12, note = "irsaliye 4412" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(s.Client, s.ProductId, s.VariantId)).Should().Be(12);
    }

    [Fact]
    public async Task Entry_rejects_zero_and_negative_quantity()
    {
        var s = await SeedAsync();

        foreach (var qty in new[] { 0, -3 })
        {
            var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
                new { productId = s.ProductId, productVariantId = (Guid?)null, quantity = qty });
            resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task Entry_rejects_a_product_from_another_license()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = Guid.NewGuid(), productVariantId = (Guid?)null, quantity = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Entry_rejects_a_variant_that_belongs_to_another_product()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = Guid.NewGuid(), quantity = 1 });

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DetailAsync(resp)).Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Count_writes_the_difference_only()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = s.VariantId, quantity = 10 });

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/counts",
            new { productId = s.ProductId, productVariantId = s.VariantId, countedQuantity = 7, note = "sayım" });

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        (await BalanceOfAsync(s.Client, s.ProductId, s.VariantId)).Should().Be(7);

        var movements = await s.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/panel/stock/movements?productId={s.ProductId}");
        movements!.Should().HaveCount(2);
        movements.Should().Contain(m => m.GetProperty("quantity").GetInt32() == -3);
    }

    [Fact]
    public async Task Count_matching_the_ledger_writes_no_movement()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = s.VariantId, quantity = 5 });

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/counts",
            new { productId = s.ProductId, productVariantId = s.VariantId, countedQuantity = 5 });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var movements = await s.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/panel/stock/movements?productId={s.ProductId}");
        movements!.Should().ContainSingle();
    }

    [Fact]
    public async Task Count_rejects_negative_counted_quantity()
    {
        var s = await SeedAsync();

        var resp = await s.Client.PostAsJsonAsync("/api/panel/stock/counts",
            new { productId = s.ProductId, productVariantId = (Guid?)null, countedQuantity = -1 });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Movements_are_returned_newest_first()
    {
        var s = await SeedAsync();

        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = (Guid?)null, quantity = 1, note = "ilk" });
        await s.Client.PostAsJsonAsync("/api/panel/stock/entries",
            new { productId = s.ProductId, productVariantId = (Guid?)null, quantity = 2, note = "ikinci" });

        var movements = await s.Client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/panel/stock/movements?productId={s.ProductId}");

        movements!.Should().HaveCount(2);
        movements[0].GetProperty("note").GetString().Should().Be("ikinci");
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelStockControllerTests`
Expected: FAIL — 404 (uç yok).

- [ ] **Step 3: Controller'ı yaz**

`OrderDeck.LicenseServer/Controllers/Panel/PanelStockController.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Stock;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Panelin stok yüzü: bakiye okuma, mal kabul girişi, sayım düzeltmesi ve
/// hareket dökümü.
///
/// Tümü <see cref="AllowStockStaffAttribute"/> taşır — stok elemanının işi
/// tam olarak budur. Öznitelik yalnız metotlara konabiliyor (derleyici
/// zorluyor), yani yarın bu sınıfa eklenen bir uç kendiliğinden açık gelmez.
/// </summary>
[ApiController]
[Route("api/panel/stock")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelStockController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly StockBalanceService _balances;

    public PanelStockController(LicenseDbContext db, StockBalanceService balances)
    {
        _db = db;
        _balances = balances;
    }

    public sealed record StockBalanceDto(Guid ProductId, Guid? ProductVariantId, int Quantity);

    public sealed record StockMovementDto(
        Guid Id, Guid ProductId, Guid? ProductVariantId,
        int Quantity, int Reason, Guid? OrderId, string? Note,
        DateTimeOffset OccurredAt, DateTimeOffset CreatedAt);

    public sealed record CreateEntryRequest(
        Guid ProductId,
        Guid? ProductVariantId,
        [Range(1, 100_000)] int Quantity,
        [MaxLength(CatalogLimits.MovementNote)] string? Note);

    public sealed record CreateCountRequest(
        Guid ProductId,
        Guid? ProductVariantId,
        [Range(0, 1_000_000)] int CountedQuantity,
        [MaxLength(CatalogLimits.MovementNote)] string? Note);

    /// <summary>
    /// Bakiyeler. <c>productId</c> birden çok kez verilebilir; hiç verilmezse
    /// lisansın tamamı döner.
    /// </summary>
    [HttpGet("balances")]
    [AllowStockStaff]
    public async Task<IActionResult> Balances(
        [FromQuery] Guid[]? productId, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NotFound();

        var rows = await _balances.GetAsync(licenseId.Value, productId, ct);

        return Ok(rows
            .Select(b => new StockBalanceDto(b.ProductId, b.ProductVariantId, b.Quantity))
            .ToList());
    }

    /// <summary>Hareket dökümü — en yeni önce.</summary>
    [HttpGet("movements")]
    [AllowStockStaff]
    public async Task<IActionResult> Movements(
        [FromQuery] Guid productId,
        [FromQuery] Guid? productVariantId,
        [FromQuery] int take = 100,
        CancellationToken ct = default)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NotFound();

        take = Math.Clamp(take, 1, 500);

        var q = _db.StockMovements
            .Where(m => m.LicenseId == licenseId.Value && m.ProductId == productId);
        if (productVariantId is not null)
            q = q.Where(m => m.ProductVariantId == productVariantId);

        var rows = await q
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(take)
            .Select(m => new StockMovementDto(
                m.Id, m.ProductId, m.ProductVariantId,
                m.Quantity, (int)m.Reason, m.OrderId, m.Note,
                m.OccurredAt, m.CreatedAt))
            .ToListAsync(ct);

        return Ok(rows);
    }

    /// <summary>Mal kabul / stok girişi.</summary>
    [HttpPost("entries")]
    [AllowStockStaff]
    public async Task<IActionResult> CreateEntry(
        [FromBody] CreateEntryRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NotFound();

        var target = await ResolveTargetAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct);
        if (target is null) return NotFound(TargetProblem());

        var now = DateTimeOffset.UtcNow;
        _db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = req.ProductId,
            ProductVariantId = req.ProductVariantId,
            Quantity = req.Quantity,
            Reason = StockMovementReason.Entry,
            Note = req.Note,
            OccurredAt = now,
            CreatedAt = now,
            CreatedByOperatorId = User.GetOperatorId(),
        });
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created,
            await CurrentBalanceAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct));
    }

    /// <summary>
    /// Sayım. İstemci <b>sayılan adedi</b> gönderir, sunucu farkı yazar.
    ///
    /// Neden fark: aradaki saniyelerde bir satış düşmüş olabilir. İstemci farkı
    /// hesaplasaydı o satışı sessizce ezerdi. Fark sıfırsa hiçbir satır yazılmaz
    /// — defter gürültüyle şişmesin.
    /// </summary>
    [HttpPost("counts")]
    [AllowStockStaff]
    public async Task<IActionResult> CreateCount(
        [FromBody] CreateCountRequest req, CancellationToken ct)
    {
        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return NotFound();

        var target = await ResolveTargetAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct);
        if (target is null) return NotFound(TargetProblem());

        var current = await CurrentBalanceAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct);
        var delta = req.CountedQuantity - current.Quantity;

        if (delta == 0) return Ok(current);

        var now = DateTimeOffset.UtcNow;
        _db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId.Value,
            ProductId = req.ProductId,
            ProductVariantId = req.ProductVariantId,
            Quantity = delta,
            Reason = StockMovementReason.CountAdjustment,
            Note = req.Note,
            OccurredAt = now,
            CreatedAt = now,
            CreatedByOperatorId = User.GetOperatorId(),
        });
        await _db.SaveChangesAsync(ct);

        return StatusCode(StatusCodes.Status201Created,
            await CurrentBalanceAsync(licenseId.Value, req.ProductId, req.ProductVariantId, ct));
    }

    // ─── yardımcılar ──────────────────────────────────────────────────

    private ProblemDetails TargetProblem() => new()
    {
        Title = "stock-target-not-found",
        Detail = "Ürün veya varyant bulunamadı.",
        Status = StatusCodes.Status404NotFound,
    };

    /// <summary>
    /// Ürünün (ve verilmişse varyantın) bu lisansa ait olduğunu doğrular.
    /// Varyantın <c>ProductId</c>'si de kontrol edilir: başka ürünün varyantına
    /// giriş yapmak sessiz veri bozulmasıdır.
    /// </summary>
    private async Task<bool?> ResolveTargetAsync(
        Guid licenseId, Guid productId, Guid? variantId, CancellationToken ct)
    {
        var productExists = await _db.Products
            .AnyAsync(p => p.Id == productId && p.LicenseId == licenseId, ct);
        if (!productExists) return null;

        if (variantId is null) return true;

        var variantOk = await _db.ProductVariants
            .AnyAsync(v => v.Id == variantId
                        && v.LicenseId == licenseId
                        && v.ProductId == productId, ct);
        return variantOk ? true : null;
    }

    private async Task<StockBalanceDto> CurrentBalanceAsync(
        Guid licenseId, Guid productId, Guid? variantId, CancellationToken ct)
    {
        var rows = await _balances.GetAsync(licenseId, new[] { productId }, ct);
        var match = rows.FirstOrDefault(b => b.ProductVariantId == variantId);
        return new StockBalanceDto(productId, variantId, match?.Quantity ?? 0);
    }

    /// <summary>
    /// Müşterinin aktif lisansı. Panel controller'larının ortak kalıbı; ilk
    /// verilen (en eski) lisans seçilir.
    /// </summary>
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

- [ ] **Step 4: Stok elemanı uç envanterini genişlet**

`OrderDeck.LicenseServer.Tests/Auth/StockStaffEndpointInventoryTests.cs`,
`ExpectedOpenEndpoints` dizisinin **sonuna** (fotoğraf satırlarından sonra):

```csharp

        "PanelStockController.Balances",
        "PanelStockController.Movements",
        "PanelStockController.CreateEntry",
        "PanelStockController.CreateCount",
```

**Neden bu adım var:** bu test stok elemanına açık uç kümesini bilerek *tam
liste* olarak sabitliyor — `[AllowStockStaff]` yazan herkes testi kırar ve
kararını buraya yazarak kayda geçirmek zorunda kalır. Yani bu adım atlanırsa
Task 7 kırmızı bırakır. Eklerken spec kuralını doğrula: stok elemanı "ürün
kartı açar, stok girer, etiket basar; **müşteri, sipariş, ödeme ve ciro
bilgilerini göremez**". Dördü de yalnız stok verisi döndürüyor — `Movements`
ucu hareketin ürün/miktar/sebep alanlarını döner, satışı yapan müşteriyi
değil. Bu kural bozulacak olursa uç `[AllowStockStaff]` almamalı.

- [ ] **Step 5: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelStockControllerTests`
Expected: PASS (8 test).

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockStaffEndpointInventoryTests`
Expected: PASS — açık uç kümesi dört yeni satırla eşleşiyor.

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelControllerConventionTests`
Expected: PASS — yeni controller `[ApiController]` + `[Authorize(Bearer-Customer)]` taşıyor.

- [ ] **Step 6: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelStockController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelStockControllerTests.cs \
        OrderDeck.LicenseServer.Tests/Auth/StockStaffEndpointInventoryTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): PanelStockController — giriş, sayım, bakiye, hareket

Sayımda istemci sayılan adedi gönderiyor, farkı sunucu yazıyor: aradaki
saniyede düşen satış sessizce ezilmesin.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 8: WPF katalog çekme ucu (tam anlık görüntü)

**Neden artımlı değil:** panelden ürün ve varyant **silinebiliyor**. Artımlı bir
imleç silmeleri hiç göremez — WPF'te hayalet satır kalır ve o satır yayında
yanlış ürüne eşleşir. Katalog küçük olduğu için tam sayfalı anlık görüntü hem
basit hem kendini onarıcı: WPF sayfaları toplar, yerel kopyayı takas eder.

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesWpfCatalogPullControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesWpfCatalogPullControllerTests(ApiFactory factory) => _factory = factory;

    private async Task<(HttpClient Client, Guid LicenseId)> SeedAsync(
        int productCount, bool archiveFirst = false)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-CATP-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        for (var i = 0; i < productCount; i++)
        {
            var product = new Product
            {
                Id = Guid.NewGuid(), LicenseId = license.Id,
                Code = "A" + (i + 1), Name = "Ürün " + (i + 1),
                DefaultPrice = 100m + i,
                IsArchived = archiveFirst && i == 0,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.Products.Add(product);
            db.ProductVariants.Add(new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                Axis1Value = "M", Axis1Code = "M", VariantCode = product.Code + "-M",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return (client, license.Id);
    }

    [Fact]
    public async Task Returns_products_with_their_variants()
    {
        var (client, licenseId) = await SeedAsync(1);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows!.Should().ContainSingle();
        rows[0].GetProperty("code").GetString().Should().Be("A1");
        rows[0].GetProperty("nameSearch").GetString().Should().Be("ÜRÜN 1".Replace("Ü", "U"));
        rows[0].GetProperty("variants").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Archived_products_are_excluded()
    {
        var (client, licenseId) = await SeedAsync(2, archiveFirst: true);

        var rows = await client.GetFromJsonAsync<List<JsonElement>>(
            $"/api/v1/licenses/{licenseId}/catalog/products");

        rows!.Should().ContainSingle();
        rows[0].GetProperty("code").GetString().Should().Be("A2");
    }

    [Fact]
    public async Task Pages_through_the_whole_catalog_with_the_after_cursor()
    {
        var (client, licenseId) = await SeedAsync(5);

        var seen = new List<Guid>();
        Guid? after = null;
        while (true)
        {
            var url = $"/api/v1/licenses/{licenseId}/catalog/products?take=2"
                      + (after is null ? "" : $"&after={after}");
            var page = await client.GetFromJsonAsync<List<JsonElement>>(url);
            if (page!.Count == 0) break;
            seen.AddRange(page.Select(p => p.GetProperty("id").GetGuid()));
            after = seen[^1];
        }

        seen.Should().HaveCount(5);
        seen.Should().OnlyHaveUniqueItems();
        seen.Should().BeInAscendingOrder();
    }

    [Fact]
    public async Task Another_customers_license_is_not_readable()
    {
        var (client, _) = await SeedAsync(1);
        var (_, otherLicenseId) = await SeedAsync(1);

        var resp = await client.GetAsync($"/api/v1/licenses/{otherLicenseId}/catalog/products");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~LicensesWpfCatalogPullControllerTests`
Expected: FAIL — 404.

- [ ] **Step 3: Controller'ı yaz**

`OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF'in yerel katalog kopyasını beslediği uç. <b>Tam anlık görüntü</b>,
/// artımlı değil.
///
/// Neden artımlı değil: panelden ürün ve varyant silinebiliyor; <c>since</c>
/// imleci silmeleri hiç göremez ve WPF'te hayalet satır bırakır — o satır da
/// yayında yanlış ürüne eşleşir. Katalog lisans başına yüzler mertebesinde
/// olduğu için tam sayfalı çekme hem ucuz hem kendini onarıcı.
///
/// Sayfalama <b>Id üstünde keyset</b>: <c>OrderBy(Id).Where(Id > after)</c>.
/// Offset kullanılmıyor — sayfalar arasında araya giren bir kayıt satır
/// atlatırdı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/catalog")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfCatalogPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    public LicensesWpfCatalogPullController(LicenseDbContext db) => _db = db;

    public sealed record CatalogVariantDto(
        Guid Id,
        string? Axis1Value, string? Axis1Code,
        string? Axis2Value, string? Axis2Code,
        string VariantCode, string? Barcode,
        bool IsActive);

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
        List<CatalogVariantDto> Variants);

    /// <param name="after">Son alınan ürünün Id'si; ilk sayfada verilmez.</param>
    /// <param name="take">Varsayılan 200, üst sınır 500.</param>
    [HttpGet("products")]
    public async Task<IActionResult> Products(
        Guid licenseId,
        [FromQuery] Guid? after,
        [FromQuery] int take = 200,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 500);

        var q = _db.Products
            .Where(p => p.LicenseId == licenseId && !p.IsArchived);
        if (after is not null)
            q = q.Where(p => p.Id.CompareTo(after.Value) > 0);

        // Maliyet (Cost) bilerek dışarda: WPF'in eşleştirme ve kart gösterimi
        // için gerekmiyor, kâr hesabı panelde yapılıyor.
        var rows = await q
            .OrderBy(p => p.Id)
            .Take(take)
            .Select(p => new CatalogProductDto(
                p.Id, p.CategoryId, p.Code, p.Name, p.NameSearch,
                p.DefaultPrice, p.ShelfLocation,
                p.Axis1Name, p.Axis1Role == null ? null : (int?)p.Axis1Role,
                p.Axis2Name, p.Axis2Role == null ? null : (int?)p.Axis2Role,
                p.UpdatedAt,
                p.Variants
                    .OrderBy(v => v.VariantCode)
                    .Select(v => new CatalogVariantDto(
                        v.Id, v.Axis1Value, v.Axis1Code,
                        v.Axis2Value, v.Axis2Code,
                        v.VariantCode, v.Barcode, v.IsActive))
                    .ToList()))
            .ToListAsync(ct);

        return Ok(rows);
    }
}
```

- [ ] **Step 4: Testi koştur, yeşili gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~LicensesWpfCatalogPullControllerTests`
Expected: PASS (4 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfCatalogPullController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfCatalogPullControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): WPF katalog çekme ucu — tam anlık görüntü

Artımlı imleç silmeleri göremez ve WPF'te hayalet ürün bırakırdı; katalog
küçük olduğu için keyset sayfalı tam çekme hem basit hem kendini onarıcı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 9: WPF stok çekme ucu — **bakiye anlık görüntüsü**, artımlı imleçle

**Files:**
- Create: `OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfStockPullController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfStockPullControllerTests.cs`

**Bu ucun sözleşmesi — dikkatle okuyun, "hareket listesi" DEĞİL:** Spec
(`2026-08-07-stok-sistemi-design.md`, "WPF'e hareket değil, bakiye anlık
görüntüsü iner") gereği WPF'e ham defter satırları **inmez**. WPF her
ürün/varyant için **tek satır** tutar ve ekranda `sunucu bakiyesi − yerel
bekleyen hareketler` gösterir. Bütün hareket geçmişini indirmek WPF'in yerel
tablosunu sonsuza kadar büyütürdü ve WPF'e defteri kendi toplama işini
yükleyerek ikinci bir toplama uygulaması doğururdu.

**Yine de imleç hareketler üstünde koşar,** çünkü toplanabilir bir "bakiye
satırı" yok — bakiye bir toplamdır, `UpdatedAt`'i olan bir kaydı yoktur. Akış:
imleçten sonraki hareketler sayfalanır → **hangi anahtarların değiştiği**
bulunur → o anahtarların bakiyesi **sorgu anında yeniden hesaplanıp** mutlak
değer olarak gönderilir.

**Bunun kritik sonucu:** dönen miktar bir *fark* değil, o anahtarın **o andaki
tam bakiyesidir**. Bir sayfa bir anahtarın hareketlerini ortasından kesse bile
gönderilen sayı doğrudur; anahtar sonraki sayfada tekrar görünür ve WPF aynı
değeri üstüne yazar. WPF tarafı bu yüzden **upsert** eder, toplamaz.

**Dikkat — eşitlik tuzağı:** Tek senkron paketindeki bütün hareketler **aynı**
`CreatedAt` damgasını taşır (`now` bir kez okunuyor). Bu yüzden imleç yalnız
`CreatedAt > since` olsaydı, `take` sınırı bir eşitlik kümesinin ortasından
keserse kalan satırlar bir daha **hiç** gelmezdi — ve onlara ait anahtarlar
sessizce eski bakiyede kalırdı. İmleç bu yüzden bileşik:
`CreatedAt > since || (CreatedAt == since && Id > sinceId)`.

İmleç istemcide hareket satırından okunamayacağı için (hareket dönmüyoruz)
**yanıtın gövdesinde** `cursorCreatedAt` / `cursorId` olarak açıkça döner.

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfStockPullControllerTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Licenses;

public class LicensesWpfStockPullControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public LicensesWpfStockPullControllerTests(ApiFactory factory) => _factory = factory;

    private static readonly DateTimeOffset Stamp =
        new(2026, 8, 13, 10, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Bir ürün + <paramref name="variantCount"/> varyant açar ve her varyanta
    /// <paramref name="movementsPerVariant"/> adet −1 hareketi yazar. Hareketlerin
    /// HEPSİ aynı <c>CreatedAt</c> damgasını taşır — tek senkron paketinin
    /// gerçek davranışı budur ve eşitlik tuzağını ancak böyle sınayabiliriz.
    /// </summary>
    private async Task<(HttpClient Client, Guid LicenseId, Guid ProductId, List<Guid> VariantIds)>
        SeedAsync(int variantCount, int movementsPerVariant)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-STKP-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Code = "A1", Name = "Tişört", DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Products.Add(product);

        var variantIds = new List<Guid>();
        for (var v = 0; v < variantCount; v++)
        {
            var variant = new ProductVariant
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                Axis1Value = $"B{v}", Axis1Code = $"B{v}",
                VariantCode = $"A1-B{v}", IsActive = true,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.ProductVariants.Add(variant);
            variantIds.Add(variant.Id);

            for (var i = 0; i < movementsPerVariant; i++)
            {
                db.StockMovements.Add(new StockMovement
                {
                    Id = Guid.NewGuid(),
                    LicenseId = license.Id,
                    ProductId = product.Id,
                    ProductVariantId = variant.Id,
                    Quantity = -1,
                    Reason = StockMovementReason.Sale,
                    OccurredAt = Stamp.AddHours(-6),
                    CreatedAt = Stamp,
                });
            }
        }

        await db.SaveChangesAsync();
        return (client, license.Id, product.Id, variantIds);
    }

    private static string Url(Guid licenseId, DateTimeOffset since, Guid sinceId, int take)
        => $"/api/v1/licenses/{licenseId}/stock/balances/since"
           + $"?since={Uri.EscapeDataString(since.ToString("O"))}&sinceId={sinceId}&take={take}";

    private async Task<JsonElement> GetPageAsync(
        HttpClient client, Guid licenseId, DateTimeOffset since, Guid sinceId, int take)
        => await client.GetFromJsonAsync<JsonElement>(Url(licenseId, since, sinceId, take));

    [Fact]
    public async Task Returns_recomputed_balances_for_keys_touched_after_the_cursor()
    {
        var (client, licenseId, productId, variantIds) = await SeedAsync(1, 1);

        var page = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 100);

        var balances = page.GetProperty("balances").EnumerateArray().ToList();
        balances.Should().HaveCount(1);
        balances[0].GetProperty("productId").GetGuid().Should().Be(productId);
        balances[0].GetProperty("productVariantId").GetGuid().Should().Be(variantIds[0]);
        balances[0].GetProperty("quantity").GetInt32().Should().Be(-1);
        page.GetProperty("hasMore").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public async Task Quantity_is_the_absolute_balance_not_the_paged_delta()
    {
        // Tek anahtarda iki hareket, sayfa boyu 1: sayfa anahtarın hareketlerini
        // ortasından kesiyor. Dönen sayı yine de TAM bakiye (−2) olmalı.
        var (client, licenseId, _, _) = await SeedAsync(1, 2);

        var page = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 1);

        var balances = page.GetProperty("balances").EnumerateArray().ToList();
        balances.Should().HaveCount(1);
        balances[0].GetProperty("quantity").GetInt32().Should().Be(-2);
        page.GetProperty("hasMore").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public async Task Paging_never_loses_keys_that_share_a_created_at()
    {
        var (client, licenseId, _, variantIds) = await SeedAsync(5, 1);

        var seen = new HashSet<Guid>();
        var since = Stamp.AddMinutes(-1);
        var sinceId = Guid.Empty;

        while (true)
        {
            var page = await GetPageAsync(client, licenseId, since, sinceId, 2);
            var balances = page.GetProperty("balances").EnumerateArray().ToList();
            if (balances.Count == 0) break;

            foreach (var b in balances)
                seen.Add(b.GetProperty("productVariantId").GetGuid());

            since = page.GetProperty("cursorCreatedAt").GetDateTimeOffset();
            sinceId = page.GetProperty("cursorId").GetGuid();
        }

        seen.Should().BeEquivalentTo(variantIds);
    }

    [Fact]
    public async Task Cursor_at_the_end_returns_nothing_and_preserves_the_cursor()
    {
        var (client, licenseId, _, _) = await SeedAsync(2, 1);

        var first = await GetPageAsync(client, licenseId, Stamp.AddMinutes(-1), Guid.Empty, 100);
        var cursorCreatedAt = first.GetProperty("cursorCreatedAt").GetDateTimeOffset();
        var cursorId = first.GetProperty("cursorId").GetGuid();

        var next = await GetPageAsync(client, licenseId, cursorCreatedAt, cursorId, 100);

        next.GetProperty("balances").GetArrayLength().Should().Be(0);
        next.GetProperty("hasMore").GetBoolean().Should().BeFalse();
        // İmleç geri sarmamalı: boş sayfa istemcinin imlecini olduğu gibi döndürür.
        next.GetProperty("cursorCreatedAt").GetDateTimeOffset().Should().Be(cursorCreatedAt);
        next.GetProperty("cursorId").GetGuid().Should().Be(cursorId);
    }

    [Fact]
    public async Task Another_customers_license_is_not_readable()
    {
        var (client, _, _, _) = await SeedAsync(1, 1);
        var (_, otherLicenseId, _, _) = await SeedAsync(1, 1);

        var resp = await client.GetAsync(Url(otherLicenseId, Stamp.AddMinutes(-1), Guid.Empty, 100));

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~LicensesWpfStockPullControllerTests`
Expected: FAIL — 404.

- [ ] **Step 3: Controller'ı yaz**

`OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfStockPullController.cs`:

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Stock;

namespace OrderDeck.LicenseServer.Controllers.Licenses;

/// <summary>
/// WPF'in stok durumunu artımlı çektiği uç. <b>Ham defter satırı döndürmez</b>:
/// WPF ürün/varyant başına tek satır tutar ve ekranda
/// <c>sunucu bakiyesi − yerel bekleyen hareketler</c> gösterir. Bütün hareket
/// geçmişini indirmek WPF'in yerel tablosunu sonsuza büyütür, üstüne defteri
/// toplama işini ikinci kez uygulatırdı.
///
/// İmleç yine de <b>hareketler</b> üstünde koşar, çünkü toplanabilir bir "bakiye
/// satırı" yok — bakiye bir toplamdır, kendi <c>UpdatedAt</c>'i olan bir kaydı
/// yoktur. Akış: imleçten sonraki hareketleri sayfala → değişen anahtarları
/// bul → o anahtarların bakiyesini <b>sorgu anında yeniden hesapla</b>.
///
/// Dönen miktar bu yüzden bir fark değil, o anın <b>mutlak bakiyesidir</b>.
/// Sayfa bir anahtarın hareketlerini ortasından kesse bile gönderilen sayı
/// doğrudur; anahtar sonraki sayfada tekrar görünür. İstemci <b>upsert</b>
/// eder, toplamaz.
///
/// İmleç <see cref="Domain.StockMovement.CreatedAt"/> (sunucu yazma anı)
/// üstünde, <see cref="Domain.StockMovement.OccurredAt"/> üstünde DEĞİL:
/// çevrimdışı satılan sipariş geçmişe dönük bir <c>OccurredAt</c> ile geliyor ve
/// iş zamanı imleci onu sessizce atlardı.
///
/// İmleç <b>bileşik</b> (<c>since</c> + <c>sinceId</c>): tek senkron paketindeki
/// bütün hareketler aynı <c>CreatedAt</c> damgasını taşıyor, <c>take</c> sınırı
/// bu eşitlik kümesinin ortasından kesebilir. Yalnız zaman imleci olsaydı kesilen
/// satırların anahtarları eski bakiyede donup kalırdı.
/// </summary>
[ApiController]
[Route("api/v1/licenses/{licenseId:guid}/stock")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class LicensesWpfStockPullController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly StockBalanceService _balances;

    public LicensesWpfStockPullController(LicenseDbContext db, StockBalanceService balances)
    {
        _db = db;
        _balances = balances;
    }

    public sealed record StockBalancePullItem(
        Guid ProductId,
        Guid? ProductVariantId,
        int Quantity);

    /// <param name="Balances">Bu sayfada değişen anahtarların <b>mutlak</b> bakiyeleri.</param>
    /// <param name="CursorCreatedAt">Bir sonraki çağrıda <c>since</c> olarak gönderilecek değer.</param>
    /// <param name="CursorId">Bir sonraki çağrıda <c>sinceId</c> olarak gönderilecek değer.</param>
    /// <param name="HasMore">Sayfa dolduysa true — istemci hemen tekrar çağırmalı.</param>
    public sealed record StockBalancePullResponse(
        IReadOnlyList<StockBalancePullItem> Balances,
        DateTimeOffset CursorCreatedAt,
        Guid CursorId,
        bool HasMore);

    /// <param name="since">Son alınan sayfanın <c>cursorCreatedAt</c>'i; ilk çekmede çok eski bir tarih.</param>
    /// <param name="sinceId">Son alınan sayfanın <c>cursorId</c>'si; ilk çekmede boş GUID.</param>
    /// <param name="take">Taranacak <b>hareket</b> sayısı (dönen bakiye satırı sayısı değil). Varsayılan 500, üst sınır 1000.</param>
    [HttpGet("balances/since")]
    public async Task<IActionResult> BalancesSince(
        Guid licenseId,
        [FromQuery] DateTimeOffset since,
        [FromQuery] Guid sinceId,
        [FromQuery] int take = 500,
        CancellationToken ct = default)
    {
        var customerId = User.GetTenantCustomerId();
        var ownsLicense = await _db.Licenses
            .AnyAsync(l => l.Id == licenseId && l.CustomerId == customerId, ct);
        if (!ownsLicense) return NotFound();

        take = Math.Clamp(take, 1, 1000);

        var page = await _db.StockMovements
            .Where(m => m.LicenseId == licenseId
                        && (m.CreatedAt > since
                            || (m.CreatedAt == since && m.Id.CompareTo(sinceId) > 0)))
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Take(take)
            .Select(m => new { m.Id, m.CreatedAt, m.ProductId, m.ProductVariantId })
            .ToListAsync(ct);

        // Boş sayfa istemcinin imlecini geri sarmaz — aynen iade edilir.
        if (page.Count == 0)
            return Ok(new StockBalancePullResponse([], since, sinceId, false));

        var touched = page
            .Select(p => new StockKey(p.ProductId, p.ProductVariantId))
            .ToHashSet();
        var productIds = page.Select(p => p.ProductId).Distinct().ToList();

        // Bakiye tam olarak burada yeniden hesaplanıyor: sayfanın dışında kalan
        // hareketler de toplama dahil, yani dönen sayı mutlak ve güncel.
        var balances = (await _balances.GetAsync(licenseId, productIds, ct))
            .Where(b => touched.Contains(new StockKey(b.ProductId, b.ProductVariantId)))
            .Select(b => new StockBalancePullItem(b.ProductId, b.ProductVariantId, b.Quantity))
            .ToList();

        var last = page[^1];
        return Ok(new StockBalancePullResponse(
            balances, last.CreatedAt, last.Id, page.Count == take));
    }
}
```

- [ ] **Step 4: Testi koştur, yeşili gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~LicensesWpfStockPullControllerTests`
Expected: PASS (5 test).

- [ ] **Step 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Licenses/LicensesWpfStockPullController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Licenses/LicensesWpfStockPullControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(stok): WPF stok çekme ucu — bakiye anlık görüntüsü, bileşik imleç

WPF'e ham defter inmiyor; ürün/varyant başına mutlak bakiye iniyor, imleç
hareketlerin CreatedAt'i üstünde koşuyor. Tek pakette yazılan hareketler
aynı CreatedAt'i paylaştığı için imleç Id ile bileşik: salt zaman imleci
take sınırında kesilen anahtarları eski bakiyede dondurup bırakırdı.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Task 10: Silme korumaları + kapanış notu

`StockMovement`, ürün ve varyanta `Restrict` ile bağlı. Koruma konmazsa panelden
silme denemesi `DbUpdateException` → 500 verir; kullanıcı Türkçe bir açıklama
yerine "beklenmeyen hata" görür.

**Files:**
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs:494` civarı
- Modify: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs` (Delete)
- Modify: `docs/superpowers/specs/2026-08-07-stok-sistemi-design.md`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/StockDeleteGuardTests.cs`

- [ ] **Step 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Controllers/Panel/StockDeleteGuardTests.cs`:

```csharp
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class StockDeleteGuardTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public StockDeleteGuardTests(ApiFactory factory) => _factory = factory;

    private sealed record Seed(HttpClient Client, Guid ProductId, Guid VariantId);

    private async Task<Seed> SeedAsync(bool withMovement)
    {
        var (client, customerId, _) = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customerId,
            LicenseKey = "LDK-GARD-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30)
        };
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = license.Id,
            Code = "A1", Name = "Tişört", DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.Products.Add(product);

        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            Axis1Value = "M", Axis1Code = "M", VariantCode = "A1-M",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
        };
        db.ProductVariants.Add(variant);

        if (withMovement)
        {
            db.StockMovements.Add(new StockMovement
            {
                Id = Guid.NewGuid(), LicenseId = license.Id,
                ProductId = product.Id, ProductVariantId = variant.Id,
                Quantity = 5, Reason = StockMovementReason.Entry,
                OccurredAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
        return new Seed(client, product.Id, variant.Id);
    }

    private static async Task<string?> TitleAsync(HttpResponseMessage resp)
    {
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return doc.RootElement.TryGetProperty("title", out var t) ? t.GetString() : null;
    }

    [Fact]
    public async Task Product_with_movements_cannot_be_deleted()
    {
        var s = await SeedAsync(withMovement: true);

        var resp = await s.Client.DeleteAsync($"/api/panel/products/{s.ProductId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("product-has-stock-movements");
    }

    [Fact]
    public async Task Variant_with_movements_cannot_be_deleted()
    {
        var s = await SeedAsync(withMovement: true);

        var resp = await s.Client.DeleteAsync(
            $"/api/panel/products/{s.ProductId}/variants/{s.VariantId}");

        resp.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await TitleAsync(resp)).Should().Be("variant-has-stock-movements");
    }

    [Fact]
    public async Task Product_without_movements_is_still_deletable()
    {
        var s = await SeedAsync(withMovement: false);

        var resp = await s.Client.DeleteAsync($"/api/panel/products/{s.ProductId}");

        resp.StatusCode.Should().BeOneOf(HttpStatusCode.NoContent, HttpStatusCode.OK);
    }
}
```

- [ ] **Step 2: Testi koştur, kırmızıyı gör**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockDeleteGuardTests`
Expected: FAIL — 204/500 dönüyor, 409 değil.

- [ ] **Step 3: Ürün silmesine koruma ekle**

`PanelProductsController.cs` — `Delete` metodunda, ürünün bu lisansa ait olduğu
doğrulandıktan **hemen sonra**, hiçbir silme yapılmadan önce:

```csharp
        // Restrict FK zaten silmeyi engelliyor; buradaki kontrol kullanıcıya
        // 500 yerine Türkçe bir açıklama vermek için. Defteri olan ürün
        // silinemez — silinseydi geçmiş satışların dayanağı kaybolurdu.
        var hasMovements = await _db.StockMovements
            .AnyAsync(m => m.ProductId == id, ct);
        if (hasMovements)
            return Problem(
                title: "product-has-stock-movements",
                detail: "Bu ürünün stok hareketleri var; silinemez. Arşivleyebilirsiniz.",
                statusCode: StatusCodes.Status409Conflict);
```

- [ ] **Step 4: Varyant silmesine koruma ekle**

`PanelProductVariantsController.cs` — `Delete` metodunda, varyantın bulunduğu
doğrulandıktan hemen sonra:

```csharp
        var hasMovements = await _db.StockMovements
            .AnyAsync(m => m.ProductVariantId == variantId, ct);
        if (hasMovements)
            return Problem(
                title: "variant-has-stock-movements",
                detail: "Bu varyantın stok hareketleri var; silinemez. Pasife alabilirsiniz.",
                statusCode: StatusCodes.Status409Conflict);
```

Her iki dosyada da `using Microsoft.EntityFrameworkCore;` zaten var; parametre
adlarını (`id` / `variantId`, `ct`) metodun kendi imzasından **doğrulayın** ve
gerekiyorsa uyarlayın.

- [ ] **Step 5: Testleri koştur**

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~StockDeleteGuardTests`
Expected: PASS (3 test).

Run: `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj`
Expected: hepsi PASS.

- [ ] **Step 6: Spec'e kapanış notu ekle**

`docs/superpowers/specs/2026-08-07-stok-sistemi-design.md` dosyasının **sonuna**:

```markdown
---

## Faz 1b sunucu ayağı — kapanış notu (2026-08-13)

Uygulanan kararlar:

- Defter `StockMovement` tablosunda; **bakiye hiçbir yerde saklanmıyor**.
- Sipariş senkronu deftere **mutabakatla** yazıyor (`StockLedgerReconciler`),
  olay ekleyerek değil. Gerekçe: WPF aynı siparişi iptal / iptal geri alma /
  basım / fiyat düzeltme durumlarında yeniden gönderiyor.
- `Order.ProductId` / `ProductVariantId` **FK değil**; `StockMovement`'ınkiler FK
  (Restrict) ve hareketi olan ürün/varyant 409 ile korunuyor.
- `StockMovement.OccurredAt` iş zamanı (geçmişe dönük olabilir),
  `CreatedAt` sunucu yazma anı — **çekme imleci `CreatedAt` üstünde** ve
  eşitlikleri kırmak için `Id` ile bileşik.
- WPF katalog çekmesi **tam anlık görüntü** (silmeler artımlı imleçte görünmez).
  Stok çekmesi **artımlı imleçli ama gövdesi bakiye**: WPF'e ham defter satırı
  inmiyor, değişen anahtarların o anki mutlak bakiyesi iniyor — istemci upsert
  eder, toplamaz.
- **Stok takibi için açma/kapama anahtarı YOK** (kullanıcı kararı, 2026-08-13).
  Ürün kartı olmayan satış zaten hareket üretmiyor; negatif bakiye uyarı, engel
  değil. Yani stok tutmayan yayıncı hiçbir şey yapmadan satmaya devam ediyor.

**Kapanan açık konu — "WPF yerel replikanın sınırı" (Faz 1b'de ölçülecekti):**
çekme ucu **arşivlenmiş ürünleri hiç göndermiyor** (`LicensesWpfCatalogPullController`),
yani yerel replika aktif katalog kadar büyür — yayıncı büyüdükçe değil. Ayrıca
stok tarafında WPF'e ham defter değil ürün/varyant başına **tek bakiye satırı**
iniyor; yerel tablo satış sayısıyla değil katalog boyutuyla ölçekleniyor. Ek bir
"yalnız aktifleri tut" kuralına gerek kalmadı.

Kapsam dışı bırakılanlar: barkod üretimi/okutma (Faz 1c), WPF yerel replika ve
eşleştirme (ayrı plan), panel stok ekranları (OrderDeck-Mobile deposu).
```

Aynı dosyadaki "Açık konular" listesinde **"WPF yerel replikanın sınırı"**
maddesini işaretle (`- [ ]` → `- [x]`) ve sonuna ekle:
`— kapandı: arşivliler çekilmiyor, stok tarafı bakiye satırı (Faz 1b).`

- [ ] **Step 7: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs \
        OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs \
        OrderDeck.LicenseServer.Tests/Controllers/Panel/StockDeleteGuardTests.cs \
        docs/superpowers/specs/2026-08-07-stok-sistemi-design.md
git commit -m "$(cat <<'EOF'
feat(stok): hareketi olan ürün/varyant silinemez (409)

Restrict FK zaten engelliyordu ama 500 veriyordu; kullanıcı artık Türkçe
açıklama ve arşivleme önerisi görüyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Bitirme doğrulaması

- [ ] `dotnet build OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj` → 0 hata, 0 uyarı
- [ ] `dotnet build OrderDeck.App/OrderDeck.App.csproj` → 0 hata (istemci DTO değişikliği kırmamalı)
- [ ] `dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj` → hepsi PASS (~788)
- [ ] `dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj` → hepsi PASS (WPF tarafı değişmedi)
- [ ] İki migration üretildi ve `Up()` içerikleri gözle doğrulandı
- [ ] `git log --oneline` → 10 commit, hepsi tek konu

## Kapsam dışı

- WPF tarafı: yerel replika şeması (migration 025), `CatalogSyncService`,
  eksen değeri eşleştirici, varyant seçici drawer, ürün kartı — **ayrı plan**.
- Panel stok ekranları — **OrderDeck-Mobile deposu, ayrı plan**.
- Barkod üretimi, etiket PDF'i, okutma, arşivleme — **Faz 1c**.
- Postgres göçü — ertelendi.

