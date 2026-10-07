using FluentAssertions;
using OrderDeck.Core.Customers;
using OrderDeck.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.Tests.Customers;

public class PhoneNormalizerTests
{
    // Numara her koşuda üretilir; InlineData yalnız yazılış biçimini taşır.
    // {0} operatör kodu (3 hane), {1} 3 hane, {2} ve {3} ikişer hane.
    [Theory]
    [InlineData("{0}{1}{2}{3}")]            // 10 digit, no prefix
    [InlineData("0{0}{1}{2}{3}")]           // 11 digit, leading 0
    [InlineData("90{0}{1}{2}{3}")]          // 12 digit, no plus
    [InlineData("+90{0}{1}{2}{3}")]         // already E.164
    [InlineData("+90 {0} {1} {2} {3}")]     // spaces
    [InlineData("0 {0} {1}-{2}-{3}")]       // mixed spacing
    [InlineData("(0{0}) {1} {2} {3}")]      // parens
    public void NormalizeTr_AcceptsCommonFormats(string format)
    {
        var n = TestPhone.NewNational();
        var input = string.Format(format, n[..3], n[3..6], n[6..8], n[8..]);

        PhoneNormalizer.NormalizeTr(input).Should().Be("+90" + n);
    }

    // {0} rastgele 8 hane, {1} rastgele 9 hane.
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("123")]            // too short
    [InlineData("12345678901234")] // too long
    [InlineData("+15551234567")]   // non-TR country code
    [InlineData("05{0}")]          // 9 hane + baştaki 0 → 10 hane sanılıyordu
    [InlineData("2{1}")]           // sabit hat (2 ile başlar), mobil değil
    [InlineData("03{1}")]          // 11 hane ama abone 3 ile başlıyor
    [InlineData("904{1}")]         // 12 hane ama abone 4 ile başlıyor
    public void NormalizeTr_RejectsInvalidInput(string? format)
    {
        var input = format is null
            ? null
            : string.Format(format, TestPhone.Digits(8), TestPhone.Digits(9));

        PhoneNormalizer.NormalizeTr(input).Should().BeNull();
    }

    // {0} üretilen 10 haneli ulusal numara, {1} onun ilk 9 hanesi,
    // {2} rastgele 9 hane (sabit hat gövdesi).
    [Theory]
    [InlineData("+90{0}", true)]
    [InlineData("+90{0}0", false)]          // 14 chars
    [InlineData("+90{1}", false)]           // 12 chars
    [InlineData("+15551234567", false)]     // not TR
    [InlineData("+902{2}", false)]          // sabit hat E.164 uzunluğunda
    [InlineData(null, false)]
    [InlineData("", false)]
    public void IsValidTr_ChecksE164TrFormat(string? format, bool expected)
    {
        var n = TestPhone.NewNational();
        var input = format is null
            ? null
            : string.Format(format, n, n[..9], TestPhone.Digits(9));

        PhoneNormalizer.IsValidTr(input).Should().Be(expected);
    }
}
