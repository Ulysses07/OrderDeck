using System.ComponentModel;
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
/// panel), o yüzden buradaki testler yalnız Load'un üç durumunu sınıyor.
///
/// Testlerin çoğu ART ARDA iki Load çağırıyor. Sebep üretimden geliyor:
/// <c>MainShellViewModel</c> aktif kodun HER tuş vuruşunda Load'u yeniden
/// çağırıyor, yani kart pratikte hiçbir zaman taze bir nesne değil — bayat
/// kalan tek bir alan yayında yanlış ürünü gösterir.
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

    /// <summary>İki ürün + her birinin kendi varyantı; geçişleri sınayan testlerin ortak zemini.</summary>
    private static void SeedTwoProducts(CatalogReplicaRepository repo)
        => repo.Replace(
            [
                Product("p1", "A1", "Güzel Elbise", "lic/p1/kapak.img"),
                Product("p2", "B2", "Mavi Etek"),
            ],
            [
                new CatalogVariant("v1", "p1", "Kırmızı", "KIRM", "M", "M",
                                   "A1-KIRM-M", null, true, 0),
                new CatalogVariant("v2", "p2", "Mavi", "MAVI", "L", "L",
                                   "B2-MAVI-L", null, true, 0),
            ],
            []);

    [Fact]
    public void Empty_code_shows_neither_product_nor_unknown()
    {
        var (vm, repo, _) = Make();
        SeedTwoProducts(repo);

        // Önce gerçek bir ürün: boş koleksiyonun boş kalmasını değil,
        // DOLU kartın gerçekten temizlendiğini ölçüyoruz.
        vm.Load("A1");
        vm.Load("   ");

        vm.Code.Should().BeEmpty();
        vm.HasProduct.Should().BeFalse();
        vm.IsUnknown.Should().BeFalse();
        // Bayat kalan her alan yayında bir önceki ürünü göstermeye devam ederdi.
        vm.Name.Should().BeEmpty();
        vm.CoverPhotoKey.Should().BeNull();
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
    public void Unknown_code_after_a_loaded_product_leaves_nothing_behind()
    {
        var (vm, repo, _) = Make();
        SeedTwoProducts(repo);

        vm.Load("A1");
        vm.Load("YOKBOYLEKOD");

        vm.Code.Should().Be("YOKBOYLEKOD");
        vm.HasProduct.Should().BeFalse();
        vm.IsUnknown.Should().BeTrue();
        // "Katalogda yok" yazısının ALTINDA eski ürünün adı, fotoğrafı ve
        // varyantları durursa operatör tanınmayan kodu tanınmış sanar.
        vm.Name.Should().BeEmpty();
        vm.CoverPhotoKey.Should().BeNull();
        vm.Variants.Should().BeEmpty();
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
        // Kartta katalogun KANONİK yazımı durur, operatörün tuşladığı metin
        // değil: kod ekrandan okunup panele/kargoya yazılabiliyor.
        vm.Code.Should().Be("GUZEL ELBISE");
        // Pasif varyant gösterilmez: operatör satamayacağı bir kırılımı görmesin.
        vm.Variants.Should().ContainSingle().Which.Display.Should().Be("Kırmızı · M");
    }

    [Fact]
    public void Loading_a_second_product_drops_the_first_products_variants()
    {
        var (vm, repo, _) = Make();
        SeedTwoProducts(repo);

        vm.Load("A1");
        vm.Load("B2");

        vm.Name.Should().Be("Mavi Etek");
        // Üretimde Load her tuş vuruşunda koşuyor; birikirse kartta iki ayrı
        // ürünün varyantları yan yana durur ve operatör yanlışını okutur.
        vm.Variants.Select(v => v.Display).Should().Equal("Mavi · L");
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
    public void Cover_key_change_raises_a_change_for_the_photo_path()
    {
        var (vm, repo, _) = Make();
        SeedTwoProducts(repo);

        var changed = new List<string?>();
        ((INotifyPropertyChanged)vm).PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        vm.Load("A1");

        // PhotoAbsolutePath hesaplanan bir özellik: CoverPhotoKey değişince
        // haber verilmezse Image bağı ilk çizimdeki değerde donar ve kart
        // yeni ürünün adıyla ESKİ ürünün fotoğrafını gösterir.
        changed.Should().Contain(nameof(ProductCardViewModel.PhotoAbsolutePath));
    }

    [Fact]
    public void Variant_without_axis_values_falls_back_to_its_code()
    {
        var vm = new CatalogVariantViewModel(
            new CatalogVariant("v1", "p1", null, null, null, null, "A1", null, true, 0));

        vm.Display.Should().Be("A1");
    }

    // Tek eksenli ürün (yalnız "Beden") bu katalogda İSTİSNA DEĞİL, olağan hâl.
    // İkinci eksen süzülmezse rozette "M · " yazar; kimsenin fark etmeyeceği
    // kadar küçük, her üründe görünecek kadar sık. Üç biçimin de sınanmasının
    // sebebi ölçüldü: süzgeç `v is not null`'a düşürülünce yalnız null durumu
    // yeşil kalıyor — sunucudan boş dize gelen kurulumda rozet bozulurdu ve
    // CatalogSyncService değeri olduğu gibi geçiriyor.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Variant_with_a_single_axis_has_no_dangling_separator(string? missingAxis)
    {
        var vm = new CatalogVariantViewModel(
            new CatalogVariant("v1", "p1", "M", "M", missingAxis, null, "A1-M", null, true, 0));

        vm.Display.Should().Be("M");
    }
}
