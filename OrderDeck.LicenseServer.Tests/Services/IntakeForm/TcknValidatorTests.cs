using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// TC Kimlik No kontrol basamağı. Geçersiz örnekler prod verisinden çıkan
/// desenlerdir (dolgu numaralar + kontrol basamağı tutmayanlar); geçerli
/// örnekler her koşuda algoritmayla ÜRETİLİR, geçersizler üretilen bir
/// değerden türetilir — gerçek kimlik numarası repo'ya girmez.
/// </summary>
public class TcknValidatorTests
{
    // İlk hanenin iki sınırı ve ortası; kalan sekiz hane her koşuda rastgele.
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(9)]
    public void Valid_numbers_pass(int firstDigit)
        => TcknValidator.Validate(ValidStartingWith(firstDigit)).Should().BeNull();

    // {0}: üretilen geçerli TCKN'nin d10'u bozulmuş hâli, {1}: d11'i bozulmuş hâli.
    [Theory]
    [InlineData("11111111111")]   // gerçek veride 4 kez geçen dolgu
    [InlineData("{0}")]           // kontrol basamağı tutmuyor
    [InlineData("{1}")]           // d10 doğru, d11 yanlış
    public void Checksum_failures_are_rejected(string format)
    {
        var tckn = TestTckn.NewValid();
        var input = string.Format(format, WithWrongDigit(tckn, 9), WithWrongDigit(tckn, 10));

        TcknValidator.Validate(input).Should().Contain("geçersiz");
    }

    // {0}: üretilen geçerli TCKN; {1} onun ilk 10 hanesi, {2} ilk 3 hanesi,
    // {3} 4.–10. haneleri.
    [Theory]
    [InlineData("{1}")]       // 10 hane
    [InlineData("{0}0")]      // 12 hane
    [InlineData("{1}a")]
    [InlineData("{2} {3}")]
    public void Wrong_shape_gets_the_length_message(string format)
    {
        var tckn = TestTckn.NewValid();
        var input = string.Format(format, tckn, tckn[..10], tckn[..3], tckn[3..10]);

        TcknValidator.Validate(input).Should().Contain("11 rakam");
    }

    [Fact]
    public void Leading_zero_is_rejected()
        => TcknValidator.Validate("0" + TestPhone.Digits(10)).Should().Contain("0 ile başlayamaz");

    [Fact]
    public void Blank_is_valid_because_the_field_is_optional()
    {
        TcknValidator.Normalize("   ").Should().BeNull();
        TcknValidator.Validate(null).Should().BeNull();
        TcknValidator.Validate("").Should().BeNull();
    }

    [Fact]
    public void Normalize_trims_surrounding_whitespace()
    {
        var tckn = TestTckn.NewValid();
        TcknValidator.Normalize($"  {tckn} ").Should().Be(tckn);
    }

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

    /// <summary>İlk hanesi verilen, kalan sekiz hanesi rastgele geçerli TCKN.</summary>
    private static string ValidStartingWith(int firstDigit)
    {
        var firstNine = new int[9];
        firstNine[0] = firstDigit;
        for (var i = 1; i < 9; i++) firstNine[i] = Random.Shared.Next(10);
        return TestTckn.FromFirstNine(firstNine);
    }

    /// <summary>Verilen hanesi bir artırılmış (mod 10) kopya: o kontrol basamağı
    /// artık tutmaz, öteki haneler aynı kalır.</summary>
    private static string WithWrongDigit(string tckn, int index)
        => tckn[..index] + (char)('0' + (tckn[index] - '0' + 1) % 10) + tckn[(index + 1)..];
}
