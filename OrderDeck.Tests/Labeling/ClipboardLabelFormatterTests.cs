using FluentAssertions;
using OrderDeck.Labeling;
using Xunit;

namespace OrderDeck.Tests.Labeling;

public class ClipboardLabelFormatterTests
{
    private readonly ClipboardLabelFormatter _f = new();

    [Fact]
    public void Format_uses_at_username_and_original_message()
    {
        var clipboard = _f.Format("@ayse_y", "MAVI XL aldım");
        clipboard.Should().Be("@ayse_y MAVI XL aldım");
    }

    [Fact]
    public void Format_inserts_at_when_username_missing_prefix()
    {
        var clipboard = _f.Format("ayse_y", "MAVI XL aldım");
        clipboard.Should().Be("@ayse_y MAVI XL aldım");
    }

    [Fact]
    public void Format_collapses_internal_whitespace()
    {
        var clipboard = _f.Format("@ayse_y", "MAVI    XL  aldım");
        clipboard.Should().Be("@ayse_y MAVI XL aldım");
    }

    [Fact]
    public void Format_trims_outer_whitespace()
    {
        var clipboard = _f.Format("  @ayse_y  ", "  MAVI XL  ");
        clipboard.Should().Be("@ayse_y MAVI XL");
    }
}
