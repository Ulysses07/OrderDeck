using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class StockBalanceRepositoryTests
{
    private static StockBalanceRepository Build(out InMemorySqlite db)
    {
        db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new StockBalanceRepository(db);
    }

    [Fact]
    public void GetCursor_returns_seeded_beginning_of_time()
    {
        var repo = Build(out var db);
        using (db)
        {
            var cursor = repo.GetCursor();

            cursor.CreatedAt.Should().Be(DateTimeOffset.MinValue);
            cursor.Id.Should().Be(Guid.Empty);
        }
    }

    [Fact]
    public void ApplyPage_writes_balances_and_advances_cursor()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");
            var vid = Guid.NewGuid().ToString("N");
            var at = new DateTimeOffset(2026, 8, 15, 10, 0, 0, TimeSpan.Zero);
            var cid = Guid.NewGuid();

            repo.ApplyPage(new[]
            {
                new CatalogStockBalance(pid, vid, 7),
                new CatalogStockBalance(pid, null, 3),
            }, new StockCursor(at, cid));

            var rows = repo.GetForProduct(pid);
            rows.Should().HaveCount(2);
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, vid, 7));
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, null, 3));

            var cursor = repo.GetCursor();
            cursor.CreatedAt.Should().Be(at);
            cursor.Id.Should().Be(cid);
        }
    }

    [Fact]
    public void ApplyPage_replaces_instead_of_summing()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");
            var vid = Guid.NewGuid().ToString("N");
            var c1 = new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid());
            var c2 = new StockCursor(DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid());

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, vid, 7) }, c1);
            repo.ApplyPage(new[] { new CatalogStockBalance(pid, vid, 4) }, c2);

            // 11 DEĞİL 4: sunucu mutlak bakiye gönderiyor, istemci toplamıyor.
            repo.GetForProduct(pid).Should()
                .ContainSingle().Which.Quantity.Should().Be(4);
        }
    }

    [Fact]
    public void ApplyPage_deduplicates_product_level_rows_despite_null_variant()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, null, 5) },
                new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));
            repo.ApplyPage(new[] { new CatalogStockBalance(pid, null, 2) },
                new StockCursor(DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid()));

            // SQLite'ta UNIQUE iki NULL'u eşit saymaz; tekillik "IS" ile
            // yapılan elle silmeden geliyor. Bu test o silmenin bekçisi.
            repo.GetForProduct(pid).Should()
                .ContainSingle().Which.Quantity.Should().Be(2);
        }
    }

    /// <summary>
    /// Silme yalnız sayfadaki ANAHTARI vurmalı. Ölçüldü: DELETE'ten varyant
    /// koşulu çıkarıldığında paketin geri kalanı yeşil kalıyor, çünkü her test
    /// ya ürün başına tek satır yazıyor ya da bütün satırları aynı sayfada
    /// yazıyor — kayıp hemen geri ekleniyor. Üretimde ise tek varyant taşıyan
    /// bir sayfa o ürünün diğer bakiyelerini siler ve sonraki bir sayfa
    /// tesadüfen kapsayana dek silinmiş kalırlar.
    /// </summary>
    [Fact]
    public void ApplyPage_leaves_untouched_keys_of_the_same_product_alone()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");
            var a = Guid.NewGuid().ToString("N");
            var b = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[]
            {
                new CatalogStockBalance(pid, a, 7),
                new CatalogStockBalance(pid, b, 5),
                new CatalogStockBalance(pid, null, 3),
            }, new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, a, 6) },
                new StockCursor(DateTimeOffset.UnixEpoch.AddMinutes(1), Guid.NewGuid()));

            var rows = repo.GetForProduct(pid);
            rows.Should().HaveCount(3);
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, a, 6));
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, b, 5));
            rows.Should().ContainEquivalentOf(new CatalogStockBalance(pid, null, 3));
        }
    }

    [Fact]
    public void ApplyPage_with_empty_page_still_advances_cursor()
    {
        var repo = Build(out var db);
        using (db)
        {
            var at = new DateTimeOffset(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);
            var cid = Guid.NewGuid();

            repo.ApplyPage(Array.Empty<CatalogStockBalance>(), new StockCursor(at, cid));

            repo.GetCursor().Should().Be(new StockCursor(at, cid));
        }
    }

    [Fact]
    public void GetForProduct_returns_empty_for_unknown_product()
    {
        var repo = Build(out var db);
        using (db)
        {
            repo.GetForProduct(Guid.NewGuid().ToString("N")).Should().BeEmpty();
        }
    }

    [Fact]
    public void GetForProduct_ignores_other_products()
    {
        var repo = Build(out var db);
        using (db)
        {
            var a = Guid.NewGuid().ToString("N");
            var b = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[]
            {
                new CatalogStockBalance(a, null, 1),
                new CatalogStockBalance(b, null, 2),
            }, new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

            repo.GetForProduct(a).Should().ContainSingle().Which.Quantity.Should().Be(1);
        }
    }

    [Fact]
    public void Negative_quantity_round_trips()
    {
        var repo = Build(out var db);
        using (db)
        {
            var pid = Guid.NewGuid().ToString("N");

            repo.ApplyPage(new[] { new CatalogStockBalance(pid, null, -3) },
                new StockCursor(DateTimeOffset.UnixEpoch, Guid.NewGuid()));

            repo.GetForProduct(pid).Should().ContainSingle().Which.Quantity.Should().Be(-3);
        }
    }
}
