using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OrderDeck.LicenseServer.Data;
using OrderDeck.LicenseServer.Domain;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Controllers.Customers;

/// <summary>
/// KVKK silme (<c>POST /api/v1/admin/customers/{id}/purge</c>) gerçek SQL
/// Server'da.
///
/// Bu testler neden InMemory'de olamaz: purge, lisansı silip gerisini
/// <b>veritabanı cascade'ine</b> bırakıyor. InMemory'de yabancı anahtar diye
/// bir şey yok — ne cascade çalışır ne de <c>Restrict</c> kısıtı patlar. Yani
/// purge'ün üretimde 500 atıp atmadığı orada <b>hiçbir zaman</b> görünmez.
///
/// Somut risk: <c>StockMovement</c> hem <c>License</c>'a (Cascade) hem
/// <c>Product</c>/<c>ProductVariant</c>'a (Restrict) bağlı. Stok hareketi olan
/// bir müşteri silinirken bu ikisi çarpışırsa purge FK ihlaliyle düşer ve
/// yasal silme talebi karşılanamaz.
/// </summary>
[Collection(SqlServerCollection.Name)]
public sealed class CustomerPurgeRelationalTests : IAsyncLifetime
{
    private const string Password = "purge-password-123";

    private readonly SqlServerContainerFixture _sql;
    private RelationalApiFactory _factory = null!;

    public CustomerPurgeRelationalTests(SqlServerContainerFixture sql) => _sql = sql;

    public async Task InitializeAsync()
        => _factory = new RelationalApiFactory(await _sql.CreateDatabaseAsync());

    public Task DisposeAsync()
    {
        _factory.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task Stok_hareketi_olan_musteri_silinebilir()
    {
        var (client, customerId, email) = await SeedCustomerAsync();
        await SeedCatalogAndStockAsync(customerId);

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/admin/customers/{customerId}/purge", new { confirmEmail = email });

        resp.StatusCode.Should().Be(HttpStatusCode.NoContent,
            "stok defteri olan bir yayıncı da KVKK talebinde silinebilmeli; " +
            "cascade ile Restrict çarpışırsa bu istek FK ihlaliyle 500 döner");

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        (await db.Licenses.AnyAsync(l => l.CustomerId == customerId)).Should().BeFalse();
        // Asıl mesele burası: hareketler PII taşıyan katalogla birlikte gitmeli,
        // yetim kalmamalı. Kalırlarsa silme eksik ve bakiye sorgusu onları toplar.
        (await db.StockMovements.AnyAsync()).Should().BeFalse();
        (await db.Products.AnyAsync()).Should().BeFalse();
        (await db.ProductVariants.AnyAsync()).Should().BeFalse();
    }

    /// <summary>Admin oluşturur, giriş yapar, silinecek müşteriyi açar.</summary>
    private async Task<(HttpClient Client, Guid CustomerId, string Email)> SeedCustomerAsync()
    {
        var (token, _) = await _factory.SeedAdminAndLoginAsync(
            username: $"a-{Guid.NewGuid():N}", password: "admin-password");
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

        var email = $"purge-{Guid.NewGuid():N}@example.com";
        var created = await client.PostAsJsonAsync("/api/v1/admin/customers", new
        {
            email, name = "Silinecek", initialPassword = Password, autoConfirm = true
        });
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var body = (await created.Content.ReadFromJsonAsync<IdBody>())!;

        return (client, body.id, email);
    }

    /// <summary>Lisans + ürün + varyant + her iki hedefe hareket. Varyantlı satır
    /// ayrıca yazılıyor: Restrict FK'lardan biri ürüne, diğeri varyanta bakıyor,
    /// yalnız ürün seviyesinde hareket yazsak ikincisi hiç sınanmazdı.</summary>
    private async Task SeedCatalogAndStockAsync(Guid customerId)
    {
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var now = DateTimeOffset.UtcNow;

        var license = new License
        {
            Id = Guid.NewGuid(),
            CustomerId = customerId,
            // Kolon dar; InMemory kırpma hatası vermediği için burada kısaltmak şart.
            LicenseKey = "LDK-P-" + Guid.NewGuid().ToString("N")[..12],
            SkuCode = "STD",
            ActivationSlots = 1,
            IssuedAt = now,
            ExpiresAt = now.AddDays(30),
        };
        var product = new Product
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            Code = "A12",
            Name = "Ürün",
            NameSearch = "urun",
            DefaultPrice = 100m,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var variant = new ProductVariant
        {
            Id = Guid.NewGuid(),
            LicenseId = license.Id,
            ProductId = product.Id,
            Axis1Value = "M",
            Axis1ValueNorm = "M",
            Axis2ValueNorm = "",
            Barcode = "BRC-" + Guid.NewGuid().ToString("N")[..12],
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Licenses.Add(license);
        db.Products.Add(product);
        db.ProductVariants.Add(variant);
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            Quantity = 10, Reason = StockMovementReason.Entry,
            OccurredAt = now, CreatedAt = now,
        });
        db.StockMovements.Add(new StockMovement
        {
            Id = Guid.NewGuid(), LicenseId = license.Id, ProductId = product.Id,
            ProductVariantId = variant.Id,
            Quantity = -1, Reason = StockMovementReason.Sale,
            OccurredAt = now, CreatedAt = now,
        });
        await db.SaveChangesAsync();
    }

    private sealed record IdBody(Guid id);
}
