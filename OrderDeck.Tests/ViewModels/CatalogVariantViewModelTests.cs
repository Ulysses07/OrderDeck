using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// Ürün kartındaki varyant rozetinin METNİNİ sınar. Testler daha önce
/// <c>ProductCardViewModelTests</c> içinde duruyordu; oysa kartın yükleme
/// akışıyla ilgileri yok, ölçtükleri tek şey bu görünüm modelinin etiketi.
/// </summary>
public class CatalogVariantViewModelTests
{
    private static CatalogVariant Variant(string? axis1, string? axis2)
        => new("v1", "p1", axis1, axis2, null, true, 0);

    [Fact]
    public void Variant_with_two_axes_joins_them_with_a_separator()
    {
        var vm = new CatalogVariantViewModel(Variant("Siyah", "M"), "A1");

        vm.Display.Should().Be("Siyah · M");
    }

    [Fact]
    public void Variant_without_axis_values_falls_back_to_the_caller_supplied_label()
    {
        // Eksensiz üründe de tam bir varyant var ama gösterilecek eksen değeri
        // yok; rozette ürünün stok kodu durur. Kod çağırandan geliyor: bu
        // görünüm modeli ürünü hiç görmüyor.
        var vm = new CatalogVariantViewModel(Variant(null, null), "A1");

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
        var vm = new CatalogVariantViewModel(Variant("M", missingAxis), "A1");

        vm.Display.Should().Be("M");
    }
}
