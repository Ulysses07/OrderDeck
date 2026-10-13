using FluentAssertions;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales;

/// <summary>
/// FİYAT kutusunun tek okuyucusu. Eski okuma kültürden bağımsız biçimi önce deniyordu:
/// "99,50" → 9950 TL, "1.500" → 1,5 TL — yayında yanlış fiyatlı etiketin kaynaklarından biri.
/// </summary>
public class PriceInputTests
{
    [Theory]
    // Tek ayraç + 1–2 hane → ondalık
    [InlineData("99,50", "99.50")]
    [InlineData("99.50", "99.50")]
    [InlineData("99,5", "99.5")]
    [InlineData("1.5", "1.5")]
    // Tek ayraç + tam 3 hane (ve tekrarlayan 3'lü gruplar) → binlik
    [InlineData("1.500", "1500")]
    [InlineData("1,500", "1500")]
    [InlineData("1.500.000", "1500000")]
    [InlineData("12,345,678", "12345678")]
    // İki tür ayraç: sonuncusu ondalık
    [InlineData("1.500,50", "1500.50")]
    [InlineData("1,500.50", "1500.50")]
    [InlineData("1.234.567,8", "1234567.8")]
    [InlineData("1500,50", "1500.50")]
    // Ayraçsız
    [InlineData("250", "250")]
    [InlineData("0", "0")]
    [InlineData("0,00", "0")]
    // Boşluk, ₺ ve TL atılır
    [InlineData(" 250 ", "250")]
    [InlineData("₺250", "250")]
    [InlineData("250 TL", "250")]
    [InlineData("250tl", "250")]
    [InlineData("1 500", "1500")]
    [InlineData("1 500,00 ₺", "1500")]
    [InlineData("-0", "0")]
    public void Valid_inputs_read_as_expected(string text, string expected)
    {
        var r = PriceInput.Parse(text);

        r.Status.Should().Be(PriceInputStatus.Ok, text);
        r.Value.Should().Be(decimal.Parse(expected, System.Globalization.CultureInfo.InvariantCulture), text);
    }

    [Theory]
    [InlineData("1.2345")]      // 4 hane: ne binlik ne kuruş
    [InlineData("1234.567")]    // ilk grup 3'ten uzun
    [InlineData("1.50.000")]    // bozuk grup
    [InlineData("1,50.00")]     // binlik grubu 3 hane değil
    [InlineData("1.500,505")]   // kuruş 3 hane
    [InlineData("1,500,50.5,0")]
    [InlineData(".50")]
    [InlineData("5.")]
    [InlineData("abc")]
    [InlineData("12a")]
    [InlineData("+50")]
    [InlineData("-")]
    public void Ambiguous_or_garbage_inputs_are_rejected(string text)
        => PriceInput.Parse(text).Status.Should().Be(PriceInputStatus.Invalid, text);

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("TL")]
    [InlineData("₺")]
    [InlineData(null)]
    public void Empty_inputs_are_empty(string? text)
        => PriceInput.Parse(text).Status.Should().Be(PriceInputStatus.Empty);

    [Theory]
    [InlineData("-50")]
    [InlineData("-1.500")]
    [InlineData("−50")]
    public void Negative_prices_are_rejected(string text)
        => PriceInput.Parse(text).Status.Should().Be(PriceInputStatus.Negative, text);

    [Theory]
    [InlineData("1500")]
    [InlineData("99.5")]
    [InlineData("0.01")]
    [InlineData("1234567.89")]
    public void Format_round_trips_through_parse(string value)
    {
        var d = decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);
        PriceInput.Parse(PriceInput.Format(d)).Value.Should().Be(d);
    }
}
