using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class AxisCodeDeriverTests
{
    // Spec tablosu — "Kod parçası ASCII'ye türetilir (Code128 tuzağı)".
    [Theory]
    [InlineData("Siyah", "SIYA")]
    [InlineData("Yeşil", "YESI")]
    [InlineData("Bej", "BEJ")]
    [InlineData("M", "M")]
    [InlineData("38", "38")]
    [InlineData("103 Nude", "103")]
    public void Derives_spec_table_values(string display, string expected)
        => AxisCodeDeriver.Derive(display).Should().Be(expected);

    [Theory]
    [InlineData("çğıİöşü", "CGII")]
    [InlineData("ÇĞİÖŞÜ", "CGIO")]
    public void Strips_all_turkish_letters_to_ascii(string display, string expected)
        => AxisCodeDeriver.Derive(display).Should().Be(expected);

    [Fact]
    public void Result_is_always_ascii_uppercase_alphanumeric()
    {
        var code = AxisCodeDeriver.Derive("açık mavi-2");
        code.Should().MatchRegex("^[A-Z0-9]+$");
    }

    [Fact]
    public void Falls_through_to_next_token_when_first_token_has_no_ascii()
        => AxisCodeDeriver.Derive("— Mavi").Should().Be("MAVI");

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("—")]
    public void Returns_empty_when_nothing_derivable(string display)
        => AxisCodeDeriver.Derive(display).Should().BeEmpty();

    [Fact]
    public void Truncates_to_four_characters()
        => AxisCodeDeriver.Derive("Antrasit").Should().Be("ANTR");
}
