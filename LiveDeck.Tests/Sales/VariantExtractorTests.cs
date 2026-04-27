using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class VariantExtractorTests
{
    private readonly VariantExtractor _x = new();

    [Theory]
    [InlineData("MAVI XL ALDIM", new[] { "S", "M", "L", "XL" }, "XL")]
    [InlineData("MAVI M ALDIM",  new[] { "S", "M", "XL" },      "M")]
    [InlineData("MAVI 38 ALDIM", new[] { "36", "38", "40" },     "38")]
    [InlineData("MAVI ALDIM TEK BEDEN", new[] { "TEK BEDEN" },   "TEK BEDEN")]
    [InlineData("MAVI ALDIM",   new[] { "TEK BEDEN" },           "TEK BEDEN")]
    public void Extracts_size_when_listed_in_active_code(
        string normalised, string[] sizes, string expected)
    {
        _x.Extract(normalised, sizes).Should().Be(expected);
    }

    [Theory]
    [InlineData("MAVI ALDIM", new[] { "S", "M", "XL" })]
    [InlineData("MAVI XS ALDIM", new[] { "S", "M", "XL" })]
    public void Returns_null_when_no_listed_size_present(string normalised, string[] sizes)
    {
        _x.Extract(normalised, sizes).Should().BeNull();
    }

    [Theory]
    [InlineData("MAVI M VE XL ALDIM", new[] { "M", "XL" })]
    public void Returns_null_when_multiple_sizes_match(string normalised, string[] sizes)
    {
        _x.Extract(normalised, sizes).Should().BeNull();
    }
}
