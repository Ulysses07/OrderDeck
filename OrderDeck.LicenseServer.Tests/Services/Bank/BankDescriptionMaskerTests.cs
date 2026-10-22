using FluentAssertions;
using OrderDeck.LicenseServer.Services.Bank;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Bank;

/// <summary>KVKK Ö-16: açıklamaya yazılmış IBAN ve 11 haneli numara kayda düz girmez. Değerler üretilir (repo public).</summary>
public sealed class BankDescriptionMaskerTests
{
    private static string Spaced(string iban)
        => string.Join(" ", iban.Chunk(4).Select(c => new string(c)));

    /// <summary>TCKN biçimli ama kontrol hanesi tutmayan 11 hane: geçerli TCKN'nin son hanesi bir kaydırılır (son hane ilk on
    /// hanenin toplamına bağlı; tek hane değişince hiçbir koşuda tutmaz).</summary>
    private static string BrokenTckn()
    {
        var tckn = TestTckn.NewValid();
        return tckn[..10] + (char)('0' + (tckn[10] - '0' + 1) % 10);
    }

    [Fact]
    public void Bitisik_IBAN_maskelenir()
    {
        var iban = BankHasherTests.TestIban();
        var masked = BankDescriptionMasker.Mask($"HAVALE {iban} ornek_musteri34");

        masked.Should().Be($"HAVALE {BankHasher.MaskIban(iban)} ornek_musteri34");
        masked.Should().NotContain(iban[4..12]);
    }

    [Fact]
    public void Dortlu_gruplarla_yazilmis_ve_kucuk_harfli_IBAN_maskelenir()
    {
        var iban = BankHasherTests.TestIban();
        var masked = BankDescriptionMasker.Mask("EFT " + Spaced(iban).ToLowerInvariant() + " SIPARIS");

        masked.Should().Be($"EFT {BankHasher.MaskIban(iban)} SIPARIS");
    }

    [Fact]
    public void Harfe_bitisik_IBAN_de_maskelenir()
    {
        // Sınır yok: "IBANTR…" ya da "…1234AHMET" gibi bitişik yazımda da IBAN maskelenir; mod-97 yanlış eşleşmeyi eler.
        var iban = BankHasherTests.TestIban();
        BankDescriptionMasker.Mask($"IBAN{iban}AHMET").Should().Be($"IBAN{BankHasher.MaskIban(iban)}AHMET");
    }

    [Fact]
    public void Kontrol_hanesi_tutmayan_IBAN_bicimli_dizi_maskelenmez()
    {
        var wrong = BankHasherTests.WrongCheckDigits(BankHasherTests.TestIban());
        BankDescriptionMasker.Mask($"REF {wrong}").Should().Be($"REF {wrong}");
    }

    [Fact]
    public void Gecersiz_aday_arkasindaki_gercek_IBANi_yutmaz()
    {
        // "TR" + 7 rakam ve ardından gelen IBAN'ın ilk 17 karakteri de IBAN biçimine uyar. Bu aday kontrol hanesinden düşünce
        // arama adayın sonundan sürerse IBAN'ın başı yutulur ve IBAN düz kalır. Tuzak adayın kontrol haneleri bilerek
        // bozulur: kalıp tutar, mod-97 hiçbir koşuda tutmaz.
        var iban = BankHasherTests.TestIban();
        var decoy = BankHasherTests.WrongCheckDigits(BankHasherTests.WithCheckDigits("TR", TestPhone.Digits(5) + iban[..17]))[..9];

        BankDescriptionMasker.Mask($"{decoy} {iban}").Should().Be($"{decoy} {BankHasher.MaskIban(iban)}");
    }

    [Fact]
    public void On_bir_haneli_numara_yildizlanir()
    {
        var tckn = TestTckn.NewValid();
        BankDescriptionMasker.Mask($"FAST {tckn} AD SOYAD").Should().Be($"FAST {BankDescriptionMasker.ElevenDigitMask} AD SOYAD");
    }

    [Theory]
    [InlineData("TC:", ".")]
    [InlineData("(", ")")]
    [InlineData("", "")]
    public void Noktalamaya_bitisik_on_bir_hane_de_yildizlanir(string before, string after)
    {
        var digits = TestPhone.Digits(11);
        BankDescriptionMasker.Mask($"{before}{digits}{after}").Should().Be($"{before}{BankDescriptionMasker.ElevenDigitMask}{after}");
    }

    [Theory]
    [InlineData("TC", "")]
    [InlineData("TCKN", "")]
    [InlineData("HAVALE TCNO", " AD SOYAD")]
    [InlineData("HAVALE ", "AHMET")]
    [InlineData("ş", "")]
    public void Harfe_bitisik_gecerli_TCKN_de_yildizlanir(string before, string after)
    {
        // Kimlik numarasının en doğal yazımları harfe bitişik: "TC…", "TCKN…", isme bitişik "…AHMET". Kontrol haneleri tutan dizi
        // komşusu harf olsa da maskelenir.
        var tckn = TestTckn.NewValid();
        BankDescriptionMasker.Mask($"{before}{tckn}{after}").Should().Be($"{before}{BankDescriptionMasker.ElevenDigitMask}{after}");
    }

    [Theory]
    [InlineData("HAVALE user", "")]
    [InlineData("ş", "")]
    [InlineData("HAVALE ", "AHMET")]
    public void Harfe_bitisik_TCKN_olmayan_on_bir_hane_maskelenmez(string before, string after)
    {
        // Kullanıcı adının parçası olabilir (eşleştirici açıklamadaki kullanıcı adına bakıyor): kontrol hanesi tutmayan dizi düz kalır.
        var digits = BrokenTckn();
        BankDescriptionMasker.Mask($"{before}{digits}{after}").Should().Be($"{before}{digits}{after}");
    }

    [Fact]
    public void Kullanici_adindaki_sifirla_baslayan_telefon_degismez()
    {
        // TCKN 0 ile başlamaz: kullanıcı adına bitişik "0" + 10 haneli telefon hiçbir koşuda TCKN sayılmaz.
        var text = $"HAVALE user0{TestPhone.NewNational()}";
        BankDescriptionMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void Alt_cizgi_ya_da_noktayla_ayrilmis_on_bir_hane_yildizlanir()
    {
        // '_' ve '.' harf değil: dizi tek başına duruyor sayılır ve maskelenir (o kullanıcı adı açıklamadan eşleşmez).
        var digits = "0" + TestPhone.NewNational();
        BankDescriptionMasker.Mask($"ayse_{digits} ali.{digits}")
            .Should().Be($"ayse_{BankDescriptionMasker.ElevenDigitMask} ali.{BankDescriptionMasker.ElevenDigitMask}");
    }

    [Theory]
    [InlineData(10)]
    [InlineData(12)]
    public void On_bir_haneden_farkli_rakam_dizisi_degismez(int length)
    {
        var digits = TestPhone.Digits(length);
        BankDescriptionMasker.Mask($"REF {digits}").Should().Be($"REF {digits}");
    }

    [Fact]
    public void Kullanici_adi_ve_TR_ile_baslayan_sozcukler_degismez()
    {
        const string text = "TRANSFER ODEME KODU ABC123 ornek_musteri34 TR12 HAVALE";
        BankDescriptionMasker.Mask(text).Should().Be(text);
    }

    [Fact]
    public void Bos_ve_null_aynen_doner_maskeleme_idempotent()
    {
        BankDescriptionMasker.Mask(null).Should().BeNull();
        BankDescriptionMasker.Mask("").Should().Be("");
        var once = BankDescriptionMasker.Mask(
            $"{BankHasherTests.TestIban()} {TestTckn.NewValid()} TC{TestTckn.NewValid()} user{BrokenTckn()}");
        BankDescriptionMasker.Mask(once).Should().Be(once);
    }
}
