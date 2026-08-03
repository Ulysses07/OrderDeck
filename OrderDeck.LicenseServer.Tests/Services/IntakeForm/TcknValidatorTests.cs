using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// TC Kimlik No kontrol basamağı. Geçersiz örnekler prod verisinden çıkan
/// desenlerdir (dolgu numaralar + kontrol basamağı tutmayanlar); geçerli
/// örnekler algoritmayla ÜRETİLDİ — gerçek kimlik numarası repo'ya girmez.
/// </summary>
public class TcknValidatorTests
{
    [Theory]
    [InlineData("12345678950")]
    [InlineData("10000000078")]
    [InlineData("98765432150")]
    public void Valid_numbers_pass(string tckn)
        => TcknValidator.Validate(tckn).Should().BeNull();

    [Theory]
    [InlineData("11111111111")]   // gerçek veride 4 kez geçen dolgu
    [InlineData("12345678901")]   // kontrol basamağı tutmuyor
    [InlineData("12345678951")]   // d10 doğru, d11 yanlış
    public void Checksum_failures_are_rejected(string tckn)
        => TcknValidator.Validate(tckn).Should().Contain("geçersiz");

    [Theory]
    [InlineData("1234567895")]    // 10 hane
    [InlineData("123456789500")]  // 12 hane
    [InlineData("1234567895a")]
    [InlineData("123 4567895")]
    public void Wrong_shape_gets_the_length_message(string tckn)
        => TcknValidator.Validate(tckn).Should().Contain("11 rakam");

    [Fact]
    public void Leading_zero_is_rejected()
        => TcknValidator.Validate("01234567895").Should().Contain("0 ile başlayamaz");

    [Fact]
    public void Blank_is_valid_because_the_field_is_optional()
    {
        TcknValidator.Normalize("   ").Should().BeNull();
        TcknValidator.Validate(null).Should().BeNull();
        TcknValidator.Validate("").Should().BeNull();
    }

    [Fact]
    public void Normalize_trims_surrounding_whitespace()
        => TcknValidator.Normalize("  12345678950 ").Should().Be("12345678950");
}
