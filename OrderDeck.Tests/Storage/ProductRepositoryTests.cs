using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

/// <summary>
/// Ürün kartının yerel deposu (spec §9.1). Sunucuya hiç dokunmaz — Postgres
/// göçünden etkilenmemesi bilinçli bir sınır.
/// </summary>
public class ProductRepositoryTests
{
    private static (InMemorySqlite Db, ProductRepository Repo) Fx()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return (db, new ProductRepository(db));
    }

    [Fact]
    public void Get_returns_null_when_code_unknown()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Get("A12").Should().BeNull();
    }

    [Fact]
    public void Save_then_Get_round_trips_product_and_sizes()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Save(new Product("A12", "Krem Triko Kazak", "photos/a12.jpg", 1000),
                  [new ProductSize("A12", "S", 6, 0),
                   new ProductSize("A12", "M", 9, 1)]);

        var p = repo.Get("A12");
        p.Should().NotBeNull();
        p!.Name.Should().Be("Krem Triko Kazak");
        p.PhotoPath.Should().Be("photos/a12.jpg");

        var sizes = repo.GetSizes("A12");
        sizes.Should().HaveCount(2);
        sizes[0].Size.Should().Be("S");
        sizes[0].Quantity.Should().Be(6);
        sizes[1].Size.Should().Be("M");
    }

    [Fact]
    public void Save_replaces_previous_size_set_entirely()
    {
        var (db, repo) = Fx();
        using var _ = db;

        repo.Save(new Product("A12", "Kazak", null, 1000),
                  [new ProductSize("A12", "S", 6, 0),
                   new ProductSize("A12", "M", 9, 1)]);

        // Beden seti daralıyor: M gitmeli, kalıntı bırakmamalı.
        repo.Save(new Product("A12", "Kazak", null, 2000),
                  [new ProductSize("A12", "S", 4, 0)]);

        var sizes = repo.GetSizes("A12");
        sizes.Should().ContainSingle();
        sizes[0].Quantity.Should().Be(4);
        repo.Get("A12")!.UpdatedAt.Should().Be(2000);
    }

    [Fact]
    public void GetSizes_orders_by_sort_order_not_alphabetically()
    {
        var (db, repo) = Fx();
        using var _ = db;

        // Alfabetik sıra L, M, S, XL olurdu — beden sırası bu değil.
        repo.Save(new Product("A12", "Kazak", null, 1000),
                  [new ProductSize("A12", "S", 1, 0),
                   new ProductSize("A12", "M", 2, 1),
                   new ProductSize("A12", "L", 3, 2),
                   new ProductSize("A12", "XL", 4, 3)]);

        repo.GetSizes("A12").Select(s => s.Size)
            .Should().Equal("S", "M", "L", "XL");
    }

    [Fact]
    public void Codes_are_matched_case_insensitively()
    {
        var (db, repo) = Fx();
        using var _ = db;

        // Hero kod girişi büyük harfe zorluyor ama eski kayıtlar karışık
        // olabilir; kod aramasının harf duyarlı olmaması gerekiyor.
        repo.Save(new Product("A12", "Kazak", null, 1000), []);

        repo.Get("a12").Should().NotBeNull();
    }
}
