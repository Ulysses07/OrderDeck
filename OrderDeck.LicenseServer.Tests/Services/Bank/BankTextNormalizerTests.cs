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
    [InlineData("ĞÜLÇİN Buğra", "gulcin bugra")]
    [InlineData("Kâzım Hâlâ Îlim Ûmit", "kazim hala ilim umit")]
    [InlineData("IŞIL İSTANBUL ığüşöç ĞÜLÇİN", "isil istanbul igusoc gulcin")]
    [InlineData("AYSE GUL\tX\r\nY", "ayse gul x y")]
    public void Normalize_turkce_harf_ve_ayiricilari_sadelestirir(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("Şengül", "sengul")] // ayrıştırılmış (NFD) Şengül — macOS kopyala-yapıştır
    [InlineData("İstanbul", "istanbul")] // ayrıştırılmış (NFD) İstanbul
    [InlineData("Doğan", "dogan")] // ayrıştırılmış (NFD) Doğan
    public void Normalize_ayristirilmis_birlesik_isareti_ayirici_saymaz(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    [Theory]
    [InlineData("José Hélène Rojên", "jose helene rojen")]
    [InlineData("ＡＹＳＥ", "ayse")] // tam genişlikli "AYSE"
    [InlineData("ﬁliz", "filiz")] // "fi" bitişik harfi
    [InlineData("ay­se", "ayse")] // yumuşak tire görünmez, kelimeyi bölmez
    public void Normalize_turkce_disi_aksan_ve_uyumluluk_harflerini_sadelestirir(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Tokenlar_ve_bitisik_metin()
    {
        var t = BankTextNormalizer.Tokenize("HAVALE ayse_gul34 acıklama");
        t.Tokens.Should().Equal("havale", "ayse", "gul34", "aciklama");
        t.Joined.Should().Be("havaleaysegul34aciklama");
    }

    [Fact]
    public void Aksanli_ve_aksansiz_yazim_ayni_bitisik_metni_verir()
        => BankTextNormalizer.Tokenize("Hélène").Joined.Should().Be(BankTextNormalizer.Tokenize("HELENE").Joined);

    [Fact]
    public void Kullanici_adi_anahtari_ayni_kurallarla_uretilir()
    {
        BankTextNormalizer.UsernameKey("@Ayse_Gül.34").Should().Be("aysegul34", "bitişik anahtar: ayırıcısız");
        BankTextNormalizer.UsernameTokens("Ayse_Gül.34").Should().Equal("ayse", "gul", "34");
    }

    [Fact]
    public void Kullanici_adi_harf_rakam_sinirlarinin_hepsinde_bolunur()
        => BankTextNormalizer.UsernameTokens("34gul_a1").Should().Equal("34", "gul", "a", "1");

    [Fact]
    public void Bos_ve_null_guvenli()
    {
        BankTextNormalizer.Normalize(null).Should().BeEmpty();
        BankTextNormalizer.Tokenize("").Tokens.Should().BeEmpty();
        BankTextNormalizer.UsernameKey(null).Should().BeEmpty();
        BankTextNormalizer.UsernameTokens(null).Should().BeEmpty();
    }
}
