using FluentAssertions;
using Microsoft.EntityFrameworkCore;
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
