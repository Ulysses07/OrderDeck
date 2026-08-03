using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// E-posta doğrulaması. Örnekler prod verisinden alındı: eski
/// <c>[EmailAddress]</c> 500 kaydın sıfırını reddediyordu, gerçek hatalar
/// aşağıdaki iki grupta toplanıyor.
/// </summary>
public class EmailValidatorTests
{
    [Theory]
    [InlineData("ayse@gmail.com")]
    [InlineData("a.b_c-d+e@hotmail.com")]
    [InlineData("kullanici@yahoo.com.tr")]
    // Tanımadığımız ama meşru alan adları serbest kalmalı — beyaz liste değil,
    // yalnız açık yazım hatası aranıyor. Hepsi gerçek veride var.
    [InlineData("info@arpas.com")]
    [InlineData("ogrenci@metu.edu.tr")]
    [InlineData("satis@penti.com.tr")]
    [InlineData("kisi@mehmetmert.com")]
    [InlineData("kisi@hotmail.de")]
    [InlineData("kisi@ttmail.com")]
    [InlineData("kisi@msn.com")]
    public void Valid_addresses_pass(string email)
        => EmailValidator.Validate(EmailValidator.Normalize(email)).Should().BeNull();

    [Theory]
    [InlineData("sefikaturkyilmaz@gmail")]   // TLD yok
    [InlineData("temirf006@gamil")]          // TLD yok
    [InlineData("ayse.gmail.com")]           // @ yok
    [InlineData("ayse@@gmail.com")]
    [InlineData("ayse@.com")]
    [InlineData("ayse@gmail..com")]
    public void Malformed_addresses_are_rejected(string email)
        => EmailValidator.Validate(EmailValidator.Normalize(email)).Should().NotBeNull();

    [Fact]
    public void Turkish_characters_get_their_own_message()
        => EmailValidator.Validate("ayşe@gmail.com").Should().Contain("Türkçe karakter");

    // Asıl zararlı grup: biçim kusursuz, alan adı yanlış. Regex bunları görmez.
    [Theory]
    [InlineData("gmail.con", "gmail.com")]
    [InlineData("gml.com", "gmail.com")]
    [InlineData("qmail.com", "gmail.com")]
    [InlineData("gamil.com", "gmail.com")]
    [InlineData("hotmail.comtr", "hotmail.com")]
    [InlineData("hotmial.com", "hotmail.com")]
    [InlineData("yhaoo.com", "yahoo.com")]
    public void Domain_typos_are_caught_with_a_suggestion(string domain, string expected)
    {
        EmailValidator.SuggestDomain(domain).Should().Be(expected);
        EmailValidator.Validate("kisi@" + domain).Should().Contain(expected);
    }

    [Fact]
    public void Normalize_trims_and_lowercases_only_the_domain()
        => EmailValidator.Normalize("  Ayse.Yilmaz@GMAIL.COM ")
            .Should().Be("Ayse.Yilmaz@gmail.com");

    [Fact]
    public void Blank_is_not_an_error_here_required_handles_it()
    {
        EmailValidator.Normalize("   ").Should().BeNull();
        EmailValidator.Validate(null).Should().BeNull();
    }
}
