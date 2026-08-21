using FluentAssertions;
using OrderDeck.LicenseServer.Services.Auth;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Auth;

/// <summary>
/// Kuralların iki yönünü de bağlıyor: neyi reddettiğini ve neyi
/// reddetmediğini. İkincisi en az birincisi kadar önemli — kural gerçek bir
/// anahtarı ya da geliştirici kurulumunu reddederse ilk yapılacak şey onu
/// devre dışı bırakmak olur, o zaman da üretimde hiçbir şey korumaz.
/// </summary>
public class JwtOptionsValidatorTests
{
    /// <summary>32 baytı geçen, tekrarsız, gerçekçi bir anahtar.</summary>
    private const string GoodKey = "k7Qb2xR9tLm4Wz8vN3pJ6yH1sD5gF0aC";

    private static JwtOptions Options(string secret, string issuer = "orderdeck-license-server")
        => new() { SecretKey = secret, Issuer = issuer };

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Gecerli_yapilandirma_her_ortamda_gecer(bool isProduction)
    {
        JwtOptionsValidator.Validate(Options(GoodKey), isProduction)
            .Succeeded.Should().BeTrue();
    }

    // --- Her ortamda geçerli kurallar: bunlar "yanlış" değil, "çalışmaz" ---

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Bos_anahtar_her_ortamda_reddedilir(string secret)
    {
        // Compose'da `Jwt__SecretKey: "${JWT_SECRET}"` değişken yoksa boş
        // string'e çözülür; bu senaryonun tek savunması burası.
        var result = JwtOptionsValidator.Validate(Options(secret), isProduction: false);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Jwt:SecretKey");
    }

    [Fact]
    public void Kisa_anahtar_her_ortamda_reddedilir()
    {
        var result = JwtOptionsValidator.Validate(Options("kisa-anahtar"), isProduction: false);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("bayt");
    }

    [Fact]
    public void Hata_mesaji_anahtarin_kendisini_icermez()
    {
        // Doğrulama hatası startup log'una düşer; log sır saklayan bir yer
        // değil. Uzunluk şartını sağlayan ama placeholder olan bir anahtarla
        // hem üretim hem uzunluk yolunu tetikliyoruz.
        var secret = JwtOptionsValidator.Placeholder;

        var result = JwtOptionsValidator.Validate(Options(secret), isProduction: true);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().NotContain(secret);
    }

    [Fact]
    public void Bos_issuer_reddedilir()
    {
        // ValidateIssuer açık ve ValidIssuer bu değer: boşsa sunucu ayakta
        // ama hiçbir token doğrulanamaz.
        var result = JwtOptionsValidator.Validate(Options(GoodKey, issuer: ""), isProduction: false);

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("Jwt:Issuer");
    }

    // --- Yalnız üretimde geçerli kurallar ---

    [Fact]
    public void Placeholder_anahtar_uretimde_reddedilir()
    {
        var result = JwtOptionsValidator.Validate(
            Options(JwtOptionsValidator.Placeholder), isProduction: true);

        result.Failed.Should().BeTrue();
    }

    [Fact]
    public void Placeholder_anahtar_gelistirmede_kabul_edilir()
    {
        // Geliştirici makinesinde placeholder meşru: orada üretilen token'ın
        // kimseye zararı yok. Bu ayrımı kaldırmak yerel kurulumu bozardı.
        JwtOptionsValidator.Validate(
            Options(JwtOptionsValidator.Placeholder), isProduction: false)
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Placeholder_turevi_anahtar_uretimde_reddedilir()
    {
        // Birebir eşleşme yetmez: placeholder'ı "düzenleyip" bırakmak da aynı
        // hata. REPLACE-WITH ibaresi kalmışsa değer düşünülmemiş demektir.
        var secret = "replace-with-your-own-secret-key-at-least-32-chars";

        JwtOptionsValidator.Validate(Options(secret), isProduction: true)
            .Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("12341234123412341234123412341234")]
    public void Doldurma_anahtar_uretimde_reddedilir(string secret)
    {
        // Uzunluk şartını dolduran ama entropisi olmayan değerler.
        JwtOptionsValidator.Validate(Options(secret), isProduction: true)
            .Failed.Should().BeTrue();
    }

    [Theory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData("12341234123412341234123412341234")]
    public void Doldurma_anahtar_gelistirmede_kabul_edilir(string secret)
    {
        JwtOptionsValidator.Validate(Options(secret), isProduction: false)
            .Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Test_ortaminin_anahtari_uretim_kurallarindan_bile_gecer()
    {
        // ApiFactory'nin kullandığı anahtar. Kurallar bunu reddederse bütün
        // sunucu test paketi çöker — eşiklerin gerçekçiliğinin kanıtı.
        var secret = "test-secret-key-must-be-at-least-32-bytes-long-for-hs256";

        JwtOptionsValidator.Validate(Options(secret), isProduction: true)
            .Succeeded.Should().BeTrue();
    }
}
