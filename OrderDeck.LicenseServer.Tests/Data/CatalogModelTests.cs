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

    /// <summary>
    /// Guard rail: kolon sınırı ile doğrulama sınırı AYNI sayı olmalı.
    ///
    /// Testler EF InMemory üstünde koşuyor ve InMemory <c>HasMaxLength</c>'i yok
    /// sayıyor — yani ikisi ayrışsa hiçbir davranış testi bunu göstermez, fark
    /// yalnız prod'da (SQL Server) 500 olarak ortaya çıkar. Bu test farkı model
    /// metadata'sından okuyup burada yakalar.
    /// </summary>
    [Fact]
    public void Column_lengths_come_from_CatalogLimits()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var expected = new (Type Entity, string Property, int Limit)[]
        {
            (typeof(Category), nameof(Category.Name), CatalogLimits.CategoryName),
            (typeof(Category), nameof(Category.Path), CatalogLimits.CategoryPath),
            (typeof(Product), nameof(Product.Code), CatalogLimits.ProductCode),
            (typeof(Product), nameof(Product.Name), CatalogLimits.ProductName),
            (typeof(Product), nameof(Product.NameSearch), CatalogLimits.NameSearch),
            (typeof(Product), nameof(Product.Axis1Name), CatalogLimits.AxisName),
            (typeof(Product), nameof(Product.Axis2Name), CatalogLimits.AxisName),
            (typeof(Product), nameof(Product.ShelfLocation), CatalogLimits.ShelfLocation),
            (typeof(ProductPhoto), nameof(ProductPhoto.ObjectKey), CatalogLimits.PhotoObjectKey),
            (typeof(ProductPhoto), nameof(ProductPhoto.ContentType), CatalogLimits.PhotoContentType),
            (typeof(ProductVariant), nameof(ProductVariant.Axis1Value), CatalogLimits.AxisValue),
            (typeof(ProductVariant), nameof(ProductVariant.Axis2Value), CatalogLimits.AxisValue),
            (typeof(ProductVariant), nameof(ProductVariant.Axis1Code), CatalogLimits.AxisCode),
            (typeof(ProductVariant), nameof(ProductVariant.Axis2Code), CatalogLimits.AxisCode),
            (typeof(ProductVariant), nameof(ProductVariant.VariantCode), CatalogLimits.VariantCode),
            (typeof(ProductVariant), nameof(ProductVariant.Barcode), CatalogLimits.Barcode),
            (typeof(ProductVariant), nameof(ProductVariant.Axis1ValueNorm), CatalogLimits.AxisValue),
            (typeof(ProductVariant), nameof(ProductVariant.Axis2ValueNorm), CatalogLimits.AxisValue),
            (typeof(ProductBroadcastCode), nameof(ProductBroadcastCode.SellerAxisValue), CatalogLimits.AxisValue),
            (typeof(ProductBroadcastCode), nameof(ProductBroadcastCode.Code), CatalogLimits.BroadcastCode),
            (typeof(ProductBroadcastCode), nameof(ProductBroadcastCode.CodeNormalized), CatalogLimits.BroadcastCode),
        };

        foreach (var (entity, property, limit) in expected)
        {
            var actual = db.Model.FindEntityType(entity)!.FindProperty(property)!.GetMaxLength();

            actual.Should().Be(limit,
                "{0}.{1} kolonunun sınırı CatalogLimits ile aynı olmalı — ayrışırsa "
                + "doğrulamadan geçen girdi prod'da kesme hatası verir",
                entity.Name, property);
        }
    }

    /// <summary>
    /// Derinlik tavanı keyfi bir sayı değil: <c>Path</c> seviye başına 33
    /// karakter büyüyor. Tavan yükseltilirse bu hesap kolon sınırını aşmamalı.
    /// </summary>
    [Fact]
    public void Max_category_depth_fits_in_the_path_column()
    {
        var longestPath = 1 + (33 * CatalogLimits.CategoryMaxDepth);

        longestPath.Should().BeLessThanOrEqualTo(CatalogLimits.CategoryPath);
    }

    /// <summary>
    /// Bekçi: <c>NameSearch</c> türetilmiş bir kolon ve türetme controller'da
    /// DEĞİL, <c>SaveChanges</c> zincirinde yapılıyor. Bu test controller'a hiç
    /// uğramadan doğrudan DbContext üstünden yazıyor — yani kuralı atlayan bir
    /// yazma yolu (içe aktarma, senkron işi, toplu düzenleme) eklendiğinde bile
    /// kolonun doğru dolduğunu gösteriyor.
    /// </summary>
    [Fact]
    public async Task NameSearch_is_derived_on_insert_and_refreshed_on_update()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = NewLicense();
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "NS1",
            Name = "  Kırmızı  Tişört ",
            DefaultPrice = 100m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();

        product.NameSearch.Should().Be("KIRMIZI TISORT");

        product.Name = "Mavi Gömlek";
        await db.SaveChangesAsync();

        product.NameSearch.Should().Be("MAVI GOMLEK",
            "girdi değişince türetilmiş kolon bayat kalırsa arama sessizce "
            + "yanlış sonuç döner");
    }

    [Fact]
    public async Task Product_photos_round_trip_and_cover_is_the_lowest_sort_order()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();

        var license = NewLicense();
        db.Licenses.Add(license);

        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, Code = "PH1",
            Name = "Fotoğraflı", DefaultPrice = 10m,
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        db.Products.Add(product);

        foreach (var sort in new[] { 2, 0, 1 })
            db.ProductPhotos.Add(new ProductPhoto
            {
                Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
                ObjectKey = $"{license.Id:N}/products/{product.Id:N}/{sort}.img",
                ContentType = "image/jpeg", SizeBytes = 1024, SortOrder = sort,
                CreatedAt = DateTimeOffset.UtcNow,
            });

        await db.SaveChangesAsync();

        var loaded = await db.Products
            .Include(p => p.Photos)
            .FirstAsync(p => p.Id == product.Id);

        loaded.Photos.Should().HaveCount(3);
        loaded.Photos.OrderBy(p => p.SortOrder).First().ObjectKey
            .Should().EndWith("0.img", "kapak = en küçük SortOrder");
    }

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
            VariantCode = "SK00042-KIRM",
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
