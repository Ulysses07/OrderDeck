using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public class PhoneNormalizerTests
{
    [Theory]
    [InlineData("5551234567", "+905551234567")]
    [InlineData("05551234567", "+905551234567")]
    [InlineData("+905551234567", "+905551234567")]
    [InlineData("0 555 123-45-67", "+905551234567")]
    public void NormalizeTr_AcceptsCommonFormats(string input, string expected)
        => PhoneNormalizer.NormalizeTr(input).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("123")]
    public void NormalizeTr_RejectsInvalid(string? input)
        => PhoneNormalizer.NormalizeTr(input).Should().BeNull();

    [Theory]
    [InlineData("+905551234567", true)]
    [InlineData("+90555123456", false)]   // too short
    [InlineData("+9055512345678", false)] // too long
    [InlineData("905551234567", false)]   // missing +
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsValidTr_ValidatesE164Format(string? input, bool expected)
        => PhoneNormalizer.IsValidTr(input).Should().Be(expected);
}
