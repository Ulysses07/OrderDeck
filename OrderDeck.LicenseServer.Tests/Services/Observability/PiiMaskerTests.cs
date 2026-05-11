using FluentAssertions;
using OrderDeck.LicenseServer.Services.Observability;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Observability;

public sealed class PiiMaskerTests
{
    // ── MaskEmail ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("ahmet@example.com", "a***@e***.com")]
    [InlineData("a@example.com", "a***@e***.com")]
    [InlineData("burak.demir@orderdeckapp.com", "b***@o***.com")]
    [InlineData("test@gmail.com", "t***@g***.com")]
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

    [Theory]
    [InlineData("+90 555 123 45 67", "***4567")]
    [InlineData("05551234567", "***4567")]
    [InlineData("(555) 123-4567", "***4567")]
    [InlineData("12345", "***2345")]
    public void MaskPhone_keeps_last_four_digits(string input, string expected)
    {
        PiiMasker.MaskPhone(input).Should().Be(expected);
    }

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
    [InlineData("Ahmet Yıldız", "A*** Y***")]
    [InlineData("Burak", "B***")]
    [InlineData("Ali Veli Selim", "A*** V*** S***")]
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
