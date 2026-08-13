using FluentAssertions;
using OrderDeck.Shared.Text;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class SearchNormalizerTests
{
    [Theory]
    [InlineData("Tişört", "TISORT")]
    [InlineData("Kırmızı  Elbise", "KIRMIZI ELBISE")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    [InlineData("A-1", "A-1")]
    public void Normalizes_to_a_collation_independent_form(string? value, string expected)
        => SearchNormalizer.Normalize(value).Should().Be(expected);

    /// <summary>
    /// Asıl kural: aranan iğne ile saklanan değer aynı fonksiyondan geçtiği için,
    /// kullanıcının hangi biçimde yazdığı fark etmemeli.
    /// </summary>
    [Theory]
    [InlineData("tisort")]
    [InlineData("TİŞÖRT")]
    [InlineData("  Tişört  ")]
    public void Different_spellings_of_the_same_word_collapse_to_one_form(string typed)
        => SearchNormalizer.Normalize(typed)
            .Should().Be(SearchNormalizer.Normalize("Tişört"));

    [Fact]
    public void Keeps_non_alphanumeric_characters()
        => SearchNormalizer.Normalize("Set (2'li) - Yeşil")
            .Should().Be("SET (2'LI) - YESIL");
}
