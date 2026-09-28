using System.Text;
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
    [InlineData("AYSE\u00A0GUL\tX\r\nY", "ayse gul x y")] // bölünmez boşluk (U+00A0), sekme, satır sonu
    public void Normalize_turkce_harf_ve_ayiricilari_sadelestirir(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    // Girdiler kaçış dizisiyle: düz yazılırsa bir yapıştırma ya da editör NFC'ye birleştirir ve test sessizce
    // birleşik harf testine döner, işaret atlamayı kaldıran bir değişiklik fark edilmez.
    [Theory]
    [InlineData("S\u0327engu\u0308l", "sengul")] // ayrıştırılmış (NFD) Şengül — macOS kopyala-yapıştır
    [InlineData("I\u0307stanbul", "istanbul")] // ayrıştırılmış (NFD) İstanbul
    [InlineData("Dog\u0306an", "dogan")] // ayrıştırılmış (NFD) Doğan
    public void Normalize_ayristirilmis_birlesik_isareti_ayirici_saymaz(string input, string expected)
    {
        input.IsNormalized(NormalizationForm.FormC).Should().BeFalse("girdi ayrıştırılmış (NFD) kalmalı, yoksa test boşa döner");
        BankTextNormalizer.Normalize(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("José Hélène Rojên", "jose helene rojen")]
    [InlineData("ＡＹＳＥ", "ayse")] // tam genişlikli "AYSE"
    [InlineData("ﬁliz", "filiz")] // "fi" bitişik harfi
    [InlineData("ay\u00ADse", "ayse")] // yumuşak tire (U+00AD) görünmez, kelimeyi bölmez
    [InlineData("ay\u200Bse\u200Dx", "aysex")] // sıfır genişlikli boşluk (U+200B) ve birleştirici (U+200D) de biçim karakteri
    [InlineData("ay\u200Ese\u2060x\uFEFFy", "aysexy")] // soldan-sağa işareti (U+200E), sözcük birleştirici (U+2060), BOM (U+FEFF)
    public void Normalize_turkce_disi_aksan_ve_uyumluluk_harflerini_sadelestirir(string input, string expected)
        => BankTextNormalizer.Normalize(input).Should().Be(expected);

    [Fact]
    public void Normalize_gecersiz_utf16_birimini_ayirici_sayar_atmaz()
    {
        // UTF-16 sınırından kesilmiş emoji (ObifinPollJob.Trim, IntakeForm FB adı [..64]) eşleşmemiş vekil bırakır;
        // eşleşmemiş vekil ve U+FFFE ayrıştırmayı patlatır. Girdiler bilerek InlineData'da değil: öznitelik dizesi
        // UTF-8 saklanır, eşleşmemiş vekil orada U+FFFD'ye döner ve test boşa çıkar.
        (string Girdi, string Beklenen)[] durumlar =
        [
            ("Ayşe\uD83C", "ayse"), // sonda yarıya kesilmiş emoji: yalnız yüksek vekil
            ("ay\uDC00se", "ay se"), // eşleşmemiş düşük vekil
            ("ay\uFFFEse", "ay se"), // U+FFFE karakter-dışı
            ("a\uD800\uD83D\uDE00b", "a b"), // eşleşmemiş vekilin yanındaki tam emoji çifti bozulmaz
        ];
        foreach (var (girdi, beklenen) in durumlar)
        {
            FluentActions.Invoking(() => girdi.Normalize(NormalizationForm.FormKD))
                .Should().Throw<ArgumentException>("girdi ayrıştırmayı patlatan geçersiz birim içermeli");
            BankTextNormalizer.Normalize(girdi).Should().Be(beklenen);
        }
        BankTextNormalizer.UsernameKey("gül\uD83C").Should().Be("gul");
        BankTextNormalizer.UsernameTokens("gül\uD83C34").Should().Equal("gul", "34");
    }

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
