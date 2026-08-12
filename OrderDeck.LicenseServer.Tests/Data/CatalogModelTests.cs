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
