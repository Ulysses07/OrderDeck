using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class CatalogCodeSequenceTests
{
    [Fact]
    public void First_code_is_A1()
        => CatalogCodeSequence.Next(Array.Empty<string>()).Should().Be("A1");

    [Fact]
    public void Increments_the_number()
        => CatalogCodeSequence.Next(new[] { "A1", "A2", "A3" }).Should().Be("A4");

    [Fact]
    public void Rolls_over_to_next_prefix_after_999()
        => CatalogCodeSequence.Next(new[] { "A999" }).Should().Be("B1");

    [Fact]
    public void Rolls_over_from_Z999_to_AA1()
        => CatalogCodeSequence.Next(new[] { "Z999" }).Should().Be("AA1");

    [Fact]
    public void Rolls_over_from_AZ999_to_BA1()
        => CatalogCodeSequence.Next(new[] { "AZ999" }).Should().Be("BA1");

    // "Sayaç geri gitmez": en yüksek kod neyse ondan devam eder, aradaki
    // boşluklar doldurulmaz. Arşivlenen ürünün kodu tekrar kullanılırsa eski
    // siparişler yanlış ürünü gösterir.
    [Fact]
    public void Never_reuses_a_gap()
        => CatalogCodeSequence.Next(new[] { "A1", "A7" }).Should().Be("A8");

    // Önek "ayarlanabilir": yayıncı elle B1 yazdığında sayaç oradan devam eder.
    [Fact]
    public void Longer_prefix_wins_over_lexicographic_order()
        => CatalogCodeSequence.Next(new[] { "Z5", "AA2" }).Should().Be("AA3");

    [Fact]
    public void Ignores_hand_written_codes_that_do_not_match_the_pattern()
        => CatalogCodeSequence.Next(new[] { "KIRMIZI-ELBISE", "A4" }).Should().Be("A5");

    [Fact]
    public void Falls_back_to_A1_when_no_code_matches_the_pattern()
        => CatalogCodeSequence.Next(new[] { "özel-kod" }).Should().Be("A1");

    [Fact]
    public void Is_case_insensitive_on_input_but_uppercase_on_output()
        => CatalogCodeSequence.Next(new[] { "a12" }).Should().Be("A13");
}
