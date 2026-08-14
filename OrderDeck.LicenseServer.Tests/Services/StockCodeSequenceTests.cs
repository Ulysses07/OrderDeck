using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;

namespace OrderDeck.LicenseServer.Tests.Services;

public class StockCodeSequenceTests
{
    [Fact]
    public void First_code_is_SK00001()
    {
        StockCodeSequence.Next(Array.Empty<string>()).Should().Be("SK00001");
    }

    [Fact]
    public void Next_takes_the_highest_number_plus_one()
    {
        StockCodeSequence.Next(new[] { "SK00001", "SK00002" }).Should().Be("SK00003");
    }

    /// <summary>
    /// Boşluk DOLDURULMAZ. Silinen ürünün kodunu yeniden vermek, o kodla
    /// basılmış etiketi ve geçmiş stok hareketini başka bir ürüne bağlardı.
    /// </summary>
    [Fact]
    public void Gaps_are_never_reused()
    {
        StockCodeSequence.Next(new[] { "SK00001", "SK00005" }).Should().Be("SK00006");
    }

    [Fact]
    public void Unparseable_codes_are_ignored()
    {
        StockCodeSequence.Next(new[] { "A1", "", "   ", "SKABCDE", "SK1" })
            .Should().Be("SK00001");
    }

    [Fact]
    public void Casing_and_padding_do_not_matter()
    {
        StockCodeSequence.Next(new[] { " sk00009 " }).Should().Be("SK00010");
    }

    /// <summary>
    /// Tavan aşılırsa kırılmadan büyür: 99999'dan sonra SK100000. Sayaç
    /// dolunca istisna atmak, lisansın ürün eklemesini tamamen durdururdu.
    /// </summary>
    [Fact]
    public void Overflows_into_six_digits_instead_of_throwing()
    {
        StockCodeSequence.Next(new[] { "SK99999" }).Should().Be("SK100000");
    }
}
