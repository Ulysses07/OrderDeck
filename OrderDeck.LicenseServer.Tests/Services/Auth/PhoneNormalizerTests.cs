using FluentAssertions;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("5551112233", "+905551112233")]
    [InlineData("05551112233", "+905551112233")]
    [InlineData("+905551112233", "+905551112233")]
    [InlineData("905551112233", "+905551112233")]
    [InlineData("0 555 111 22 33", "+905551112233")]
    [InlineData("0555-111-22-33", "+905551112233")]
    [InlineData("+90 555 111 2233", "+905551112233")]
    public void Normalize_returns_E164_for_valid_TR_input(string input, string expected)
    {
        PhoneNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("5551112233xx")]
    [InlineData("+15551112233")]
    [InlineData("5551112233444")]
    public void Normalize_throws_for_invalid_input(string input)
    {
        Action act = () => PhoneNormalizer.Normalize(input);
        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void TryNormalize_returns_false_for_invalid()
    {
        PhoneNormalizer.TryNormalize("garbage", out var result).Should().BeFalse();
        result.Should().BeNull();
    }

    [Fact]
    public void TryNormalize_returns_true_for_valid()
    {
        PhoneNormalizer.TryNormalize("0555 111 2233", out var result).Should().BeTrue();
        result.Should().Be("+905551112233");
    }
}
