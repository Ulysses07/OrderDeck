using FluentAssertions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using OrderDeck.LicenseServer.Services.Privacy;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Privacy;

public sealed class TcknProtectorTests
{
    private static TcknProtector New() =>
        new(new EphemeralDataProtectionProvider(), NullLogger<TcknProtector>.Instance);

    // Sabit TCKN YAZILMAZ (CLAUDE.md): 11 haneli değer üretilir.
    private static string NewDigits() =>
        string.Concat(Guid.NewGuid().ToString("N").Where(char.IsDigit).Concat(new string('7', 11)).Take(11));

    [Fact]
    public void Sifreli_metin_duz_metni_icermez_ve_geri_cozulur()
    {
        var p = New();
        var plain = NewDigits();
        var protectedValue = p.Protect(plain)!;
        protectedValue.Should().NotContain(plain);
        p.Unprotect(protectedValue).Should().Be(plain);
    }

    [Fact]
    public void Eski_duz_metin_oldugu_gibi_okunur()
    {
        var plain = NewDigits();
        New().Unprotect(plain).Should().Be(plain);
    }

    [Fact]
    public void Null_ve_bos_korunur()
    {
        var p = New();
        p.Protect(null).Should().BeNull();
        p.Unprotect(null).Should().BeNull();
        p.Protect("").Should().BeNull();
    }

    [Fact]
    public void Cozulemeyen_deger_null_doner_firlatmaz()
        => New().Unprotect("CfDJ8" + Guid.NewGuid().ToString("N")).Should().BeNull();

    [Fact]
    public void On_bir_haneli_olmayan_eski_duz_metin_de_oldugu_gibi_okunur()
    {
        // Kayıt akışı TC'yi doğrulamadan yazıyordu; eski satırda 10 hane de olabilir.
        var plain = NewDigits()[..10];
        TcknProtector.IsLegacyPlaintext(plain).Should().BeTrue();
        New().Unprotect(plain).Should().Be(plain);
    }

    [Fact]
    public void IsLegacyPlaintext_DataProtection_onekine_bakar()
    {
        TcknProtector.IsLegacyPlaintext(NewDigits()).Should().BeTrue();
        TcknProtector.IsLegacyPlaintext("CfDJ8abc").Should().BeFalse();
        TcknProtector.IsLegacyPlaintext(New().Protect(NewDigits())!).Should().BeFalse();
    }
}
