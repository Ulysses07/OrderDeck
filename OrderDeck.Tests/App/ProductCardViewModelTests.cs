using System.IO;
using FluentAssertions;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Shared.Text;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.App;

/// <summary>
/// Ürün kartı artık SALT OKUR: kaynağı sunucu kataloğunun yerel replikası.
/// Tanımlama/düzenleme/fotoğraf seçme akışları kaldırıldı (katalogun sahibi
/// panel), o yüzden buradaki testler yalnız Load'un dört durumunu sınıyor.
/// </summary>
public class ProductCardViewModelTests
{
    private static (ProductCardViewModel Vm, CatalogReplicaRepository Repo, string Root) Make()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new CatalogReplicaRepository(db);
        var root = Path.Combine(Path.GetTempPath(), "od-test-" + Guid.NewGuid().ToString("N"));
        return (new ProductCardViewModel(repo, new CatalogPhotoCache(root)), repo, root);
    }

    private static CatalogProduct Product(
        string id, string code, string name = "Elbise", string? coverKey = null)
        => new(id, null, code, SearchNormalizer.Normalize(code), name,
               199.90m, null, "Renk", 1, "Beden", 2, coverKey, 1_700_000_000);

    [Fact]
    public void Empty_code_shows_neither_product_nor_unknown()
    {
        var (vm, _, _) = Make();

        vm.Load("   ");

        vm.Code.Should().BeEmpty();
        vm.HasProduct.Should().BeFalse();
        vm.IsUnknown.Should().BeFalse();
        vm.Variants.Should().BeEmpty();
    }

    [Fact]
    public void Unknown_code_is_reported_without_clearing_the_typed_code()
    {
        var (vm, _, _) = Make();

        vm.Load("A7");

        // Kod ekranda kalmalı: operatör neyi yazdığını görsün.
        vm.Code.Should().Be("A7");
        vm.HasProduct.Should().BeFalse();
        vm.IsUnknown.Should().BeTrue();
    }

    [Fact]
    public void Known_code_loads_name_and_active_variants()
    {
        var (vm, repo, _) = Make();
        repo.Replace(
            [Product("p1", "GUZEL ELBISE", "Güzel Elbise")],
            [
                new CatalogVariant("v1", "p1", "Kırmızı", "KIRM", "M", "M",
                                   "GUZEL ELBISE-KIRM-M", null, true, 0),
                new CatalogVariant("v2", "p1", "Kırmızı", "KIRM", "L", "L",
                                   "GUZEL ELBISE-KIRM-L", null, false, 1),
            ],
            []);

        vm.Load("güzel elbise");

        vm.HasProduct.Should().BeTrue();
        vm.IsUnknown.Should().BeFalse();
        vm.Name.Should().Be("Güzel Elbise");
        // Pasif varyant gösterilmez: operatör satamayacağı bir kırılımı görmesin.
        vm.Variants.Should().ContainSingle().Which.Display.Should().Be("Kırmızı · M");
    }

    [Fact]
    public void Photo_path_is_null_until_the_cover_file_is_cached()
    {
        var (vm, repo, root) = Make();
        var photos = new CatalogPhotoCache(root);
        repo.Replace([Product("p1", "A1", coverKey: "lic/p1/kapak.img")], [], []);

        vm.Load("A1");
        vm.PhotoAbsolutePath.Should().BeNull();

        // Senkron fotoğrafı indirdikten sonra aynı kod yeniden okunduğunda yol dolar.
        photos.Save("lic/p1/kapak.img", [1, 2, 3]);
        vm.Load("A1");
        vm.PhotoAbsolutePath.Should().NotBeNull();
    }

    [Fact]
    public void Variant_without_axis_values_falls_back_to_its_code()
    {
        var vm = new CatalogVariantViewModel(
            new CatalogVariant("v1", "p1", null, null, null, null, "A1", null, true, 0));

        vm.Display.Should().Be("A1");
    }
}
