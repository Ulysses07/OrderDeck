using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public class PhoneNormalizerTests
{
    // Numara her koşuda üretilir; InlineData yalnız yazılış biçimini taşır.
    // {0} operatör kodu (3 hane), {1} 3 hane, {2} ve {3} ikişer hane.
    [Theory]
    [InlineData("{0}{1}{2}{3}")]
    [InlineData("0{0}{1}{2}{3}")]
    [InlineData("+90{0}{1}{2}{3}")]
    [InlineData("0 {0} {1}-{2}-{3}")]
    public void NormalizeTr_AcceptsCommonFormats(string format)
    {
        var n = TestPhone.NewNational();
        var input = string.Format(format, n[..3], n[3..6], n[6..8], n[8..]);

        PhoneNormalizer.NormalizeTr(input).Should().Be("+90" + n);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("123")]
    public void NormalizeTr_RejectsInvalid(string? input)
        => PhoneNormalizer.NormalizeTr(input).Should().BeNull();

    // {0} üretilen 10 haneli ulusal numara, {1} onun ilk 9 hanesi.
    [Theory]
    [InlineData("+90{0}", true)]
    [InlineData("+90{1}", false)]   // too short
    [InlineData("+90{0}8", false)]  // too long
    [InlineData("90{0}", false)]    // missing +
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsValidTr_ValidatesE164Format(string? format, bool expected)
    {
        var n = TestPhone.NewNational();
        var input = format is null ? null : string.Format(format, n, n[..9]);

        PhoneNormalizer.IsValidTr(input).Should().Be(expected);
    }
}
