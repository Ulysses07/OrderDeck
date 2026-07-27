using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using OrderDeck.LicenseServer.Services.WhatsApp;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.WhatsApp;

public sealed class WhatsAppSignatureValidatorTests
{
    private const string Secret = "app-secret-abc";
    private const string Body = """{"object":"whatsapp_business_account","entry":[]}""";

    private static string Sign(string body, string secret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    [Fact]
    public void Accepts_correct_signature()
    {
        WhatsAppSignatureValidator.IsValid(Sign(Body, Secret), Body, Secret).Should().BeTrue();
    }

    [Fact]
    public void Accepts_uppercase_hex()
    {
        var upper = Sign(Body, Secret).ToUpperInvariant().Replace("SHA256=", "sha256=");
        WhatsAppSignatureValidator.IsValid(upper, Body, Secret).Should().BeTrue();
    }

    [Fact]
    public void Rejects_when_body_tampered()
    {
        var sig = Sign(Body, Secret);
        WhatsAppSignatureValidator.IsValid(sig, Body + " ", Secret).Should().BeFalse();
    }

    [Fact]
    public void Rejects_signature_from_another_secret()
    {
        WhatsAppSignatureValidator.IsValid(Sign(Body, "other-secret"), Body, Secret).Should().BeFalse();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("deadbeef")]                      // prefix yok
    [InlineData("sha256=")]                       // boş hex
    [InlineData("sha256=abc")]                    // kısa
    [InlineData("sha1=0123456789abcdef")]         // yanlış algoritma
    public void Rejects_malformed_headers(string? header)
    {
        WhatsAppSignatureValidator.IsValid(header, Body, Secret).Should().BeFalse();
    }

    [Fact]
    public void Rejects_non_hex_characters_of_right_length()
    {
        var bogus = "sha256=" + new string('z', 64);
        WhatsAppSignatureValidator.IsValid(bogus, Body, Secret).Should().BeFalse();
    }

    [Fact]
    public void Rejects_everything_when_app_secret_not_configured()
    {
        // Yapılandırılmamış sunucu, imzasız/istekli her çağrıyı reddetmeli (fail-closed).
        WhatsAppSignatureValidator.IsValid(Sign(Body, ""), Body, "").Should().BeFalse();
    }
}
