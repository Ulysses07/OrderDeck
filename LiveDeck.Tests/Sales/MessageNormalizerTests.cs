using FluentAssertions;
using LiveDeck.Core.Sales.Pipeline;
using Xunit;

namespace LiveDeck.Tests.Sales;

public class MessageNormalizerTests
{
    private readonly MessageNormalizer _n = new();

    [Theory]
    [InlineData("Mavi xl aldım", "MAVI XL ALDIM")]
    [InlineData("MAVİ XL", "MAVI XL")]
    [InlineData("mavi̇ xl", "MAVI XL")]
    [InlineData("Kırmızı M", "KIRMIZI M")]
    [InlineData("İSTANBUL", "ISTANBUL")]
    public void Normalizes_turkish_characters_and_uppercases(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("mavi   xl", "MAVI XL")]
    [InlineData("  mavi xl  ", "MAVI XL")]
    [InlineData("mavi\txl\nm", "MAVI XL M")]
    public void Collapses_whitespace(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("mavi 🌹 xl", "MAVI XL")]
    [InlineData("MAVİ ❤️ M ALDIM", "MAVI M ALDIM")]
    public void Strips_emoji(string input, string expected)
    {
        _n.Normalize(input).Should().Be(expected);
    }

    [Fact]
    public void Empty_input_returns_empty()
    {
        _n.Normalize("").Should().Be("");
        _n.Normalize("   ").Should().Be("");
    }
}
