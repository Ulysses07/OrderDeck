using FluentAssertions;
using LiveDeck.Core.Customers;
using Xunit;

namespace LiveDeck.Tests.Customers;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("5551234567", "+905551234567")]            // 10 digit, no prefix
    [InlineData("05551234567", "+905551234567")]           // 11 digit, leading 0
    [InlineData("905551234567", "+905551234567")]          // 12 digit, no plus
    [InlineData("+905551234567", "+905551234567")]         // already E.164
    [InlineData("+90 555 123 45 67", "+905551234567")]     // spaces
    [InlineData("0 555 123-45-67", "+905551234567")]       // mixed spacing
    [InlineData("(0555) 123 45 67", "+905551234567")]      // parens
    public void NormalizeTr_AcceptsCommonFormats(string input, string expected)
    {
        PhoneNormalizer.NormalizeTr(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("123")]            // too short
    [InlineData("12345678901234")] // too long
    [InlineData("+12025551234")]   // non-TR country code
    public void NormalizeTr_RejectsInvalidInput(string? input)
    {
        PhoneNormalizer.NormalizeTr(input).Should().BeNull();
    }

    [Theory]
    [InlineData("+905551234567", true)]
    [InlineData("+9055512345670", false)]   // 14 chars
    [InlineData("+9055512345", false)]      // 12 chars
    [InlineData("+15551234567", false)]     // not TR
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsValidTr_ChecksE164TrFormat(string? input, bool expected)
    {
        PhoneNormalizer.IsValidTr(input).Should().Be(expected);
    }
}
