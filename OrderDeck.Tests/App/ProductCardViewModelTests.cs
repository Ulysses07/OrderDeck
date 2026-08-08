using System.IO;
using System.Linq;
using FluentAssertions;
using Moq;
using OrderDeck.App.Services;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using OrderDeck.Core.Storage;
using OrderDeck.Core.Storage.Repositories;
using OrderDeck.Core.Time;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.App;

public class ProductCardViewModelTests
{
    private static (ProductCardViewModel Vm, ProductRepository Repo) Make()
    {
        var db = new InMemorySqlite();
        new MigrationRunner(db).Run();
        var repo = new ProductRepository(db);
        var photos = new ProductPhotoStore(
            Path.Combine(Path.GetTempPath(), "od-card-" + System.Guid.NewGuid().ToString("N")));
        var clock = new Mock<IClock>();
        clock.Setup(c => c.UnixNow()).Returns(2000L);
        return (new ProductCardViewModel(repo, photos, clock.Object), repo);
    }

    [Fact]
    public void Load_unknown_code_offers_definition_without_opening_the_form()
    {
        var (vm, _) = Make();

        vm.Load("A100");

        vm.Code.Should().Be("A100");
        vm.HasProduct.Should().BeFalse();
        // Form KENDİLİĞİNDEN açılmaz: kod kutusuna "A100" yazılırken "A",
        // "A1", "A10" da tanınmayan kodlardır — form her tuşta açılıp
        // kapanırdı. Kart yalnız "tanımlı değil" der.
        vm.IsUnknown.Should().BeTrue();
        vm.IsEditing.Should().BeFalse();
        vm.Name.Should().BeEmpty();
        vm.Sizes.Should().BeEmpty();
    }

    [Fact]
    public void BeginEdit_opens_the_form_for_an_unknown_code()
    {
        var (vm, _) = Make();
        vm.Load("A100");

        vm.BeginEditCommand.Execute(null);

        vm.IsEditing.Should().BeTrue();
        vm.IsUnknown.Should().BeFalse();
    }

    [Fact]
    public void Load_known_code_shows_saved_product()
    {
        var (vm, repo) = Make();
        repo.Save(new Product("A100", "Kırmızı Elbise", "a100.jpg", 0),
            new[] { new ProductSize("A100", "S", 3, 0), new ProductSize("A100", "M", 0, 1) });

        vm.Load("A100");

        vm.HasProduct.Should().BeTrue();
        vm.IsEditing.Should().BeFalse();
        vm.Name.Should().Be("Kırmızı Elbise");
        vm.Sizes.Select(s => s.Size).Should().Equal("S", "M");
        vm.Sizes[0].Quantity.Should().Be(3);
    }

    [Fact]
    public void Load_blank_code_clears_the_card()
    {
        var (vm, repo) = Make();
        repo.Save(new Product("A100", "Kırmızı Elbise", null, 0), new[] { new ProductSize("A100", "S", 3, 0) });
        vm.Load("A100");

        vm.Load("");

        // Hero'daki kod kutusu boşaltıldığında kart eski ürünü göstermeye
        // devam ederse operatör yanlış stoğa bakar.
        vm.HasProduct.Should().BeFalse();
        vm.IsEditing.Should().BeFalse();
        vm.Sizes.Should().BeEmpty();
    }

    [Fact]
    public void ApplySizesText_creates_tiles_in_written_order()
    {
        var (vm, _) = Make();
        vm.Load("A100");

        vm.SizesText = "S, M, L, XL";
        vm.ApplySizesText();

        vm.Sizes.Select(s => s.Size).Should().Equal("S", "M", "L", "XL");
        vm.Sizes.Select(s => s.SortOrder).Should().Equal(0, 1, 2, 3);
        vm.Sizes.Should().OnlyContain(s => s.Quantity == 0);
    }

    [Fact]
    public void ApplySizesText_keeps_quantities_of_surviving_sizes()
    {
        var (vm, _) = Make();
        vm.Load("A100");
        vm.SizesText = "S,M";
        vm.ApplySizesText();
        vm.Sizes[1].Quantity = 7;   // M = 7

        vm.SizesText = "M,L";
        vm.ApplySizesText();

        // Operatör beden setini düzeltirken hayatta kalan bedenin adedini
        // yeniden yazmak zorunda kalmamalı.
        vm.Sizes.Select(s => s.Size).Should().Equal("M", "L");
        vm.Sizes[0].Quantity.Should().Be(7);
    }

    [Fact]
    public void ApplySizesText_drops_duplicates_and_blanks()
    {
        var (vm, _) = Make();
        vm.Load("A100");

        vm.SizesText = "S, , s ,M,,M";
        vm.ApplySizesText();

        // Beden Product tablosunda PK'nın parçası — çift satır INSERT'te
        // patlar; burada eleriz.
        vm.Sizes.Select(s => s.Size).Should().Equal("S", "M");
    }

    [Fact]
    public void Save_persists_product_and_leaves_edit_mode()
    {
        var (vm, repo) = Make();
        vm.Load("A100");
        vm.Name = "Kırmızı Elbise";
        vm.SizesText = "S,M";
        vm.ApplySizesText();
        vm.Sizes[0].Quantity = 4;

        vm.SaveCommand.Execute(null);

        vm.IsEditing.Should().BeFalse();
        vm.HasProduct.Should().BeTrue();
        repo.Get("A100")!.Name.Should().Be("Kırmızı Elbise");
        repo.Get("A100")!.UpdatedAt.Should().Be(2000);   // IClock, unix SANİYE
        repo.GetSizes("A100").Single(s => s.Size == "S").Quantity.Should().Be(4);
    }

    [Fact]
    public void Save_is_blocked_while_name_is_blank()
    {
        var (vm, repo) = Make();
        vm.Load("A100");
        vm.SizesText = "S";
        vm.ApplySizesText();

        vm.SaveCommand.CanExecute(null).Should().BeFalse();

        vm.Name = "Kırmızı Elbise";
        vm.SaveCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void CancelEdit_restores_the_saved_state()
    {
        var (vm, repo) = Make();
        repo.Save(new Product("A100", "Kırmızı Elbise", null, 0),
            new[] { new ProductSize("A100", "S", 3, 0) });
        vm.Load("A100");
        vm.BeginEditCommand.Execute(null);
        vm.Name = "Bozuk isim";
        vm.SizesText = "XXL";
        vm.ApplySizesText();

        vm.CancelEditCommand.Execute(null);

        vm.Name.Should().Be("Kırmızı Elbise");
        vm.Sizes.Select(s => s.Size).Should().Equal("S");
        vm.IsEditing.Should().BeFalse();
    }

    [Fact]
    public void SetPhoto_copies_the_file_and_exposes_absolute_path()
    {
        var (vm, _) = Make();
        vm.Load("A100");
        var src = Path.Combine(Path.GetTempPath(), System.Guid.NewGuid().ToString("N") + ".png");
        File.WriteAllBytes(src, new byte[] { 1 });

        vm.SetPhoto(src);

        vm.PhotoPath.Should().Be("a100.png");
        vm.PhotoAbsolutePath.Should().NotBeNull();
        File.Exists(vm.PhotoAbsolutePath!).Should().BeTrue();
        File.Delete(src);
    }

    [Theory]
    [InlineData(0, false, true)]
    [InlineData(1, true, false)]
    [InlineData(2, true, false)]
    [InlineData(3, false, false)]
    public void Size_tile_low_and_out_flags(int qty, bool low, bool outOfStock)
    {
        var tile = new ProductSizeViewModel("M", qty, 0);

        // Mockup: .cnt.low amber, .size.out soluk + üstü çizili.
        tile.IsLow.Should().Be(low);
        tile.IsOutOfStock.Should().Be(outOfStock);
    }

    [Fact]
    public void Size_tile_flags_react_to_quantity_edits()
    {
        var tile = new ProductSizeViewModel("M", 5, 0);

        tile.Quantity = 0;

        // Adet kartta satır-içi düzenleniyor; rozetler anında dönmezse
        // operatör tükenmiş bedeni fark etmez.
        tile.IsOutOfStock.Should().BeTrue();
        tile.IsLow.Should().BeFalse();
    }
}
