using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public class WhatsAppLinkBuilderTests
{
    private readonly WhatsAppLinkBuilder _b = new();

    [Fact]
    public void Build_produces_wa_me_url_with_phone_and_message()
    {
        var url = _b.Build("+905551234567", "bilalcanli", "Bilal Canlı", "İstanbul");

        url.Should().StartWith("https://wa.me/905551234567?text=");
    }

    [Fact]
    public void Build_strips_plus_space_and_dash_from_phone()
    {
        var url = _b.Build("+90 555 123-4567", "u", "n", "a");

        url.Should().StartWith("https://wa.me/905551234567?text=");
    }

    [Fact]
    public void Build_encodes_newline_and_special_chars_in_message()
    {
        var url = _b.Build("+905551234567", "user&one", "Ad Soyad", "Adres+Test");

        // URL encoded: \n = %0A, & = %26, + = %2B, space = %20 (or +)
        url.Should().Contain("%0A");           // newlines encoded
        url.Should().Contain("user%26one");    // & encoded
        url.Should().Contain("Adres%2BTest");  // + encoded
    }

    [Fact]
    public void Build_includes_three_labeled_lines_in_message()
    {
        var url = _b.Build("+905551234567", "uname", "Test User", "Test Adres");

        // Decode the text param to verify structure
        var queryStart = url.IndexOf("?text=") + 6;
        var encoded = url[queryStart..];
        var decoded = Uri.UnescapeDataString(encoded);

        decoded.Should().Contain("Kullanıcı adı: uname");
        decoded.Should().Contain("Ad Soyad: Test User");
        decoded.Should().Contain("Adres: Test Adres");
    }
}
