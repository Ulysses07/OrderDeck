using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
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

    // TestTckn üretiminin bu dosyadaki algoritmayla tutarlı kaldığını doğrular
    // — TcknAtRestTests gibi başka yerler üretilen değerin GEÇERLİ
    // olduğunu varsayıyor. 1000 tekrar genel bir fuzz taraması — negatif-mod
    // dalı gibi nadir bir deseni GÜVENİLİR yakalamaz (p≈1,1e-4 çekiliş
    // başına); o dal aşağıdaki Negatif_mod_dali_dogru_normalize_edilir'de
    // belirlenimli olarak sınanıyor.
    [Fact]
    public void TestTckn_uretimi_kontrol_basamaklariyla_tutarli()
    {
        for (var i = 0; i < 1000; i++)
            TcknValidator.Validate(TestTckn.NewValid()).Should().BeNull();
    }

    // odd*7 < even deseni kasıtlı zorlanıyor (1+0+0+0+0)*7=7 < 9+9+9+9=36 —
    // normalize eksikse (("...%10+10)%10" yerine yalnız "%10") C#'ta negatif
    // kalan döner ve bu TCKN geçersiz sayılır. Dizi doğrudan rakamlardan
    // kuruluyor, kimlik-şekilli bir dize yazılmıyor.
    [Fact]
    public void Negatif_mod_dali_dogru_normalize_edilir()
    {
        var tckn = TestTckn.FromFirstNine(new[] { 1, 9, 0, 9, 0, 9, 0, 9, 0 });
        TcknValidator.Validate(tckn).Should().BeNull();
    }
}
