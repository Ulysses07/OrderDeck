using FluentAssertions;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Storage;

public class CatalogReplicaRepositoryTests
{
    private static CatalogReplicaRepository Make(out IDbConnectionFactory db)
    {
        db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        return new CatalogReplicaRepository(db);
    }

    private static CatalogProduct Product(string id, string code, string name = "Elbise")
        => new(id, null, code, OrderDeck.Shared.Text.SearchNormalizer.Normalize(code),
               name, 199.90m, null, null, null, null, null, null, 1_700_000_000);

    [Fact]
    public void FindByCode_matches_case_and_turkish_letters_insensitively()
    {
        var repo = Make(out _);
        repo.Replace([Product("p1", "GUZEL ELBISE")], [], []);

        repo.FindByCode("güzel elbise")!.Id.Should().Be("p1");
        repo.FindByCode("  Güzel   Elbise ")!.Id.Should().Be("p1");
        repo.FindByCode("ISIK 1").Should().BeNull();
    }

    [Fact]
    public void Replace_wipes_rows_that_the_server_no_longer_reports()
    {
        var repo = Make(out _);
        repo.Replace([Product("p1", "A1"), Product("p2", "A2")], [], []);

        // Sunucu artık yalnız A2'yi bildiriyor: A1 panelden silinmiş demektir.
        repo.Replace([Product("p2", "A2")], [], []);

        repo.FindByCode("A1").Should().BeNull();
        repo.FindByCode("A2").Should().NotBeNull();
    }

    [Fact]
    public void GetVariants_returns_only_that_products_variants_in_sort_order()
    {
        var repo = Make(out _);
        repo.Replace(
            [Product("p1", "A1"), Product("p2", "A2")],
            [
                new CatalogVariant("v2", "p1", "Kırmızı", "KIRM", "M", "M", "A1-KIRM-M", null, true, 1),
                new CatalogVariant("v1", "p1", "Kırmızı", "KIRM", "S", "S", "A1-KIRM-S", null, true, 0),
                new CatalogVariant("v9", "p2", null, null, null, null, "A2", null, true, 0),
            ],
            []);

        var variants = repo.GetVariants("p1");

        variants.Select(v => v.Id).Should().Equal("v1", "v2");
    }

    [Fact]
    public void Replace_round_trips_categories()
    {
        var repo = Make(out _);
        repo.Replace([], [], [new CatalogCategory("c1", null, "Erkek", "/c1/", 0, true)]);

        repo.GetCategories().Should().ContainSingle()
            .Which.Name.Should().Be("Erkek");
    }

    [Fact]
    public void CoverPhotoKeys_lists_every_live_key_once()
    {
        var repo = Make(out _);
        repo.Replace(
            [
                Product("p1", "A1") with { CoverPhotoKey = "lic/products/p1/k.img" },
                Product("p2", "A2"),
            ],
            [], []);

        repo.CoverPhotoKeys().Should().Equal("lic/products/p1/k.img");
    }
}
