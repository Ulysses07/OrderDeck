using FluentAssertions;
using OrderDeck.LicenseServer.Services.Observability;
using OrderDeck.LicenseServer.Tests.TestHelpers;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Observability;

public sealed class PiiMaskerTests
{
    // ── MaskEmail ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ahmet@example.com", "a***@e***.com")]
    [InlineData("a@example.com", "a***@e***.com")]
    [InlineData("ornek.musteri@example.org", "o***@e***.org")]
    [InlineData("test@example.net", "t***@e***.net")]
    public void MaskEmail_masks_local_and_domain_preserving_tld(string input, string expected)
    {
        PiiMasker.MaskEmail(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("noatsign")]
    [InlineData("@nolocal.com")]
    [InlineData("trailing@")]
    public void MaskEmail_returns_stars_for_invalid_input(string? input)
    {
        PiiMasker.MaskEmail(input).Should().Be("***");
    }

    [Fact]
    public void MaskEmail_handles_domain_without_dot()
    {
        // Edge: "user@localhost" — no TLD. Should still mask, not crash.
        PiiMasker.MaskEmail("user@localhost").Should().Be("u***@l***");
    }

    // ── MaskPhone ─────────────────────────────────────────────────────────

    // Numara her koşuda üretilir: {0} operatör kodu, {1} 3 hane, {2} ve {3} ikişer hane.
    [Theory]
    [InlineData("+90 {0} {1} {2} {3}")]
    [InlineData("0{0}{1}{2}{3}")]
    [InlineData("({0}) {1}-{2}{3}")]
    public void MaskPhone_keeps_last_four_digits(string format)
    {
        var n = TestPhone.NewNational();
        var input = string.Format(format, n[..3], n[3..6], n[6..8], n[8..]);

        PiiMasker.MaskPhone(input).Should().Be("***" + n[^4..]);
    }

    [Fact]
    public void MaskPhone_keeps_last_four_digits_of_short_input()
        => PiiMasker.MaskPhone("12345").Should().Be("***2345");

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("12")]
    public void MaskPhone_returns_stars_for_short_or_invalid_input(string? input)
    {
        PiiMasker.MaskPhone(input).Should().Be("***");
    }

    // ── MaskName ──────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Örnek Müşteri", "Ö*** M***")]
    [InlineData("Burak", "B***")]
    [InlineData("Örnek Ara Müşteri", "Ö*** A*** M***")]
    [InlineData("  Foo  Bar  ", "F*** B***")]
    public void MaskName_masks_each_word_with_first_letter(string input, string expected)
    {
        PiiMasker.MaskName(input).Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void MaskName_returns_stars_for_empty_input(string? input)
    {
        PiiMasker.MaskName(input).Should().Be("***");
    }
}
