using FluentAssertions;
using OrderDeck.App.ViewModels;
using OrderDeck.Core.Catalog;
using Xunit;

namespace OrderDeck.Tests.ViewModels;

/// <summary>
/// "Etiket bas" çekmecesinin görünüm modeli. Yazıcıya hiç dokunmuyor —
/// bu yüzden sınanabilen tek şey listelenen satırlar ve üretilen yük.
/// </summary>
public class BarcodeLabelViewModelTests
{
    private static CatalogProduct Elbise() => new(
        "p1", null, "SK00001", "SK00001", "Elbise", 100m, null,
        "Renk", 1, "Beden", 2, null, 0);

    private static CatalogVariant V(string id, string renk, string beden, string barcode) =>
        new(id, "p1", renk, beden, barcode, true, 0);

    [Fact]
    public void Cozulmus_urunun_varyantlarini_listeler()
    {
        var sut = new BarcodeLabelViewModel();

        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001"),
                    V("v2", "Siyah", "L", "0000000002") },
            new[] { "M", "L" }));

        sut.Rows.Should().HaveCount(2);
        sut.Rows[0].Barcode.Should().Be("0000000001");
    }

    [Fact]
    public void Varsayilan_adet_birdir()
    {
        new BarcodeLabelViewModel().Copies.Should().Be(1);
    }

    [Fact]
    public void Hicbir_satir_secili_degilse_basilamaz()
    {
        var sut = new BarcodeLabelViewModel();
        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001") },
            new[] { "M" }));

        sut.Rows[0].IsSelected = false;

        sut.CanPrint.Should().BeFalse();
    }

    [Fact]
    public void Secili_satirlar_etikete_cevrilir()
    {
        var sut = new BarcodeLabelViewModel();
        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001"),
                    V("v2", "Siyah", "L", "0000000002") },
            new[] { "M", "L" }));
        sut.Rows[1].IsSelected = false;

        var labels = sut.BuildLabels();

        labels.Should().ContainSingle();
        labels[0].Barcode.Should().Be("0000000001");
        labels[0].ProductName.Should().Be("Elbise");
        labels[0].VariantName.Should().Be("Siyah · M");
    }

    /// <summary>
    /// Barkodsuz varyant sunucuda olamaz, ama WPF replikası bayat olabilir
    /// (senkron turu daha gelmemiş). Boş yükle basılan etiket okunamayan bir
    /// çıktı üretir ve hata ancak ürün rafa gittikten sonra anlaşılır.
    /// </summary>
    [Fact]
    public void Barkodsuz_varyant_listelenmez()
    {
        var sut = new BarcodeLabelViewModel();

        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001"),
                    new CatalogVariant("v2", "p1", "Siyah", "L", null, true, 1),
                    new CatalogVariant("v3", "p1", "Siyah", "XL", "   ", true, 2) },
            new[] { "M", "L", "XL" }));

        sut.Rows.Should().ContainSingle();
        sut.Rows[0].Barcode.Should().Be("0000000001");
    }

    /// <summary>
    /// Operatör arka arkaya iki ürünün etiketini basıyor. İlk ürünün satırları
    /// kalsaydı ikinci basımda YANLIŞ ürüne etiket çıkardı — sessiz ve pahalı
    /// bir hata, çünkü çıktı gözle kusursuz görünür.
    /// </summary>
    [Fact]
    public void Ikinci_yukleme_onceki_urunun_satirlarini_birakmaz()
    {
        var sut = new BarcodeLabelViewModel();
        sut.Load(new BroadcastCodeResolution(
            Elbise(), "ATES", "Siyah", "Beden", 2,
            new[] { V("v1", "Siyah", "M", "0000000001"),
                    V("v2", "Siyah", "L", "0000000002") },
            new[] { "M", "L" }));

        var pantolon = new CatalogProduct(
            "p2", null, "SK00002", "SK00002", "Pantolon", 200m, null,
            "Renk", 1, "Beden", 2, null, 0);
        sut.Load(new BroadcastCodeResolution(
            pantolon, "KAYA", "Mavi", "Beden", 2,
            new[] { new CatalogVariant("v9", "p2", "Mavi", "38", "0000000009", true, 0) },
            new[] { "38" }));

        sut.Rows.Should().ContainSingle();
        sut.Rows[0].Barcode.Should().Be("0000000009");
        sut.ProductName.Should().Be("Pantolon");
        sut.BuildLabels().Should().ContainSingle()
            .Which.ProductName.Should().Be("Pantolon");
    }
}
