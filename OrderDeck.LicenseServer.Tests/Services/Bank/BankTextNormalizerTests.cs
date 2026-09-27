using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>Açıklama ve kullanıcı adı aynı normalizasyondan geçer: küçük harf (tr-TR), Türkçe harf
/// → ASCII, ayırıcılar boşluk. Bitişik yazım için boşluksuz birleşik metin de üretilir.</summary>
public sealed class BankTextNormalizerTests
{
    [Theory]
    [InlineData("Işıl ŞENGÜL", "isil sengul")]
    [InlineData("@Ayse_Gül.34", "ayse gul 34")]
    [InlineData("EFT-GELEN/İSTANBUL:ÖDEME", "eft gelen istanbul odeme")]
    [InlineData("  çok   boşluk ", "cok bosluk")]
    public void Normalize_turkce_harf_ve_ayiricilari_sadelestirir(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Tokenlar_ve_bitisik_metin()
    {
        var t = BankTextNormalizer.Tokenize("HAVALE ayse_gul34 acıklama");
        t.Tokens.Should().Equal("havale", "ayse", "gul34", "aciklama");
        t.Joined.Should().Be("havaleaysegul34aciklama");
    }

    [Fact]
    public void Kullanici_adi_anahtari_ayni_kurallarla_uretilir()
    {
        BankTextNormalizer.UsernameKey("@Ayse_Gül.34").Should().Be("aysegul34", "bitişik anahtar: ayırıcısız");
        BankTextNormalizer.UsernameTokens("Ayse_Gül.34").Should().Equal("ayse", "gul", "34");
    }

    [Fact]
    public void Bos_ve_null_guvenli()
    {
        BankTextNormalizer.Normalize(null).Should().BeEmpty();
        BankTextNormalizer.Tokenize("").Tokens.Should().BeEmpty();
    }
}
