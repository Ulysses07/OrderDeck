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

    /// <summary>
    /// K1 (spec 2026-10-13 §4.2): ProductId ve ProductVariantId oluşum alanıdır, güncellemede yazılmaz; yeniden
    /// gönderim ürünü ya da varyantı değiştiremez ve defter yerinde kalır. Masaüstü bu iki alanı eklemeden sonra
    /// hiç güncellemiyor: OrderDeck.Core'daki bütün <c>UPDATE Label</c> cümleleri (yedek onayı, iptal, iptali geri
    /// alma, baskı, fiyat, eşitleme onayı, kargo bağı; <c>LabelRepository</c>, <c>ShipmentRepository</c>) ürün
    /// kimliğine dokunmuyor. Yani "yeniden gönderimle varyant bağlama" meşru bir yol değildi; açık kaldıkça yalnız
    /// bayat ya da katalog bilmeyen gönderimin bağı değiştirip defteri geri sarmasına yarıyordu.
    /// </summary>
    [Fact]
    public async Task Repushing_the_same_order_does_not_rebind_the_variant()
    {
        var (client, licenseId, productId, variantId) = await SeedAsync();
        var orderId = Guid.NewGuid();

        (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, productId, null) }, catalogAware = true }))
            .EnsureSuccessStatusCode();

        var resp = await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, productId, variantId) }, catalogAware = true });

        resp.EnsureSuccessStatusCode();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
        var saved = await db.Orders.SingleAsync(o => o.Id == orderId);
        saved.ProductId.Should().Be(productId);
        saved.ProductVariantId.Should().BeNull("varyant eklemede bağlanmadıysa sonradan bağlanmaz");
        var movements = await db.StockMovements.Where(m => m.OrderId == orderId).ToListAsync();
        movements.Should().ContainSingle("defter ilk gönderimin ürün düzeyi düşümünde kalır")
            .Which.ProductVariantId.Should().BeNull();
        movements.Sum(m => m.Quantity).Should().Be(-1);
    }

    /// <summary>
    /// ProductId = null İKİ ayrı şey demek olabilir: "operatör ürünü
    /// belirleyemedi" (meşru) ve "bu istemci katalog diye bir şey bilmiyor".
    /// İkisi ayırt edilmezse, güncellenmemiş bir WPF aynı siparişi tekrar
    /// gönderdiğinde mutabakat "artık stok etkisi yok" diye okur ve daha önce
    /// yazılmış satış hareketini +1 ile GERİ SARAR.
    /// </summary>
    [Fact]
    public async Task Catalog_unaware_client_does_not_unwind_the_stock_movement()
    {
        var (client, licenseId, productId, variantId) = await SeedAsync();
        var orderId = Guid.NewGuid();

        // 1) Güncel istemci: katalog kimlikleriyle gönderiyor → -1 hareket.
        (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new
            {
                orders = new[] { OrderPayload(orderId, productId, variantId) },
                catalogAware = true,
            })).EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            (await db.StockMovements.CountAsync(m => m.OrderId == orderId))
                .Should().Be(1, "güncel istemcinin satışı deftere girmeli");
        }

        // 2) Güncellenmemiş istemci: catalogAware alanını hiç göndermiyor.
        (await client.PostAsJsonAsync(
            $"/api/v1/licenses/{licenseId}/orders/sync",
            new { orders = new[] { OrderPayload(orderId, null, null) } }))
            .EnsureSuccessStatusCode();

        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var rows = await db.StockMovements
                .Where(m => m.OrderId == orderId)
                .ToListAsync();

            rows.Should().HaveCount(1, "eski istemcinin paketi deftere hiç girmemeli");
            rows.Sum(m => m.Quantity).Should().Be(-1);
        }
    }
}
