using FluentAssertions;
using OrderDeck.LicenseServer.Services.IntakeForm;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.IntakeForm;

/// <summary>
/// Liste bozulursa fatura reddedilir: form da doğrulama da aynı gömülü
/// kaynaktan besleniyor, o yüzden veri setinin bütünlüğü test ediliyor.
/// </summary>
public class TurkeyRegionsTests
{
    [Fact]
    public void Dataset_has_81_cities_and_973_districts()
    {
        TurkeyRegions.Cities.Should().HaveCount(81);
        TurkeyRegions.Cities.Sum(c => TurkeyRegions.Districts[c].Count).Should().Be(973);
    }

    [Fact]
    public void Every_city_has_at_least_one_district()
    {
        var empty = TurkeyRegions.Cities.Where(c => TurkeyRegions.Districts[c].Count == 0);
        empty.Should().BeEmpty();
    }

    [Theory]
    [InlineData("istanbul", "İstanbul")]
    [InlineData("ISTANBUL", "İstanbul")]
    [InlineData("  Ankara ", "Ankara")]
    [InlineData("KAHRAMANMARAŞ", "Kahramanmaraş")]
    [InlineData("ığdır", "Iğdır")]
    public void MatchCity_normalizes_case_and_whitespace(string input, string expected)
    {
        TurkeyRegions.MatchCity(input).Should().Be(expected);
    }

    /// <summary>
    /// MatchCity/MatchDistrict i·ı·İ·I ayrımını yok sayıyor. Bu ancak listede
    /// yalnız bu harflerle ayrışan iki ad yoksa güvenli — veri seti büyüyünce
    /// (yeni ilçe) sessizce bozulmasın diye burada sabitleniyor.
    /// </summary>
    [Fact]
    public void No_two_names_differ_only_by_dotted_or_dotless_i()
    {
        static string Fold(string s) =>
            s.Replace('İ', 'i').Replace('I', 'i').Replace('ı', 'i').ToLowerInvariant();

        TurkeyRegions.Cities.Select(Fold).Should().OnlyHaveUniqueItems();

        foreach (var city in TurkeyRegions.Cities)
            TurkeyRegions.Districts[city].Select(Fold).Should()
                .OnlyHaveUniqueItems($"{city} ilçeleri ayırt edilebilmeli");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Lefkoşa")]
    public void MatchCity_returns_null_for_unknown(string? input)
    {
        TurkeyRegions.MatchCity(input).Should().BeNull();
    }

    [Fact]
    public void MatchDistrict_resolves_within_the_matched_city()
    {
        TurkeyRegions.MatchDistrict("istanbul", "kadıköy").Should().Be("Kadıköy");
    }

    [Fact]
    public void MatchDistrict_rejects_a_district_from_another_city()
    {
        TurkeyRegions.MatchDistrict("Ankara", "Kadıköy").Should().BeNull();
    }

    [Fact]
    public void MatchDistrict_returns_null_when_city_is_unknown()
    {
        TurkeyRegions.MatchDistrict("Lefkoşa", "Merkez").Should().BeNull();
    }
}
