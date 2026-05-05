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

    [Fact]
    public void IsKnown_recognises_wheel()
    {
        AnimationCatalog.IsKnown("wheel").Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("slot-machine")]
    [InlineData(null)]
    public void IsKnown_rejects_unknown_or_empty(string? id)
    {
        AnimationCatalog.IsKnown(id!).Should().BeFalse();
    }
}
