# Faz 1c — Barkod Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Her ürün varyantına sunucuda benzersiz, 10 haneli, opak bir barkod numarası verilsin; panelde görünüp düzenlenebilsin; WPF bu numarayı Code128 olarak etikete bassın ve okutulduğunda ürünü açsın.

**Architecture:** Barkod yükü **türetilmiş değil, atanmış**: lisans başına bir sayaç (`BarcodeCounter`) 10 haneli sıra numarası üretir, `(LicenseId, Barcode)` benzersiz indeksiyle korunur. Atama, varyantı yazan isteğin **kendi `SaveChangesAsync`'i içinde** olur — sayaç ile varyant tek iş biriminde işlenir, ayrı bir kaydetme yoktur. Numara türetilmediği için varyant yaratılır yaratılmaz bellidir, katalog senkronuyla WPF replikasına iner ve **çevrimdışı okutma** çalışır. WPF tarafında okutma yeni bir ekran açmaz: barkod, var olan yayın-kodu kutusunda çözülür — barkod → varyant → ürün → o ürünün yayın kodu; sonuç yine bir `BroadcastCodeResolution` olduğu için aşağıdaki bütün akış (varyant çipleri, stok rozetleri, sipariş) değişmeden çalışır.

**Tech Stack:** ASP.NET Core 10 + EF Core 10 (SQL Server prod, InMemory test) · React + Vite + TS (ayrı repo: `OrderDeck-Mobile/apps/panel`) · WPF `net10.0-windows` + Dapper/SQLite replika · **ZXing.Net 0.16.11** (Apache 2.0, görüntü kütüphanesinden bağımsız) + `System.Drawing` vektör çizimi · xunit + FluentAssertions 7.x

**Spec:** `docs/superpowers/specs/2026-08-16-barkod-faz1c-design.md`

---

## Spec'ten sapma (uygulamadan önce oku)

Spec'te panelde bir **"Boşları doldur"** toplu butonu geçiyor. Bu plan onu **kapsam dışı bırakıyor**: Görev 7'den sonra `Barcode` sütunu `NOT NULL`, sunucu üç yazma yolunda da boşları kendisi dolduruyor ve göç mevcut satırları geriye dönük dolduruyor — yani **barkodsuz varyant var olamaz**. Buton hiçbir zaman iş yapmayan ölü bir düğme olurdu. Satır bazlı "Oluştur" (var olan barkodu yenisiyle değiştirme) kalıyor; asıl ihtiyaç olan yetenek o.

---

## Dosya yapısı

**Sunucu — `OrderDeck.LicenseServer`**

| Dosya | Sorumluluk |
|---|---|
| `Domain/BarcodeCounter.cs` (yeni) | Lisans başına sıradaki numara + `RowVersion` eşzamanlılık damgası |
| `Services/Catalog/BarcodeAllocator.cs` (yeni) | Sayaçtan N numara ayırır, elle alınmış numaraları atlar; **`SaveChanges` ÇAĞIRMAZ** |
| `Domain/ProductVariant.cs` (değişir) | `Barcode` `string?` → `string`, XML doc'taki ölü varsayım silinir |
| `Data/LicenseDbContext.cs` (değişir) | `BarcodeCounter` eşlemesi + `(LicenseId, Barcode)` benzersiz indeksi |
| `Controllers/Panel/PanelProductVariantsController.cs` (değişir) | `VariantRequest.Barcode`, üç yazma yolunda doğrulama + otomatik doldurma |
| `Controllers/Panel/PanelBarcodesController.cs` (yeni) | `POST /api/panel/barcodes/next?count=N` |
| `Controllers/Panel/PanelBroadcastCodesController.cs` (değişir) | 10 haneli saf sayıyı yayın kodu olarak reddet |
| `Controllers/Panel/PanelProductsController.cs` (değişir) | `q` araması varyant barkoduyla da eşleşsin |
| `Migrations/*_AddBarcodeCounter.cs` (yeni) | Sayaç tablosu |
| `Migrations/*_BarcodeNotNullAndUnique.cs` (yeni) | Geriye dönük doldurma + NOT NULL + benzersiz indeks |

**Panel — `C:\Users\burak\source\repos\OrderDeck-Mobile\apps\panel`**

| Dosya | Sorumluluk |
|---|---|
| `src/api/catalog.ts` (değişir) | `VariantUpsert.barcode`, `nextBarcodes()` çağrısı |
| `src/components/catalog/VariantSection.tsx` (değişir) | Barkod sütunu, satır içi düzenleme, satır bazlı "Oluştur" |
| `src/screens/StokScreen.tsx` (değişir) | Arama kutusu ipucu metni |

**WPF — `OrderDeck.Core` / `OrderDeck.Labeling` / `OrderDeck.App`**

| Dosya | Sorumluluk |
|---|---|
| `OrderDeck.Core/Storage/Migrations/031_catalog_variant_barcode_index.sql` (yeni) | Replikada barkod indeksi (kolon 025'ten beri var) |
| `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs` (değişir) | `FindVariantByBarcode`, `GetBroadcastCodes(productId)` |
| `OrderDeck.Core/Catalog/BroadcastCodeResolver.cs` (değişir) | Yayın kodu bulunamazsa barkod olarak dene |
| `OrderDeck.Labeling/BarcodeLabelDocument.cs` (yeni) | Code128 modül dizisi → vektör dikdörtgenler → `PrintDocument` |
| `OrderDeck.Labeling/BarcodeLabelPrinter.cs` (yeni) | Yazıcıya gönderme (ayrı sınıf: yük farklı) |
| `OrderDeck.App/Views/Drawers/BarcodeLabelDrawer.xaml(.cs)` (yeni) | Varyant seç → adet → bas |
| `OrderDeck.App/Views/Shell/ProductCard.xaml` (değişir) | "Etiket bas" düğmesi |
| `OrderDeck.App/Views/Shell/ActiveProductBar.xaml` (değişir) | `MaxLength` 32→64, `CharacterCasing` kalkar |
| `Directory.Packages.props` (değişir) | `ZXing.Net` 0.16.11 |

---

## Görev 1: `BarcodeCounter` varlığı + eşleme + göç

**Dosyalar:**
- Oluştur: `OrderDeck.LicenseServer/Domain/BarcodeCounter.cs`
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Test: `OrderDeck.LicenseServer.Tests/Domain/BarcodeCounterMappingTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`OrderDeck.LicenseServer.Tests/Domain/BarcodeCounterMappingTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Domain;

public class BarcodeCounterMappingTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public async Task Sayac_lisans_basina_tek_satirdir()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();

        db.BarcodeCounters.Add(new BarcodeCounter { LicenseId = licenseId, Next = 1 });
        await db.SaveChangesAsync();

        var row = await db.BarcodeCounters.FindAsync(licenseId);
        row!.Next.Should().Be(1);
    }
}
```

- [ ] **Adım 2: Testi çalıştır, derlenmediğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~BarcodeCounterMappingTests
```

Beklenen: derleme hatası — `BarcodeCounter` ve `db.BarcodeCounters` yok.

- [ ] **Adım 3: Varlığı yaz**

`OrderDeck.LicenseServer/Domain/BarcodeCounter.cs`:

```csharp
namespace OrderDeck.LicenseServer.Domain;

/// <summary>
/// Lisans başına barkod sıra numarası üreteci. Tek satır, tek sayı.
///
/// <para><b>Neden ayrı tablo:</b> barkod yükü türetilmiş DEĞİL — eksen
/// değerlerinden ya da Id'den hesaplanmıyor. Türetseydik eksen değeri
/// düzeltilince (yazım hatası) basılı etiket geçersiz olurdu. Atanan bir
/// sayı, kaynağını bir yerde saklamayı zorunlu kılar; burası orası.</para>
///
/// <para><b>Neden lisans başına:</b> numaralar kısa (10 hane) ve operatörün
/// gözüyle okunabilir olsun diye küçük başlıyor. Global tek sayaç, kiracıların
/// numaralarını birbirine karıştırıp gereksizce büyütürdü. Benzersizlik zaten
/// <c>(LicenseId, Barcode)</c> indeksinde.</para>
///
/// <para><b>RowVersion:</b> aynı lisans için iki eşzamanlı ayırma, sayacı aynı
/// değerden okuyup aynı numarayı verebilirdi. Damga bunu
/// <see cref="Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException"/>'a
/// çevirir; çağıran 409 döner. Benzersiz indeks son savunma hattı, ilk değil.
/// Emsal: <c>20260501075917_AddConcurrencyTokens</c> (License/Activation).</para>
/// </summary>
public class BarcodeCounter
{
    /// <summary>Birincil anahtar; lisans başına tek satır.</summary>
    public Guid LicenseId { get; set; }

    /// <summary>Bir sonraki VERİLECEK numara. İlk satır 1'den başlar.</summary>
    public long Next { get; set; }

    /// <summary>Eşzamanlılık damgası; SQL Server <c>rowversion</c>.</summary>
    public byte[]? RowVersion { get; set; }
}
```

- [ ] **Adım 4: `LicenseDbContext`'e ekle**

`OrderDeck.LicenseServer/Data/LicenseDbContext.cs` — diğer `DbSet`lerin yanına:

```csharp
public DbSet<BarcodeCounter> BarcodeCounters => Set<BarcodeCounter>();
```

`OnModelCreating` içinde, `ProductVariant` eşlemesinin hemen ardına:

```csharp
mb.Entity<BarcodeCounter>(b =>
{
    b.HasKey(c => c.LicenseId);
    // ValueGeneratedOnAddOrUpdate + IsConcurrencyToken: SQL Server'da
    // rowversion'a çevrilir, InMemory'de sessizce yok sayılır (testler
    // eşzamanlılık damgasını zaten sınayamıyor — bkz. sınıf XML doc'u).
    b.Property(c => c.RowVersion).IsRowVersion();
});
```

- [ ] **Adım 5: Testi çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~BarcodeCounterMappingTests
```

Beklenen: 1 passed.

- [ ] **Adım 6: EF göçünü üret**

```bash
dotnet ef migrations add AddBarcodeCounter --project OrderDeck.LicenseServer
```

Üretilen dosyayı **oku**: yalnızca `BarcodeCounter` tablosunu yaratmalı. Başka bir tabloya dokunuyorsa göç dosyasını sil, modeldeki istenmeyen değişikliği bul, tekrar üret.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Domain/BarcodeCounter.cs OrderDeck.LicenseServer/Data/LicenseDbContext.cs OrderDeck.LicenseServer/Migrations OrderDeck.LicenseServer.Tests/Domain/BarcodeCounterMappingTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): lisans başına barkod sayacı

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 2: `BarcodeAllocator`

**Dosyalar:**
- Oluştur: `OrderDeck.LicenseServer/Services/Catalog/BarcodeAllocator.cs`
- Test: `OrderDeck.LicenseServer.Tests/Services/BarcodeAllocatorTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

`OrderDeck.LicenseServer.Tests/Services/BarcodeAllocatorTests.cs`:

```csharp
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services;

public class BarcodeAllocatorTests
{
    private static LicenseDbContext NewDb() =>
        new(new DbContextOptionsBuilder<LicenseDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    [Fact]
    public void Format_on_haneye_sifirla_doldurur()
    {
        BarcodeAllocator.Format(1).Should().Be("0000000001");
        BarcodeAllocator.Format(9_999_999_999).Should().Be("9999999999");
    }

    [Fact]
    public async Task Ilk_ayirma_birden_baslar_ve_ardisik_verir()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(licenseId, 3, default);

        codes.Should().Equal("0000000001", "0000000002", "0000000003");
    }

    [Fact]
    public async Task Ayirma_kaydetmez_sayaci_cagiran_isler()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        await sut.AllocateAsync(licenseId, 1, default);

        // Ayırıcı kendi SaveChanges'ini çağırmıyor: sayaç ile varyant AYNI
        // iş biriminde işlenmeli. Çağırsaydı, sonraki doğrulama hatasında
        // sayaç ilerlemiş ama varyant yazılmamış olurdu.
        db.ChangeTracker.HasChanges().Should().BeTrue();
    }

    [Fact]
    public async Task Ikinci_ayirma_kaldigi_yerden_devam_eder()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        var sut = new BarcodeAllocator(db);

        await sut.AllocateAsync(licenseId, 2, default);
        await db.SaveChangesAsync();
        var second = await sut.AllocateAsync(licenseId, 2, default);

        second.Should().Equal("0000000003", "0000000004");
    }

    [Fact]
    public async Task Elle_alinmis_numaralar_atlanir()
    {
        var licenseId = Guid.NewGuid();
        await using var db = NewDb();
        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = licenseId,
            ProductId = Guid.NewGuid(),
            Barcode = "0000000002",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(licenseId, 2, default);

        codes.Should().Equal("0000000001", "0000000003");
    }

    [Fact]
    public async Task Baska_lisansin_numarasi_engel_degildir()
    {
        var mine = Guid.NewGuid();
        await using var db = NewDb();
        db.ProductVariants.Add(new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = Guid.NewGuid(),   // BAŞKA lisans
            ProductId = Guid.NewGuid(),
            Barcode = "0000000001",
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var sut = new BarcodeAllocator(db);

        var codes = await sut.AllocateAsync(mine, 1, default);

        codes.Should().Equal("0000000001");
    }
}
```

- [ ] **Adım 2: Testleri çalıştır, derlenmediğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~BarcodeAllocatorTests
```

Beklenen: derleme hatası — `BarcodeAllocator` yok.

- [ ] **Adım 3: Ayırıcıyı yaz**

`OrderDeck.LicenseServer/Services/Catalog/BarcodeAllocator.cs`:

```csharp
using System.Globalization;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;

namespace OrderDeck.LicenseServer.Services.Catalog;

/// <summary>
/// Lisans sayacından barkod numarası ayırır.
///
/// <para><b>KAYDETMEZ.</b> İzlenen <see cref="BarcodeCounter"/> satırını
/// değiştirir ve biter; <c>SaveChanges</c> çağırmak ÇAĞIRANIN işi. Sebep:
/// sayaç ile varyant tek iş biriminde işlenmeli. Ayırıcı kendi kaydını
/// yapsaydı, ondan sonra gelen bir doğrulama hatası ya da benzersizlik
/// çakışması sayacı ilerlemiş, varyantı yazılmamış bırakırdı — numaralar
/// sessizce delinirdi. Bu kural teste bağlandı:
/// <c>Ayirma_kaydetmez_sayaci_cagiran_isler</c>.</para>
///
/// <para><b>Atlama:</b> panelden elle yazılmış bir barkod, sayacın sırada
/// olduğu değeri kapmış olabilir. Numaralar tek tek değil, tek sorguda
/// aralık olarak sorulur; çakışanlar atlanır. Döngü sonlanır çünkü her
/// turda <c>Next</c> en az 1 ilerler.</para>
///
/// <para><b>DİKKAT — görülmeyen satırlar:</b> "alınmış" sorgusu yalnız
/// KAYDEDİLMİŞ varyantları görür. Aynı istek içinde iki ayrı ayırma yapılıp
/// arada kaydedilmezse ikincisi birincinin numaralarını görmez. Bugün her
/// istek tek ayırma yapıyor; bu kalıp bozulursa burası da bozulur.</para>
/// </summary>
public sealed class BarcodeAllocator
{
    private readonly LicenseDbContext _db;

    public BarcodeAllocator(LicenseDbContext db) => _db = db;

    /// <summary>10 hane, soldan sıfır dolgulu, kültürden bağımsız.</summary>
    internal static string Format(long n) =>
        n.ToString("D10", CultureInfo.InvariantCulture);

    public async Task<IReadOnlyList<string>> AllocateAsync(
        Guid licenseId, int count, CancellationToken ct)
    {
        if (count <= 0) return Array.Empty<string>();

        var counter = await _db.BarcodeCounters
            .FirstOrDefaultAsync(c => c.LicenseId == licenseId, ct);

        if (counter is null)
        {
            counter = new BarcodeCounter { LicenseId = licenseId, Next = 1 };
            _db.BarcodeCounters.Add(counter);
        }

        var result = new List<string>(count);
        while (result.Count < count)
        {
            var need = count - result.Count;
            var candidates = new List<string>(need);
            for (var i = 0; i < need; i++)
                candidates.Add(Format(counter.Next + i));

            var taken = await _db.ProductVariants
                .AsNoTracking()
                .Where(v => v.LicenseId == licenseId && candidates.Contains(v.Barcode))
                .Select(v => v.Barcode)
                .ToListAsync(ct);

            var takenSet = new HashSet<string>(taken, StringComparer.Ordinal);
            foreach (var candidate in candidates)
            {
                counter.Next++;
                if (!takenSet.Contains(candidate)) result.Add(candidate);
            }
        }

        return result;
    }
}
```

- [ ] **Adım 4: DI'ya kaydet**

`OrderDeck.LicenseServer/Program.cs` — diğer scoped katalog servislerinin yanına:

```csharp
builder.Services.AddScoped<BarcodeAllocator>();
```

Gerekirse dosyanın başına `using OrderDeck.LicenseServer.Services.Catalog;` ekle.

- [ ] **Adım 5: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~BarcodeAllocatorTests
```

Beklenen: 6 passed.

- [ ] **Adım 6: Commit**

```bash
git add OrderDeck.LicenseServer/Services/Catalog/BarcodeAllocator.cs OrderDeck.LicenseServer/Program.cs OrderDeck.LicenseServer.Tests/Services/BarcodeAllocatorTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): sayaçtan numara ayıran BarcodeAllocator

Ayırma çağıranın SaveChanges'ine bırakıldı: sayaç ile varyant
tek iş biriminde işlensin, hata hâlinde numara delinmesin.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 3: `VariantRequest.Barcode` + doğrulama + otomatik doldurma

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs`
- Değiştir: `OrderDeck.LicenseServer/Domain/ProductVariant.cs` (yalnız XML doc)
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductVariantsControllerTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

`PanelProductVariantsControllerTests.cs` sonuna ekle (dosyadaki mevcut yardımcıları — `CustomerAuthHelper`, lisans/ürün kurulumu — aynen kullan; yeni bir fixture kurma):

```csharp
    [Fact]
    public async Task Barkod_bos_birakilirsa_sunucu_doldurur()
    {
        var (client, productId) = await SetupProductAsync();

        var res = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", axis2Value = (string?)null, isActive = true });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await res.Content.ReadFromJsonAsync<PanelProductsController.VariantDto>();
        dto!.Barcode.Should().Be("0000000001");
    }

    [Fact]
    public async Task Elle_yazilan_barkod_korunur()
    {
        var (client, productId) = await SetupProductAsync();

        var res = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", axis2Value = (string?)null,
                  isActive = true, barcode = "8690000000017" });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await res.Content.ReadFromJsonAsync<PanelProductsController.VariantDto>();
        dto!.Barcode.Should().Be("8690000000017");
    }

    [Fact]
    public async Task Ayni_barkod_iki_varyanta_verilemez()
    {
        var (client, productId) = await SetupProductAsync();

        await client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", axis2Value = (string?)null,
                  isActive = true, barcode = "8690000000017" });

        var res = await client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value = "Beyaz", axis2Value = (string?)null,
                  isActive = true, barcode = "8690000000017" });

        res.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Code128_disi_karakter_reddedilir()
    {
        var (client, productId) = await SetupProductAsync();

        // Türkçe harf Code128'in ASCII 32-126 kümesinde YOK; yazıcıya
        // gönderilse okunamayan bir sembol basılırdı.
        var res = await client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", axis2Value = (string?)null,
                  isActive = true, barcode = "ÜRÜN-1" });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Toplu_yolda_her_satir_kendi_numarasini_alir()
    {
        var (client, productId) = await SetupProductAsync();

        var res = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants/bulk",
            new { items = new[]
            {
                new { axis1Value = "Siyah", axis2Value = (string?)null, isActive = true },
                new { axis1Value = "Beyaz", axis2Value = (string?)null, isActive = true },
            }});

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content
            .ReadFromJsonAsync<PanelProductVariantsController.BulkResultDto>();
        body!.Variants.Select(v => v.Barcode)
            .Should().Equal("0000000001", "0000000002");
    }

    [Fact]
    public async Task Guncellemede_bos_barkod_mevcut_degeri_silmez()
    {
        var (client, productId) = await SetupProductAsync();

        var created = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", axis2Value = (string?)null, isActive = true });
        var dto = await created.Content
            .ReadFromJsonAsync<PanelProductsController.VariantDto>();

        var res = await client.PutAsJsonAsync(
            $"/api/panel/products/{productId}/variants/{dto!.Id}",
            new { axis1Value = "Siyah", axis2Value = (string?)null, isActive = false });

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await res.Content
            .ReadFromJsonAsync<PanelProductsController.VariantDto>();
        updated!.Barcode.Should().Be("0000000001");
    }
```

`SetupProductAsync` dosyada yoksa, mevcut testlerin lisans + ürün kurulum satırlarını aynen taşıyan özel bir yardımcı olarak dosyanın altına ekle; yeni bir kalıp icat etme.

- [ ] **Adım 2: Testleri çalıştır, başarısız olduklarını doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelProductVariantsControllerTests
```

Beklenen: yeni 6 test başarısız (`Barcode` null döner / istek alanı yok sayılır).

- [ ] **Adım 3: `VariantRequest`'e alanı ekle**

`PanelProductVariantsController.cs` satır 35-38:

```csharp
    public sealed record VariantRequest(
        [MaxLength(CatalogLimits.AxisValue)] string? Axis1Value,
        [MaxLength(CatalogLimits.AxisValue)] string? Axis2Value,
        bool IsActive,
        [MaxLength(CatalogLimits.Barcode)] string? Barcode = null);
```

Varsayılan `null`: mevcut çağrı yerleri ve testler dört argümanlı hâle geçmek zorunda kalmasın.

- [ ] **Adım 4: Doğrulama + ayırma yardımcılarını ekle**

Aynı dosyada, `VariantValuesTakenAsync`'in yanına:

```csharp
    /// <summary>
    /// Code128 yalnız ASCII 32-126 basar. Türkçe harf ya da kontrol karakteri
    /// içeren bir yük, yazıcıya gitse okunamayan sembol üretirdi — kabulü
    /// burada, yazma yolunun başında kesiyoruz.
    /// </summary>
    private static bool IsPrintableCode128(string s) =>
        s.All(c => c >= ' ' && c <= '~');

    /// <summary>
    /// Barkod yükünü hazırlar: elle yazılmışsa doğrular, boşsa sayaçtan ayırır.
    /// Hata varsa <paramref name="error"/> dolar.
    ///
    /// <para><b>Boş = hata değil:</b> kural "kullanıcı barkod yazsın" değil,
    /// "barkodsuz varyant var olmasın". Sunucu boşluğu kendisi doldurunca
    /// kural ihlal edilemez hâle geliyor ve ileride gelecek Excel toplu
    /// içe aktarımı barkod sütunu boş bir dosyayla da çalışabiliyor.</para>
    /// </summary>
    private async Task<string?> ResolveBarcodeAsync(
        Guid licenseId, string? requested, CancellationToken ct,
        Action<IActionResult> fail)
    {
        var trimmed = Trim(requested);
        if (trimmed is null)
        {
            var allocated = await _barcodes.AllocateAsync(licenseId, 1, ct);
            return allocated[0];
        }

        if (trimmed.Length > CatalogLimits.Barcode)
        {
            fail(Problem(title: "barcode-too-long",
                detail: $"Barkod en fazla {CatalogLimits.Barcode} karakter olabilir.",
                statusCode: 400));
            return null;
        }

        if (!IsPrintableCode128(trimmed))
        {
            fail(Problem(title: "barcode-not-printable",
                detail: "Barkod yalnız İngiliz alfabesi harfleri, rakam ve "
                      + "temel noktalama içerebilir (Code128).",
                statusCode: 400));
            return null;
        }

        return trimmed;
    }

    /// <summary>
    /// Bu barkod lisansta zaten kullanılıyorsa 409 döndürür, yoksa null.
    /// <c>VariantValuesTakenAsync</c> ile aynı gerekçe: hem SaveChanges öncesi
    /// ön kontrol hem sonrası yarış sınıflandırması tek metottan geçsin.
    /// </summary>
    private async Task<IActionResult?> BarcodeTakenAsync(
        Guid licenseId, string barcode, Guid? excludeId, CancellationToken ct)
    {
        var exists = await _db.ProductVariants
            .AsNoTracking()
            .AnyAsync(v => v.LicenseId == licenseId
                           && v.Barcode == barcode
                           && (excludeId == null || v.Id != excludeId), ct);

        if (!exists) return null;

        return Problem(title: "duplicate-barcode",
            detail: $"'{barcode}' barkodu başka bir varyantta kullanılıyor.",
            statusCode: 409);
    }
```

Ctor ve alan:

```csharp
    private readonly LicenseDbContext _db;
    private readonly BarcodeAllocator _barcodes;

    public PanelProductVariantsController(LicenseDbContext db, BarcodeAllocator barcodes)
    {
        _db = db;
        _barcodes = barcodes;
    }
```

Dosyanın başına `using OrderDeck.LicenseServer.Services.Catalog;` ekle.

- [ ] **Adım 5: Üç yazma yoluna bağla**

**`Create`** — satır 56 (`conflict` kontrolünden sonra), varyant kurulmadan önce:

```csharp
        IActionResult? barcodeError = null;
        var barcode = await ResolveBarcodeAsync(
            product.LicenseId, req.Barcode, ct, e => barcodeError = e);
        if (barcodeError is not null) return barcodeError;

        var barcodeConflict = await BarcodeTakenAsync(
            product.LicenseId, barcode!, excludeId: null, ct);
        if (barcodeConflict is not null) return barcodeConflict;
```

`new ProductVariant { … }` içine `Barcode = barcode!,` ekle. `catch (DbUpdateException)` bloğunda, `raced` kontrolünün ardına:

```csharp
            var racedBarcode = await BarcodeTakenAsync(
                product.LicenseId, barcode!, excludeId: null, ct);
            if (racedBarcode is not null) return racedBarcode;
```

**`CreateBulk`** — adım 3'ün (var olanlarla çakışma döngüsü) ardına:

```csharp
        // Tek ayırma çağrısı: ayırıcı yalnız KAYDEDİLMİŞ satırları görüyor,
        // parti içinde ikinci kez çağırmak aynı numaraları verirdi.
        var autoCount = items.Count(i => Trim(i.Barcode) is null);
        var pool = new Queue<string>(
            await _barcodes.AllocateAsync(product.LicenseId, autoCount, ct));

        var barcodes = new List<string>(items.Count);
        foreach (var item in items)
        {
            IActionResult? barcodeError = null;
            var explicitCode = Trim(item.Barcode);
            if (explicitCode is null)
            {
                barcodes.Add(pool.Dequeue());
                continue;
            }

            var resolved = await ResolveBarcodeAsync(
                product.LicenseId, explicitCode, ct, e => barcodeError = e);
            if (barcodeError is not null) return barcodeError;
            barcodes.Add(resolved!);
        }

        // Parti içi tekrar — eksen değerlerindekiyle aynı gerekçe: benzersiz
        // indeksten dönen hata prod'da 500 olurdu.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var code in barcodes)
            if (!seen.Add(code))
                return Problem(title: "duplicate-barcode-in-batch",
                    detail: $"'{code}' barkodu listede birden fazla kez var.",
                    statusCode: 409);

        foreach (var code in barcodes)
        {
            var barcodeConflict = await BarcodeTakenAsync(
                product.LicenseId, code, excludeId: null, ct);
            if (barcodeConflict is not null) return barcodeConflict;
        }
```

`built.Zip(items, …)` ifadesini indeksli hâle çevir ki `barcodes[i]` yazılabilsin:

```csharp
        var variants = built.Select((segments, i) => new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = product.LicenseId,
            ProductId = product.Id,
            Axis1Value = segments.Axis1Value,
            Axis2Value = segments.Axis2Value,
            Barcode = barcodes[i],
            IsActive = items[i].IsActive,
            CreatedAt = now,
            UpdatedAt = now,
        }).ToList();
```

`catch (DbUpdateException)` bloğunda, mevcut döngünün ardına barkod döngüsünü de ekle:

```csharp
            foreach (var code in barcodes)
            {
                var racedBarcode = await BarcodeTakenAsync(
                    product.LicenseId, code, excludeId: null, ct);
                if (racedBarcode is not null) return racedBarcode;
            }
```

**`Update`** — `conflict` kontrolünün ardına:

```csharp
        // Boş gönderilen barkod, MEVCUT değeri korur. "Sil" anlamına gelseydi
        // panelde alanı temizleyen bir yanlış tıklama basılı etiketi
        // geçersizleştirirdi; barkodsuz varyant zaten var olamaz.
        var requestedBarcode = Trim(req.Barcode);
        if (requestedBarcode is not null
            && !string.Equals(requestedBarcode, variant.Barcode, StringComparison.Ordinal))
        {
            IActionResult? barcodeError = null;
            var resolved = await ResolveBarcodeAsync(
                product.LicenseId, requestedBarcode, ct, e => barcodeError = e);
            if (barcodeError is not null) return barcodeError;

            var barcodeConflict = await BarcodeTakenAsync(
                product.LicenseId, resolved!, id, ct);
            if (barcodeConflict is not null) return barcodeConflict;

            variant.Barcode = resolved!;
        }
```

- [ ] **Adım 6: XML doc'lardaki ölü varsayımı düzelt**

`OrderDeck.LicenseServer/Domain/ProductVariant.cs` — `Barcode` özelliğinin doc'u:

```csharp
    /// <summary>
    /// Fiziksel kimlik. Yük <b>varyant yaratılırken</b> atanır ve bir daha
    /// türetilmez: lisans başına 10 haneli bir sayaçtan gelen opak numara
    /// (bkz. <see cref="BarcodeCounter"/>), ya da paneldeki elle girilen değer.
    ///
    /// <para><b>Neden türetilmiyor:</b> eksen değerinden ya da Id'den
    /// hesaplansaydı, bir yazım düzeltmesi ("Siyah" → "Siyah ") basılı
    /// etiketleri geçersiz kılardı. Atanmış numara, yazıldığı andan sonra
    /// hiçbir düzenlemeden etkilenmez.</para>
    ///
    /// <para><b>Neden boş olamaz:</b> kural "kullanıcı barkod yazsın" değil,
    /// "barkodsuz varyant var olmasın" — sunucu üç yazma yolunda da boşluğu
    /// kendisi dolduruyor. Benzersizlik <c>(LicenseId, Barcode)</c>
    /// indeksinde.</para>
    ///
    /// <para>Numara türetilmediği için varyant yaratılır yaratılmaz belli;
    /// katalog senkronuyla WPF replikasına iniyor ve <b>çevrimdışı okutma</b>
    /// çalışıyor.</para>
    /// </summary>
    public string Barcode { get; set; } = string.Empty;
```

`PanelProductVariantsController` sınıf doc'u satır 20-21'deki "Faz 1c'de barkot yükü basım anında … yazılıp dondurulacak" cümlesini şununla değiştir:

```
/// Barkod yükü varyant YARATILIRKEN atanır (basım anında değil): boş
/// gönderilirse sunucu lisans sayacından doldurur, benzersizliği
/// (LicenseId, Barcode) indeksi korur.
```

- [ ] **Adım 7: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelProductVariantsControllerTests
```

Beklenen: tümü passed.

- [ ] **Adım 8: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductVariantsController.cs OrderDeck.LicenseServer/Domain/ProductVariant.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductVariantsControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): varyant yazma yollarında barkod doldurma ve doğrulama

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 4: `POST /api/panel/barcodes/next`

**Dosyalar:**
- Oluştur: `OrderDeck.LicenseServer/Controllers/Panel/PanelBarcodesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBarcodesControllerTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

```csharp
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using OrderDeck.LicenseServer.Controllers.Panel;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Panel;

public class PanelBarcodesControllerTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public PanelBarcodesControllerTests(ApiFactory factory) => _factory = factory;

    [Fact]
    public async Task Istenen_kadar_ardisik_numara_dondurur()
    {
        var client = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        await LicenseHelper.EnsureActiveLicenseAsync(_factory, client);

        var res = await client.PostAsync("/api/panel/barcodes/next?count=3", null);

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content
            .ReadFromJsonAsync<PanelBarcodesController.NextBarcodesDto>();
        body!.Barcodes.Should().HaveCount(3);
        body.Barcodes.Should().OnlyContain(b => b.Length == 10);
    }

    [Fact]
    public async Task Sifir_ya_da_negatif_adet_reddedilir()
    {
        var client = await CustomerAuthHelper.CreateAuthenticatedClientAsync(_factory);
        await LicenseHelper.EnsureActiveLicenseAsync(_factory, client);

        var res = await client.PostAsync("/api/panel/barcodes/next?count=0", null);

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Kimliksiz_istek_reddedilir()
    {
        var client = _factory.CreateClient();

        var res = await client.PostAsync("/api/panel/barcodes/next?count=1", null);

        res.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
```

`LicenseHelper.EnsureActiveLicenseAsync` dosyada yoksa, `PanelProductVariantsControllerTests`'teki lisans kurulum satırlarını bu dosyanın altına özel bir yardımcı olarak kopyala — yeni ortak yardımcı sınıf çıkarma, bu plan kapsamı değil.

- [ ] **Adım 2: Testleri çalıştır, derlenmediğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBarcodesControllerTests
```

Beklenen: derleme hatası — `PanelBarcodesController` yok.

- [ ] **Adım 3: Controller'ı yaz**

```csharp
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Controllers.Panel;

/// <summary>
/// Barkod numarası ayırma ucu. Panelde operatör bir varyanta yeni numara
/// vermek istediğinde kullanılır.
///
/// <para><b>Neden ayrı uç:</b> varyant yazma yolları boş barkodu zaten
/// dolduruyor; bu uç, kullanıcının numarayı YAZMADAN ÖNCE görüp
/// onaylayabilmesi için var (alan doldurulur, kaydet ayrı adımdır).</para>
///
/// <para>Ayırma burada kalıcıdır: dönen numaralar sayaçta tüketilir. Kullanıcı
/// kaydetmezse o numaralar boşa gider — kabul edildi, 10 hane 10 milyar
/// numara demek.</para>
/// </summary>
[ApiController]
[Route("api/panel/barcodes")]
[Authorize(AuthenticationSchemes = "Bearer-Customer")]
public sealed class PanelBarcodesController : ControllerBase
{
    private readonly LicenseDbContext _db;
    private readonly BarcodeAllocator _barcodes;

    public PanelBarcodesController(LicenseDbContext db, BarcodeAllocator barcodes)
    {
        _db = db;
        _barcodes = barcodes;
    }

    public sealed record NextBarcodesDto(IReadOnlyList<string> Barcodes);

    /// <summary>Tavan 200: <c>CatalogLimits.MaxBulkVariants</c> ile aynı sebep.</summary>
    private const int MaxCount = 200;

    [AllowStockStaff]
    [HttpPost("next")]
    public async Task<IActionResult> Next([FromQuery] int count, CancellationToken ct)
    {
        if (count <= 0 || count > MaxCount)
            return Problem(title: "invalid-count",
                detail: $"Adet 1 ile {MaxCount} arasında olmalı.", statusCode: 400);

        var licenseId = await ResolveActiveLicenseAsync(ct);
        if (licenseId is null) return Problem(title: "no-active-license", statusCode: 400);

        var codes = await _barcodes.AllocateAsync(licenseId.Value, count, ct);

        // Bu uçta kaydetmek ZORUNLU: ayırıcı sayacı yalnız değiştiriyor. Aksi
        // hâlde numaralar yanıtta döner ama sayaç ilerlemez ve bir sonraki
        // istek aynı numaraları verirdi.
        try
        {
            await _db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            return Problem(title: "barcode-counter-busy",
                detail: "Aynı anda başka bir barkod işlemi yapıldı; tekrar dene.",
                statusCode: 409);
        }

        return Ok(new NextBarcodesDto(codes));
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

- [ ] **Adım 4: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBarcodesControllerTests
```

Beklenen: 3 passed.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBarcodesController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBarcodesControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): panel için numara ayırma ucu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 5: 10 haneli saf sayı yayın kodu olamaz

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastCodesController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastCodesControllerTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

Mevcut test dosyasının sonuna:

```csharp
    [Fact]
    public async Task On_haneli_saf_sayi_yayin_kodu_olamaz()
    {
        var (client, productId) = await SetupProductAsync();

        // WPF'te tek kutu var: operatör kodu da barkodu da oraya yazıyor.
        // 10 haneli saf sayı barkod numara uzayı; yayın kodu olarak da
        // kabul edilseydi aynı metin iki farklı ürüne çözülebilirdi.
        var res = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { code = "0000000001", sellerAxisValue = (string?)null });

        res.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Dokuz_haneli_sayi_yayin_kodu_olabilir()
    {
        var (client, productId) = await SetupProductAsync();

        var res = await client.PostAsJsonAsync(
            $"/api/panel/products/{productId}/broadcast-codes",
            new { code = "123456789", sellerAxisValue = (string?)null });

        res.StatusCode.Should().Be(HttpStatusCode.Created);
    }
```

- [ ] **Adım 2: Testleri çalıştır, ilkinin başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBroadcastCodesControllerTests
```

Beklenen: `On_haneli_saf_sayi_yayin_kodu_olamaz` başarısız (201 dönüyor).

- [ ] **Adım 3: Kuralı ekle**

`PanelBroadcastCodesController` içinde, kod yazan uçta (Create ve varsa Update) uzunluk doğrulamasının hemen ardına:

```csharp
        // Barkod numara uzayıyla çakışmayı engelle. WPF'te kod kutusu tek:
        // önce yayın kodu, bulunamazsa barkod aranıyor. 10 haneli saf sayı
        // her iki kümede de bulunabilseydi aynı metin iki farklı ürüne
        // çözülür, hangisinin açılacağı sıralamaya kalırdı.
        if (code.Length == 10 && code.All(char.IsAsciiDigit))
            return Problem(title: "invalid-code",
                detail: "10 haneli saf sayı barkod numarası olarak ayrıldı; "
                      + "yayın kodu olarak kullanılamaz.",
                statusCode: 400);
```

`code` değişkeninin adı dosyada farklıysa (trimlenmiş hâli) ona uydur.

- [ ] **Adım 4: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelBroadcastCodesControllerTests
```

Beklenen: tümü passed.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelBroadcastCodesController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelBroadcastCodesControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): 10 haneli saf sayıyı yayın kodu olarak reddet

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 6: Ürün araması barkodla da eşleşsin

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs`
- Test: `OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

```csharp
    [Fact]
    public async Task Arama_varyant_barkoduyla_da_eslesir()
    {
        var (client, productId) = await SetupProductAsync();
        await client.PostAsJsonAsync($"/api/panel/products/{productId}/variants",
            new { axis1Value = "Siyah", axis2Value = (string?)null,
                  isActive = true, barcode = "8690000000017" });

        // Depoda barkod okutucusu var: operatör ürünü aramak yerine eldeki
        // parçayı okutuyor. Arama barkodu tanımasa okutucu panelde işe yaramaz.
        var res = await client.GetAsync("/api/panel/products?q=8690000000017");

        res.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await res.Content.ReadFromJsonAsync<PanelProductsController.ProductListDto>();
        body!.Items.Should().ContainSingle().Which.Id.Should().Be(productId);
    }
```

Dönüş DTO'sunun adı/şekli dosyadaki mevcut liste testinden alınmalı; farklıysa ona uydur.

- [ ] **Adım 2: Testi çalıştır, başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelProductsControllerTests
```

Beklenen: boş liste döndüğü için başarısız.

- [ ] **Adım 3: Süzgeci genişlet**

`PanelProductsController.List` içindeki mevcut satır:

```csharp
query = query.Where(p => p.NameSearch.Contains(needle) || p.Code.Contains(needle));
```

şununla değiştirilir:

```csharp
// Barkod NORMALIZE EDİLMEZ: yük ASCII, birebir eşleşmeli. Normalize edilmiş
// `needle` ile aramak "8690-0001" gibi yükleri sessizce kaçırırdı — bu yüzden
// ham `q` kullanılıyor.
query = query.Where(p =>
    p.NameSearch.Contains(needle)
    || p.Code.Contains(needle)
    || p.Variants.Any(v => v.Barcode == rawQuery));
```

`rawQuery`, metodun başında `q?.Trim() ?? string.Empty` olarak hesaplanır (`needle` hesabının hemen yanında).

- [ ] **Adım 4: Testi çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~PanelProductsControllerTests
```

Beklenen: tümü passed.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.LicenseServer/Controllers/Panel/PanelProductsController.cs OrderDeck.LicenseServer.Tests/Controllers/Panel/PanelProductsControllerTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): ürün aramasını varyant barkoduna genişlet

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 7: `Barcode` NOT NULL + benzersiz indeks + geriye dönük doldurma

**Dosyalar:**
- Değiştir: `OrderDeck.LicenseServer/Data/LicenseDbContext.cs`
- Oluştur: `OrderDeck.LicenseServer/Migrations/*_BarcodeNotNullAndUnique.cs`
- Test: `OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`CatalogModelTests.cs` sonuna:

```csharp
    [Fact]
    public void Barkod_lisans_icinde_benzersizdir()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(ProductVariant))!;

        var index = entity.GetIndexes().FirstOrDefault(i =>
            i.Properties.Select(p => p.Name)
                .SequenceEqual(new[] { "LicenseId", "Barcode" }));

        index.Should().NotBeNull("barkod benzersizliği son savunma hattı");
        index!.IsUnique.Should().BeTrue();
    }

    [Fact]
    public void Barkod_zorunludur()
    {
        using var db = NewDb();
        var entity = db.Model.FindEntityType(typeof(ProductVariant))!;

        entity.FindProperty(nameof(ProductVariant.Barcode))!
            .IsNullable.Should().BeFalse();
    }
```

`NewDb()` dosyada mevcut değilse, dosyadaki diğer testlerin kullandığı kurulumu aynen kullan.

- [ ] **Adım 2: Testleri çalıştır, başarısız olduklarını doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~CatalogModelTests
```

Beklenen: iki test başarısız.

- [ ] **Adım 3: Eşlemeyi güncelle**

`LicenseDbContext.OnModelCreating`, `ProductVariant` bloğunda:

```csharp
    b.Property(v => v.Barcode).HasMaxLength(CatalogLimits.Barcode).IsRequired();
```

ve mevcut indeksin yanına:

```csharp
    // Son savunma hattı. İlk savunma controller'daki ön kontrol; bu indeks
    // yarışta kaybedenin 500 yerine 409 almasını sağlıyor.
    // DİKKAT: EF InMemory benzersiz indeksi ZORLAMIYOR — bu kuralı testler
    // ancak model metadata'sı üzerinden doğrulayabilir.
    b.HasIndex(v => new { v.LicenseId, v.Barcode }).IsUnique();
```

- [ ] **Adım 4: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj --filter FullyQualifiedName~CatalogModelTests
```

Beklenen: tümü passed.

- [ ] **Adım 5: Göçü üret ve geriye dönük doldurmayı ELLE ekle**

```bash
dotnet ef migrations add BarcodeNotNullAndUnique --project OrderDeck.LicenseServer
```

Üretilen `Up(...)` metodunun **en başına**, `AlterColumn` ve `CreateIndex` çağrılarından **önce** şunu ekle:

```csharp
        // Mevcut satırların hepsinin barkodu NULL: alan bugüne dek panelden
        // yazılamıyordu, yani elle konmuş bir değer OLAMAZ. Bu yüzden çakışma
        // kontrolü gerekmiyor; lisans içinde 1'den başlayarak numaralıyoruz.
        migrationBuilder.Sql("""
            WITH numbered AS (
                SELECT Id,
                       ROW_NUMBER() OVER (PARTITION BY LicenseId ORDER BY CreatedAt, Id) AS rn
                FROM ProductVariants
            )
            UPDATE pv
            SET pv.Barcode = RIGHT('0000000000' + CAST(n.rn AS varchar(10)), 10)
            FROM ProductVariants pv
            JOIN numbered n ON n.Id = pv.Id;
            """);

        // Sayacı doldurma sonrasına kur: bir sonraki numara, o lisansta
        // kullanılan en büyük sıranın bir fazlası olmalı. Kurulmasaydı ayırıcı
        // 1'den başlar ve ilk ayırmalar tek tek atlanarak ilerlerdi.
        migrationBuilder.Sql("""
            INSERT INTO BarcodeCounters (LicenseId, [Next])
            SELECT LicenseId, COUNT(*) + 1
            FROM ProductVariants
            GROUP BY LicenseId;
            """);
```

**DİKKAT:** Bu SQL testlerde çalışmıyor — EF InMemory `EnsureCreated()` kullanıyor, göçler hiç uygulanmıyor (bkz. `Program.cs`'teki `IsRelational()` dalı). Doğrulama Görev 16'daki elle prod-öncesi kontrole bırakıldı.

- [ ] **Adım 6: Tüm sunucu testlerini çalıştır**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: tümü passed (~747 + yeni testler).

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.LicenseServer/Data/LicenseDbContext.cs OrderDeck.LicenseServer/Migrations OrderDeck.LicenseServer.Tests/Data/CatalogModelTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): barkodu zorunlu ve lisans içinde benzersiz yap

Mevcut satırlar göçte lisans başına 1'den numaralanıyor; sayaç
kaldığı yerden başlasın diye aynı göçte tohumlanıyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

> **Panel görevleri AYRI DEPODA.** Çalışma dizini
> `C:\Users\burak\source\repos\OrderDeck-Mobile\apps\panel`. Bu depodaki
> commit'ler LiveDeck commit'lerine karışmaz; ayrı dal, ayrı PR.

## Görev 8: Panelde barkod sütunu

**Dosyalar:**
- Değiştir: `src/api/catalog.ts`
- Değiştir: `src/components/catalog/VariantSection.tsx`
- Test: `src/components/catalog/VariantSection.test.tsx`

- [ ] **Adım 1: Başarısız testleri yaz**

`VariantSection.test.tsx` — mock'a yeni hook'u ekle ve testleri yaz:

```tsx
vi.mock("../../api/catalog", () => ({
  useCreateVariantsBulk: () => ({ mutateAsync: state.bulkImpl, isPending: false }),
  useUpdateVariant: () => ({ mutate: state.updateImpl }),
  useDeleteVariant: () => ({ mutate: vi.fn() }),
  useAxisValueSuggestions: () => ({ data: state.suggestions }),
  useNextBarcodes: () => ({ mutateAsync: state.nextBarcodesImpl, isPending: false }),
}));
```

`state` bloğuna ekle:

```tsx
const state = vi.hoisted(() => ({
  bulkImpl: vi.fn(async (_a: unknown) => {}),
  updateImpl: vi.fn((_a: unknown) => {}),
  nextBarcodesImpl: vi.fn(async (_count: number) => ["0000000042"]),
  suggestions: [] as string[],
}));
```

Testler:

```tsx
  it("var olan varyantın barkodunu gösterir", () => {
    const existing = [variant({ id: "v1", axis1Value: "Siyah", barcode: "0000000007" })];
    render(<VariantSection productId="p1" axis1Name="Renk" axis2Name={null} variants={existing} />);

    expect(screen.getByDisplayValue("0000000007")).toBeInTheDocument();
  });

  it("barkod düzenlenip kaydedilince sunucuya gider", async () => {
    state.updateImpl = vi.fn();
    const existing = [variant({ id: "v1", axis1Value: "Siyah", barcode: "0000000007" })];
    render(<VariantSection productId="p1" axis1Name="Renk" axis2Name={null} variants={existing} />);

    const input = screen.getByLabelText("Siyah barkodu");
    await userEvent.clear(input);
    await userEvent.type(input, "8690000000017");
    await userEvent.tab();

    expect(state.updateImpl).toHaveBeenCalledWith({
      productId: "p1",
      id: "v1",
      body: {
        axis1Value: "Siyah",
        axis2Value: null,
        isActive: true,
        barcode: "8690000000017",
      },
    });
  });

  it("değişmeyen barkod isteği tetiklemez", async () => {
    state.updateImpl = vi.fn();
    const existing = [variant({ id: "v1", axis1Value: "Siyah", barcode: "0000000007" })];
    render(<VariantSection productId="p1" axis1Name="Renk" axis2Name={null} variants={existing} />);

    await userEvent.click(screen.getByLabelText("Siyah barkodu"));
    await userEvent.tab();

    expect(state.updateImpl).not.toHaveBeenCalled();
  });

  it("Oluştur yeni numara ayırıp kaydeder", async () => {
    state.updateImpl = vi.fn();
    state.nextBarcodesImpl = vi.fn(async () => ["0000000042"]);
    const existing = [variant({ id: "v1", axis1Value: "Siyah", barcode: "0000000007" })];
    render(<VariantSection productId="p1" axis1Name="Renk" axis2Name={null} variants={existing} />);

    await userEvent.click(screen.getByRole("button", { name: "Siyah için yeni barkod oluştur" }));

    expect(state.nextBarcodesImpl).toHaveBeenCalledWith(1);
    expect(state.updateImpl).toHaveBeenCalledWith(
      expect.objectContaining({ body: expect.objectContaining({ barcode: "0000000042" }) }),
    );
  });
```

- [ ] **Adım 2: Testleri çalıştır, başarısız olduklarını doğrula**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile/apps/panel && npx vitest run src/components/catalog/VariantSection.test.tsx
```

Beklenen: 4 yeni test başarısız (`useNextBarcodes` yok, barkod girdisi render edilmiyor).

- [ ] **Adım 3: API sözleşmesini genişlet**

`src/api/catalog.ts` — `VariantUpsert`'e alan ekle:

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
  /**
   * Boş/yok gönderilirse SUNUCU doldurur (yaratmada sayaçtan ayırır,
   * güncellemede mevcut değeri korur). Yani "barkodsuz varyant" hiçbir
   * yoldan oluşamıyor — panelin bunu zorlaması gerekmiyor.
   */
  barcode?: string | null;
};
```

Dosyanın sonuna hook'u ekle:

```ts
/**
 * Sayaçtan numara ayırır. Ayırma KALICI: dönen numara kullanılmasa bile
 * sayaçta tüketilir. Bu yüzden sadece kullanıcı açıkça "Oluştur"a bastığında
 * çağrılıyor — render sırasında ya da alan odaklanınca değil.
 */
export function useNextBarcodes() {
  return useMutation({
    mutationFn: async (count: number) => {
      const resp = await apiClient.post<{ barcodes: string[] }>(
        `/api/panel/barcodes/next?count=${count}`,
      );
      return resp.data.barcodes;
    },
  });
}
```

- [ ] **Adım 4: `ExistingVariants` satırına barkod alanını ekle**

`src/components/catalog/VariantSection.tsx` — `ExistingVariants` içindeki `<li>`, ad ile butonlar arasına:

```tsx
        <BarcodeCell
          productId={productId}
          variant={v}
          label={label}
          onSave={(barcode) =>
            update.mutate({
              productId,
              id: v.id,
              body: {
                axis1Value: v.axis1Value,
                axis2Value: v.axis2Value,
                isActive: v.isActive,
                barcode,
              },
            })
          }
        />
```

Aynı dosyanın altına bileşeni ekle:

```tsx
/**
 * Barkod hücresi. Değer sunucudan geliyor ve orada zaten dolu; buradaki
 * düzenleme yalnız ÖZEL bir barkodu (tedarikçinin bastığı EAN gibi) elle
 * geçirmek için.
 *
 * Kaydetme `onBlur`'da: her tuş vuruşunda PUT atmak, benzersizlik kontrolünü
 * yarım yazılmış değerler üzerinde çalıştırır ve gereksiz 409 üretirdi.
 * Değer değişmediyse istek hiç atılmıyor.
 */
function BarcodeCell({
  variant,
  label,
  onSave,
}: {
  productId: string;
  variant: Variant;
  label: string;
  onSave: (barcode: string) => void;
}) {
  const initial = variant.barcode ?? "";
  const [value, setValue] = useState(initial);
  const next = useNextBarcodes();
  const [error, setError] = useState<string | null>(null);

  // Sunucu barkodu değiştirdiyse (yeni ayırma, başka sekme) yereli tazele.
  useEffect(() => setValue(initial), [initial]);

  function commit(candidate: string) {
    const trimmed = candidate.trim();
    if (trimmed === initial) return;
    onSave(trimmed);
  }

  return (
    <span className="flex items-center gap-1">
      <input
        type="text"
        inputMode="text"
        maxLength={64}
        value={value}
        aria-label={`${label} barkodu`}
        onChange={(e) => setValue(e.target.value)}
        onBlur={() => commit(value)}
        className="w-32 rounded border border-bg-elevated bg-bg-surface px-2 py-0.5 font-mono text-xs text-text"
      />
      <button
        type="button"
        aria-label={`${label} için yeni barkod oluştur`}
        disabled={next.isPending}
        onClick={async () => {
          setError(null);
          try {
            const [allocated] = await next.mutateAsync(1);
            setValue(allocated);
            onSave(allocated);
          } catch (e) {
            setError(problemMessage(e, "Barkod alınamadı."));
          }
        }}
        className="rounded px-2 py-0.5 text-xs text-text-muted hover:text-accent"
      >
        Oluştur
      </button>
      {error && (
        <span role="alert" className="text-xs text-danger">
          {error}
        </span>
      )}
    </span>
  );
}
```

Dosyanın importlarına `useEffect`, `useState` (zaten varsa ekleme), `useNextBarcodes` ve `type Variant` ekle.

- [ ] **Adım 5: Testleri çalıştır, geçtiğini doğrula**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile/apps/panel && npx vitest run src/components/catalog/VariantSection.test.tsx && npx tsc --noEmit -p tsconfig.json
```

Beklenen: tüm testler passed. **Not:** `tsconfig.json`'da `"files": []` olduğu için `typecheck` bu dosyayı görmüyor olabilir (bilinen sahte-yeşil borcu) — testlerin geçmesi asıl kanıt.

- [ ] **Adım 6: Commit (panel deposunda)**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile && git add apps/panel/src/api/catalog.ts apps/panel/src/components/catalog/VariantSection.tsx apps/panel/src/components/catalog/VariantSection.test.tsx && git commit -m "$(cat <<'EOF'
feat(katalog): varyant satırına barkod sütunu

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 9: Stok aramasında barkod ipucu

**Dosyalar:**
- Değiştir: `src/screens/StokScreen.tsx`
- Test: `src/screens/StokUrunScreen.test.tsx` (aynı ekranın testi; yoksa `StokScreen.test.tsx` oluştur)

- [ ] **Adım 1: Başarısız testi yaz**

```tsx
  it("arama kutusu barkodu da kabul ettiğini söyler", () => {
    render(<StokScreen />);
    expect(
      screen.getByPlaceholderText("Ürün adı, kodu veya barkod"),
    ).toBeInTheDocument();
  });
```

Dosyada mevcut render kurulumu (provider/mock sarmalayıcıları) neyse aynısını kullan.

- [ ] **Adım 2: Testi çalıştır, başarısız olduğunu doğrula**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile/apps/panel && npx vitest run src/screens/StokUrunScreen.test.tsx
```

Beklenen: placeholder "Ürün adı veya kodu" olduğu için başarısız.

- [ ] **Adım 3: İpucu metnini güncelle**

`src/screens/StokScreen.tsx`:

```tsx
        placeholder="Ürün adı, kodu veya barkod"
```

- [ ] **Adım 4: Testi çalıştır, geçtiğini doğrula**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile/apps/panel && npx vitest run
```

Beklenen: tümü passed.

- [ ] **Adım 5: Commit**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile && git add apps/panel/src/screens/StokScreen.tsx apps/panel/src/screens/StokUrunScreen.test.tsx && git commit -m "$(cat <<'EOF'
feat(stok): arama kutusu barkodu da kabul ettiğini söylesin

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

> **Buradan sonrası tekrar LiveDeck deposunda.**

## Görev 10: WPF replikasında barkod indeksi (göç 031)

**Dosyalar:**
- Oluştur: `OrderDeck.Core/Storage/Migrations/031_catalog_variant_barcode_index.sql`
- Test: `OrderDeck.Tests/Storage/MigrationRunnerTests.cs`

- [ ] **Adım 1: Başarısız testi yaz**

`MigrationRunnerTests.cs` — `Run_creates_all_tables_at_version_5_with_dropped_legacy_columns` içinde `version.Should().Be(30);` satırını `31` yap ve testin sonuna ekle:

```csharp
        // Göç 031: okutma yolu barkodu indeksten buluyor. İndekssiz sorgu
        // her okutmada tam tarama olurdu — yayın sırasında hissedilir.
        conn.Query<string>(
            "SELECT name FROM sqlite_master WHERE type='index' AND tbl_name='CatalogVariant'")
            .Should().Contain("IX_CatalogVariant_Barcode");
```

`Run_is_idempotent` içindeki `version.Should().Be(30);` da `31` olacak.

- [ ] **Adım 2: Testi çalıştır, başarısız olduğunu doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests
```

Beklenen: sürüm 30 döndüğü için başarısız.

- [ ] **Adım 3: Göç dosyasını yaz**

`OrderDeck.Core/Storage/Migrations/031_catalog_variant_barcode_index.sql`:

```sql
-- Barkod okutma yolu için indeks. KOLON YENİ DEĞİL: CatalogVariant.Barcode
-- göç 025'ten beri var (replika sunucunun alanlarını birebir taşıyor) ve
-- CatalogSyncService onu zaten yazıyor. Eksik olan tek şey indeksti.
--
-- Neden gerekli: okutma yolu (BroadcastCodeResolver) barkodu tek satır
-- aramasıyla çözüyor. İndekssiz her okutma tam tarama demek — yayın
-- sırasında binlerce satırlık replikada operatörün hissedeceği bir gecikme.
--
-- Neden UNIQUE DEĞİL: benzersizliğin sahibi sunucu ((LicenseId, Barcode)
-- indeksi). Replikada UNIQUE olsaydı, senkron sırasında geçici bir çakışma
-- (bir varyantın barkodu diğerine devredilirken sıra meselesi) INSERT'i
-- düşürür, işlem geri alınır ve katalog senkronu SESSİZCE ölürdü — göç 026
-- döneminde VariantCode NOT NULL yüzünden başımıza gelen tam olarak buydu.
CREATE INDEX IF NOT EXISTS IX_CatalogVariant_Barcode ON CatalogVariant(Barcode);

UPDATE _meta SET SchemaVersion = 31 WHERE Id = 1;
```

Dosyanın `OrderDeck.Core.csproj`'de gömülü kaynak olarak toplandığını doğrula (`Migrations\*.sql` joker varsa ek iş yok; yoksa açık `<EmbeddedResource Include=... />` satırı ekle).

- [ ] **Adım 4: Testi çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~MigrationRunnerTests
```

Beklenen: tümü passed.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.Core/Storage/Migrations/031_catalog_variant_barcode_index.sql OrderDeck.Tests/Storage/MigrationRunnerTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): replikada barkod indeksi (göç 031)

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 11: Replika deposunda barkod sorguları

**Dosyalar:**
- Değiştir: `OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs`
- Test: `OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

Mevcut test dosyasının kalıbını (`InMemorySqlite` + `MigrationRunner` + `repo.Replace(...)`) kullanarak sonuna ekle. Yardımcılar dosyada yoksa aynen bunları yaz:

```csharp
    private static CatalogReplicaRepository Build()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CatalogReplicaRepository(db);
    }

    private static CatalogProduct Product(string id) => new(
        id, null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private static CatalogVariant Variant(string id, string productId, string barcode) =>
        new(id, productId, "Siyah", "M", barcode, true, 0);
```

Testler:

```csharp
    [Fact]
    public void Barkod_varyanti_bulur()
    {
        var repo = Build();
        repo.Replace(
            new[] { Product("p1") },
            new[] { Variant("v1", "p1", barcode: "0000000007") },
            Array.Empty<CatalogCategory>(),
            Array.Empty<CatalogBroadcastCode>());

        repo.FindVariantByBarcode("0000000007")!.Id.Should().Be("v1");
    }

    [Fact]
    public void Barkod_aramasi_bosluklari_kirpar()
    {
        var repo = Build();
        repo.Replace(
            new[] { Product("p1") },
            new[] { Variant("v1", "p1", barcode: "0000000007") },
            Array.Empty<CatalogCategory>(),
            Array.Empty<CatalogBroadcastCode>());

        // Okutucu klavye taklidi yapıyor; başa/sona boşluk düşebiliyor.
        repo.FindVariantByBarcode("  0000000007  ")!.Id.Should().Be("v1");
    }

    [Fact]
    public void Barkod_aramasi_harf_duyarli()
    {
        var repo = Build();
        repo.Replace(
            new[] { Product("p1") },
            new[] { Variant("v1", "p1", barcode: "AB12") },
            Array.Empty<CatalogCategory>(),
            Array.Empty<CatalogBroadcastCode>());

        // Yük opak: "ab12" BAŞKA bir barkod olabilir. Normalize etmek,
        // yanlış ürünü açmaya yol açardı.
        repo.FindVariantByBarcode("ab12").Should().BeNull();
    }

    [Fact]
    public void Bos_barkod_null_doner()
    {
        var repo = Build();
        repo.FindVariantByBarcode("   ").Should().BeNull();
    }

    [Fact]
    public void Urunun_yayin_kodlari_sirayla_doner()
    {
        var repo = Build();
        repo.Replace(
            new[] { Product("p1") },
            Array.Empty<CatalogVariant>(),
            Array.Empty<CatalogCategory>(),
            new[]
            {
                new CatalogBroadcastCode("p1", "Siyah", "ATES", "ATES", 0, 1),
                new CatalogBroadcastCode("p1", null, "KAR", "KAR", 0, 0),
            });

        repo.GetBroadcastCodes("p1").Select(c => c.Code)
            .Should().Equal("KAR", "ATES");
    }
```

`Build()`, `Product(...)`, `Variant(...)` yardımcıları dosyada varsa onları kullan; yoksa `BroadcastCodeResolverTests`'teki karşılıklarını bu dosyaya taşımadan, aynı imzayla yerel olarak yaz.

- [ ] **Adım 2: Testleri çalıştır, derlenmediğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogReplicaRepositoryTests
```

Beklenen: derleme hatası — `FindVariantByBarcode` / `GetBroadcastCodes` yok.

- [ ] **Adım 3: Sorguları ekle**

`CatalogReplicaRepository.cs` — `GetVariants`'ın yanına:

```csharp
    /// <summary>
    /// Barkodu birebir eşleşen varyant, yoksa <c>null</c>.
    ///
    /// <para><b>Normalize EDİLMEZ</b> (yayın kodunun aksine): barkod yükü opak
    /// bir dizgi, okutucu onu karakteri karakterine üretiyor. Normalize etmek
    /// "AB12" ile "ab12"yi aynı sayardı — bunlar farklı iki varyant olabilir
    /// ve okutma yanlış ürünü açardı. Yalnız baştaki/sondaki boşluk kırpılır:
    /// okutucular klavye taklidi yapıyor ve sonda bir Enter/boşluk bırakabiliyor.</para>
    ///
    /// <para><c>LIMIT 1</c>: benzersizliğin sahibi sunucu. Replikada indeks
    /// UNIQUE değil (gerekçesi göç 031'de), yani teorik bir çift satır
    /// sorguyu patlatmak yerine ilkini döndürsün.</para>
    /// </summary>
    public CatalogVariant? FindVariantByBarcode(string? barcode)
    {
        var needle = (barcode ?? string.Empty).Trim();
        if (needle.Length == 0) return null;

        using var conn = _factory.Open();
        return conn.Query<VariantRow>(
            """
            SELECT Id, ProductId, Axis1Value, Axis2Value, Barcode, IsActive, SortOrder
            FROM CatalogVariant
            WHERE Barcode = @needle
            LIMIT 1
            """, new { needle })
            .Select(r => new CatalogVariant(
                r.Id, r.ProductId, r.Axis1Value, r.Axis2Value,
                r.Barcode, r.IsActive == 1, r.SortOrder))
            .FirstOrDefault();
    }

    /// <summary>
    /// Ürünün yayın kodları, panelde verilen sırayla.
    ///
    /// <para>Barkod okutma yolu için: barkod varyanta, varyant ürüne çözülüyor
    /// ama akışın geri kalanı bir YAYIN KODU bekliyor. Sıra önemli — birden
    /// çok kod varsa operatörün panelde ilk sıraya koyduğu kod gösterilir.</para>
    /// </summary>
    public IReadOnlyList<CatalogBroadcastCode> GetBroadcastCodes(string productId)
    {
        using var conn = _factory.Open();
        return conn.Query<BroadcastCodeRow>(
            """
            SELECT ProductId, SellerAxisValue, Code, CodeNormalized, CreatedAt, SortOrder
            FROM CatalogBroadcastCode
            WHERE ProductId = @productId
            ORDER BY SortOrder, Code
            """, new { productId })
            .Select(r => new CatalogBroadcastCode(
                r.ProductId, r.SellerAxisValue, r.Code, r.CodeNormalized,
                r.CreatedAt, r.SortOrder))
            .ToList();
    }
```

- [ ] **Adım 4: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~CatalogReplicaRepositoryTests
```

Beklenen: tümü passed.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.Core/Storage/Repositories/CatalogReplicaRepository.cs OrderDeck.Tests/Storage/CatalogReplicaRepositoryTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): replikada barkod ve yayın kodu sorguları

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 12: Kod kutusunda barkod yedek yolu

**Dosyalar:**
- Değiştir: `OrderDeck.Core/Catalog/BroadcastCodeResolver.cs`
- Test: `OrderDeck.Tests/Catalog/BroadcastCodeResolverTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

`BroadcastCodeResolverTests.cs` sonuna (dosyadaki `Elbise()`, `V(...)`, `Build(...)` yardımcılarını kullan):

```csharp
    [Fact]
    public void Barkod_urunu_ve_satici_ekseni_degerini_cozer()
    {
        var sut = Build(Elbise(),
            new[]
            {
                V("v1", "Siyah", "M") with { Barcode = "0000000001" },
                V("v2", "Beyaz", "M") with { Barcode = "0000000002" },
            },
            new[]
            {
                new CatalogBroadcastCode("p1", "Siyah", "ATES", "ATES", 0, 0),
                new CatalogBroadcastCode("p1", "Beyaz", "KAR", "KAR", 0, 1),
            });

        var hit = sut.Resolve("0000000002");

        // Okutulan parça Beyaz; kart Beyaz kırılımını açmalı, ilk kodu değil.
        hit!.Product.Id.Should().Be("p1");
        hit.Code.Should().Be("KAR");
        hit.SellerAxisValue.Should().Be("Beyaz");
    }

    [Fact]
    public void Yayin_kodu_barkoda_gore_oncelikli()
    {
        // Aynı metin hem yayın kodu hem barkod olsaydı yayın kodu kazanır:
        // operatörün ağzından çıkan kod, elindeki parçadan önce gelir.
        var sut = Build(Elbise(),
            new[] { V("v1", "Siyah", "M") with { Barcode = "ATES" } },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "ATES", "ATES", 0, 0) });

        sut.Resolve("ATES")!.Code.Should().Be("ATES");
    }

    [Fact]
    public void Yayin_kodu_olmayan_urunun_barkodu_reddedilir()
    {
        var sut = Build(Elbise(),
            new[] { V("v1", "Siyah", "M") with { Barcode = "0000000001" } },
            Array.Empty<CatalogBroadcastCode>());

        // Kart bir YAYIN KODU gösteriyor; kodu olmayan ürünü açmak, operatöre
        // izleyicilere söyleyeceği kodu olmayan bir ürün göstermek olurdu.
        sut.Resolve("0000000001").Should().BeNull();
    }

    [Fact]
    public void Barkodun_satici_degerine_kod_yoksa_reddedilir()
    {
        var sut = Build(Elbise(),
            new[] { V("v1", "Beyaz", "M") with { Barcode = "0000000002" } },
            new[] { new CatalogBroadcastCode("p1", "Siyah", "ATES", "ATES", 0, 0) });

        // Beyaz kırılımının kodu yok. "ATES"e düşmek yanlış rengi açardı.
        sut.Resolve("0000000002").Should().BeNull();
    }

    [Fact]
    public void Eksensiz_urunde_barkod_tek_koda_coz()
    {
        var product = new CatalogProduct(
            "p2", null, "SK00002", "SK00002", "Çanta", 50m, null,
            null, null, null, null, null, 0);
        var sut = Build(product,
            new[] { new CatalogVariant("v9", "p2", null, null, "0000000009", true, 0) },
            new[] { new CatalogBroadcastCode("p2", null, "CANTA", "CANTA", 0, 0) });

        sut.Resolve("0000000009")!.Code.Should().Be("CANTA");
    }
```

`Build(...)` bugün `Elbise()` tipini alıyorsa imzasını `CatalogProduct` alacak şekilde genelleştir (zaten öyleyse dokunma).

- [ ] **Adım 2: Testleri çalıştır, başarısız olduklarını doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BroadcastCodeResolverTests
```

Beklenen: barkod testleri `null` döndüğü için başarısız (`Yayin_kodu_barkoda_gore_oncelikli` ve `..._reddedilir` zaten geçer).

- [ ] **Adım 3: Yedek yolu ekle**

`BroadcastCodeResolver.cs` — `Resolve`'un ilk satırını değiştir ve yeni metodu ekle:

```csharp
    /// <summary>
    /// Kutuya yazılan/okutulan metni çözer.
    ///
    /// <para><b>Sıra:</b> önce yayın kodu, sonra barkod. Aynı metin ikisinde
    /// birden olamaz — sunucu 10 haneli saf sayıyı yayın kodu olarak
    /// reddediyor — ama sıra yine de anlamlı: operatörün ağzından çıkan kod,
    /// elindeki parçadan önce gelir.</para>
    /// </summary>
    public BroadcastCodeResolution? Resolve(string? code)
    {
        var hit = _repo.FindBroadcastCode(code) ?? ResolveByBarcode(code);
        if (hit is null) return null;

        var product = _repo.GetProductById(hit.ProductId);
        if (product is null) return null;

        var sellerAxis = AxisIndexOf(product, SellerRole);
        var viewerAxis = AxisIndexOf(product, ViewerRole);

        var variants = _repo.GetVariants(product.Id)
            .Where(v => v.IsActive)
            .Where(v => sellerAxis == 0 || Same(AxisValue(v, sellerAxis), hit.SellerAxisValue))
            .ToList();

        var viewerValues = viewerAxis == 0
            ? Array.Empty<string>()
            : variants.Select(v => AxisValue(v, viewerAxis))
                      .Where(v => !string.IsNullOrWhiteSpace(v))
                      .Select(v => v!.Trim())
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();

        return new BroadcastCodeResolution(
            product, hit.Code, hit.SellerAxisValue,
            viewerAxis == 0 ? null : AxisName(product, viewerAxis),
            viewerAxis, variants, viewerValues);
    }

    /// <summary>
    /// Barkodu bir <see cref="CatalogBroadcastCode"/>'a indirger.
    ///
    /// <para><b>Neden koda indirgeniyor:</b> barkod bir varyantı gösteriyor
    /// ama kartın ve sipariş akışının tamamı bir yayın kodu üzerinden
    /// çalışıyor. Barkodu koda çevirince aşağıdaki gövde — varyant süzme,
    /// izleyici ekseni, stok rozetleri — hiç değişmeden çalışıyor. Ayrı bir
    /// çözümleme dalı yazsaydık iki yol zamanla ayrışırdı.</para>
    ///
    /// <para><b>Kodu olmayan ürün reddedilir</b> (<c>null</c> → kartta
    /// "katalogda yok"): kart yayın kodunu gösteriyor, kodu olmayan bir ürünü
    /// açmak operatöre izleyicilere söyleyecek kodu olmayan bir ürün
    /// göstermek olurdu.</para>
    ///
    /// <para>Satıcı ekseni eşleşmesi C#'ta, <see cref="Same"/> ile: SQLite'ta
    /// Türkçe katlama yok, SQL'de karşılaştırmak "İ/ı" çiftlerini kaçırırdı.</para>
    /// </summary>
    private CatalogBroadcastCode? ResolveByBarcode(string? barcode)
    {
        var variant = _repo.FindVariantByBarcode(barcode);
        if (variant is null) return null;

        var product = _repo.GetProductById(variant.ProductId);
        if (product is null) return null;

        var codes = _repo.GetBroadcastCodes(product.Id);
        if (codes.Count == 0) return null;

        var sellerAxis = AxisIndexOf(product, SellerRole);
        if (sellerAxis == 0) return codes[0];

        var sellerValue = AxisValue(variant, sellerAxis);
        return codes.FirstOrDefault(c => Same(c.SellerAxisValue, sellerValue));
    }
```

- [ ] **Adım 4: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BroadcastCodeResolverTests
```

Beklenen: tümü passed.

- [ ] **Adım 5: Commit**

```bash
git add OrderDeck.Core/Catalog/BroadcastCodeResolver.cs OrderDeck.Tests/Catalog/BroadcastCodeResolverTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): kod kutusunda barkod yedek yolu

Barkod bir yayın koduna indirgeniyor; kartın ve sipariş akışının
geri kalanı değişmeden çalışıyor.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 13: Kod kutusu barkodu kabul etsin

**Dosyalar:**
- Değiştir: `OrderDeck.App/Views/Shell/ActiveProductBar.xaml`

- [ ] **Adım 1: Kutuyu düzelt**

Satır 18 civarındaki `TextBox`:

```xml
<TextBox x:Name="CodeBox"
         MaxLength="64"
         ... (diğer öznitelikler aynı) />
```

- `MaxLength` 32 → **64**: `CatalogLimits.Barcode` ile aynı. 32'de kalsaydı uzun bir tedarikçi barkodu okutulurken sessizce kırpılır, hiçbir ürüne çözülmezdi.
- `CharacterCasing="Upper"` **kaldırılır**: yayın kodu için zararsızdı (normalize ediliyor) ama barkod yükü opak — okutucunun ürettiği "ab12" büyütülünce BAŞKA bir barkod olur ve eşleşme kaçar.

Kaldırılan özniteliğin yerine XAML yorumu bırak:

```xml
<!-- CharacterCasing YOK: barkod yükü opak, büyütmek eşleşmeyi kaçırır.
     Yayın kodu zaten SearchNormalizer'da katlanıyor, görsel büyütmeye
     ihtiyacı yok. -->
```

- [ ] **Adım 2: Derle**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
```

Beklenen: 0 hata.

- [ ] **Adım 3: Commit**

```bash
git add OrderDeck.App/Views/Shell/ActiveProductBar.xaml
git commit -m "$(cat <<'EOF'
fix(barkod): kod kutusu 64 karaktere çıksın, büyütme kalksın

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 14: Code128 etiket belgesi

**Dosyalar:**
- Değiştir: `Directory.Packages.props`, `OrderDeck.Labeling/OrderDeck.Labeling.csproj`
- Oluştur: `OrderDeck.Labeling/BarcodeLabelDocument.cs`, `OrderDeck.Labeling/BarcodeLabelPrinter.cs`
- Test: `OrderDeck.Tests/Labeling/BarcodeLabelDocumentTests.cs`

- [ ] **Adım 1: Paketi ekle**

`Directory.Packages.props`, "Domain dependencies" bloğuna:

```xml
    <!-- Code128 modül üretimi. LİSANS: Apache 2.0 (2026-08-16 doğrulandı) —
         ticari kullanım serbest, atıf yeterli. 0.16.11'de çekirdek paket
         GÖRÜNTÜ KÜTÜPHANESİNDEN BAĞIMSIZ: yalnız bool[] modül dizisi
         üretiyor, çizimi biz yapıyoruz. Bu yüzden System.Drawing.Common'a
         zorlamıyor ve ileride sunucu tarafında da kullanılabilir.
         (Elenenler: QuestPDF = 1M$ eşikli ticari lisans; iText7 = AGPL;
         BarcodeLib = System.Drawing.Common'a bağımlı, Windows'a çivili.) -->
    <PackageVersion Include="ZXing.Net" Version="0.16.11" />
```

`OrderDeck.Labeling/OrderDeck.Labeling.csproj`:

```xml
    <PackageReference Include="ZXing.Net" />
```

- [ ] **Adım 2: Başarısız testleri yaz**

`OrderDeck.Tests/Labeling/BarcodeLabelDocumentTests.cs`:

```csharp
using FluentAssertions;
using OrderDeck.Labeling;
using Xunit;

namespace OrderDeck.Tests.Labeling;

public class BarcodeLabelDocumentTests
{
    [Fact]
    public void Modul_dizisi_sessiz_bolgeyle_cevrelenir()
    {
        var modules = BarcodeLabelDocument.EncodeWithQuietZone("0000000001");

        // Sessiz bölge okuyucunun barkodun NEREDE bittiğini anlamasını
        // sağlıyor; ZXing'in encode'u onu vermiyor, biz ekliyoruz.
        modules.Take(BarcodeLabelDocument.QuietZoneModules)
            .Should().OnlyContain(m => m == false);
        modules.TakeLast(BarcodeLabelDocument.QuietZoneModules)
            .Should().OnlyContain(m => m == false);
    }

    [Fact]
    public void Ilk_cizgi_sessiz_bolgeden_hemen_sonra_baslar()
    {
        var modules = BarcodeLabelDocument.EncodeWithQuietZone("0000000001");

        modules[BarcodeLabelDocument.QuietZoneModules].Should().BeTrue();
    }

    [Fact]
    public void On_haneli_sayi_makul_genislikte()
    {
        var modules = BarcodeLabelDocument.EncodeWithQuietZone("0000000001");

        // Code128-C 10 haneyi 5 çift olarak sıkıştırıyor: start + 5 veri +
        // checksum + stop ≈ 90 modül, + 20 sessiz bölge. 60 mm etikete
        // 0.4 mm modülle (≈44 mm) rahat sığıyor.
        modules.Length.Should().BeInRange(100, 130);
    }

    [Fact]
    public void Bos_yuk_reddedilir()
    {
        var act = () => BarcodeLabelDocument.EncodeWithQuietZone("");

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MmToHundredths_LabelPrintDocument_ile_ayni()
    {
        // İki belge aynı yazıcıya, aynı kâğıda basıyor. Ölçü dönüşümü
        // ayrışsaydı biri kâğıda otururken diğeri kayardı.
        BarcodeLabelDocument.MmToHundredths(60)
            .Should().Be(LabelPrintDocument.MmToHundredths(60));
    }
}
```

- [ ] **Adım 3: Testleri çalıştır, derlenmediğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BarcodeLabelDocumentTests
```

Beklenen: derleme hatası — `BarcodeLabelDocument` yok.

- [ ] **Adım 4: Belgeyi yaz**

`OrderDeck.Labeling/BarcodeLabelDocument.cs`:

```csharp
using System.Drawing;
using System.Drawing.Printing;
using System.Runtime.Versioning;

namespace OrderDeck.Labeling;

/// <summary>
/// Barkodlu ürün etiketi. Müşteri etiketinden (<see cref="LabelPrintDocument"/>)
/// AYRI: yükü de düzeni de farklı, ortak soyutlama ikisini de bulandırırdı.
///
/// <para><b>Neden vektör:</b> barkod raster görüntü olarak basılsaydı
/// yazıcının 203 dpi ızgarası ile görüntünün pikselleri hizalanmaz, çizgi
/// kalınlıkları bir nokta oynar ve okuma oranı düşerdi. Dikdörtgen olarak
/// çizince sürücü ızgaraya kendisi oturtuyor.</para>
///
/// <para><b>Modül genişliği:</b> 60 mm etikete 0.4 mm modülle basıyoruz
/// (10 hane ≈ 44 mm). Standardın izin verdiği asgari 0.25 mm'ye inmiyoruz:
/// 203 dpi yazıcının nokta boyu 0.125 mm, yani 0.25 mm modül tam iki nokta —
/// bir noktalık sapma çizgiyi %50 bozar. 0.4 mm'de sapma payı var.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class BarcodeLabelDocument
{
    /// <summary>Standardın istediği asgari 10 modül.</summary>
    public const int QuietZoneModules = 10;

    /// <summary>Modül genişliği (mm). Gerekçe sınıf doc'unda.</summary>
    public const float ModuleWidthMm = 0.4f;

    /// <summary>Çizgi yüksekliği (mm).</summary>
    public const float BarHeightMm = 12f;

    public static int MmToHundredths(int mm) => (int)Math.Round(mm * 100.0 / 25.4);

    /// <summary>
    /// Code128 modül dizisi + iki uçta sessiz bölge.
    ///
    /// <para>ZXing'in <c>encode</c>'u sessiz bölge VERMİYOR — yalnız çizgi
    /// desenini döndürüyor. Eklemeseydik okuyucu barkodun nerede bittiğini
    /// anlayamaz, etiketin kenarındaki mürekkebi veri sanardı.</para>
    /// </summary>
    public static bool[] EncodeWithQuietZone(string payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
            throw new ArgumentException("Barkod yükü boş olamaz.", nameof(payload));

        var bars = new ZXing.OneD.Code128Writer().encode(payload);

        var result = new bool[bars.Length + QuietZoneModules * 2];
        for (var i = 0; i < bars.Length; i++)
            result[QuietZoneModules + i] = bars[i];
        return result;
    }

    /// <summary>Tek etikette basılacak içerik.</summary>
    public sealed record Label(string Barcode, string ProductName, string VariantName);

    /// <summary>
    /// Her etiketi <paramref name="copies"/> kez basan bir belge kurar.
    /// Sayfa başına tek etiket — rulo yazıcıda "sayfa" zaten bir etiket.
    /// </summary>
    public static PrintDocument Build(
        IReadOnlyList<Label> labels, int copies,
        string printerName, int widthMm, int heightMm, string fontFamily)
    {
        if (labels.Count == 0)
            throw new ArgumentException("Basılacak etiket yok.", nameof(labels));
        if (copies <= 0)
            throw new ArgumentOutOfRangeException(nameof(copies));

        var queue = new List<Label>(labels.Count * copies);
        foreach (var label in labels)
            for (var i = 0; i < copies; i++)
                queue.Add(label);

        var doc = new PrintDocument();
        doc.PrinterSettings.PrinterName = printerName;
        doc.DefaultPageSettings.Margins = new Margins(0, 0, 0, 0);
        doc.DefaultPageSettings.PaperSize = new PaperSize(
            "LabelBarcode", MmToHundredths(widthMm), MmToHundredths(heightMm));

        var index = 0;
        doc.PrintPage += (_, e) =>
        {
            DrawLabel(e.Graphics!, queue[index], widthMm, heightMm, fontFamily);
            index++;
            e.HasMorePages = index < queue.Count;
        };
        return doc;
    }

    private static void DrawLabel(
        Graphics g, Label label, int widthMm, int heightMm, string fontFamily)
    {
        // Grafik birimi milimetreye çevriliyor: bütün ölçüler etiketin
        // fiziksel boyutuyla aynı dilde olsun, dpi hesabı tek yerde kalsın.
        g.PageUnit = GraphicsUnit.Millimeter;

        var modules = EncodeWithQuietZone(label.Barcode);
        var barcodeWidth = modules.Length * ModuleWidthMm;
        var left = Math.Max(0f, (widthMm - barcodeWidth) / 2f);

        using var nameFont = new Font(fontFamily, 3f, FontStyle.Bold);
        using var variantFont = new Font(fontFamily, 2.5f);
        using var codeFont = new Font(fontFamily, 2.5f);
        using var black = new SolidBrush(Color.Black);

        var y = 1.5f;
        g.DrawString(
            TruncateToWidth(g, label.ProductName, nameFont, widthMm - 2f),
            nameFont, black, 1f, y);
        y += 4f;

        if (label.VariantName.Length > 0)
        {
            g.DrawString(
                TruncateToWidth(g, label.VariantName, variantFont, widthMm - 2f),
                variantFont, black, 1f, y);
            y += 3.5f;
        }

        for (var i = 0; i < modules.Length; i++)
            if (modules[i])
                g.FillRectangle(black, left + i * ModuleWidthMm, y, ModuleWidthMm, BarHeightMm);

        y += BarHeightMm + 1f;

        // İnsan tarafından okunabilir satır: okuyucu çalışmazsa operatör
        // numarayı elle yazabilsin.
        var size = g.MeasureString(label.Barcode, codeFont);
        g.DrawString(label.Barcode, codeFont, black, (widthMm - size.Width) / 2f, y);
    }

    /// <summary>
    /// Sığmayan metni "…" ile kırpar. <see cref="LabelPrintDocument"/>'teki
    /// kardeşiyle aynı davranış; ayrı duruyorlar çünkü o sınıf iç kullanım
    /// için özel ve iki belge birbirine bağlanmasın isteniyor.
    /// </summary>
    private static string TruncateToWidth(Graphics g, string text, Font font, float maxWidth)
    {
        if (g.MeasureString(text, font).Width <= maxWidth) return text;

        for (var len = text.Length - 1; len > 0; len--)
        {
            var candidate = text[..len] + "…";
            if (g.MeasureString(candidate, font).Width <= maxWidth) return candidate;
        }
        return "…";
    }
}
```

- [ ] **Adım 5: Yazıcı sarmalayıcısını yaz**

`OrderDeck.Labeling/BarcodeLabelPrinter.cs`:

```csharp
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using OrderDeck.Core.Settings;

namespace OrderDeck.Labeling;

/// <summary>
/// Barkodlu etiketi yazıcıya gönderir. <see cref="LabelPrinter"/>'dan ayrı bir
/// sınıf: yükü farklı (müşteri/mesaj değil, ürün/varyant/barkod), ortak bir
/// arayüze zorlamak ikisini de bulandırırdı.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class BarcodeLabelPrinter
{
    private readonly AppSettings _settings;
    private readonly ILogger<BarcodeLabelPrinter>? _log;

    public BarcodeLabelPrinter(AppSettings settings, ILogger<BarcodeLabelPrinter>? log = null)
    {
        _settings = settings;
        _log = log;
    }

    public void Print(IReadOnlyList<BarcodeLabelDocument.Label> labels, int copies)
    {
        using var doc = BarcodeLabelDocument.Build(
            labels, copies,
            _settings.PrinterName,
            _settings.LabelWidthMm,
            _settings.LabelHeightMm,
            _settings.LabelFontFamily);

        var started = DateTimeOffset.UtcNow;
        doc.Print();
        var elapsed = DateTimeOffset.UtcNow - started;
        if (elapsed > TimeSpan.FromSeconds(10))
            _log?.LogWarning(
                "Barkod etiketi basımı {Seconds:F1} sn sürdü ({Count} etiket).",
                elapsed.TotalSeconds, labels.Count * copies);
    }
}
```

`LabelPrinter`'ın DI kaydının yanına (`OrderDeck.App/AppHost.cs`) ekle:

```csharp
services.AddSingleton<BarcodeLabelPrinter>();
```

- [ ] **Adım 6: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BarcodeLabelDocumentTests
```

Beklenen: 5 passed.

- [ ] **Adım 7: Commit**

```bash
git add Directory.Packages.props OrderDeck.Labeling/OrderDeck.Labeling.csproj OrderDeck.Labeling/BarcodeLabelDocument.cs OrderDeck.Labeling/BarcodeLabelPrinter.cs OrderDeck.App/AppHost.cs OrderDeck.Tests/Labeling/BarcodeLabelDocumentTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): Code128 etiket belgesi ve yazıcısı

Barkod vektör dikdörtgen olarak çiziliyor: raster görüntü 203 dpi
ızgarasına oturmaz ve okuma oranını düşürürdü.

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 15: "Etiket bas" çekmecesi

**Dosyalar:**
- Oluştur: `OrderDeck.App/Views/Drawers/BarcodeLabelDrawer.xaml` + `.xaml.cs`
- Oluştur: `OrderDeck.App/ViewModels/BarcodeLabelViewModel.cs`
- Değiştir: `OrderDeck.App/Views/Shell/ProductCard.xaml`, `OrderDeck.App/ViewModels/MainShellViewModel.cs`
- Test: `OrderDeck.Tests/ViewModels/BarcodeLabelViewModelTests.cs`

- [ ] **Adım 1: Başarısız testleri yaz**

```csharp
using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

public class BarcodeLabelViewModelTests
{
    private static CatalogProduct Elbise() => new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private static CatalogVariant V(string id, string renk, string beden, string barcode) =>
        new(id, "p1", renk, beden, barcode, true, 0);

    [Fact]
    public void Cozulmus_urunun_varyantlarini_listeler()
    {
        var sut = new BarcodeLabelViewModel();

        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001"),
                    V("v2", "Siyah", "L", "0000000002") },
            new[] { "M", "L" }));

        sut.Rows.Should().HaveCount(2);
        sut.Rows[0].Barcode.Should().Be("0000000001");
    }

    [Fact]
    public void Varsayilan_adet_birdir()
    {
        new BarcodeLabelViewModel().Copies.Should().Be(1);
    }

    [Fact]
    public void Hicbir_satir_secili_degilse_basilamaz()
    {
        var sut = new BarcodeLabelViewModel();
        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001") },
            new[] { "M" }));

        sut.Rows[0].IsSelected = false;

        sut.CanPrint.Should().BeFalse();
    }

    [Fact]
    public void Secili_satirlar_etikete_cevrilir()
    {
        var sut = new BarcodeLabelViewModel();
        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001"),
                    V("v2", "Siyah", "L", "0000000002") },
            new[] { "M", "L" }));
        sut.Rows[1].IsSelected = false;

        var labels = sut.BuildLabels();

        labels.Should().ContainSingle();
        labels[0].Barcode.Should().Be("0000000001");
        labels[0].ProductName.Should().Be("Elbise");
        labels[0].VariantName.Should().Be("Siyah · M");
    }
}
```

- [ ] **Adım 2: Testleri çalıştır, derlenmediğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BarcodeLabelViewModelTests
```

Beklenen: derleme hatası — `BarcodeLabelViewModel` yok.

- [ ] **Adım 3: ViewModel'i yaz**

`OrderDeck.App/ViewModels/BarcodeLabelViewModel.cs`:

```csharp
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using OrderDeck.Core.Catalog;
using OrderDeck.Labeling;

namespace OrderDeck.App.ViewModels;

/// <summary>
/// "Etiket bas" çekmecesi. Kartta AÇIK olan ürünün varyantlarını listeler,
/// operatör hangilerini kaç kez basacağını seçer.
///
/// <para><b>Yazıcıya dokunmuyor:</b> yalnız <see cref="BuildLabels"/> ile
/// yükü hazırlıyor. Basma, çekmecenin arkasındaki kod-behind'de
/// <c>BarcodeLabelPrinter</c> ile yapılıyor — böylece bu sınıf
/// <c>System.Drawing.Printing</c>'e ve Windows'a bağlanmadan test edilebiliyor.</para>
/// </summary>
public sealed partial class BarcodeLabelViewModel : ObservableObject
{
    public ObservableCollection<BarcodeLabelRow> Rows { get; } = new();

    [ObservableProperty]
    private string _productName = string.Empty;

    /// <summary>Etiket başına kopya. 1: operatör çoğunlukla tek parça etiketliyor.</summary>
    [ObservableProperty]
    private int _copies = 1;

    public bool CanPrint => Rows.Any(r => r.IsSelected);

    public void Load(BroadcastCodeResolution? resolution)
    {
        Rows.Clear();
        ProductName = resolution?.Product.Name ?? string.Empty;
        if (resolution is null) return;

        foreach (var v in resolution.Variants)
        {
            // Barkodsuz varyant sunucuda var olamaz; yine de replikada bayat
            // bir satır olabilir (senkron turu gelmemiş). Basılamayanı
            // listeye almıyoruz — boş barkodla etiket basmak okunamayan
            // bir çıktı üretirdi.
            if (string.IsNullOrWhiteSpace(v.Barcode)) continue;
            Rows.Add(new BarcodeLabelRow(v.Barcode!, Describe(v)));
        }

        OnPropertyChanged(nameof(CanPrint));
    }

    public IReadOnlyList<BarcodeLabelDocument.Label> BuildLabels() =>
        Rows.Where(r => r.IsSelected)
            .Select(r => new BarcodeLabelDocument.Label(r.Barcode, ProductName, r.Display))
            .ToList();

    private static string Describe(CatalogVariant v)
    {
        var parts = new[] { v.Axis1Value, v.Axis2Value }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Trim());
        return string.Join(" · ", parts);
    }
}

public sealed partial class BarcodeLabelRow : ObservableObject
{
    public BarcodeLabelRow(string barcode, string display)
    {
        Barcode = barcode;
        Display = display;
    }

    public string Barcode { get; }
    public string Display { get; }

    /// <summary>Varsayılan seçili: operatör çoğunlukla hepsini basıyor.</summary>
    [ObservableProperty]
    private bool _isSelected = true;
}
```

`CanPrint`'in satır seçimi değişince tazelenmesi için `Load` sonunda her satırın `PropertyChanged`'ine abone ol:

```csharp
            row.PropertyChanged += (_, _) => OnPropertyChanged(nameof(CanPrint));
```

(`Rows.Add` çağrısını `var row = new BarcodeLabelRow(...)` ile ikiye böl.)

- [ ] **Adım 4: Testleri çalıştır, geçtiğini doğrula**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj --filter FullyQualifiedName~BarcodeLabelViewModelTests
```

Beklenen: 4 passed.

- [ ] **Adım 5: Çekmeceyi ve düğmeyi bağla**

`OrderDeck.App/Views/Drawers/BarcodeLabelDrawer.xaml` — `VariantPickerDrawer` ile aynı iskelet ve aynı `OD.*` stil kaynakları:

```xml
<UserControl x:Class="OrderDeck.App.Views.Drawers.BarcodeLabelDrawer"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <!-- Etiket basma çekmecesi. Başlık şeritten geliyor; gövde ürün adını,
         varyant satırlarını ve adet kutusunu taşıyor. Satırlar varsayılan
         SEÇİLİ: operatör çoğunlukla hepsini basıyor, tersini seçmek istisna. -->
    <StackPanel Margin="{StaticResource OD.Pad.5}" VerticalAlignment="Top">
        <TextBlock Text="{Binding ProductName}"
                   Style="{StaticResource OD.Text.Section}"/>

        <ItemsControl ItemsSource="{Binding Rows}"
                      Margin="{StaticResource OD.Pad.Top5}">
            <ItemsControl.ItemTemplate>
                <DataTemplate>
                    <DockPanel Margin="{StaticResource OD.Pad.Bottom3}">
                        <TextBlock DockPanel.Dock="Right"
                                   Text="{Binding Barcode}"
                                   FontFamily="Consolas"
                                   Style="{StaticResource OD.Text.Hint}"/>
                        <CheckBox Content="{Binding Display}"
                                  Style="{StaticResource OD.CheckBox}"
                                  IsChecked="{Binding IsSelected, Mode=TwoWay}"/>
                    </DockPanel>
                </DataTemplate>
            </ItemsControl.ItemTemplate>
        </ItemsControl>

        <TextBlock Text="Adet (her satır için)"
                   Style="{StaticResource OD.Text.Hint}"
                   Margin="{StaticResource OD.Pad.Top4}"/>
        <TextBox Text="{Binding Copies, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                 MaxLength="3"/>

        <!-- Hiçbir satır seçili değilken kapalı: boş iş yazıcıya gitmesin. -->
        <Button Content="Bas"
                Style="{StaticResource OD.Button.Primary}"
                Margin="{StaticResource OD.Pad.Top5}"
                IsDefault="True"
                IsEnabled="{Binding CanPrint}"
                Click="Print_OnClick"/>

        <Button Content="Vazgeç"
                Style="{StaticResource OD.Button.Ghost}"
                HorizontalContentAlignment="Center"
                Margin="{StaticResource OD.Pad.Top4}"
                Click="Cancel_OnClick"/>
    </StackPanel>
</UserControl>
```

`OrderDeck.App/Views/Drawers/BarcodeLabelDrawer.xaml.cs`:

```csharp
using System.Windows;
using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.App.Services.Drawers;
using OrderDeck.App.ViewModels;
using OrderDeck.Labeling;

namespace OrderDeck.App.Views.Drawers;

/// <summary>
/// Etiket basma çekmecesi. Kurucusu private + statik <c>Create</c>:
/// <c>VariantPickerDrawer</c> ile aynı kalıp.
///
/// <para>Yazıcı burada çözülüyor, ViewModel'de değil: <c>BarcodeLabelPrinter</c>
/// <c>[SupportedOSPlatform("windows")]</c> ve <c>System.Drawing.Printing</c>'e
/// bağlı. ViewModel'e enjekte etseydik onu da Windows'a çivilerdik ve
/// testlerde örneklenemezdi.</para>
/// </summary>
public partial class BarcodeLabelDrawer : UserControl
{
    private readonly Drawer _drawer;
    private readonly BarcodeLabelViewModel _vm;

    private BarcodeLabelDrawer(Drawer drawer, BarcodeLabelViewModel vm)
    {
        InitializeComponent();
        _drawer = drawer;
        _vm = vm;
        DataContext = vm;
    }

    public static BarcodeLabelDrawer Create(Drawer drawer, BarcodeLabelViewModel vm)
        => new(drawer, vm);

    private void Print_OnClick(object sender, RoutedEventArgs e)
    {
        var printer = App.Host.Services.GetRequiredService<BarcodeLabelPrinter>();
        printer.Print(_vm.BuildLabels(), _vm.Copies);
        _drawer.Close(true);
    }

    private void Cancel_OnClick(object sender, RoutedEventArgs e) => _drawer.Close(false);
}
```

`OrderDeck.App/Views/Shell/ProductCard.xaml` — varyant çiplerinin altına, `HasProduct` görünürlük bloğunun içine:

```xml
<Button Content="Etiket bas"
        Style="{StaticResource OD.Button.Ghost}"
        Margin="{StaticResource OD.Pad.Top4}"
        Command="{Binding DataContext.PrintLabelsCommand,
                          RelativeSource={RelativeSource AncestorType=UserControl}}"/>
```

Bağın `RelativeSource` ile kurulması şart: `ProductCard`'ın `DataContext`'i `ProductCardViewModel`, komut ise `MainShellViewModel`'de (çekmeceyi açan servis orada).

`OrderDeck.App/ViewModels/MainShellViewModel.cs` — diğer çekmece komutlarının yanına:

```csharp
    /// <summary>
    /// Kartta açık ürünün etiketlerini bastırır. Çekmece
    /// <c>IDrawerService.ShowAsync(title, factory)</c> ile açılıyor —
    /// bütün diğer çekmecelerle aynı yol.
    ///
    /// <para>Ürün yoksa sessizce çıkar: düğme zaten yalnız çözülmüş kodda
    /// görünüyor, ama kod kutusu komut tetiklendikten sonra da temizlenebilir.</para>
    /// </summary>
    [RelayCommand]
    private async Task PrintLabelsAsync()
    {
        if (_drawers is null) return;
        if (ProductCard.Resolution is null) return;

        var vm = new BarcodeLabelViewModel();
        vm.Load(ProductCard.Resolution);

        await _drawers.ShowAsync("Etiket Bas",
            d => Views.Drawers.BarcodeLabelDrawer.Create(d, vm));
    }
```

- [ ] **Adım 6: Derle**

```bash
dotnet build OrderDeck.App/OrderDeck.App.csproj
```

Beklenen: 0 hata, 0 yeni uyarı.

- [ ] **Adım 7: Commit**

```bash
git add OrderDeck.App/ViewModels/BarcodeLabelViewModel.cs OrderDeck.App/Views/Drawers/BarcodeLabelDrawer.xaml OrderDeck.App/Views/Drawers/BarcodeLabelDrawer.xaml.cs OrderDeck.App/Views/Shell/ProductCard.xaml OrderDeck.App/ViewModels/MainShellViewModel.cs OrderDeck.Tests/ViewModels/BarcodeLabelViewModelTests.cs
git commit -m "$(cat <<'EOF'
feat(barkod): ürün kartından etiket basma çekmecesi

Co-Authored-By: Claude Opus 4.6 <noreply@anthropic.com>
EOF
)"
```

---

## Görev 16: Son doğrulama

- [ ] **Adım 1: Sunucu tarafı**

```bash
dotnet test OrderDeck.LicenseServer.Tests/OrderDeck.LicenseServer.Tests.csproj
```

Beklenen: tümü passed.

- [ ] **Adım 2: WPF/Chat tarafı**

```bash
dotnet test OrderDeck.Tests/OrderDeck.Tests.csproj
dotnet build OrderDeck.App/OrderDeck.App.csproj
```

Beklenen: tümü passed, 0 hata, 0 yeni uyarı.

- [ ] **Adım 3: Panel**

```bash
cd /c/Users/burak/source/repos/OrderDeck-Mobile/apps/panel && npx vitest run
```

Beklenen: tümü passed.

- [ ] **Adım 4: Ölü kavram taraması**

```bash
grep -rn "basım anında" --include=*.cs .
```

Beklenen: hiç eşleşme (Görev 3'te iki XML doc düzeltildi). Kalan varsa düzelt.

- [ ] **Adım 5: Göç SQL'inin ELLE doğrulanması**

Göç SQL'i test kapsamında değil (EF InMemory `EnsureCreated()` kullanıyor). Prod'a çıkmadan önce yerel bir SQL Server kopyasında:

```bash
dotnet ef database update --project OrderDeck.LicenseServer --connection "<yerel SQL Server bağlantısı>"
```

Sonra doğrula:
- Her `ProductVariants` satırının `Barcode`'u dolu ve 10 hane.
- Aynı `LicenseId` içinde tekrar eden barkod yok.
- `BarcodeCounters` her lisans için bir satır içeriyor ve `Next` = o lisansın varyant sayısı + 1.

- [ ] **Adım 6: Elle uçtan uca (kullanıcı)**

1. Panelde bir ürüne varyant ekle → barkod alanı kendiliğinden dolmalı.
2. "Oluştur"a bas → numara değişmeli, kaydedilmeli.
3. WPF'i aç, katalog senkronunu bekle, ürün kartını aç → "Etiket bas" → etiket çıkmalı.
4. Çıkan etiketi bir barkod okuyucusuyla WPF'in kod kutusuna okut → kart o ürünün doğru satıcı-eksen kırılımıyla açılmalı.
5. Ağı kes, tekrar okut → hâlâ çalışmalı (replika yerel).

---

## Kapsam dışı

- Panelde "Boşları doldur" toplu butonu (gerekçe: en üstteki sapma notu).
- Excel toplu içe aktarım (ayrı spec + plan).
- Barkod okutmayla **stok girişi** (Faz 1c yalnız yayın tarafını çözüyor).
- EAN-13 / QR gibi başka simgelemeler.
- Etiket şablonu özelleştirme (boyut ayarı mevcut `AppSettings`'ten geliyor).

