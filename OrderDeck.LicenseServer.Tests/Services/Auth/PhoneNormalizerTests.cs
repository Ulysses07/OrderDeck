using FluentAssertions;
using OrderDeck.LicenseServer.Services.Auth;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

public class PhoneNormalizerTests
{
    // Numara her koşuda üretilir; InlineData yalnız yazılış biçimini taşır.
    // {0} operatör kodu (3 hane), {1} 3 hane, {2} ve {3} ikişer hane.
    [Theory]
    [InlineData("{0}{1}{2}{3}")]
    [InlineData("0{0}{1}{2}{3}")]
    [InlineData("+90{0}{1}{2}{3}")]
    [InlineData("90{0}{1}{2}{3}")]
    [InlineData("0 {0} {1} {2} {3}")]
    [InlineData("0{0}-{1}-{2}-{3}")]
    [InlineData("+90 {0} {1} {2}{3}")]
    public void Normalize_returns_E164_for_valid_TR_input(string format)
    {
        var n = TestPhone.NewNational();
        var input = string.Format(format, n[..3], n[3..6], n[6..8], n[8..]);

        PhoneNormalizer.Normalize(input).Should().Be("+90" + n);
    }

    // {0} üretilen 10 haneli ulusal numara, {1} rastgele 8 hane,
    // {2} rastgele 9 hane, {3} rastgele 3 hane.
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("123")]
    [InlineData("{0}xx")]
    [InlineData("+15551112233")]
    [InlineData("{0}{3}")]
    [InlineData("05{1}")]          // 9 hane + baştaki 0
    [InlineData("2{2}")]           // sabit hat
    [InlineData("+902{2}")]        // sabit hat, E.164
    [InlineData("03{2}")]          // abone 3 ile başlıyor
    public void Normalize_throws_for_invalid_input(string format)
    {
        var input = string.Format(format, TestPhone.NewNational(),
            TestPhone.Digits(8), TestPhone.Digits(9), TestPhone.Digits(3));

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
        var n = TestPhone.NewNational();
        PhoneNormalizer.TryNormalize($"0{n[..3]} {n[3..6]} {n[6..]}", out var result).Should().BeTrue();
        result.Should().Be("+90" + n);
    }
}
