using FluentAssertions;
using OrderDeck.LicenseServer.Services.Email;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Email;

public class LicenseKeyMaskerTests
{
    [Fact]
    public void Mask_typical_LDK_key_keeps_prefix_and_last4()
    {
        // 36-char keys: LDK- + 32 hex
        var key = "LDK-A1B2C3D4E5F6789012345678ABCDEF12";
        var masked = LicenseKeyMasker.Mask(key);

        masked.Should().StartWith("LDK-");
        masked.Should().EndWith("EF12");
        masked.Should().NotContain("A1B2");
        masked.Should().NotContain("CDEF12");  // only the suffix EF12 should leak
        masked.Length.Should().Be(4 /*LDK-*/ + 24 /*bullets*/ + 4 /*suffix*/);
    }

    [Fact]
    public void Mask_does_not_reveal_middle_characters()
    {
        var key = "LDK-AAAAAAAABBBBBBBBCCCCCCCCDDDDDDDD";
        var masked = LicenseKeyMasker.Mask(key);

        masked.Should().NotContain("AAAA");
        masked.Should().NotContain("BBBB");
        masked.Should().NotContain("CCCC");
        masked.Should().EndWith("DDDD");
    }

    [Fact]
    public void Mask_null_or_empty_returns_empty()
    {
        LicenseKeyMasker.Mask(null).Should().BeEmpty();
        LicenseKeyMasker.Mask("").Should().BeEmpty();
        LicenseKeyMasker.Mask("   ").Should().BeEmpty();
    }

    [Fact]
    public void Mask_short_key_falls_back_gracefully()
    {
        // Edge case: someone configures a 6-char key
        var masked = LicenseKeyMasker.Mask("ABCDEF");
        // Last 4 visible (CDEF), 2 dots before
        masked.Should().EndWith("CDEF");
        masked.Should().NotContain("AB");
    }

    [Fact]
    public void Mask_renewal_template_does_not_leak_full_key()
    {
        var fullKey = "LDK-DEADBEEFCAFEBABE1234567890ABCDEF";
        var (_, html, plain) = EmailTemplates.Renewal7d(
            "Test", fullKey, DateTimeOffset.UtcNow.AddDays(7), "https://x.com", null);

        html.Should().NotContain(fullKey);
        plain.Should().NotContain(fullKey);
        html.Should().Contain("CDEF");  // suffix visible for recognition
        plain.Should().Contain("CDEF");
    }
}
