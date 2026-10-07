using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

public class WhatsAppLinkBuilderTests
{
    private readonly WhatsAppLinkBuilder _b = new();

    [Fact]
    public void Build_produces_wa_me_url_with_phone_and_message()
    {
        var phone = TestPhone.NewE164();
        var url = _b.Build(phone, "ornekmusteri", "Örnek Müşteri", "İstanbul");

        url.Should().StartWith($"https://wa.me/{phone[1..]}?text=");
    }

    [Fact]
    public void Build_strips_plus_space_and_dash_from_phone()
    {
        var n = TestPhone.NewNational();
        var url = _b.Build($"+90 {n[..3]} {n[3..6]}-{n[6..]}", "u", "n", "a");

        url.Should().StartWith($"https://wa.me/90{n}?text=");
    }

    [Fact]
    public void Build_encodes_newline_and_special_chars_in_message()
    {
        var url = _b.Build(TestPhone.NewE164(), "user&one", "Ad Soyad", "Adres+Test");

        // URL encoded: \n = %0A, & = %26, + = %2B, space = %20 (or +)
        url.Should().Contain("%0A");           // newlines encoded
        url.Should().Contain("user%26one");    // & encoded
        url.Should().Contain("Adres%2BTest");  // + encoded
    }

    [Fact]
    public void Build_includes_three_labeled_lines_in_message()
    {
        var url = _b.Build(TestPhone.NewE164(), "uname", "Test User", "Test Adres");

        // Decode the text param to verify structure
        var queryStart = url.IndexOf("?text=") + 6;
        var encoded = url[queryStart..];
        var decoded = Uri.UnescapeDataString(encoded);

        decoded.Should().Contain("Kullanıcı adı: uname");
        decoded.Should().Contain("Ad Soyad: Test User");
        decoded.Should().Contain("Adres: Test Adres");
    }
}
