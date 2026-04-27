using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class QuantityExtractorTests
{
    private readonly QuantityExtractor _x = new();

    [Theory]
    [InlineData("MAVI XL ALDIM", 1)]
    [InlineData("MAVI XL 2 TANE ALDIM", 2)]
    [InlineData("MAVI XL 3 ADET ALDIM", 3)]
    [InlineData("MAVI XL X2 ALDIM", 2)]
    [InlineData("MAVI XL +2 ALDIM", 2)]
    [InlineData("MAVI XL IKI TANE", 2)]
    [InlineData("MAVI XL UC ADET", 3)]
    [InlineData("MAVI XL IKISER ALDIM", 2)]
    [InlineData("MAVI XL DORT ALDIM", 4)]
    public void Extracts_quantity_or_defaults_to_one(string msg, int expected)
    {
        _x.Extract(msg).Should().Be(expected);
    }

    [Theory]
    [InlineData("MAVI XL 99 TANE", 99)]
    public void Caps_extreme_values_at_50(string msg, int _)
    {
        _x.Extract(msg).Should().BeLessOrEqualTo(50);
    }
}
