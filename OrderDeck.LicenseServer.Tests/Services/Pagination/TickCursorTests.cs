using FluentAssertions;
using OrderDeck.LicenseServer.Services.Pagination;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Pagination;

public class TickCursorTests
{
    [Fact]
    public void Encode_then_decode_roundtrips_exact_values()
    {
        var at = new DateTimeOffset(2026, 5, 23, 14, 30, 0, TimeSpan.Zero);
        var id = Guid.Parse("00000000-0000-0000-0000-000000000001");

        var enc = TickCursor.Encode(at, id);
        TickCursor.TryDecode(enc, out var ticks, out var decodedId).Should().BeTrue();

        ticks.Should().Be(at.UtcTicks);
        decodedId.Should().Be(id);
    }

    [Fact]
    public void Decode_null_returns_false()
    {
        TickCursor.TryDecode(null, out var ticks, out var id).Should().BeFalse();
        ticks.Should().Be(0);
        id.Should().Be(Guid.Empty);
    }

    [Fact]
    public void Decode_empty_returns_false()
    {
        TickCursor.TryDecode("", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Decode_no_separator_returns_false()
    {
        TickCursor.TryDecode("12345", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Decode_non_numeric_ticks_returns_false()
    {
        TickCursor.TryDecode("abc|00000000-0000-0000-0000-000000000001", out _, out _)
            .Should().BeFalse();
    }

    [Fact]
    public void Decode_invalid_guid_returns_false()
    {
        TickCursor.TryDecode("12345|not-a-guid", out _, out _).Should().BeFalse();
    }

    [Fact]
    public void Encode_uses_N_format_no_dashes()
    {
        var id = Guid.Parse("12345678-1234-1234-1234-123456789012");
        var enc = TickCursor.Encode(DateTimeOffset.UtcNow, id);
        enc.Should().NotContain("-");
        enc.Should().Contain("|");
    }
}
