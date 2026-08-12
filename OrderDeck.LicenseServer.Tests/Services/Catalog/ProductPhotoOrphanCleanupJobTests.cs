using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Services.BroadcastPosts;
using OrderDeck.LicenseServer.Services.Catalog;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

/// <summary>
/// Yetim ürün fotoğrafı mutabakatı. İş YIKICI — yanlış kurgulanırsa canlı
/// fotoğrafları siler; bu yüzden testler yalnız "siliyor mu"yu değil,
/// <b>neye dokunmadığını</b> da bağlıyor.
/// </summary>
public class ProductPhotoOrphanCleanupJobTests : IClassFixture<ApiFactory>
{
    private readonly ApiFactory _factory;
    public ProductPhotoOrphanCleanupJobTests(ApiFactory f) => _factory = f;

    /// <summary>Ödemsiz süreden (24 saat) rahatça eski bir damga.</summary>
    private static DateTimeOffset Old => DateTimeOffset.UtcNow.AddDays(-3);

    private async Task<Guid> CreateLicenseAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var customer = new Customer
        {
            Id = Guid.NewGuid(), Email = $"orphan-{Guid.NewGuid():N}@t.com",
            Name = "Yetim", PasswordHash = "x", CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Customers.Add(customer);
        var license = new License
        {
            Id = Guid.NewGuid(), CustomerId = customer.Id,
            LicenseKey = "LDK-ORPH-" + Guid.NewGuid().ToString("N"),
            SkuCode = "STD", ActivationSlots = 1,
            IssuedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(30),
        };
        db.Licenses.Add(license);
        await db.SaveChangesAsync();
        return license.Id;
    }

    private async Task<Guid> CreateProductAsync(Guid licenseId, string? photoObjectKey)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;
        var product = new Product
        {
            Id = Guid.NewGuid(), LicenseId = licenseId,
            Code = "A" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(),
            Name = "Yetim testi", DefaultPrice = 10m,
            PhotoObjectKey = photoObjectKey,
            CreatedAt = now, UpdatedAt = now,
        };
        db.Products.Add(product);
        await db.SaveChangesAsync();
        return product.Id;
    }

    private static string PhotoKey(Guid licenseId, Guid productId)
        => $"{licenseId:N}/products/{productId:N}/{Guid.NewGuid():N}.img";

    private async Task RunJobAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<IBroadcastMediaStorage>();
        var job = new ProductPhotoOrphanCleanupJob(
            db, storage, NullLogger<ProductPhotoOrphanCleanupJob>.Instance);
        await job.RunAsync();
    }

    [Fact]
    public async Task RunAsync_deletes_an_aged_key_that_no_product_references()
    {
        var licenseId = await CreateLicenseAsync();
        var productId = await CreateProductAsync(licenseId, photoObjectKey: null);
        var orphan = PhotoKey(licenseId, productId);
        _factory.BroadcastMedia.Seed(orphan, 1024, "image/jpeg", Old);

        await RunJobAsync();

        (await _factory.BroadcastMedia.HeadAsync(orphan)).Should().BeNull(
            "DB'de karşılığı olmayan ve ödemsiz süreden eski nesne silinmeli");
        _factory.BroadcastMedia.DeleteCalls.Should().Contain(orphan);
    }

    [Fact]
    public async Task RunAsync_keeps_a_key_that_a_product_still_references()
    {
        var licenseId = await CreateLicenseAsync();
        var productId = Guid.NewGuid();
        var live = $"{licenseId:N}/products/{productId:N}/{Guid.NewGuid():N}.img";
        _factory.BroadcastMedia.Seed(live, 1024, "image/jpeg", Old);

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var now = DateTimeOffset.UtcNow;
            db.Products.Add(new Product
            {
                Id = productId, LicenseId = licenseId,
                Code = "LIVE" + Guid.NewGuid().ToString("N")[..4].ToUpperInvariant(),
                Name = "Canlı fotoğraf", DefaultPrice = 10m,
                PhotoObjectKey = live,
                CreatedAt = now, UpdatedAt = now,
            });
            await db.SaveChangesAsync();
        }

        await RunJobAsync();

        (await _factory.BroadcastMedia.HeadAsync(live)).Should().NotBeNull(
            "bir ürünün PhotoObjectKey'i olan nesneye asla dokunulmamalı");
        _factory.BroadcastMedia.DeleteCalls.Should().NotContain(live);
    }

    /// <summary>
    /// Presigned yükleme yapılmış ama henüz Attach edilmemiş anahtar DB'de
    /// GÖRÜNMEZ. Ödemsiz süre olmasaydı iş, kullanıcı dialogu kapatmadan
    /// fotoğrafını silerdi.
    /// </summary>
    [Fact]
    public async Task RunAsync_keeps_a_fresh_orphan_inside_the_grace_period()
    {
        var licenseId = await CreateLicenseAsync();
        var productId = await CreateProductAsync(licenseId, photoObjectKey: null);
        var fresh = PhotoKey(licenseId, productId);
        _factory.BroadcastMedia.Seed(fresh, 1024, "image/jpeg",
            DateTimeOffset.UtcNow.AddMinutes(-5));

        await RunJobAsync();

        (await _factory.BroadcastMedia.HeadAsync(fresh)).Should().NotBeNull(
            "yeni yüklenmiş ama henüz iliştirilmemiş nesne korunmalı");
        _factory.BroadcastMedia.DeleteCalls.Should().NotContain(fresh);
    }

    /// <summary>
    /// Kovada yayın gönderisi medyası da var. İş yalnız
    /// <c>{licenseId:N}/products/</c> önekine bakıyor; başka hiçbir şeye
    /// dokunmadığı yapı gereği garanti.
    /// </summary>
    [Fact]
    public async Task RunAsync_ignores_objects_outside_the_products_prefix()
    {
        var licenseId = await CreateLicenseAsync();
        await CreateProductAsync(licenseId, photoObjectKey: null);
        var postMedia = $"{licenseId:N}/posts/{Guid.NewGuid():N}.bin";
        _factory.BroadcastMedia.Seed(postMedia, 2048, "image/jpeg", Old);

        await RunJobAsync();

        (await _factory.BroadcastMedia.HeadAsync(postMedia)).Should().NotBeNull(
            "products/ öneki dışındaki nesne mutabakatın kapsamı dışında");
        _factory.BroadcastMedia.DeleteCalls.Should().NotContain(postMedia);
    }
}
