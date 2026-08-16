using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Customers;
using OrderDeck.Core.Sales;
using OrderDeck.Core.Sessions;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Catalog;

public class StockBalanceProviderTests
{
    private static (InMemorySqlite Db, StockBalanceRepository Stock,
                    LabelRepository Labels, StockBalanceProvider Provider) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        new SessionRepository(db).Insert(
            new StreamSession("s1", null, 100, null, new[] { "instagram" }, null));
        new CustomerRepository(db).Insert(
            new Customer("c1", "instagram", "@a", null, null, 100, 100,
                false, null, null, 0, 0m, BlacklistedAt: null, Address: null, Phone: null));

        var stock = new StockBalanceRepository(db);
        var labels = new LabelRepository(db);
        return (db, stock, labels, new StockBalanceProvider(stock, labels));
    }

    private static Label Row(string id, string productId, string? variantId) =>
        new(id, "s1", "c1", "instagram", "@a", "mesaj", "A12", 100m, 150, 200,
            ProductId: productId, ProductVariantId: variantId);

    [Fact]
    public void Subtracts_pending_labels_from_server_balance()
    {
        var (db, stock, labels, provider) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        var vid = Guid.NewGuid().ToString("N");
        stock.ApplyPage(new[] { new CatalogStockBalance(pid, vid, 10) },
            new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        labels.Insert(Row("l1", pid, vid));
        labels.Insert(Row("l2", pid, vid));

        provider.ForProduct(pid).For(vid).Should().Be(8);
    }

    [Fact]
    public void Product_level_and_variant_balances_are_independent()
    {
        var (db, stock, labels, provider) = Fx();
        using var _ = db;

        var pid = Guid.NewGuid().ToString("N");
        var vid = Guid.NewGuid().ToString("N");
        stock.ApplyPage(new[]
        {
            new CatalogStockBalance(pid, vid, 5),
            new CatalogStockBalance(pid, null, 4),
        }, new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));
        labels.Insert(Row("l1", pid, null));

        var snapshot = provider.ForProduct(pid);
        snapshot.For(vid).Should().Be(5);
        snapshot.ProductLevel.Should().Be(3);
    }

    [Fact]
    public void Unknown_variant_reads_as_zero()
    {
        var (db, _s, _l, provider) = Fx();
        using var _ = db;

        provider.ForProduct(Guid.NewGuid().ToString("N"))
            .For(Guid.NewGuid().ToString("N")).Should().Be(0);
    }

    [Fact]
    public void Pending_without_server_row_goes_negative()
    {
        var (db, _s, labels, provider) = Fx();
        using var _ = db;

        // Sunucu bu anahtarı hiç bilmiyor (panelde stok girilmemiş) ama satış
        // yapıldı. Engellemiyoruz: eksiye düşer ve arayüzde vurgulanır.
        var pid = Guid.NewGuid().ToString("N");
        labels.Insert(Row("l1", pid, null));

        provider.ForProduct(pid).ProductLevel.Should().Be(-1);
    }

    [Fact]
    public void RaiseBalancesChanged_notifies_subscribers()
    {
        var (db, _s, _l, provider) = Fx();
        using var _ = db;

        var fired = 0;
        provider.BalancesChanged += (_, __) => fired++;

        provider.RaiseBalancesChanged();

        fired.Should().Be(1);
    }
}
