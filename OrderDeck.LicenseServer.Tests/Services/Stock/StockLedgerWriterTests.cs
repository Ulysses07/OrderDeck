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
        bool cancelled = false, bool shippingFee = false, bool tentative = false,
        DateTimeOffset? addedAt = null, DateTimeOffset? cancelledAt = null) => new
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
        addedAt = addedAt ?? DateTimeOffset.UtcNow,
        printedAt = (DateTimeOffset?)null,
        cancelledAt = cancelled
            ? (cancelledAt ?? DateTimeOffset.UtcNow)
            : (DateTimeOffset?)null,
        cancelReason = cancelled ? "vazgeçti" : null,
        isShippingFee = shippingFee,
        isBackupPromoted = false,
        isTentativeBackup = tentative,
        productId,
        productVariantId = variantId
    };

    // Bu dosyadaki testlerin tamamı KATALOG BİLEN istemciyi taklit ediyor
    // (paketlerinde ürün/varyant kimliği var), o yüzden bayrak açık gidiyor;
    // sunucu bayrak yoksa o paket için mutabakatı hiç çalıştırmıyor.
    private Task<HttpResponseMessage> SyncAsync(Seed s, params object[] orders)
        => s.Client.PostAsJsonAsync(
            $"/api/v1/licenses/{s.LicenseId}/orders/sync",
            new { orders, catalogAware = true });

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

    /// <summary>
    /// Aynı sipariş id'si tek pakette iki kez geçerse telafi iki kez yazılmamalı.
    /// Yazıcı DOĞRUDAN sürülüyor: uç dışa açık bir HTTP API ve bu invaryantın
    /// sahibi controller değil yazıcının kendisi.
    /// </summary>
    [Fact]
    public async Task Duplicate_order_id_in_one_batch_does_not_double_compensate()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();

        // Defterde zaten satış duruyor (−1).
        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId))).EnsureSuccessStatusCode();

        var now = DateTimeOffset.UtcNow;
        using (var scope = _factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<LicenseDbContext>();
            var writer = scope.ServiceProvider
                .GetRequiredService<OrderDeck.LicenseServer.Services.Stock.StockLedgerWriter>();

            // Aynı sipariş, aynı pakette iki kez, ikisi de iptal.
            var input = new OrderDeck.LicenseServer.Services.Stock.LedgerOrderInput(
                new OrderDeck.LicenseServer.Services.Stock.LedgerOrderState(
                    orderId, s.ProductId, s.VariantId,
                    IsShippingFee: false, IsCancelled: true, IsTentativeBackup: false),
                SoldAt: now.AddHours(-2),
                CancelledAt: now);

            await writer.ApplyAsync(s.LicenseId, new[] { input, input }, now, default);
            await db.SaveChangesAsync();
        }

        var movements = await MovementsAsync(s.LicenseId);
        movements.Sum(m => m.Quantity).Should().Be(0);
        movements.Should().HaveCount(2);
    }

    /// <summary>
    /// Aynı yeni siparişin tek pakette iki kez gelmesi senkron ucunu da
    /// düşürmemeli (iki kez <c>Add</c> → EF izleme istisnası → 500).
    /// </summary>
    [Fact]
    public async Task Duplicate_order_id_in_one_request_is_accepted_once()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();

        var resp = await SyncAsync(
            s,
            Payload(orderId, s.ProductId, s.VariantId),
            Payload(orderId, s.ProductId, s.VariantId));

        resp.EnsureSuccessStatusCode();
        (await MovementsAsync(s.LicenseId)).Should().ContainSingle(m => m.Quantity == -1);
    }

    /// <summary>
    /// İptal telafisinin iş zamanı iptal anıdır, satış anı değil — yoksa
    /// geçmişe dönük her stok raporu yanlış çıkar.
    /// </summary>
    [Fact]
    public async Task Cancellation_movement_occurs_at_cancel_time_not_at_sale_time()
    {
        var s = await SeedAsync();
        var orderId = Guid.NewGuid();
        var soldAt = DateTimeOffset.UtcNow.AddDays(-4);
        var cancelledAt = DateTimeOffset.UtcNow.AddHours(-3);

        (await SyncAsync(s, Payload(orderId, s.ProductId, s.VariantId, addedAt: soldAt)))
            .EnsureSuccessStatusCode();
        (await SyncAsync(s, Payload(
                orderId, s.ProductId, s.VariantId,
                cancelled: true, addedAt: soldAt, cancelledAt: cancelledAt)))
            .EnsureSuccessStatusCode();

        var movements = await MovementsAsync(s.LicenseId);
        movements.Should().HaveCount(2);

        var sale = movements.Single(m => m.Quantity == -1);
        sale.OccurredAt.Should().BeCloseTo(soldAt, TimeSpan.FromSeconds(2));

        var compensation = movements.Single(m => m.Quantity == 1);
        compensation.OccurredAt.Should().BeCloseTo(cancelledAt, TimeSpan.FromSeconds(2));
        compensation.OccurredAt.Should().NotBeCloseTo(soldAt, TimeSpan.FromMinutes(1));
    }
}
