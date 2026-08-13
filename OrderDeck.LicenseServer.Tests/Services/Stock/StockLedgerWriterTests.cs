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
