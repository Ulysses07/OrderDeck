using FluentAssertions;
using OrderDeck.LicenseServer.Services.Catalog;
using Xunit;

namespace OrderDeck.LicenseServer.Tests.Services.Catalog;

public class VariantCodeBuilderTests
{
    [Fact]
    public void Axisless_variant_code_is_the_product_code_itself()
        => VariantCodeBuilder.Build("A1", null, null).Should().Be("A1");

    [Fact]
    public void One_fragment_is_appended_with_a_dash()
        => VariantCodeBuilder.Build("A1", "SIYA", null).Should().Be("A1-SIYA");

    [Fact]
    public void Two_fragments_are_appended_in_order()
        => VariantCodeBuilder.Build("A1", "SIYA", "M").Should().Be("A1-SIYA-M");

    // Boş parça = parça yok; yarım tireli kod ("A1-") asla üretilmemeli.
    [Theory]
    [InlineData("", null)]
    [InlineData(null, "")]
    [InlineData("", "")]
    public void Empty_fragments_are_treated_as_absent(string? axis1Code, string? axis2Code)
        => VariantCodeBuilder.Build("A1", axis1Code, axis2Code).Should().Be("A1");

    // Birinci eksen olmadan ikincisi olamaz (ürün kartı bunu doğruluyor);
    // yine de savunmacı davran: sarkan tire üretme.
    [Fact]
    public void Second_fragment_without_a_first_one_is_ignored()
        => VariantCodeBuilder.Build("A1", null, "M").Should().Be("A1");
}
