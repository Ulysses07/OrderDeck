using FluentAssertions;
using OrderDeck.Core.Sales;
using Xunit;

namespace OrderDeck.Tests.Sales;

public class AnimationCatalogTests
{
    [Fact]
    public void DefaultId_is_wheel()
    {
        AnimationCatalog.DefaultId.Should().Be("wheel");
    }

    [Theory]
    [InlineData("wheel")]
    [InlineData("slot-machine")]
    [InlineData("bingo")]
    [InlineData("card-draw")]
    [InlineData("magic-hat")]
    [InlineData("spotlight-grid")]
    [InlineData("eliminator")]
    [InlineData("roulette-strip")]
    [InlineData("falling-names")]
    [InlineData("race")]
    public void IsKnown_recognises_shipped_ids(string id)
    {
        AnimationCatalog.IsKnown(id).Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("does-not-exist")]
    [InlineData("WHEEL")]            // case-sensitive — uppercase variant rejected
    [InlineData("wheel ")]            // trailing whitespace not trimmed
    [InlineData(null)]
    public void IsKnown_rejects_unknown_or_empty(string? id)
    {
        AnimationCatalog.IsKnown(id!).Should().BeFalse();
    }
}
